using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>One drop-off file row: the file, plus (on the aggregated dashboard list) the box it
/// landed in. Rendered by the _DropFileRow partial so a single box's file list and the dashboard's
/// combined list stay pixel-identical. When <see cref="UploaderKey"/> is set (a single box, which has
/// one query scope), the uploader chip becomes a link that filters the list to just that uploader;
/// on the combined dashboard list it is left null and the chip renders as a plain label.</summary>
public record DropFileRow(MediaItem Item, string? BucketName = null, string? UploaderKey = null, string? PageKey = null);
