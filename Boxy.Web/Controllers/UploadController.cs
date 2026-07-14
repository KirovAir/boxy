using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

/// <summary>
/// Public, unauthenticated drop-off at <c>/u/{slug}</c>. Anyone with the bucket URL can upload;
/// there is intentionally no antiforgery/session gate. A first visit mints a long-lived
/// <c>boxy_uid</c> cookie that acts as an anonymous identity so a visitor can later delete their
/// own uploads. Uploads use the chunked engine (JS) with a plain-form fallback (no JS).
/// </summary>
[IgnoreAntiforgeryToken]
public class UploadController(
    IDbContextFactory<AppDbContext> dbFactory,
    IngestionService ingestion,
    ChunkedUploadService chunked,
    IBlobStore storage,
    ILogger<UploadController> logger) : Controller
{
    // no-store: the HTML must never be cached, so mobile browsers always load the current
    // page (and thus the current, version-stamped upload.js) rather than a stale copy.
    [HttpGet("/u/{slug}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(string slug, [FromQuery] int ok = 0)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null)
        {
            return NotFound();
        }

        var token = GetOrCreateUploaderToken();
        await using var db = await dbFactory.CreateDbContextAsync();
        var mine = await db.MediaItems.AsNoTracking()
            .Where(m => m.BucketId == bucket.Id && m.UploaderToken == token)
            .OrderByDescending(m => m.CreatedDate)
            .Take(200)
            .ToListAsync();

        return View(new UploadPageViewModel
        {
            BucketName = bucket.Name,
            BucketSlug = bucket.Slug,
            IsOpen = bucket.IsOpen,
            UploadedCount = ok,
            MyUploads = mine,
            MaxBytes = await MaxUploadBytesAsync(bucket)
        });
    }

    // ── No-JS fallback: plain multipart form post ─────────────────────────────
    [HttpPost("/u/{slug}")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Submit(string slug, CancellationToken ct)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null || !bucket.IsOpen)
        {
            return NotFound();
        }

        var token = GetOrCreateUploaderToken();
        var maxBytes = await MaxUploadBytesAsync(bucket);
        var count = 0;
        foreach (var file in Request.Form.Files)
        {
            if (file.Length == 0 || (maxBytes > 0 && file.Length > maxBytes))
            {
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();
                await ingestion.IngestAsync(UploadSource.FromStream(stream), file.FileName, bucket.Id, false, token, maxBytes: maxBytes, quotaOwnerId: bucket.OwnerId, ct: ct);
                count++;
            }
            catch (QuotaExceededException)
            {
                // The box owner is out of room for this file; a smaller later one may still fit.
                continue;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upload failed for {File} into bucket {Bucket}", file.FileName, bucket.Id);
            }
        }

        return RedirectToAction(nameof(Index), new { slug, ok = count });
    }

    // ── Chunked engine (JS): parallel, out-of-order chunks by index ───────────
    [HttpPost("/api/u/{slug}/chunk")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Chunk(string slug, [FromQuery] string uploadId, [FromQuery] int index, CancellationToken ct)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null || !bucket.IsOpen)
        {
            return NotFound();
        }

        try
        {
            await chunked.WriteChunkAsync(uploadId, index, Request.Body, await MaxUploadBytesAsync(bucket), ct);
            return Ok();
        }
        catch (UploadTooLargeException ex)
        {
            return BadRequest(new { error = $"That file is over the {ex.MaxBytes / 1024 / 1024} MB limit for this box." });
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    // Which chunk indices are already stored - lets the client resume an interrupted upload by
    // re-sending only the missing chunks. The size/chunkSize the client is working to are required, so a
    // part is only ever reported as present when its length matches the slot the client would put it in.
    [HttpGet("/api/u/{slug}/chunks")]
    public async Task<IActionResult> Chunks(string slug, [FromQuery] string uploadId, [FromQuery] long size, [FromQuery] long chunkSize)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null || !bucket.IsOpen)
        {
            return NotFound();
        }

        try
        {
            return Json(new { have = chunked.ExistingChunks(uploadId, size, chunkSize) });
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    // Finalize: assemble the parts, ingest, dedup, queue. Returns the new item's slug.
    [HttpPost("/api/u/{slug}/complete")]
    public async Task<IActionResult> Complete(string slug, [FromQuery] string uploadId, [FromQuery] int total,
        [FromQuery] string name, [FromQuery] long size, [FromQuery] long chunkSize, CancellationToken ct)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null || !bucket.IsOpen)
        {
            return NotFound();
        }

        var token = GetOrCreateUploaderToken();
        var layout = new UploadLayout(size, chunkSize, total);
        try
        {
            var item = await chunked.CompleteAsync(uploadId, layout, name, bucket.Id, false, token, maxBytes: await MaxUploadBytesAsync(bucket), quotaOwnerId: bucket.OwnerId, ct: ct);
            return Json(new { slug = item.Slug, title = item.Title });
        }
        catch (UploadTooLargeException ex)
        {
            return BadRequest(new { error = $"That file is over the {ex.MaxBytes / 1024 / 1024} MB limit for this box." });
        }
        catch (QuotaExceededException)
        {
            return BadRequest(new { error = "This box is full - the owner is out of storage space." });
        }
        catch (UploadIncompleteException ex)
        {
            // The staged parts don't add up to the file the client described. They're gone now, so tell the
            // client to forget its resume state and send the file again from the start.
            logger.LogWarning(ex, "Discarded incomplete upload {UploadId} into bucket {Bucket}", uploadId, bucket.Id);
            return BadRequest(new { error = "That upload didn't arrive intact. Please pick the file again.", restart = true });
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { error = "Upload session not found.", restart = true });
        }
    }

    // Cancel an in-progress upload: discard its parts.
    [HttpPost("/api/u/{slug}/abort")]
    public IActionResult Abort(string slug, [FromQuery] string uploadId)
    {
        try
        {
            chunked.Abort(uploadId);
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        return Ok();
    }

    // ── Delete one of the visitor's own uploads (cookie-verified) ─────────────
    [HttpPost("/u/{slug}/delete/{mediaSlug}")]
    public async Task<IActionResult> DeleteMine(string slug, string mediaSlug)
    {
        var token = CurrentUploaderToken();
        if (token is null)
        {
            return Forbid();
        }

        var bucket = await FindBucketAsync(slug);
        if (bucket is null)
        {
            return NotFound();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Slug == mediaSlug && m.BucketId == bucket.Id && m.UploaderToken == token);
        if (item is null)
        {
            return NotFound();
        }

        await DeleteItemAsync(db, item);
        return RedirectToAction(nameof(Index), new { slug });
    }

    // Server-rendered "your uploads" row for one of the uploader's own files, so upload.js can insert the
    // finished row (with its icon/metadata) instead of rebuilding the markup - one source of truth.
    [HttpGet("/u/{slug}/mine/{mediaSlug}/row")]
    public async Task<IActionResult> MineRow(string slug, string mediaSlug, [FromServices] PartialRenderer renderer)
    {
        var token = CurrentUploaderToken();
        if (token is null)
        {
            return NotFound();
        }

        var bucket = await FindBucketAsync(slug);
        if (bucket is null)
        {
            return NotFound();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Slug == mediaSlug && m.BucketId == bucket.Id && m.UploaderToken == token);
        if (item is null)
        {
            return NotFound();
        }

        var html = await renderer.RenderAsync(ControllerContext, "_MineRow", new MineRowVm(item, bucket.Slug));
        return Content(html, "text/html");
    }

    private async Task DeleteItemAsync(AppDbContext db, MediaItem item)
    {
        // Only remove physical files when no other item references the same content (dedup-safe).
        var shared = await db.MediaItems.AnyAsync(m => m.ContentHash == item.ContentHash && m.Id != item.Id);
        if (!shared)
        {
            await storage.DeleteAsync(item.ContentHash + item.Extension);
            if (item.PosterFileName is not null)
            {
                await storage.DeleteAsync(item.PosterFileName);
            }

            if (item.WebFileName is not null)
            {
                await storage.DeleteAsync(item.WebFileName);
            }
        }

        db.MediaItems.Remove(item);
        await db.SaveChangesAsync();
    }

    private string GetOrCreateUploaderToken()
    {
        return UploaderCookie.GetOrCreate(HttpContext);
    }

    private string? CurrentUploaderToken()
    {
        return UploaderCookie.Current(HttpContext);
    }

    private async Task<Bucket?> FindBucketAsync(string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var bucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slug);
        // An expired box is link-off: every public endpoint here treats it as gone. The owner still
        // sees it on their dashboard (and can restore it) until the grace window deletes it.
        return bucket is null || Retention.IsExpired(bucket.ExpiresAt, DateTime.UtcNow) ? null : bucket;
    }

    /// <summary>The upload cap a drop-off into this box must respect - the box owner's limit (0 when the
    /// owner is an admin, or no cap is set).</summary>
    private async Task<long> MaxUploadBytesAsync(Bucket bucket)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == bucket.OwnerId);
        if (owner?.Role == UserRole.Admin)
        {
            return 0;
        }

        return (await db.GetSettingsAsync<PlatformSettings>()).MaxUploadBytes;
    }
}
