namespace Boxy.Data.Entities;

/// <summary>One counted view of a share's page: the moment, and the viewer's IP with a best-effort
/// country tag. Exactly the views the counter counts - no owner previews, no bots.</summary>
public class MediaView : AuditableEntity
{
    public int Id { get; set; }

    public int MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Viewer's IP as the proxy reported it, shown on hover in the owner's log.</summary>
    public string? Ip { get; set; }

    /// <summary>ISO country code resolved from the IP in the background; null when private or unknown.</summary>
    public string? Country { get; set; }
}

public class MediaViewConfiguration : AuditEntityConfiguration<MediaView>
{
    public override void Configure(EntityTypeBuilder<MediaView> builder)
    {
        base.Configure(builder);
        builder.ToTable(nameof(MediaView));
        builder.HasKey(e => e.Id);

        // The timeline reads one item's views newest-first.
        builder.HasIndex(e => new { e.MediaItemId, e.CreatedDate });

        builder.HasOne(e => e.MediaItem)
            .WithMany()
            .HasForeignKey(e => e.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
