using Boxy.Data.Entities;
using Boxy.Web;
using Boxy.Web.Services;

namespace Boxy.Tests;

[TestClass]
public class MediaLogicTests
{
    [TestMethod]
    public void H264Mp4_IsWebPlayable()
    {
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mp4", "h264", "aac", "yuv420p"));
    }

    [TestMethod]
    public void H264Mov_IsWebPlayable()
    {
        // .mov is an mp4-family container - playable-as-is codec-wise (the worker still remuxes it
        // into a real .mp4 so it's served as video/mp4, but the codec check itself passes).
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mov", "h264", "aac", "yuv420p"));
    }

    [TestMethod]
    public void HevcMp4_IsNotWebPlayable()
    {
        // The file we serve by default is never H.265, whatever container it arrives in. Playing it needs
        // a hardware decoder, and on a share link we don't know the viewer - Firefox on Linux has none at
        // all. H.265 is offered as an extra source instead (see HevcCodecs), never as the only one.
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mp4", "hevc", "aac", "yuv420p"));
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mov", "hevc", null, "yuv420p"));
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mkv", "hevc", "aac", "yuv420p"));
    }

    [TestMethod]
    public void Hi10pHevcMp4_IsNotWebPlayable()
    {
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mp4", "hevc", "aac", "yuv420p10le"));
    }

    [TestMethod]
    public void HevcIsKeptAsTheHqRendition()
    {
        // Demoted, not discarded: H.265 is still worth keeping whole for the devices that can decode it,
        // at 8-bit and at 10-bit (so HDR phone footage survives instead of being flattened).
        Assert.IsTrue(MediaProcessor.CanKeepAsHq("hevc", "aac", "yuv420p"));
        Assert.IsTrue(MediaProcessor.CanKeepAsHq("hevc", null, "yuv420p10le"));

        // Nothing else earns a second file. H.264 already IS the universal file, and a codec no browser
        // decodes is not made better by being offered first.
        Assert.IsFalse(MediaProcessor.CanKeepAsHq("h264", "aac", "yuv420p"));
        Assert.IsFalse(MediaProcessor.CanKeepAsHq("vp9", "opus", "yuv420p"));
        // 4:2:2 / 4:4:4 HEVC is not something browsers decode, so there is no point keeping it.
        Assert.IsFalse(MediaProcessor.CanKeepAsHq("hevc", "aac", "yuv422p"));
        // An audio codec an mp4 stream-copy can't carry rules out the lossless remux this depends on.
        Assert.IsFalse(MediaProcessor.CanKeepAsHq("hevc", "ac3", "yuv420p"));
    }

    [TestMethod]
    public void H264Mp4_NoAudio_IsWebPlayable()
    {
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mp4", "h264", null, "yuv420p"));
    }

    [TestMethod]
    public void H264Mkv_IsNotWebPlayable_WrongContainer()
    {
        // H.264 in a non-mp4 container is not universal as-is - it must be remuxed to .mp4.
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mkv", "h264", "aac", "yuv420p"));
    }

