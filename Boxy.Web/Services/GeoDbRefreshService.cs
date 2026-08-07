using System.IO.Compression;
using System.Net;
using Boxy.Web.Extensions;

namespace Boxy.Web.Services;

/// <summary>
/// Keeps the DB-IP Lite databases behind <see cref="GeoLookup"/> present and current, under the
/// storage root in <c>_geo</c>. DB-IP publishes a new edition each month at a keyless URL; this loads
/// whatever is already on disk at startup, downloads a fresh copy once a file is over a month old,
/// and checks again daily, so a failed download just means the previous edition keeps serving until
/// the next tick works.
/// </summary>
public class GeoDbRefreshService(
    GeoLookup geo,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<GeoDbRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // A hair over a month: the current edition stays good until the next one is published, and a few
    // days late never matters at city-level accuracy.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(35);

    private bool loaded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Geo database refresh failed");
            }
        } while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var dir = Path.Combine(config.GetStoragePath(env), "_geo");
        Directory.CreateDirectory(dir);

        var refreshed = false;
        foreach (var file in new[] { GeoLookup.CityFile, GeoLookup.AsnFile })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - MaxAge)
            {
                refreshed |= await DownloadAsync(path, ct);
            }
        }

        if (refreshed || !loaded)
        {
            geo.Load(dir);
            loaded = true;
        }
    }

    /// <summary>Downloads one database, gunzipped, into place via a temp file so a torn download can
    /// never replace a good copy. Tries the current month's edition first and falls back one month
    /// for the gap before a new edition is published.</summary>
    private async Task<bool> DownloadAsync(string path, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var tmp = path + ".tmp";
        var client = httpFactory.CreateClient("geodb");
        foreach (var month in new[] { DateTime.UtcNow, DateTime.UtcNow.AddMonths(-1) })
        {
            var url = $"https://download.db-ip.com/free/{name}-{month:yyyy-MM}.mmdb.gz";
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                await using (var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress))
                await using (var file = File.Create(tmp))
                {
                    await gz.CopyToAsync(file, ct);
                }

                File.Move(tmp, path, overwrite: true);
                logger.LogInformation("Geo database updated: {Name}, edition {Month:yyyy-MM}", name, month);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Geo database download failed: {Url}", url);
                File.Delete(tmp);
                return false;
            }
        }

        logger.LogWarning("Geo database not available upstream: {Name}", name);
        return false;
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
}
