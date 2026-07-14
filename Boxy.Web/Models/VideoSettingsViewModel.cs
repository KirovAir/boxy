using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>The admin video-settings form. <see cref="FromDb"/> tells the admin whether these came from
/// in-app config or the environment fallback - same convention as <see cref="EmailSettingsViewModel"/>.</summary>
public class VideoSettingsViewModel
{
    public required int Crf { get; init; }
    public required int MaxLongEdge { get; init; }
    public required string Preset { get; init; }
    public required int MaxrateKbps { get; init; }

    /// <summary>What an uploaded video becomes when neither the uploader nor the box says otherwise.</summary>
    public required ConversionProfile DefaultProfile { get; init; }

    public required VideoEncoder Encoder { get; init; }

    /// <summary>Whether this server actually managed to encode a frame on its GPU at startup. When false the
    /// hardware option is shown disabled with the reason, rather than silently doing nothing when picked.</summary>
    public required bool GpuAvailable { get; init; }

    public string? GpuUnavailableReason { get; init; }

    /// <summary>Whether this ffmpeg can tone-map HDR, so the page can say when an HDR upload's fallback
    /// copy will only be re-labelled rather than properly converted.</summary>
    public required bool CanToneMap { get; init; }

    public required bool FromDb { get; init; }
}
