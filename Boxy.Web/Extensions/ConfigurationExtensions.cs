namespace Boxy.Web.Extensions;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Root directory for content-addressed media storage. Absolute paths are used as-is
    /// (e.g. <c>/data/storage</c> in the container); relative paths resolve under the content root.
    /// </summary>
    public static string GetStoragePath(this IConfiguration config, IWebHostEnvironment env)
    {
        var raw = config["StoragePath"] ?? "storage";
        return Path.IsPathRooted(raw) ? raw : Path.Combine(env.ContentRootPath, raw);
    }

    /// <summary>
    /// Absolute base URL for public links (OpenGraph needs absolute URLs). Uses the configured public
    /// URL when set (behind the Cloudflare tunnel / reverse proxy), else the current request's host.
    /// </summary>
    public static string PublicBaseUrl(this IConfiguration config, HttpRequest request)
    {
        var configured = config["PublicBaseUrl"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";
    }
}
