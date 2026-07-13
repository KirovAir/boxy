using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Boxy.Web.Extensions;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Stores content in an Azure Blob Storage container (real Azure, or the Azurite emulator via a
/// connection string). Only the object primitives live here; content addressing, dedup, scratch, and
/// serving are shared in <see cref="RemoteBlobStoreBase"/>.
/// </summary>
public sealed class AzureBlobStore : RemoteBlobStoreBase
{
    private readonly BlobContainerClient _container;

    public AzureBlobStore(IConfiguration config, IWebHostEnvironment env, AzureBlobSettings settings, ILogger<AzureBlobStore> logger)
        : base(config.GetStoragePath(env), logger)
    {
        var service = new BlobServiceClient(settings.ConnectionString);
        _container = service.GetBlobContainerClient(settings.Container);
    }

    public override Task EnsureReadyAsync(CancellationToken ct = default)
    {
        return _container.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    protected override async Task UploadAsync(string name, string localPath, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(name);
        await blob.UploadAsync(localPath, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = ContentTypes.Guess(Path.GetExtension(name)) }
        }, ct);
    }

    public override async Task<bool> ExistsAsync(string fileName, CancellationToken ct = default)
    {
        return await _container.GetBlobClient(fileName).ExistsAsync(ct);
    }

    public override async Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default)
    {
        return await _container.GetBlobClient(fileName).OpenReadAsync(cancellationToken: ct);
    }

    public override async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            await _container.GetBlobClient(fileName).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete Azure blob {Blob}", fileName);
        }
    }

    protected override async Task<BlobStat?> StatAsync(string name, CancellationToken ct)
    {
        try
        {
            var props = await _container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct);
            return new BlobStat(props.Value.ContentLength, QuoteETag(props.Value.ETag.ToString()), props.Value.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    protected override async Task<Stream> OpenRangeAsync(string name, long offset, long length, CancellationToken ct)
    {
        var result = await _container.GetBlobClient(name).DownloadStreamingAsync(
            new BlobDownloadOptions { Range = new HttpRange(offset, length) }, ct);
        return result.Value.Content;
    }

    public override async Task<BlobUsage> GetUsageAsync(CancellationToken ct = default)
    {
        long total = 0;
        var count = 0;
        await foreach (var blob in _container.GetBlobsAsync(cancellationToken: ct))
        {
            total += blob.Properties.ContentLength ?? 0;
            count++;
        }

        return new BlobUsage(total, count);
    }
}
