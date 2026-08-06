using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Boxy.Web;

/// <summary>
/// Renders a share's description: Markdown in, safe HTML out. Raw HTML is disabled outright - a public
/// share page must never run an owner's markup - and Markdig does not police link schemes itself, so
/// every link is scrubbed here to http/https/mailto. Bare URLs become links without any syntax (the way
/// people actually paste them), newlines stay line breaks, and links open in a new tab so the share
/// keeps playing behind them. Images are demoted to links: a description that hotlinks an image would
/// let an owner track viewers, and the share's own media is the picture here.
/// </summary>
public static class Descriptions
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in doc.Descendants().OfType<LinkInline>())
        {
            link.IsImage = false;
            link.Url = SafeUrl(link.Url);
            var attributes = link.GetAttributes();
            attributes.AddProperty("target", "_blank");
            attributes.AddProperty("rel", "noopener nofollow");
        }

        foreach (var link in doc.Descendants().OfType<AutolinkInline>())
        {
            link.Url = SafeUrl(link.Url);
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        return writer.ToString();
    }

    /// <summary>The description as plain text, for the meta/OpenGraph tags where markup is noise.</summary>
    public static string ToPlainText(string? markdown)
    {
        return string.IsNullOrWhiteSpace(markdown) ? "" : Markdig.Markdown.ToPlainText(markdown, Pipeline).Trim();
    }

    // Whatever isn't plainly a web or mail link gets its destination dropped: the text stays, the
    // javascript: (or anything else creative) goes nowhere.
    private static string SafeUrl(string? url)
    {
        return url is not null
               && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? url
            : "#";
    }
}
