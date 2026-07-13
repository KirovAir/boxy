using Microsoft.AspNetCore.Mvc;

namespace Boxy.Web.Controllers;

public class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index()
    {
        return Redirect("/dashboard");
    }

    [HttpGet("/error")]
    public IActionResult Error()
    {
        return View();
    }

    // Friendly page for any error status (404 etc.) - reached via UseStatusCodePagesWithReExecute
    // so a missing upload box or video shows a message instead of a blank screen.
    [HttpGet("/status/{code:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code;
        return View("StatusPage", code);
    }
}
