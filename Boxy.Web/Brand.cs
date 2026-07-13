namespace Boxy.Web;

/// <summary>Brand constants and the shared page-title format ("Page - Boxy").</summary>
public static class Brand
{
    public const string Name = "Boxy";

    /// <summary>Formats a document title as "<paramref name="page"/> - Boxy", or just "Boxy" when
    /// the page name is empty or already the brand.</summary>
    public static string Title(string? page)
    {
        return string.IsNullOrWhiteSpace(page) || string.Equals(page, Name, StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{page} - {Name}";
    }
}
