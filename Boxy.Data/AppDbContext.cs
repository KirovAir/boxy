using System.Reflection;
using Boxy.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace Boxy.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Bucket> Buckets { get; set; } = null!;
    public DbSet<MediaItem> MediaItems { get; set; } = null!;
    public DbSet<MediaLike> MediaLikes { get; set; } = null!;

    /// <summary>Key/value platform settings; see <c>Boxy.Data.Extensions.ConfigExtensions</c>.</summary>
    public DbSet<Config> Configs { get; set; } = null!;

    // Data Protection keys live in the DB so the admin login cookie survives redeploys
    // and travels with the (backed-up) database.
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }

    private void SetAuditDates()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = now;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedDate = now;
            }
        }
    }

    public override int SaveChanges()
    {
        SetAuditDates();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetAuditDates();
        return base.SaveChangesAsync(cancellationToken);
    }
}
