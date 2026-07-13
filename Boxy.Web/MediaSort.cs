using Boxy.Data.Entities;
using Boxy.Web.Models;

namespace Boxy.Web;

/// <summary>
/// One source of truth for how any media list is ordered. Each sort key maps to a single ordering (defined
/// once, always Id-tiebroken so paging is stable), and each surface declares the menu it exposes as an
/// ordered list of <see cref="SortOption"/> - so the controller's allow-list and the view's sort links read
/// from the same place instead of drifting. Reused by shares, drop-offs, and a single box's file list.
/// </summary>
public static class MediaSort
{
    // Every ordering ends with .ThenBy(Id): without a unique tiebreak, ties on a non-unique key
    // (CreatedDate/Title/SizeBytes/Kind) make SQLite return rows in an unstable order across Skip/Take,
    // silently dropping or duplicating rows between pages.
    private static readonly Dictionary<string, Func<IQueryable<MediaItem>, IOrderedQueryable<MediaItem>>> Orderings =
        new()
        {
            ["new"] = q => q.OrderByDescending(m => m.CreatedDate).ThenBy(m => m.Id),
            ["old"] = q => q.OrderBy(m => m.CreatedDate).ThenBy(m => m.Id),
            ["name"] = q => q.OrderBy(m => m.OriginalFileName).ThenBy(m => m.Id),
            ["title"] = q => q.OrderBy(m => m.Title).ThenBy(m => m.Id),
            ["size"] = q => q.OrderByDescending(m => m.SizeBytes).ThenBy(m => m.Id),
            ["views"] = q => q.OrderByDescending(m => m.Views).ThenBy(m => m.Id),
            ["type"] = q => q.OrderBy(m => m.Kind).ThenByDescending(m => m.CreatedDate).ThenBy(m => m.Id),
            // Newest capture date first, nulls (files with no metadata date) last, then newest-uploaded.
            ["captured"] = q => q.OrderBy(m => m.CapturedAt == null).ThenByDescending(m => m.CapturedAt)
                .ThenByDescending(m => m.CreatedDate).ThenBy(m => m.Id)
        };

    /// <summary>The default sort when none/an unknown one is requested.</summary>
    public const string Default = "new";

    /// <summary>Sort menu for a user's own shares (title + view count are meaningful here).</summary>
    public static readonly IReadOnlyList<SortOption> Shares =
    [
        new("new", "Newest"), new("old", "Oldest"), new("title", "Title"),
        new("size", "Largest"), new("views", "Most viewed"), new("type", "Type"), new("captured", "Date taken")
    ];

    /// <summary>Sort menu for drop-off / box files (by original file name, no view count).</summary>
    public static readonly IReadOnlyList<SortOption> Files =
    [
        new("new", "Newest"), new("old", "Oldest"), new("name", "Name"),
        new("size", "Largest"), new("type", "Type"), new("captured", "Date taken")
    ];

    /// <summary>The keys a menu allows, for <c>Page&lt;T&gt;.Normalize</c>.</summary>
    public static IEnumerable<string> Keys(this IReadOnlyList<SortOption> options)
    {
        return options.Select(o => o.Key);
    }

    /// <summary>Apply a sort key's ordering; an unknown key falls back to <see cref="Default"/>.</summary>
    public static IQueryable<MediaItem> SortBy(this IQueryable<MediaItem> query, string sort)
    {
        return (Orderings.TryGetValue(sort, out var order) ? order : Orderings[Default])(query);
    }
}
