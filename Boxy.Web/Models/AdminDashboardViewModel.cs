using Boxy.Data.Entities;

namespace Boxy.Web.Models;

public class AdminDashboardViewModel
{
    public required IReadOnlyList<Bucket> Buckets { get; init; }
    public required IReadOnlyDictionary<int, int> BucketCounts { get; init; }

    /// <summary>Admin-uploaded videos - the public shares (with view counts).</summary>
    public required Page<MediaItem> Videos { get; init; }

    /// <summary>Files dropped into buckets by others - for the admin to download/process, not shared.</summary>
    public required Page<MediaItem> Files { get; init; }

    // Active type filter + per-type counts for each list's chip strip (empty filter = "All").
    public MediaFilter VideosFilter { get; init; } = MediaFilter.None;
    public MediaFilter FilesFilter { get; init; } = MediaFilter.None;
    public IReadOnlyDictionary<MediaKind, int> VideoKindCounts { get; init; } = new Dictionary<MediaKind, int>();
    public IReadOnlyDictionary<MediaKind, int> FileKindCounts { get; init; } = new Dictionary<MediaKind, int>();

    public required string BaseUrl { get; init; }

    /// <summary>The signed-in owner's namespace, used to build each share's pretty URL: an admin
    /// links to <c>/s/{slug}</c>, a regular user to <c>/s/{username}/{slug}</c>.</summary>
    public required string? OwnerUsername { get; init; }

    public required bool OwnerIsAdmin { get; init; }

    /// <summary>Upload size cap for this user in bytes (0 = unlimited), for the uploader's pre-check.</summary>
    public required long MaxUploadBytes { get; init; }

    /// <summary>This user's total storage quota in bytes (0 = unlimited; not shown when unlimited).</summary>
    public required long QuotaBytes { get; init; }

    /// <summary>How much of the quota this user currently occupies (only computed when capped).</summary>
    public required long UsageBytes { get; init; }
}
