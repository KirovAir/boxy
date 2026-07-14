namespace Boxy.Web.Services;

/// <summary>Result of storing content-addressed bytes: the content hash, byte size, and whether the
/// content already existed (so no new copy was written).</summary>
public record StoredFile(string Hash, long Size, bool Deduped);

/// <summary>
/// Where an ingest's bytes come from. A stream is copied into the store, which is what a plain form post
/// has to do. A staged file is a local scratch file whose SHA-256 the caller already computed (the chunked
/// uploader hashes while it concatenates the parts), so the store takes the file over whole - a rename on
/// the filesystem backend - instead of making another full copy of it. On a multi-GB upload that is the
/// difference between one extra pass over the bytes and three.
/// </summary>
public abstract record UploadSource
{
    public abstract Task<StoredFile> StoreAsync(IBlobStore store, string extension, CancellationToken ct);

    public static UploadSource FromStream(Stream stream)
    {
        return new StreamUploadSource(stream);
    }

    /// <summary>A local scratch file the store may consume. It is gone once stored, either way.</summary>
    public static UploadSource FromStagedFile(string path, string hash)
    {
        return new StagedFileUploadSource(path, hash);
    }
}

public sealed record StreamUploadSource(Stream Stream) : UploadSource
{
    public override Task<StoredFile> StoreAsync(IBlobStore store, string extension, CancellationToken ct)
    {
        return store.SaveAsync(Stream, extension, ct);
    }
}

public sealed record StagedFileUploadSource(string Path, string Hash) : UploadSource
{
    public override Task<StoredFile> StoreAsync(IBlobStore store, string extension, CancellationToken ct)
    {
        return store.SaveStagedAsync(Path, Hash, extension, ct);
    }
}

/// <summary>
/// A local, on-disk copy of a blob for tools that need a real file path (ffmpeg/ffprobe). For the
/// filesystem backend this points at the blob itself and disposing is a no-op; for remote backends it is
/// a temporary download that is deleted on dispose. Always use it in a <c>using</c>.
/// </summary>
public sealed class LocalBlobCopy(string path, bool isTemporary) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        if (!isTemporary)
        {
            return;
        }

        try
        {
            File.Delete(Path);
        }
        catch
        {
            /* best-effort scratch cleanup */
        }
    }
}

/// <summary>
/// How to serve a blob's bytes over HTTP. The filesystem backend hands back a local path so the
/// framework serves it with native Range/ETag handling; remote backends (added with S3/Azure) will
/// provide a range opener so the app streams partial content itself. This split is the key difference
/// between backends, so it is modelled as a type the serving code switches on.
/// </summary>
public abstract record BlobServe;

/// <summary>Serve straight from a local file (native Range/conditional handling).</summary>
public sealed record LocalBlobServe(string Path) : BlobServe;

/// <summary>Serve by streaming from a remote backend: total length, validators, and a range opener that
/// fetches <c>[offset, offset+length)</c> from the backend. The serving code turns Range/conditional
/// request headers into the right byte fetch and status.</summary>
public sealed record RemoteBlobServe(
    long Length,
    string ETag,
    DateTimeOffset LastModified,
    Func<long, long, CancellationToken, Task<Stream>> OpenRange) : BlobServe;

/// <summary>
/// Content-addressed blob storage. Blobs are named <c>{hash}{ext}</c> (plus derived poster/thumb/web
/// names); the implementation is chosen by config (<c>Storage:Provider</c>). Ephemeral working files
/// (chunk staging, ffmpeg scratch) always live on local disk under <see cref="ScratchDir"/> regardless
/// of backend - only finished content goes through the store.
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores a stream's bytes content-addressed as <c>{hash}{extension}</c>, deduplicating
    /// against existing identical content.</summary>
    Task<StoredFile> SaveAsync(Stream source, string extension, CancellationToken ct = default);

    /// <summary>
    /// Stores an already-hashed local scratch file content-addressed as <c>{hash}{extension}</c>. The file
    /// is consumed: moved into place on the filesystem backend, uploaded then deleted on a remote one. Use
    /// this over <see cref="SaveAsync(Stream,string,CancellationToken)"/> whenever the bytes are already on
    /// local disk and hashed, so a multi-GB file is not copied and re-read a second time.
    /// <paramref name="hash"/> must be the lowercase hex SHA-256 of the file's bytes.
    /// </summary>
    Task<StoredFile> SaveStagedAsync(string localSourcePath, string hash, string extension, CancellationToken ct = default);

    /// <summary>Stores a local file's bytes under a specific name (for derived outputs like posters and
    /// web renditions). The source file may be consumed - do not reuse it afterwards.</summary>
    Task PutAsync(string fileName, string localSourcePath, CancellationToken ct = default);

    Task<bool> ExistsAsync(string fileName, CancellationToken ct = default);

    /// <summary>Opens the blob's full byte stream (e.g. to add to a zip). Throws if it does not exist.</summary>
    Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default);

    /// <summary>Best-effort delete; never throws (logs on failure).</summary>
    Task DeleteAsync(string fileName, CancellationToken ct = default);

    /// <summary>How to serve this blob over HTTP, or null if it does not exist.</summary>
    Task<BlobServe?> GetServeAsync(string fileName, CancellationToken ct = default);

    /// <summary>A local file copy for path-based tools (ffmpeg), or null if the blob does not exist.</summary>
    Task<LocalBlobCopy?> GetLocalCopyAsync(string fileName, CancellationToken ct = default);

    /// <summary>Local directory for ephemeral working files. Always on local disk.</summary>
    string ScratchDir { get; }

    /// <summary>Drops scratch files older than <paramref name="maxAge"/> left by crashes or aborts.</summary>
    void CleanupStaleScratch(TimeSpan maxAge);

    /// <summary>Total physical footprint of stored content (every blob - originals, posters, web
    /// renditions), for the admin statistics page. Excludes local scratch.</summary>
    Task<BlobUsage> GetUsageAsync(CancellationToken ct = default);
}

/// <summary>Physical storage footprint: total bytes across all stored blobs and how many there are.</summary>
public record BlobUsage(long TotalBytes, int ObjectCount);
