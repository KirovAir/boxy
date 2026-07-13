using Boxy.Data;
using Boxy.Data.Extensions;
using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Resolves the effective video-encoding settings. These are edited in-app under Settings -> Video and
/// stored as one JSON row in the <c>Config</c> table; until an admin saves them, the built-in defaults on
/// <see cref="VideoSettings"/> apply (they are no longer read from the environment). Mirrors
/// <see cref="EmailSettingsProvider"/>: resolved per call (no cache), so an admin's save applies to the
/// very next transcode without a restart and without any invalidation to get wrong. The read costs one
/// indexed row lookup, and only on a transcode - a job that then runs for minutes.
///
/// Values are normalized on the way out as well as in, so a hand-edited DB row can neither inject ffmpeg
/// arguments nor trigger a lossless, disk-filling encode.
/// </summary>
public class VideoSettingsProvider(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>The effective settings and whether they came from the DB (true) or the built-in
    /// defaults (false, i.e. never configured in-app).</summary>
    public async Task<(VideoSettings Settings, bool FromDb)> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.HasSettingsAsync<VideoSettings>(ct))
        {
            var stored = await db.GetSettingsAsync<VideoSettings>(ct);
            return (stored.Normalized(), true);
        }

        return (new VideoSettings().Normalized(), false);
    }

    public async Task<VideoSettings> GetEffectiveAsync(CancellationToken ct = default)
    {
        return (await GetAsync(ct)).Settings;
    }

    /// <summary>Persists settings from the admin form, clamped and allowlisted.</summary>
    public async Task SaveAsync(VideoSettings incoming, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SaveSettingsAsync(incoming.Normalized(), ct);
    }
}
