using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Boxy.Web.Extensions;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Stores content in an S3 bucket (real AWS, or any S3-compatible service such as MinIO, R2 or Backblaze
/// via <see cref="S3Settings.ServiceUrl"/>). Only the object primitives live here; content addressing,
/// dedup, scratch, and serving are shared in <see cref="RemoteBlobStoreBase"/>.
/// </summary>
public sealed class S3BlobStore : RemoteBlobStoreBase
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3BlobStore(IConfiguration config, IWebHostEnvironment env, S3Settings settings, ILogger<S3BlobStore> logger)
        : base(config.GetStoragePath(env), logger)
    {
        _bucket = settings.Bucket;

        var s3Config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            s3Config.ServiceURL = settings.ServiceUrl;
            s3Config.AuthenticationRegion = settings.Region;
        }
        else
        {
            s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);
        _client = new AmazonS3Client(credentials, s3Config);
    }

    public override async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket))
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct);
        }
    }

    protected override async Task UploadAsync(string name, string localPath, CancellationToken ct)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = name,
            FilePath = localPath,
            ContentType = ContentTypes.Guess(Path.GetExtension(name))
        }, ct);
    }

    public override async Task<bool> ExistsAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, fileName, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public override async Task<Stream> OpenReadAsync(string fileName, CancellationToken ct = default)
    {
        var response = await _client.GetObjectAsync(_bucket, fileName, ct);
        return response.ResponseStream;
    }

    public override async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, fileName, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete S3 object {Key}", fileName);
        }
    }

    protected override async Task<BlobStat?> StatAsync(string name, CancellationToken ct)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, name, ct);
            var lastModified = meta.LastModified is DateTime lm
                ? new DateTimeOffset(DateTime.SpecifyKind(lm, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow;
            return new BlobStat(meta.ContentLength, QuoteETag(meta.ETag), lastModified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    protected override async Task<Stream> OpenRangeAsync(string name, long offset, long length, CancellationToken ct)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = name,
            ByteRange = new ByteRange(offset, offset + length - 1)
        }, ct);
        return response.ResponseStream;
    }

    public override async Task<BlobUsage> GetUsageAsync(CancellationToken ct = default)
    {
        long total = 0;
        var count = 0;
        var request = new ListObjectsV2Request { BucketName = _bucket };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            // An empty bucket returns a null S3Objects collection on the v4 SDK.
            foreach (var obj in response.S3Objects ?? [])
            {
                total += obj.Size ?? 0;
                count++;
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated ?? false);

        return new BlobUsage(total, count);
    }
}
