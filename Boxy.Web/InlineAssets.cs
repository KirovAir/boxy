using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.FileProviders;

namespace Boxy.Web;

/// <summary>
/// Inlines one or more wwwroot scripts into the page as a single &lt;script&gt; block, so there is no
/// separate asset request for an ad/content blocker, a flaky asset hop, or an in-app browser to drop. Used
/// on the public box-drop page, where the chunked uploader has to run "at all costs". The files stay the
/// single source of truth under <c>wwwroot/js</c>; their contents are read once per file version and cached.
/// </summary>
public static class InlineAssets
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static IHtmlContent InlineScript(IFileProvider webRoot, params string[] relativePaths)
    {
        var sb = new StringBuilder("<script>\n");
        foreach (var rel in relativePaths)
        {
            var file = webRoot.GetFileInfo(rel);
            if (!file.Exists)
            {
                continue;
            }

            // Key on path + version so an edit to the file is picked up without a restart.
            var key = $"{rel}|{file.LastModified.UtcTicks}|{file.Length}";
            sb.Append(Cache.GetOrAdd(key, _ => Read(file))).Append('\n');
        }

        sb.Append("</script>");
        return new HtmlString(sb.ToString());
    }

    private static string Read(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        // A literal </script> in the body would end the block early. Our scripts contain none, but
        // neutralise the sequence defensively so a future edit can't silently break the page.
        return reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
    }
}
