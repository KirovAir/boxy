using Boxy.Data;
using Boxy.Data.Entities;

namespace Boxy.Web.Services;

/// <summary>
/// Dropping the physical files behind a media item, dedup-safely.
///
/// Storage is content-addressed, so two people uploading the same clip share one set of bytes - the
/// original, the poster, and every derived rendition. A blob may only go when the LAST row referencing it
/// does, and conversely, if it isn't deleted here it is never deleted at all: <see cref="IBlobStore"/> has
/// no enumeration API, so an orphan is a file nothing in the system can ever find again.
///
/// This lived as five near-identical copies (share delete, bulk delete, drop-off delete, account delete,
/// retention sweep) which had already drifted apart - one of them checked the hash but not the extension,
/// and leaked the original. Every rendition column added to MediaItem had to be remembered in all five.
/// Now it is one.
/// </summary>
public static class MediaBlobs
{
    /// <summary>Drop the files of an item that is going away.</summary>
    public static Task DeleteUnreferencedAsync(AppDbContext db, IBlobStore storage, MediaProcessingQueue queue,
        MediaItem item, CancellationToken ct = default)
    {
        return DeleteUnreferencedAsync(db, storage, queue, item.Id, item.ContentHash, item.Extension,
            item.PosterFileName, item.WebFileName, item.HqFileName, ct);
    }

    /// <summary>
    /// Drop a named set of files. Taken as values rather than off the item, because the caller that needs
    /// this most is a REPLACE: the row survives, but it has moved on to different bytes, and what has to go
    /// is what it used to point at.
    /// </summary>
    public static async Task DeleteUnreferencedAsync(AppDbContext db, IBlobStore storage, MediaProcessingQueue queue,
        int itemId, string contentHash, string extension, string? poster, string? web, string? hq, CancellationToken ct = default)
    {
        // "Still referenced" always excludes the item these files came from. It has either been removed
        // already or has been repointed at new bytes, so either way its claim is gone - and excluding it by
        // id means this works whether the caller deletes the row before or after calling.
        if (!await db.MediaItems.AnyAsync(m => m.Id != itemId
                                               && m.ContentHash == contentHash && m.Extension == extension, ct))
        {
            // Keyed on hash AND extension: the same bytes re-uploaded under a different extension are a
            // different file on disk, and the old one still has to go.
            await storage.DeleteAsync(contentHash + extension, ct);
        }

        // A twin mid-pipeline holds its claims only in the worker's memory: the columns read below say
        // "unreferenced" for the very names that run is about to advertise. Leave every derived file to
        // that run's own sweep, which keeps what it adopts and reclaims the rest. Worth the rare leak on
        // a profile mismatch - deleting a file mid-adoption is the loss that can't be repaired.
        var busyTwin = (await db.MediaItems
                .Where(m => m.Id != itemId && m.ContentHash == contentHash)
                .Select(m => m.Id).ToListAsync(ct))
            .Any(queue.IsPending);
        if (busyTwin)
        {
            return;
        }

        if (poster is not null
            && !await db.MediaItems.AnyAsync(m => m.Id != itemId && m.PosterFileName == poster, ct))
        {
            await storage.DeleteAsync(poster, ct);
        }

        foreach (var rendition in new[] { web, hq })
        {
            // HqFileName can BE the original: an upload that is already a faststart hvc1 mp4 needs no
            // second file, so the rendition is the upload. Those bytes are handled above, on the hash, and
            // deleting them here as well - on a bare name, without the hash check - would take out a file
            // another item legitimately still holds as its own original.
            if (rendition is null || !ConversionProfiles.IsDerivedRendition(rendition))
            {
                continue;
            }

            if (!await db.MediaItems.AnyAsync(m => m.Id != itemId
                                                   && (m.WebFileName == rendition || m.HqFileName == rendition), ct))
            {
                await storage.DeleteAsync(rendition, ct);
            }
        }

        await DeleteUnreferencedHlsAsync(db, storage, itemId, contentHash, null, false, ct);
    }

    /// <summary>
    /// Drop the HLS pairs on this hash that no lane claims any more. The HLS package has no filename
    /// column - its names ride the hash and the lane's stem - so "claimed" is derived: this item's own
    /// current lane (when the caller still has one) plus every other row on the hash that advertises the
    /// variant, each through its own profile's stem. Swept per stem, so a profile switch can't strand the
    /// old lane's pair, and deleting names that never existed is a no-op.
    /// </summary>
    public static async Task DeleteUnreferencedHlsAsync(AppDbContext db, IBlobStore storage, int itemId,
        string contentHash, string? ownWebStem, bool ownHq, CancellationToken ct = default)
    {
        var twins = await db.MediaItems
            .Where(m => m.Id != itemId && m.ContentHash == contentHash && (m.HlsCodecs != null || m.HlsHqCodecs != null))
            .Select(m => new { m.HlsWebStem, m.HlsHqCodecs })
            .ToListAsync(ct);

        foreach (var stem in ConversionProfiles.HlsWebStems)
        {
            var claimed = stem == ownWebStem || twins.Any(t => t.HlsWebStem == stem);
            if (!claimed)
            {
                await storage.DeleteAsync(ConversionProfiles.HlsPlaylistName(contentHash, stem), ct);
                await storage.DeleteAsync(ConversionProfiles.HlsMediaName(contentHash, stem), ct);
            }
        }

        if (!ownHq && !twins.Any(t => t.HlsHqCodecs != null))
        {
            var stem = "-" + ConversionProfiles.HlsHqVariant;
            await storage.DeleteAsync(ConversionProfiles.HlsPlaylistName(contentHash, stem), ct);
            await storage.DeleteAsync(ConversionProfiles.HlsMediaName(contentHash, stem), ct);
        }
    }
}
