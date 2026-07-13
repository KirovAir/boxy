namespace Boxy.Data.Entities;

/// <summary>
/// Generic key/value store for platform settings. Each settings POCO is persisted as one row,
/// keyed by its type name, with the object serialized to JSON in <see cref="Value"/>. Adding a
/// new setting means adding a field to a POCO - no schema migration. See
/// <c>Boxy.Data.Extensions.ConfigExtensions</c>.
/// </summary>
public class Config
{
    /// <summary>The settings key (the POCO's type name).</summary>
    public string Id { get; set; } = "";

    /// <summary>The settings object serialized as JSON.</summary>
    public string Value { get; set; } = "";
}

public class ConfigConfiguration : IEntityTypeConfiguration<Config>
{
    public void Configure(EntityTypeBuilder<Config> builder)
    {
        builder.ToTable(nameof(Config));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Value).HasColumnType("text").IsRequired();
    }
}
