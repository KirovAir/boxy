namespace Boxy.Web.Models;

/// <summary>The shared admin page header: an optional back link, a brand-marked icon, the h1 title,
/// and any status stamps. One shape so every admin screen opens at the same altitude.</summary>
public class PageHeader
{
    public required string Title { get; init; }
    public required PageIcon Icon { get; init; }

    /// <summary>Show the "← Back to dashboard" link above the title (the detail pages).</summary>
    public bool Back { get; init; }

    /// <summary>Status badges rendered inline after the title, as (text, stamp modifier class) pairs.</summary>
    public IReadOnlyList<(string Text, string Css)> Stamps { get; init; } = [];

    /// <summary>Optional "created ..." timestamp shown after the stamps.</summary>
    public DateTime? Created { get; init; }
}

/// <summary>The line-icon beside an admin page title, kept in one set so the pages stay consistent.</summary>
public enum PageIcon
{
    Dashboard,
    Box,
    Share,
    File,
    Settings
}
