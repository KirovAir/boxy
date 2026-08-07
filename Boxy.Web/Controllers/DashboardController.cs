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
    VideoSettingsProvider videoSettings,
    MediaProcessingQueue queue,
    IEmailSender emailSender,
    EmailComposer emailComposer,
    GeoLookup geo,
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
            DefaultProfile = (await videoSettings.GetEffectiveAsync(ct)).DefaultProfile,
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

    /// <summary>What to do with a video uploaded here: what the uploader picked, else the instance
    /// default. A dashboard upload is a share the uploader owns, so there is no box default in between.</summary>
    private async Task<ConversionProfile> ProfileForUploadAsync(ConversionProfile? chosen, CancellationToken ct = default)
    {
        var settings = await videoSettings.GetEffectiveAsync(ct);
        return ConversionProfiles.Resolve(chosen, null, settings.DefaultProfile);
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

    // Save what happens to videos dropped in this box. Null (the empty option) means "whatever the site
    // default is at the time", which is deliberately not snapshotted: an admin changing the site default
    // should move every box that hasn't made its own choice.
    [HttpPost("buckets/{id:int}/conversion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetConversion(int id, string? profile, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var box = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId, ct);
        if (box is null)
        {
            return RedirectToAction(nameof(Bucket), new { id });
        }

        box.DefaultProfile = ConversionProfiles.Parse(profile);
        await db.SaveChangesAsync(ct);
        this.FlashSuccess("Saved. It applies to videos dropped in from now on.");
        return RedirectToAction(nameof(Bucket), new { id });
    }

    // Open a box up into a shared gallery, or close it back to private-per-uploader. When on, every
    // visitor sees an "everyone's uploads" list on the drop-off page and can preview and download what
    // others dropped in; deleting stays limited to the uploader (and the owner) regardless. Owner-only.
    [HttpPost("buckets/{id:int}/sharing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSharing(int id, bool sharedView, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var box = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == UserId, ct);
        if (box is null)
        {
            return RedirectToAction(nameof(Bucket), new { id });
        }

        box.SharedView = sharedView;
        await db.SaveChangesAsync(ct);
        this.FlashSuccess(sharedView
            ? "Shared view is on. Everyone with the link now sees and can download every file in this box."
            : "Shared view is off. Each visitor sees only the files they dropped in.");
        return RedirectToAction(nameof(Bucket), new { id });
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
        var profile = await ProfileForUploadAsync(ConversionProfiles.Parse(Request.Form["profile"]), ct);
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
                await ingestion.IngestAsync(UploadSource.FromStream(stream), file.FileName, null, true, null, UserId, profile, expiry, maxBytes, UserId, ct);
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
    [RequestSizeLimit(ChunkedUploadService.MaxChunkBytes)]
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
        catch (ChunkTooLargeException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "That chunk is too large." });
        }
        catch (Exception ex) when (ex is Microsoft.AspNetCore.Http.BadHttpRequestException or OperationCanceledException or IOException)
        {
            // The client hung up mid-chunk (a dropped connection, or the uploader's stall-watchdog aborting a
            // stuck chunk to retry it). Not a server error - answer with a retryable status rather than 500.
            logger.LogDebug(ex, "Chunk {Index} for upload {UploadId} was interrupted by the client", index, uploadId);
            return StatusCode(StatusCodes.Status408RequestTimeout);
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
        [FromQuery] long size, [FromQuery] long chunkSize, [FromQuery] string? profile, CancellationToken ct)
    {
        var layout = new UploadLayout(size, chunkSize, total);
        var chosen = await ProfileForUploadAsync(ConversionProfiles.Parse(profile), ct);
        var expiry = await DefaultExpiryAsync(ct);
        var maxBytes = await MaxUploadBytesAsync(ct);
        var userId = UserId;

        var run = finalizer.StartOrJoin(uploadId, (services, jobCt) => AssembleAsync(services,
            async chunked => (MediaItem?)await chunked.CompleteAsync(uploadId, layout, name, null, true, null, userId, chosen, expiry, maxBytes, userId, jobCt)));

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
                await MediaBlobs.DeleteUnreferencedAsync(db, storage, queue, m, ct);
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

    // The share's view timeline: one entry per counted view (same rules as the counter: no owner
    // previews, no bots, no locked views). The counter predates the log, so an older share can show
    // more total views than logged entries - the page says so rather than pretend.
    [HttpGet("media/{id:int}/views")]
    public async Task<IActionResult> ViewLog(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await OwnedMedia(db).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (item is null || item.BucketId is not null)
        {
            return NotFound();
        }

        var views = await db.MediaViews.AsNoTracking()
            .Where(v => v.MediaItemId == id)
            .OrderByDescending(v => v.CreatedDate)
            .Take(ViewLogViewModel.Cap)
            .Select(v => new { v.CreatedDate, v.Ip })
            .ToListAsync();
        return View(new ViewLogViewModel
        {
            Item = item,
            Views = views.Select(v => new ViewLogRow(v.CreatedDate, v.Ip, geo.Locate(v.Ip))).ToList()
        });
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

        string? stalePoster = null;
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

            stalePoster = item.PosterFileName != newPoster ? item.PosterFileName : null;
            item.PosterFileName = newPoster;
        }

        await db.SaveChangesAsync(ct);

        // Drop the previous poster once nothing else references it (dedup-safe). After the save, the
        // worker's own order: the row must never point at a blob this method already deleted.
        if (stalePoster is not null
            && !await db.MediaItems.AnyAsync(m => m.PosterFileName == stalePoster && m.Id != id, ct))
        {
            await storage.DeleteAsync(stalePoster, ct);
        }

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

    // Re-run the conversion under a different profile. Needed because re-uploading the same file is a
    // no-op: ingestion dedups on content and hands back the item that already exists, so without this the
    // choice made at upload could never be taken back.
    //
    // The item stays Ready throughout, so the share keeps serving what it has for the whole re-encode and
    // nothing goes dark. It goes in the backfill lane for the same reason: nobody is waiting on it, and it
    // must not delay somebody's actual upload.
    [HttpPost("media/{id:int}/convert")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConvertMedia(int id, string? profile, CancellationToken ct)
    {
        if (ConversionProfiles.Parse(profile) is not { } chosen)
        {
            this.FlashError("Pick a conversion.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await OwnedMedia(db).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            return RedirectToAction(nameof(Index));
        }

        item.Profile = chosen;
        // Clear the tombstone, so an item that failed under the previous profile is genuinely reconsidered
        // rather than skipped as already-hopeless.
        item.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        // The item stays Ready (its share keeps playing), so status alone wouldn't tell the edit page a
        // re-encode is happening. Enqueuing marks it pending, which the status endpoint surfaces as "Queued"
        // from the click, then the worker's live report takes over. No request-thread write to the progress
        // store, so there's no race with the worker clearing it.
        queue.EnqueueBackfill(item.Id);

        this.FlashSuccess($"Converting again as “{ConversionProfiles.Label(chosen)}”. It keeps playing until the new version is ready.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    // The owner supplies the web version themselves, encoded at home on a fast machine, instead of making
    // this box grind through a long transcode. Accepted only when it already meets the universal contract
    // (H.264 8-bit 4:2:0, AAC/MP3 or no audio) and matches the source's duration, then stored under the
    // exact name the worker would have produced - which is what keeps every existing path working: the
    // startup heal accepts it, a later "convert again" under the same profile reuses it, and a profile
    // switch replaces and cleans it up like any other rendition.
    [HttpPost("media/{id:int}/webversion")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadWebVersion(int id, IFormFile? file, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await OwnedMedia(db).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            this.FlashError("That item no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        if (file is null || file.Length == 0)
        {
            this.FlashWarning("Choose a file to upload.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (item.Kind != MediaKind.Video)
        {
            this.FlashError("This only applies to videos.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Racing the worker would mean both sides writing the item's rendition fields; whoever saves
        // last wins and the other's file leaks. Status alone doesn't cover it: a "convert again"
        // backfill keeps the item Ready the whole run, so ask the queue too. Failed is fine - this is
        // exactly the rescue for a transcode that timed out.
        if (item.Status is MediaStatus.Uploaded or MediaStatus.Processing || queue.IsPending(item.Id))
        {
            this.FlashWarning("This video is still converting - wait for that to finish first.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Identical bytes are stored once, renditions included: a hand-in would swap what every item
        // on this hash serves, not just this one. Refuse rather than silently rewrite another share.
        // Accepted: the refusal confirms identical bytes exist somewhere on the instance. The caller
        // already holds the exact file, so the signal is thin; per-item rendition names would close it.
        if (await db.MediaItems.AnyAsync(m => m.Id != id && m.ContentHash == item.ContentHash, ct))
        {
            this.FlashError("Another item holds this exact file (identical bytes), and a handed-in version would change what it plays too. Remove the duplicate first.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var maxBytes = await MaxUploadBytesAsync(ct);
        if (maxBytes > 0 && file.Length > maxBytes)
        {
            this.FlashError($"That file is over the {MbLabel(maxBytes)} upload limit.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var scratch = storage.ScratchDir;
        var tmpIn = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        var tmpOut = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var fs = System.IO.File.Create(tmpIn))
            {
                await file.CopyToAsync(fs, ct);
            }

            var (made, error) = await StageRenditionAsync(tmpIn, tmpOut, item,
                p => MediaProcessor.CanStreamCopyToMp4(p.VideoCodec, p.AudioCodec, p.PixFmt),
                MediaProcessor.UniversalCodecs,
                "That file isn't universally playable. Encode it as H.264 (8-bit 4:2:0) with AAC or MP3 audio and try again.", ct);
            if (made is null)
            {
                this.FlashError(error!);
                return RedirectToAction(nameof(Edit), new { id });
            }

            // The remux took a while for a big file; if a conversion was queued or started in the
            // meantime, or a dedup twin appeared, back off rather than overwrite what they rely on.
            if (queue.IsPending(item.Id)
                || await db.MediaItems.AnyAsync(m => m.Id != id && m.ContentHash == item.ContentHash, ct))
            {
                this.FlashWarning("This video changed while the file uploaded (a conversion, or a duplicate appeared). Try again in a moment.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            // "Don't convert it" has no web lane to swap - but an owner-supplied H.264 is the missing
            // piece of the Best shape, with zero server encoding: this file becomes the universal lane
            // and the original (H.265 and all) goes back to being offered first to devices that take it.
            // The worker settles that second part via a backfill, because it needs the original (a big
            // download on a remote store, so not for a request thread) - and for THIS item that is only
            // probe, validate and stream-copy work: it adopts the file stored here, never re-encodes.
            if (!ConversionProfiles.Transcodes(item.Profile))
            {
                // The backfill below runs under the global conversion ceiling; with Remux/Off it would
                // collapse the profile straight back to as-uploaded and never adopt this file. Refuse up
                // front instead of storing a rendition nothing will ever reference.
                if ((await videoSettings.GetEffectiveAsync(ct)).ConversionMode != ConversionMode.Full)
                {
                    this.FlashError("The server-wide conversion mode doesn't build web versions right now, so a handed-in one would never be used. Switch video settings to full conversion first.");
                    return RedirectToAction(nameof(Edit), new { id });
                }

                var bestName = item.ContentHash + ConversionProfiles.WebSuffix(ConversionProfile.Best);
                await storage.PutAsync(bestName, tmpOut, ct);
                item.Profile = ConversionProfile.Best;
                item.ErrorMessage = null;
                await db.SaveChangesAsync(ct);
                queue.EnqueueBackfill(item.Id);

                logger.LogInformation("Owner-supplied web version for {Slug} (was as-uploaded): {Codec} {Width}x{Height}",
                    item.Slug, made.VideoCodec, made.Width, made.Height);
                this.FlashSuccess("Web version saved. Browsers that can't play the original get your H.264; the original is offered first where it plays. Settling now, no re-encode.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Safari's HLS copy must change the moment the mp4 does: repackage it from the exact bytes
            // about to be stored - they are still local here - or clear it, so Safari falls back to
            // progressive rather than keep serving the old video. The H.265 package is untouched: that
            // rendition didn't change.
            item.HlsCodecs = (await videoSettings.GetEffectiveAsync(ct)).EnableHls
                ? await RepackageHlsAsync(item, tmpOut, ConversionProfiles.HlsWebVariant, MediaProcessor.UniversalCodecs, ct)
                : null;
            item.HlsWebStem = item.HlsCodecs is null
                ? null
                : ConversionProfiles.HlsStem(ConversionProfiles.HlsWebVariant, item.Profile);
            if (item.HlsCodecs is null)
            {
                item.HlsHqCodecs = null; // no master playlist without the floor, so don't advertise the rest
            }

            var webName = item.ContentHash + ConversionProfiles.WebSuffix(item.Profile);
            var webSize = new FileInfo(tmpOut).Length; // before PutAsync - it moves the file away
            await storage.PutAsync(webName, tmpOut, ct);

            var (oldWeb, oldHq) = (item.WebFileName, item.HqFileName);
            item.WebFileName = webName;
            item.WebCodec = made.VideoCodec;
            item.WebSizeBytes = webSize;
            item.WebWidth = made.Width;
            item.WebHeight = made.Height;
            item.WebEncoder = "uploaded";
            item.EncodeCrf = null;
            item.EncodePreset = null;
            item.EncodeToneMapped = false;
            item.EncodeMs = null;
            item.Status = MediaStatus.Ready;
            item.ErrorMessage = null;

            // A profile that offers no H.265 rendition must not keep advertising one. It can be here as a
            // leftover of a failed switch away from Best (the fail path restores the old renditions), and
            // this upload is exactly the rescue for that failure - leaving it would keep offering the H.265
            // file and make the startup heal requeue the item on every boot.
            if (!ConversionProfiles.WantsHq(item.Profile))
            {
                item.HqFileName = null;
                item.HqCodecs = null;
                item.HqSizeBytes = null;
                // The master playlist offers the H.265 variant off HlsHqCodecs alone; a profile with no
                // H.265 lane must stop advertising it, and clearing this lets the sweep reclaim the pair.
                item.HlsHqCodecs = null;
            }

            // Last gate, at the save itself: the repackage and store above can take minutes, and a
            // conversion enqueued in that window would fight these columns. The blobs already hold the
            // new bytes; bailing keeps the columns consistent and the queued run settles from there.
            if (queue.IsPending(item.Id))
            {
                this.FlashWarning("A conversion was queued while this uploaded and will settle shortly. Check the result and upload again if needed.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            await db.SaveChangesAsync(ct);

            // Drop what this replaced when it lived under another name (a legacy suffix, or an earlier
            // profile's lane) and nothing else references it - the worker's own cleanup rules.
            foreach (var stale in new[] { oldWeb, oldHq })
            {
                if (stale is null || stale == item.WebFileName || stale == item.HqFileName
                    || !ConversionProfiles.IsDerivedRendition(stale))
                {
                    continue;
                }

                if (!await db.MediaItems.AnyAsync(m => m.WebFileName == stale || m.HqFileName == stale, ct))
                {
                    await storage.DeleteAsync(stale, ct);
                }
            }

            await MediaBlobs.DeleteUnreferencedHlsAsync(db, storage, item.Id, item.ContentHash,
                item.HlsWebStem, item.HlsHqCodecs is not null, ct);

            logger.LogInformation("Owner-supplied web version for {Slug}: {Codec} {Width}x{Height} {Bytes} bytes",
                item.Slug, made.VideoCodec, made.Width, made.Height, item.WebSizeBytes);
            this.FlashSuccess("Web version replaced. The share now plays the file you uploaded.");
        }
        finally
        {
            TryDeleteLocal(tmpIn);
            TryDeleteLocal(tmpOut);
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // Same idea, for the better rendition: a source with no H.265 of its own (AV1, VP9, plain H.264) gets
    // nothing offered ahead of the H.264 copy, but the owner can encode one at home and hand it in here.
    // Stored under the worker's own name ({hash}-hevc.mp4), and ProduceHqAsync's reuse branch runs before
    // its source gate precisely so a reprocess adopts this file instead of throwing it away.
    [HttpPost("media/{id:int}/hqversion")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadHqVersion(int id, IFormFile? file, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await OwnedMedia(db).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            this.FlashError("That item no longer exists.");
            return RedirectToAction(nameof(Index));
        }

        if (file is null || file.Length == 0)
        {
            this.FlashWarning("Choose a file to upload.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Only "Best" advertises a second file; on any other profile the heal would rightly flag it.
        if (item.Kind != MediaKind.Video || !ConversionProfiles.WantsHq(item.Profile))
        {
            this.FlashError($"The H.265 rendition is only offered on “{ConversionProfiles.Label(ConversionProfile.Best)}”.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (item.Status is MediaStatus.Uploaded or MediaStatus.Processing || queue.IsPending(item.Id))
        {
            this.FlashWarning("This video is still converting - wait for that to finish first.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Same twin rule as the web version: shared bytes mean shared renditions, so a hand-in here
        // would swap what another item offers too.
        if (await db.MediaItems.AnyAsync(m => m.Id != id && m.ContentHash == item.ContentHash, ct))
        {
            this.FlashError("Another item holds this exact file (identical bytes), and a handed-in version would change what it plays too. Remove the duplicate first.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var maxBytes = await MaxUploadBytesAsync(ct);
        if (maxBytes > 0 && file.Length > maxBytes)
        {
            this.FlashError($"That file is over the {MbLabel(maxBytes)} upload limit.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var scratch = storage.ScratchDir;
        var tmpIn = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        var tmpOut = Path.Combine(scratch, $"tmp_{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var fs = System.IO.File.Create(tmpIn))
            {
                await file.CopyToAsync(fs, ct);
            }

            var (made, error) = await StageRenditionAsync(tmpIn, tmpOut, item,
                p => MediaProcessor.CanKeepAsHq(p.VideoCodec, p.AudioCodec, p.PixFmt),
                MediaProcessor.HqCodecSet,
                "That file isn't an H.265 video we can offer. Encode it as H.265 4:2:0 (8- or 10-bit) with AAC or MP3 audio and try again.", ct);
            if (made is null)
            {
                this.FlashError(error!);
                return RedirectToAction(nameof(Edit), new { id });
            }

            // A source we can't describe exactly is one we never offer: browsers only SKIP the H.265 file
            // when the codecs string tells them precisely what it is. The remux forced the hvc1 tag, so
            // this only fails on an exotic profile (Rext, 4:4:4 snuck past as e.g. a mislabeled stream).
            if (MediaProcessor.HevcCodecs(made) is not { } codecs)
            {
                this.FlashError("That H.265 file uses a profile browsers can't be told about, so it can't be offered safely. Encode it as Main or Main 10.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            if (queue.IsPending(item.Id)
                || await db.MediaItems.AnyAsync(m => m.Id != id && m.ContentHash == item.ContentHash, ct))
            {
                this.FlashWarning("This video changed while the file uploaded (a conversion, or a duplicate appeared). Try again in a moment.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Safari's HLS copy of this rendition must change along with it; a new package can only ride
            // in a master playlist that exists, so without the web variant there is nothing to update.
            item.HlsHqCodecs = item.HlsCodecs is not null && (await videoSettings.GetEffectiveAsync(ct)).EnableHls
                ? await RepackageHlsAsync(item, tmpOut, ConversionProfiles.HlsHqVariant, MediaProcessor.HqCodecSet, ct)
                : null;

            var hqName = item.ContentHash + ConversionProfiles.HqSuffix;
            var hqSize = new FileInfo(tmpOut).Length; // before PutAsync - it moves the file away
            await storage.PutAsync(hqName, tmpOut, ct);

            var oldHq = item.HqFileName;
            item.HqFileName = hqName;
            item.HqCodecs = codecs;
            item.HqSizeBytes = hqSize;
            // Same last gate as the web version: don't let these columns race a just-queued conversion.
            if (queue.IsPending(item.Id))
            {
                this.FlashWarning("A conversion was queued while this uploaded and will settle shortly. Check the result and upload again if needed.");
                return RedirectToAction(nameof(Edit), new { id });
            }

            await db.SaveChangesAsync(ct);

            // The old HQ file can be the ORIGINAL blob (an upload that already was a faststart hvc1 mp4);
            // IsDerivedRendition is what keeps that one safe from this cleanup.
            if (oldHq is not null && oldHq != hqName && ConversionProfiles.IsDerivedRendition(oldHq)
                && !await db.MediaItems.AnyAsync(m => m.WebFileName == oldHq || m.HqFileName == oldHq, ct))
            {
                await storage.DeleteAsync(oldHq, ct);
            }

            await MediaBlobs.DeleteUnreferencedHlsAsync(db, storage, item.Id, item.ContentHash,
                item.HlsWebStem, item.HlsHqCodecs is not null, ct);

            logger.LogInformation("Owner-supplied H.265 rendition for {Slug}: {Codecs} {Bytes} bytes",
                item.Slug, codecs, hqSize);
            this.FlashSuccess("H.265 version saved. Devices that can decode it now take it ahead of the H.264 copy.");
        }
        finally
        {
            TryDeleteLocal(tmpIn);
            TryDeleteLocal(tmpOut);
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Repackage one HLS variant from a freshly staged rendition (still local), mirroring the worker's
    /// packaging bar: package, validate, store media before playlist. Returns the variant's CODECS value,
    /// or null when anything declined - the caller then simply stops advertising HLS and the share plays
    /// progressive, exactly like a video the worker never packaged.
    /// </summary>
    private async Task<string?> RepackageHlsAsync(MediaItem item, string localSource, string variant,
        IReadOnlyCollection<string> allowedCodecs, CancellationToken ct)
    {
        var workDir = Path.Combine(storage.ScratchDir, $"tmp_hls_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workDir);
            var playlist = Path.Combine(workDir, variant + ".m3u8");
            if (!await processor.PackageHlsAsync(localSource, workDir, variant, variant, ct)
                || await processor.ValidateHlsAsync(playlist, item.DurationSeconds, allowedCodecs, ct) is not { } packaged
                || MediaProcessor.HlsVariantCodecs(packaged) is not { } codecs)
            {
                return null;
            }

            var stem = ConversionProfiles.HlsStem(variant, item.Profile);
            await storage.PutAsync(ConversionProfiles.HlsMediaName(item.ContentHash, stem),
                Path.Combine(workDir, variant + ".m4s"), ct);
            await storage.PutAsync(ConversionProfiles.HlsPlaylistName(item.ContentHash, stem), playlist, ct);
            return codecs;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
            }
            catch
            {
                /* best-effort scratch cleanup; the periodic sweep catches leftovers */
            }
        }
    }

    /// <summary>
    /// Holds an owner-supplied rendition to the same bar as a worker-made file: the caller's codec gate on
    /// the probe, then the same lossless faststart remux every produced file goes through (whatever
    /// container it arrived in), then <see cref="MediaProcessor.ValidateWebOutputAsync"/> - including the
    /// duration check against the original, here enforced BOTH ways, because user input can be the wrong
    /// video entirely and a longer one sails past the validator's truncation-only rule. Returns the probe
    /// of the accepted mp4 now sitting at <paramref name="tmpOut"/>, or an error to flash.
    /// </summary>
    private async Task<(ProbeResult? Made, string? Error)> StageRenditionAsync(string tmpIn, string tmpOut,
        MediaItem item, Func<ProbeResult, bool> accepts, IReadOnlyCollection<string> allowedCodecs,
        string codecError, CancellationToken ct)
    {
        var probe = await processor.ProbeAsync(tmpIn, ct);
        if (probe is null || !accepts(probe))
        {
            return (null, codecError);
        }

        if (!await processor.RemuxFastStartAsync(tmpIn, tmpOut, probe.VideoCodec, ct)
            || await processor.ValidateWebOutputAsync(tmpOut, item.DurationSeconds, allowedCodecs, ct) is not { } made)
        {
            return (null, "That file couldn't be used - make sure it's the same video, fully encoded (its length must match the original).");
        }

        var madeDuration = made.VideoDuration ?? made.Duration;
        if (item.DurationSeconds is > 1 && madeDuration is { } dur
            && dur > item.DurationSeconds.Value + Math.Max(1.0, item.DurationSeconds.Value * 0.04))
        {
            return (null, "That file couldn't be used - it's longer than the original, so it doesn't look like the same video.");
        }

        return (made, null);
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
        await MediaBlobs.DeleteUnreferencedAsync(db, storage, queue, item, ct);

        // A drop-off is managed on its box page; a share on the dashboard. Return to whichever it was.
        if (bucketId is int bid)
        {
            this.FlashSuccess("File deleted.");
            return RedirectToAction(nameof(Bucket), new { id = bid });
        }

        this.FlashSuccess("Share deleted.");
        return RedirectToAction(nameof(Index));
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
