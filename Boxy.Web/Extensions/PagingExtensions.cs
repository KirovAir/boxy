namespace Boxy.Web.Extensions;

public static class PagingExtensions
{
    /// <summary>
    /// Builds a URL that preserves the current query string but overrides the given keys - so a
    /// pager or sort link on one section keeps every other section's page/sort intact. A null
    /// override value drops that key.
    /// </summary>
    public static string UrlWith(this HttpRequest request, params (string Key, string? Value)[] overrides)
    {
        var q = request.Query
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                q.Remove(key);
            }
            else
            {
                q[key] = value;
            }
        }

        var query = string.Join("&", q
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return query.Length == 0 ? request.Path.ToString() : $"{request.Path}?{query}";
    }
}
