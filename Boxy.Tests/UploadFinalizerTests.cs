using Boxy.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boxy.Tests;

/// <summary>
/// The finalizer's job is to make finishing an upload survive the client walking away: the work is detached
/// from the request, a finished result is replayed rather than redone, and a failed one can be retried.
/// </summary>
[TestClass]
public class UploadFinalizerTests
{
    private UploadFinalizer _finalizer = null!;

    [TestInitialize]
    public void Setup()
    {
        _finalizer = NewFinalizer(TimeSpan.FromMinutes(30));
    }

    private static UploadFinalizer NewFinalizer(TimeSpan keepResult)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new UploadFinalizer(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<UploadFinalizer>.Instance) { KeepResult = keepResult };
    }

    [TestMethod]
    public async Task StartOrJoin_RunsTheWorkOnce_EvenWhenEveryoneAsksAtOnce()
    {
        // Two tabs, or a client that gave up and re-asked, must not set two assemblies going on the same
        // parts - they would race each other over the same files.
        var gate = new TaskCompletionSource();
        var runs = 0;

        // Start them for real in parallel, so the map's add-or-update actually contends.
        var joined = new Task<UploadOutcome>[16];
        Parallel.For(0, joined.Length, i => joined[i] = _finalizer.StartOrJoin("upload1", async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return UploadOutcome.Done("abc", "clip");
        }));

        gate.SetResult();
        var outcomes = await Task.WhenAll(joined);

        Assert.AreEqual(1, runs, "the assembly must run once, however many callers ask for it");
        Assert.IsTrue(outcomes.All(o => o.Ok && o.Slug == "abc"));
    }

    [TestMethod]
    public async Task StartOrJoin_ReplaysASuccess_SoAskingAgainIsSafe()
    {
        // The client that loses the answer to a 10-minute assembly asks again. It must get the item back,
        // not "no such upload" - and certainly not a second copy of the file.
        var runs = 0;

        var first = await _finalizer.StartOrJoin("upload1", (_, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(UploadOutcome.Done("abc", "clip"));
        });

        var second = await _finalizer.StartOrJoin("upload1", (_, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(UploadOutcome.Done("different", "other"));
        });

        Assert.AreEqual(1, runs);
        Assert.AreEqual("abc", first.Slug);
        Assert.AreEqual("abc", second.Slug, "a finished upload replays its own result");
    }

    [TestMethod]
    public async Task StartOrJoin_RetriesAfterAFailure()
    {
        var failed = await _finalizer.StartOrJoin("upload1", (_, _) => Task.FromResult(UploadOutcome.Failed("disk full")));
        Assert.IsFalse(failed.Ok);
        Assert.IsTrue(failed.Restart, "a failed finalize discards the parts, so the client must start over");

        var retried = await _finalizer.StartOrJoin("upload1", (_, _) => Task.FromResult(UploadOutcome.Done("abc", "clip")));
        Assert.IsTrue(retried.Ok, "a failure must not pin the client to that failure forever");
    }

    [TestMethod]
    public async Task StartOrJoin_TurnsAnUnexpectedThrowIntoAFailure()
    {
        var outcome = await _finalizer.StartOrJoin("upload1", (_, _) => throw new IOException("no space left on device"));

        Assert.IsFalse(outcome.Ok);
        Assert.IsTrue(outcome.Restart);
        Assert.IsNotNull(outcome.Error);
    }

    [TestMethod]
    public async Task IsRunning_IsTrueOnlyWhileAssembling()
    {
        Assert.IsFalse(_finalizer.IsRunning("upload1"), "nothing has been started");

        var gate = new TaskCompletionSource();
        var run = _finalizer.StartOrJoin("upload1", async (_, _) =>
        {
            await gate.Task;
            return UploadOutcome.Done("abc", "clip");
        });

        // The abort endpoint leans on this: the parts must not be deleted while they're being assembled.
        await WaitUntil(() => _finalizer.IsRunning("upload1"));

        gate.SetResult();
        await run;
        Assert.IsFalse(_finalizer.IsRunning("upload1"));
    }

    [TestMethod]
    public async Task StartOrJoin_ReplaysASuccess_EvenWhenTheAssemblyOutlastedTheRetention()
    {
        // A 50 GB assembly can take longer than results are kept for. Retention has to run from when the job
        // finished, not when it started, or the result is prunable the instant it lands: the client comes
        // back for its answer, finds the job gone, and gets a fresh assembly run against parts that the
        // finished run already deleted - a successful upload reported as a failure.
        var finalizer = NewFinalizer(TimeSpan.FromMilliseconds(200));
        var runs = 0;

        Task<UploadOutcome> Start()
        {
            return finalizer.StartOrJoin("upload1", async (_, _) =>
            {
                Interlocked.Increment(ref runs);
                await Task.Delay(500); // outlasts the retention window all by itself
                return UploadOutcome.Done("abc", "clip");
            });
        }

        var first = await Start();
        var replayed = await Start();

        Assert.AreEqual(1, runs, "the result must survive its own assembly taking longer than the retention");
        Assert.AreEqual("abc", first.Slug);
        Assert.AreEqual("abc", replayed.Slug);
    }

    [TestMethod]
    public async Task StartOrJoin_EventuallyForgetsAFinishedUpload()
    {
        var finalizer = NewFinalizer(TimeSpan.FromMilliseconds(50));
        var runs = 0;

        Task<UploadOutcome> Start()
        {
            return finalizer.StartOrJoin("upload1", (_, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(UploadOutcome.Done("abc", "clip"));
            });
        }

        await Start();
        await Task.Delay(150); // past the retention window
        await Start();

        Assert.AreEqual(2, runs, "results are not kept forever");
    }

    [TestMethod]
    public void Find_ReturnsNothing_ForAnUploadTheServerNeverSaw()
    {
        // What a client polling across a server restart hits. It re-asks, and the staged parts are still there.
        Assert.IsNull(_finalizer.Find("upload1"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(condition(), "condition never came true");
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
