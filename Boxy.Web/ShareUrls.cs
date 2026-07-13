using System.Text.RegularExpressions;
using Boxy.Data.Entities;

namespace Boxy.Web;

/// <summary>Builds the public path of a share and validates the human-chosen slug. The pretty URL
/// depends on the owner: an admin publishes to the root (<c>/s/{slug}</c>); a regular user publishes
/// under their username (<c>/s/{username}/{slug}</c>).</summary>
public static partial class ShareUrls
{
    public const int MaxSlugLength = 64;

    /// <summary>The public path for a share. Uses the custom slug when set, else the stable token;
    /// namespaces a regular user's share under their username.</summary>
    public static string Path(MediaItem item, User? owner)
    {
        return Path(item, owner?.Username, owner?.Role == UserRole.Admin);
    }

    /// <inheritdoc cref="Path(MediaItem, User?)"/>
    public static string Path(MediaItem item, string? ownerUsername, bool ownerIsAdmin)
    {
        var slug = string.IsNullOrEmpty(item.CustomSlug) ? item.Slug : item.CustomSlug;
        if (ownerIsAdmin)
        {
            return $"/s/{slug}";
        }

        if (!string.IsNullOrEmpty(ownerUsername))
        {
            return $"/s/{ownerUsername}/{slug}";
        }

        // A regular user with no username has no namespace for a custom slug to resolve in (that would
        // 404). The stable token always resolves at the root, so fall back to it - covers a custom slug
        // left behind after the username was cleared.
        return $"/s/{item.Slug}";
    }

    /// <summary>Absolute share URL (path joined to a base URL) for canonical/OpenGraph tags.</summary>
    public static string Url(string baseUrl, MediaItem item, User? owner)
    {
        return baseUrl.TrimEnd('/') + Path(item, owner);
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    /// <summary>Lower-case and trim a candidate; returns null when it's blank (which clears the
    /// custom slug and reverts the URL to the stable token).</summary>
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>1-64 chars of a-z, 0-9, dot, hyphen, underscore, beginning and ending with a letter
    /// or digit. Keeps slugs clean and unambiguous in a URL path segment.</summary>
    public static bool IsValid(string slug)
    {
        return slug.Length is > 0 and <= MaxSlugLength && SlugPattern().IsMatch(slug);
    }
}