    [TestMethod]
    public void Vp9Webm_IsNotWebPlayable()
    {
        // VP9/.webm plays on desktop Chrome/Firefox but NOT on iOS Safari → not universal.
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".webm", "vp9", "opus", "yuv420p"));
    }

    [TestMethod]
    public void Av1Mp4_IsNotWebPlayable()
    {
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mp4", "av1", "aac", "yuv420p"));
    }

    [TestMethod]
    public void Hi10pH264Mp4_IsNotWebPlayable()
    {
        // 10-bit H.264 (High 10 / yuv420p10le) is still codec "h264" but browsers/iOS can't decode
        // it - it must be transcoded to 8-bit, never served/stream-copied as-is.
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mp4", "h264", "aac", "yuv420p10le"));
    }

    [TestMethod]
    public void CanStreamCopyToMp4_H264Only()
    {
        // A clean 8-bit H.264 upload is still copied, never re-encoded: this is the path that keeps a
        // perfectly good 4K H.264 file untouched, and nobody should "fix" the H.265 change by breaking it.
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", "aac", "yuv420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", "mp3", "yuvj420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", null, "yuv420p"));

        // H.265 is not: copying it into the file we serve is the bug this replaced.
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("hevc", "aac", "yuv420p"));
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("hevc", "aac", "yuv420p10le"));
    }

    [TestMethod]
    public void HevcCodecs_DescribesOnlyWhatItCanStateHonestly()
    {
        // The string a browser uses to decide whether to even try. Main and Main 10, hvc1-tagged.
        Assert.AreEqual("hvc1.1.6.L93.B0",
            MediaProcessor.HevcCodecs(Probe("hevc", "yuv420p", "Main", 93, "hvc1")));
        Assert.AreEqual("hvc1.2.4.L120.B0",
            MediaProcessor.HevcCodecs(Probe("hevc", "yuv420p10le", "Main 10", 120, "hvc1")));

        // hev1 is refused outright by Safari, so it is never advertised - the remux retags it to hvc1
        // first, and this is probed from the file we actually serve.
        Assert.IsNull(MediaProcessor.HevcCodecs(Probe("hevc", "yuv420p", "Main", 93, "hev1")));
        // Anything we can't describe exactly is not offered at all: a source we can't name is a source a
        // browser can't skip, and it would download it only to fail.
        Assert.IsNull(MediaProcessor.HevcCodecs(Probe("hevc", "yuv422p", "Rext", 93, "hvc1")));
        Assert.IsNull(MediaProcessor.HevcCodecs(Probe("hevc", "yuv420p", "Main", null, "hvc1")));
        Assert.IsNull(MediaProcessor.HevcCodecs(Probe("h264", "yuv420p", "High", 41, "avc1")));
    }

    [TestMethod]
    public void EveryLaneGetsItsOwnBlobName()
    {
        // Blob names are content-addressed, so two uploads of the same bytes under different profiles land
        // on the same stem. If the lanes shared a suffix they would overwrite each other's output, and a
        // "don't convert it" upload would silently serve someone else's H.264 transcode.
        var names = ConversionProfiles.Choices.Select(ConversionProfiles.WebSuffix).ToList();
        Assert.AreEqual(ConversionProfiles.WebSuffix(ConversionProfile.Best),
            ConversionProfiles.WebSuffix(ConversionProfile.Universal),
            "Best and Universal produce the same capped H.264 file, so they may share it.");
        Assert.AreEqual(3, names.Distinct().Count());
        CollectionAssert.DoesNotContain(names, ConversionProfiles.HqSuffix);
    }

    [TestMethod]
    public void OnlyDerivedRenditionsAreDeletable()
    {
        Assert.IsTrue(ConversionProfiles.IsDerivedRendition("abc123-h264.mp4"));
        Assert.IsTrue(ConversionProfiles.IsDerivedRendition("abc123-h264-full.mp4"));
        Assert.IsTrue(ConversionProfiles.IsDerivedRendition("abc123-asis.mp4"));
        Assert.IsTrue(ConversionProfiles.IsDerivedRendition("abc123-hevc.mp4"));

        // The load-bearing case: HqFileName points at the ORIGINAL when the upload is already a faststart
        // hvc1 mp4. Cleaning up a stale rendition must never treat that as its own file to delete - after a
        // replace the item's hash has moved on, and another item may still share those bytes by dedup.
        Assert.IsFalse(ConversionProfiles.IsDerivedRendition("abc123.mp4"));
        Assert.IsFalse(ConversionProfiles.IsDerivedRendition("abc123.mov"));
        Assert.IsFalse(ConversionProfiles.IsDerivedRendition("abc123.jpg"));
        Assert.IsFalse(ConversionProfiles.IsDerivedRendition("abc123-thumb.jpg"));
    }

    [TestMethod]
    public void ReprocessesWhatDoesNotMatchItsProfile()
    {
        // The bug that started all this: a published video still serving H.265 by default.
        Assert.IsTrue(Needs(ConversionProfile.Best, "hevc", "h-h264.mp4".Replace("h-", "h"), "hevc", null));
        // A legacy item from before the lanes were named, and one that was never processed at all.
        Assert.IsTrue(Needs(ConversionProfile.Best, "h264", "h-web.mp4", "h264", null));
        Assert.IsTrue(Needs(ConversionProfile.Universal, "hevc", null, null, null));

        // An owner asked for a different conversion and the in-memory queue lost it to a restart.
        Assert.IsTrue(Needs(ConversionProfile.FullSize, "hevc", "h-h264.mp4", "h264", null), "capped file on a full-size item");
        Assert.IsTrue(Needs(ConversionProfile.AsUploaded, "hevc", "h-h264.mp4", "h264", null), "converted file on a don't-convert item");
        Assert.IsTrue(Needs(ConversionProfile.Universal, "hevc", "h-h264.mp4", "h264", "h.mp4"), "H.265 still advertised after leaving Best");
    }

    [TestMethod]
    public void ReprocessingSelfTerminates()
    {
        // Every settled shape must stop matching, or the worker re-probes the whole library on every boot
        // - which is exactly what the predicate this replaced did to a faststart H.265 mp4, forever.
        Assert.IsFalse(Needs(ConversionProfile.Best, "hevc", "h-h264.mp4", "h264", "h.mp4"), "Best: H.264 lane + H.265 rendition");
        Assert.IsFalse(Needs(ConversionProfile.Best, "h264", null, "h264", null), "a clean H.264 upload, served untouched");
        Assert.IsFalse(Needs(ConversionProfile.Universal, "vp9", "h-h264.mp4", "h264", null), "VP9 transcoded");
        Assert.IsFalse(Needs(ConversionProfile.FullSize, "hevc", "h-h264-full.mp4", "h264", null), "full-size lane");
        Assert.IsFalse(Needs(ConversionProfile.AsUploaded, "vp9", "h-asis.mp4", "vp9", null), "kept as uploaded, remuxed");
        Assert.IsFalse(Needs(ConversionProfile.AsUploaded, "h264", null, "h264", null), "kept as uploaded, served raw");

        // Not a video: never touched, whatever the columns say.
        Assert.IsFalse(Needs(ConversionProfile.Best, null, null, null, null));
    }

    private static bool Needs(ConversionProfile profile, string? videoCodec, string? web, string? webCodec, string? hq)
    {
        return ConversionProfiles.NeedsReprocessing(
            new ConversionProfiles.RenditionState(profile, "h", videoCodec, web, webCodec, hq));
    }

    private static ProbeResult Probe(string codec, string pixFmt, string? profile, int? level, string? tag)
    {
        return new ProbeResult(1920, 1080, 10, codec, "aac", pixFmt, 10, null, null, profile, level, tag);
    }

    [TestMethod]
    public void CanStreamCopyToMp4_NonUniversal_False()
    {
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("h264", "opus", "yuv420p")); // opus → transcode audio
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("vp9", "aac", "yuv420p"));
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("av1", "aac", "yuv420p"));
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("h264", "aac", "yuv420p10le")); // 10-bit → transcode
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("h264", "aac", "yuv444p")); // 4:4:4 → transcode
        Assert.IsFalse(MediaProcessor.CanStreamCopyToMp4("h264", "aac", null)); // unknown → conservative
    }

    [TestMethod]
    public void Slug_HasRequestedLength_AndSafeAlphabet()
    {
        var slug = SlugGenerator.New(10);
        Assert.AreEqual(10, slug.Length);
        Assert.IsFalse(slug.Contains('l') || slug.Contains('o') || slug.Contains('0') || slug.Contains('1'));
    }

    [TestMethod]
    public void GuessContentType_KnownAndUnknown()
    {
        Assert.AreEqual("video/mp4", ContentTypes.Guess(".mp4"));
        Assert.AreEqual("application/octet-stream", ContentTypes.Guess(".xyz"));
    }
}
