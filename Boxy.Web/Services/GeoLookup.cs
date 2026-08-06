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

    /// <summary>The viewer's IP as seen through the reverse proxy: X-Real-IP, else the first
    /// X-Forwarded-For hop, else the socket. Display-only, so a spoofed header is merely cosmetic.</summary>
    public static string? ClientIp(HttpRequest request)
    {
        var ip = request.Headers["X-Real-IP"].ToString();
        if (ip.Length == 0)
        {
            ip = request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        }

        if (ip.Length == 0)
        {
            ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }

        return IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : null;
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
                var http = httpFactory.CreateClient("geo");
                var doc = await http.GetFromJsonAsync<JsonElement>($"https://api.country.is/{ip}");
                country = doc.TryGetProperty("country", out var c) ? c.GetString() : null;
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
