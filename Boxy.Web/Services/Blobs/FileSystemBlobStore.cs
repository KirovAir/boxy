using System.Security.Cryptography;
using Boxy.Web.Extensions;

namespace Boxy.Web.Services;

/// <summary>
/// Local-filesystem blob store: content is named by the SHA-256 of its bytes under a storage root, so
/// uploading identical content twice never writes a second copy - that is the dedup. Serving hands back
/// the local path so the framework does native Range/ETag handling, and path-based tools (ffmpeg) read
/// the blob in place with no copy.
/// </summary>
public class FileSystemBlobStore(IConfiguration config, IWebHostEnvironment env, ILogger<FileSystemBlobStore> logger) : IBlobStore
{
    private string Root
    {
        get
        {
            var path = config.GetStoragePath(env);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private string ResolvePath(string fileName)
    {
        return Path.Combine(Root, fileName);
    }

    public string ScratchDir
    {
        get
        {
            var dir = Path.Combine(Root, "_tmp");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Streams <paramref name="source"/> to disk, hashes it, and stores it as <c>{hash}{extension}</c>.
    /// If that content already exists, the temp copy is discarded and <see cref="StoredFile.Deduped"/> is
    /// true.
    /// </summary>
    public async Task<StoredFile> SaveAsync(Stream source, string extension, CancellationToken ct = default)
    {
        var root = Root;
        var tempPath = Path.Combine(root, $"tmp_{Guid.NewGuid():N}");

        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await source.CopyToAsync(fs, ct);
            }

            string hash;
            await using (var read = File.OpenRead(tempPath))
            {
                using var sha = SHA256.Create();
                hash = Convert.ToHexStringLower(await sha.ComputeHashAsync(read, ct));
            }

            var size = new FileInfo(tempPath).Length;
            var fileName = hash + extension;
            var target = Path.Combine(root, fileName);

            if (File.Exists(target))
            {
                File.Delete(tempPath);
                return new StoredFile(hash, size, true);
            }

            try
            {
                File.Move(tempPath, target);
                return new StoredFile(hash, size, false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Race: another upload of identical content created the target first between the
                // check above and this move. Same bytes, so just treat it as a dedup hit.
                TryDeletePath(tempPath);
                return new StoredFile(hash, size, true);
            }
        }
        catch
        {
            TryDeletePath(tempPath);
            throw;
        }
    }

    public Task PutAsync(string fileName, string localSourcePath, CancellationToken ct = default)
    {
        var target = ResolvePath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(localSourcePath, target, true);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string fileName, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(ResolvePath(fileName)));
    }

    public Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default)
    {
        return Task.FromResult<Stream>(File.OpenRead(ResolvePath(fileName)));
    }

    public Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        TryDeletePath(ResolvePath(fileName));
        return Task.CompletedTask;
    }

    public Task<BlobServe?> GetServeAsync(string fileName, CancellationToken ct = default)
    {
        var path = ResolvePath(fileName);
        return Task.FromResult<BlobServe?>(File.Exists(path) ? new LocalBlobServe(path) : null);
    }

    public Task<LocalBlobCopy?> GetLocalCopyAsync(string fileName, CancellationToken ct = default)
    {
        var path = ResolvePath(fileName);
        return Task.FromResult<LocalBlobCopy?>(File.Exists(path) ? new LocalBlobCopy(path, false) : null);
    }

    /// <summary>
    /// Drop scratch files left behind by a crash or abandoned session: half-finished chunked uploads
    /// (part folders and combined temps under <c>_tmp</c>), plus orphaned <c>tmp_*</c> scratch in the
    /// content-addressed root - both interrupted ingest saves and interrupted ffmpeg outputs use that
    /// prefix. Real content files are named by a hex hash, never <c>tmp_*</c>, so they're safe.
    /// </summary>
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
                    TryDeletePath(file);
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

            // Orphaned scratch files in the storage root (not under _tmp): interrupted ingest saves
            // and ffmpeg outputs, all prefixed tmp_. Content files are hex-hash named, so never match.
            foreach (var file in Directory.EnumerateFiles(Root, "tmp_*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDeletePath(file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Temp cleanup failed");
        }
    }

    public Task<BlobUsage> GetUsageAsync(CancellationToken ct = default)
    {
        long total = 0;
        var count = 0;
        // Content lives flat in the root; EnumerateFiles is non-recursive so the _tmp scratch dir and
        // any tmp_* leftovers in the root are excluded.
        foreach (var file in Directory.EnumerateFiles(Root))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("tmp_", StringComparison.Ordinal))
            {
                continue;
            }

            total += new FileInfo(file).Length;
            count++;
        }

        return Task.FromResult(new BlobUsage(total, count));
    }

    private void TryDeletePath(string path)
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
            logger.LogWarning(ex, "Failed to delete {Path}", path);
        }
    }
}
