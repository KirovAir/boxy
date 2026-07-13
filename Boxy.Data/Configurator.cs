using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Boxy.Data;

public static class Configurator
{
    public static void AddDbContext<T>(this IServiceCollection services) where T : DbContext
    {
        // Singleton factory so background services (e.g. the media worker) can create contexts;
        // callers use CreateDbContextAsync() to get a short-lived context per unit of work.
        services.AddDbContextFactory<T>(Configure);
    }

    private static void Configure(IServiceProvider sp, DbContextOptionsBuilder options)
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var connectionString = config.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        options.UseSqlite(connectionString,
            o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }

    public static async Task Initialize(this AppDbContext context, ILogger? logger = null)
    {
        await BackupBeforeMigration(context, logger);
        await RebaseOntoBaselineIfNeeded(context, logger);
        await context.Database.MigrateAsync();
        // WAL keeps reads (video streaming) from blocking writes (uploads/processing).
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// One-time, self-terminating rebase for when the migration history has been squashed to a single
    /// baseline. A database created by the old (pre-squash) chain has history rows for migrations that no
    /// longer exist in this assembly; without this, <c>MigrateAsync</c> would see the new baseline as
    /// "pending" and try to re-create tables that already exist. Since the squashed
    /// baseline is schema-identical to the accumulated chain, we simply replace the stale history with the
    /// single baseline row (metadata only - no schema or data is touched). Runs entirely inside the app's
    /// own startup connection, so there's no external writer racing SQLite.
    ///
    /// Guarded to act ONLY when the assembly holds exactly one migration (i.e. squashed) and the database's
    /// history is non-empty and lacks that baseline; a fresh install (no history table) and an
    /// already-rebased database both fall through untouched.
    /// </summary>
    private static async Task RebaseOntoBaselineIfNeeded(AppDbContext context, ILogger? logger)
    {
        var migrations = context.Database.GetMigrations().ToList();
        if (migrations.Count != 1)
        {
            return; // only meaningful once the history has been squashed to a single baseline
        }

        var baseline = migrations[0];

        List<string> applied;
        try
        {
            applied = await context.Database
                .SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"")
                .ToListAsync();
        }
        catch
        {
            return; // no history table yet: a brand-new database, MigrateAsync will create everything
        }

        if (applied.Count == 0 || applied.Contains(baseline))
        {
            return; // fresh database, or already rebased onto the baseline
        }

        // An existing database from the old chain: its tables already match the baseline schema, so replace
        // the stale history with just the baseline row inside one transaction.
        await using var tx = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})",
            baseline, "10.0.9");
        await tx.CommitAsync();
        logger?.LogWarning("Rebased {Count} legacy migration row(s) onto squashed baseline {Baseline}.",
            applied.Count, baseline);
    }

    private static async Task BackupBeforeMigration(AppDbContext context, ILogger? logger)
    {
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        logger?.LogInformation("Applying {Count} pending migration(s): {Names}",
            pending.Count, string.Join(", ", pending));

        var dbPath = context.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            return;
        }

        var backupPath = dbPath + ".bak";
        File.Copy(dbPath, backupPath, true);
        logger?.LogInformation("Database backed up to {BackupPath}", backupPath);
    }
}
