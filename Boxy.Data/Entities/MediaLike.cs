namespace Boxy.Data.Entities;

/// <summary>One like on a shared video by one anonymous visitor (their boxy_uid token).</summary>
public class MediaLike : AuditableEntity
{
    public int Id { get; set; }

    public int MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>The liker's anonymous account (boxy_uid cookie value).</summary>
    public string UploaderToken { get; set; } = "";
}

public class MediaLikeConfiguration : AuditEntityConfiguration<MediaLike>
{
    public override void Configure(EntityTypeBuilder<MediaLike> builder)
    {
        base.Configure(builder);
        builder.ToTable(nameof(MediaLike));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UploaderToken).IsRequired();

        // One like per visitor per video.
        builder.HasIndex(e => new { e.MediaItemId, e.UploaderToken }).IsUnique();

        builder.HasOne(e => e.MediaItem)
            .WithMany()
            .HasForeignKey(e => e.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
