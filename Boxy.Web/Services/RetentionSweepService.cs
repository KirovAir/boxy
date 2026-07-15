using System.Globalization;
using Boxy.Data;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Deletes content whose grace window has elapsed. A regular user's boxes and shares go link-off at
/// their <c>ExpiresAt</c> (enforced in the request path); <see cref="Retention.GraceDays"/> later this
/// sweep removes the rows and, dedup-safely, their files. Admin content (null ExpiresAt) is never
/// touched, nor is anything created before the retention feature (also null). Runs on start, then hourly.
///
/// It also warns owners: once content has gone link-off (entered the grace window) the owner is emailed
/// once, so they have the grace period to restore anything they want to keep before it's deleted.
/// </summary>
public class RetentionSweepService(
    IDbContextFactory<AppDbContext> dbFactory,
    IBlobStore storage,
    IEmailSender email,
    EmailComposer composer,
    IConfiguration config,
    ILogger<RetentionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retention sweep failed");
            }
        } while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Warn owners of content that just went link-off, before we get to deleting anything.
        try
        {
            await RemindExpiringAsync(db, now, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Expiry reminder pass failed");
        }

        // Grace has elapsed once ExpiresAt + GraceDays <= now, i.e. ExpiresAt <= now - GraceDays.
        var cutoff = now.AddDays(-Retention.GraceDays);

        var shares = await db.MediaItems
            .Where(m => m.BucketId == null && m.ExpiresAt != null && m.ExpiresAt <= cutoff)
            .ToListAsync(ct);

        var boxes = await db.Buckets
            .Where(b => b.ExpiresAt != null && b.ExpiresAt <= cutoff)
            .ToListAsync(ct);

        var drops = boxes.Count == 0
            ? []
            : await db.MediaItems
                .Where(m => m.BucketId != null && boxes.Select(b => b.Id).Contains(m.BucketId.Value))
                .ToListAsync(ct);

        if (shares.Count == 0 && boxes.Count == 0)
        {
            return;
        }

        // Remove rows first (MediaLikes cascade at the DB level), then drop any now-unreferenced files.
        foreach (var m in shares.Concat(drops))
        {
            db.MediaItems.Remove(m);
        }

        foreach (var b in boxes)
        {
            db.Buckets.Remove(b);
        }

        await db.SaveChangesAsync(ct);

        foreach (var m in shares.Concat(drops))
        {
            await MediaBlobs.DeleteUnreferencedAsync(db, storage, m, ct);
        }

        logger.LogInformation(
            "Retention sweep removed {Shares} expired share(s) and {Boxes} box(es) with {Drops} drop-off file(s)",
            shares.Count, boxes.Count, drops.Count);
    }

    // Email owners once about content now in its grace window (link-off, not yet deleted), so they can
    // restore anything worth keeping. Email-only and best-effort: needs a configured provider, and the
    // reminder mark only advances once delivery succeeds, so a transient failure is retried next hour.
    private async Task RemindExpiringAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        if (!await email.IsEnabledAsync(ct))
        {
            return;
        }

        var graceCutoff = now.AddDays(-Retention.GraceDays);

        var shares = await db.MediaItems.AsNoTracking()
            .Where(m => m.BucketId == null && m.ExpiryRemindedAt == null
                        && m.ExpiresAt != null && m.ExpiresAt <= now && m.ExpiresAt > graceCutoff)
            .Select(m => new ExpiringItem(m.Id, m.OwnerId, m.Title, "share", m.ExpiresAt!.Value))
            .ToListAsync(ct);

        var boxes = await db.Buckets.AsNoTracking()
            .Where(b => b.ExpiryRemindedAt == null
                        && b.ExpiresAt != null && b.ExpiresAt <= now && b.ExpiresAt > graceCutoff)
            .Select(b => new ExpiringItem(b.Id, b.OwnerId, b.Name, "box", b.ExpiresAt!.Value))
            .ToListAsync(ct);

        if (shares.Count == 0 && boxes.Count == 0)
        {
            return;
        }

        var ownerIds = shares.Concat(boxes).Select(i => i.OwnerId).Distinct().ToList();
        var owners = await db.Users.AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        foreach (var ownerId in ownerIds)
        {
            if (!owners.TryGetValue(ownerId, out var address) || !LooksLikeEmail(address))
            {
                continue;
            }

            var mine = boxes.Where(b => b.OwnerId == ownerId)
                .Concat(shares.Where(s => s.OwnerId == ownerId))
                .OrderBy(i => i.ExpiresAt)
                .ToList();

            var items = mine
                .Select(i => new ExpiringItemLine(i.Name, i.Kind, Retention.DeleteAfter(i.ExpiresAt).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)))
                .ToList();
            var link = config["PublicBaseUrl"]?.TrimEnd('/') is { Length: > 0 } b ? $"{b}/dashboard" : null;
            var msg = await composer.ExpiryReminderAsync(new ExpiryReminderEmail(items, link));
            if (!await email.SendAsync(address, msg.Subject, msg.Html, msg.Text, ct))
            {
                continue; // retry next hour; mark not advanced
            }

            var boxIds = mine.Where(i => i.Kind == "box").Select(i => i.Id).ToList();
            var shareIds = mine.Where(i => i.Kind == "share").Select(i => i.Id).ToList();
            if (boxIds.Count > 0)
            {
                await db.Buckets.Where(b => boxIds.Contains(b.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.ExpiryRemindedAt, now), ct);
            }

            if (shareIds.Count > 0)
            {
                await db.MediaItems.Where(m => shareIds.Contains(m.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExpiryRemindedAt, now), ct);
            }

            logger.LogInformation("Reminded owner {Owner} of {Count} expiring item(s)", ownerId, mine.Count);
        }
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private record ExpiringItem(int Id, int OwnerId, string Name, string Kind, DateTime ExpiresAt);
}
