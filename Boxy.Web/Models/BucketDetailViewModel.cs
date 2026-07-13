using Boxy.Data.Entities;

namespace Boxy.Web.Models;

public class BucketDetailViewModel
{
    public required Bucket Bucket { get; init; }
    public required Page<MediaItem> Files { get; init; }
    public required string BaseUrl { get; init; }

    // Active type filter + per-type counts for this box's chip strip (empty filter = "All").
    public MediaFilter Filter { get; init; } = MediaFilter.None;
    public IReadOnlyDictionary<MediaKind, int> KindCounts { get; init; } = new Dictionary<MediaKind, int>();

    /// <summary>The uploader the list is narrowed to, when the owner clicked one visitor's chip; null
    /// otherwise. Derived from the resolved token, so it carries the name/colour for the "showing X"
    /// banner. Its presence (with <see cref="Filter"/>) is what makes the list "filtered".</summary>
    public UploaderIdentity? ActiveUploader { get; init; }

    /// <summary>The owner's current email (from the database, not the possibly-stale auth cookie), used
    /// to show and gate the "email me on drop-offs" toggle. This is where the worker actually sends.</summary>
    public string? OwnerEmail { get; init; }
}
