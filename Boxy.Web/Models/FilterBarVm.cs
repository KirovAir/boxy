using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>The type-filter chip strip for one media list. <see cref="KindKey"/>/<see cref="PageKey"/> are
/// that list's query-string prefixes (e.g. "vt"/"vp") so a chip preserves the other list's state and
/// resets its own page to 1.</summary>
public sealed record FilterBarVm(
    MediaFilter Active,
    IReadOnlyDictionary<MediaKind, int> Counts,
    string KindKey,
    string PageKey);

/// <summary>One entry in a sort menu: the query-string value and its display label. Defined once per
/// surface in <c>MediaSort</c>, so the controller allow-list and the view links never diverge.</summary>
public sealed record SortOption(string Key, string Label);

/// <summary>The sort links for one media list, extracted from the per-list markup so filter+sort share one
/// header row and no surface duplicates the sortbar.</summary>
public sealed record SortBarVm(
    IReadOnlyList<SortOption> Options,
    string Active,
    string SortKey,
    string PageKey);
