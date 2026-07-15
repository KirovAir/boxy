using Boxy.Web.Services;

namespace Boxy.Tests;

[TestClass]
public class MediaProcessingQueueTests
{
    [TestMethod]
    public void Pending_counts_stacked_requests_and_clears_only_at_zero()
    {
        var queue = new MediaProcessingQueue();
        Assert.IsFalse(queue.IsPending(5));

        queue.EnqueueBackfill(5);
        Assert.IsTrue(queue.IsPending(5));

        // A second request for the same item (e.g. a double-click of "convert again") stacks.
        queue.EnqueueBackfill(5);
        Assert.IsTrue(queue.IsPending(5));

        // The first run finishing must NOT drop the item while the second is still outstanding - this is
        // exactly the race that made the bar report "finished" too early.
        queue.Done(5);
        Assert.IsTrue(queue.IsPending(5));

        queue.Done(5);
        Assert.IsFalse(queue.IsPending(5));

        // An extra Done (belt-and-braces / a re-processed id) is harmless.
        queue.Done(5);
        Assert.IsFalse(queue.IsPending(5));
    }

    [TestMethod]
    public async Task Both_lanes_drain_and_stop_being_pending_once_read()
    {
        var queue = new MediaProcessingQueue();
        queue.Enqueue(1);
        queue.EnqueueBackfill(2);

        // Uploads drain before backfill.
        var first = await queue.ReadNextAsync(CancellationToken.None);
        var second = await queue.ReadNextAsync(CancellationToken.None);
        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);

        // Reading does not settle pending - the worker owns that via Done when it finishes.
        Assert.IsTrue(queue.IsPending(1));
        queue.Done(1);
        queue.Done(2);
        Assert.IsFalse(queue.IsPending(1));
        Assert.IsFalse(queue.IsPending(2));
    }
}
