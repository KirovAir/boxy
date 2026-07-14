namespace Boxy.Data.Entities;

/// <summary>
/// An open drop-off folder. Anyone who knows its <see cref="Slug"/> can upload to it
/// at <c>/u/{slug}</c> without authenticating. The admin creates and names buckets.
/// </summary>
public class Bucket : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Public, unguessable URL token used in <c>/u/{slug}</c>.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Admin-facing label.</summary>
    public string Name { get; set; } = "";

    /// <summary>When false, uploads are rejected but existing items stay reachable.</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>When this box's public upload link stops working ("link-off"). Null = never expires
    /// (admin boxes, or retention disabled). After this plus a grace period a background sweep deletes
    /// the box together with the files dropped into it.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the owner was emailed that this box had expired and would be deleted. Set once so
    /// the reminder fires a single time; cleared when the owner restores it.</summary>
    public DateTime? ExpiryRemindedAt { get; set; }

    /// <summary>Optional URL that receives a POST when files are dropped into this box. Null = off.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>When true, the box owner is emailed (at their account address) about drop-offs.</summary>
    public bool EmailOnDrop { get; set; }

    /// <summary>Per-channel watermarks for drop-off notifications: only drops created after a channel's
    /// mark are reported on it, so a burst of uploads collapses into one message and each channel advances
    /// independently (a failed webhook can't re-trigger email, and vice versa). Set by the worker.</summary>
    public DateTime? WebhookNotifiedAt { get; set; }

    public DateTime? EmailNotifiedAt { get; set; }

    /// <summary>What happens to videos dropped into this box, when the sender doesn't say. Null falls
    /// through to the instance default. The public drop-off form deliberately doesn't offer the choice -
    /// an anonymous sender has no idea who will watch, and a drop-off is only ever seen by the box
    /// owner - so this is where the owner makes it once.</summary>
    public ConversionProfile? DefaultProfile { get; set; }

    /// <summary>The account that owns this box.</summary>
    public int OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    public ICollection<MediaItem> Items { get; set; } = [];
}

public class BucketConfiguration : AuditEntityConfiguration<Bucket>
{
    public override void Configure(EntityTypeBuilder<Bucket> builder)
    {
        base.Configure(builder);
        builder.ToTable(nameof(Bucket));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Slug).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(e => e.Slug).IsUnique();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.DefaultProfile).HasConversion<string>();
        builder.HasIndex(e => e.ExpiresAt);

        builder.HasOne(e => e.Owner)
            .WithMany(u => u.Buckets)
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => e.OwnerId);
    }
}
