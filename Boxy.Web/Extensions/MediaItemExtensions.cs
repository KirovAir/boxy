using Boxy.Data.Entities;

namespace Boxy.Web.Extensions;

public static class MediaItemExtensions
{
    /// <summary>
    /// A short cache-busting token for the poster URL. The poster is stored by its content hash, so its
    /// file name doubles as a version: replace the thumbnail and this changes, which changes the URL and
    /// busts every cache (the browser, any proxy, and link-preview crawlers). Empty when there's no poster.
    /// </summary>
    public static string PosterVersion(this MediaItem item)
    {
        return item.PosterFileName is { Length: > 0 } name ? name[..Math.Min(12, name.Length)] : "";
    }
}
