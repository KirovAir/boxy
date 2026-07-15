using System.Collections.Concurrent;
using System.Globalization;

namespace Boxy.Web.Services;

/// <summary>Where a queued or running conversion is in its lifecycle, for the progress the UI polls.</summary>
public enum ConversionStage
{
    /// <summary>Enqueued; the single worker has not reached it yet.</summary>
    Queued,

    /// <summary>Probing and poster extraction, before the encode.</summary>
    Preparing,

    /// <summary>The encode or remux itself: this is the stage whose <c>Percent</c> moves.</summary>
    Converting,

    /// <summary>Building the H.265 sidecar and saving.</summary>
    Finishing
}

/// <summary>One parsed ffmpeg <c>-progress</c> block: how far into the output it has written, and the
/// current encode speed relative to realtime (null when ffmpeg reports "N/A").</summary>
public readonly record struct FfmpegProgress(TimeSpan OutTime, double? Speed);

/// <summary>
/// Turns ffmpeg's <c>-progress pipe:1</c> line stream into <see cref="FfmpegProgress"/> snapshots. ffmpeg
/// emits blocks of <c>key=value</c> lines, each terminated by <c>progress=continue</c> (or a final
/// <c>progress=end</c>); this accumulates a block and yields a snapshot when it closes. Pure and fed one
/// line at a time, so it is unit-testable without a process.
/// </summary>
public sealed class FfmpegProgressParser
{
    private TimeSpan? outTime;
    private double? speed;

    /// <summary>Feed one stdout line. Returns a snapshot when a progress block just closed, else null.</summary>
    public FfmpegProgress? Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return null;
        }

        var key = line[..eq];
        var value = line[(eq + 1)..];
        switch (key)
        {
            case "out_time_us" or "out_time_ms":
                // Both are microseconds in ffmpeg's progress output (out_time_ms is a long-standing misnomer),
                // and they agree, so either one works; N/A early in the run just fails to parse and is skipped.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    outTime = TimeSpan.FromMilliseconds(us / 1000.0);
                }

                break;
            case "out_time":
                // "HH:MM:SS.ffffff", the human form - a fallback for any build that omits out_time_us.
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
                {
                    outTime = ts;
                }

                break;
            case "speed":
                speed = ParseSpeed(value);
                break;
            case "progress":
                if (outTime is { } elapsed)
                {
                    return new FfmpegProgress(elapsed, speed);
                }

                break;
        }

        return null;
    }

    // "1.02x", " 3x", or "N/A". Anything that isn't a positive number becomes null (unknown speed).
    private static double? ParseSpeed(string value)
    {
        var trimmed = value.Trim().TrimEnd('x');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}

/// <summary>A point-in-time view of a conversion, as the status endpoint hands it to the poller.</summary>
/// <param name="Stage">Lifecycle stage.</param>
/// <param name="Percent">0-100 through the encode, or null when it cannot be known (unknown source
/// duration, or a stage with no measurable progress) - the UI shows an indeterminate bar then.</param>
/// <param name="Speed">Encode speed relative to realtime (3.1 means 3.1x), or null.</param>
public readonly record struct ConversionSnapshot(ConversionStage Stage, int? Percent, double? Speed);

/// <summary>
/// The live progress of conversions, in memory, keyed by media item id. The worker writes to it as it runs;
/// the status endpoint reads it, so the poll the browser already runs can drive a progress bar without any
/// new realtime channel. Deliberately not persisted: a restart re-queues unfinished items and starts their
/// progress afresh, and a stale row would only mislead.
///
/// Keyed by id and safe for concurrent access, so it makes no assumption about the single-worker pipeline it
/// has today: if conversions are ever parallelised, each reports under its own id and nothing here changes.
/// </summary>
public sealed class ConversionProgress
{
    private readonly ConcurrentDictionary<int, ConversionSnapshot> live = new();

    /// <summary>Record where an item's conversion is now. Percent is clamped to 0-100; pass null for a stage
    /// with nothing measurable to show.</summary>
    public void Report(int mediaItemId, ConversionStage stage, int? percent = null, double? speed = null)
    {
        live[mediaItemId] = new ConversionSnapshot(stage, percent is { } p ? Math.Clamp(p, 0, 100) : null, speed);
    }

    /// <summary>Drop an item's progress once it is done, failed, or abandoned.</summary>
    public void Clear(int mediaItemId)
    {
        live.TryRemove(mediaItemId, out _);
    }

    /// <summary>The current progress of an item, or null when nothing is queued or running for it.</summary>
    public ConversionSnapshot? Get(int mediaItemId)
    {
        return live.TryGetValue(mediaItemId, out var snapshot) ? snapshot : null;
    }
}
