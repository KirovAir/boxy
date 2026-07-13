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
    public void HevcMp4_IsWebPlayable()
    {
        // 8-bit HEVC in an mp4-family container is now accepted as-is (Apple + hardware-decode browsers).
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mp4", "hevc", "aac", "yuv420p"));
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mov", "hevc", null, "yuv420p"));
    }

    [TestMethod]
    public void HevcMkv_IsNotWebPlayable_WrongContainer()
    {
        // HEVC is accepted, but .mkv isn't an mp4-family container, so it still needs a remux to mp4.
        Assert.IsFalse(MediaProcessor.IsWebPlayable(".mkv", "hevc", "aac", "yuv420p"));
    }

    [TestMethod]
    public void Hi10pHevcMp4_IsWebPlayable()
    {
        // 10-bit HEVC (Main 10) decodes wherever HEVC does, so HDR footage is kept, not transcoded.
        Assert.IsTrue(MediaProcessor.IsWebPlayable(".mp4", "hevc", "aac", "yuv420p10le"));
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
    public void CanStreamCopyToMp4_H264OrHevc_True()
    {
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", "aac", "yuv420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", "mp3", "yuvj420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("h264", null, "yuv420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("hevc", "aac", "yuv420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("hevc", null, "yuvj420p"));
        Assert.IsTrue(MediaProcessor.CanStreamCopyToMp4("hevc", "aac", "yuv420p10le")); // 10-bit HEVC (HDR) kept
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
