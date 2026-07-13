using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>Everything the share page and its server-rendered OpenGraph tags need.</summary>
public class ShareViewModel
{
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Absolute canonical URL of this share page (for og:url).</summary>
    public required string PageUrl { get; init; }

    /// <summary>Absolute URL of the file to preview inline (player/image/pdf/audio src, og:video/image).</summary>
    public required string FileUrl { get; init; }

    /// <summary>Absolute URL that forces a download of the original file with its real name.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Absolute URL of the poster image (the video player's poster attribute), or null.</summary>
    public string? PosterUrl { get; init; }

    /// <summary>Absolute URL for the link-preview image: the poster, the image itself, or a default card.</summary>
    public required string OgImageUrl { get; init; }

    /// <summary>MIME type of the previewed stream.</summary>
    public required string ContentType { get; init; }

    /// <summary>How to present it: video player, inline image/pdf/audio, or a download card.</summary>
    public MediaKind Kind { get; init; }

    /// <summary>Whether to offer the original as a download (opt-in for media; always on for plain files;
    /// off once a download cap is reached).</summary>
    public bool CanDownload { get; init; }

    /// <summary>True when downloads would be offered but the cap has been reached - show a note instead.</summary>
    public bool DownloadLimitReached { get; init; }

    public string? OriginalFileName { get; init; }
    public long SizeBytes { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }

    public int Views { get; init; }
    public int Likes { get; init; }
    public bool LikedByMe { get; init; }

    /// <summary>The share slug, for the like endpoint.</summary>
    public required string Slug { get; init; }
}
