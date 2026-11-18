using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Extensions;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

// Token API for scripts and AI agents: upload a file as a share, then poll it until its link works. A key
// only reaches the uploader's own shares, never boxes, drop-offs or settings. The guide at /api and the
// OpenAPI document describe it; both are public, the key is what's secret.
[Authorize(AuthenticationSchemes = ApiKeys.Scheme)]
[Route("api/v1")]
public class ApiV1Controller(
    IDbContextFactory<AppDbContext> dbFactory,
    IngestionService ingestion,
    VideoSettingsProvider videoSettings,
    IConfiguration config) : ControllerBase
{
    private const int PageSize = 50;

    private int UserId => User.GetUserId();

    [AllowAnonymous]
    [HttpGet("/api")]
    public IActionResult Guide()
    {
        return Content(ApiDocs.Guide(config.PublicBaseUrl(Request)), "text/markdown; charset=utf-8");
    }

    [AllowAnonymous]
    [HttpGet("/api/openapi.json")]
    public IActionResult OpenApi()
    {
        return Content(ApiDocs.OpenApi(config.PublicBaseUrl(Request)), "application/json; charset=utf-8");
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (me, settings) = await LoadAsync(db, ct);
        var isAdmin = me.Role == UserRole.Admin;
        var quota = isAdmin ? 0L : Math.Max(0L, me.QuotaBytes ?? settings.DefaultUserQuotaBytes);
        return Ok(new
        {
            name = me.Name ?? me.Username ?? me.Email,
            username = me.Username,
            maxUploadBytes = isAdmin ? 0L : settings.MaxUploadBytes,
            quotaBytes = quota,
            usedBytes = await QuotaService.UsageBytesAsync(db, UserId, ct)
        });
    }

    [HttpPost("files")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var file = Request.HasFormContentType ? Request.Form.Files.GetFile("file") ?? Request.Form.Files.FirstOrDefault() : null;
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "Send the file as multipart/form-data in a field named \"file\"." });
        }

        var conversion = Request.Form["conversion"].ToString();
        ConversionProfile? chosen = conversion switch
        {
            "convert" => ConversionProfile.Best,
            "repackage" => ConversionProfile.AsUploaded,
            _ => null
        };
        if (chosen is null && conversion.Length > 0)
        {
            return BadRequest(new { error = "conversion must be \"convert\" or \"repackage\"." });
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (me, settings) = await LoadAsync(db, ct);
        var isAdmin = me.Role == UserRole.Admin;
        var maxBytes = isAdmin ? 0L : settings.MaxUploadBytes;
        if (maxBytes > 0 && file.Length > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        var profile = ConversionProfiles.Resolve(chosen, null, (await videoSettings.GetEffectiveAsync(ct)).DefaultProfile);
        var expiry = Retention.ExpiryFor(isAdmin, settings.RetentionDays, DateTime.UtcNow);

        MediaItem item;
        try
        {
            await using var stream = file.OpenReadStream();
            item = await ingestion.IngestAsync(UploadSource.FromStream(stream), file.FileName, null, true, null, null,
                UserId, profile, expiry, maxBytes, UserId, ct);
        }
        catch (UploadTooLargeException ex)
        {
            return TooLarge(ex.MaxBytes);
        }
        catch (QuotaExceededException)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage,
                new { error = "Your storage quota is full. Delete something or ask an admin to raise it." });
        }
        catch (StorageFullException)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = "The server is out of storage space." });
        }

        var title = Request.Form["title"].ToString().Trim();
        if (title.Length > 0 && title != item.Title)
        {
            item = await db.MediaItems.FirstAsync(m => m.Id == item.Id, ct);
            item.Title = title;
            await db.SaveChangesAsync(ct);
        }

        return Created($"/api/v1/files/{item.Slug}", Describe(item, me));
    }

    [HttpGet("files")]
    public async Task<IActionResult> List(int page = 1, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (me, _) = await LoadAsync(db, ct);
        var query = db.MediaItems.AsNoTracking().Where(m => m.BucketId == null && m.OwnerId == UserId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.CreatedDate)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        return Ok(new
        {
            page,
            pageSize = PageSize,
            total,
            files = items.Select(m => Describe(m, me))
        });
    }

    [HttpGet("files/{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (me, _) = await LoadAsync(db, ct);
        var item = await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Slug == id && m.BucketId == null && m.OwnerId == UserId, ct);
        return item is null ? NotFound(new { error = "No share of yours with that id." }) : Ok(Describe(item, me));
    }

    private async Task<(User Me, PlatformSettings Settings)> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == UserId, ct);
        return (me, await db.GetSettingsAsync<PlatformSettings>(ct));
    }

    private object Describe(MediaItem m, User owner)
    {
        return new
        {
            id = m.Slug,
            title = m.Title,
            fileName = m.OriginalFileName,
            kind = m.Kind.ToString().ToLowerInvariant(),
            sizeBytes = m.SizeBytes,
            status = m.Status.ToString().ToLowerInvariant(),
            url = ShareUrls.Url(config.PublicBaseUrl(Request), m, owner),
            // SQLite hands dates back unlabelled; they are UTC, and an agent should be told so.
            expiresAt = m.ExpiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : (DateTime?)null,
            createdAt = DateTime.SpecifyKind(m.CreatedDate, DateTimeKind.Utc),
            error = m.ErrorMessage
        };
    }

    private ObjectResult TooLarge(long maxBytes)
    {
        return StatusCode(StatusCodes.Status413PayloadTooLarge,
            new { error = $"That file is over the {maxBytes / 1024 / 1024} MB upload limit." });
    }
}
