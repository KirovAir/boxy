using Boxy.Web;

namespace Boxy.Tests;

[TestClass]
public class RetentionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void IsPubliclyVisible_PublishedUnexpiredShare_IsVisible()
    {
        Assert.IsTrue(Retention.IsPubliclyVisible(bucketId: null, published: true, expiresAt: null, Now));
        Assert.IsTrue(Retention.IsPubliclyVisible(null, true, Now.AddDays(1), Now)); // expires in the future
    }

    [TestMethod]
    public void IsPubliclyVisible_DropOff_IsNeverPublic_EvenIfPublished()
    {
        // The security invariant: a bucket drop-off (BucketId set) is never publicly served, even if some
        // path set Published on it - a stranger's dropped file can't be exposed by publishing.
        Assert.IsFalse(Retention.IsPubliclyVisible(bucketId: 5, published: true, expiresAt: null, Now));
    }

    [TestMethod]
    public void IsPubliclyVisible_UnpublishedOrExpired_IsNotPublic()
    {
        Assert.IsFalse(Retention.IsPubliclyVisible(null, published: false, null, Now));
        Assert.IsFalse(Retention.IsPubliclyVisible(null, published: true, Now.AddDays(-1), Now)); // expired
    }
}
