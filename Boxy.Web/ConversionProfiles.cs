using Boxy.Data.Entities;
using Boxy.Web.Models;

namespace Boxy.Web;

/// <summary>
/// Everything that varies between <see cref="ConversionProfile"/>s, in one place: the copy the uploader
/// reads, the encoder knobs the worker uses, and the parse of whatever arrives on the wire. The views, the
/// controllers and the worker all read from here, so a new profile is one arm added in one file rather
/// than a switch to find in five. Mirrors <see cref="MediaKinds"/>.
/// </summary>
public static class ConversionProfiles
{
    /// <summary>What we do when nobody has said otherwise: play everywhere, keep the good copy too.</summary>
    public const ConversionProfile Fallback = ConversionProfile.Best;

    /// <summary>The picker, in the order it is offered. Best first: it is the default and the safe answer.</summary>
    public static readonly ConversionProfile[] Choices =
    [
        ConversionProfile.Best, ConversionProfile.Universal, ConversionProfile.FullSize, ConversionProfile.AsUploaded
    ];

    public static string Label(ConversionProfile profile)
    {
        return profile switch
        {
            ConversionProfile.Best => "Best for every device",
            ConversionProfile.Universal => "Plays anywhere",
            ConversionProfile.FullSize => "Plays anywhere, full size",
            ConversionProfile.AsUploaded => "Don't convert it",
            _ => profile.ToString()
        };
    }

    public static string Help(ConversionProfile profile)
    {
        return profile switch
        {
            ConversionProfile.Best =>
                "Devices that can decode it play your video untouched, H.265 and HDR and all. Everyone else "
                + "gets an H.264 copy. Two files, so it takes the longest and uses the most disk. Nobody gets "
                + "a black screen.",
            ConversionProfile.Universal =>
                "One H.264 file, up to the size set in Settings. Smallest and quickest, and everybody gets "
                + "the same thing.",
            ConversionProfile.FullSize =>
                "One H.264 file at the size you uploaded. For 4K and screen recordings, where the detail is "
                + "the point. It will be big and it will take a while.",
            ConversionProfile.AsUploaded =>
                "Ships exactly the file you uploaded. Instant, and no extra disk. A viewer whose browser "
                + "doesn't have the codec gets nothing to play, so pick this when you know who's watching.",
            _ => ""
        };
    }

    /// <summary>Read a profile off the wire. Anything unrecognised - a typo, a stale form, a hand-crafted
    /// query string on an endpoint that takes no antiforgery token - is null, and the caller falls back to
    /// a default it chose itself. <c>Enum.TryParse</c> alone is not enough: it happily parses "9" into an
    /// undefined member.</summary>
    public static ConversionProfile? Parse(string? value)
    {
        return Enum.TryParse<ConversionProfile>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
    }

    /// <summary>The profile in force for an upload: what the uploader picked, else the box's default, else
    /// the instance default. One chain, so no caller invents its own.</summary>
    public static ConversionProfile Resolve(ConversionProfile? chosen, ConversionProfile? boxDefault, ConversionProfile instanceDefault)
    {
        return chosen ?? boxDefault ?? instanceDefault;
    }

    /// <summary>True when this profile ever re-encodes. <see cref="ConversionProfile.AsUploaded"/> never does.</summary>
    public static bool Transcodes(ConversionProfile profile)
    {
        return profile != ConversionProfile.AsUploaded;
    }

    /// <summary>True when an H.265 source is worth keeping alongside the H.264 copy, as a source browsers
    /// with a decoder take first. Only <see cref="ConversionProfile.Best"/> pays the extra file for it.</summary>
    public static bool WantsHq(ConversionProfile profile)
    {
        return profile == ConversionProfile.Best;
    }

    /// <summary>
    /// The encoder knobs for a transcode under this profile. The admin's global settings are the base;
    /// a profile only ever overrides them, and the result is re-<see cref="VideoSettings.Normalized"/>d,
    /// so nothing off the wire can reach an ffmpeg argument. The profile SELECTS settings; it never
    /// carries them.
    /// </summary>
    public static VideoSettings Settings(ConversionProfile profile, VideoSettings global)
    {
        if (profile != ConversionProfile.FullSize)
        {
            return global.Normalized();
        }

        // Full size means exactly that: no resolution cap, and no bitrate ceiling to smear the detail the
        // uploader chose this for. Quality is still CRF-bounded.
        return new VideoSettings
        {
            Crf = global.Crf,
            Preset = global.Preset,
            MaxLongEdge = 0,
            MaxrateKbps = 0
        }.Normalized();
    }

    /// <summary>The suffix of the produced web file, so a lane's output can never overwrite another
    /// lane's output for the same bytes (the blob name is content-addressed, and the content is the same).
    /// Also what tells the heal, cheaply, which lane an existing file came out of.</summary>
    public static string WebSuffix(ConversionProfile profile)
    {
        return profile switch
        {
            ConversionProfile.FullSize => "-h264-full.mp4",
            ConversionProfile.AsUploaded => "-asis.mp4",
            _ => "-h264.mp4"
        };
    }

    /// <summary>The suffix of the kept H.265 rendition. Not per-profile: only one profile makes one.</summary>
    public const string HqSuffix = "-hevc.mp4";

    private static readonly string[] RenditionSuffixes =
        ["-h264.mp4", "-h264-full.mp4", "-asis.mp4", HqSuffix];

    /// <summary>
    /// True when a blob name is something the worker DERIVED, rather than an uploaded original
    /// (<c>{hash}{ext}</c>) or a poster (<c>{hash}.jpg</c> / <c>{hash}-thumb.jpg</c>).
    ///
    /// This is a safety rail on deletion, and it earns its keep because <c>HqFileName</c> can legitimately
    /// point AT the original blob: when an upload is already a faststart hvc1 mp4 there is nothing to
    /// produce, so the rendition IS the upload. A cleanup that treats every old rendition name as its own
    /// to delete would then delete the original bytes - and after a replace, the item's hash no longer
    /// matches them, so the obvious "is this my own original?" guard misses and another item that shares
    /// those bytes by dedup loses its file.
    ///
    /// An original is only ever deleted along with the item that owns it, on the paths that check the hash.
    /// </summary>
    public static bool IsDerivedRendition(string name)
    {
        return RenditionSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
    }
}
