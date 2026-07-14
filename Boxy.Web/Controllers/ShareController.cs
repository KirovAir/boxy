using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web.Extensions;
using Boxy.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

/// <summary>Public share page with server-rendered OpenGraph metadata. An admin's shares live at
/// <c>/s/{slug}</c>; a regular user's under their username at <c>/s/{username}/{slug}</c>. The stable
/// token always resolves at the root too, so links never break after a rename.</summary>
public class ShareController(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config, Services.ShareUnlock unlock) : Controller
{
    [HttpGet("/s/{slug}")]
    public Task<IActionResult> Index(string slug)
    {
        return ShowAsync(null, slug);
    }

    [HttpGet("/s/{username}/{slug}")]
    public Task<IActionResult> IndexNamespaced(string username, string slug)
    {
        return ShowAsync(username, slug);
    }

    [HttpPost("/s/{slug}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Unlock(string slug, string? password)
    {
        return UnlockAsync(null, slug, password);
    }

    [HttpPost("/s/{username}/{slug}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UnlockNamespaced(string username, string slug, string? password)
    {
        return UnlockAsync(username, slug, password);
    }

    private async Task<IActionResult> ShowAsync(string? username, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await ResolveAsync(db, username, slug);
        var owner = item?.Owner;

        // Only user-uploaded videos are public shares. Bucket drop-offs (BucketId != null) are never
        // shareable - they exist only for the box owner to download/process. Anything unpublished or
        // still processing is 404 to everyone but the share's owner (previewing before publishing).
        var uid = User.GetUserId();
        var isOwner = item is not null && uid != 0 && item.OwnerId == uid;
        // Expiry is link-off for everyone, owner included: the page is dead once past ExpiresAt, and the
        // owner restores it from the dashboard during the grace window before it's deleted.
        if (item is null || item.BucketId is not null
                         || (!item.Published && !isOwner) || (item.Status != MediaStatus.Ready && !isOwner)
                         || Retention.IsExpired(item.ExpiresAt, DateTime.UtcNow))
        {
            return NotFound();
        }

        // A password-protected share shows the unlock prompt (not the content, and no view count) until
        // the visitor enters the password. The owner always bypasses.
        if (!isOwner && item.SharePasswordHash is not null && !unlock.IsUnlocked(Request, item))
        {
            return View("Locked", new ShareLockedViewModel { PostPath = Request.Path });
        }

        // Count real viewers: not the owner previewing, and not link-preview crawlers/bots
        // (WhatsApp, Slack, Discord, etc. fetch /s/ for the OpenGraph card).
        var ua = Request.Headers.UserAgent.ToString();
        var isBot = ua.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(
            ua, "bot|crawl|spider|facebookexternalhit|whatsapp|telegram|slack|discord|embed|preview|curl|wget|python-requests",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var incremented = !isOwner && item.Published && !isBot;
        if (incremented)
        {
            await db.MediaItems.Where(m => m.Id == item.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Views, m => m.Views + 1));
        }

        var likeToken = Services.UploaderCookie.Current(HttpContext);
        var likes = await db.MediaLikes.CountAsync(l => l.MediaItemId == item.Id);
        var likedByMe = likeToken is not null
                        && await db.MediaLikes.AnyAsync(l => l.MediaItemId == item.Id && l.UploaderToken == likeToken);

        var baseUrl = config.PublicBaseUrl(Request);
        var kind = MediaKinds.Of(item.Extension, item.VideoCodec is not null, item.WebFileName is not null);
        // A video is advertised as video/mp4 only when that's what we actually serve: a produced
        // rendition, or an mp4-family original served as-is. A "don't convert it" video with no web file is
        // served raw in its own container, so it keeps its real MIME (as does every non-video kind).
        var servedAsMp4 = item.WebFileName is not null || item.Extension is ".mp4" or ".m4v" or ".mov";
        var contentType = kind == MediaKind.Video && servedAsMp4 ? "video/mp4" : item.ContentType;
        var capReached = !isOwner && item.MaxDownloads is int md && item.DownloadCount >= md;

        // Both URLs carry a ?v= token derived from the blob they serve, so each is safe to cache forever
        // and a re-converted video is picked up the moment it changes.
        var fileUrl = $"{baseUrl}/f/{item.Slug}?v={item.WebVersion()}";

        // Best first. The H.265 rendition names itself exactly so a browser without the decoder can skip
        // it; the H.264 one names itself vaguely on purpose, because it is the source that must never be
        // skipped. See VideoSource.
        var sources = new List<VideoSource>();
        if (kind == MediaKind.Video && item is { HqFileName: not null, HqCodecs: not null })
        {
            sources.Add(new VideoSource($"{baseUrl}/f/{item.Slug}?r=hq&v={item.HqVersion()}",
                $"video/mp4; codecs=\"{item.HqCodecs}\""));
        }

        if (kind == MediaKind.Video)
        {
            sources.Add(new VideoSource(fileUrl, contentType));
        }

        var model = new ShareViewModel
        {
            Sources = sources,
            Title = item.Title,
            Description = item.Description,
            PageUrl = ShareUrls.Url(baseUrl, item, owner),
            FileUrl = fileUrl,
            DownloadUrl = $"{baseUrl}/f/{item.Slug}?download=1",
            PosterUrl = item.PosterFileName is not null ? $"{baseUrl}/poster/{item.Slug}?v={item.PosterVersion()}" : null,
            // Link-preview image: the poster if we have one, else the image itself, else a branded card
            // so PDFs, audio, and generic files still unfurl with something instead of a blank preview.
            OgImageUrl = item.PosterFileName is not null ? $"{baseUrl}/poster/{item.Slug}?v={item.PosterVersion()}"
                : kind == MediaKind.Image ? $"{baseUrl}/f/{item.Slug}"
                : $"{baseUrl}/img/og-default.png",
            ContentType = contentType,
            Kind = kind,
            // The cap only applies to the public; the owner bypasses it (matching the /f endpoint), so
            // their own page keeps the download button.
            CanDownload = MediaKinds.CanDownloadOriginal(item.AllowDownload, kind) && !capReached,
            DownloadLimitReached = MediaKinds.CanDownloadOriginal(item.AllowDownload, kind) && capReached,
            OriginalFileName = item.OriginalFileName,
            SizeBytes = item.SizeBytes,
            Width = item.Width,
            Height = item.Height,
            Views = item.Views + (incremented ? 1 : 0),
            Likes = likes,
            LikedByMe = likedByMe,
            Slug = item.Slug
        };
        // Name the view explicitly: both actions funnel through here, so the view can't be inferred
        // from the (varying) action name.
        return View("Index", model);
    }

    /// <summary>Verify a share's password; on success set the unlock cookie and reload the now-open page.</summary>
    private async Task<IActionResult> UnlockAsync(string? username, string slug, string? password)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await ResolveAsync(db, username, slug);
        if (item is null || item.BucketId is not null || !item.Published
            || Retention.IsExpired(item.ExpiresAt, DateTime.UtcNow) || item.SharePasswordHash is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(password) || !SharePasswords.Verify(item.SharePasswordHash, password))
        {
            return View("Locked", new ShareLockedViewModel { PostPath = Request.Path, HasError = true });
        }

        unlock.Grant(Response, item);
        return Redirect(Request.Path);
    }

    /// <summary>Find the share behind a URL. At the root, an admin's custom slug wins, then the stable
    /// token (a permanent alias for any owner). Under a username, the match is scoped to that user's
    /// shares by either their custom slug or the stable token. Slugs are stored lower-case.</summary>
    private static async Task<MediaItem?> ResolveAsync(AppDbContext db, string? username, string slug)
    {
        var s = slug.ToLowerInvariant();
        var q = db.MediaItems.AsNoTracking().Include(m => m.Owner);
        if (username is null)
        {
            return await q.FirstOrDefaultAsync(m => m.CustomSlug == s && m.Owner!.Role == UserRole.Admin)
                   ?? await q.FirstOrDefaultAsync(m => m.Slug == s);
        }

        var name = username.ToLowerInvariant();
        return await q.FirstOrDefaultAsync(m =>
            m.Owner!.Username == name && (m.CustomSlug == s || m.Slug == s));
    }
}
