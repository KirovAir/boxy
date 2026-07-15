using System.Diagnostics;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boxy.Tests;

/// <summary>
/// End-to-end check of the live-progress plumbing (real ffmpeg -> DrainProgressAsync -> FfmpegProgressParser
/// -> callback). Runs a genuine CPU transcode, so it is skipped (Inconclusive, not failed) wherever ffmpeg
/// is not on PATH - the parser and store are covered without ffmpeg by ConversionProgressTests.
/// </summary>
[TestClass]
public class FfmpegProgressIntegrationTests
{
    [TestMethod]
    public async Task Transcode_reports_live_progress()
    {
        if (!FfmpegAvailable())
        {
            Assert.Inconclusive("ffmpeg is not on PATH; skipping the live-progress integration test.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "boxy-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "in.mp4");
        var output = Path.Combine(dir, "out.mp4");

        try
        {
            // A short synthetic clip with a video and audio track, so the real transcode path runs.
            Run("ffmpeg", $"-y -f lavfi -i testsrc=duration=6:size=640x360:rate=30 " +
                          $"-f lavfi -i sine=frequency=800:duration=6 -c:v libx264 -preset ultrafast " +
                          $"-pix_fmt yuv420p -c:a aac -shortest \"{input}\"");
            Assert.IsTrue(new FileInfo(input).Length > 0, "failed to create the test input");

            var processor = new MediaProcessor(
                Options.Create(new FfmpegSettings()), NullLogger<MediaProcessor>.Instance);

            var snapshots = new List<FfmpegProgress>();
            var settings = new VideoSettings
            {
                Encoder = VideoEncoder.Software, Crf = 30, Preset = "slow", MaxLongEdge = 0, MaxrateKbps = 0
            };
            var caps = new EncoderCapabilities(CanToneMap: false, CanEncodeOnGpu: false, VaapiDevice: "");

            var encoder = await processor.TranscodeWebAsync(input, output, settings, caps, sourceIsHdr: false,
                onProgress: p =>
                {
                    lock (snapshots)
                    {
                        snapshots.Add(p);
                    }
                });

            Assert.AreEqual(VideoEncoder.Software, encoder, "the software transcode should have succeeded");
            Assert.IsTrue(new FileInfo(output).Length > 0, "no output file was produced");
            Assert.IsTrue(snapshots.Count > 0, "no progress was captured from ffmpeg -progress");
            Assert.IsTrue(snapshots.Max(s => s.OutTime.TotalSeconds) > 1.0,
                "progress never advanced through the clip");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort temp cleanup */
            }
        }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            return Run("ffmpeg", "-version", throwOnError: false) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int Run(string exe, string args, bool throwOnError = true)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        proc.WaitForExit(120_000);
        if (throwOnError && proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{exe} {args} exited {proc.ExitCode}");
        }

        return proc.ExitCode;
    }
}
