using Boxy.Data;
using Boxy.Data.Entities;

namespace Boxy.Web.Services;

/// <summary>
/// Background pipeline: for each queued item, probe metadata, extract a poster, and - only
/// when the original is not browser-playable - produce a web-safe mp4. Items interrupted by
/// a restart are re-queued on startup.
/// </summary>
public class MediaProcessingWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IBlobStore storage,
    MediaProcessor processor,
    FileMetadataExtractor metadata,
    MediaProcessingQueue queue,
    ILogger<MediaProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int id;
            try
            {
                id = await queue.ReadNextAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error processing media {Id}", id);
            }
        }
    }

    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Include Failed so items that failed under an older build (e.g. an image bug) heal on
        // the next start. A genuinely bad file just fails again - no loop.
        var pending = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Uploaded || m.Status == MediaStatus.Processing
                                                         || m.Status == MediaStatus.Failed)
            .Select(m => m.Id)
            .ToListAsync(ct);

        // One-time heal of items processed before universal-MP4 normalization: Ready videos still
        // served as their original that a modern browser/device can't play everywhere - any .mov
        // (served as video/quicktime, which desktop Chrome/Firefox reject) and any non-H.264 source
        // (VP9/AV1/VP8/HEVC that fail on iOS Safari). A successful heal sets WebFileName and a failed
        // one sets ErrorMessage, so a healed OR doomed item no longer matches - this self-terminates
        // and never re-runs a hopeless transcode on every startup, and never touches fine H.264 mp4s.
        var heal = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Ready && m.WebFileName == null && m.ErrorMessage == null
                        && (m.Extension == ".mov" || (m.VideoCodec != null && m.VideoCodec != "h264")))
            .Select(m => m.Id)
            .ToListAsync(ct);

        foreach (var id in pending)
        {
            queue.Enqueue(id);
        }

        // The heal goes in the slow lane: these items are already published and already serving
        // something, so a library-sized backfill must never delay the next real upload.
        foreach (var id in heal)
        {
            queue.EnqueueBackfill(id);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Re-queued {Count} unfinished media item(s) after startup", pending.Count);
        }

        if (heal.Count > 0)
        {
            logger.LogInformation("Queued {Count} legacy item(s) for universal-MP4 heal", heal.Count);
        }
    }

    private async Task ProcessAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            return;
        }

        // Reprocessing an already-published item (a legacy heal) must NOT flip it back to Processing -
        // ShareController 404s anything not Ready, so that would take the public /v page offline for
        // the whole reprocess window. Keep it Ready throughout and only add the normalized web file.
        var healing = item.Status == MediaStatus.Ready;
        if (!healing)
        {
            item.Status = MediaStatus.Processing;
        }

        item.ErrorMessage = null;
        await db.SaveChangesAsync(ct);

        // Pull the original to a local path for ffmpeg. On a remote backend this is a temp download,
        // deleted when this method returns; on the filesystem backend it's the blob itself (no copy).
        using var original = await storage.GetLocalCopyAsync(item.ContentHash + item.Extension, ct);
        if (original is null)
        {
            await FailAsync(db, item, "Stored file missing", healing, ct);
            return;
        }

        var originalPath = original.Path;
        var probe = await processor.ProbeAsync(originalPath, ct);
        if (probe is not null)
        {
            item.Width = probe.Width;
            item.Height = probe.Height;
            item.DurationSeconds = probe.Duration;
            item.VideoCodec = probe.VideoCodec;
            item.AudioCodec = probe.AudioCodec;
        }

        // Byte-derived capture date (best-effort; a null is normal - most non-camera files have none). The
        // extractor reads the local scratch file ffmpeg already pulled, so there's no second blob download.
        item.CapturedAt = metadata.CaptureDate(MediaKinds.FacetOf(item.Extension), originalPath, probe);

        // Images: ffprobe reports a still image as a single-frame "video" (mjpeg/png/…), so we
        // must special-case them by extension - a thumbnail is fine, but never transcode. Uses the one
        // shared classifier, so .avif/.svg are handled as images here exactly as they filter and icon
        // elsewhere (the old private list omitted them, rendering .avif as a broken video).
        if (MediaKinds.FacetOf(item.Extension) == MediaKind.Image)
        {
            // Distinct name so a .jpg original never becomes its own ffmpeg input+output.
            var thumb = item.ContentHash + "-thumb.jpg";
            if (await storage.ExistsAsync(thumb, ct) || await ProducePosterAsync(originalPath, thumb, 0, false, ct))
            {
                item.PosterFileName = thumb;
            }

            item.Status = MediaStatus.Ready;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Anything without a real video stream - PDFs, documents, audio, archives, or a file ffprobe
        // can't read - is shared and downloaded as-is. No transcode, no poster; the share page renders
        // the right preview (inline pdf/image/audio) or a download card from the file type.
        if (probe?.VideoCodec is null)
        {
            item.Status = MediaStatus.Ready;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Processed {Slug} as a shareable file ({Ext})", item.Slug, item.Extension);
            return;
        }

        var copyable = MediaProcessor.CanStreamCopyToMp4(probe.VideoCodec, probe.AudioCodec, probe.PixFmt);
        // Video-track length is the right yardstick for the output truncation check (the container's
        // format.duration can be inflated by tracks we deliberately drop with -map).
        var expectedDuration = probe.VideoDuration ?? probe.Duration;

        // Poster frame (best-effort; a missing poster is not fatal).
        var posterName = item.ContentHash + ".jpg";
        if (await storage.ExistsAsync(posterName, ct))
        {
            item.PosterFileName = posterName;
        }
        else
        {
            // Seek ~10% in to skip intros and fade-from-black, then let the thumbnail filter pick the
            // best frame of the window there. Falls back to the start when the duration is unknown.
            var at = probe.Duration is > 0 ? probe.Duration.Value * 0.1 : 0;
            if (await ProducePosterAsync(originalPath, posterName, at, true, ct))
            {
                item.PosterFileName = posterName;
            }
        }

        // "Keep original" upload: skip normalization. Serve an already-faststart mp4 as-is, else
        // stream-copy the source into a faststart mp4 (codec untouched); if even that fails, serve the
        // raw original. Never transcode - the uploader opted to own compatibility.
        if (item.KeepOriginal)
        {
            var keptName = item.ContentHash + "-web.mp4";
            if (!IsProgressiveMp4(item.Extension, originalPath)
                && await ProduceKeptOriginalAsync(originalPath, keptName, probe.VideoCodec, ct))
            {
                item.WebFileName = keptName;
            }

            item.Status = MediaStatus.Ready;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Processed {Slug} keeping original codec {Codec} (web={Web})",
                item.Slug, probe.VideoCodec, item.WebFileName is not null);
            return;
        }

        // An already-progressive H.264 mp4/m4v is browser-universal as-is - serve the original,
        // zero re-work, zero quality loss. Everything else (.mov, non-faststart mp4, .mkv/.webm,
        // VP9/AV1, 10-bit H.264) gets a normalized -web.mp4; an HEVC source (8- or 10-bit, HDR kept)
        // is stream-copied into it (with the hvc1 tag Safari needs) rather than re-encoded.
        var serveAsIs = copyable && IsProgressiveMp4(item.Extension, originalPath);
        if (!serveAsIs)
        {
            var webName = item.ContentHash + "-web.mp4";
            if (await ProduceWebFileAsync(originalPath, webName, copyable, probe.VideoCodec, expectedDuration, ct))
            {
                item.WebFileName = webName;
            }
            else if (healing)
            {
                // Never take a live, published video offline because a reprocess failed - leave it
                // served as the original (still video/mp4 on the wire) rather than 404ing it. Record
                // the failure so the one-time heal doesn't retry this doomed item on every startup.
                logger.LogWarning("Heal reprocess couldn't produce a web file for {Slug}; left as original", item.Slug);
                item.Status = MediaStatus.Ready;
                item.ErrorMessage = "Legacy heal skipped (could not produce a web version)";
                await db.SaveChangesAsync(ct);
                return;
            }
            else
            {
                await FailAsync(db, item, "Could not produce a playable web version", healing, ct);
                return;
            }
        }

        item.Status = MediaStatus.Ready;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Processed {Slug}: {W}x{H}, copyable={Copy}, servedAsIs={AsIs}, web={Web}",
            item.Slug, item.Width, item.Height, copyable, serveAsIs, item.WebFileName is not null);
    }

    /// <summary>
    /// Produces a validated, browser-universal <c>-web.mp4</c>: reuse a prior valid copy, else a
    /// lossless stream-copy remux when the codecs allow it, else a full transcode. Every branch
    /// re-probes the output (ffmpeg can exit 0 on a truncated file), so a bad remux falls through
    /// to a transcode and a bad transcode fails cleanly instead of serving a broken file.
    /// </summary>
    private async Task<bool> ProduceWebFileAsync(string originalPath, string webName, bool copyable, string? videoCodec, double? duration, CancellationToken ct)
    {
        // Reuse a valid web file from an earlier run / another item with identical content.
        if (await storage.ExistsAsync(webName, ct))
        {
            using var existing = await storage.GetLocalCopyAsync(webName, ct);
            if (existing is not null && await processor.ValidateWebOutputAsync(existing.Path, duration, ct))
            {
                return true;
            }
        }

        var scratch = ScratchOut(".mp4");
        try
        {
            // Lossless remux when the source codecs are already browser-safe (H.264/HEVC + AAC/MP3/none).
            if (copyable
                && await processor.RemuxFastStartAsync(originalPath, scratch, videoCodec, ct)
                && await processor.ValidateWebOutputAsync(scratch, duration, ct))
            {
                await storage.PutAsync(webName, scratch, ct);
                return true;
            }

            TryDeleteLocal(scratch);

            // Universal fallback: full transcode to H.264/AAC mp4.
            if (await processor.TranscodeWebAsync(originalPath, scratch, ct)
                && await processor.ValidateWebOutputAsync(scratch, duration, ct))
            {
                await storage.PutAsync(webName, scratch, ct);
                return true;
            }

            return false;
        }
        finally
        {
            TryDeleteLocal(scratch);
        }
    }

    /// <summary>
    /// For a "keep original" upload: make the source streamable without re-encoding. Produces (or
    /// reuses) a faststart mp4 by stream-copy, codec untouched. Returns false when even the remux
    /// fails, so the caller serves the raw original. No codec restriction; the uploader owns playback.
    /// </summary>
    private async Task<bool> ProduceKeptOriginalAsync(string originalPath, string webName, string? videoCodec, CancellationToken ct)
    {
        // Reuse a valid faststart mp4 from an earlier run / another item with identical content.
        if (await storage.ExistsAsync(webName, ct))
        {
            using var existing = await storage.GetLocalCopyAsync(webName, ct);
            if (existing is not null && MediaProcessor.IsFastStartMp4(existing.Path))
            {
                return true;
            }
        }

        var scratch = ScratchOut(".mp4");
        try
        {
            if (await processor.RemuxFastStartAsync(originalPath, scratch, videoCodec, ct)
                && MediaProcessor.IsFastStartMp4(scratch))
            {
                await storage.PutAsync(webName, scratch, ct);
                return true;
            }

            return false;
        }
        finally
        {
            TryDeleteLocal(scratch);
        }
    }

    // Run ffmpeg's poster generator to a local scratch file, then store it under the given name.
    private async Task<bool> ProducePosterAsync(string originalPath, string name, double at, bool representative, CancellationToken ct)
    {
        var scratch = ScratchOut(".jpg");
        try
        {
            if (await processor.GeneratePosterAsync(originalPath, scratch, at, representative, ct))
            {
                await storage.PutAsync(name, scratch, ct);
                return true;
            }

            return false;
        }
        finally
        {
            TryDeleteLocal(scratch);
        }
    }

    // A unique local scratch path for an ffmpeg output; PutAsync moves it into the store on success.
    private string ScratchOut(string extension)
    {
        return Path.Combine(storage.ScratchDir, $"tmp_{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            /* best-effort scratch cleanup */
        }
    }

    private static bool IsProgressiveMp4(string extension, string path)
    {
        // .mov is intentionally excluded: served as video/quicktime it's rejected by desktop
        // Chrome/Firefox, so a .mov is always remuxed into a real .mp4.
        return (extension is ".mp4" or ".m4v") && MediaProcessor.IsFastStartMp4(path);
    }

    private async Task FailAsync(AppDbContext db, MediaItem item, string message, bool healing, CancellationToken ct)
    {
        if (healing)
        {
            // Reprocessing an already-published item that failed - don't demote it to Failed (that
            // would 404 its /v page); leave it Ready serving what it already had. Record the reason so
            // the one-time heal doesn't re-attempt this doomed item on every startup.
            item.Status = MediaStatus.Ready;
            item.ErrorMessage = "Legacy heal skipped: " + message;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Heal reprocess of {Slug} failed ({Message}); left published", item.Slug, message);
            return;
        }

        item.Status = MediaStatus.Failed;
        item.ErrorMessage = message;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Media {Slug} failed: {Message}", item.Slug, message);
    }
}
