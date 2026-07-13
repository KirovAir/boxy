using System.Reflection;
using Boxy.Web.Models;
using Boxy.Web.Services;

namespace Boxy.Tests;

[TestClass]
public class VideoSettingsTests
{
    [TestMethod]
    public void Normalize_ClampsCrf_ToPracticalRange()
    {
        Assert.AreEqual(14, new VideoSettings { Crf = 0 }.Normalized().Crf); // lossless would be enormous
        Assert.AreEqual(35, new VideoSettings { Crf = 51 }.Normalized().Crf);
        Assert.AreEqual(23, new VideoSettings { Crf = 23 }.Normalized().Crf);
    }

    [TestMethod]
    public void Normalize_MaxLongEdge_ZeroDisables_OtherwiseClamped()
    {
        Assert.AreEqual(0, new VideoSettings { MaxLongEdge = 0 }.Normalized().MaxLongEdge); // "no cap"
        Assert.AreEqual(0, new VideoSettings { MaxLongEdge = -5 }.Normalized().MaxLongEdge);
        Assert.AreEqual(480, new VideoSettings { MaxLongEdge = 100 }.Normalized().MaxLongEdge);
        Assert.AreEqual(4320, new VideoSettings { MaxLongEdge = 8000 }.Normalized().MaxLongEdge); // small-VPS guard
        Assert.AreEqual(1920, new VideoSettings { MaxLongEdge = 1920 }.Normalized().MaxLongEdge);
    }

    [TestMethod]
    public void Normalize_MaxrateKbps_ZeroDisables_OtherwiseClamped()
    {
        Assert.AreEqual(0, new VideoSettings { MaxrateKbps = 0 }.Normalized().MaxrateKbps);
        Assert.AreEqual(500, new VideoSettings { MaxrateKbps = 1 }.Normalized().MaxrateKbps);
        Assert.AreEqual(100_000, new VideoSettings { MaxrateKbps = 999_999 }.Normalized().MaxrateKbps);
    }

    [TestMethod]
    public void Normalize_Preset_AllowlistedAndCaseInsensitive()
    {
        Assert.AreEqual("veryfast", new VideoSettings { Preset = "  VeryFast " }.Normalized().Preset);
        Assert.AreEqual("slow", new VideoSettings { Preset = "placebo" }.Normalized().Preset);
        Assert.AreEqual("slow", new VideoSettings { Preset = "" }.Normalized().Preset);
    }

    [TestMethod]
    public void Normalize_Preset_RejectsArgumentInjection()
    {
        // The preset is interpolated into the ffmpeg argument line, which is split on whitespace into
        // argv - an unvalidated value would let an admin form smuggle in extra ffmpeg arguments.
        var evil = new VideoSettings { Preset = "slow -y -f mp4 /data/boxy.db" }.Normalized();
        Assert.AreEqual("slow", evil.Preset);
        Assert.IsFalse(MediaProcessor.TranscodeArgs("in.mkv", "out.mp4", evil).Contains("/data/boxy.db"));
    }

    [TestMethod]
    public void TranscodeArgs_UsesTheSettings()
    {
        var args = MediaProcessor.TranscodeArgs("in.mkv", "out.mp4",
            new VideoSettings { Crf = 23, MaxLongEdge = 1280, Preset = "fast", MaxrateKbps = 4000 });
        StringAssert.Contains(args, "-crf 23");
        StringAssert.Contains(args, "-preset fast");
        StringAssert.Contains(args, "min(1280,iw)");
        StringAssert.Contains(args, "-maxrate 4000k -bufsize 8000k");
    }

    [TestMethod]
    public void TranscodeArgs_ZeroValues_DisableCapAndCeiling()
    {
        var args = MediaProcessor.TranscodeArgs("in.mkv", "out.mp4",
            new VideoSettings { Crf = 18, MaxLongEdge = 0, Preset = "slow", MaxrateKbps = 0 });
        Assert.IsFalse(args.Contains("force_original_aspect_ratio")); // no cap
        Assert.IsFalse(args.Contains("-maxrate")); // pure CRF
        StringAssert.Contains(args, "scale=trunc(iw/2)*2:trunc(ih/2)*2");
    }

    [TestMethod]
    public void VideoSettings_NeverCarriesAnExecutablePath()
    {
        // VideoSettings is written from an HTTP form into the DB. An executable path in here would be
        // handed to Process.Start, turning any admin-session compromise into arbitrary code execution on
        // the host. Binary paths belong in FfmpegSettings (environment only). Do not "just add" one.
        var names = typeof(VideoSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        Assert.IsFalse(names.Any(n => n.Contains("Path", StringComparison.OrdinalIgnoreCase)
                                      || n.Contains("Exe", StringComparison.OrdinalIgnoreCase)
                                      || n.Contains("Command", StringComparison.OrdinalIgnoreCase)));
    }
}
