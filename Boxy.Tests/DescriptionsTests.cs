using Boxy.Web;

namespace Boxy.Tests;

[TestClass]
public class DescriptionsTests
{
    [TestMethod]
    public void BareUrlsBecomeSafeLinks()
    {
        var html = Descriptions.ToHtml("kijk hier https://example.com/clip leuk he");
        StringAssert.Contains(html, "<a href=\"https://example.com/clip\"");
        StringAssert.Contains(html, "target=\"_blank\"");
        StringAssert.Contains(html, "rel=\"noopener nofollow\"");
    }

    [TestMethod]
    public void RawHtmlNeverExecutes()
    {
        // The share page is public; an owner's markup must render as text, not run.
        var html = Descriptions.ToHtml("<script>alert(1)</script> en <img src=x onerror=alert(1)>");
        Assert.IsFalse(html.Contains("<script"));
        Assert.IsFalse(html.Contains("<img"));
        StringAssert.Contains(html, "&lt;script&gt;");
    }

    [TestMethod]
    public void CreativeUrlSchemesGoNowhere()
    {
        // Markdig does not police schemes itself, so this is ours to hold.
        var html = Descriptions.ToHtml("[klik](javascript:alert(1)) en [ook](data:text/html,x)");
        Assert.IsFalse(html.Contains("javascript:"));
        Assert.IsFalse(html.Contains("data:"));
        StringAssert.Contains(html, "href=\"#\"");
    }

    [TestMethod]
    public void ImagesAreDemotedToLinks()
    {
        // A hotlinked image would let an owner track viewers; the text and destination survive.
        var html = Descriptions.ToHtml("![foto](https://example.com/pixel.png)");
        Assert.IsFalse(html.Contains("<img"));
        StringAssert.Contains(html, "<a href=\"https://example.com/pixel.png\"");
    }

    [TestMethod]
    public void NewlinesStayLineBreaks()
    {
        StringAssert.Contains(Descriptions.ToHtml("regel een\nregel twee"), "<br");
    }

    [TestMethod]
    public void PlainTextStripsTheMarkdown()
    {
        Assert.AreEqual("dikke tekst en link", Descriptions.ToPlainText("**dikke** _tekst_ en [link](https://x.nl)"));
        Assert.AreEqual("", Descriptions.ToPlainText(null));
        Assert.AreEqual("", Descriptions.ToHtml("   "));
    }
}
