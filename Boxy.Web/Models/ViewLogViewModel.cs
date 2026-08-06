using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>A share's view timeline: when each counted view happened, newest first.</summary>
public class ViewLogViewModel
{
    public required MediaItem Item { get; init; }

    /// <summary>View moments (UTC), newest first, capped at the latest <see cref="Cap"/>.</summary>
    public required IReadOnlyList<DateTime> Views { get; init; }

    public const int Cap = 1000;
}
