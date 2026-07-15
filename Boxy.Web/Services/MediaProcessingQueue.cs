using System.Collections.Concurrent;

namespace Boxy.Web.Services;

/// <summary>
/// In-process hand-off from upload requests to the background <see cref="MediaProcessingWorker"/>.
/// Uploads stay fast; probing/poster/transcode happen out of band.
///
/// Two lanes, because the worker is strictly serial (one ffmpeg at a time) and a backfill can be as
/// long as the library: a fresh upload must never queue behind a re-encode of everything, or its share
/// link 404s for hours. <see cref="Enqueue"/> is the fast lane; <see cref="EnqueueBackfill"/> is drained
/// only when no upload is waiting.
/// </summary>
public class MediaProcessingQueue
{
    private readonly ConcurrentQueue<int> uploads = new();
    private readonly ConcurrentQueue<int> backfill = new();

    // One permit per queued item, whichever lane it went into: the reader blocks until there is
    // genuinely something to do, then picks the lane itself.
    private readonly SemaphoreSlim waiting = new(0);

    // How many outstanding conversions each item has: incremented on enqueue, decremented when the worker
    // reports it done (Done). This is what the status endpoint reads to show "Queued" - both for an item
    // waiting its turn and for a second "convert again" stacked behind a run already in progress. Keeping it
    // here, rather than seeding the live-progress store from the request thread, is what makes the "Queued"
    // state race-free: the queue owns "waiting to run", the worker owns "running", and the endpoint prefers
    // the latter. A count (not a flag) so two requests for one item both clear correctly.
    private readonly ConcurrentDictionary<int, int> pending = new();

    /// <summary>How many items are waiting across both lanes right now (the one being worked on has already
    /// been dequeued, so it is not counted). For a "still N queued" note when a conversion starts.</summary>
    public int Depth => uploads.Count + backfill.Count;

    /// <summary>A just-uploaded item someone is waiting on. Jumps the backfill.</summary>
    public void Enqueue(int mediaItemId)
    {
        pending.AddOrUpdate(mediaItemId, 1, (_, count) => count + 1);
        uploads.Enqueue(mediaItemId);
        waiting.Release();
    }

    /// <summary>An already-published item being reprocessed (a startup heal, or an owner re-converting).
    /// It is already serving something, so it yields to anything a user is waiting on.</summary>
    public void EnqueueBackfill(int mediaItemId)
    {
        pending.AddOrUpdate(mediaItemId, 1, (_, count) => count + 1);
        backfill.Enqueue(mediaItemId);
        waiting.Release();
    }

    /// <summary>Whether the item is enqueued and not yet finished: waiting its turn, or a repeat request
    /// stacked behind a run already under way. The status endpoint shows this as "Queued", but prefers the
    /// worker's live "running" report when there is one, so an actively-encoding item reads as Converting
    /// rather than Queued even while its (still-outstanding) count keeps it pending here.</summary>
    public bool IsPending(int mediaItemId)
    {
        return pending.TryGetValue(mediaItemId, out var count) && count > 0;
    }

    /// <summary>Called by the worker once it has finished an item, however it ended, to balance the enqueue
    /// so the item stops counting as pending. Drops the key at zero - and only at zero, atomically, so a
    /// re-enqueue that races the finish is not lost.</summary>
    public void Done(int mediaItemId)
    {
        var remaining = pending.AddOrUpdate(mediaItemId, 0, (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
        {
            pending.TryRemove(new KeyValuePair<int, int>(mediaItemId, 0));
        }
    }

    /// <summary>The next item to process, uploads first. Waits until one is available.</summary>
    public async ValueTask<int> ReadNextAsync(CancellationToken ct)
    {
        while (true)
        {
            await waiting.WaitAsync(ct);
            if (uploads.TryDequeue(out var id) || backfill.TryDequeue(out id))
            {
                return id;
            }
        }
    }
}
