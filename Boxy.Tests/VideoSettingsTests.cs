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
        Assert.IsFalse(Args(evil).Contains("/data/boxy.db"));
    }

    [TestMethod]
    public void TranscodeArgs_UsesTheSettings()
    {
        var args = Args(new VideoSettings { Crf = 23, MaxLongEdge = 1280, Preset = "fast", MaxrateKbps = 4000 });
        StringAssert.Contains(args, "-crf 23");
        StringAssert.Contains(args, "-preset fast");
        StringAssert.Contains(args, "min(1280,iw)");
        StringAssert.Contains(args, "-maxrate 4000k -bufsize 8000k");
    }

    [TestMethod]
    public void TranscodeArgs_ZeroValues_DisableCapAndCeiling()
    {
        var args = Args(new VideoSettings { Crf = 18, MaxLongEdge = 0, Preset = "slow", MaxrateKbps = 0 });
        Assert.IsFalse(args.Contains("force_original_aspect_ratio")); // no cap
        Assert.IsFalse(args.Contains("-maxrate")); // pure CRF
        StringAssert.Contains(args, "scale=trunc(iw/2)*2:trunc(ih/2)*2");
    }

    [TestMethod]
    public void ToneMapsOnlyRealHdr_AndOnlyWhenFfmpegCanDoIt()
    {
        // An HDR source, on a build that can convert it: do the real thing.
        var hdr = Args(new VideoSettings(), Caps(toneMap: true), isHdr: true);
        StringAssert.Contains(hdr, "zscale=t=linear:npl=100");
        StringAssert.Contains(hdr, "tonemap=tonemap=hable");

        // The same source on a build with no zscale (a stock Homebrew ffmpeg): we cannot convert it, so we
        // at least stop lying about it. Labelling is not tone-mapping and must not pretend to be.
        var noZscale = Args(new VideoSettings(), Caps(toneMap: false), isHdr: true);
        Assert.IsFalse(noZscale.Contains("tonemap"));
        StringAssert.Contains(noZscale, "setparams=color_primaries=bt709");

        // Ordinary SDR - including 10-bit SDR, which is why the HDR test is the transfer curve and not the
        // bit depth. Tone-mapping this would crush footage that was perfectly fine.
        var sdr = Args(new VideoSettings(), Caps(toneMap: true));
        Assert.IsFalse(sdr.Contains("tonemap"));
        Assert.IsFalse(sdr.Contains("zscale"));
        StringAssert.Contains(sdr, "setparams=color_primaries=bt709");
    }

    [TestMethod]
    public void HardwareEncodingOnlyWhenAskedForAndAvailable()
    {
        var wants = new VideoSettings { Encoder = VideoEncoder.Hardware, Crf = 22 };

        // Asked for it, and the machine proved at boot that it can.
        var gpu = Args(wants, Caps(gpu: true));
        StringAssert.Contains(gpu, "-vaapi_device /dev/dri/renderD128");
        StringAssert.Contains(gpu, "-c:v h264_vaapi");
        StringAssert.Contains(gpu, "-rc_mode CQP -qp 22");
        StringAssert.Contains(gpu, "format=nv12,hwupload"); // frames must reach the GPU's own memory
        Assert.IsFalse(gpu.Contains("libx264"));
        // VAAPI ignores -maxrate under constant-QP, so don't pretend to set a ceiling we aren't setting.
        Assert.IsFalse(gpu.Contains("-maxrate"));

        // Asked for it on a machine with no usable render device: silently, correctly, the CPU. There is
        // no such thing as half-encoding a video, so this can only ever be software.
        var noDevice = Args(wants, Caps(gpu: false));
        StringAssert.Contains(noDevice, "-c:v libx264");
        Assert.IsFalse(noDevice.Contains("vaapi"));
        Assert.IsFalse(noDevice.Contains("hwupload"));

        // Never asked for: the CPU, even on a machine that could.
        var software = Args(new VideoSettings(), Caps(gpu: true));
        StringAssert.Contains(software, "-c:v libx264");
        Assert.IsFalse(software.Contains("vaapi"));
    }

    [TestMethod]
    public void HardwareEncodingStillToneMapsAndScales()
    {
        // The colour and size work happens on the CPU and the result is uploaded, so turning hardware on
        // must not quietly cost an HDR source its tone-map or a 4K source its cap.
        var args = Args(new VideoSettings { Encoder = VideoEncoder.Hardware, MaxLongEdge = 1920 },
            Caps(toneMap: true, gpu: true), isHdr: true);
        StringAssert.Contains(args, "tonemap=tonemap=hable");
        StringAssert.Contains(args, "min(1920,iw)");
        // Order matters: convert, resize, then hand it to the GPU. hwupload has to come last.
        Assert.IsTrue(args.IndexOf("tonemap", StringComparison.Ordinal) < args.IndexOf("hwupload", StringComparison.Ordinal));
        Assert.IsTrue(args.IndexOf("scale=", StringComparison.Ordinal) < args.IndexOf("hwupload", StringComparison.Ordinal));
        StringAssert.EndsWith(args.TrimEnd(), "\"out.mp4\"");
    }

    private static EncoderCapabilities Caps(bool toneMap = false, bool gpu = false)
    {
        return new EncoderCapabilities(toneMap, gpu, "/dev/dri/renderD128");
    }

    private static string Args(VideoSettings settings, EncoderCapabilities? caps = null, bool isHdr = false)
    {
        return MediaProcessor.TranscodeArgs("in.mkv", "out.mp4", settings, caps ?? Caps(), isHdr);
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
