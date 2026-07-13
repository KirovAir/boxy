using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>
/// How one media list is narrowed. Every facet is optional; "not set" means "all", so an unfiltered list
/// adds no predicate. Parsing is total - anything unrecognised is dropped - so a hand-edited URL can never
/// throw or leak rows. Facets travel in the query string under a per-list prefix ("v" for shares, "f" for
/// drop-offs, "" for a single box), matching the existing page/sort keys.
/// </summary>
public sealed record MediaFilter(MediaKind? Kind, MediaStatus? Status)
{
    public static readonly MediaFilter None = new(null, null);

    public bool IsEmpty => Kind is null && Status is null;

    /// <summary>Read this list's facets out of the query string under its prefix (e.g. "vt"/"vst").</summary>
    public static MediaFilter From(IQueryCollection query, string prefix)
    {
        return new MediaFilter(
            Enum.TryParse<MediaKind>(query[prefix + "t"].ToString(), true, out var k) ? k : null,
            Enum.TryParse<MediaStatus>(query[prefix + "st"].ToString(), true, out var s) ? s : null);
    }
}
