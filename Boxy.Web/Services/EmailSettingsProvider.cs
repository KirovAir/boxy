using Boxy.Data;
using Boxy.Data.Extensions;
using Boxy.Web.Models;
using Microsoft.AspNetCore.DataProtection;

namespace Boxy.Web.Services;

/// <summary>
/// Resolves the effective email configuration. Once an admin saves settings in-app they live in the DB,
/// with the SMTP password encrypted via Data Protection (whose keys are themselves in the DB, so nothing
/// sensitive is written to disk). Until then the appsettings/env values are used, so existing deployments
/// keep working. Also handles encrypted save with "leave blank to keep the current password".
/// </summary>
public class EmailSettingsProvider(
    IDbContextFactory<AppDbContext> dbFactory,
    IConfiguration config,
    IDataProtectionProvider dataProtection)
{
    private IDataProtector Protector => dataProtection.CreateProtector("Boxy.EmailSettings.Password");

    /// <summary>The effective settings (password in clear, ready to use) and whether they came from the
    /// DB (true) or the environment fallback (false).</summary>
    public async Task<(EmailSettings Settings, bool FromDb)> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.HasSettingsAsync<EmailSettings>(ct))
        {
            var stored = await db.GetSettingsAsync<EmailSettings>(ct);
            stored.Smtp.Password = Unprotect(stored.Smtp.Password);
            return (stored, true);
        }

        var env = config.GetSection(EmailSettings.SectionName).Get<EmailSettings>() ?? new EmailSettings();
        return (env, false);
    }

    public async Task<EmailSettings> GetEffectiveAsync(CancellationToken ct = default)
    {
        return (await GetAsync(ct)).Settings;
    }

    /// <summary>Persists settings from the admin form, encrypting the password. An empty incoming password
    /// keeps the one already stored, so the admin never has to retype the secret.</summary>
    public async Task SaveAsync(EmailSettings incoming, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(incoming.Smtp.Password))
        {
            // Blank = keep the current effective password - the DB one if set, otherwise the env
            // fallback - so a first save that follows the "leave blank to keep" hint never wipes a
            // working env-provided secret.
            var (current, _) = await GetAsync(ct);
            incoming.Smtp.Password = Protect(current.Smtp.Password);
        }
        else
        {
            incoming.Smtp.Password = Protect(incoming.Smtp.Password);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SaveSettingsAsync(incoming, ct);
    }

    private string? Protect(string? plain)
    {
        return string.IsNullOrEmpty(plain) ? plain : Protector.Protect(plain);
    }

    private string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        try
        {
            return Protector.Unprotect(stored);
        }
        catch
        {
            return null;
        } // key gone / value tampered - treat as no password
    }
}
