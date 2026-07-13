using System.Security.Claims;
using Boxy.Web.Services;

namespace Boxy.Web.Extensions;

public static class ClaimsExtensions
{
    /// <summary>The signed-in account id, or 0 when unauthenticated.</summary>
    public static int GetUserId(this ClaimsPrincipal user)
    {
        return int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
    {
        return user.IsInRole(UserService.AdminRole);
    }
}
