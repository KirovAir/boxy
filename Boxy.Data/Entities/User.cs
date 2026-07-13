namespace Boxy.Data.Entities;

public enum UserRole
{
    /// <summary>A registered account: owns its own boxes and shares, nothing else.</summary>
    User,

    /// <summary>Additionally manages platform settings and the user list.</summary>
    Admin
}

/// <summary>
/// A registered account. Owns its own <see cref="Bucket"/>s and share <see cref="MediaItem"/>s.
/// The public drop-off side stays anonymous (see <see cref="MediaItem.UploaderToken"/>); only
/// account holders sign in.
/// </summary>
public class User : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Unique login identity, stored lower-cased.</summary>
    public string Email { get; set; } = "";

    /// <summary>Optional alternative login identity, stored lower-cased. Unique when set, and never
    /// contains '@', so usernames and emails can't collide as login identifiers.</summary>
    public string? Username { get; set; }

    /// <summary>Optional display name; falls back to the username or email when unset.</summary>
    public string? Name { get; set; }

    /// <summary>PBKDF2 hash from <c>PasswordHasher&lt;User&gt;</c>.</summary>
    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>When false the account can't sign in - disable without deleting their content.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional per-user storage cap in bytes, overriding the platform default. Null = use the
    /// default; 0 = unlimited for this account. Admins are never capped.</summary>
    public long? QuotaBytes { get; set; }

    public ICollection<Bucket> Buckets { get; set; } = [];
}

public class UserConfiguration : AuditEntityConfiguration<User>
{
    public override void Configure(EntityTypeBuilder<User> builder)
    {
        base.Configure(builder);
        builder.ToTable(nameof(User));
        builder.HasKey(e => e.Id);
        // NOCASE so the DB enforces the case-insensitive login uniqueness the app already normalises for.
        builder.Property(e => e.Email).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(e => e.Email).IsUnique();
        builder.Property(e => e.Username).UseCollation("NOCASE");
        builder.HasIndex(e => e.Username).IsUnique();
        builder.Property(e => e.PasswordHash).IsRequired();
        builder.Property(e => e.Role).HasConversion<string>();
    }
}
