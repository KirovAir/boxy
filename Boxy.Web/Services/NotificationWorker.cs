using System.Net;
using System.Net.Sockets;
using System.Text;
using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Sends owner notifications for drop-offs. Runs on a short timer and, each tick, scans for boxes with
/// drops arrived since a channel's watermark and notifies once per channel (webhook and/or email). The
/// watermarks (<see cref="Bucket.WebhookNotifiedAt"/>, <see cref="Bucket.EmailNotifiedAt"/>) are the
/// single source of truth, so the worker is self-healing: a drop is picked up whether or not the process
/// was running when it landed (restart-safe), each channel advances independently, and a mark only moves
/// once that channel delivers - a failed send is retried on the next tick (up to a give-up window) rather
/// than silently swallowed. The timer also debounces a burst of uploads into one message.
/// </summary>
public class NotificationWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IEmailSender email,
    EmailComposer composer,
    IConfiguration config,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // Stop retrying a channel whose delivery keeps failing once its oldest un-notified drop is this old,
    // so a permanently broken endpoint doesn't get hammered (and its log spammed) forever.
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Drop-off notification scan failed");
            }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var emailOn = await email.IsEnabledAsync(ct);

        // Only boxes with a pending drop on a channel that's actually usable. The channel filters keep
        // this cheap: most boxes have no webhook and no email opt-in. Email is skipped entirely when no
        // provider is configured, so those boxes' backlogs simply wait (and batch) until one is.
        var pending = await db.Buckets
            .Where(b =>
                (b.WebhookUrl != null
                 && db.MediaItems.Any(m => m.BucketId == b.Id && m.CreatedDate > (b.WebhookNotifiedAt ?? DateTime.MinValue)))
                || (emailOn && b.EmailOnDrop
                            && db.MediaItems.Any(m => m.BucketId == b.Id && m.CreatedDate > (b.EmailNotifiedAt ?? DateTime.MinValue))))
            .Select(b => b.Id)
            .ToListAsync(ct);

        foreach (var boxId in pending)
        {
            try
            {
                await DispatchAsync(db, boxId, now, emailOn, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Drop-off notification failed for box {Box}", boxId);
            }
        }
    }

    private async Task DispatchAsync(AppDbContext db, int boxId, DateTime now, bool emailOn, CancellationToken ct)
    {
        var box = await db.Buckets.Include(b => b.Owner).FirstOrDefaultAsync(b => b.Id == boxId, ct);
        if (box is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(box.WebhookUrl))
        {
            await RunChannelAsync(db, box, now,
                box.WebhookNotifiedAt,
                t => box.WebhookNotifiedAt = t,
                "webhook",
                drops => SendWebhookAsync(box, drops, ct),
                ct);
        }

        if (emailOn && box.EmailOnDrop)
        {
            await RunChannelAsync(db, box, now,
                box.EmailNotifiedAt,
                t => box.EmailNotifiedAt = t,
                "email",
                drops => SendEmailAsync(box, drops, ct),
                ct);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }
    }

    // Gathers a channel's pending drops, sends them, and advances the channel's watermark on success (or
    // once the drops are old enough that retrying is pointless). Leaves the mark on a transient failure so
    // the next scan retries. Does not save - the caller persists all channel marks in one write.
    private async Task RunChannelAsync(
        AppDbContext db, Bucket box, DateTime now,
        DateTime? mark, Action<DateTime> setMark, string channel,
        Func<IReadOnlyList<DropSummary>, Task<bool>> send, CancellationToken ct)
    {
        var since = mark ?? DateTime.MinValue;
        var drops = await db.MediaItems.AsNoTracking()
            .Where(m => m.BucketId == box.Id && m.CreatedDate > since && m.CreatedDate <= now)
            .OrderBy(m => m.CreatedDate)
            .Select(m => new DropSummary(m.OriginalFileName, m.SizeBytes, m.CreatedDate))
            .ToListAsync(ct);
        if (drops.Count == 0)
        {
            return;
        }

        var delivered = await send(drops);
        if (delivered)
        {
            logger.LogInformation("Box {Box}: {Channel} notified owner of {Count} new drop-off(s)", box.Id, channel, drops.Count);
            setMark(now);
        }
        else if (drops[0].CreatedDate < now - GiveUpAfter)
        {
            logger.LogWarning("Box {Box}: giving up on {Channel} notification after {Hours}h of failures", box.Id, channel, GiveUpAfter.TotalHours);
            setMark(now);
        }
    }

    /// <summary>Posts the batch to the box's webhook. Returns true when delivered (2xx) or when the
    /// target is blocked (retrying a blocked address never helps, so we let the watermark advance);
    /// false only on a transient failure worth retrying.</summary>
    private async Task<bool> SendWebhookAsync(Bucket box, IReadOnlyList<DropSummary> drops, CancellationToken ct)
    {
        var url = box.WebhookUrl!;
        var allowInternal = await AllowInternalWebhooksAsync(ct);
        if (!await IsAllowedTargetAsync(url, allowInternal, ct))
        {
            logger.LogWarning("Webhook target blocked for box {Box} (private/loopback; enable internal webhooks to allow): {Url}", box.Id, url);
            return true;
        }

        var payload = new
        {
            box = box.Name,
            boxSlug = box.Slug,
            fileCount = drops.Count,
            totalBytes = drops.Sum(d => d.SizeBytes),
            files = drops.Select(d => new { name = d.OriginalFileName, bytes = d.SizeBytes })
        };
        // Serialize up front and send as a length-delimited body (not chunked), which simple webhook
        // receivers handle more reliably.
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        var client = httpFactory.CreateClient("webhook");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(url, content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.LogWarning("Webhook for box {Box} returned {Status} (attempt {Attempt})", box.Id, (int)resp.StatusCode, attempt);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook POST for box {Box} failed (attempt {Attempt})", box.Id, attempt);
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        return false;
    }

    /// <summary>Emails the box owner the batch. Returns true when sent (or when there's no valid
    /// recipient, since retrying a bad address never helps); false on a transient send failure.</summary>
    private async Task<bool> SendEmailAsync(Bucket box, IReadOnlyList<DropSummary> drops, CancellationToken ct)
    {
        var to = box.Owner?.Email;
        if (string.IsNullOrWhiteSpace(to) || !LooksLikeEmail(to))
        {
            logger.LogWarning("Box {Box}: email on but owner has no valid address; skipping", box.Id);
            return true;
        }

        var files = drops.Select(d => new EmailFile(d.OriginalFileName, d.SizeBytes)).ToList();
        var msg = await composer.DropOffAsync(new DropOffEmail(box.Name, files, BoxLink(box)));
        return await email.SendAsync(to, msg.Subject, msg.Html, msg.Text, ct);
    }

    private string? BoxLink(Bucket box)
    {
        var baseUrl = config["PublicBaseUrl"]?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/dashboard/buckets/{box.Id}";
    }

    private async Task<bool> AllowInternalWebhooksAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.GetSettingsAsync<PlatformSettings>(ct)).AllowInternalWebhooks;
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    /// <summary>Guard against SSRF: only http(s), and (unless the admin opted in) never a target that
    /// resolves to a loopback/private/link-local address. Best-effort - does not defeat DNS rebinding.
    /// Redirects can't slip past this: the webhook client is configured not to follow them.</summary>
    private static async Task<bool> IsAllowedTargetAsync(string url, bool allowInternal, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (allowInternal)
        {
            return true;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            return addresses.Length > 0 && addresses.All(a => !IsPrivate(a));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork || ip.IsIPv4MappedToIPv6)
        {
            var b = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).GetAddressBytes();
            return b[0] == 10
                   || b[0] == 127
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254);
        }

        // IPv6 unique-local (fc00::/7).
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
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

    private record DropSummary(string OriginalFileName, long SizeBytes, DateTime CreatedDate);
}
