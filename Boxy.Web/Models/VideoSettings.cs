using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>
/// Who does the encoding. This is a CHOICE, not a path, which is why it is safe to have in the in-app
/// settings at all: it selects between two code-defined command lines. The render device it implies is a
/// path, and that stays in <see cref="FfmpegSettings"/>, out of reach of HTTP.
/// </summary>
public enum VideoEncoder
{
    /// <summary>libx264 on the CPU. The best quality per byte, and slow: roughly real time for 1080p.</summary>
    Software,

    /// <summary>The GPU, via VAAPI. Ten to thirty times faster, and needs more bits for the same picture.
    /// Falls back to software whenever the machine turns out not to have a usable render device, and
    /// whenever a hardware encode fails, so choosing it can never be worse than not choosing it.</summary>
    Hardware
}

/// <summary>
/// The global ceiling on what conversion may do - the one lever that lets a light server cap or switch off
/// the single most expensive thing Boxy does. A per-upload or per-box profile is still honoured, but never
/// beyond this: <see cref="ConversionProfiles.UnderMode"/> clamps it, and the worker persists the result.
/// </summary>
public enum ConversionMode
{
    /// <summary>Full conversions: transcode to H.264 as each profile asks. The default.</summary>
    Full,

    /// <summary>Remux only: never re-encode. Stream-copies an upload into a faststart mp4 where the container
    /// needs it (a .mov into an .mp4), codec untouched - cheap, no CPU. Caps heavier profiles down to this,
    /// so an H.265 phone clip stays H.265 and only Safari plays it inline.</summary>
    Remux,

    /// <summary>Off: store and serve originals exactly as uploaded - no transcode, no remux. Lightest; a
    /// viewer whose browser lacks the codec gets nothing to play.</summary>
    Off
}

/// <summary>
/// The video-encoding knobs an admin controls in-app, stored as one JSON row in the <c>Config</c> table
/// (see <c>ConfigExtensions</c>). Applies to videos transcoded from now on - anything already encoded
/// keeps the rendition it has.
///
/// This type deliberately holds NO executable paths and NO timeouts: it is written from an HTTP form, so
/// anything in it is reachable by anyone holding an admin session. Paths stay in <see cref="FfmpegSettings"/>
/// (environment only) because they are passed to <c>Process.Start</c>. Keep it that way.
/// </summary>
public class VideoSettings
{
    /// <summary>x264 constant-rate factor: lower = better quality and bigger files. Clamped to 14-35
    /// (0 would be lossless: files many times larger than the source, on top of the kept original).</summary>
    public int Crf { get; set; } = 18;

    /// <summary>Cap on the longer edge, in pixels. 0 disables the cap. Keeps the progressive stream
    /// playable and the encoded H.264 level within old-decoder limits.</summary>
    public int MaxLongEdge { get; set; } = 1920;

    /// <summary>x264 speed/compression preset. Allowlisted - it is interpolated into the ffmpeg
    /// argument line, so an arbitrary string here would be argument injection.</summary>
    public string Preset { get; set; } = "slow";

    /// <summary>Bitrate ceiling in kbps so a high-motion clip can't spike past what cellular can stream.
    /// 0 disables the ceiling (pure CRF).</summary>
    public int MaxrateKbps { get; set; } = 16000;

    /// <summary>What happens to an uploaded video when neither the uploader nor the box says otherwise.
    /// Unlike the knobs above this one is not just a quality dial: it decides how many files a video
    /// becomes, so it is the setting that sets this server's disk and CPU bill.</summary>
    public ConversionProfile DefaultProfile { get; set; } = ConversionProfiles.Fallback;

    /// <summary>Whether to encode on the CPU or the GPU. Ignored on a machine with no usable render
    /// device: there is nothing to fall back FROM, so it simply runs in software.</summary>
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Software;

    /// <summary>The global ceiling on conversion work (see <see cref="ConversionMode"/>). Full by default;
    /// Remux and Off cap every upload's profile down, whatever the box or the uploader asked for.</summary>
    public ConversionMode ConversionMode { get; set; } = ConversionMode.Full;

    /// <summary>Whether to generate poster frames and image thumbnails at all. On by default, and independent
    /// of <see cref="ConversionMode"/>: a light server can keep cheap posters while switching transcoding
    /// off, or drop posters too to avoid every ffmpeg call.</summary>
    public bool GeneratePosters { get; set; } = true;

    /// <summary>The x264 presets we accept, fastest-to-slowest. "placebo" is excluded on purpose.</summary>
    public static readonly string[] AllowedPresets =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"
    ];

    /// <summary>
    /// A safe copy: numbers clamped to sane ranges, preset forced onto the allowlist. Applied on BOTH save
    /// and read, so a hand-edited DB row or a typo'd environment variable can neither inject ffmpeg
    /// arguments nor produce a lossless, disk-filling encode.
    /// </summary>
    public VideoSettings Normalized()
    {
        var preset = Preset?.Trim().ToLowerInvariant();
        return new VideoSettings
        {
            Crf = Math.Clamp(Crf, 14, 35),
            MaxLongEdge = MaxLongEdge <= 0 ? 0 : Math.Clamp(MaxLongEdge, 480, 4320),
            MaxrateKbps = MaxrateKbps <= 0 ? 0 : Math.Clamp(MaxrateKbps, 500, 100_000),
            Preset = preset is not null && AllowedPresets.Contains(preset) ? preset : "slow",
            // A JSON blob is easy to hand-edit and a form is easy to forge: an undefined enum member would
            // otherwise fall through every switch in the worker and quietly transcode nothing.
            DefaultProfile = Enum.IsDefined(DefaultProfile) ? DefaultProfile : ConversionProfiles.Fallback,
            Encoder = Enum.IsDefined(Encoder) ? Encoder : VideoEncoder.Software,
            ConversionMode = Enum.IsDefined(ConversionMode) ? ConversionMode : ConversionMode.Full,
            GeneratePosters = GeneratePosters
        };
    }
}
