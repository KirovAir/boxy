using Boxy.Data;
using Boxy.Data.Entities;

namespace Boxy.Web.Services;

/// <summary>Thrown when an upload exceeds the configured size cap. Carries the limit for the message.</summary>
public class UploadTooLargeException(long maxBytes) : Exception
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>
/// Turns an uploaded stream into a stored, deduplicated <see cref="MediaItem"/> and queues
/// it for background processing (probe/poster/transcode).
/// </summary>
public class IngestionService(
    IDbContextFactory<AppDbContext> dbFactory,
    IBlobStore storage,
    MediaProcessingQueue queue,
    QuotaService quota,
    ILogger<IngestionService> logger)
{
    public async Task<MediaItem> IngestAsync(
        UploadSource source,
        string originalFileName,
        int? bucketId,
        bool published,
        string? uploaderToken = null,
        int? ownerId = null,
        ConversionProfile profile = ConversionProfiles.Fallback,
        DateTime? expiresAt = null,
        long maxBytes = 0,
        int? quotaOwnerId = null,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var stored = await source.StoreAsync(storage, extension, ct);
        await EnforceSizeLimitAsync(stored, extension, maxBytes, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Every item has an owner: the signed-in uploader for a share, or the box owner for a drop-off.
        var ownerIdFinal = ownerId
            ?? await db.Buckets.Where(b => b.Id == bucketId).Select(b => (int?)b.OwnerId).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Cannot ingest into bucket {bucketId}: it has no owner.");

        // Dedup: the same uploader re-sending identical content to the same bucket returns the existing
        // item. Different uploaders each get their own item (scoped by uploader token), while the bytes
        // are still stored only once.
        var existing = await db.MediaItems.FirstOrDefaultAsync(
            m => m.BucketId == bucketId && m.ContentHash == stored.Hash
                                        && m.UploaderToken == uploaderToken && m.OwnerId == ownerIdFinal, ct);
        if (existing is not null)
        {
            logger.LogInformation("Dedup hit: {File} matches existing item {Slug}", originalFileName, existing.Slug);
            return existing;
        }

        // A genuinely new item counts against the responsible user's quota (the owner for a share, the
        // box owner for a drop-off). The check and the insert are held under one per-user lock so
        // concurrent uploads can't both slip past the cap; over-quota bytes are rolled back.
        MediaItem item;
        using (quotaOwnerId is int qo ? await quota.LockAsync(qo, ct) : null)
        {
            await EnforceQuotaAsync(quotaOwnerId, stored, extension, stored.Size, ct);

            item = new MediaItem
            {
                Slug = await NewUniqueSlugAsync(db, ct),
                BucketId = bucketId,
                Title = FallbackTitle(originalFileName),
                ContentHash = stored.Hash,
                OriginalFileName = originalFileName,
                Extension = extension,
                ContentType = ContentTypes.Guess(extension),
                Kind = MediaKinds.FacetOf(extension),
                SizeBytes = stored.Size,
                Status = MediaStatus.Uploaded,
                Published = published,
                UploaderToken = uploaderToken,
                OwnerId = ownerIdFinal,
                Profile = profile,
                ExpiresAt = expiresAt
            };

            db.MediaItems.Add(item);
            await db.SaveChangesAsync(ct);
        }

        queue.Enqueue(item.Id);
        // Drop-off notifications (webhook/email) are picked up by NotificationWorker, which scans for
        // boxes with drops past their watermark - no signalling from here needed.

        logger.LogInformation("Ingested {File} as {Slug} (bucket {Bucket}, deduped-storage={Dedup})",
            originalFileName, item.Slug, bucketId, stored.Deduped);
        return item;
    }

    /// <summary>
    /// Swap new bytes into an existing item, keeping its identity (slug, URL, title, views, likes) and
    /// re-deriving everything about the file. The old, now-unreferenced physical files are removed
    /// dedup-safely, and the item is re-queued for probe/poster/transcode. Returns null if the item is
    /// gone. Authorization is the caller's job.
    /// </summary>
    public async Task<MediaItem?> ReplaceAsync(int itemId, UploadSource source, string originalFileName, long maxBytes = 0, int? quotaOwnerId = null, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var stored = await source.StoreAsync(storage, extension, ct);
        await EnforceSizeLimitAsync(stored, extension, maxBytes, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item is null)
        {
            return null;
        }

        var (oldHash, oldExt, oldPoster, oldWeb) = (item.ContentHash, item.Extension, item.PosterFileName, item.WebFileName);

        // Only the growth over the current bytes counts against the quota (the item already occupies its
        // old size). The check and the save share the per-user lock so concurrent uploads can't both pass.
        using (quotaOwnerId is int qo ? await quota.LockAsync(qo, ct) : null)
        {
            await EnforceQuotaAsync(quotaOwnerId, stored, extension, stored.Size - item.SizeBytes, ct);

            // New bytes, new probe: reset every derived field so nothing stale survives the swap.
            item.ContentHash = stored.Hash;
            item.Extension = extension;
            item.ContentType = ContentTypes.Guess(extension);
            item.Kind = MediaKinds.FacetOf(extension);
            item.OriginalFileName = originalFileName;
            item.SizeBytes = stored.Size;
            item.Width = item.Height = null;
            item.DurationSeconds = null;
            item.VideoCodec = item.AudioCodec = null;
            item.CapturedAt = null;
            item.WebFileName = null;
            item.PosterFileName = null;
            item.Status = MediaStatus.Uploaded;
            item.ErrorMessage = null;

            await db.SaveChangesAsync(ct);
        }

        // Drop old artifacts only when nothing else references them. The content file's name is
        // hash+extension, so the check must match both: re-uploading identical bytes under a different
        // extension leaves this item on the same hash yet a new file, and the old one must still go.
        if (!await db.MediaItems.AnyAsync(m => m.ContentHash == oldHash && m.Extension == oldExt, ct))
        {
            await storage.DeleteAsync(oldHash + oldExt, ct);
        }

        if (oldPoster is not null && !await db.MediaItems.AnyAsync(m => m.PosterFileName == oldPoster, ct))
        {
            await storage.DeleteAsync(oldPoster, ct);
        }

        if (oldWeb is not null && !await db.MediaItems.AnyAsync(m => m.WebFileName == oldWeb, ct))
        {
            await storage.DeleteAsync(oldWeb, ct);
        }

        queue.Enqueue(item.Id);
        logger.LogInformation("Replaced item {Slug} with {File} (deduped-storage={Dedup})",
            item.Slug, originalFileName, stored.Deduped);
        return item;
    }

    // Reject an over-cap upload, dropping the bytes we just wrote (unless they were already stored for
    // another item). Runs after hashing, so it also covers the chunked path (which assembles first).
    private async Task EnforceSizeLimitAsync(StoredFile stored, string extension, long maxBytes, CancellationToken ct)
    {
        if (maxBytes > 0 && stored.Size > maxBytes)
        {
            if (!stored.Deduped)
            {
                await storage.DeleteAsync(stored.Hash + extension, ct);
            }

            throw new UploadTooLargeException(maxBytes);
        }
    }

    // Reject an upload that would push the responsible user past their quota, dropping the bytes we just
    // wrote (unless they were already stored for another item). No-op when there's no quota owner.
    private async Task EnforceQuotaAsync(int? quotaOwnerId, StoredFile stored, string extension, long additionalBytes, CancellationToken ct)
    {
        if (quotaOwnerId is not int owner || additionalBytes <= 0)
        {
            return;
        }

        try
        {
            await quota.EnsureRoomAsync(owner, additionalBytes, ct);
        }
        catch (QuotaExceededException)
        {
            if (!stored.Deduped)
            {
                await storage.DeleteAsync(stored.Hash + extension, ct);
            }

            throw;
        }
    }

    private static async Task<string> NewUniqueSlugAsync(AppDbContext db, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var slug = SlugGenerator.New();
            if (!await db.MediaItems.AnyAsync(m => m.Slug == slug, ct))
            {
                return slug;
            }
        }

        return SlugGenerator.New(14);
    }

    private static string FallbackTitle(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
    }
}
