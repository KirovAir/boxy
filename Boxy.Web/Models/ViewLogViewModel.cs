using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>A share's view timeline: when each logged view happened, newest first.</summary>
public class ViewLogViewModel
{
    public required MediaItem Item { get; init; }

    /// <summary>Logged views (UTC), newest first, capped at the latest <see cref="Cap"/>.</summary>
    public required IReadOnlyList<ViewLogRow> Views { get; init; }

    public const int Cap = 1000;
}

/// <summary>One logged view: the moment and where it came from.</summary>
public record ViewLogRow(DateTime At, string? Ip, string? Country);
