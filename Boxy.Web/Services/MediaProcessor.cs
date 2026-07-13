using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Boxy.Web.Models;
using Microsoft.Extensions.Options;

namespace Boxy.Web.Services;

/// <summary>
/// Thin wrapper over ffprobe/ffmpeg: inspects a file, extracts a poster frame, and produces a
/// browser-safe mp4 (H.264 always; HEVC/H.265 where hardware decoders are common) by lossless remux
/// when the source codecs already allow it, otherwise a transcode to H.264. Every invocation is
/// bounded by a timeout so one bad file can't wedge the processing queue.
/// </summary>
public class MediaProcessor(
    IOptions<FfmpegSettings> ffmpeg,
    VideoSettingsProvider videoSettings,
    ILogger<MediaProcessor> logger)
{
    // H.264 in an mp4-family container plays natively on EVERY target browser/device. HEVC/H.265 is
    // accepted too: it plays on all Apple devices and on Windows/macOS Chrome/Firefox/Edge with a
    // hardware decoder (most modern hardware), so heavy HEVC sources (phone/camera footage) are
    // stream-copied into mp4 rather than re-encoded. VP8/VP9/AV1 and .webm still transcode by default
    // (they fail on Apple except the newest hardware). Copied at 4:2:0: 8-bit for H.264, 8- or 10-bit
    // for HEVC (so HDR phone footage is preserved, not flattened); H.264 High 10 and 4:2:2/4:4:4 transcode.
    private static readonly string[] Mp4FamilyContainers = [".mp4", ".m4v", ".mov"];
    private static readonly string[] CopyableAudioCodecs = ["aac", "mp3"];
    private static readonly string[] CopyableVideoCodecs = ["h264", "hevc"];

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

        int? width = null, height = null, rotation = null;
        string? videoCodec = null, audioCodec = null, pixFmt = null;
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
            creation, rotation is null ? null : Math.Abs(rotation.Value) % 360);
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
    /// Transcodes to a widely-compatible H.264 High / AAC mp4 with faststart. Caps the longer edge
    /// (default 1080p) and bitrate so the progressive stream stays smooth on mobile and the encoded
    /// H.264 level stays within old-device limits. -pix_fmt yuv420p forces 8-bit 4:2:0 (browsers
    /// can't decode the yuv420p10le a 10-bit/HDR source would otherwise produce).
    /// </summary>
    public async Task<bool> TranscodeWebAsync(string input, string output, CancellationToken ct = default)
    {
        // Read the quality knobs here, once per transcode, so an admin's change in Settings -> Video
        // applies to the next video without a restart. Files already encoded keep the rendition they have.
        var settings = await videoSettings.GetEffectiveAsync(ct);
        var tmp = ScratchPath(output);
        var (code, _, err) = await RunAsync(FfmpegPath, TranscodeArgs(input, tmp, settings), TranscodeTimeout, ct);
        if (code != 0)
        {
            logger.LogError("Transcode failed for {Path}: {Err}", input, Tail(err));
        }

        return Finalize(code, tmp, output);
    }

    /// <summary>
    /// Builds the ffmpeg argument line for a web transcode. Static and pure so the settings actually reach
    /// the encoder (and so a bad preset can never smuggle in extra arguments) is testable. Normalizes again
    /// as belt and braces: a command line is never built from unclamped input.
    /// </summary>
    public static string TranscodeArgs(string input, string output, VideoSettings settings)
    {
        var v = settings.Normalized();
        // scale=trunc(.../2)*2 rounds odd dimensions down to even (libx264 requires even w/h).
        var scale = v.MaxLongEdge > 0
            ? $"scale='min({v.MaxLongEdge},iw)':'min({v.MaxLongEdge},ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        var rate = v.MaxrateKbps > 0 ? $"-maxrate {v.MaxrateKbps}k -bufsize {v.MaxrateKbps * 2}k " : "";
        return
            $"-nostdin -hide_banner -nostats -loglevel error -y -i \"{input}\" -map 0:v:0 -map 0:a:0? -vf \"{scale}\" " +
            $"-c:v libx264 -preset {v.Preset} -crf {v.Crf} {rate}-profile:v high -pix_fmt yuv420p " +
            $"-c:a aac -b:a 160k -ac 2 -movflags +faststart -f mp4 \"{output}\"";
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
    /// Re-probes a freshly produced web file and confirms it is actually servable: an H.264 mp4 with
    /// its moov atom up front, whose duration matches the source (ffmpeg can exit 0 while writing a
    /// truncated file). Guards against shipping a file that "plays but stalls/ends early".
    /// </summary>
    public async Task<bool> ValidateWebOutputAsync(string path, double? sourceDuration, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        var probe = await ProbeAsync(path, ct);
        // A servable web file is an 8-bit 4:2:0 H.264 or HEVC mp4 with the moov atom up front.
        if (probe?.VideoCodec is null || !CopyableVideoCodecs.Contains(probe.VideoCodec)
                                      || !IsCopyablePixFmt(probe.VideoCodec, probe.PixFmt) || !IsFastStartMp4(path))
        {
            return false;
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
                return false;
            }
        }

        return true;
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

    // Pixel formats we can stream-copy (no re-encode) per codec. H.264 only decodes 8-bit 4:2:0 in
    // browsers. HEVC hardware also decodes Main 10, so 10-bit 4:2:0 HEVC is accepted too (HDR phone
    // footage stays HDR); only H.264 High 10 and any 4:2:2 / 4:4:4 source still transcodes.
    private static bool IsCopyablePixFmt(string? videoCodec, string? pixFmt)
    {
        return videoCodec == "hevc"
            ? pixFmt is "yuv420p" or "yuvj420p" or "yuv420p10le"
            : Is8BitYuv420(pixFmt);
    }

    /// <summary>Codecs+pixel format an mp4 container can hold AND every target browser can decode, so a
    /// lossless stream-copy remux is sufficient (no re-encode needed).</summary>
    public static bool CanStreamCopyToMp4(string? videoCodec, string? audioCodec, string? pixFmt)
    {
        return videoCodec is not null && CopyableVideoCodecs.Contains(videoCodec)
                                      && IsCopyablePixFmt(videoCodec, pixFmt)
                                      && (string.IsNullOrEmpty(audioCodec) || CopyableAudioCodecs.Contains(audioCodec));
    }

    /// <summary>True when the original file, as-is, is a browser-universal H.264 mp4-family file.</summary>
    public static bool IsWebPlayable(string extension, string? videoCodec, string? audioCodec, string? pixFmt)
    {
        return Mp4FamilyContainers.Contains(extension.ToLowerInvariant())
               && CanStreamCopyToMp4(videoCodec, audioCodec, pixFmt);
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
    int? Rotation = null); // 0/90/180/270 display rotation
