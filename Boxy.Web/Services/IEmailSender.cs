namespace Boxy.Web.Services;

/// <summary>
/// Sends transactional email using the current configuration (resolved per call, so admin changes take
/// effect without a restart). Callers check <see cref="IsEnabledAsync"/> before sending and treat a
/// <c>false</c> return as a transient failure worth retrying.
/// </summary>
public interface IEmailSender
{
    /// <summary>True when a usable provider is currently configured.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>Sends one message. Returns true on delivery, false when disabled or on failure (never throws).</summary>
    Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}
