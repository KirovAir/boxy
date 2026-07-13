namespace Boxy.Web.Models;

/// <summary>Admin statistics: instance-wide totals plus a per-user breakdown.</summary>
public class StatsViewModel
{
    // Physical footprint on the backend (every blob).
    public required long StoredBytes { get; init; }
    public required int StoredObjects { get; init; }
    public required string StorageProvider { get; init; }

    // Deduplicated source files (the original uploads, one blob per distinct content).
    public required long OriginalBytes { get; init; }
    public required int OriginalCount { get; init; }

    // Space dedup saved: the same content uploaded more than once is stored once.
    public required long DedupSavedBytes { get; init; }

    // Everything stored that isn't an original: generated posters and web renditions.
    public long GeneratedBytes => Math.Max(0, StoredBytes - OriginalBytes);
    public int GeneratedCount => Math.Max(0, StoredObjects - OriginalCount);

    public required int UserCount { get; init; }
    public required int ActiveUserCount { get; init; }
    public required int BoxCount { get; init; }
    public required int ShareCount { get; init; }
    public required int DropOffCount { get; init; }

    public required IReadOnlyList<UserUsageRow> Users { get; init; }

    /// <summary>Read-only summary of the loaded configuration (drivers, providers, media settings), so an
    /// admin can see what the instance booted with. Secrets are never included - only whether they're set.</summary>
    public required IReadOnlyList<ConfigGroup> Config { get; init; }
}

public record ConfigGroup(string Title, IReadOnlyList<ConfigItem> Items);

public record ConfigItem(string Label, string Value);

/// <summary>One user's usage: what they own and how much their uploads total (logical size).</summary>
public record UserUsageRow(
    int Id,
    string Email,
    string? Username,
    bool IsActive,
    int Shares,
    int Boxes,
    int DropOffs,
    long ContentBytes);
