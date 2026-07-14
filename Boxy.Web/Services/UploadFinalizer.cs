using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Services;

/// <summary>What finishing an upload came to: the item it produced, or why it didn't produce one.</summary>
public record UploadOutcome(string? Slug, string? Title, string? Error, bool Restart = false, bool Gone = false)
{
    public bool Ok => Error is null;

    public static UploadOutcome Done(string slug, string? title)
    {
        return new UploadOutcome(slug, title, null);
    }

    /// <summary>A failed finalize always discards the staged parts, so the client is always told to start
    /// over rather than resume onto staging that no longer exists.</summary>
    public static UploadOutcome Failed(string error)
    {
        return new UploadOutcome(null, null, error, Restart: true);
    }

    /// <summary>The item a replace was aimed at is no longer there. Re-sending won't help.</summary>
    public static UploadOutcome ItemGone()
    {
        return new UploadOutcome(null, null, "That item no longer exists.", Gone: true);
    }
}

/// <summary>
/// Runs the finalize step of a chunked upload - concatenate, hash, store, insert - detached from the
/// request that asked for it.
///
/// Assembling a multi-GB file is minutes of disk I/O, and a reverse proxy will not hold a silent connection
/// open that long (nginx gives up after 60 seconds by default), so a synchronous finalize means the server
/// quietly succeeds while the client is told the upload failed. The request that starts the work therefore
/// waits only briefly: a small file lands inside that window and answers straight away, a large one gets a
/// 202 and the client polls. The work carries on either way, so a phone that drops off mid-assembly still
/// gets its file.
///
/// A finished result is kept and replayed, which makes finalizing idempotent: a client that lost the answer
/// and asks again gets the same item back instead of "no such upload". A failed one is dropped, so asking
/// again genuinely retries.
/// </summary>
public sealed class UploadFinalizer(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    ILogger<UploadFinalizer> logger)
{
    /// <summary>How long a request waits for the assembly before handing the client a 202 to poll on.
    /// Comfortably inside any proxy's read timeout, and long enough that ordinary files never poll.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan KeepResult = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    /// <summary>Start finalizing this upload, or join the run already under way for it. A completed,
    /// successful run is replayed rather than repeated.</summary>
    public Task<UploadOutcome> StartOrJoin(string uploadId, Func<IServiceProvider, CancellationToken, Task<UploadOutcome>> work)
    {
        Prune();
        var job = _jobs.AddOrUpdate(
            uploadId,
            _ => NewJob(uploadId, work),
            (_, existing) => Replaceable(existing) ? NewJob(uploadId, work) : existing);
        return job.Run;
    }

    /// <summary>The run for this upload, or null if there isn't one (a restart forgets them).</summary>
    public Task<UploadOutcome>? Find(string uploadId)
    {
        return _jobs.TryGetValue(uploadId, out var job) ? job.Run : null;
    }

    /// <summary>Whether this upload is being assembled right now. Its parts must not be pulled out from
    /// under it.</summary>
    public bool IsRunning(string uploadId)
    {
        return _jobs.TryGetValue(uploadId, out var job) && !job.Run.IsCompleted;
    }

    // A run that finished badly shouldn't pin the client to that failure forever; one that finished well is
    // the answer, and gets replayed as often as it's asked for.
    private static bool Replaceable(Job job)
    {
        return job.Run.IsCompleted && !(job.Run.IsCompletedSuccessfully && job.Run.Result.Ok);
    }

    private Job NewJob(string uploadId, Func<IServiceProvider, CancellationToken, Task<UploadOutcome>> work)
    {
        // Lazy, because AddOrUpdate may call this factory more than once under contention and only the value
        // that actually lands in the map is ever run - two assemblies of the same upload would fight.
        return new Job(new Lazy<Task<UploadOutcome>>(
            () => Task.Run(() => RunAsync(uploadId, work)),
            LazyThreadSafetyMode.ExecutionAndPublication));
    }

    private async Task<UploadOutcome> RunAsync(string uploadId, Func<IServiceProvider, CancellationToken, Task<UploadOutcome>> work)
    {
        // The request's own scope and cancellation token are deliberately not used: the client is expected
        // to leave, and taking the assembly down with it is the bug this class exists to prevent. The work
        // only stops for a shutdown.
        using var scope = scopes.CreateScope();
        try
        {
            return await work(scope.ServiceProvider, lifetime.ApplicationStopping);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finalizing upload {UploadId} failed", uploadId);
            return UploadOutcome.Failed("Something went wrong finishing that upload. Please try again.");
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - KeepResult;
        foreach (var (id, job) in _jobs)
        {
            if (job.StartedUtc < cutoff && job.Run.IsCompleted)
            {
                _jobs.TryRemove(new KeyValuePair<string, Job>(id, job));
            }
        }
    }

    private sealed record Job(Lazy<Task<UploadOutcome>> Lazy)
    {
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public Task<UploadOutcome> Run => Lazy.Value;
    }
}

/// <summary>
/// Turns a finalize into an HTTP answer. Every reply carries an explicit <c>status</c> so the client has one
/// shape to read: <c>done</c>, <c>assembling</c> (poll), <c>unknown</c> (ask again), or <c>error</c>.
/// </summary>
public static class UploadResults
{
    /// <summary>Give the assembly a moment to land, so an ordinary file answers in a single round trip, and
    /// hand back a 202 to poll on when it's going to take longer than a proxy will wait.</summary>
    public static async Task<IActionResult> AwaitOrAcceptAsync(Task<UploadOutcome> run)
    {
        var finished = await Task.WhenAny(run, Task.Delay(UploadFinalizer.Grace));
        return finished == run
            ? Describe(await run)
            : new ObjectResult(new { status = "assembling" }) { StatusCode = StatusCodes.Status202Accepted };
    }

    /// <summary>The state of a finalize the client is polling, including one the server has forgotten (a
    /// restart), which the client answers by asking to finalize again - the parts are still staged.</summary>
    public static IActionResult Describe(Task<UploadOutcome>? run)
    {
        if (run is null)
        {
            return new JsonResult(new { status = "unknown" });
        }

        return run.IsCompleted
            ? Describe(run.GetAwaiter().GetResult())
            : new ObjectResult(new { status = "assembling" }) { StatusCode = StatusCodes.Status202Accepted };
    }

    private static IActionResult Describe(UploadOutcome outcome)
    {
        if (outcome.Ok)
        {
            return new JsonResult(new { status = "done", slug = outcome.Slug, title = outcome.Title });
        }

        return new JsonResult(new { status = "error", error = outcome.Error, restart = outcome.Restart, gone = outcome.Gone });
    }
}
