using Boxy.Data.Entities;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Boxy.Web.Services;

/// <summary>
/// The one place byte-derived, kind-specific file metadata is read. Today that is the capture date - EXIF
/// <c>DateTimeOriginal</c> for images (ffprobe can't read image EXIF), the container <c>creation_time</c>
/// for video (already in the probe result, so no second read) - and it is the seam a future reader (PDF
/// page count, richer audio tags) slots into as one more branch, not a new abstraction.
///
/// Every path is fail-safe: a corrupt or unexpected file yields null, never an exception. Extraction runs
/// on the background queue after the bytes and row are already persisted, so it can only ever make a row
/// less rich - it can never fail an upload.
/// </summary>
public sealed class FileMetadataExtractor(ILogger<FileMetadataExtractor> logger)
{
    /// <summary>The capture date for a file, or null when it carries none. <paramref name="probe"/> is the
    /// ffprobe result the worker already computed, reused for video so there's no second read.</summary>
    public DateTime? CaptureDate(MediaKind kind, string localPath, ProbeResult? probe)
    {
        return kind switch
        {
            MediaKind.Image => Sanitize(ReadExifOriginal(localPath)),
            MediaKind.Video => Sanitize(probe?.CreationTimeUtc),
            _ => null
        };
    }

    private DateTime? ReadExifOriginal(string path)
    {
        try
        {
            // A header read - EXIF sits in the first KBs, so this is fast even on a large image.
            using var stream = File.OpenRead(path);
            var exif = ImageMetadataReader.ReadMetadata(stream).OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exif is not null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken))
            {
                return taken;
            }
        }
        catch (Exception ex) // ImageMetadataReader throws on unknown/corrupt formats - that's data, not an error
        {
            logger.LogDebug(ex, "EXIF read failed for {Path}", path);
        }

        return null;
    }

    /// <summary>Reject the sentinel "unset" epochs (QuickTime's 1904, Unix's 1970) that ffmpeg-produced or
    /// re-muxed files carry, so the UI never claims a video was shot in 1904; the value is timezone-naive,
    /// so it is pinned to Unspecified and stored/shown verbatim.</summary>
    private static DateTime? Sanitize(DateTime? value)
    {
        return value is { Year: > 1970 } dt ? DateTime.SpecifyKind(dt, DateTimeKind.Unspecified) : null;
    }
}
