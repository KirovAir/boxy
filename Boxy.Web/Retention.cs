namespace Boxy.Web;

/// <summary>
/// Content retention for regular users: their boxes and shares expire a configurable number of days
/// after creation, then a grace period later are deleted. Admin content never expires. One place for
/// the rules so the creation paths, the enforcement checks, and the sweep all agree.
/// </summary>
public static class Retention
{
    /// <summary>Days between a share/box's link-off (ExpiresAt) and its actual deletion. During this
    /// window the owner can still see and restore it; the public link is already dead.</summary>
    public const int GraceDays = 7;

    /// <summary>The link-off date for a newly created item, or null when it should never expire
    /// (the owner is an admin, or retention is switched off).</summary>
    public static DateTime? ExpiryFor(bool ownerIsAdmin, int retentionDays, DateTime nowUtc)
    {
        return ownerIsAdmin || retentionDays <= 0 ? null : nowUtc.AddDays(retentionDays);
    }

    /// <summary>True once the public link should be dead (past ExpiresAt).</summary>
    public static bool IsExpired(DateTime? expiresAt, DateTime nowUtc)
    {
        return expiresAt is DateTime e && e <= nowUtc;
    }

    /// <summary>Whether an item is visible to the anonymous public: a published, unexpired owner share.
    /// A bucket drop-off (<paramref name="bucketId"/> set) is NEVER public - only whoever manages it may
    /// see its bytes/poster - so publishing can't expose a file a stranger dropped into someone's box.</summary>
    public static bool IsPubliclyVisible(int? bucketId, bool published, DateTime? expiresAt, DateTime nowUtc)
    {
        return bucketId is null && published && !IsExpired(expiresAt, nowUtc);
    }

    /// <summary>The moment an expired item becomes eligible for deletion (ExpiresAt + grace).</summary>
    public static DateTime DeleteAfter(DateTime expiresAt)
    {
        return expiresAt.AddDays(GraceDays);
    }
}
