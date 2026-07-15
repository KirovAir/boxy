using System.Diagnostics;
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
    FfmpegCapabilities capabilities,
    FileMetadataExtractor metadata,
    MediaProcessingQueue queue,
    ConversionProgress progress,
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
            finally
            {
                // However it ended - done, failed, or crashed - this item is no longer in flight, so its
                // live progress goes away. One place, so no exit path inside ProcessAsync can leak an entry.
                progress.Clear(id);
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
        // Note this does NOT exclude drop-offs, and must not. It is tempting - a box's contents are only
        // ever seen by its owner, so converting them automatically is CPU spent for nobody - but the way to
        // stop that is to give a drop-off a profile that asks for nothing (see ConversionProfiles.BoxFallback),
        // not to make the scan blind to boxes. An owner CAN ask for a box file to be converted, with Convert
        // again, and that request lives in an in-memory queue: a scan that skipped boxes would drop it on the
        // next restart and leave the item claiming a profile it never got.
        var heal = (await db.MediaItems
                .Where(m => m.Status == MediaStatus.Ready && m.ErrorMessage == null && m.VideoCodec != null)
                .Select(m => new
                {
                    m.Id,
                    State = new ConversionProfiles.RenditionState(m.Profile, m.ContentHash, m.Extension,
                        m.VideoCodec, m.WebFileName, m.WebCodec, m.HqFileName)
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
        var sw = Stopwatch.StartNew();

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

        // The effective video config governs the two heavy choices below: whether to make posters at all,
        // and the global conversion ceiling (Full/Remux/Off). Read once here so the item, what we serve, and
        // the startup heal all reason from the same answer.
        var videoCfg = await videoSettings.GetEffectiveAsync(ct);

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
            if (videoCfg.GeneratePosters
                && (await storage.ExistsAsync(thumb, ct) || await ProduceImageThumbnailAsync(originalPath, item.Extension, thumb, ct)))
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
            // Except for one case: a file ffprobe can no longer read AT ALL, which we already knew to be a
            // video (its codec is on the row from an earlier, working probe). Its bytes have gone bad. It
            // leaves here Ready and serving what it has, which is right - but with nothing recorded, the
            // reprocess scan would keep finding its renditions out of step with its profile, fail to fix
            // them, and queue it again on every single boot. Leave a tombstone so it is asked once.
            //
            // Never for an unreadable NON-video: a zip or an odd document also probes as nothing, and those
            // are perfectly fine as they are (their VideoCodec is null, so the scan ignores them anyway).
            if (probe is null && item.VideoCodec is not null)
            {
                item.ErrorMessage = "The stored file can no longer be read as a video.";
                logger.LogWarning("Video {Slug} could not be probed; leaving it as it was", item.Slug);
            }

            item.Status = MediaStatus.Ready;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Processed {Slug} as a shareable file ({Ext})", item.Slug, item.Extension);
            return;
        }

        // Video-track length is the right yardstick for the output truncation check (the container's
        // format.duration can be inflated by tracks we deliberately drop with -map).
        var expectedDuration = probe.VideoDuration ?? probe.Duration;

        // The worker has this video now, so give the polling UI something to show. Preparing covers the
        // probe and poster; the encode below reports Converting with a real percentage against the source
        // length. The entry is cleared centrally when the item leaves the pipeline (see ExecuteAsync).
        progress.Report(id, ConversionStage.Preparing);

        void ReportConverting(FfmpegProgress p) =>
            progress.Report(id, ConversionStage.Converting, Percent(p.OutTime, expectedDuration), p.Speed);

        // Poster frame (best-effort; a missing poster is not fatal). A reprocess (Convert again, or a heal)
        // must NOT touch a custom thumbnail the owner uploaded in Edit: it is stored under its own bytes'
        // hash, not the video's, so regenerating the auto {hash}.jpg here and reassigning PosterFileName
        // would both lose the curated frame and orphan its blob forever (there is no enumeration to reclaim
        // it). Only make or adopt the auto poster when the item has no custom one.
        var posterName = item.ContentHash + ".jpg";
        if (videoCfg.GeneratePosters && (item.PosterFileName is null || item.PosterFileName == posterName))
        {
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
        }

        // Every lane rewrites both renditions from scratch, so a re-convert to a different profile can't
        // leave the previous profile's rendition hanging around and being served. All four columns are
        // captured, not just the names: on a failed reprocess the item stays live and goes on serving these
        // exact files, so putting the names back while leaving the codecs null would keep the H.265
        // rendition on disk and in service but stop the share page from advertising it (it needs both), and
        // report the default lane as never processed.
        // Apply the global conversion ceiling and persist it, so what we store, serve, and later heal against
        // all agree. Remux and Off cap any transcoding profile down to AsUploaded; the worker decides
        // remux-vs-serve-original from the mode, in the AsUploaded branch below.
        item.Profile = ConversionProfiles.UnderMode(item.Profile, videoCfg.ConversionMode);

        // What we're about to do, up front: the old log went silent from here until the item was done, so a
        // conversion that was slow or stuck looked identical to one that never started. The completion line
        // below reports what actually came out (sizes, encoder, elapsed).
        logger.LogInformation(
            "Converting {Slug} \"{Title}\": {Codec}{Hdr} {Dims}, {Size}, {Profile} profile{Encoder}{Queued}",
            item.Slug, item.Title, probe.VideoCodec, probe.IsHdr ? " HDR" : "",
            Format.Dimensions(item.Width, item.Height), Format.Bytes(item.SizeBytes), item.Profile,
            ConversionProfiles.Transcodes(item.Profile) ? $", {videoCfg.Encoder} encoder" : "",
            queue.Depth > 0 ? $" ({queue.Depth} more queued)" : "");

        var previous = (item.WebFileName, item.WebCodec, item.HqFileName, item.HqCodecs);
        item.WebFileName = item.WebCodec = item.HqFileName = item.HqCodecs = null;

        // "Don't convert it": ship exactly what was uploaded. Serve an already-faststart mp4 as-is, else
        // stream-copy the source into a faststart mp4 so it at least streams (codec untouched); if even
        // that fails, serve the raw original. Never transcode - the uploader opted to own compatibility.
        if (item.Profile == ConversionProfile.AsUploaded)
        {
            // Off serves the original untouched - not even a remux. Remux/Full still stream-copy a container
            // that needs it (a .mov) into a faststart mp4, codec never re-encoded.
            var keptName = item.ContentHash + ConversionProfiles.WebSuffix(item.Profile);
            if (videoCfg.ConversionMode != ConversionMode.Off
                && !IsProgressiveMp4(item.Extension, originalPath)
                && await ProduceKeptOriginalAsync(originalPath, keptName, probe.VideoCodec, ct, ReportConverting))
            {
                item.WebFileName = keptName;
            }

            // Either way the codec is the source's: that is the whole point of this profile. When the remux
            // fails we serve the raw original, which for "don't convert it" IS the promise - do not resurrect
            // a prior web file to keep it "faststart". A prior -asis.mp4 is already reused by
            // ProduceKeptOriginalAsync above, so it never reaches here; anything else on the stem is a file
            // from a transcoding profile, and you cannot prove from metadata that an h264/same-size/same-pixfmt
            // file is a lossless repackage rather than a re-encode. Serving it would convert an upload the
            // owner said to leave alone. The raw original is always the safe, in-spec fallback.
            item.WebCodec = probe.VideoCodec;
            item.WebSizeBytes = item.WebFileName is null ? item.SizeBytes : await BlobSizeAsync(item.WebFileName, ct);
            item.HqSizeBytes = null;
            item.WebWidth = item.Width;
            item.WebHeight = item.Height;
            item.WebEncoder = item.WebFileName is not null ? "copied" : null; // remuxed, or the raw original
            item.EncodeCrf = null;
            item.EncodePreset = null;
            item.SourceIsHdr = probe.IsHdr;
            item.EncodeToneMapped = false;
            item.EncodeMs = (int)sw.Elapsed.TotalMilliseconds;
            await FinishAsync(db, item, previous.WebFileName, previous.HqFileName, ct);
            logger.LogInformation("Kept {Slug} as uploaded in {Seconds:0.0}s: {Codec} {Size} (remuxed={Remuxed})",
                item.Slug, sw.Elapsed.TotalSeconds, probe.VideoCodec, Format.Bytes(item.WebSizeBytes ?? 0),
                item.WebFileName is not null);
            return;
        }

        // The default lane is always H.264, whatever came in. An already-progressive H.264 mp4/m4v is
        // exactly that already, so it is served untouched - zero re-work, zero quality loss, at whatever
        // resolution it was uploaded at (the cap only ever applies to a file we are re-encoding anyway).
        // Everything else (.mov, non-faststart mp4, .mkv/.webm, VP9/AV1, 10-bit, and now H.265) gets one.
        var copyable = MediaProcessor.CanStreamCopyToMp4(probe.VideoCodec, probe.AudioCodec, probe.PixFmt);
        var serveAsIs = copyable && IsProgressiveMp4(item.Extension, originalPath);

        // How the default lane got made, for the completion log below. Defaults suit the serve-as-is case:
        // no re-encode, and the served file keeps the source's own dimensions.
        var lane = WebLane.ServedAsIs;
        VideoSettings? applied = null;
        var toneMapped = false;
        int? webWidth = item.Width, webHeight = item.Height;

        if (serveAsIs)
        {
            item.WebCodec = probe.VideoCodec;
        }
        else
        {
            var settings = ConversionProfiles.Settings(item.Profile, videoCfg);
            var webName = item.ContentHash + ConversionProfiles.WebSuffix(item.Profile);
            var produced = await ProduceWebFileAsync(originalPath, webName, copyable, probe, expectedDuration, settings, ct, ReportConverting);
            if (produced is null)
            {
                // Never take a live, published video offline because a reprocess failed - leave it serving
                // what it already had rather than 404ing it, and record the reason so the heal doesn't
                // retry this doomed item on every startup.
                (item.WebFileName, item.WebCodec, item.HqFileName, item.HqCodecs) = previous;
                await FailAsync(db, item, "Could not produce a playable web version", healing, ct);
                return;
            }

            item.WebFileName = webName;
            item.WebCodec = produced.Probe.VideoCodec;
            lane = produced.Lane;
            applied = produced.Applied;
            toneMapped = produced.ToneMapped;
            webWidth = produced.Probe.Width;
            webHeight = produced.Probe.Height;
        }

        // The default lane is produced; the remaining work (the H.265 sidecar, then saving) is Finishing.
        progress.Report(id, ConversionStage.Finishing);

        // The better rendition, offered ahead of the H.264 file and skipped by anything that can't decode
        // it. Only "Best" pays the extra file for it, and only an H.265 source has anything to give.
        if (ConversionProfiles.WantsHq(item.Profile))
        {
            await ProduceHqAsync(item, probe, originalPath, expectedDuration, ct);
        }

        // Record what came out, before FinishAsync (which is the save), so the file details view can show
        // the same facts the log line below reports.
        item.WebSizeBytes = item.WebFileName is null ? item.SizeBytes : await BlobSizeAsync(item.WebFileName, ct);
        item.HqSizeBytes = await BlobSizeAsync(item.HqFileName, ct);
        item.WebWidth = webWidth;
        item.WebHeight = webHeight;
        item.WebEncoder = EncoderName(lane);
        item.EncodeCrf = applied?.Crf;
        item.EncodePreset = lane == WebLane.Cpu ? applied?.Preset : null;
        item.SourceIsHdr = probe.IsHdr;
        item.EncodeToneMapped = toneMapped;
        item.EncodeMs = (int)sw.Elapsed.TotalMilliseconds;

        await FinishAsync(db, item, previous.WebFileName, previous.HqFileName, ct);

        var how = lane switch
        {
            WebLane.Gpu => "GPU-encoded",
            WebLane.Cpu => "CPU-encoded",
            WebLane.Copied => "stream-copied",
            WebLane.Reused => "reused",
            _ => "served as-is"
        };
        var ratio = item.WebSizeBytes is { } wb && item.SizeBytes > 0
            ? $" ({100.0 * wb / item.SizeBytes:0}% of original)"
            : "";
        var applies = applied is null
            ? ""
            : $", CRF/QP {applied.Crf}" + (lane == WebLane.Cpu ? $" preset {applied.Preset}" : "")
              + (applied.MaxLongEdge > 0 ? $", capped {applied.MaxLongEdge}px" : "") + (toneMapped ? ", HDR tone-mapped" : "");
        var speed = expectedDuration is > 0 && sw.Elapsed.TotalSeconds > 0.1
            ? $", {expectedDuration.Value / sw.Elapsed.TotalSeconds:0.0}x realtime"
            : "";

        logger.LogInformation(
            "Converted {Slug} in {Seconds:0.0}s: {How} web {Dims} {WebCodec} {WebSize}{Ratio}, hq {Hq} {HqSize}{Applies}{Speed}",
            item.Slug, sw.Elapsed.TotalSeconds, how, Format.Dimensions(webWidth, webHeight), item.WebCodec,
            Format.Bytes(item.WebSizeBytes ?? 0), ratio, item.HqCodecs ?? "none",
            item.HqSizeBytes is { } hb ? Format.Bytes(hb) : "none", applies, speed);
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
    private async Task<WebProvenance?> ProduceWebFileAsync(string originalPath, string webName, bool copyable,
        ProbeResult probe, double? duration, VideoSettings settings, CancellationToken ct,
        Action<FfmpegProgress>? onProgress = null)
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
                return new WebProvenance(reused, WebLane.Reused, null, false);
            }
        }

        var scratch = ScratchOut(".mp4");
        try
        {
            // Lossless remux when the source is already H.264 + AAC/MP3/none and only the container is
            // wrong (a .mov, or an mp4 with its moov atom at the end).
            if (copyable
                && await processor.RemuxFastStartAsync(originalPath, scratch, probe.VideoCodec, ct, onProgress)
                && await processor.ValidateWebOutputAsync(scratch, duration, MediaProcessor.UniversalCodecs, ct) is { } remuxed)
            {
                await storage.PutAsync(webName, scratch, ct);
                return new WebProvenance(remuxed, WebLane.Copied, null, false);
            }

            TryDeleteLocal(scratch);

            // Universal fallback: full transcode to H.264/AAC mp4. TranscodeWebAsync reports the encoder that
            // actually ran (it can fall back from GPU to CPU), so the provenance records what really happened.
            var caps = capabilities.ForEncoding();
            var encoder = await processor.TranscodeWebAsync(originalPath, scratch, settings, caps, probe.IsHdr, ct, onProgress);
            if (encoder is { } used
                && await processor.ValidateWebOutputAsync(scratch, duration, MediaProcessor.UniversalCodecs, ct) is { } encoded)
            {
                await storage.PutAsync(webName, scratch, ct);
                var lane = used == VideoEncoder.Hardware ? WebLane.Gpu : WebLane.Cpu;
                return new WebProvenance(encoded, lane, settings.Normalized(), probe.IsHdr && caps.CanToneMap);
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
    private async Task<bool> ProduceKeptOriginalAsync(string originalPath, string webName, string? videoCodec,
        CancellationToken ct, Action<FfmpegProgress>? onProgress = null)
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
            if (await processor.RemuxFastStartAsync(originalPath, scratch, videoCodec, ct, onProgress)
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

    /// <summary>
    /// Thumbnail for a still image. ffmpeg reads the common formats directly, but has no HEIF demuxer, so an
    /// iPhone HEIC/HEIF is first decoded to a PNG (via libheif) that ffmpeg can then scale. Everything else
    /// goes straight through the ffmpeg poster path. Returns true when a thumbnail was produced and stored.
    /// </summary>
    private async Task<bool> ProduceImageThumbnailAsync(string originalPath, string extension, string name, CancellationToken ct)
    {
        if (!MediaProcessor.IsHeif(extension))
        {
            return await ProducePosterAsync(originalPath, name, 0, false, ct);
        }

        var decoded = ScratchOut(".png");
        try
        {
            return await processor.DecodeHeifAsync(originalPath, decoded, ct)
                   && await ProducePosterAsync(decoded, name, 0, false, ct);
        }
        finally
        {
            TryDeleteLocal(decoded);
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

    /// <summary>The stored byte size of a rendition, read straight from the backend (a local file stat, or a
    /// remote HEAD via the serve descriptor), or null when there is no such blob. Best-effort: it feeds the
    /// log line and the file details, so a null just shows as unknown rather than failing the conversion.</summary>
    private async Task<long?> BlobSizeAsync(string? fileName, CancellationToken ct)
    {
        if (fileName is null)
        {
            return null;
        }

        // Truly non-fatal, as the summary promises: this runs before the Ready save, and on a remote
        // backend GetServeAsync does a network HEAD that can fail transiently. A size we can't read must
        // never strand an already-stored rendition in Processing - swallow everything but a real shutdown.
        try
        {
            return await storage.GetServeAsync(fileName, ct) switch
            {
                LocalBlobServe local => File.Exists(local.Path) ? new FileInfo(local.Path).Length : null,
                RemoteBlobServe remote => remote.Length,
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the size of {File}; recording it as unknown", fileName);
            return null;
        }
    }

    /// <summary>How far an encode is, as a whole-number percent of the source length, or null when the
    /// duration is unknown or implausibly short (the UI shows an indeterminate bar then). The store clamps
    /// to 0-100, so a slightly-long output can't overshoot.</summary>
    private static int? Percent(TimeSpan outTime, double? durationSeconds)
    {
        return durationSeconds is > 0.5 ? (int)(outTime.TotalSeconds / durationSeconds.Value * 100) : null;
    }

    /// <summary>The stored name for a lane, for <see cref="MediaItem.WebEncoder"/>. Null for served-as-is,
    /// which has no encoder of its own.</summary>
    private static string? EncoderName(WebLane lane)
    {
        return lane switch
        {
            WebLane.Gpu => "gpu",
            WebLane.Cpu => "cpu",
            WebLane.Copied => "copied",
            WebLane.Reused => "reused",
            _ => null
        };
    }

    /// <summary>How the default (H.264) lane's served file came to be, for the conversion log and the file
    /// details view.</summary>
    private enum WebLane
    {
        ServedAsIs, // an already web-safe upload, served untouched
        Copied, // stream-copied (remuxed) into a faststart mp4, codec unchanged
        Cpu, // transcoded with libx264
        Gpu, // transcoded with h264_vaapi
        Reused // an earlier valid rendition on disk was kept (dedup / heal)
    }

    /// <summary>What producing the default lane yielded: the probe of the produced file, how it was made, the
    /// settings that actually reached the encoder (null when nothing was re-encoded), and whether an HDR
    /// source was tone-mapped down to SDR.</summary>
    private sealed record WebProvenance(ProbeResult Probe, WebLane Lane, VideoSettings? Applied, bool ToneMapped);
}
