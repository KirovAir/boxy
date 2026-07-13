using Boxy.Data.Entities;
using Boxy.Web.Models;

namespace Boxy.Web.Extensions;

/// <summary>Filtering that composes with the existing sort/paging: one place applies a <see cref="MediaFilter"/>
/// to a media query, and one place counts what each type chip would yield. Both filter on the materialized
/// <see cref="MediaItem.Kind"/> column, so they are plain indexed SQL predicates.</summary>
public static class MediaQueryExtensions
{
    public static IQueryable<MediaItem> WhereFacets(this IQueryable<MediaItem> query, MediaFilter filter)
    {
        if (filter.Kind is { } kind)
        {
            query = query.Where(m => m.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(m => m.Status == status);
        }

        return query;
    }

    /// <summary>
    /// How many items sit behind each type chip, against the list's own scope with the OTHER facets applied
    /// but the KIND facet dropped - so a chip reads "what you'd get", not "0" once a different kind is
    /// selected. One GROUP BY on the stored Kind column answers it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<MediaKind, int>> KindCountsAsync(
        this IQueryable<MediaItem> source, MediaFilter filter, CancellationToken ct)
    {
        var rows = await source.WhereFacets(filter with { Kind = null })
            .GroupBy(m => m.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Kind, x => x.Count);
    }

    /// <summary>
    /// One page of a media list: filter, then sort, then page. The filter is applied before counting so
    /// the pager, the page auto-clamp, and the "N of M" line all reflect the filtered set. Reusable by any
    /// controller that lists media - shares, drop-offs, a single box.
    /// </summary>
    public static async Task<Page<MediaItem>> ToPageAsync(
        this IQueryable<MediaItem> baseQuery, MediaFilter filter, int number, int size, string sort, CancellationToken ct)
    {
        var q = baseQuery.WhereFacets(filter);
        var total = await q.CountAsync(ct);
        var pages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)size);
        number = Math.Clamp(number, 1, pages);
        var items = await q.SortBy(sort).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<MediaItem> { Items = items, Number = number, Size = size, Total = total, Sort = sort };
    }
}
