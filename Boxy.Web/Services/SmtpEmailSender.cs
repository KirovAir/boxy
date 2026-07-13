using Boxy.Web.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Boxy.Web.Services;

/// <summary>SMTP email via MailKit. Reads the effective settings per call (so admin edits apply without a
/// restart) and is enabled once the provider is "smtp" with a host configured.</summary>
public sealed class SmtpEmailSender(EmailSettingsProvider settingsProvider, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        return IsSmtp(await settingsProvider.GetEffectiveAsync(ct));
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetEffectiveAsync(ct);
        if (!IsSmtp(settings))
        {
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(settings.FromName, settings.From));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

            var security = settings.Smtp.Security?.Trim().ToLowerInvariant() switch
            {
                "none" => SecureSocketOptions.None,
                "ssl" => SecureSocketOptions.SslOnConnect,
                "starttls" => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.Auto
            };

            using var client = new SmtpClient();
            await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, security, ct);
            if (!string.IsNullOrWhiteSpace(settings.Smtp.User))
            {
                await client.AuthenticateAsync(settings.Smtp.User, settings.Smtp.Password ?? "", ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email send to {To} failed", toEmail);
            return false;
        }
    }

    private static bool IsSmtp(EmailSettings s)
    {
        return s.Provider.Trim().Equals("smtp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Smtp.Host);
    }
}
