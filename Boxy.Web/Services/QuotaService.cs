using System.Collections.Concurrent;
using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Per-user storage quotas. A user's usage is the logical size of everything they're responsible for -
/// their own shares plus the drop-offs collected in their boxes - matching the stats page. The effective
/// cap is the per-user override, else the platform default; admins are never capped.
/// </summary>
public class QuotaService(IDbContextFactory<AppDbContext> dbFactory)
{
    // One lock per user so a check and its insert are atomic: two concurrent uploads for the same capped
    // account can't both read the pre-upload usage and both slip past the cap. Single-instance app, so an
    // in-process lock is enough.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    /// <summary>Serializes quota check + insert for one user. Dispose to release. Wrap the whole
    /// check-then-persist section so the reservation is honoured.</summary>
    public async Task<IDisposable> LockAsync(int userId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    /// <summary>Effective quota in bytes for a user; 0 means unlimited (admin, explicit 0, or no cap set).</summary>
    public async Task<long> EffectiveQuotaBytesAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Role, u.QuotaBytes })
            .FirstOrDefaultAsync(ct);
        if (user is null || user.Role == UserRole.Admin)
        {
            return 0;
        }

        if (user.QuotaBytes is long q)
        {
            return q < 0 ? 0 : q;
        }

        return (await db.GetSettingsAsync<PlatformSettings>(ct)).DefaultUserQuotaBytes;
    }

    /// <summary>Logical bytes a user currently occupies: their shares plus drop-offs in their boxes.</summary>
    public async Task<long> UsageBytesAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await UsageBytesAsync(db, userId, ct);
    }

    public static async Task<long> UsageBytesAsync(AppDbContext db, int userId, CancellationToken ct = default)
    {
        return await db.MediaItems
            .Where(m => m.OwnerId == userId || (m.BucketId != null && m.Bucket!.OwnerId == userId))
            .SumAsync(m => (long?)m.SizeBytes, ct) ?? 0;
    }

    /// <summary>Throws <see cref="QuotaExceededException"/> if adding <paramref name="additionalBytes"/>
    /// to the user would cross their cap. No-op when the user is uncapped or unresolved.</summary>
    public async Task EnsureRoomAsync(int userId, long additionalBytes, CancellationToken ct = default)
    {
        var quota = await EffectiveQuotaBytesAsync(userId, ct);
        if (quota <= 0)
        {
            return;
        }

        var usage = await UsageBytesAsync(userId, ct);
        if (usage + additionalBytes > quota)
        {
            throw new QuotaExceededException(quota, usage);
        }
    }
}

/// <summary>Raised when an upload would push the responsible user past their storage quota.</summary>
public class QuotaExceededException(long quotaBytes, long usedBytes) : Exception("Storage quota exceeded")
{
    public long QuotaBytes { get; } = quotaBytes;
    public long UsedBytes { get; } = usedBytes;
}
