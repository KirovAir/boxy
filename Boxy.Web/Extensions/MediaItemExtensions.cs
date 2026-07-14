using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The cache-busting token for <c>/f/{slug}</c>, and the reason the video URL can be cached hard at all.
    ///
    /// It must change whenever the BYTES behind that URL change, and the obvious "use the content hash"
    /// doesn't: the whole point of the heal is to swap the original for a produced rendition, and both of
    /// those names start with the same hash. So this is derived from the served blob's full NAME, which is
    /// exactly the thing that changes ({hash}.mp4 becomes {hash}-h264.mp4).
    ///
    /// Without it, the viewer this feature exists for keeps the broken bytes. /f/ used to promise
    /// immutable for a week, so a browser that had already downloaded the undecodable H.265 file would not
    /// even ask for a new one - it would just keep failing, for seven days, on a video we had already fixed.
    /// </summary>
    public static string WebVersion(this MediaItem item)
    {
        return BlobVersion(item.WebFileName ?? item.ContentHash + item.Extension);
    }

    /// <summary>The same, for the H.265 rendition. Empty when the item has none.</summary>
    public static string HqVersion(this MediaItem item)
    {
        return item.HqFileName is { Length: > 0 } name ? BlobVersion(name) : "";
    }

    private static string BlobVersion(string blobName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(blobName));
        return Convert.ToHexStringLower(digest)[..8];
    }
}
