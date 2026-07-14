using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Boxy.Data.Entities;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>Thrown when the staged parts don't add up to the file the client says it sent (a missing part,
/// or one whose length doesn't match its slot). The parts are discarded, so the client must start over.</summary>
public class UploadIncompleteException(string reason) : Exception(reason);

/// <summary>Thrown when taking an upload any further would eat into the free space the server keeps in
/// reserve. Carries the room that's left, for the log.</summary>
public class StorageFullException(long freeBytes) : Exception($"Only {freeBytes} bytes free on the working volume")
{
    public long FreeBytes { get; } = freeBytes;
}

/// <summary>Thrown when a single chunk body runs past the ceiling. Only a client that isn't ours can manage
/// this - the uploader sends 16 MB chunks - so it means someone is trying to write an unbounded body.</summary>
public class ChunkTooLargeException(long maxBytes) : Exception($"A chunk may not exceed {maxBytes} bytes")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>
/// Reliable, parallel large-file uploads. The client splits a file into fixed-size chunks and
/// uploads several at once; each chunk is stored as its own part file under a per-upload folder,
/// so chunks may arrive out of order and a dropped one is simply re-sent. On completion the parts
/// are concatenated in order - hashed in the same pass - and the assembled file is handed to
/// <see cref="IngestionService"/>, which takes it over rather than copying it again.
/// Built for multi-GB uploads over flaky/mobile links.
///
/// The client declares the file's total size and its chunk size, which pins down exactly how long every
/// part must be. Parts that don't match their slot are never counted as present (so a resume re-sends
/// them) and never assembled, which is what stops a stale part - say from a build with a different chunk
/// size - being spliced into a file it doesn't belong to.
/// </summary>
public partial class ChunkedUploadService(
    IBlobStore storage,
    IngestionService ingestion,
    StorageSettings settings,
    ILogger<ChunkedUploadService> logger)
{
    // 1 MB copy buffer: the default 80 KB makes a needless number of syscalls over a multi-GB file.
    private const int CopyBuffer = 1024 * 1024;

    /// <summary>
    /// The most one chunk request may write. The uploader sends 16 MB chunks, so this is generous headroom
    /// rather than a real constraint - what it is actually for is putting a ceiling on a request body that
    /// nothing else bounds. A chunk body is not seekable, so its length is not known before it arrives, and
    /// the drop-off endpoint is open to anyone with the box's link: without a ceiling enforced as the bytes
    /// land, one request can write until the volume is full.
    /// </summary>
    public const int MaxChunkBytes = 64 * 1024 * 1024;

    // The volume can fill up while a chunk is arriving - other uploads are landing at the same time - so the
    // reserve gets re-checked as the bytes come in, not only before them.
    private const long FreeSpaceCheckEvery = 16L * 1024 * 1024;

    [GeneratedRegex("^[a-zA-Z0-9]{8,64}$")]
    private static partial Regex UploadIdPattern();

    /// <summary>
    /// Refuse to write <paramref name="wanted"/> more bytes when doing so would eat into the reserve the
    /// server keeps free. A full disk takes down far more than the upload that filled it, and staged chunks
    /// are otherwise unbounded: the per-file cap doesn't apply to an admin's box, and where it does apply it
    /// only ever bounds a single upload id, so a visitor with the link to an open box can just keep starting
    /// new ones.
    /// </summary>
    private void EnsureRoomOnDisk(long wanted)
    {
        var reserve = settings.MinFreeDiskBytes;
        if (reserve <= 0)
        {
            return;
        }

        long free;
        try
        {
            var volume = Volumes.GetOrAdd(storage.ScratchDir, VolumeFor);
            if (volume is null)
            {
                return; // can't work out which volume this is; don't invent a reason to reject uploads
            }

            free = volume.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            // Can't tell (an odd mount, a platform that won't say). Better to allow than to block everything.
            logger.LogDebug(ex, "Could not read free space for the scratch volume");
            return;
        }

        if (free - wanted < reserve)
        {
            logger.LogWarning("Refusing {Wanted} bytes: {Free} free, {Reserve} reserved", wanted, free, reserve);
            throw new StorageFullException(free);
        }
    }

    // Resolved once per storage path: working out the mount reads the system's mount table, and the scratch
    // directory doesn't move. The free space itself is read fresh from the volume every time.
    private static readonly ConcurrentDictionary<string, DriveInfo?> Volumes = new();

    private static DriveInfo? VolumeFor(string path)
    {
        // It has to be the volume the storage path actually sits on, not the root filesystem. Boxy's storage
        // is normally a mounted volume (/data in the container), so asking "/" how much room is left would
        // measure a completely different disk - one that is nowhere near full while the real one is.
        var drives = DriveInfo.GetDrives();
        var mount = DeepestMountFor(drives.Select(d => d.RootDirectory.FullName), path);
        return mount is null ? null : drives.FirstOrDefault(d => d.RootDirectory.FullName == mount);
    }

    /// <summary>The deepest mount point containing <paramref name="path"/>, matched on whole path segments so
    /// that <c>/data</c> doesn't claim <c>/database</c>.</summary>
    public static string? DeepestMountFor(IEnumerable<string> mounts, string path)
    {
        var full = Path.GetFullPath(path);
        string? best = null;
        foreach (var mount in mounts)
        {
            if (Contains(mount, full) && (best is null || mount.Length > best.Length))
            {
                best = mount;
            }
        }

        return best;

        static bool Contains(string mount, string full)
        {
            if (!full.StartsWith(mount, StringComparison.Ordinal))
            {
                return false;
            }

            return mount.Length == full.Length
                   || mount.EndsWith(Path.DirectorySeparatorChar)           // the root itself, "/" or "C:\"
                   || full[mount.Length] == Path.DirectorySeparatorChar;    // a clean segment boundary
        }
    }

    /// <summary>The exact byte length part <paramref name="index"/> must have for a file of
    /// <paramref name="size"/> bytes cut into <paramref name="chunkSize"/> pieces, or -1 when the index
    /// falls outside the file.</summary>
    public static long ExpectedChunkLength(int index, long size, long chunkSize)
    {
        if (index < 0 || size <= 0 || chunkSize <= 0)
        {
            return -1;
        }

        var start = (long)index * chunkSize;
        return start >= size ? -1 : Math.Min(chunkSize, size - start);
    }

    /// <summary>Store one chunk (by index) as its own part file. Idempotent - a retry overwrites it.
    /// The chunk is written to a private temp and atomically moved into place, so an interrupted write
    /// never leaves a truncated part that a resume would mistake for a complete one. The temp name is
    /// unique per attempt, so two concurrent writes of the same index (two tabs, or a retry racing a
    /// request the server is still reading) can't clobber each other's file.</summary>
    public async Task WriteChunkAsync(string uploadId, int index, Stream chunk, long maxBytes = 0, CancellationToken ct = default)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var dir = PartDir(uploadId);
        // Reserve for the largest body this request is allowed to write, since we can't know its length yet.
        EnsureRoomOnDisk(MaxChunkBytes);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, index.ToString());
        var tmp = Path.Combine(dir, $"{index}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBuffer, useAsync: true))
            {
                await CopyBoundedAsync(chunk, fs, ct);
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

    /// <summary>
    /// Copy a chunk body to disk, refusing it the moment it runs past the ceiling and re-checking the free
    /// space reserve as the bytes land. Copying the body straight out with no bound is what would let a
    /// single request fill the volume: nothing upstream knows how long it is.
    /// </summary>
    private async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try
        {
            long written = 0;
            long sinceCheck = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBuffer), ct)) > 0)
            {
                written += read;
                if (written > MaxChunkBytes)
                {
                    throw new ChunkTooLargeException(MaxChunkBytes);
                }

                sinceCheck += read;
                if (sinceCheck >= FreeSpaceCheckEvery)
                {
                    sinceCheck = 0;
                    EnsureRoomOnDisk(0);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
    /// interrupted upload by sending only what's missing. A part is only reported when its length is
    /// exactly what that slot needs, so a half-written temp, or a part left over from a build that cut the
    /// file differently, is re-sent rather than trusted.</summary>
    public IReadOnlyList<int> ExistingChunks(string uploadId, long size, long chunkSize)
    {
        var dir = PartDir(uploadId); // validates the id
        if (!Directory.Exists(dir) || size <= 0 || chunkSize <= 0)
        {
            return [];
        }

        var indices = new List<int>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (int.TryParse(Path.GetFileName(file), out var idx)
                && new FileInfo(file).Length == ExpectedChunkLength(idx, size, chunkSize))
            {
                indices.Add(idx);
            }
        }

        return indices;
    }

    /// <summary>Concatenate the parts in order and ingest them as a new item. Cleans up parts either way.</summary>
    public Task<MediaItem> CompleteAsync(
        string uploadId,
        UploadLayout layout,
        string fileName,
        int? bucketId,
        bool published,
        string? uploaderToken,
        int? ownerId = null,
        ConversionProfile profile = ConversionProfiles.Fallback,
        DateTime? expiresAt = null,
        long maxBytes = 0,
        int? quotaOwnerId = null,
        CancellationToken ct = default)
    {
        return AssembleAndUseAsync(uploadId, layout,
            assembled => ingestion.IngestAsync(assembled, fileName, bucketId, published, uploaderToken, ownerId, profile, expiresAt, maxBytes, quotaOwnerId, ct),
            ct);
    }

    /// <summary>Same assembly as <see cref="CompleteAsync"/>, but swaps the bytes into an existing item
    /// (keeping its slug, URL, and stats) instead of creating a new one. Returns null if the item is
    /// gone. Authorization is the caller's job.</summary>
    public Task<MediaItem?> CompleteReplaceAsync(string uploadId, UploadLayout layout, string fileName, int itemId, long maxBytes = 0, int? quotaOwnerId = null, CancellationToken ct = default)
    {
        return AssembleAndUseAsync(uploadId, layout,
            assembled => ingestion.ReplaceAsync(itemId, assembled, fileName, maxBytes, quotaOwnerId, ct),
            ct);
    }

    /// <summary>
    /// Concatenate the parts in order into one file, hashing as we go so the bytes are read once and not
    /// again, hand it to <paramref name="use"/> as a staged file (the store moves it into place rather
    /// than copying it), then clean up regardless of outcome.
    /// </summary>
    private async Task<T> AssembleAndUseAsync<T>(string uploadId, UploadLayout layout, Func<UploadSource, Task<T>> use, CancellationToken ct)
    {
        var dir = PartDir(uploadId);
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException($"No upload in progress for id {uploadId}");
        }

        if (!layout.IsValid)
        {
            throw new UploadIncompleteException($"Upload {uploadId} declared an impossible layout ({layout})");
        }

        // Assembling writes the whole file out again alongside its parts. Find out now that there isn't room
        // for it, rather than half way through and with the volume full.
        EnsureRoomOnDisk(layout.Size);

        var combined = dir + ".combined";
        try
        {
            var hash = await ConcatenateAsync(dir, uploadId, layout, combined, ct);
            return await use(UploadSource.FromStagedFile(combined, hash));
        }
        finally
        {
            // The store consumes the assembled file on success, so this only bites when something failed.
            TryDeleteFile(combined);
            TryDeleteDir(dir);
        }
    }

    /// <summary>Writes parts 0..n-1 into <paramref name="combined"/> and returns the SHA-256 of the result,
    /// computed in the same pass. Throws if any part is missing or the wrong length for its slot.</summary>
    private async Task<string> ConcatenateAsync(string dir, string uploadId, UploadLayout layout, string combined, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using (var outFs = new FileStream(combined, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer, useAsync: true))
        {
            await using var hashing = new CryptoStream(outFs, sha, CryptoStreamMode.Write, leaveOpen: true);
            for (var i = 0; i < layout.Total; i++)
            {
                var part = new FileInfo(Path.Combine(dir, i.ToString()));
                var expected = ExpectedChunkLength(i, layout.Size, layout.ChunkSize);
                if (!part.Exists)
                {
                    throw new UploadIncompleteException($"Upload {uploadId} is missing chunk {i}");
                }

                if (part.Length != expected)
                {
                    throw new UploadIncompleteException(
                        $"Upload {uploadId} chunk {i} is {part.Length} bytes, expected {expected}");
                }

                await using var inFs = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, useAsync: true);
                await inFs.CopyToAsync(hashing, CopyBuffer, ct);
            }
        }

        var size = new FileInfo(combined).Length;
        if (size != layout.Size)
        {
            throw new UploadIncompleteException($"Upload {uploadId} assembled to {size} bytes, expected {layout.Size}");
        }

        return Convert.ToHexStringLower(sha.Hash!);
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

/// <summary>
/// How the client cut the file up: its total byte size, the chunk size it used, and how many parts that
/// makes. The three have to agree, which is what <see cref="IsValid"/> checks - it means the server can
/// derive the exact length of every part instead of taking the client's word for what arrived.
/// </summary>
public readonly record struct UploadLayout(long Size, long ChunkSize, int Total)
{
    public bool IsValid =>
        Size > 0 && ChunkSize > 0 && Total > 0
        && Total == (Size + ChunkSize - 1) / ChunkSize;

    public override string ToString()
    {
        return $"size={Size} chunk={ChunkSize} total={Total}";
    }
}
