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

    /// <summary>A just-uploaded item someone is waiting on. Jumps the backfill.</summary>
    public void Enqueue(int mediaItemId)
    {
        uploads.Enqueue(mediaItemId);
        waiting.Release();
    }

    /// <summary>An already-published item being reprocessed (a startup heal, or an owner re-converting).
    /// It is already serving something, so it yields to anything a user is waiting on.</summary>
    public void EnqueueBackfill(int mediaItemId)
    {
        backfill.Enqueue(mediaItemId);
        waiting.Release();
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
