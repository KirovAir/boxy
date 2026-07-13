namespace Boxy.Web.Models;

/// <summary>Configures the shared chunked-uploader partial (_Uploader). One uploader per page -
/// upload.js binds to #uploadForm / #queue / #readout, so a page renders this at most once.</summary>
public class UploaderVm
{
    /// <summary>No-JS multipart fallback target (upload.js takes over when scripting is on).</summary>
    public required string Action { get; init; }

    public required string ChunkUrl { get; init; }
    public required string CompleteUrl { get; init; }

    /// <summary>Reload the page once every upload finishes (dashboard + replace show the new state).</summary>
    public bool Reload { get; init; }

    public bool Multiple { get; init; }
    public string InputName { get; init; } = "files";
    public string Title { get; init; } = "Drop files here";
    public string Hint { get; init; } = "or click to browse. Any size";
    public string ButtonText { get; init; } = "Upload";

    /// <summary>Show the "keep original (don't re-encode)" opt-out (new share uploads only).</summary>
    public bool KeepOriginalOption { get; init; }

    /// <summary>Authenticated forms carry the antiforgery token; the anonymous drop-off form doesn't.</summary>
    public bool Antiforgery { get; init; } = true;

    /// <summary>Base URL for per-file delete on the public drop-off page; null elsewhere.</summary>
    public string? DeleteBase { get; init; }

    /// <summary>Upload size cap in bytes for the pre-check (0 = unlimited).</summary>
    public long MaxBytes { get; init; }
}
