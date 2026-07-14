namespace Boxy.Web.Models;

/// <summary>
/// FFmpeg deployment configuration, bound from the "Ffmpeg" section. Everything here is a deployment
/// concern and is settable only via appsettings/environment - never through the app. The executable paths
/// in particular must never become editable over HTTP: they are handed to <c>Process.Start</c>, so a
/// writable path would turn any admin-session compromise into arbitrary process execution on the host.
///
/// The video-quality knobs (quality, resolution, preset, bitrate) are NOT here: they are edited in-app and
/// persisted as <see cref="VideoSettings"/>, with built-in defaults until an admin sets them.
/// </summary>
public class FfmpegSettings
{
    public const string SectionName = "Ffmpeg";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// The VAAPI render node used for hardware encoding, when an admin turns it on in Settings -> Video.
    ///
    /// It lives HERE, and not in the in-app settings, precisely because it is a path: it is interpolated
    /// into the ffmpeg command line and handed to <c>Process.Start</c>, so making it HTTP-writable would
    /// hand any admin-session compromise a way to point the encoder at an arbitrary file. The CHOICE of
    /// hardware or software is a safe enum and is editable in-app; the device it means is not.
    /// </summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    // Per-operation ceilings so one pathological file can't wedge the single-consumer processing queue.
    public int ProbeTimeoutSeconds { get; set; } = 60;
    public int PosterTimeoutSeconds { get; set; } = 120;
    public int TranscodeTimeoutMinutes { get; set; } = 90;
}
