namespace Boxy.Web.Models;

/// <summary>What the "Server setup" admin page shows: the live storage/upload environment, plus the inputs
/// it needs to warn when the deployment can't comfortably take large (10 GB RAW/lossless) uploads.</summary>
public class ServerSetupViewModel
{
    public required string StorageProvider { get; init; }
    public required string ScratchDir { get; init; }

    /// <summary>Free space on the volume the working/scratch files actually sit on, or null when the
    /// platform won't report it.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>The reserve Boxy keeps free on that volume (0 = disabled).</summary>
    public long MinFreeDiskBytes { get; init; }

    /// <summary>The per-file cap for regular users, in MB (0 = unlimited). Admin uploads are never capped.</summary>
    public int MaxUploadMb { get; init; }

    public bool GpuAvailable { get; init; }
    public string? GpuUnavailableReason { get; init; }
    public bool CanToneMap { get; init; }

    /// <summary>The largest single upload the platform is being targeted at (RAW/lossless). Used only to
    /// size the "you need ~2x this free" guidance; not a hard limit.</summary>
    public const long TargetLargeUploadBytes = 10L * 1024 * 1024 * 1024;
}
