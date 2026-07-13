using System.Security.Claims;
using Boxy.Data;
using Boxy.Data.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Boxy.Web.Services;

/// <summary>
/// Account authentication and registration. Passwords are hashed with the framework's
/// <see cref="PasswordHasher{T}"/> (PBKDF2); there is no ASP.NET Core Identity stack. The single
/// config admin from earlier is replaced by DB accounts, seeded on first run (see <see cref="SeedAsync"/>).
/// </summary>
public class UserService(IDbContextFactory<AppDbContext> dbFactory, IBlobStore storage, ILogger<UserService> logger)
{
    /// <summary>Role claim value that gates the admin-only platform area.</summary>
    public const string AdminRole = nameof(UserRole.Admin);

    public const int MinPasswordLength = 8;

    private static readonly PasswordHasher<User> Hasher = new();

    /// <summary>Returns the account for these credentials (matched by username or email), or null when
    /// they don't match an active user.</summary>
    public async Task<User?> ValidateAsync(string? usernameOrEmail, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var id = Normalize(usernameOrEmail);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == id || u.Username == id, ct);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        return Hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed
            ? null
            : user;
    }

    /// <summary>Creates an account. Returns an error message on invalid input or a taken email/username.</summary>
    public async Task<(User? User, string? Error)> RegisterAsync(
        string? email, string? username, string? password, string? name, UserRole role, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !LooksLikeEmail(email))
        {
            return (null, "Enter a valid email address.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return (null, $"Password must be at least {MinPasswordLength} characters.");
        }

        var normalizedUsername = NormalizeOrNull(username);
        if (normalizedUsername is not null && !IsValidUsername(normalizedUsername))
        {
            return (null, "Username must be 3-32 characters: letters, numbers, dot, dash or underscore.");
        }

        var normalized = Normalize(email);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Email == normalized, ct))
        {
            return (null, "That email is already registered.");
        }

        // Check the username against both columns: a login identifier must map to at most one account,
        // and the seeded admin's "email" can be a plain word (from the Admin__Username fallback).
        if (normalizedUsername is not null
            && await db.Users.AnyAsync(u => u.Username == normalizedUsername || u.Email == normalizedUsername, ct))
        {
            return (null, "That username is already taken.");
        }

        var user = new User
        {
            Email = normalized,
            Username = normalizedUsername,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Role = role
        };
        user.PasswordHash = Hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Registered account {Email} ({Role})", normalized, role);
        return (user, null);
    }

    /// <summary>Sets a new password for an account. Returns an error message on invalid input.</summary>
    public async Task<string?> SetPasswordAsync(int id, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return "That account no longer exists.";
        }

        user.PasswordHash = Hasher.HashPassword(user, password);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Change an account's login identifiers - its email and optional username. Enforces the
    /// same invariant as registration: every identifier maps to at most one account, so the new email
    /// or username may not match any other account's email or username. Returns an error message on
    /// invalid or already-taken input; null on success.</summary>
    public async Task<string?> UpdateIdentityAsync(int id, string? email, string? username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !LooksLikeEmail(email))
        {
            return "Enter a valid email address.";
        }

        var normalizedEmail = Normalize(email);
        var normalizedUsername = NormalizeOrNull(username);
        if (normalizedUsername is not null && !IsValidUsername(normalizedUsername))
        {
            return "Username must be 3-32 characters: letters, numbers, dot, dash or underscore.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return "That account no longer exists.";
        }

        // The new email and username must be free across both columns on every OTHER account.
        if (await db.Users.AnyAsync(u => u.Id != id && (u.Email == normalizedEmail || u.Username == normalizedEmail), ct))
        {
            return "That email is already in use.";
        }

        if (normalizedUsername is not null
            && await db.Users.AnyAsync(u => u.Id != id && (u.Username == normalizedUsername || u.Email == normalizedUsername), ct))
        {
            return "That username is already taken.";
        }

        user.Email = normalizedEmail;
        user.Username = normalizedUsername;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Updated login details for account {UserId}", id);
        return null;
    }

    /// <summary>Enable or disable an account. A disabled account can't sign in and its live sessions
    /// are dropped on the next request (see the cookie OnValidatePrincipal check).</summary>
    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, active), ct);
    }

    /// <summary>Promote an account to admin. The target's live session picks up the new role on its
    /// next request (see the cookie OnValidatePrincipal check).</summary>
    public async Task PromoteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin), ct);
    }

    /// <summary>Demote an account to a regular user, but only if it isn't the last active admin.
    /// The guard is part of the update statement so concurrent cross-demotions can't both succeed and
    /// leave the platform with no admin. Returns false when the guard blocked it.</summary>
    public async Task<bool> TryDemoteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Users
            .Where(u => u.Id == id
                        && (u.Role != UserRole.Admin || !u.IsActive
                                                     || db.Users.Any(a => a.Id != id && a.Role == UserRole.Admin && a.IsActive)))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.User), ct);
        return affected > 0;
    }

    /// <summary>Disable an account, but only if it isn't the last active admin (atomic guard).
    /// Returns false when the guard blocked it.</summary>
    public async Task<bool> TryDisableAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Users
            .Where(u => u.Id == id && u.IsActive
                                   && (u.Role != UserRole.Admin
                                       || db.Users.Any(a => a.Id != id && a.Role == UserRole.Admin && a.IsActive)))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), ct);
        return affected > 0;
    }

    /// <summary>Number of active admins - used to refuse removing/disabling the last one.</summary>
    public async Task<int> ActiveAdminCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, ct);
    }

    /// <summary>
    /// Deletes an account and everything it's responsible for: its shares, its boxes, and the drop-off
    /// files inside those boxes. Refuses (returns false) when the account is the last active admin, or
    /// is already gone. Physical files are removed only when no surviving item still references the same
    /// content (dedup-safe).
    /// </summary>
    public async Task<bool> DeleteUserAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Capture the file identities of everything the account owns before deletion - its shares and the
        // drop-offs collected in its boxes are all owned by it, and all are removed together by the FK
        // cascade, so their blobs can't be recovered afterwards.
        var ownedFiles = await db.MediaItems
            .Where(m => m.OwnerId == id)
            .Select(m => new { m.ContentHash, m.Extension, m.PosterFileName, m.WebFileName })
            .ToListAsync(ct);

        // Delete the account as one atomic statement, only if it isn't the last active admin - so
        // concurrent "delete/demote each other" requests can't both succeed and leave no admin. The FK
        // cascade removes its boxes and every item it owns (shares and the drop-offs in its boxes alike);
        // MediaLikes cascade in turn.
        var deleted = await db.Users
            .Where(u => u.Id == id
                        && (u.Role != UserRole.Admin || !u.IsActive
                                                     || db.Users.Any(a => a.Id != id && a.Role == UserRole.Admin && a.IsActive)))
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return false; // not found, or would remove the last active admin
        }

        // Now the rows are gone, drop any file no surviving item still points at (dedup-safe).
        foreach (var f in ownedFiles)
        {
            if (!await db.MediaItems.AnyAsync(m => m.ContentHash == f.ContentHash, ct))
            {
                await storage.DeleteAsync(f.ContentHash + f.Extension, ct);
            }

            if (f.PosterFileName is not null && !await db.MediaItems.AnyAsync(m => m.PosterFileName == f.PosterFileName, ct))
            {
                await storage.DeleteAsync(f.PosterFileName, ct);
            }

            if (f.WebFileName is not null && !await db.MediaItems.AnyAsync(m => m.WebFileName == f.WebFileName, ct))
            {
                await storage.DeleteAsync(f.WebFileName, ct);
            }
        }

        logger.LogInformation("Deleted account {UserId} and {MediaCount} media item(s)", id, ownedFiles.Count);
        return true;
    }

    public static ClaimsPrincipal CreatePrincipal(User user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name ?? user.Username ?? user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Guarantees the instance is never left without a way in. When no active admin exists - a fresh
    /// install, or an upgrade where the account lost the role - the account named by config
    /// (<c>Admin:Email</c>, else the legacy <c>Admin:Username</c>) is promoted back to an active admin,
    /// or created from <c>Admin:Password</c> if it's absent. A healthy instance that already has an
    /// admin is left untouched, so a deliberately removed admin is never resurrected. Finally adopts any
    /// pre-multi-user boxes/shares (null owner) under the first admin.
    /// </summary>
    public async Task SeedAsync(IConfiguration config, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var email = Normalize(config["Admin:Email"] ?? config["Admin:Username"] ?? "admin");
        var configUsername = NormalizeOrNull(config["Admin:Username"]);
        if (configUsername is not null && !IsValidUsername(configUsername))
        {
            configUsername = null; // a non-username value (e.g. an email) - the email field already covers it
        }

        // Only step in when the instance has no working admin. Touching a healthy one would resurrect
        // an admin that was deliberately deleted while others remain.
        if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive, ct))
        {
            var password = config["Admin:Password"];
            var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

            if (admin is not null)
            {
                // The configured account exists but isn't a usable admin - promote it. Rescues an
                // upgraded instance whose account ended up without the role, without touching its password.
                admin.Role = UserRole.Admin;
                admin.IsActive = true;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Restored admin rights for the configured account {Email}", email);
            }
            else if (string.IsNullOrEmpty(password))
            {
                logger.LogWarning("No admin account exists and Admin__Password is unset - set Admin__Password (with Admin__Email) to create one.");
            }
            else if (await db.Users.AnyAsync(u => u.Username == email, ct))
            {
                // The configured login id is already someone's username; creating an admin with it as
                // an email would make one identifier match two accounts. Make the operator pick a
                // unique email rather than break login.
                logger.LogError("Cannot seed the admin: the login id '{Email}' is already an account's username - set Admin__Email to a unique value.", email);
            }
            else
            {
                // Claim the configured username only if no account already uses it as a username OR an
                // email; otherwise the admin still logs in by email. Guards the unique-username index
                // and keeps every login identifier mapping to a single account.
                var freeUsername = configUsername is not null
                                   && !await db.Users.AnyAsync(u => u.Username == configUsername || u.Email == configUsername, ct)
                    ? configUsername
                    : null;
                admin = new User { Email = email, Username = freeUsername, Role = UserRole.Admin };
                admin.PasswordHash = Hasher.HashPassword(admin, password);
                db.Users.Add(admin);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Seeded admin account {Email}", email);
            }
        }

        // Give the configured admin its username if it hasn't got one yet and no account already uses
        // that name (as a username or an email), so the operator can sign in by username after
        // upgrading. Idempotent: a no-op once set.
        if (configUsername is not null)
        {
            var acct = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (acct is { Username: null } && !await db.Users.AnyAsync(u => u.Username == configUsername || u.Email == configUsername, ct))
            {
                acct.Username = configUsername;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Set username '{Username}' on the configured admin account", configUsername);
            }
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    // Letters, digits, dot, dash, underscore - and never '@', so a username can't collide with an email
    // as a login identifier.
    private static bool IsValidUsername(string username)
    {
        return username.Length is >= 3 and <= 32
               && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }
}
