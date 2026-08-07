using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;

namespace Boxy.Web.Services;

/// <summary>In-process IP geolocation for the view log, reading the DB-IP Lite databases that
/// <see cref="GeoDbRefreshService"/> keeps on disk. No visitor IP ever leaves the server for this.
/// Until the databases exist (first boot, host without internet) every lookup simply comes back
/// empty and the log shows bare times.</summary>
public class GeoLookup
{
    public const string CityFile = "dbip-city-lite.mmdb";
    public const string AsnFile = "dbip-asn-lite.mmdb";

    private DatabaseReader? city;
    private DatabaseReader? asn;

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

    /// <summary>Where a view came from: country, city and network provider, each null when the
    /// databases don't know. A private or unparsable IP resolves to nothing.</summary>
    public GeoInfo Locate(string? ip)
    {
        if (ip is null || !IPAddress.TryParse(ip, out var parsed) || IsPrivate(parsed))
        {
            return GeoInfo.None;
        }

        string? country = null, place = null, provider = null;
        if (city is { } cities && cities.TryCity(parsed, out var c))
        {
            country = c?.Country.IsoCode;
            place = c?.City.Name;
        }

        if (asn is { } networks && networks.TryAsn(parsed, out var a))
        {
            provider = a?.AutonomousSystemOrganization;
        }

        return new GeoInfo(country, place, provider);
    }

    /// <summary>(Re)opens whichever databases exist in <paramref name="dir"/>. Readers being swapped
    /// out are not disposed: a lookup on another thread may still hold one, and a dropped
    /// memory-mapped reader costs nothing until the GC gets to it.</summary>
    public void Load(string dir)
    {
        city = Open(Path.Combine(dir, CityFile)) ?? city;
        asn = Open(Path.Combine(dir, AsnFile)) ?? asn;
    }

    private static DatabaseReader? Open(string path)
    {
        return File.Exists(path) ? new DatabaseReader(path) : null;
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

/// <summary>What the databases know about one viewer's IP.</summary>
public record GeoInfo(string? Country, string? City, string? Provider)
{
    public static readonly GeoInfo None = new(null, null, null);

    /// <summary>"Amsterdam, NL", just "NL" when the city is unknown, or null when nothing resolved.</summary>
    public string? Place => Country is null ? null : City is null ? Country : $"{City}, {Country}";
}
