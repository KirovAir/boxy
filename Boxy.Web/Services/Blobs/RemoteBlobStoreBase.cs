using System.Security.Cryptography;

namespace Boxy.Web.Services;

/// <summary>
/// Shared logic for remote blob backends (S3, Azure). Content addressing, dedup, the local scratch area,
/// serving descriptors, and pulling a local copy for ffmpeg all live here; a concrete backend only
/// implements the primitive object operations (exists / upload / open / stat / range / delete).
/// Working files still live on local disk under <see cref="ScratchDir"/> - only finished content is remote.
/// </summary>
public abstract class RemoteBlobStoreBase(string scratchRoot, ILogger logger) : IBlobStore
{
    /// <summary>Logger for subclasses (e.g. best-effort delete failures).</summary>
    protected ILogger Logger => logger;

    /// <summary>Create the bucket/container if it doesn't exist. Called once at startup.</summary>
    public abstract Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>Total physical footprint (listed from the backend).</summary>
    public abstract Task<BlobUsage> GetUsageAsync(CancellationToken ct = default);

    public string ScratchDir
    {
        get
        {
            var dir = Path.Combine(scratchRoot, "_tmp");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ── Primitives implemented per backend ────────────────────────────────
    protected abstract Task UploadAsync(string name, string localPath, CancellationToken ct);
    public abstract Task<bool> ExistsAsync(string fileName, CancellationToken ct = default);
    public abstract Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default);
    public abstract Task DeleteAsync(string fileName, CancellationToken ct = default);
    protected abstract Task<BlobStat?> StatAsync(string name, CancellationToken ct);
    protected abstract Task<Stream> OpenRangeAsync(string name, long offset, long length, CancellationToken ct);

    // ── Shared behaviour ──────────────────────────────────────────────────
    public async Task<StoredFile> SaveAsync(Stream source, string extension, CancellationToken ct = default)
    {
        // Stage locally to hash the bytes (content addressing) before the upload; on a hash collision the
        // content already exists remotely, so we skip the upload - that's the dedup.
        var temp = Path.Combine(ScratchDir, $"up_{Guid.NewGuid():N}");
        try
        {
            await using (var fs = File.Create(temp))
            {
                await source.CopyToAsync(fs, ct);
            }

            string hash;
            await using (var read = File.OpenRead(temp))
            {
                using var sha = SHA256.Create();
                hash = Convert.ToHexStringLower(await sha.ComputeHashAsync(read, ct));
            }

            var size = new FileInfo(temp).Length;
            var name = hash + extension;
            if (await ExistsAsync(name, ct))
            {
                return new StoredFile(hash, size, true);
            }

            await UploadAsync(name, temp, ct);
            return new StoredFile(hash, size, false);
        }
        finally
        {
            TryDeleteLocal(temp);
        }
    }

    public Task PutAsync(string fileName, string localSourcePath, CancellationToken ct = default)
    {
        return UploadAsync(fileName, localSourcePath, ct);
    }

    public async Task<BlobServe?> GetServeAsync(string fileName, CancellationToken ct = default)
    {
        var stat = await StatAsync(fileName, ct);
        if (stat is null)
        {
            return null;
        }

        return new RemoteBlobServe(stat.Length, stat.ETag, stat.LastModified,
            (offset, length, c) => OpenRangeAsync(fileName, offset, length, c));
    }

    public async Task<LocalBlobCopy?> GetLocalCopyAsync(string fileName, CancellationToken ct = default)
    {
        if (!await ExistsAsync(fileName, ct))
        {
            return null;
        }

        var temp = Path.Combine(ScratchDir, $"dl_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        await using (var source = await OpenReadAsync(fileName, ct))
        await using (var fs = File.Create(temp))
        {
            await source.CopyToAsync(fs, ct);
        }

        return new LocalBlobCopy(temp, true);
    }

    public void CleanupStaleScratch(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var scratch = ScratchDir;

            foreach (var file in Directory.EnumerateFiles(scratch))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDeleteLocal(file);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(scratch))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete stale upload dir {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scratch cleanup failed");
        }
    }

    protected void TryDeleteLocal(string path)
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

    // Ensure a validator is a syntactically valid strong ETag (quoted), whatever the backend returns.
    protected static string QuoteETag(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.StartsWith('"') || value.StartsWith("W/", StringComparison.Ordinal) ? value : $"\"{value.Trim('"')}\"";
    }

    protected record BlobStat(long Length, string ETag, DateTimeOffset LastModified);
}
