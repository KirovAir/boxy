namespace Boxy.Web.Models;

/// <summary>The admin video-settings form. <see cref="FromDb"/> tells the admin whether these came from
/// in-app config or the environment fallback - same convention as <see cref="EmailSettingsViewModel"/>.</summary>
public class VideoSettingsViewModel
{
    public required int Crf { get; init; }
    public required int MaxLongEdge { get; init; }
    public required string Preset { get; init; }
    public required int MaxrateKbps { get; init; }
    public required bool FromDb { get; init; }
}
