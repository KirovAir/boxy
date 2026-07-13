using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Data.Extensions;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

public class AccountController(UserService users, IDbContextFactory<AppDbContext> dbFactory) : Controller
{
    [HttpGet("/login")]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        // Already signed in? Skip the landing/login page and go straight to the dashboard.
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/dashboard");
        }

        ViewBag.ReturnUrl = returnUrl;
        ViewBag.RegistrationEnabled = await RegistrationEnabledAsync();
        return View();
    }

    [HttpPost("/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string usernameOrEmail, string password, bool rememberMe = false, string? returnUrl = null)
    {
        var user = await users.ValidateAsync(usernameOrEmail, password);
        if (user is null)
        {
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.RegistrationEnabled = await RegistrationEnabledAsync();
            ViewBag.Error = "Invalid login or password.";
            return View();
        }

        await SignInAsync(user, rememberMe);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/dashboard");
    }

    [HttpGet("/register")]
    public async Task<IActionResult> Register(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect("/dashboard");
        }

        if (!await RegistrationEnabledAsync())
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost("/register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string email, string? username, string password, string? name, string? returnUrl = null)
    {
        if (!await RegistrationEnabledAsync())
        {
            return NotFound();
        }

        var (user, error) = await users.RegisterAsync(email, username, password, name, UserRole.User);
        if (user is null)
        {
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.Error = error;
            ViewBag.Email = email;
            ViewBag.Username = username;
            ViewBag.Name = name;
            return View();
        }

        await SignInAsync(user, true);
        this.FlashSuccess("Welcome to Boxy. Create your first box or upload a file to share.");
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/dashboard");
    }

    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        this.FlashSuccess("Signed out.");
        return RedirectToAction("Login");
    }

    private async Task SignInAsync(User user, bool rememberMe)
    {
        // Remember me → a persistent cookie kept for a long time; otherwise a session cookie.
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddYears(1) : null
        };
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserService.CreatePrincipal(user),
            props);
    }

    private async Task<bool> RegistrationEnabledAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.GetSettingsAsync<PlatformSettings>()).RegistrationEnabled;
    }
}
