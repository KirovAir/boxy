using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Boxy.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Boxy.Web.Services;

/// <summary>Per-account API keys for scripts and agents: <c>boxy_</c> plus 32 random bytes. Only the hash
/// is stored, so a key can be shown once and replaced, never looked up again.</summary>
public static class ApiKeys
{
    public const string Scheme = "ApiKey";
    public const string Prefix = "boxy_";

    public static string Generate()
    {
        return Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

/// <summary>
/// Signs a request in from <c>Authorization: Bearer boxy_…</c>. Only the API controller asks for this
/// scheme, so a key opens the API and nothing else, and the API never accepts the login cookie (which is
/// what keeps it out of reach of cross-site requests).
/// </summary>
public class ApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<AppDbContext> dbFactory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var key = header["Bearer ".Length..].Trim();
        if (!key.StartsWith(ApiKeys.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Not a Boxy API key.");
        }

        var hash = ApiKeys.Hash(key);
        await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.ApiKeyHash == hash && u.IsActive, Context.RequestAborted);
        return user is null
            ? AuthenticateResult.Fail("Unknown or revoked API key.")
            : AuthenticateResult.Success(new AuthenticationTicket(UserService.CreatePrincipal(user), Scheme.Name));
    }

    // A body of our own, so the status-code pages don't swap the 401 for an HTML page an agent can't read.
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new
        {
            error = "Send your API key as \"Authorization: Bearer boxy_...\". Create one on your account page."
        });
    }
}
