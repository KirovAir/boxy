using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Boxy.Web.Models;
using Microsoft.Extensions.Options;

namespace Boxy.Web.Services;

/// <summary>
/// Thin wrapper over ffprobe/ffmpeg: inspects a file, extracts a poster frame, and produces the mp4 we
/// serve - a lossless remux when the source codecs already allow it, otherwise a transcode to H.264.
/// Every invocation is bounded by a timeout so one bad file can't wedge the processing queue.
/// </summary>
public class MediaProcessor(
    IOptions<FfmpegSettings> ffmpeg,
    ILogger<MediaProcessor> logger)
{
    // The file a browser gets by default is ALWAYS H.264 8-bit 4:2:0 in an mp4-family container, because
    // that is the only combination with no asterisks next to it: every browser, every OS, no hardware
    // decoder required. HEVC/H.265 used to be on this list, which was a bet that the viewer had a hardware
    // decoder - and on a share link you don't know the viewer, so it isn't a bet you can make. Firefox on
    // Linux has no HEVC decoder at all, and it got a black screen.
    //
    // H.265 is not gone, it is demoted: see HqVideoCodecs. It rides along as a SECOND <source> that a
    // capable device takes first and everything else skips, which is the same win without the black screen.
    private static readonly string[] Mp4FamilyContainers = [".mp4", ".m4v", ".mov"];
    private static readonly string[] CopyableAudioCodecs = ["aac", "mp3"];
    private static readonly string[] CopyableVideoCodecs = ["h264"];

    // Codecs worth keeping whole as the pickier, better rendition: the source H.265, HDR and 10-bit
    // intact, rather than flattened into the H.264 copy. Offered ahead of it and skipped by any browser
    // without the decoder, so nothing is risked by offering it.
    private static readonly string[] HqVideoCodecs = ["hevc"];

    /// <summary>What the file behind <c>/f/{slug}</c> is allowed to be. The worker validates its output
    /// against this, which is also what makes an H.265 file produced by an older build get rejected and
    /// re-made instead of being silently reused.</summary>
    public static IReadOnlyCollection<string> UniversalCodecs => CopyableVideoCodecs;

    /// <summary>What the optional better rendition is allowed to be.</summary>
    public static IReadOnlyCollection<string> HqCodecSet => HqVideoCodecs;

    // Binary paths and per-operation timeouts are deployment concerns: bound once at boot from the
    // "Ffmpeg" section, never editable in-app (an HTTP-writable executable path would be a remote-code-
    // execution primitive). The video-quality knobs are runtime-editable and come from
    // VideoSettingsProvider at transcode time instead, so an admin's change applies without a restart.
    private readonly FfmpegSettings ffmpegOptions = ffmpeg.Value;

    private string FfmpegPath => ffmpegOptions.FfmpegPath;
    private string FfprobePath => ffmpegOptions.FfprobePath;

    private TimeSpan ProbeTimeout => TimeSpan.FromSeconds(ffmpegOptions.ProbeTimeoutSeconds);
    private TimeSpan PosterTimeout => TimeSpan.FromSeconds(ffmpegOptions.PosterTimeoutSeconds);
    private TimeSpan TranscodeTimeout => TimeSpan.FromMinutes(ffmpegOptions.TranscodeTimeoutMinutes);

    public async Task<ProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(FfprobePath,
            $"-v error -print_format json -show_streams -show_format \"{filePath}\"", ProbeTimeout, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            return ParseProbe(stdout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse ffprobe output for {Path}", filePath);
            return null;
        }
    }

    /// <summary>The ffprobe-JSON → <see cref="ProbeResult"/> mapping, pulled out as a pure static so it can
    /// be exercised with captured fixtures (like <see cref="IsWebPlayable"/>/<see cref="CanStreamCopyToMp4"/>).</summary>
    public static ProbeResult ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int? width = null, height = null, rotation = null, level = null;
        string? videoCodec = null, audioCodec = null, pixFmt = null, videoProfile = null, codecTag = null;
        string? colorTransfer = null;
        double? videoDuration = null;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (type == "video" && videoCodec is null)
                {
                    // Skip embedded cover art / thumbnails (disposition.attached_pic == 1) - ffprobe
                    // reports them as an mjpeg/png "video" stream, which would otherwise make an
                    // audio-only file look like a (broken) video.
                    if (IsAttachedPic(s))
                    {
                        continue;
                    }

                    videoCodec = codec;
                    width = s.TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    height = s.TryGetProperty("height", out var h) ? h.GetInt32() : null;
                    pixFmt = s.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                    // Profile / level / container tag: the three things an RFC 6381 codecs parameter is
                    // made of. Without them we can't tell a browser precisely what a source is, and a
                    // source we can't describe precisely is one we don't offer at all.
                    videoProfile = s.TryGetProperty("profile", out var pr) ? pr.GetString() : null;
                    level = s.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number
                        ? lv.GetInt32()
                        : null;
                    codecTag = s.TryGetProperty("codec_tag_string", out var tg) ? tg.GetString() : null;
                    // The transfer curve is what makes a file HDR, and 10-bit alone is NOT it: plenty of
                    // 10-bit footage is ordinary SDR, and tone-mapping that would wreck it.
                    colorTransfer = s.TryGetProperty("color_transfer", out var trc) ? trc.GetString() : null;
                    // Per-stream duration (when present) is the length of the video track itself -
                    // the right yardstick for truncation checks, since we -map only this stream and
                    // the container's format.duration can be inflated by other (dropped) tracks.
                    videoDuration = ParseSeconds(s, "duration");
                    rotation = ReadRotation(s);
                    // Store DISPLAY dimensions: a portrait phone clip is encoded landscape with a 90°
                    // rotation flag, so swap to what the viewer actually sees. Playback is unaffected -
                    // ffmpeg auto-rotates on decode and the transcode reads codec/duration, not these.
                    if (rotation is 90 or 270 or -90 or -270)
                    {
                        (width, height) = (height, width);
                    }
                }
                else if (type == "audio" && audioCodec is null)
                {
                    audioCodec = codec;
                }
            }
        }

        DateTime? creation = null;
        if (root.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty("creation_time", out var ct)
            && DateTimeOffset.TryParse(ct.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            creation = dto.UtcDateTime;
        }

        var duration = root.TryGetProperty("format", out var f2) ? ParseSeconds(f2, "duration") : null;

        return new ProbeResult(width, height, duration, videoCodec, audioCodec, pixFmt, videoDuration,
            creation, rotation is null ? null : Math.Abs(rotation.Value) % 360,
            videoProfile, level, codecTag, colorTransfer);
    }

    // Rotation lives either in a stream tag (tags.rotate) or a "Display Matrix" side-data entry, depending
    // on the ffmpeg build; try both.
    private static int? ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty("rotate", out var rt)
            && int.TryParse(rt.GetString(), out var r))
        {
            return r;
        }

        if (stream.TryGetProperty("side_data_list", out var sd))
        {
            foreach (var d in sd.EnumerateArray())
            {
                if (d.TryGetProperty("rotation", out var rv) && rv.ValueKind == JsonValueKind.Number)
                {
                    return rv.GetInt32();
                }
            }
        }

        return null;
    }

    private static bool IsAttachedPic(JsonElement stream)
    {
        return stream.TryGetProperty("disposition", out var disp)
               && disp.TryGetProperty("attached_pic", out var ap)
               && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;
    }

    private static double? ParseSeconds(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out var v)
               && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Extracts a single JPEG frame (downscaled to ≤1280px wide) as the poster. For videos, pass
    /// <paramref name="representative"/> so the thumbnail filter picks the most representative frame
    /// of the window at the seek point (histogram-based) instead of whatever lands there - this skips
    /// the near-black / low-detail frames a fixed grab hits on fade-ins and intro cards.
    /// </summary>
    public async Task<bool> GeneratePosterAsync(string input, string output, double? atSeconds, bool representative = false, CancellationToken ct = default)
    {
        // Only seek for videos (atSeconds > 0). A leading -ss on a still image seeks past its
        // single frame and produces an empty file. force_divisible_by=2 keeps both dimensions
        // even - odd-sized sources (e.g. a 945-wide photo) otherwise fail JPEG 4:2:0 encoding.
        var seek = atSeconds is > 0
            ? $"-ss {atSeconds.Value.ToString("0.000", CultureInfo.InvariantCulture)} "
            : "";
        // thumbnail scans the batch of frames that follows the seek and keeps the one whose colour
        // histogram deviates most from the batch average, so uniform/near-black frames lose out.
        var pick = representative ? "thumbnail," : "";
        // -f forces the muxer so the ".part" temp extension doesn't confuse ffmpeg's format guess.
        var tmp = ScratchPath(output);
        var (code, _, err) = await RunAsync(FfmpegPath,
            $"-nostdin -hide_banner -nostats -loglevel error -y {seek}-i \"{input}\" -frames:v 1 -update 1 -vf \"{pick}scale=w='min(1280,iw)':h=-2:force_divisible_by=2\" -q:v 3 -f image2 \"{tmp}\"",
            PosterTimeout, ct);
        if (code != 0)
        {
            logger.LogWarning("Poster generation failed for {Path}: {Err}", input, Tail(err));
        }

        return Finalize(code, tmp, output);
    }

    /// <summary>
    /// Normalizes an uploaded image into a JPEG poster. With target dimensions (a video's resolution)
    /// the image is scaled to cover and centre-cropped to that aspect, so the poster lines up with the
    /// video frame instead of being stretched or letterboxed; without them it just downscales to
    /// ≤1280px wide, keeping the image's own aspect.
    /// </summary>
    public async Task<bool> ResizeThumbnailAsync(string input, string output, int? width, int? height, CancellationToken ct = default)
    {
        string filter;
        if (width is > 0 && height is > 0)
        {
            var (tw, th) = FitWithin(width.Value, height.Value, 1280);
            filter = $"scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}";
        }
        else
        {
            filter = "scale=w='min(1280,iw)':h=-2:force_divisible_by=2";
        }

        var tmp = ScratchPath(output);
        var (code, _, err) = await RunAsync(FfmpegPath,
            $"-nostdin -hide_banner -nostats -loglevel error -y -i \"{input}\" -frames:v 1 -update 1 -vf \"{filter}\" -q:v 3 -f image2 \"{tmp}\"",
            PosterTimeout, ct);
        if (code != 0)
        {
            logger.LogWarning("Thumbnail resize failed for {Path}: {Err}", input, Tail(err));
        }

        return Finalize(code, tmp, output);
    }

    /// <summary>Scale (w,h) down to fit a max long edge, rounded to even dimensions for JPEG 4:2:0.</summary>
    private static (int W, int H) FitWithin(int width, int height, int maxLongEdge)
    {
        var factor = Math.Min(1.0, maxLongEdge / (double)Math.Max(width, height));
        return (EvenAtLeast2((int)Math.Round(width * factor)), EvenAtLeast2((int)Math.Round(height * factor)));
    }

    private static int EvenAtLeast2(int n)
    {
        return n < 2 ? 2 : n - (n % 2);
    }

    /// <summary>
    /// Transcodes to a widely-compatible H.264 High / AAC mp4 with faststart. The caps come from the
    /// caller (the item's conversion profile picks them), so an admin's change in Settings -> Video
    /// applies to the next video without a restart, and a full-size upload can lift them entirely.
    /// -pix_fmt yuv420p forces 8-bit 4:2:0 (browsers can't decode the yuv420p10le a 10-bit/HDR source
    /// would otherwise produce).
    /// </summary>
    /// <summary>
    /// Encodes the H.264 file. On the GPU when the admin asked for it and this machine proved at boot that
    /// it can, and if that encode fails for any reason at all, again on the CPU before giving up.
    ///
    /// That retry is the point of the whole hardware path. Render devices are a minefield of drivers,
    /// permissions and container passthrough, and a video that cannot be encoded is a video nobody can
    /// watch. Falling back means the worst case of turning hardware on is exactly the behaviour of leaving
    /// it off, one wasted attempt later.
    /// </summary>
    public async Task<bool> TranscodeWebAsync(string input, string output, VideoSettings settings,
        EncoderCapabilities caps, bool sourceIsHdr, CancellationToken ct = default)
    {
        var onGpu = settings.Normalized().Encoder == VideoEncoder.Hardware && caps.CanEncodeOnGpu;
        if (onGpu)
        {
            var hw = ScratchPath(output);
            var (hwCode, _, hwErr) = await RunAsync(FfmpegPath,
                TranscodeArgs(input, hw, settings, caps, sourceIsHdr), TranscodeTimeout, ct);
            if (Finalize(hwCode, hw, output))
            {
                return true;
            }

            logger.LogWarning("Hardware encode failed for {Path}, falling back to the CPU: {Err}", input, Tail(hwErr));
        }

        // Software, either because that is what was asked for or because the GPU just declined.
        var software = new VideoSettings
        {
            Crf = settings.Crf, MaxLongEdge = settings.MaxLongEdge, Preset = settings.Preset,
            MaxrateKbps = settings.MaxrateKbps, DefaultProfile = settings.DefaultProfile,
            Encoder = VideoEncoder.Software
        };

        var tmp = ScratchPath(output);
        var (code, _, err) = await RunAsync(FfmpegPath,
            TranscodeArgs(input, tmp, software, caps, sourceIsHdr), TranscodeTimeout, ct);
        if (code != 0)
        {
            logger.LogError("Transcode failed for {Path}: {Err}", input, Tail(err));
        }

        return Finalize(code, tmp, output);
    }

    /// <summary>
    /// Builds the ffmpeg argument line for a web transcode. Static and pure so that the settings actually
    /// reaching the encoder is testable, and so a bad preset can never smuggle in extra arguments.
    /// Normalizes again as belt and braces: a command line is never built from unclamped input.
    ///
    /// The filter chain is built in the order the pixels travel: fix the colour, then the size, then hand
    /// it to whoever is doing the encoding.
    /// </summary>
    public static string TranscodeArgs(string input, string output, VideoSettings settings,
        EncoderCapabilities caps, bool sourceIsHdr)
    {
        var v = settings.Normalized();
        var onGpu = v.Encoder == VideoEncoder.Hardware && caps.CanEncodeOnGpu;

        var filters = new List<string>();

        // ── Colour ────────────────────────────────────────────────────────────────────────────────────
        // An HDR source is 10-bit, BT.2020, PQ. The output is 8-bit SDR, and ffmpeg carries the source's
        // colour tags across regardless, so without help the file claims PQ while holding SDR pixels and a
        // colour-managed browser applies an HDR curve to them and renders the video wrong.
        //
        // Given zscale we can do the real thing: convert to linear light, map the highlights down with a
        // proper curve, and land in BT.709. Without it (a stock Homebrew ffmpeg has no zscale; the runtime
        // image does) we can at least stop lying, and label the output as the SDR it is.
        //
        // Only for genuinely HDR sources - ProbeResult.IsHdr asks about the transfer curve, not the bit
        // depth, because tone-mapping ordinary 10-bit SDR footage would crush it for no reason.
        if (sourceIsHdr && caps.CanToneMap)
        {
            filters.Add("zscale=t=linear:npl=100");
            filters.Add("format=gbrpf32le");
            filters.Add("zscale=p=bt709");
            filters.Add("tonemap=tonemap=hable:desat=0");
            filters.Add("zscale=t=bt709:m=bt709:r=tv");
            filters.Add("format=yuv420p");
        }

        // ── Size ──────────────────────────────────────────────────────────────────────────────────────
        // trunc(../2)*2 rounds odd dimensions down to even (H.264 requires even w/h).
        filters.Add(v.MaxLongEdge > 0
            ? $"scale='min({v.MaxLongEdge},iw)':'min({v.MaxLongEdge},ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2");

        // Say what the output is. Note the obvious `-color_primaries bt709 -color_trc bt709 -colorspace
        // bt709` OUTPUT options do not do this: ffmpeg accepts them and writes the source's tags anyway.
        // Verified against the real binary. Don't "simplify" this back.
        filters.Add("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709");

        // ── Encoder ───────────────────────────────────────────────────────────────────────────────────
        if (!onGpu)
        {
            var rate = v.MaxrateKbps > 0 ? $"-maxrate {v.MaxrateKbps}k -bufsize {v.MaxrateKbps * 2}k " : "";
            return
                $"-nostdin -hide_banner -nostats -loglevel error -y -i \"{input}\" -map 0:v:0 -map 0:a:0? "
                + $"-vf \"{string.Join(',', filters)}\" "
                + $"-c:v libx264 -preset {v.Preset} -crf {v.Crf} {rate}-profile:v high -pix_fmt yuv420p "
                + $"-c:a aac -b:a 160k -ac 2 -movflags +faststart -f mp4 \"{output}\"";
        }

        // The GPU wants its frames in its own memory, in a format it understands, so the chain ends by
        // converting to nv12 and uploading. Everything above still runs on the CPU, which is fine: the
        // encode is the expensive part, and this keeps one filter chain rather than two.
        filters.Add("format=nv12");
        filters.Add("hwupload");

        // VAAPI has no CRF. Constant-QP is the closest thing to "aim for a quality, not a bitrate", and the
        // same number means something different here than it does to x264 - the settings page says so
        // rather than pretending the two are comparable. No -maxrate: it is ignored under CQP.
        return
            $"-nostdin -hide_banner -nostats -loglevel error -y -vaapi_device {caps.VaapiDevice} "
            + $"-i \"{input}\" -map 0:v:0 -map 0:a:0? "
            + $"-vf \"{string.Join(',', filters)}\" "
            + $"-c:v h264_vaapi -rc_mode CQP -qp {v.Crf} -profile:v high "
            + $"-c:a aac -b:a 160k -ac 2 -movflags +faststart -f mp4 \"{output}\"";
    }

    /// <summary>Remux (no re-encode) into a real mp4 container with the moov atom up front. Takes the
    /// first video + first audio track only (drops timecode/data tracks that can break the mux).</summary>
    public async Task<bool> RemuxFastStartAsync(string input, string output, string? videoCodec = null, CancellationToken ct = default)
    {
        var tmp = ScratchPath(output);
        // HEVC in mp4 must carry the hvc1 tag or Safari/QuickTime refuse it (some encoders write hev1).
        var tag = videoCodec == "hevc" ? "-tag:v hvc1 " : "";
        var (code, _, err) = await RunAsync(FfmpegPath,
            $"-nostdin -hide_banner -nostats -loglevel error -y -i \"{input}\" -map 0:v:0 -map 0:a:0? -c copy {tag}-movflags +faststart -f mp4 \"{tmp}\"",
            TranscodeTimeout, ct);
        if (code != 0)
        {
            logger.LogWarning("Faststart remux failed for {Path}: {Err}", input, Tail(err));
        }

        return Finalize(code, tmp, output);
    }

    /// <summary>
    /// Re-probes a freshly produced file and confirms it is actually servable: an mp4 with its moov atom
    /// up front, in one of <paramref name="allowedCodecs"/>, whose duration matches the source (ffmpeg can
    /// exit 0 while writing a truncated file). Guards against shipping a file that "plays but stalls".
    ///
    /// The codec set is the caller's, and that is what makes the heal work: the same check that stops a new
    /// upload being stream-copied to H.265 also REJECTS an H.265 file produced by an older build, so a
    /// legacy item's existing blob is not silently reused. Null accepts any codec (the as-uploaded lane,
    /// where the uploader owns compatibility and we only guarantee it streams). Returns the probe of the
    /// accepted file, so a caller that needs to describe it precisely doesn't probe it twice.
    /// </summary>
    public async Task<ProbeResult?> ValidateWebOutputAsync(string path, double? sourceDuration,
        IReadOnlyCollection<string>? allowedCodecs, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        var probe = await ProbeAsync(path, ct);
        if (probe?.VideoCodec is null || !IsFastStartMp4(path)
                                      || (allowedCodecs is not null && !allowedCodecs.Contains(probe.VideoCodec)))
        {
            return null;
        }

        // Compare the produced video-track length against the source video track (not container
        // duration - a dropped commentary/data track can inflate that). Generous tolerance: a real
        // mid-encode truncation loses far more than this.
        var actual = probe.VideoDuration ?? probe.Duration;
        if (sourceDuration is > 1 && actual is { } dur)
        {
            var tolerance = Math.Max(1.0, sourceDuration.Value * 0.04);
            if (dur < sourceDuration.Value - tolerance)
            {
                logger.LogWarning("Web output {Path} is {Actual:0.0}s vs source {Source:0.0}s - truncated",
                    path, dur, sourceDuration.Value);
                return null;
            }
        }

        return probe;
    }

    /// <summary>
    /// True only if an MP4/MOV has its <c>moov</c> atom before <c>mdat</c> (progressive-play ready).
    /// Reads only the top-level box headers and fails to <c>false</c> on any parse uncertainty: the
    /// produced web file is validated anyway and a faststart remux is a cheap, lossless stream-copy,
    /// so "remux when unsure" is strictly safer than risking a moov-at-end file that stalls on iOS.
    /// </summary>
    public static bool IsFastStartMp4(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var len = fs.Length;
            var hdr = new byte[16];
            long pos = 0;
            for (var i = 0; i < 64 && pos + 8 <= len; i++)
            {
                fs.Position = pos;
                if (fs.Read(hdr, 0, 8) < 8)
                {
                    return false;
                }

                var size = ((ulong)hdr[0] << 24) | ((ulong)hdr[1] << 16) | ((ulong)hdr[2] << 8) | hdr[3];
                var type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
                var headerLen = 8;
                if (size == 1)
                {
                    if (fs.Read(hdr, 8, 8) < 8)
                    {
                        return false;
                    }

                    size = 0;
                    for (var b = 8; b < 16; b++)
                    {
                        size = (size << 8) | hdr[b];
                    }

                    headerLen = 16;
                }
                else if (size == 0)
                {
                    // Box extends to EOF - nothing after it, so moov never came first.
                    return false;
                }

                if (type == "moov")
                {
                    return true;
                }

                if (type == "mdat")
                {
                    return false;
                }

                if (size < (ulong)headerLen)
                {
                    return false;
                }

                pos += (long)size;
            }
        }
        catch
        {
            // ignore - fall through to the safe (remux) default
        }

        return false;
    }

    // 8-bit 4:2:0 is the only H.264 pixel format browsers/iOS can decode. 10-bit (High 10 /
    // yuv420p10le) and 4:2:2 / 4:4:4 must be transcoded, never stream-copied - a copy keeps the
    // undecodable pixel format and ships a black screen.
    private static bool Is8BitYuv420(string? pixFmt)
    {
        return pixFmt is "yuv420p" or "yuvj420p";
    }

    // HEVC hardware decodes Main 10 wherever it decodes Main, so the H.265 rendition takes 10-bit 4:2:0
    // as well and HDR phone footage stays HDR. 4:2:2 / 4:4:4 is neither.
    private static bool IsHqPixFmt(string? pixFmt)
    {
        return Is8BitYuv420(pixFmt) || pixFmt is "yuv420p10le";
    }

    private static bool AudioIsCopyable(string? audioCodec)
    {
        return string.IsNullOrEmpty(audioCodec) || CopyableAudioCodecs.Contains(audioCodec);
    }

    /// <summary>Codecs + pixel format an mp4 can hold AND every browser on every OS can decode with no
    /// hardware help, so a lossless stream-copy remux is enough and no re-encode is needed. This is the
    /// contract behind the file <c>/f/{slug}</c> serves, so it is deliberately the narrowest thing that
    /// always works: H.264, 8-bit 4:2:0, with AAC/MP3 or no audio.</summary>
    public static bool CanStreamCopyToMp4(string? videoCodec, string? audioCodec, string? pixFmt)
    {
        return videoCodec is not null && CopyableVideoCodecs.Contains(videoCodec)
                                      && Is8BitYuv420(pixFmt)
                                      && AudioIsCopyable(audioCodec);
    }

    /// <summary>True when the source is worth keeping whole as the better, pickier rendition offered
    /// ahead of the H.264 copy: H.265 at 8- or 10-bit 4:2:0. Nothing is risked by offering it, because a
    /// browser that can't decode it skips the source.</summary>
    public static bool CanKeepAsHq(string? videoCodec, string? audioCodec, string? pixFmt)
    {
        return videoCodec is not null && HqVideoCodecs.Contains(videoCodec)
                                      && IsHqPixFmt(pixFmt)
                                      && AudioIsCopyable(audioCodec);
    }

    /// <summary>True when the original file, as-is, is a browser-universal H.264 mp4-family file.</summary>
    public static bool IsWebPlayable(string extension, string? videoCodec, string? audioCodec, string? pixFmt)
    {
        return Mp4FamilyContainers.Contains(extension.ToLowerInvariant())
               && CanStreamCopyToMp4(videoCodec, audioCodec, pixFmt);
    }

    /// <summary>
    /// The exact RFC 6381 codecs parameter for an H.265 rendition, e.g. <c>hvc1.2.4.L120.B0</c>, or null
    /// when we cannot state it honestly - in which case the source simply isn't offered.
    ///
    /// Precision is the whole point: a browser only SKIPS a source it knows it can't decode, and it can
    /// only know that if we tell it exactly what the source is. A bare "video/mp4" makes it accept the
    /// file, download it, and only then discover it has no decoder, which is a dead end (the HTML spec
    /// aborts resource selection on a decode error, so it never falls through to the next source).
    ///
    /// Must be built from a probe of the file we are actually going to SERVE, never the upload: an
    /// hev1-tagged source becomes hvc1 in the remux, and claiming hvc1 for a file that is still hev1
    /// would sail past Safari's type check and then fail to render.
    ///
    /// The tier is always reported as Main (B0). ffprobe does not expose general_tier_flag, and getting
    /// it wrong can only make a decoder accept a High-tier file it then struggles with - which the share
    /// page's decode-error fallback catches - whereas guessing the PROFILE wrong would be unrecoverable.
    /// </summary>
    public static string? HevcCodecs(ProbeResult probe)
    {
        // hev1 in an mp4 is refused outright by Safari/QuickTime, so only the hvc1 tag is ever advertised.
        if (probe.VideoCodec != "hevc" || probe.CodecTag != "hvc1" || probe.Level is not > 0)
        {
            return null;
        }

        // profile_idc plus the compatibility-flags word browsers expect alongside it. Main and Main 10 are
        // the only profiles we ever copy; anything else (Rext, 4:4:4) we can't describe, so we don't offer.
        var (idc, compatibility) = probe.Profile?.Trim().ToLowerInvariant() switch
        {
            "main" => (1, 6),
            "main 10" => (2, 4),
            _ => (0, 0)
        };

        return idc == 0 ? null : $"hvc1.{idc}.{compatibility}.L{probe.Level}.B0";
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        // Bound the run: link the caller's token (app shutdown) with a per-operation timeout so a
        // hung or pathologically slow ffmpeg can't block the single-consumer queue forever.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
        {
            linked.CancelAfter(timeout);
        }

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Don't leave an orphan ffmpeg/ffprobe running after cancellation/shutdown/timeout.
            try
            {
                proc.Kill(true);
            }
            catch
            {
                /* already gone */
            }

            if (ct.IsCancellationRequested)
            {
                throw; // genuine app shutdown - propagate
            }

            // Timeout: reap the killed process, then report failure so the item goes Failed and the
            // queue keeps moving.
            timedOut = true;
            try
            {
                await proc.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                /* ignore */
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (timedOut)
        {
            logger.LogError("{Exe} timed out after {Seconds}s and was killed: {Args}",
                exe, timeout.TotalSeconds, args);
            return (-1, stdout, stderr);
        }

        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>A unique scratch path next to <paramref name="output"/> (same directory, so the final
    /// move is atomic). The <c>tmp_</c> prefix keeps it out of the content-file namespace - those are
    /// always named by a hex hash - so the periodic root cleanup can sweep abandoned scratch by prefix
    /// without ever touching a real stored file. The GUID also avoids two same-content jobs colliding.</summary>
    private static string ScratchPath(string output)
    {
        return Path.Combine(Path.GetDirectoryName(output) ?? string.Empty,
            "tmp_" + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
    }

    /// <summary>Promote a temp output to its final path on success; otherwise discard it - so a
    /// failed or killed ffmpeg never leaves a partial file at the path we'd serve.</summary>
    private static bool Finalize(int code, string tmp, string output)
    {
        try
        {
            if (code == 0 && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                File.Move(tmp, output, true);
                return true;
            }
        }
        catch
        {
            // fall through to cleanup
        }

        try
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static string Tail(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s[Math.Max(0, s.Length - 500)..];
    }
}

public record ProbeResult(
    int? Width,
    int? Height,
    double? Duration,
    string? VideoCodec,
    string? AudioCodec,
    string? PixFmt = null,
    double? VideoDuration = null,
    DateTime? CreationTimeUtc = null, // format.tags.creation_time (video capture date)
    int? Rotation = null, // 0/90/180/270 display rotation
    string? Profile = null, // codec profile, e.g. "Main", "Main 10", "High"
    int? Level = null, // general_level_idc, e.g. 120 for level 4.0
    string? CodecTag = null, // container tag, e.g. "hvc1", "hev1", "avc1"
    string? ColorTransfer = null) // transfer curve: "smpte2084" (PQ) and "arib-std-b67" (HLG) are HDR
{
    /// <summary>
    /// True when this is genuinely high dynamic range, which is a question about the TRANSFER CURVE and
    /// nothing else. Deliberately not "is it 10-bit": plenty of 10-bit video is ordinary SDR, and running
    /// a tone-map over that would crush contrast on footage that was perfectly fine.
    /// </summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}
