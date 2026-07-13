using System.Text;

namespace Boxy.Web;

/// <summary>
/// A stable, friendly identity for an anonymous drop-off uploader, derived purely from their opaque
/// <c>UploaderToken</c> (the <c>boxy_uid</c> cookie GUID). The same token always yields the same
/// name + colour, on every machine and every run, with no stored state and no schema change - so it
/// works retroactively on every file already dropped. This is how a box owner tells one anonymous
/// visitor's uploads from another's ("the Quiet Otter sent these three").
///
/// The token itself is a delete-credential (a visitor proves ownership of their uploads with it in
/// <c>DeleteMine</c>), so it must never reach a URL or the screen. <see cref="Code"/> is a one-way
/// hash of the token that is safe to put in a filter link; the token stays server-side and the owner
/// filters by the code, which the controller resolves back to a token among the box's own uploaders.
/// </summary>
public sealed record UploaderIdentity(string Name, string Slug, string Code, string Color)
{
    // Colour-neutral so the adjective never fights the dot's hue ("Amber Otter" in a blue dot reads
    // as a mistake). 24 x 24 = 576 combinations - ample for the handful of visitors one box sees, so
    // a collision (two visitors sharing a name in the same box) is vanishingly rare.
    private static readonly string[] Adjectives =
    [
        "Brave", "Calm", "Clever", "Cosy", "Eager", "Gentle", "Happy", "Jolly",
        "Keen", "Kind", "Lively", "Lucky", "Merry", "Nimble", "Plucky", "Quiet",
        "Rapid", "Shy", "Snug", "Spry", "Sunny", "Swift", "Tidy", "Witty"
    ];

    private static readonly string[] Animals =
    [
        "Otter", "Heron", "Fox", "Badger", "Robin", "Marten", "Hare", "Finch",
        "Lynx", "Wren", "Stoat", "Ibis", "Vole", "Newt", "Crane", "Gecko",
        "Panda", "Koala", "Tapir", "Quokka", "Lemur", "Bison", "Moose", "Seal"
    ];

    /// <summary>Derive a stable identity from an uploader token, or <c>null</c> for an owner/admin item
    /// (which has no token). Pure and deterministic.</summary>
    public static UploaderIdentity? For(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        // Independent slices of one well-mixed 64-bit hash: low bits pick the adjective, the next
        // decade picks the animal, high bits pick the hue, and the low 32 bits are the public code.
        var h = Fnv1a64(token);
        var adjective = Adjectives[(int)(h % (ulong)Adjectives.Length)];
        var animal = Animals[(int)(h / (ulong)Adjectives.Length % (ulong)Animals.Length)];
        var hue = (int)((h >> 40) % 360);

        var name = $"{adjective} {animal}";
        // Fixed saturation/lightness keeps every dot legible on the light kraft background and against
        // each other; only the hue varies, so the palette stays cohesive.
        return new UploaderIdentity(name, name.Replace(' ', '-').ToLowerInvariant(),
            (h & 0xFFFFFFFF).ToString("x8"), $"hsl({hue} 62% 45%)");
    }

    // FNV-1a (64-bit). A fixed, well-known hash - NOT string.GetHashCode(), which is randomized per
    // process since .NET Core and so would hand the same visitor a different animal after every restart.
    private static ulong Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var h = offset;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= prime;
        }

        return h;
    }
}
