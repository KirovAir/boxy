namespace Boxy.Web.Services;

/// <summary>
/// The anonymous "account": a long-lived <c>boxy_uid</c> cookie that identifies a visitor across
/// visits without a login. Used to let people manage their own bucket uploads and to like shares.
/// </summary>
public static class UploaderCookie
{
    public const string Name = "boxy_uid";

    /// <summary>Return the visitor's token, minting and setting a persistent cookie if absent.</summary>
    public static string GetOrCreate(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(Name, out var existing) && !string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var token = SlugGenerator.New(24);
        ctx.Response.Cookies.Append(Name, token, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(10),
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/"
        });
        return token;
    }

    /// <summary>Return the visitor's token if they already have one, else null (no cookie is set).</summary>
    public static string? Current(HttpContext ctx)
    {
        return ctx.Request.Cookies.TryGetValue(Name, out var t) && !string.IsNullOrEmpty(t) ? t : null;
    }
}
