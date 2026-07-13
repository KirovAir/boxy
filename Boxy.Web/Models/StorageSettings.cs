namespace Boxy.Web.Models;

/// <summary>
/// Storage configuration, bound from the "Storage" section. <see cref="Provider"/> selects the backend
/// for finished content; ephemeral working files always stay on local disk regardless. More providers
/// slot in by adding an <c>IBlobStore</c> and a case in the DI switch.
/// </summary>
public class StorageSettings
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "filesystem";
    public S3Settings S3 { get; set; } = new();
    public AzureBlobSettings Azure { get; set; } = new();
}

public class S3Settings
{
    /// <summary>Custom endpoint for S3-compatible services (MinIO, Backblaze, R2). Empty = real AWS.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "boxy";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing (<c>host/bucket/key</c>). Required by MinIO and most emulators.</summary>
    public bool ForcePathStyle { get; set; } = true;
}

public class AzureBlobSettings
{
    public string? ConnectionString { get; set; }
    public string Container { get; set; } = "boxy";
}
