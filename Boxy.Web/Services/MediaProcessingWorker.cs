using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web.Models;

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
    VideoSettingsProvider videoSettings,
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

        // Reprocess every published video whose files no longer match what its profile asks for: the ones
        // whose default lane still isn't H.264 (a browser with no H.265 decoder cannot play them at all),
        // and the ones whose owner asked for a different conversion that the in-memory queue lost to a
        // restart. ErrorMessage excludes the ones already found to be hopeless, so nothing is retried
        // forever. See ConversionProfiles.NeedsReprocessing - the rules live there so they can be tested,
        // and so this stays one definition rather than a predicate duplicated in SQL.
        //
        // Evaluated in memory, over a narrow projection, because the rules compose blob names from the
        // profile and that is not worth expressing as a CASE in SQL for a query that runs once at boot.
        var heal = (await db.MediaItems
                .Where(m => m.Status == MediaStatus.Ready && m.ErrorMessage == null && m.VideoCodec != null)
                .Select(m => new
                {
                    m.Id,
                    State = new ConversionProfiles.RenditionState(m.Profile, m.ContentHash, m.VideoCodec,
                        m.WebFileName, m.WebCodec, m.HqFileName)
                })
                .ToListAsync(ct))
            .Where(x => ConversionProfiles.NeedsReprocessing(x.State))
            .Select(x => x.Id)
            .ToList();

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
            logger.LogInformation("Queued {Count} video(s) whose files don't match their conversion profile", heal.Count);
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

        // Every lane rewrites both renditions from scratch, so a re-convert to a different profile can't
        // leave the previous profile's rendition hanging around and being served.
        var (previousWeb, previousHq) = (item.WebFileName, item.HqFileName);
        item.WebFileName = item.WebCodec = item.HqFileName = item.HqCodecs = null;

        // "Don't convert it": ship exactly what was uploaded. Serve an already-faststart mp4 as-is, else
        // stream-copy the source into a faststart mp4 so it at least streams (codec untouched); if even
        // that fails, serve the raw original. Never transcode - the uploader opted to own compatibility.
        if (item.Profile == ConversionProfile.AsUploaded)
        {
            var keptName = item.ContentHash + ConversionProfiles.WebSuffix(item.Profile);
            if (!IsProgressiveMp4(item.Extension, originalPath)
                && await ProduceKeptOriginalAsync(originalPath, keptName, probe.VideoCodec, ct))
            {
                item.WebFileName = keptName;
            }

            // Either way the codec is the source's: that is the whole point of this profile.
            item.WebCodec = probe.VideoCodec;
            await FinishAsync(db, item, previousWeb, previousHq, ct);
            logger.LogInformation("Processed {Slug} as uploaded, codec {Codec} (remuxed={Remuxed})",
                item.Slug, probe.VideoCodec, item.WebFileName is not null);
            return;
        }

        // The default lane is always H.264, whatever came in. An already-progressive H.264 mp4/m4v is
        // exactly that already, so it is served untouched - zero re-work, zero quality loss, at whatever
        // resolution it was uploaded at (the cap only ever applies to a file we are re-encoding anyway).
        // Everything else (.mov, non-faststart mp4, .mkv/.webm, VP9/AV1, 10-bit, and now H.265) gets one.
        var copyable = MediaProcessor.CanStreamCopyToMp4(probe.VideoCodec, probe.AudioCodec, probe.PixFmt);
        var serveAsIs = copyable && IsProgressiveMp4(item.Extension, originalPath);
        if (serveAsIs)
        {
            item.WebCodec = probe.VideoCodec;
        }
        else
        {
            var settings = ConversionProfiles.Settings(item.Profile, await videoSettings.GetEffectiveAsync(ct));
            var webName = item.ContentHash + ConversionProfiles.WebSuffix(item.Profile);
            var produced = await ProduceWebFileAsync(originalPath, webName, copyable, probe.VideoCodec, expectedDuration, settings, ct);
            if (produced is null)
            {
                // Never take a live, published video offline because a reprocess failed - leave it serving
                // what it already had rather than 404ing it, and record the reason so the heal doesn't
                // retry this doomed item on every startup.
                item.WebFileName = previousWeb;
                item.HqFileName = previousHq;
                await FailAsync(db, item, "Could not produce a playable web version", healing, ct);
                return;
            }

            item.WebFileName = webName;
            item.WebCodec = produced.VideoCodec;
        }

        // The better rendition, offered ahead of the H.264 file and skipped by anything that can't decode
        // it. Only "Best" pays the extra file for it, and only an H.265 source has anything to give.
        if (ConversionProfiles.WantsHq(item.Profile))
        {
            await ProduceHqAsync(item, probe, originalPath, expectedDuration, ct);
        }

        await FinishAsync(db, item, previousWeb, previousHq, ct);
        logger.LogInformation("Processed {Slug}: {W}x{H}, servedAsIs={AsIs}, web={Web} ({WebCodec}), hq={Hq}",
            item.Slug, item.Width, item.Height, serveAsIs, item.WebFileName ?? "original", item.WebCodec,
            item.HqCodecs ?? "none");
    }

    /// <summary>
    /// Mark the item Ready and drop whatever rendition it used to have and no longer references. The blob
    /// names are content-addressed, so an unreferenced one is invisible to every other query: if it isn't
    /// deleted here it is never deleted at all (IBlobStore has no enumeration API to find it again).
    /// </summary>
    private async Task FinishAsync(AppDbContext db, MediaItem item, string? previousWeb, string? previousHq, CancellationToken ct)
    {
        item.Status = MediaStatus.Ready;
        await db.SaveChangesAsync(ct);

        foreach (var stale in new[] { previousWeb, previousHq })
        {
            // Only ever clean up files the worker itself derived. HqFileName can point at the ORIGINAL
            // blob (an upload that already is a faststart hvc1 mp4 needs no second file), and an original
            // is not this method's to delete: it goes when its item does, on the paths that check the hash.
            if (stale is null || stale == item.WebFileName || stale == item.HqFileName
                || !ConversionProfiles.IsDerivedRendition(stale))
            {
                continue;
            }

            // Dedup-safe: identical bytes uploaded twice share their derived files too.
            if (!await db.MediaItems.AnyAsync(m => m.WebFileName == stale || m.HqFileName == stale, ct))
            {
                await storage.DeleteAsync(stale, ct);
            }
        }
    }

    /// <summary>
    /// Produces the validated H.264 file <c>/f/{slug}</c> serves: reuse a prior valid copy, else a lossless
    /// stream-copy remux when the source is already H.264, else a full transcode. Every branch re-probes the
    /// output (ffmpeg can exit 0 on a truncated file) against the universal codec set, so a bad remux falls
    /// through to a transcode and a bad transcode fails cleanly instead of serving a broken file. Returns the
    /// probe of what was produced, or null when nothing servable could be made.
    /// </summary>
    private async Task<ProbeResult?> ProduceWebFileAsync(string originalPath, string webName, bool copyable,
        string? videoCodec, double? duration, VideoSettings settings, CancellationToken ct)
    {
        // Reuse a valid web file from an earlier run / another item with identical content. The codec set
        // is what makes this safe across the H.265 change: an -h264.mp4 that somehow isn't H.264 is
        // rejected here and rebuilt, rather than being trusted because the name looks right.
        if (await storage.ExistsAsync(webName, ct))
        {
            using var existing = await storage.GetLocalCopyAsync(webName, ct);
            if (existing is not null
                && await processor.ValidateWebOutputAsync(existing.Path, duration, MediaProcessor.UniversalCodecs, ct) is { } reused)
            {
                return reused;
            }
        }

        var scratch = ScratchOut(".mp4");
        try
        {
            // Lossless remux when the source is already H.264 + AAC/MP3/none and only the container is
            // wrong (a .mov, or an mp4 with its moov atom at the end).
            if (copyable
                && await processor.RemuxFastStartAsync(originalPath, scratch, videoCodec, ct)
                && await processor.ValidateWebOutputAsync(scratch, duration, MediaProcessor.UniversalCodecs, ct) is { } remuxed)
            {
                await storage.PutAsync(webName, scratch, ct);
                return remuxed;
            }

            TryDeleteLocal(scratch);

            // Universal fallback: full transcode to H.264/AAC mp4.
            if (await processor.TranscodeWebAsync(originalPath, scratch, settings, ct)
                && await processor.ValidateWebOutputAsync(scratch, duration, MediaProcessor.UniversalCodecs, ct) is { } encoded)
            {
                await storage.PutAsync(webName, scratch, ct);
                return encoded;
            }

            return null;
        }
        finally
        {
            TryDeleteLocal(scratch);
        }
    }

    /// <summary>
    /// Offers the source H.265 alongside the H.264 file, so a device with an HEVC decoder gets the upload
    /// untouched - HDR, 10-bit, full bitrate - and everything else quietly skips it. Sets HqFileName and
    /// HqCodecs, or leaves both null when there is nothing to offer or nothing we can describe exactly.
    ///
    /// Best case there is no second file at all: when the original is already a faststart, hvc1-tagged mp4
    /// (most modern phones and drones), the "rendition" is the original blob and we write nothing.
    ///
    /// A failure here is never fatal. The H.264 file is already made, so the video plays for everyone; the
    /// worst case is that Apple viewers see the H.264 copy instead of the nicer original.
    /// </summary>
    private async Task ProduceHqAsync(MediaItem item, ProbeResult probe, string originalPath, double? duration, CancellationToken ct)
    {
        if (!MediaProcessor.CanKeepAsHq(probe.VideoCodec, probe.AudioCodec, probe.PixFmt))
        {
            return;
        }

        // The upload IS the rendition: already an mp4 that streams, already tagged the way Safari demands.
        if (IsProgressiveMp4(item.Extension, originalPath) && MediaProcessor.HevcCodecs(probe) is { } asIs)
        {
            item.HqFileName = item.ContentHash + item.Extension;
            item.HqCodecs = asIs;
            return;
        }

        var hqName = item.ContentHash + ConversionProfiles.HqSuffix;
        if (await storage.ExistsAsync(hqName, ct))
        {
            using var existing = await storage.GetLocalCopyAsync(hqName, ct);
            if (existing is not null
                && await processor.ValidateWebOutputAsync(existing.Path, duration, MediaProcessor.HqCodecSet, ct) is { } reused
                && MediaProcessor.HevcCodecs(reused) is { } reusedCodecs)
            {
                item.HqFileName = hqName;
                item.HqCodecs = reusedCodecs;
                return;
            }
        }

        var scratch = ScratchOut(".mp4");
        try
        {
            // Stream-copy only. Re-encoding H.265 to H.265 would burn an hour to lose quality; if the copy
            // won't go into an mp4, there is simply nothing better to offer than the H.264 file.
            if (await processor.RemuxFastStartAsync(originalPath, scratch, probe.VideoCodec, ct)
                && await processor.ValidateWebOutputAsync(scratch, duration, MediaProcessor.HqCodecSet, ct) is { } made
                // Describe the file we are about to SERVE, not the one that came in: the remux forces the
                // hvc1 tag, so this is the only probe whose answer is true of the bytes a browser will get.
                && MediaProcessor.HevcCodecs(made) is { } codecs)
            {
                await storage.PutAsync(hqName, scratch, ct);
                item.HqFileName = hqName;
                item.HqCodecs = codecs;
                return;
            }

            logger.LogInformation("No H.265 rendition for {Slug}; the H.264 file is the only source", item.Slug);
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
