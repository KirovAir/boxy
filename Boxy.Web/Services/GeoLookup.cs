using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Boxy.Data;

namespace Boxy.Web.Services;

/// <summary>Best-effort country tag for view log entries, resolved in the background so the share
/// page never waits on it. Uses api.country.is (free, keyless); a private or unresolvable IP simply
/// stays untagged and the log shows the bare time.</summary>
public class GeoLookup(IDbContextFactory<AppDbContext> dbFactory, IHttpClientFactory httpFactory, ILogger<GeoLookup> logger)
{
    private readonly ConcurrentDictionary<string, string?> cache = new();

    // A flood of made-up addresses must not fan out into a flood of external lookups; over this many
    // in flight, the tag is dropped and the row just stays untagged.
    private static readonly SemaphoreSlim Lookups = new(4);

    /// <summary>The viewer's IP as seen through the reverse proxy: X-Real-IP, else the first
    /// X-Forwarded-For hop, else the socket. Forwarded headers are only believed when the direct peer
    /// is a private address (our proxy); a public peer IS the client, and its headers are noise.</summary>
    public static string? ClientIp(HttpRequest request)
    {
        var peer = request.HttpContext.Connection.RemoteIpAddress;
        if (peer is not null && !IsPrivate(peer))
        {
            return peer.ToString();
        }

        var ip = request.Headers["X-Real-IP"].ToString();
        if (ip.Length == 0)
        {
            // The LAST hop is the one our own proxy appended; anything before it arrives exactly as
            // the client sent it and costs nothing to fake (measured: a seeded value passed through).
            var hops = request.Headers["X-Forwarded-For"].ToString().Split(',');
            ip = hops[^1].Trim();
        }

        return IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : peer?.ToString();
    }

    /// <summary>Fire-and-forget: resolve the IP's country and stamp it onto the view row.</summary>
    public void Tag(int viewId, string? ip)
    {
        if (ip is null || !IPAddress.TryParse(ip, out var parsed) || IsPrivate(parsed))
        {
            return;
        }

        _ = Task.Run(() => TagAsync(viewId, ip));
    }

    private async Task TagAsync(int viewId, string ip)
    {
        try
        {
            if (!cache.TryGetValue(ip, out var country))
            {
                if (!Lookups.Wait(0))
                {
                    return;
                }

                try
                {
                    var http = httpFactory.CreateClient("geo");
                    var doc = await http.GetFromJsonAsync<JsonElement>($"https://api.country.is/{ip}");
                    country = doc.TryGetProperty("country", out var c) ? c.GetString() : null;
                }
                finally
                {
                    Lookups.Release();
                }

                if (cache.Count > 4096)
                {
                    cache.Clear();
                }

                cache[ip] = country;
            }

            if (country is null)
            {
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            await db.MediaViews.Where(v => v.Id == viewId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Country, country));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Country lookup failed for view {ViewId}", viewId);
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                              || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
}
