using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Boxy.Web;

/// <summary>Severity of a flash message - maps to a toast colour in _Flash.cshtml.</summary>
public enum FlashKind
{
    Success,
    Info,
    Warning,
    Danger
}

/// <summary>One toast, shown once on the page rendered after a redirect.</summary>
public record FlashMessage(FlashKind Kind, string Text);

/// <summary>
/// Post-action feedback for the Post-Redirect-Get pattern. A controller action does its work, calls
/// e.g. <c>this.FlashSuccess("Uploads reopened.")</c>, then redirects; the next page renders the
/// message once as a dismissing toast (see <c>Views/Shared/_Flash.cshtml</c>, wired into the layout).
///
/// Backed by TempData, so a message survives exactly one redirect and is then cleared. Multiple
/// messages queued in a single action are all shown. This is the one place to reach for whenever an
/// action changes state and the user should be told what happened.
/// </summary>
public static class Flash
{
    private const string Key = "_flash";

    public static void FlashSuccess(this Controller c, string text)
    {
        Add(c.TempData, FlashKind.Success, text);
    }

    public static void FlashInfo(this Controller c, string text)
    {
        Add(c.TempData, FlashKind.Info, text);
    }

    public static void FlashWarning(this Controller c, string text)
    {
        Add(c.TempData, FlashKind.Warning, text);
    }

    public static void FlashError(this Controller c, string text)
    {
        Add(c.TempData, FlashKind.Danger, text);
    }

    private static void Add(ITempDataDictionary temp, FlashKind kind, string text)
    {
        var list = Peek(temp);
        list.Add(new FlashMessage(kind, text));
        temp[Key] = JsonSerializer.Serialize(list);
    }

    /// <summary>Read and clear the queued messages. Called once, by the layout.</summary>
    public static IReadOnlyList<FlashMessage> ReadFlashes(this ITempDataDictionary temp)
    {
        var list = Peek(temp);
        temp.Remove(Key);
        return list;
    }

    private static List<FlashMessage> Peek(ITempDataDictionary temp)
    {
        return temp.Peek(Key) is string raw && !string.IsNullOrEmpty(raw)
            ? JsonSerializer.Deserialize<List<FlashMessage>>(raw) ?? []
            : [];
    }
}
