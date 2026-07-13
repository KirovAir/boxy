using System.Text.RegularExpressions;
using Boxy.Data.Entities;

namespace Boxy.Web.Services;

/// <summary>
/// Reliable, parallel large-file uploads. The client splits a file into fixed-size chunks and
/// uploads several at once; each chunk is stored as its own part file under a per-upload folder,
/// so chunks may arrive out of order and a dropped one is simply re-sent. On completion the parts
/// are concatenated in order and handed to <see cref="IngestionService"/> (hash/dedup/store/queue).
/// Built for multi-GB uploads over flaky/mobile links.
/// </summary>
public partial class ChunkedUploadService(
    IBlobStore storage,
    IngestionService ingestion,
    ILogger<ChunkedUploadService> logger)
{
    [GeneratedRegex("^[a-zA-Z0-9]{8,64}$")]
    private static partial Regex UploadIdPattern();

    /// <summary>Store one chunk (by index) as its own part file. Idempotent - a retry overwrites it.
    /// The chunk is written to a <c>.part</c> temp and atomically moved into place, so an interrupted
    /// write never leaves a truncated part that a resume would mistake for a complete one.</summary>
    public async Task WriteChunkAsync(string uploadId, int index, Stream chunk, long maxBytes = 0, CancellationToken ct = default)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var dir = PartDir(uploadId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, index.ToString());
        var tmp = path + ".part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await chunk.CopyToAsync(fs, ct);
            }

            File.Move(tmp, path, true);
        }
        catch
        {
            TryDeleteFile(tmp);
            throw;
        }

        // Stop a capped upload the moment its staged parts cross the limit, so a client that skips the
        // pre-check can't fill the temp dir before completion rejects it. Only capped uploads pay this.
        if (maxBytes > 0 && StagedBytes(dir) > maxBytes)
        {
            TryDeleteDir(dir);
            throw new UploadTooLargeException(maxBytes);
        }
    }

    private static long StagedBytes(string dir)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (int.TryParse(Path.GetFileName(file), out _))
            {
                total += new FileInfo(file).Length;
            }
        }

        return total;
    }

    /// <summary>The chunk indices already fully stored for this upload, so the client can resume an
    /// interrupted upload by sending only what's missing. Half-written <c>.part</c> temps don't parse
    /// as an index and are never reported.</summary>
    public IReadOnlyList<int> ExistingChunks(string uploadId)
    {
        var dir = PartDir(uploadId); // validates the id
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var indices = new List<int>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (int.TryParse(Path.GetFileName(file), out var idx) && idx >= 0)
            {
                indices.Add(idx);
            }
        }

        return indices;
    }

    /// <summary>Concatenate parts 0..total-1 in order and ingest them as a new item. Cleans up parts
    /// either way.</summary>
    public Task<MediaItem> CompleteAsync(
        string uploadId,
        int total,
        string fileName,
        int? bucketId,
        bool published,
        string? uploaderToken,
        int? ownerId = null,
        bool keepOriginal = false,
        DateTime? expiresAt = null,
        long maxBytes = 0,
        int? quotaOwnerId = null,
        CancellationToken ct = default)
    {
        return AssembleAndUseAsync(uploadId, total,
            assembled => ingestion.IngestAsync(assembled, fileName, bucketId, published, uploaderToken, ownerId, keepOriginal, expiresAt, maxBytes, quotaOwnerId, ct),
            ct);
    }

    /// <summary>Same assembly as <see cref="CompleteAsync"/>, but swaps the bytes into an existing item
    /// (keeping its slug, URL, and stats) instead of creating a new one. Returns null if the item is
    /// gone. Authorization is the caller's job.</summary>
    public Task<MediaItem?> CompleteReplaceAsync(string uploadId, int total, string fileName, int itemId, long maxBytes = 0, int? quotaOwnerId = null, CancellationToken ct = default)
    {
        return AssembleAndUseAsync(uploadId, total,
            assembled => ingestion.ReplaceAsync(itemId, assembled, fileName, maxBytes, quotaOwnerId, ct),
            ct);
    }

    /// <summary>Concatenate parts 0..total-1 in order into one stream, hand it to <paramref name="use"/>,
    /// then clean up the parts and the combined file regardless of outcome.</summary>
    private async Task<T> AssembleAndUseAsync<T>(string uploadId, int total, Func<Stream, Task<T>> use, CancellationToken ct)
    {
        var dir = PartDir(uploadId);
        if (total <= 0 || !Directory.Exists(dir))
        {
            throw new InvalidOperationException($"No upload in progress for id {uploadId}");
        }

        var combined = dir + ".combined";
        try
        {
            await using (var outFs = new FileStream(combined, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (var i = 0; i < total; i++)
                {
                    var part = Path.Combine(dir, i.ToString());
                    if (!File.Exists(part))
                    {
                        throw new InvalidOperationException($"Missing chunk {i} for upload {uploadId}");
                    }

                    await using var inFs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.None);
                    await inFs.CopyToAsync(outFs, ct);
                }
            }

            await using var assembled = new FileStream(combined, FileMode.Open, FileAccess.Read, FileShare.None);
            return await use(assembled);
        }
        finally
        {
            TryDeleteFile(combined);
            TryDeleteDir(dir);
        }
    }

    public void Abort(string uploadId)
    {
        var dir = PartDir(uploadId);
        TryDeleteFile(dir + ".combined");
        TryDeleteDir(dir);
    }

    private string PartDir(string uploadId)
    {
        if (!UploadIdPattern().IsMatch(uploadId))
        {
            logger.LogWarning("Rejected malformed upload id {UploadId}", uploadId);
            throw new ArgumentException("Invalid upload id", nameof(uploadId));
        }

        return Path.Combine(storage.ScratchDir, uploadId);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete scratch file {Path}", path);
        }
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete upload dir {Dir}", dir);
        }
    }
}
