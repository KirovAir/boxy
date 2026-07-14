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

    /// <summary>
    /// How much room to leave free on the working volume, in MB. An upload that would eat into it is turned
    /// away rather than allowed to fill the disk, because a full disk doesn't just break that one upload -
    /// it breaks the database, the transcodes, and every other upload in flight.
    ///
    /// This is the only backstop on staged chunks. A drop-off box is open to anyone with the link, the
    /// per-file size cap doesn't apply to an admin's box, and even where it does it only bounds one upload
    /// id at a time - so nothing else stops a visitor staging chunks until the disk gives out.
    /// </summary>
    public int MinFreeDiskMb { get; set; } = 2048;

    public long MinFreeDiskBytes => MinFreeDiskMb <= 0 ? 0 : (long)MinFreeDiskMb * 1024 * 1024;
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
