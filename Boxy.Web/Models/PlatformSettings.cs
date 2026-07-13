namespace Boxy.Web.Models;

/// <summary>
/// Platform-wide settings the admin controls, stored as one JSON row in the <c>Config</c> table.
/// Add fields here as new toggles are needed - no migration required.
/// </summary>
public class PlatformSettings
{
    /// <summary>When true, anyone can create an account at <c>/register</c>. Off by default.</summary>
    public bool RegistrationEnabled { get; set; }

    /// <summary>Days a regular user's new boxes and shares stay live before their link goes dead (and,
    /// a grace period later, are deleted). 0 = never expire. Admin content is always exempt.
    /// Defaults to 30 - applies to new content only; existing items keep whatever expiry they had.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Largest single upload (in MB) a regular user - or an anonymous drop-off into their box -
    /// may send. 0 = unlimited. Admins are always exempt. Defaults to 0 so nothing is capped until set.</summary>
    public int MaxUploadMb { get; set; }

    /// <summary>The upload cap in bytes, or 0 when unlimited.</summary>
    public long MaxUploadBytes => MaxUploadMb <= 0 ? 0 : (long)MaxUploadMb * 1024 * 1024;

    /// <summary>Default total storage (in MB) a regular user's content may occupy. 0 = unlimited. Admins
    /// are exempt, and a per-user override (<c>User.QuotaBytes</c>) takes precedence. Defaults to 0.</summary>
    public int DefaultUserQuotaMb { get; set; }

    /// <summary>The default per-user quota in bytes, or 0 when unlimited.</summary>
    public long DefaultUserQuotaBytes => DefaultUserQuotaMb <= 0 ? 0 : (long)DefaultUserQuotaMb * 1024 * 1024;

    /// <summary>Allow box webhooks to target private/loopback addresses. Off by default (blocks SSRF on
    /// a public instance); turn on for a self-hosted setup that posts to an internal service (n8n,
    /// a Slack relay, Home Assistant, ...).</summary>
    public bool AllowInternalWebhooks { get; set; }
}
