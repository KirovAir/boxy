using Boxy.Data.Entities;

namespace Boxy.Web.Models;

public class UploadPageViewModel
{
    public required string BucketName { get; init; }
    public required string BucketSlug { get; init; }
    public bool IsOpen { get; init; }
    public int UploadedCount { get; init; }

    /// <summary>The current visitor's own uploads to this bucket (by their boxy_uid cookie).</summary>
    public required IReadOnlyList<MediaItem> MyUploads { get; init; }

    /// <summary>Upload size cap for this box in bytes (0 = unlimited), for the uploader's pre-check.</summary>
    public long MaxBytes { get; init; }
}
