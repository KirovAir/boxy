using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web.Extensions;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

/// <summary>
/// Serves the actual bytes: the playable stream at <c>/f/{slug}</c> (range-enabled so mobile
/// seeking works) and the poster at <c>/poster/{slug}</c>. Files are served inline; there is
/// deliberately no download endpoint.
/// </summary>
public class MediaController(IDbContextFactory<AppDbContext> dbFactory, IBlobStore storage, ShareUnlock unlock) : Controller
{
    // A published share with a password stays invisible to the public until unlocked; the owner or the
    // anonymous uploader (CanManage) always passes.
    private bool PasswordLocked(MediaItem item)
    {
        return item.SharePasswordHash is not null && !CanManage(item) && !unlock.IsUnlocked(Request, item);
    }

    [HttpGet("/f/{slug}")]
    public async Task<IActionResult> Stream(string slug, [FromQuery] int download = 0)
    {
        var item = await LoadVisibleAsync(slug);
        if (item is null || PasswordLocked(item))
        {
            return NotFound();
        }

        // Publicly, only ever serve the final, stable bytes: before Ready the file at this URL can still
        // change identity (original → normalized -web.mp4), which would corrupt an in-flight range
        // request. The owner may preview their own still-processing upload.
        if (item.Status != MediaStatus.Ready && !CanManage(item))
        {
            return NotFound();
        }

        // Downloading the original is opt-in per share (off by default) so previewable media stays in
        // Boxy. Enforce it on the endpoint too, not just the hidden button - the owner always may.
        if (download != 0 && !CanManage(item))
        {
            var kind = MediaKinds.Of(item.Extension, item.VideoCodec is not null, item.WebFileName is not null);
            if (!MediaKinds.CanDownloadOriginal(item.AllowDownload, kind))
            {
                return NotFound();
            }

            // Enforce the download cap: atomically count this request and refuse once exhausted. Counting
            // every request (ranges included) keeps the cap from being bypassed by ranged downloads; the
            // owner (handled above) never counts. Inline playback uses the no-download path, so it's free.
            if (item.MaxDownloads is int maxDl)
            {
                await using var cdb = await dbFactory.CreateDbContextAsync();
                var counted = await cdb.MediaItems.Where(m => m.Id == item.Id && m.DownloadCount < maxDl)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.DownloadCount, m => m.DownloadCount + 1));
                if (counted == 0)
                {
                    return NotFound();
                }
            }
        }

        // These are user-uploaded bytes served from our own origin: never let the browser MIME-sniff
        // them into something executable.
        Response.Headers.XContentTypeOptions = "nosniff";

        // download=1 forces the ORIGINAL (hi-res) file as an attachment with its real name. Otherwise
        // preview inline: the web-safe transcode for video, the original for everything else. mp4-family
        // (incl. .mov) is advertised as video/mp4 - desktop Chrome/Firefox reject video/quicktime.
        var wantOriginal = download != 0 || item.WebFileName is null;
        var fileName = wantOriginal ? item.ContentHash + item.Extension : item.WebFileName!;
        var contentType = wantOriginal ? ServedContentType(item) : "video/mp4";

        var serve = await storage.GetServeAsync(fileName, HttpContext.RequestAborted);
        if (serve is null)
        {
            return NotFound();
        }

        if (download != 0)
        {
            // Content-Disposition attachment with the uploader's original filename.
            var name = string.IsNullOrWhiteSpace(item.OriginalFileName) ? item.Slug + item.Extension : item.OriginalFileName;
            return BlobServing.Serve(serve, "application/octet-stream", name, true);
        }

        // The content-addressed -web.mp4 is truly immutable, so it can be cached hard. The original,
        // by contrast, may still be swapped for a -web.mp4 later (a legacy .mov heal keeps Status=Ready
        // while it produces one), so it must be revalidated - the ETag makes that a cheap 304.
        Response.Headers.CacheControl = item.WebFileName is not null && !wantOriginal
            ? "private, max-age=604800, immutable"
            : "private, no-cache";

        // SVG can contain script and executes when opened top-level. It still renders fine inside the
        // share page's <img> (subresource), but sandbox neutralizes a direct hit so it can't run here.
        if (contentType == "image/svg+xml")
        {
            Response.Headers.ContentSecurityPolicy = "sandbox";
        }

        return BlobServing.Serve(serve, contentType, null, true);
    }

    private static string ServedContentType(MediaItem item)
    {
        return item.Extension is ".mp4" or ".m4v" or ".mov" ? "video/mp4" : item.ContentType;
    }

    /// <summary>True for whoever may manage this item's bytes before it's public: the account that
    /// owns the share, the account that owns the box it was dropped into, or the anonymous visitor
    /// who uploaded it (matched by their <c>boxy_uid</c> cookie). Requires <c>Bucket</c> to be loaded.</summary>
    private bool CanManage(MediaItem item)
    {
        var uid = User.GetUserId();
        if (uid != 0 && (item.OwnerId == uid || item.Bucket?.OwnerId == uid))
        {
            // The share or box owner keeps access through the grace window - to download or restore.
            return true;
        }

        // The anonymous uploader may reach their own drop-off only while its box is still live; once the
        // box is link-off (expired) the file is gone for them too, matching the dead /u/ page.
        return item.UploaderToken is not null
               && !Retention.IsExpired(item.Bucket?.ExpiresAt, DateTime.UtcNow)
               && Request.Cookies.TryGetValue(UploaderCookie.Name, out var anon) && anon == item.UploaderToken;
    }

    // Lightweight polling endpoint so the uploader's "your uploads" row can flip from
    // "processing" to a thumbnail without a page reload. Visible to the owner (cookie).
    [HttpGet("/api/media/{slug}/status")]
    public async Task<IActionResult> Status(string slug)
    {
        var item = await LoadVisibleAsync(slug);
        if (item is null || PasswordLocked(item))
        {
            return NotFound();
        }

        return Json(new
        {
            status = item.Status.ToString(),
            ready = item.Status == MediaStatus.Ready,
            failed = item.Status == MediaStatus.Failed,
            poster = item.PosterFileName is not null
        });
    }

    // Toggle a like on a shared video using the visitor's anonymous boxy_uid account.
    [HttpPost("/api/media/{slug}/like")]
    public async Task<IActionResult> Like(string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // An expired (link-off) share is dead: no new likes until the sweep removes it.
        var now = DateTime.UtcNow;
        var item = await db.MediaItems.FirstOrDefaultAsync(m =>
            m.Slug == slug && m.BucketId == null && m.Published && (m.ExpiresAt == null || m.ExpiresAt > now));
        // A password-protected share is off-limits (even likes) until unlocked - same gate as the bytes.
        if (item is null || PasswordLocked(item))
        {
            // A body keeps this a clean 404 for the fetch() caller (a bodiless NotFound() on a POST is
            // re-executed by the status-code-pages middleware into a 405).
            return NotFound(new { error = "This share is no longer available." });
        }

        var token = UploaderCookie.GetOrCreate(HttpContext);
        var existing = await db.MediaLikes.FirstOrDefaultAsync(l => l.MediaItemId == item.Id && l.UploaderToken == token);
        bool liked;
        if (existing is not null)
        {
            db.MediaLikes.Remove(existing);
            liked = false;
        }
        else
        {
            db.MediaLikes.Add(new MediaLike { MediaItemId = item.Id, UploaderToken = token });
            liked = true;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Concurrent like/unlike of the same video by the same visitor raced on the unique
            // index - harmless; the count below reflects the real state.
        }

        var likes = await db.MediaLikes.CountAsync(l => l.MediaItemId == item.Id);
        return Json(new { likes, liked });
    }

    [HttpGet("/poster/{slug}")]
    public async Task<IActionResult> Poster(string slug)
    {
        var item = await LoadVisibleAsync(slug);
        if (item?.PosterFileName is null || PasswordLocked(item))
        {
            return NotFound();
        }

        var serve = await storage.GetServeAsync(item.PosterFileName, HttpContext.RequestAborted);
        if (serve is null)
        {
            return NotFound();
        }

        // The URL carries a ?v= content token, so a given URL always maps to the same bytes: cache it
        // hard. A bare hit (no token) revalidates instead, so a replaced thumbnail is still picked up.
        Response.Headers.CacheControl = Request.Query.ContainsKey("v")
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        return BlobServing.Serve(serve, "image/jpeg", null, false);
    }

    private async Task<MediaItem?> LoadVisibleAsync(string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.MediaItems.AsNoTracking().Include(m => m.Bucket).FirstOrDefaultAsync(m => m.Slug == slug);
        if (item is null)
        {
            return null;
        }

        // Visible when it's a published, unexpired public share (never a bucket drop-off), or to whoever
        // may manage it (share owner, box owner, or the anonymous uploader) - the owner keeps byte access
        // during the grace window so the dashboard can still show and restore an expired item.
        return Retention.IsPubliclyVisible(item.BucketId, item.Published, item.ExpiresAt, DateTime.UtcNow)
               || CanManage(item)
            ? item
            : null;
    }
}
