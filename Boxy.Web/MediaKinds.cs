using System.Linq.Expressions;
using Boxy.Data.Entities;

namespace Boxy.Web;

public static class MediaKinds
{
    private static readonly string[] ImageExt =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".svg", ".avif"];

    private static readonly string[] AudioExt =
        [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".wma"];

    private static readonly string[] VideoExt =
        [".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".mpg", ".mpeg", ".wmv", ".flv", ".m2ts", ".mts", ".3gp", ".ogv"];

    /// <summary>Every extension we classify into a non-File kind - so "File" is their exact complement.</summary>
    private static readonly string[] KnownExt = [.. ImageExt, .. AudioExt, .. VideoExt, ".pdf"];

    /// <summary>
    /// Classify for presentation. Image/pdf/audio are decided by extension first - ffprobe reports a
    /// still image as an "mjpeg video", so the probe signal can't be trusted for those. A file is only
    /// a playable Video when a real video stream was found (<paramref name="hasVideoStream"/>) or a web
    /// version was produced (<paramref name="hasWebVideo"/>); a video-extension file that couldn't be
    /// read (a corrupt .mp4) falls through to a plain download rather than a broken player.
    /// </summary>
    public static MediaKind Of(string extension, bool hasVideoStream = false, bool hasWebVideo = false)
    {
        var e = extension.ToLowerInvariant();
        if (e == ".pdf")
        {
            return MediaKind.Pdf;
        }

        if (ImageExt.Contains(e))
        {
            return MediaKind.Image;
        }

        if (AudioExt.Contains(e))
        {
            return MediaKind.Audio;
        }

        if (hasVideoStream || hasWebVideo)
        {
            return MediaKind.Video;
        }

        return MediaKind.File;
    }

    /// <summary>
    /// Whether the public share may download the original. Previewable media (video/image/pdf/audio)
    /// is opt-in via <paramref name="allowDownload"/> so it stays in Boxy; a plain File has no in-app
    /// preview, so download is its only use and is always offered.
    /// </summary>
    public static bool CanDownloadOriginal(bool allowDownload, MediaKind kind)
    {
        return allowDownload || kind == MediaKind.File;
    }

    /// <summary>Classify a stored item straight from its columns.</summary>
    public static MediaKind Of(MediaItem m)
    {
        return Of(m.Extension, m.VideoCodec is not null, m.WebFileName is not null);
    }

    /// <summary>
    /// What KIND of thing a file is, from its extension alone - the question the file-type filter chip and
    /// the file icon ask. Known the moment a file lands (no probe needed), stable, and - via
    /// <see cref="WhereKind"/> - expressible in SQL, so the icon, the filter chip, and the persisted
    /// <see cref="MediaItem.Kind"/> column all read from THIS one rule. Contrast <see cref="Of(MediaItem)"/>,
    /// which asks "can we play it inline?" and needs the probe result.
    /// </summary>
    public static MediaKind FacetOf(string extension)
    {
        var e = extension.ToLowerInvariant();
        if (e == ".pdf")
        {
            return MediaKind.Pdf;
        }

        if (ImageExt.Contains(e))
        {
            return MediaKind.Image;
        }

        if (AudioExt.Contains(e))
        {
            return MediaKind.Audio;
        }

        if (VideoExt.Contains(e))
        {
            return MediaKind.Video;
        }

        return MediaKind.File;
    }

    /// <summary>The Bootstrap Icons glyph class for a kind's file-type icon - the poster fallback in file
    /// rows/cards and the filter chips. One place, so the row, the card, and the chip never disagree.</summary>
    public static string IconClass(MediaKind kind)
    {
        return kind switch
        {
            MediaKind.Video => "bi-film",
            MediaKind.Image => "bi-image",
            MediaKind.Audio => "bi-music-note-beamed",
            MediaKind.Pdf => "bi-file-earmark-pdf",
            _ => "bi-file-earmark"
        };
    }

    private static string[] ExtsFor(MediaKind kind)
    {
        return kind switch
        {
            MediaKind.Pdf => [".pdf"],
            MediaKind.Image => ImageExt,
            MediaKind.Audio => AudioExt,
            MediaKind.Video => VideoExt,
            _ => []
        };
    }

    /// <summary>
    /// The same rule as <see cref="FacetOf"/>, but as a predicate EF Core pushes into SQL (an
    /// <c>Extension IN (...)</c> for a real kind, or its complement for <see cref="MediaKind.File"/>).
    /// Extensions are stored lower-cased at ingest, so the IN-list matches exactly. Filtering on the
    /// materialized <see cref="MediaItem.Kind"/> column is preferred; this exists to backfill/reconcile
    /// that column and for any query that must classify by extension directly.
    /// </summary>
    public static Expression<Func<MediaItem, bool>> WhereKind(MediaKind kind)
    {
        if (kind == MediaKind.File)
        {
            var known = KnownExt;
            return m => !known.Contains(m.Extension);
        }

        var exts = ExtsFor(kind);
        return m => exts.Contains(m.Extension);
    }

    /// <summary>The lightbox category for a stored item: only image/video preview inline in an
    /// overlay; everything else ("file") opens in a new tab. Mirrors <c>Boxy.lightbox</c> in boxy.js.</summary>
    public static string LightboxKind(MediaItem m)
    {
        return Of(m) switch
        {
            MediaKind.Image => "image",
            MediaKind.Video => "video",
            _ => "file"
        };
    }

    /// <summary>The rubber-stamp class and label for a processing state, so every file list draws
    /// the same "Ready / Processing / Failed" stamp.</summary>
    public static (string Cls, string Text) StatusStamp(MediaStatus status)
    {
        return status switch
        {
            MediaStatus.Ready => ("stamp--ready", "Ready"),
            MediaStatus.Failed => ("stamp--failed", "Failed"),
            _ => ("stamp--proc", "Processing")
        };
    }
}
