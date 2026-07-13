using System.Text.RegularExpressions;
using Boxy.Web;

namespace Boxy.Tests;

[TestClass]
public class UploaderIdentityTests
{
    [TestMethod]
    public void For_IsDeterministic_SameTokenSameIdentity()
    {
        // The entire feature rests on this: a visitor's token must map to the same name + colour every
        // time (and across processes - the helper uses FNV-1a, not the per-process-seeded GetHashCode).
        const string token = "b1c4e0aa-6f2d-4b3a-9c11-77a0f3d21e58";
        Assert.AreEqual(UploaderIdentity.For(token), UploaderIdentity.For(token));
    }

    [TestMethod]
    public void For_NullOrEmpty_IsNull()
    {
        // Owner/admin items carry no uploader token, so they get no chip.
        Assert.IsNull(UploaderIdentity.For(null));
        Assert.IsNull(UploaderIdentity.For(""));
    }

    [TestMethod]
    public void For_Code_IsEightLowerHexChars_AndUrlSafe()
    {
        // Code is what travels in the filter URL, so it must be opaque and URL-safe (never the raw token).
        var id = UploaderIdentity.For("some-token-value")!;
        Assert.IsTrue(Regex.IsMatch(id.Code, "^[0-9a-f]{8}$"), $"unexpected code '{id.Code}'");
    }

    [TestMethod]
    public void For_ColorAndSlug_HaveExpectedShape()
    {
        var id = UploaderIdentity.For("another-token")!;
        Assert.IsTrue(id.Color.StartsWith("hsl("), $"unexpected colour '{id.Color}'");
        Assert.AreEqual(id.Name.Replace(' ', '-').ToLowerInvariant(), id.Slug);
        Assert.IsTrue(id.Name.Contains(' '), "name should read 'Adjective Animal'");
    }

    [TestMethod]
    public void For_SpreadsAcrossManyTokens()
    {
        // Different tokens should land on different identities: codes are a 32-bit hash (collisions among
        // a few hundred are effectively impossible), and names should fan out across the 576-combo space.
        var ids = Enumerable.Range(0, 200)
            .Select(i => UploaderIdentity.For(new Guid(i, (short)(i * 7), (short)(i * 13), 1, 2, 3, 4, 5, 6, 7, 8).ToString())!)
            .ToList();

        Assert.AreEqual(ids.Count, ids.Select(x => x.Code).Distinct().Count(), "codes collided");
        Assert.IsTrue(ids.Select(x => x.Name).Distinct().Count() > 100, "names clustered too tightly");
    }
}
