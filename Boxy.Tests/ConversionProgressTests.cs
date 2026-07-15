using Boxy.Web.Services;

namespace Boxy.Tests;

[TestClass]
public class ConversionProgressTests
{
    // ── FfmpegProgressParser ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parser_emits_a_snapshot_only_when_the_block_closes()
    {
        var parser = new FfmpegProgressParser();

        Assert.IsNull(parser.Feed("frame=100"));
        Assert.IsNull(parser.Feed("out_time_us=5000000"));
        Assert.IsNull(parser.Feed("speed=2.0x"));

        var snap = parser.Feed("progress=continue");
        Assert.IsNotNull(snap);
        Assert.AreEqual(5.0, snap.Value.OutTime.TotalSeconds, 0.001);
        Assert.AreEqual(2.0, snap.Value.Speed!.Value, 0.001);
    }

    [TestMethod]
    public void Parser_skips_a_block_with_no_usable_time_and_reports_unknown_speed()
    {
        var parser = new FfmpegProgressParser();

        // Early in a run ffmpeg emits N/A; nothing to report yet.
        parser.Feed("out_time_us=N/A");
        Assert.IsNull(parser.Feed("progress=continue"));

        parser.Feed("out_time_us=1000000");
        parser.Feed("speed=N/A");
        var snap = parser.Feed("progress=continue");
        Assert.IsNotNull(snap);
        Assert.AreEqual(1.0, snap.Value.OutTime.TotalSeconds, 0.001);
        Assert.IsNull(snap.Value.Speed);
    }

    [TestMethod]
    public void Parser_falls_back_to_the_out_time_timestamp()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time=00:00:12.500000");

        var snap = parser.Feed("progress=end");
        Assert.IsNotNull(snap);
        Assert.AreEqual(12.5, snap.Value.OutTime.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void Parser_ignores_blank_and_malformed_lines()
    {
        var parser = new FfmpegProgressParser();
        Assert.IsNull(parser.Feed(""));
        Assert.IsNull(parser.Feed("no-equals-here"));
        Assert.IsNull(parser.Feed("=leading-equals"));
    }

    // ── ConversionProgress store ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Store_reports_reads_and_clears()
    {
        var store = new ConversionProgress();
        Assert.IsNull(store.Get(7));

        store.Report(7, ConversionStage.Converting, 42, 3.1);
        var snap = store.Get(7);
        Assert.IsNotNull(snap);
        Assert.AreEqual(ConversionStage.Converting, snap.Value.Stage);
        Assert.AreEqual(42, snap.Value.Percent!.Value);
        Assert.AreEqual(3.1, snap.Value.Speed!.Value, 0.001);

        store.Clear(7);
        Assert.IsNull(store.Get(7));
    }

    [TestMethod]
    public void Store_clamps_percent_and_keeps_null()
    {
        var store = new ConversionProgress();

        store.Report(1, ConversionStage.Converting, 150);
        Assert.AreEqual(100, store.Get(1)!.Value.Percent!.Value);

        store.Report(2, ConversionStage.Converting, -10);
        Assert.AreEqual(0, store.Get(2)!.Value.Percent!.Value);

        store.Report(3, ConversionStage.Preparing);
        Assert.IsNull(store.Get(3)!.Value.Percent);
    }
}
