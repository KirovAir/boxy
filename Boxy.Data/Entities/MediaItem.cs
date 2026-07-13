namespace Boxy.Data.Entities;

public enum MediaStatus
{
    /// <summary>Stored on disk, not yet probed/postered.</summary>
    Uploaded,

    /// <summary>ffprobe/poster/transcode in progress.</summary>
    Processing,

    /// <summary>Playable: original is web-safe, or a web version was produced.</summary>
    Ready,

    /// <summary>Processing failed (bad file, ffmpeg error).</summary>
    Failed
}

/// <summary>
/// What KIND of thing a file is, decided from its extension alone - the question a file-type filter chip
/// and a file icon ask, and known the moment the file lands. Materialized on <see cref="MediaItem.Kind"/>
/// so the type filter runs in SQL. Contrast the presentation classifier <c>Boxy.Web.MediaKinds.Of</c>,
/// which is probe-aware ("can we play this inline?").
/// </summary>
public enum MediaKind
{
    Video,
    Image,
    Audio,
    Pdf,
    File
}

/// <summary>
/// One uploaded file. Storage is content-addressed: the physical bytes live at
/// <c>{storage}/{ContentHash}{Extension}</c>, so re-uploading identical content
/// never writes twice. A <see cref="Slug"/> gives it a stable share URL at
/// <c>/v/{slug}</c> whose metadata (title/description/poster) the admin can edit
/// without the URL ever changing.
/// </summary>
public class MediaItem : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Stable, unguessable token that never changes: it serves the bytes (<c>/f/{slug}</c>,
    /// <c>/poster/{slug}</c>) and remains a permanent share-URL alias so links never break, even after
    /// a rename. The pretty, human-chosen URL is <see cref="CustomSlug"/>.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Optional human-chosen slug for the pretty share URL. Renamable, unique within its
    /// owner's namespace: an admin's shares live at <c>/s/{CustomSlug}</c>; a regular user's at
    /// <c>/s/{username}/{CustomSlug}</c>. Null until the owner sets one (the URL then uses
    /// <see cref="Slug"/>).</summary>
    public string? CustomSlug { get; set; }

    /// <summary>Bucket this was dropped into, or null when a signed-in user uploaded it as a share.</summary>
    public int? BucketId { get; set; }

    public Bucket? Bucket { get; set; }

    /// <summary>The account that owns this item: the uploader for a share, or the box owner for a drop-off
    /// (resolved at ingest). Always set, so nothing is ever ownerless - a drop-off whose box is later
    /// deleted keeps its owner (surfacing in the owner's dashboard) instead of becoming an invisible orphan.</summary>
    public int OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>SHA-256 (hex) of the file bytes - the dedup key and storage filename stem.</summary>
    public string ContentHash { get; set; } = "";

    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    /// <summary>What kind of file this is (image/video/audio/pdf/other), classified from
    /// <see cref="Extension"/> at ingest by <c>Boxy.Web.MediaKinds.FacetOf</c>. Stored (not computed at
    /// render time) so the file-type filter is a plain indexed SQL predicate. It is a pure function of the
    /// extension: if that mapping ever changes, existing rows are recomputed by a data migration.</summary>
    public MediaKind Kind { get; set; } = MediaKind.File;

    // Probed metadata (best-effort; null until processed).
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }

    /// <summary>When the content was actually captured, per the file's own metadata: EXIF DateTimeOriginal
    /// for images, the container creation_time for video. This is camera wall-clock with no reliable
    /// timezone, so it is stored and shown VERBATIM (date only) and never converted to UTC - distinct from
    /// <see cref="AuditableEntity.CreatedDate"/> (the upload time). Null for files with no such tag
    /// (screenshots, exports, anything that passed through a chat app).
    ///
    /// WARNING: unlike the created/modified timestamps, this column is timezone-NAIVE and must round-trip
    /// unchanged. If a model-wide UTC DateTime value converter is ever added, EXEMPT this property - a
    /// blanket converter would shift these dates by the server's offset and corrupt them.</summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>Content-addressed web-compatible H.264 mp4 produced when the original is not web-safe.</summary>
    public string? WebFileName { get; set; }

    /// <summary>Content-addressed poster/thumbnail (jpg) extracted for the player and OG image.</summary>
    public string? PosterFileName { get; set; }

    public MediaStatus Status { get; set; } = MediaStatus.Uploaded;

    /// <summary>When this share's public link stops working ("link-off"). Null = never expires (admin
    /// content, or retention disabled). After this plus a grace period a background sweep deletes it.
    /// A drop-off (<see cref="BucketId"/> set) leaves this null and is swept together with its box.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the owner was emailed that this share had expired and would be deleted. Set once so
    /// the reminder fires a single time; cleared when the owner restores it.</summary>
    public DateTime? ExpiryRemindedAt { get; set; }

    /// <summary>Optional password (PBKDF2 hash) that gates a public share: visitors must enter it before
    /// the page, file, and poster load. Null = no password. Only set on shares; the owner always bypasses.</summary>
    public string? SharePasswordHash { get; set; }

    /// <summary>Optional cap on how many times the public may download the original (via <c>?download=1</c>).
    /// Null = unlimited. The owner's own downloads never count.</summary>
    public int? MaxDownloads { get; set; }

    /// <summary>How many times the public has downloaded the original so far (see <see cref="MaxDownloads"/>).</summary>
    public int DownloadCount { get; set; }

    /// <summary>
    /// When true, <c>/v/{slug}</c> is publicly viewable. Only ever set on admin-uploaded videos
    /// (<see cref="BucketId"/> is null); bucket drop-offs are never public shares.
    /// </summary>
    public bool Published { get; set; }

    /// <summary>Number of times the public share page has been viewed (admin videos only).</summary>
    public int Views { get; set; }

    /// <summary>
    /// When true, the share page offers a "Download original" button and the <c>?download=1</c>
    /// endpoint serves the hi-res original to the public. Off by default so previewable media
    /// stays in Boxy; the owner/admin can always download their own, and non-previewable files
    /// (which have no in-app view) remain downloadable regardless.
    /// </summary>
    public bool AllowDownload { get; set; }

    /// <summary>
    /// When true, the processing worker skips transcoding this video: the source codec is kept and
    /// only remuxed into a faststart mp4 for streaming. Set at upload time as an opt-out of
    /// normalization for codecs the uploader knows their audience can play (e.g. VP9/AV1). The
    /// uploader owns compatibility. Off by default (everything normalizes to a universal file).
    /// </summary>
    public bool KeepOriginal { get; set; }

    /// <summary>
    /// Anonymous uploader identity (the <c>boxy_uid</c> cookie value). Lets a visitor delete
    /// their own bucket uploads without an account. Null for admin-uploaded items.
    /// </summary>
    public string? UploaderToken { get; set; }

    public string? ErrorMessage { get; set; }
}

