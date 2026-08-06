namespace Boxy.Data.Entities;

/// <summary>One counted view of a share, logged at the moment the view counter ticks. Deliberately
/// just a timestamp: no IP, no user agent, so the log is a timeline rather than a visitor tracker.</summary>
public class MediaView : AuditableEntity
{
    public int Id { get; set; }

    public int MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
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
