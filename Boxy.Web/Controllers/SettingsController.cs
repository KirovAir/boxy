using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Extensions;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

// Admin-only platform area: the registration toggle and user management.
[Authorize(Roles = UserService.AdminRole)]
[Route("settings")]
public class SettingsController(
    UserService users,
    IDbContextFactory<AppDbContext> dbFactory,
    IBlobStore blobs,
    IConfiguration config,
    EmailSettingsProvider emailProvider,
    VideoSettingsProvider videoProvider,
    IEmailSender emailSender) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return View(await db.GetSettingsAsync<PlatformSettings>(ct));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(bool registrationEnabled, int retentionDays, int maxUploadMb, int defaultUserQuotaMb, bool allowInternalWebhooks, CancellationToken ct)
    {
        retentionDays = Math.Clamp(retentionDays, 0, 3650);
        maxUploadMb = Math.Clamp(maxUploadMb, 0, 1024 * 1024);
        defaultUserQuotaMb = Math.Clamp(defaultUserQuotaMb, 0, 100 * 1024 * 1024);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SaveSettingsAsync(new PlatformSettings
        {
            RegistrationEnabled = registrationEnabled,
            RetentionDays = retentionDays,
            MaxUploadMb = maxUploadMb,
            DefaultUserQuotaMb = defaultUserQuotaMb,
            AllowInternalWebhooks = allowInternalWebhooks
        }, ct);
        this.FlashSuccess("Platform settings saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("email")]
    public async Task<IActionResult> Email(CancellationToken ct)
    {
        var (s, fromDb) = await emailProvider.GetAsync(ct);
        return View(new EmailSettingsViewModel
        {
            Enabled = string.Equals(s.Provider, "smtp", StringComparison.OrdinalIgnoreCase),
            Host = s.Smtp.Host,
            Port = s.Smtp.Port,
            Security = s.Smtp.Security,
            From = s.From,
            FromName = s.FromName,
            User = s.Smtp.User,
            PasswordSet = !string.IsNullOrEmpty(s.Smtp.Password),
            FromDb = fromDb,
            AdminEmail = await CurrentEmailAsync(ct)
        });
    }

    [HttpPost("email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Email(bool enabled, string? host, int port, string? security, string? from, string? fromName, string? user, string? password, CancellationToken ct)
    {
        await emailProvider.SaveAsync(new EmailSettings
        {
            Provider = enabled ? "smtp" : "none",
            From = string.IsNullOrWhiteSpace(from) ? "boxy@localhost" : from.Trim(),
            FromName = string.IsNullOrWhiteSpace(fromName) ? "Boxy" : fromName.Trim(),
            Smtp = new SmtpSettings
            {
                Host = host?.Trim() ?? "",
                Port = port is <= 0 or > 65535 ? 587 : port,
                User = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
                Password = password, // blank keeps the current one (handled in the provider)
                Security = string.IsNullOrWhiteSpace(security) ? "auto" : security.Trim()
            }
        }, ct);

        this.FlashSuccess("Email settings saved.");
        return RedirectToAction(nameof(Email));
    }

    [HttpPost("email/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmail(CancellationToken ct)
    {
        if (!await emailSender.IsEnabledAsync(ct))
        {
            this.FlashError("Enable and save SMTP settings before sending a test.");
            return RedirectToAction(nameof(Email));
        }

        var address = await CurrentEmailAsync(ct);
        if (address is null || !address.Contains('@'))
        {
            this.FlashError("Your account needs a valid email address to receive the test.");
            return RedirectToAction(nameof(Email));
        }

        const string text = "This is a test email from Boxy. If you're reading it, SMTP is working.";
        var ok = await emailSender.SendAsync(address, "Boxy test email", $"<p>{text}</p>", text, ct);
        if (ok)
        {
            this.FlashSuccess($"Test email sent to {address}.");
        }
        else
        {
            this.FlashError("Could not send the test email - check the settings and your SMTP server.");
        }

        return RedirectToAction(nameof(Email));
    }

    private async Task<string?> CurrentEmailAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().Where(u => u.Id == User.GetUserId()).Select(u => u.Email).FirstOrDefaultAsync(ct);
    }

    [HttpGet("video")]
    public async Task<IActionResult> Video(CancellationToken ct)
    {
        var (s, fromDb) = await videoProvider.GetAsync(ct);
        return View(new VideoSettingsViewModel
        {
            Crf = s.Crf,
            MaxLongEdge = s.MaxLongEdge,
            Preset = s.Preset,
            MaxrateKbps = s.MaxrateKbps,
            DefaultProfile = s.DefaultProfile,
            FromDb = fromDb
        });
    }

    [HttpPost("video")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Video(int crf, int maxLongEdge, string? preset, int maxrateKbps,
        string? defaultProfile, CancellationToken ct)
    {
        // Settings are stored as one JSON blob, so a field the form doesn't send is not "unchanged", it is
        // gone: build the new value from the stored one rather than from nothing.
        var current = await videoProvider.GetEffectiveAsync(ct);

        // Clamping and the preset allowlist live in VideoSettings.Normalized(), applied by the provider -
        // one choke point for the form, the environment fallback, and a hand-edited DB row alike.
        await videoProvider.SaveAsync(new VideoSettings
        {
            Crf = crf,
            MaxLongEdge = maxLongEdge,
            Preset = preset ?? "",
            MaxrateKbps = maxrateKbps,
            DefaultProfile = ConversionProfiles.Parse(defaultProfile) ?? current.DefaultProfile
        }, ct);

        this.FlashSuccess("Video settings saved. They apply to videos uploaded from now on.");
        return RedirectToAction(nameof(Video));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var accounts = await db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Email, u.Username, u.IsActive })
            .ToListAsync(ct);

        // Aggregate per owner in a few grouped queries, then merge in memory - avoids a correlated
        // subquery per user. Shares are a user's own media; drop-offs are attributed to the box owner.
        var shareAgg = await db.MediaItems.Where(m => m.BucketId == null)
            .GroupBy(m => m.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count(), Bytes = g.Sum(m => (long?)m.SizeBytes) ?? 0 })
            .ToDictionaryAsync(x => x.OwnerId, ct);

        var boxAgg = await db.Buckets
            .GroupBy(b => b.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OwnerId, ct);

        var dropAgg = await db.MediaItems.Where(m => m.BucketId != null)
            .GroupBy(m => m.Bucket!.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count(), Bytes = g.Sum(m => (long?)m.SizeBytes) ?? 0 })
            .ToDictionaryAsync(x => x.OwnerId, ct);

        var rows = accounts.Select(u =>
        {
            shareAgg.TryGetValue(u.Id, out var s);
            boxAgg.TryGetValue(u.Id, out var b);
            dropAgg.TryGetValue(u.Id, out var d);
            return new UserUsageRow(u.Id, u.Email, u.Username, u.IsActive,
                s?.Count ?? 0, b?.Count ?? 0, d?.Count ?? 0, (s?.Bytes ?? 0) + (d?.Bytes ?? 0));
        }).ToList();

        var usage = await blobs.GetUsageAsync(ct);

        // Original source files, deduplicated: one physical blob per distinct content (hash+extension),
        // so this matches the original blobs on disk. Posters and web renditions are the rest of the
        // stored footprint. Logical size counts every item, so (logical - original) is what dedup saved.
        var originals = await db.MediaItems
            .GroupBy(m => new { m.ContentHash, m.Extension })
            .Select(g => g.Max(m => m.SizeBytes))
            .ToListAsync(ct);
        var originalBytes = originals.Sum();
        var logicalBytes = await db.MediaItems.SumAsync(m => (long?)m.SizeBytes, ct) ?? 0;

        return View(new StatsViewModel
        {
            StoredBytes = usage.TotalBytes,
            StoredObjects = usage.ObjectCount,
            StorageProvider = config["Storage:Provider"] ?? "filesystem",
            OriginalBytes = originalBytes,
            OriginalCount = originals.Count,
            DedupSavedBytes = Math.Max(0, logicalBytes - originalBytes),
            UserCount = accounts.Count,
            ActiveUserCount = accounts.Count(a => a.IsActive),
            BoxCount = await db.Buckets.CountAsync(ct),
            ShareCount = await db.MediaItems.CountAsync(m => m.BucketId == null, ct),
            DropOffCount = await db.MediaItems.CountAsync(m => m.BucketId != null, ct),
            Users = rows,
            Config = LoadedConfig(await videoProvider.GetEffectiveAsync(ct))
        });
    }

    // A read-only, secret-free view of what the instance booted with, for the stats page. Sensitive
    // values (keys, passwords, connection strings) are reported as set/not set - never their contents.
    // Media processing shows the EFFECTIVE video settings (in-app if saved, else the environment).
    private List<ConfigGroup> LoadedConfig(VideoSettings video)
    {
        static string SetOrNot(string? v)
        {
            return string.IsNullOrWhiteSpace(v) ? "not set" : "set";
        }

        var storage = config.GetSection(StorageSettings.SectionName).Get<StorageSettings>() ?? new StorageSettings();
        var email = config.GetSection(EmailSettings.SectionName).Get<EmailSettings>() ?? new EmailSettings();

        var storageItems = new List<ConfigItem> { new("Driver", storage.Provider) };
        switch (storage.Provider.Trim().ToLowerInvariant())
        {
            case "s3":
                storageItems.Add(new("Bucket", storage.S3.Bucket));
                storageItems.Add(new("Region", storage.S3.Region));
                storageItems.Add(new("Endpoint", string.IsNullOrWhiteSpace(storage.S3.ServiceUrl) ? "AWS default" : storage.S3.ServiceUrl!));
                storageItems.Add(new("Path-style", storage.S3.ForcePathStyle ? "yes" : "no"));
                storageItems.Add(new("Credentials", string.IsNullOrWhiteSpace(storage.S3.AccessKey) || string.IsNullOrWhiteSpace(storage.S3.SecretKey) ? "not set" : "set"));
                break;
            case "azure":
                storageItems.Add(new("Container", storage.Azure.Container));
                storageItems.Add(new("Connection string", SetOrNot(storage.Azure.ConnectionString)));
                break;
            default:
                storageItems.Add(new("Path", config["StoragePath"] ?? "storage"));
                break;
        }

        var emailItems = new List<ConfigItem> { new("Provider", email.Provider) };
        if (email.Provider.Trim().Equals("smtp", StringComparison.OrdinalIgnoreCase))
        {
            emailItems.Add(new("Server", $"{email.Smtp.Host}:{email.Smtp.Port}"));
            emailItems.Add(new("Security", email.Smtp.Security));
            emailItems.Add(new("From", $"{email.FromName} <{email.From}>"));
            emailItems.Add(new("Authentication", string.IsNullOrWhiteSpace(email.Smtp.User) ? "none" : "enabled"));
        }

        var mediaItems = new List<ConfigItem>
        {
            new("Preset", video.Preset),
            new("Quality (CRF)", video.Crf.ToString()),
            new("Max resolution", video.MaxLongEdge > 0 ? $"{video.MaxLongEdge}px long edge" : "no cap"),
            new("Max bitrate", video.MaxrateKbps > 0 ? $"{video.MaxrateKbps} kbps" : "no ceiling")
        };

        var generalItems = new List<ConfigItem>
        {
            new("Public base URL", string.IsNullOrWhiteSpace(config["PublicBaseUrl"]) ? "uses request host" : config["PublicBaseUrl"]!),
            new("Database", "SQLite")
        };

        return
        [
            new ConfigGroup("Storage", storageItems),
            new ConfigGroup("Email", emailItems),
            new ConfigGroup("Media processing", mediaItems),
            new ConfigGroup("General", generalItems)
        ];
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new UserRow(
                u.Id, u.Email, u.Username, u.Name, u.Role, u.IsActive, u.CreatedDate,
                db.Buckets.Count(b => b.OwnerId == u.Id),
                db.MediaItems.Count(m => m.BucketId == null && m.OwnerId == u.Id),
                db.MediaItems.Where(m => m.OwnerId == u.Id || (m.BucketId != null && m.Bucket!.OwnerId == u.Id))
                    .Sum(m => (long?)m.SizeBytes) ?? 0,
                u.QuotaBytes))
            .ToListAsync(ct);

        return View(new SettingsUsersViewModel
        {
            Users = rows,
            CurrentUserId = User.GetUserId(),
            ActiveAdminCount = await users.ActiveAdminCountAsync(ct),
            DefaultQuotaBytes = (await db.GetSettingsAsync<PlatformSettings>(ct)).DefaultUserQuotaBytes
        });
    }

    [HttpPost("users/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(string email, string? username, string password, string role, CancellationToken ct)
    {
        var parsedRole = string.Equals(role, nameof(UserRole.Admin), StringComparison.OrdinalIgnoreCase)
            ? UserRole.Admin
            : UserRole.User;
        var (user, error) = await users.RegisterAsync(email, username, password, null, parsedRole, ct);
        if (user is null)
        {
            this.FlashError(error ?? "Could not create the account.");
        }
        else
        {
            this.FlashSuccess($"Account {user.Email} created.");
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUser(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            this.FlashError("That account no longer exists.");
            return RedirectToAction(nameof(Users));
        }

        if (!user.IsActive)
        {
            await users.SetActiveAsync(id, true, ct);
            this.FlashSuccess($"{user.Email} enabled.");
        }
        else if (await users.TryDisableAsync(id, ct))
        {
            this.FlashSuccess($"{user.Email} disabled.");
        }
        else
        {
            // The atomic guard refused: this is the last active admin.
            this.FlashError("This is the only active admin - promote or add another before disabling it.");
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole(int id, string role, CancellationToken ct)
    {
        if (id == User.GetUserId())
        {
            this.FlashError("You can't change your own role.");
            return RedirectToAction(nameof(Users));
        }

        var target = string.Equals(role, nameof(UserRole.Admin), StringComparison.OrdinalIgnoreCase)
            ? UserRole.Admin
            : UserRole.User;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            this.FlashError("That account no longer exists.");
            return RedirectToAction(nameof(Users));
        }

        if (user.Role == target)
        {
            return RedirectToAction(nameof(Users));
        }

        if (target == UserRole.Admin)
        {
            await users.PromoteAsync(id, ct);
            this.FlashSuccess($"{user.Email} is now an admin.");
        }
        else if (await users.TryDemoteAsync(id, ct))
        {
            this.FlashSuccess($"{user.Email} is now a regular user.");
        }
        else
        {
            // The atomic guard refused: this is the last active admin.
            this.FlashError("This is the only active admin - promote another before changing this one.");
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/identity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateIdentity(int id, string email, string? username, CancellationToken ct)
    {
        var error = await users.UpdateIdentityAsync(id, email, username, ct);
        if (error is not null)
        {
            this.FlashError(error);
        }
        else
        {
            this.FlashSuccess("Login details updated.");
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/quota")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetQuota(int id, string? quota, CancellationToken ct)
    {
        long? bytes;
        var raw = quota?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            bytes = null; // inherit the platform default
        }
        else if (long.TryParse(raw, out var mb) && mb >= 0)
        {
            mb = Math.Min(mb, 100L * 1024 * 1024); // cap at 100 TB expressed in MB
            bytes = mb == 0 ? 0 : mb * 1024 * 1024;
        }
        else
        {
            this.FlashError("Enter a whole number of MB, 0 for unlimited, or leave it blank to use the default.");
            return RedirectToAction(nameof(Users));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, bytes), ct);
        this.FlashSuccess(affected == 0 ? "That account no longer exists." : "Storage limit updated.");
        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, string password, CancellationToken ct)
    {
        var error = await users.SetPasswordAsync(id, password, ct);
        if (error is not null)
        {
            this.FlashError(error);
        }
        else
        {
            this.FlashSuccess("Password updated.");
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost("users/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(int id, CancellationToken ct)
    {
        if (id == User.GetUserId())
        {
            this.FlashError("You can't delete your own account here.");
            return RedirectToAction(nameof(Users));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            this.FlashInfo("That account was already deleted.");
            return RedirectToAction(nameof(Users));
        }

        if (await users.DeleteUserAsync(id, ct))
        {
            this.FlashSuccess($"Account {user.Email} and all its content were deleted.");
        }
        else
        {
            // The atomic guard refused: this is the last active admin.
            this.FlashError("This is the only active admin - it can't be deleted.");
        }

        return RedirectToAction(nameof(Users));
    }
}
