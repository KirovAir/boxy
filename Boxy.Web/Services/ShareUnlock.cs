using System.Security.Cryptography;
using System.Text;
using Boxy.Data.Entities;
using Microsoft.AspNetCore.DataProtection;

namespace Boxy.Web.Services;

/// <summary>
/// Remembers, in a tamper-proof cookie, that a visitor entered a password-protected share's password.
/// The cookie is bound to the share id and a fingerprint of its password hash, so changing or clearing
/// the password invalidates existing unlocks. Signed with the app's Data Protection keys.
/// </summary>
public class ShareUnlock(IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("boxy.share-unlock.v1");

    /// <summary>True when the share needs no password, or the request already carries a valid unlock.</summary>
    public bool IsUnlocked(HttpRequest request, MediaItem item)
    {
        if (item.SharePasswordHash is null)
        {
            return true;
        }

        if (!request.Cookies.TryGetValue(CookieName(item.Id), out var cookie) || string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        try
        {
            return _protector.Unprotect(cookie) == Payload(item);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Record a successful unlock for this share on the response.</summary>
    public void Grant(HttpResponse response, MediaItem item)
    {
        response.Cookies.Append(CookieName(item.Id), _protector.Protect(Payload(item)), new CookieOptions
        {
            // Explicit root path so the cookie also reaches /f and /poster, not just /s (this is the
            // framework default too, but pin it so the media endpoints can never miss the unlock).
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = response.HttpContext.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(7),
            IsEssential = true
        });
    }

    private static string CookieName(int id)
    {
        return $"bxs{id}";
    }

    private static string Payload(MediaItem item)
    {
        return $"{item.Id}:{Fingerprint(item.SharePasswordHash)}";
    }

    // A short, stable fingerprint of the password hash - so a changed/cleared password voids old cookies
    // without putting the hash itself in the cookie.
    private static string Fingerprint(string? hash)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash ?? ""))).Substring(0, 12);
    }
}
