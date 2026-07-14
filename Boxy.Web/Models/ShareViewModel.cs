using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>Everything the share page and its server-rendered OpenGraph tags need.</summary>
public class ShareViewModel
{
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Absolute canonical URL of this share page (for og:url).</summary>
    public required string PageUrl { get; init; }

    /// <summary>Absolute URL of the file to preview inline (player/image/pdf/audio src, og:video/image).
    /// For a video this is always the H.264 lane: the one thing that plays everywhere, and therefore the
    /// only thing worth handing a link-preview crawler.</summary>
    public required string FileUrl { get; init; }

    /// <summary>
    /// What to offer the &lt;video&gt; element, best first. The browser takes the first source it can
    /// actually decode and skips the rest, so an H.265 rendition can be offered ahead of the H.264 one
    /// without risking anything: a machine with no HEVC decoder never selects it.
    ///
    /// Empty for everything that isn't a video. Always ends with <see cref="FileUrl"/>, which is the
    /// source that always plays.
    /// </summary>
    public IReadOnlyList<VideoSource> Sources { get; init; } = [];

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

/// <summary>
/// One &lt;source&gt; on the player.
///
/// <paramref name="Type"/> is the whole mechanism. A bare "video/mp4" means "I might be able to play
/// this", so the browser downloads the file and only then finds out it has no decoder - and by then it is
/// too late, because the HTML spec aborts source selection on a decode error rather than falling through.
/// A precise codecs parameter means "I know exactly what this is", so a browser without the decoder skips
/// it cleanly and moves to the next source.
///
/// That cuts both ways, which is why precision is only ever used to make a source SKIPPABLE. The H.264
/// source deliberately carries no codecs parameter: Firefox has a long-standing bug where canPlayType
/// returns "" for perfectly playable avc1 strings, and an over-precise type there would make it skip the
/// one source that is guaranteed to work.
/// </summary>
public record VideoSource(string Url, string Type);
