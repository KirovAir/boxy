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
    public static Task DeleteUnreferencedAsync(AppDbContext db, IBlobStore storage, MediaItem item, CancellationToken ct = default)
    {
        return DeleteUnreferencedAsync(db, storage, item.Id, item.ContentHash, item.Extension,
            item.PosterFileName, item.WebFileName, item.HqFileName, ct);
    }

    /// <summary>
    /// Drop a named set of files. Taken as values rather than off the item, because the caller that needs
    /// this most is a REPLACE: the row survives, but it has moved on to different bytes, and what has to go
    /// is what it used to point at.
    /// </summary>
    public static async Task DeleteUnreferencedAsync(AppDbContext db, IBlobStore storage, int itemId,
        string contentHash, string extension, string? poster, string? web, string? hq, CancellationToken ct = default)
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
    }
}