public class MediaItemConfiguration : AuditEntityConfiguration<MediaItem>
{
    public override void Configure(EntityTypeBuilder<MediaItem> builder)
    {
        base.Configure(builder);
        builder.ToTable(nameof(MediaItem), t =>
        {
            // A bucket drop-off is never a public share, so it can never be published.
            t.HasCheckConstraint("CK_MediaItem_PublishedIsShare", "\"Published\" = 0 OR \"BucketId\" IS NULL");
            // A download cap only means something when downloading the original is actually offered.
            t.HasCheckConstraint("CK_MediaItem_MaxDownloadsNeedsAllow",
                "\"MaxDownloads\" IS NULL OR \"AllowDownload\" = 1");
        });
        builder.HasKey(e => e.Id);

        // NOCASE lets the DB enforce the case-insensitive uniqueness the app already normalises for.
        builder.Property(e => e.Slug).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(e => e.Slug).IsUnique();

        // The pretty slug is unique within its owner's namespace. A partial unique index on
        // (OwnerId, CustomSlug) makes two concurrent claims by one owner race on the DB instead of both
        // silently succeeding (the rarer cross-admin-root collision stays app-enforced). Filtered so the
        // many rows with no custom slug don't collide on NULL.
        builder.Property(e => e.CustomSlug).UseCollation("NOCASE");
        builder.HasIndex(e => new { e.OwnerId, e.CustomSlug })
            .IsUnique()
            .HasFilter("\"CustomSlug\" IS NOT NULL");

        // The retention sweep scans for items past their expiry.
        builder.HasIndex(e => e.ExpiresAt);

        builder.Property(e => e.Title).IsRequired();
        builder.Property(e => e.Description).HasColumnType("text");
        builder.Property(e => e.ContentHash).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>();

        // Stored as its name (like Status), for a readable DB and a stable filter predicate. No dedicated
        // index: every list query is already owner-/bucket-scoped to a small set that SQLite filters in
        // place, so a Kind index would be write-amplification without a matching read win. Add
        // (OwnerId, Kind) / (BucketId, Kind) only if a real instance ever profiles slow on the type facet.
        builder.Property(e => e.Kind).HasConversion<string>();

        // Fast dedup lookups: "does this bucket already hold this content?"
        builder.HasIndex(e => new { e.BucketId, e.ContentHash });

        // Fast "my uploads in this bucket" lookups for the anonymous uploader.
        builder.HasIndex(e => new { e.BucketId, e.UploaderToken });

        builder.HasOne(e => e.Bucket)
            .WithMany(b => b.Items)
            .HasForeignKey(e => e.BucketId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.Owner)
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => e.OwnerId);
    }
}
