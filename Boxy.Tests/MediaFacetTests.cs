using Boxy.Data.Entities;
using Boxy.Web;

namespace Boxy.Tests;

[TestClass]
public class MediaFacetTests
{
    [TestMethod]
    public void FacetOf_ClassifiesByExtension()
    {
        Assert.AreEqual(MediaKind.Video, MediaKinds.FacetOf(".mov"));
        Assert.AreEqual(MediaKind.Video, MediaKinds.FacetOf(".MP4")); // case-insensitive
        Assert.AreEqual(MediaKind.Image, MediaKinds.FacetOf(".heic"));
        Assert.AreEqual(MediaKind.Audio, MediaKinds.FacetOf(".opus"));
        Assert.AreEqual(MediaKind.Pdf, MediaKinds.FacetOf(".pdf"));
        Assert.AreEqual(MediaKind.File, MediaKinds.FacetOf(".zip"));
        Assert.AreEqual(MediaKind.File, MediaKinds.FacetOf(""));
    }

    [TestMethod]
    public void FacetOf_TreatsSvgAndAvifAsImages()
    {
        // The bug this whole change fixes: the worker's old private IsImage list omitted these two, so a
        // .avif rendered as a broken video. FacetOf (from the shared ImageExt list) classifies them as
        // images, and the worker now reads FacetOf, so the icon, the filter, and the thumbnail path agree.
        Assert.AreEqual(MediaKind.Image, MediaKinds.FacetOf(".avif"));
        Assert.AreEqual(MediaKind.Image, MediaKinds.FacetOf(".svg"));
    }

    [TestMethod]
    public void WhereKind_File_IsTheComplementOfEveryKnownExtension()
    {
        var isFile = MediaKinds.WhereKind(MediaKind.File).Compile();
        Assert.IsTrue(isFile(new MediaItem { Extension = ".zip" }));
        Assert.IsTrue(isFile(new MediaItem { Extension = ".txt" }));
        Assert.IsFalse(isFile(new MediaItem { Extension = ".mp4" }));
        Assert.IsFalse(isFile(new MediaItem { Extension = ".pdf" }));
    }

    [TestMethod]
    public void WhereKind_AndFacetOf_AreTheSameRule()
    {
        // The anti-drift guard: the SQL predicate (WhereKind) and the C# classifier (FacetOf) must agree
        // for every extension and every kind, forever. If someone edits one list and not the other, this
        // fails - which is exactly the class of bug (worker IsImage vs MediaKinds.ImageExt) that shipped.
        string[] samples =
        [
            ".mp4", ".mov", ".webm", ".mkv", ".jpg", ".png", ".heic", ".avif", ".svg",
            ".mp3", ".opus", ".flac", ".pdf", ".zip", ".txt", ".unknown", ""
        ];
        foreach (var kind in Enum.GetValues<MediaKind>())
        {
            var predicate = MediaKinds.WhereKind(kind).Compile();
            foreach (var ext in samples)
            {
                var byPredicate = predicate(new MediaItem { Extension = ext });
                var byClassifier = MediaKinds.FacetOf(ext) == kind;
                Assert.AreEqual(byClassifier, byPredicate,
                    $"WhereKind({kind}) and FacetOf disagree for '{ext}'");
            }
        }
    }
}
