using System.IO.Compression;
using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Extensions;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

// Every signed-in account's own dashboard. All queries are scoped to the current owner, so a user
// only ever sees and manages their own boxes and shares; the admin-only platform area lives elsewhere.
[Authorize]
[Route("dashboard")]
public class DashboardController(
    IDbContextFactory<AppDbContext> dbFactory,
    IngestionService ingestion,
    ChunkedUploadService chunked,
    UploadFinalizer finalizer,
    IBlobStore storage,
    MediaProcessor processor,
    IEmailSender emailSender,
    EmailComposer emailComposer,
    IConfiguration config,
    ILogger<DashboardController> logger) : Controller
{
    private const int VideoPageSize = 24;
    private const int FilePageSize = 20;

    private int UserId => User.GetUserId();

    // A media item the current user may manage: a share they own, or a drop-off in a box they own.
    private IQueryable<MediaItem> OwnedMedia(AppDbContext db)
    {
        return db.MediaItems.Where(m => m.OwnerId == UserId || (m.BucketId != null && m.Bucket!.OwnerId == UserId));
    }

    [HttpGet("")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(int? vp, string? vs, int? fp, string? fs, string? tab, CancellationToken ct)
    {
        ViewData["Tab"] = tab == "boxes" ? "boxes" : "shares";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.OwnerId == UserId)
            .OrderByDescending(b => b.CreatedDate).ToListAsync(ct);
        var counts = await db.MediaItems.Where(m => m.BucketId != null && m.Bucket!.OwnerId == UserId)
            .GroupBy(m => m.BucketId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var (vNum, vSort) = Page<MediaItem>.Normalize(vp, vs, MediaSort.Shares.Keys(), MediaSort.Default);
        var (fNum, fSort) = Page<MediaItem>.Normalize(fp, fs, MediaSort.Files.Keys(), MediaSort.Default);
        var vFilter = MediaFilter.From(Request.Query, "v");
        var fFilter = MediaFilter.From(Request.Query, "f");

        var videoQuery = db.MediaItems.AsNoTracking().Where(m => m.BucketId == null && m.OwnerId == UserId);
        var filesQuery = db.MediaItems.AsNoTracking().Where(m => m.BucketId != null && m.Bucket!.OwnerId == UserId);
        var videos = await videoQuery.ToPageAsync(vFilter, vNum, VideoPageSize, vSort, ct);
        var files = await filesQuery.ToPageAsync(fFilter, fNum, FilePageSize, fSort, ct);
        var videoKindCounts = await videoQuery.KindCountsAsync(vFilter, ct);
        var fileKindCounts = await filesQuery.KindCountsAsync(fFilter, ct);

        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        var isAdmin = me?.Role == UserRole.Admin;
        var settings = await db.GetSettingsAsync<PlatformSettings>(ct);
        var quotaBytes = isAdmin ? 0L : Math.Max(0L, me?.QuotaBytes ?? settings.DefaultUserQuotaBytes);

        return View(new AdminDashboardViewModel
        {
            Buckets = buckets,
            BucketCounts = counts,
            Videos = videos,
            Files = files,
            VideosFilter = vFilter,
            FilesFilter = fFilter,
            VideoKindCounts = videoKindCounts,
            FileKindCounts = fileKindCounts,
            BaseUrl = config.PublicBaseUrl(Request),
            OwnerUsername = me?.Username,
            OwnerIsAdmin = isAdmin,
            MaxUploadBytes = isAdmin ? 0L : settings.MaxUploadBytes,
            QuotaBytes = quotaBytes,
            UsageBytes = quotaBytes > 0 ? await QuotaService.UsageBytesAsync(db, UserId, ct) : 0
        });
    }

    [HttpPost("buckets/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBucket(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            this.FlashError("Enter a name for the box.");
            return RedirectToAction(nameof(Index));
        }

        var expiry = await DefaultExpiryAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Buckets.Add(new Bucket { Name = name.Trim(), Slug = await NewBucketSlugAsync(db), IsOpen = true, OwnerId = UserId, ExpiresAt = expiry });
        await db.SaveChangesAsync();

        this.FlashSuccess($"Box “{name.Trim()}” created.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>The link-off expiry for content the current user creates now: a regular user gets the
    /// configured retention window, an admin (or a zero setting) gets none. Self-contained so the
    /// upload paths, which have no db context of their own, can call it too.</summary>
    private async Task<DateTime?> DefaultExpiryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        var settings = await db.GetSettingsAsync<PlatformSettings>(ct);
        return Retention.ExpiryFor(me?.Role == UserRole.Admin, settings.RetentionDays, DateTime.UtcNow);
    }

    /// <summary>The upload size cap for the current user (0 = unlimited). Admins are exempt.</summary>
    private async Task<long> MaxUploadBytesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (me?.Role == UserRole.Admin)
        {
            return 0;
        }

        return (await db.GetSettingsAsync<PlatformSettings>(ct)).MaxUploadBytes;
    }

    private static string MbLabel(long bytes)
    {
        return $"{bytes / 1024 / 1024} MB";
    }

    // Restart a box's expiry countdown (or clear it, if retention is off / the owner is an admin).
    // Also un-expires a box that's in its grace window.
    [HttpPost("buckets/{id:int}/keep")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> KeepBucket(int id, CancellationToken ct)
    {
        var expiry = await DefaultExpiryAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Buckets.Where(b => b.Id == id && b.OwnerId == UserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ExpiresAt, expiry)
                .SetProperty(b => b.ExpiryRemindedAt, (DateTime?)null), ct);
        if (affected > 0)
        {
            this.FlashSuccess(expiry is null ? "This box will no longer expire." : "Kept - the box's countdown restarts.");
        }

        return RedirectToAction(nameof(Bucket), new { id });
    }

    // Same for a share: restart its countdown, or restore it during the grace window.
    [HttpPost("media/{id:int}/keep")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> KeepMedia(int id, CancellationToken ct)
    {
        var expiry = await DefaultExpiryAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.MediaItems.Where(m => m.Id == id && m.OwnerId == UserId && m.BucketId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ExpiresAt, expiry)
                .SetProperty(m => m.ExpiryRemindedAt, (DateTime?)null), ct);
        if (affected > 0)
        {
            this.FlashSuccess(expiry is null ? "This share will no longer expire." : "Kept - the share's countdown restarts.");
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("buckets/{id:int}")]
    public async Task<IActionResult> Bucket(int id, int? p, string? s, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId, ct);
        if (bucket is null)
        {
            return NotFound();
        }

        var (num, sort) = Page<MediaItem>.Normalize(p, s, MediaSort.Files.Keys(), MediaSort.Default);
        var filter = MediaFilter.From(Request.Query, "");
        var baseQuery = db.MediaItems.AsNoTracking().Where(m => m.BucketId == id);

        // "Show only this uploader": the chip link carries a one-way code (never the token, which is a
        // delete-credential). Resolve it against this box's own uploaders so the raw token stays here,
        // then narrow before counting so the type chips and pager reflect just that person's files.
        var resolved = await ResolveUploaderAsync(baseQuery, Request.Query["u"].ToString(), ct);
        if (resolved is { } up)
        {
            baseQuery = baseQuery.Where(m => m.UploaderToken == up.Token);
        }

        var files = await baseQuery.ToPageAsync(filter, num, FilePageSize, sort, ct);
        var kindCounts = await baseQuery.KindCountsAsync(filter, ct);

        // The owner's live email (not the cookie claim, which lags an admin-side email change), so the
        // email toggle reflects where the worker would actually send.
        var ownerEmail = await db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstOrDefaultAsync(ct);

        return View(new BucketDetailViewModel
        {
            Bucket = bucket, Files = files, Filter = filter, KindCounts = kindCounts,
            ActiveUploader = resolved?.Identity,
            BaseUrl = config.PublicBaseUrl(Request), OwnerEmail = ownerEmail
        });
    }

    /// <summary>Turn an uploader-chip <c>code</c> (a one-way hash, safe in a URL) back into the actual
    /// <c>UploaderToken</c> to filter by - matched only against the distinct uploaders of this very box,
    /// so the token is never accepted from, nor echoed to, the client. Returns null when the code is
    /// absent or matches nobody (a hand-edited URL simply shows the unfiltered box).</summary>
    private static async Task<(string Token, UploaderIdentity Identity)?> ResolveUploaderAsync(
        IQueryable<MediaItem> boxQuery, string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var tokens = await boxQuery.Where(m => m.UploaderToken != null)
            .Select(m => m.UploaderToken!).Distinct().ToListAsync(ct);
        foreach (var token in tokens)
        {
            if (UploaderIdentity.For(token) is { } identity && identity.Code == code)
            {
                return (token, identity);
            }
        }

        return null;
    }

    [HttpPost("buckets/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleBucket(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var bucket = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId);
        if (bucket is null)
        {
            this.FlashError("That box no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        bucket.IsOpen = !bucket.IsOpen;
        await db.SaveChangesAsync();

        // Stay on the box page (that's where the toggle lives) and confirm what changed.
        this.FlashSuccess(bucket.IsOpen ? "Uploads reopened for this box." : "Uploads closed for this box.");
        return RedirectToAction(nameof(Bucket), new { id });
    }

    [HttpPost("buckets/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBucket(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Buckets.AnyAsync(b => b.Id == id && b.OwnerId == UserId))
        {
            this.FlashInfo("That box was already deleted.");
            return RedirectToAction(nameof(Index));
        }

        // Keep the box's drop-off files, but adopt them to the owner first: deleting the box nulls
        // their BucketId (FK set-null), and an item with no box and no owner falls out of every
        // dashboard query. Ownership is confirmed above, so this only touches the owner's own files.
        await db.MediaItems.Where(m => m.BucketId == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.OwnerId, UserId));
        await db.Buckets.Where(b => b.Id == id).ExecuteDeleteAsync();

        this.FlashSuccess("Box deleted. Its uploaded files were kept.");
        return RedirectToAction(nameof(Index));
    }

    // No-JS fallback: plain multipart form post.
    [HttpPost("upload")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var keepOriginal = Request.Form["keepOriginal"] == "true";
        var expiry = await DefaultExpiryAsync(ct);
        var maxBytes = await MaxUploadBytesAsync(ct);
        var count = 0;
        var tooBig = 0;
        var full = false;
        foreach (var file in Request.Form.Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            if (maxBytes > 0 && file.Length > maxBytes)
            {
                tooBig++;
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();
                await ingestion.IngestAsync(UploadSource.FromStream(stream), file.FileName, null, true, null, UserId, keepOriginal, expiry, maxBytes, UserId, ct);
                count++;
            }
            catch (QuotaExceededException)
            {
                // This file didn't fit; a smaller later one still might, so keep going.
                full = true;
                continue;
            }
        }

        if (tooBig > 0)
        {
            this.FlashError($"{tooBig} file{(tooBig == 1 ? " was" : "s were")} over the {MbLabel(maxBytes)} limit and skipped.");
        }

        if (full)
        {
            this.FlashError("You're out of storage space. Delete something or ask an admin to raise your quota.");
        }

        if (count > 0)
        {
            this.FlashSuccess($"Uploaded {count} file{(count == 1 ? "" : "s")}. Processing now.");
        }
        else
        {
            this.FlashWarning("No files were selected.");
        }

        return RedirectToAction(nameof(Index));
    }

    // Chunked engine (JS): same reliable large-file path as the public page. Admin uploads
    // are published immediately.
    [HttpPost("upload/chunk")]
    [IgnoreAntiforgeryToken]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadChunk([FromQuery] string uploadId, [FromQuery] int index, CancellationToken ct)
    {
        try
        {
            await chunked.WriteChunkAsync(uploadId, index, Request.Body, await MaxUploadBytesAsync(ct), ct);
            return Ok();
        }
        catch (UploadTooLargeException ex)
        {
            return BadRequest(new { error = $"That file is over the {MbLabel(ex.MaxBytes)} upload limit." });
        }
        catch (StorageFullException)
        {
            return new ObjectResult(new { error = "The server is out of storage space." })
            {
                StatusCode = StatusCodes.Status507InsufficientStorage
            };
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    // Existing chunk indices for a resumed admin upload. A part only counts when its length matches the
    // slot the client would put it in, so a stale part is re-sent rather than trusted.
    [HttpGet("upload/chunks")]
    [IgnoreAntiforgeryToken]
    public IActionResult UploadChunks([FromQuery] string uploadId, [FromQuery] long size, [FromQuery] long chunkSize)
    {
        try
        {
            return Json(new { have = chunked.ExistingChunks(uploadId, size, chunkSize) });
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    // Assembly runs detached from this request - a multi-GB concatenate outlasts any proxy's read timeout -
    // so this answers with the finished item, or a 202 the client polls on.
    [HttpPost("upload/complete")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> UploadComplete([FromQuery] string uploadId, [FromQuery] int total, [FromQuery] string name,
        [FromQuery] long size, [FromQuery] long chunkSize, [FromQuery] bool keepOriginal, CancellationToken ct)
    {
        var layout = new UploadLayout(size, chunkSize, total);
        var expiry = await DefaultExpiryAsync(ct);
        var maxBytes = await MaxUploadBytesAsync(ct);
        var userId = UserId;

        var run = finalizer.StartOrJoin(uploadId, (services, jobCt) => AssembleAsync(services,
            async chunked => (MediaItem?)await chunked.CompleteAsync(uploadId, layout, name, null, true, null, userId, keepOriginal, expiry, maxBytes, userId, jobCt)));

        return await UploadResults.AwaitOrAcceptAsync(run);
    }

    [HttpGet("upload/complete/status")]
    [IgnoreAntiforgeryToken]
    public IActionResult UploadCompleteStatus([FromQuery] string uploadId)
    {
        return UploadResults.Describe(finalizer.Find(uploadId));
    }

    // Runs the assembly and turns every way it can fail into something the client can act on. Any failure
    // has already discarded the staged parts, so all of these are "start over" answers.
    private async Task<UploadOutcome> AssembleAsync(IServiceProvider services, Func<ChunkedUploadService, Task<MediaItem?>> assemble)
    {
        try
        {
            var item = await assemble(services.GetRequiredService<ChunkedUploadService>());
            return item is null ? UploadOutcome.ItemGone() : UploadOutcome.Done(item.Slug, item.Title);
        }
        catch (UploadTooLargeException ex)
        {
            return UploadOutcome.Failed($"That file is over the {MbLabel(ex.MaxBytes)} upload limit.");
        }
        catch (QuotaExceededException)
        {
            return UploadOutcome.Failed("You're out of storage space. Delete something or ask an admin to raise your quota.");
        }
        catch (StorageFullException)
        {
            return UploadOutcome.Failed("The server is out of storage space.");
        }
        catch (UploadIncompleteException ex)
        {
            logger.LogWarning(ex, "Discarded incomplete upload");
            return UploadOutcome.Failed("That upload didn't arrive intact. Please pick the file again.");
        }
        catch (ArgumentException)
        {
            return UploadOutcome.Failed("That upload is no longer valid.");
        }
        catch (InvalidOperationException)
        {
            return UploadOutcome.Failed("Upload session not found.");
        }
    }

    // Refused once the assembly has started: pulling the parts out from under it would fail the upload the
    // user is waiting on.
    [HttpPost("upload/abort")]
    [IgnoreAntiforgeryToken]
    public IActionResult UploadAbort([FromQuery] string uploadId)
    {
        try
        {
            if (finalizer.IsRunning(uploadId))
            {
                return Conflict(new { error = "That upload is already being finished." });
            }

            chunked.Abort(uploadId);
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        return Ok();
    }

    // Save the box's drop-off notification settings: a webhook URL and/or email-me toggle (owner-only).
    [HttpPost("buckets/{id:int}/notifications")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNotifications(int id, string? webhookUrl, bool emailOnDrop, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
        {
            this.FlashError("Enter a valid http(s) URL, or leave it blank to turn the webhook off.");
            return RedirectToAction(nameof(Bucket), new { id });
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var box = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId, ct);
        if (box is null)
        {
            return RedirectToAction(nameof(Bucket), new { id });
        }

        box.WebhookUrl = url;
        if (emailOnDrop && !box.EmailOnDrop)
        {
            // Opt-in: start the email watermark now so only future drops notify, never the whole backlog.
            box.EmailNotifiedAt = DateTime.UtcNow;
        }

        box.EmailOnDrop = emailOnDrop;
        await db.SaveChangesAsync(ct);
        this.FlashSuccess("Notification settings saved.");
        return RedirectToAction(nameof(Bucket), new { id });
    }

    // Grab every file dropped into a box as one zip. Streamed entry-by-entry so a box of multi-GB
    // footage never buffers in memory. Owner-only.
    [HttpGet("buckets/{id:int}/download-all")]
    public async Task<IActionResult> DownloadBox(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId, ct);
        if (bucket is null)
        {
            return NotFound();
        }

        var files = await db.MediaItems.AsNoTracking().Where(m => m.BucketId == id)
            .OrderBy(m => m.CreatedDate)
            .Select(m => new ZipEntry(m.ContentHash, m.Extension, m.OriginalFileName))
            .ToListAsync(ct);
        if (files.Count == 0)
        {
            this.FlashInfo("This box has no files to download yet.");
            return RedirectToAction(nameof(Bucket), new { id });
        }

        await StreamZipAsync(files, ZipFileName(bucket.Name), ct);
        return new EmptyResult();
    }

    // Download an arbitrary selection of the caller's own files (drop-offs or shares) as one zip.
    [HttpPost("media/bulk-download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDownload(int[] ids, CancellationToken ct)
    {
        if (ids is not { Length: > 0 })
        {
            return RedirectToAction(nameof(Index));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = await OwnedMedia(db).AsNoTracking().Where(m => ids.Contains(m.Id))
            .OrderBy(m => m.CreatedDate)
            .Select(m => new ZipEntry(m.ContentHash, m.Extension, m.OriginalFileName))
            .ToListAsync(ct);
        if (files.Count == 0)
        {
            this.FlashInfo("Nothing selected to download.");
            return RedirectToAction(nameof(Index));
        }

        await StreamZipAsync(files, "selected.zip", ct);
        return new EmptyResult();
    }

    // Delete a selection of the caller's own files in one go. Rows go first, then any now-unreferenced
    // blobs (dedup-safe across the whole batch). Redirects back to wherever the selection was made.
    [HttpPost("media/bulk-delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDelete(int[] ids, string? returnUrl, CancellationToken ct)
    {
        if (ids is { Length: > 0 })
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var items = await OwnedMedia(db).Where(m => ids.Contains(m.Id)).ToListAsync(ct);
            foreach (var m in items)
            {
                db.MediaItems.Remove(m);
            }

            await db.SaveChangesAsync(ct);
            foreach (var m in items)
            {
                await DeleteMediaFilesIfUnreferencedAsync(db, m, ct);
            }

            this.FlashSuccess($"Deleted {items.Count} item{(items.Count == 1 ? "" : "s")}.");
        }

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(Index))!);
    }

    // Streams the given files into the response as a zip, entry by entry (never buffering a whole file).
    private async Task StreamZipAsync(IReadOnlyList<ZipEntry> files, string zipName, CancellationToken ct)
    {
        // ZipArchive writes synchronously; allow sync IO on this one response so the zip can stream.
        var bodyControl = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";

        // NoCompression: the contents are already-compressed media, so deflate would just burn CPU.
        using var zip = new ZipArchive(Response.Body, ZipArchiveMode.Create, true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var blobName = f.ContentHash + f.Extension;
            if (!await storage.ExistsAsync(blobName, ct))
            {
                continue;
            }

            var name = SafeEntryName(string.IsNullOrWhiteSpace(f.OriginalFileName) ? $"file{f.Extension}" : f.OriginalFileName);
            var entry = zip.CreateEntry(UniqueEntryName(used, name), CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await using var fs = await storage.OpenReadAsync(blobName, ct);
            await fs.CopyToAsync(entryStream, ct);
        }
    }

    private record ZipEntry(string ContentHash, string Extension, string? OriginalFileName);

    // Best-effort delete of a local scratch file (ffmpeg temp in/out).
    private static void TryDeleteLocal(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            /* best-effort scratch cleanup */
        }
    }

    // A filesystem-safe, header-safe zip name from the box name.
    private static string ZipFileName(string boxName)
    {
        var safe = new string(boxName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        return (string.IsNullOrEmpty(safe) ? "box" : safe) + ".zip";
    }

    // Reduce an uploader-controlled filename to a bare, safe zip entry name: strip any directory parts
    // and traversal so extraction can't escape the target folder (zip-slip).
    private static string SafeEntryName(string name)
    {
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim();
        return name is "" or "." or ".." ? "file" : name;
    }

    // Keep zip entry names distinct when two drop-offs share an original filename.
    private static string UniqueEntryName(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2;; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // Download the original file (admin only) - how the admin retrieves bucket drop-offs.
    [HttpGet("media/{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await OwnedMedia(db).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        var serve = await storage.GetServeAsync(item.ContentHash + item.Extension, HttpContext.RequestAborted);
        if (serve is null)
        {
            return NotFound();
        }

        return BlobServing.Serve(serve, item.ContentType, item.OriginalFileName, true);
    }

    [HttpGet("media/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await OwnedMedia(db).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
        ViewBag.BaseUrl = config.PublicBaseUrl(Request);
        ViewBag.OwnerUsername = me?.Username;
        ViewBag.OwnerIsAdmin = me?.Role == UserRole.Admin;
        ViewBag.MaxUploadBytes = me?.Role == UserRole.Admin ? 0L : (await db.GetSettingsAsync<PlatformSettings>()).MaxUploadBytes;
        ViewBag.EmailEnabled = await emailSender.IsEnabledAsync();
        return View(item);
    }

    [HttpPost("media/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string title, string? description, string? slug, bool published, bool allowDownload, string? sharePassword, bool removePassword, int? maxDownloads,
        IFormFile? thumbnail, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await OwnedMedia(db).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            this.FlashError("That item no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        item.Title = string.IsNullOrWhiteSpace(title) ? item.Title : title.Trim();
        item.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        // Only an owner's share (BucketId == null) can be public. A drop-off is never publishable, so this
        // toggle can't turn a file a stranger dropped into a box into a public link (defence-in-depth: the
        // serving path also refuses to publicly serve a drop-off).
        item.Published = published && item.BucketId == null;
        item.AllowDownload = allowDownload;
        item.MaxDownloads = maxDownloads is > 0 ? maxDownloads : null;

        // Password: remove wins; else a non-blank value sets/replaces it; blank leaves it unchanged.
        if (removePassword)
        {
            item.SharePasswordHash = null;
        }
        else if (!string.IsNullOrEmpty(sharePassword))
        {
            item.SharePasswordHash = SharePasswords.Hash(sharePassword);
        }

        // Custom link: only a share has a public URL to rename (a drop-off never does).
        if (item.BucketId is null && item.OwnerId is int ownerId)
        {
            var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId, ct);
            var slugError = owner is null ? null : await ApplyCustomSlugAsync(db, item, owner, slug, ct);
            if (slugError is not null)
            {
                this.FlashError(slugError);
                return RedirectToAction(nameof(Edit), new { id });
            }
        }

        if (thumbnail is { Length: > 0 })
        {
            // For a video, size the poster to the video's resolution so it lines up with the frame;
            // for anything else, keep the image's own aspect.
            var kind = MediaKinds.Of(item.Extension, item.VideoCodec is not null, item.WebFileName is not null);
            var (tw, th) = kind == MediaKind.Video ? (item.Width, item.Height) : (null, null);
            var newPoster = await SaveCustomThumbAsync(thumbnail, tw, th, ct);
            if (newPoster is null)
            {
                this.FlashError("That thumbnail could not be read as an image.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            var oldPoster = item.PosterFileName;
            item.PosterFileName = newPoster;
            // Drop the previous poster once nothing else references it (dedup-safe).
            if (oldPoster is not null && oldPoster != newPoster
                                      && !await db.MediaItems.AnyAsync(m => m.PosterFileName == oldPoster && m.Id != id, ct))
            {
                await storage.DeleteAsync(oldPoster, ct);
            }
        }

        await db.SaveChangesAsync(ct);

        // Stay on the edit page so the change is visible and confirmed.
        this.FlashSuccess("Changes saved.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Validate and set a share's custom slug within its owner's namespace (an admin's is the
    /// root <c>/s/</c>; a user's is <c>/s/{username}/</c>). Returns an error to flash, or null on success -
    /// including a no-op or a cleared slug (which reverts the URL to the stable token).</summary>
    private static async Task<string?> ApplyCustomSlugAsync(AppDbContext db, MediaItem item, User owner, string? raw, CancellationToken ct)
    {
        var slug = ShareUrls.Normalize(raw);
        if (slug == item.CustomSlug)
        {
            return null;
        }

        if (slug is null)
        {
            item.CustomSlug = null;
            return null;
        }

        if (!ShareUrls.IsValid(slug))
        {
            return "A custom link uses 1-64 letters, numbers, dots, hyphens or underscores.";
        }

        // A regular user's shares live under their username; without one there's no namespace for a
        // custom link to resolve in. (An admin publishes to the root, so this never blocks them.)
        if (owner.Role != UserRole.Admin && string.IsNullOrEmpty(owner.Username))
        {
            return "Set a username on this account before choosing a custom link.";
        }

        // Must be free in this share's namespace. At the root, that means no other item's stable token
        // and no other admin custom slug; for a user, nothing else of theirs resolves to it.
        var taken = owner.Role == UserRole.Admin
            ? await db.MediaItems.AnyAsync(m => m.Id != item.Id
                                                && (m.Slug == slug || (m.CustomSlug == slug && m.Owner!.Role == UserRole.Admin)), ct)
            : await db.MediaItems.AnyAsync(m => m.Id != item.Id && m.OwnerId == owner.Id
                                                                && (m.Slug == slug || m.CustomSlug == slug), ct);
        if (taken)
        {
            return "That link is already taken - pick another.";
        }

        item.CustomSlug = slug;
        return null;
    }

    /// <summary>
    /// Normalizes an uploaded image into a stored, content-addressed JPEG poster (scaled down, like an
    /// auto-generated one). Returns the stored file name, or null when the upload is not a usable image.
    /// </summary>
    private async Task<string?> SaveCustomThumbAsync(IFormFile file, int? width, int? height, CancellationToken ct)
    {
        var scratch = storage.ScratchDir;
        var tmpIn = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        var tmpOut = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}.jpg");
        try
        {
            await using (var fs = System.IO.File.Create(tmpIn))
            {
                await file.CopyToAsync(fs, ct);
            }

            if (!await processor.ResizeThumbnailAsync(tmpIn, tmpOut, width, height, ct))
            {
                return null;
            }

            await using var jpg = System.IO.File.OpenRead(tmpOut);
            var stored = await storage.SaveAsync(jpg, ".jpg", ct);
            return stored.Hash + ".jpg";
        }
        finally
        {
            TryDeleteLocal(tmpIn);
            TryDeleteLocal(tmpOut);
        }
    }

    // Swap in a newer version of a file while keeping its share URL, title, views, and likes. The
    // chunked path (below) is the same reliable engine a new upload uses, with live progress; this
    // multipart action is the no-JS fallback.
    [HttpPost("media/{id:int}/replace")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> ReplaceMedia(int id, IFormFile? file, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await OwnedMedia(db).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            this.FlashError("That item no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        if (file is null || file.Length == 0)
        {
            this.FlashWarning("Choose a file to replace it with.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var maxBytes = await MaxUploadBytesAsync(ct);
        if (maxBytes > 0 && file.Length > maxBytes)
        {
            this.FlashError($"That file is over the {MbLabel(maxBytes)} upload limit.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            await ingestion.ReplaceAsync(id, UploadSource.FromStream(stream), file.FileName, maxBytes, UserId, ct);
            this.FlashSuccess("File replaced. Processing the new version now.");
        }
        catch (QuotaExceededException)
        {
            this.FlashError("You're out of storage space. Delete something or ask an admin to raise your quota.");
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // Chunked replace: upload.js stages parts through the shared upload/chunk endpoint, then calls this
    // to assemble them into this existing item. Owner-checked; the chunk staging is item-agnostic.
    [HttpPost("media/{id:int}/replace/complete")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ReplaceComplete(int id, [FromQuery] string uploadId, [FromQuery] int total,
        [FromQuery] string name, [FromQuery] long size, [FromQuery] long chunkSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await OwnedMedia(db).AnyAsync(m => m.Id == id, ct))
        {
            // A body keeps this a clean 404 for the XHR caller: a bodiless NotFound() would be
            // re-executed by UseStatusCodePagesWithReExecute against the GET-only status page (405).
            return NotFound(new { error = "That item no longer exists." });
        }

        var layout = new UploadLayout(size, chunkSize, total);
        var maxBytes = await MaxUploadBytesAsync(ct);
        var userId = UserId;

        var run = finalizer.StartOrJoin(uploadId, (services, jobCt) => AssembleAsync(services,
            chunked => chunked.CompleteReplaceAsync(uploadId, layout, name, id, maxBytes, userId, jobCt)));

        return await UploadResults.AwaitOrAcceptAsync(run);
    }

    [HttpGet("media/{id:int}/replace/complete/status")]
    [IgnoreAntiforgeryToken]
    public IActionResult ReplaceCompleteStatus(int id, [FromQuery] string uploadId)
    {
        return UploadResults.Describe(finalizer.Find(uploadId));
    }

    // Email a published share's public link to one or more addresses (WeTransfer-style). Recipients need
    // no account. Owner-scoped, capped, and gated on the share being published and email being configured.
    [HttpPost("media/{id:int}/email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailShare(int id, string? recipients, string? message, CancellationToken ct)
    {
        const int maxRecipients = 10;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await OwnedMedia(db).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.BucketId == null, ct);
        if (item is null)
        {
            this.FlashError("That share no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        // Only email a link that will actually work for the recipient: the public share page 404s for
        // non-owners unless it's published, finished processing, and not past its expiry.
        if (!item.Published || item.Status != MediaStatus.Ready || Retention.IsExpired(item.ExpiresAt, DateTime.UtcNow))
        {
            this.FlashError("This share isn't publicly viewable yet - it needs to be published, finished processing, and not expired.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (!await emailSender.IsEnabledAsync(ct))
        {
            this.FlashError("Email isn't configured on this instance yet.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var addresses = (recipients ?? string.Empty)
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(LooksLikeEmail)
            .ToList();
        if (addresses.Count == 0)
        {
            this.FlashError("Enter at least one valid email address.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (addresses.Count > maxRecipients)
        {
            this.FlashError($"You can email up to {maxRecipients} addresses at once.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        var senderName = owner?.Name ?? owner?.Username ?? owner?.Email ?? "Someone";
        var link = config.PublicBaseUrl(Request) + ShareUrls.Path(item, owner?.Username, owner?.Role == UserRole.Admin);
        var note = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (note is { Length: > 500 })
        {
            note = note[..500];
        }

        var msg = await emailComposer.ShareLinkAsync(new ShareLinkEmail(senderName, item.Title, link, note));
        var sent = 0;
        foreach (var address in addresses)
        {
            if (await emailSender.SendAsync(address, msg.Subject, msg.Html, msg.Text, ct))
            {
                sent++;
            }
        }

        if (sent == addresses.Count)
        {
            this.FlashSuccess($"Link emailed to {sent} recipient{(sent == 1 ? "" : "s")}.");
        }
        else if (sent > 0)
        {
            this.FlashWarning($"Emailed {sent} of {addresses.Count}; the rest failed to send.");
        }
        else
        {
            this.FlashError("Could not send the email - check the SMTP settings.");
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    [HttpPost("media/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMedia(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await OwnedMedia(db).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            this.FlashInfo("That item was already deleted.");
            return RedirectToAction(nameof(Index));
        }

        var bucketId = item.BucketId;
        db.MediaItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await DeleteMediaFilesIfUnreferencedAsync(db, item, ct);

        // A drop-off is managed on its box page; a share on the dashboard. Return to whichever it was.
        if (bucketId is int bid)
        {
            this.FlashSuccess("File deleted.");
            return RedirectToAction(nameof(Bucket), new { id = bid });
        }

        this.FlashSuccess("Share deleted.");
        return RedirectToAction(nameof(Index));
    }

    // Drop an item's physical files once its row is gone and nothing else references them (dedup-safe).
    // The content file is keyed by hash+extension; the poster and web file by their own stored names.
    private async Task DeleteMediaFilesIfUnreferencedAsync(AppDbContext db, MediaItem m, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(x => x.ContentHash == m.ContentHash && x.Extension == m.Extension, ct))
        {
            await storage.DeleteAsync(m.ContentHash + m.Extension, ct);
        }

        if (m.PosterFileName is not null && !await db.MediaItems.AnyAsync(x => x.PosterFileName == m.PosterFileName, ct))
        {
            await storage.DeleteAsync(m.PosterFileName, ct);
        }

        if (m.WebFileName is not null && !await db.MediaItems.AnyAsync(x => x.WebFileName == m.WebFileName, ct))
        {
            await storage.DeleteAsync(m.WebFileName, ct);
        }
    }

    private static async Task<string> NewBucketSlugAsync(AppDbContext db)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var slug = SlugGenerator.New(8);
            if (!await db.Buckets.AnyAsync(b => b.Slug == slug))
            {
                return slug;
            }
        }

        return SlugGenerator.New(12);
    }
}
