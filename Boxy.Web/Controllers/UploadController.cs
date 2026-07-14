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
    UploadFinalizer finalizer,
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

        // Note: the body is already on disk by the time we get here. MVC's form value provider reads the
        // multipart during model binding, so an oversized file is buffered before file.Length can reject it,
        // and the request as a whole is unbounded. Capping it has to happen in a resource filter, ahead of
        // model binding - it can't be done from here. The chunked engine below is the path browsers actually
        // take and is bounded properly; this fallback is only reachable with JavaScript off.
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
    // Bounded, unlike the whole-file endpoints: a chunk is 16 MB by construction, and this endpoint is open
    // to anyone with the box's link, so there's no reason to let one request write an unbounded body.
    [HttpPost("/api/u/{slug}/chunk")]
    [RequestSizeLimit(ChunkedUploadService.MaxChunkBytes)]
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
        catch (StorageFullException)
        {
            return StorageFull();
        }
        catch (ChunkTooLargeException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "That chunk is too large." });
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    private ObjectResult StorageFull()
    {
        return new ObjectResult(new { error = "This server is out of storage space. Try again later." })
        {
            StatusCode = StatusCodes.Status507InsufficientStorage
        };
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

    // Finalize: assemble the parts, ingest, dedup, queue. The assembly runs detached from this request (a
    // multi-GB concatenate outlasts any proxy's patience), so this either answers with the finished item or
    // hands back a 202 for the client to poll on.
    [HttpPost("/api/u/{slug}/complete")]
    public async Task<IActionResult> Complete(string slug, [FromQuery] string uploadId, [FromQuery] int total,
        [FromQuery] string name, [FromQuery] long size, [FromQuery] long chunkSize)
    {
        var bucket = await FindBucketAsync(slug);
        if (bucket is null || !bucket.IsOpen)
        {
            return NotFound();
        }

        var token = GetOrCreateUploaderToken();
        var layout = new UploadLayout(size, chunkSize, total);
        var maxBytes = await MaxUploadBytesAsync(bucket);
        var (bucketId, ownerId) = (bucket.Id, bucket.OwnerId);

        var run = finalizer.StartOrJoin(uploadId, (services, ct) => AssembleAsync(services,
            chunked => chunked.CompleteAsync(uploadId, layout, name, bucketId, false, token, maxBytes: maxBytes, quotaOwnerId: ownerId, ct: ct)));

        return await UploadResults.AwaitOrAcceptAsync(run);
    }

    // How an upload that's still being assembled is getting on.
    [HttpGet("/api/u/{slug}/complete/status")]
    public async Task<IActionResult> CompleteStatus(string slug, [FromQuery] string uploadId)
    {
        var bucket = await FindBucketAsync(slug);
        return bucket is null || !bucket.IsOpen ? NotFound() : UploadResults.Describe(finalizer.Find(uploadId));
    }

    // Runs the assembly and turns every way it can fail into something the client can act on. Any failure
    // has already discarded the staged parts, so all of these are "start over" answers.
    private async Task<UploadOutcome> AssembleAsync(IServiceProvider services, Func<ChunkedUploadService, Task<MediaItem>> assemble)
    {
        try
        {
            var item = await assemble(services.GetRequiredService<ChunkedUploadService>());
            return UploadOutcome.Done(item.Slug, item.Title);
        }
        catch (UploadTooLargeException ex)
        {
            return UploadOutcome.Failed($"That file is over the {ex.MaxBytes / 1024 / 1024} MB limit for this box.");
        }
        catch (QuotaExceededException)
        {
            return UploadOutcome.Failed("This box is full - the owner is out of storage space.");
        }
        catch (StorageFullException)
        {
            return UploadOutcome.Failed("This server is out of storage space. Try again later.");
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

    // Cancel an in-progress upload: discard its parts. Refused once the assembly has started, because
    // deleting the parts out from under it would fail the upload the user is waiting on.
    [HttpPost("/api/u/{slug}/abort")]
    public IActionResult Abort(string slug, [FromQuery] string uploadId)
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
