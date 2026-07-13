using Boxy.Web.Models;

namespace Boxy.Web.Services;

/// <summary>
/// Builds the app's transactional emails: renders the HTML from a Razor template and pairs it with a
/// plain-text body and a subject. One place that knows how each email is put together; callers just
/// supply a model and hand the result to <see cref="IEmailSender"/>.
/// </summary>
public class EmailComposer(EmailRenderer renderer)
{
    public async Task<ComposedEmail> ShareLinkAsync(ShareLinkEmail m)
    {
        var html = await renderer.RenderAsync("ShareLink", m);
        var text = $"{m.Sender} shared \"{m.Title}\" with you."
                   + (m.Note is null ? "" : $"\n\n{m.Note}")
                   + $"\n\nOpen it: {m.Link}";
        return new ComposedEmail($"{m.Sender} shared a file with you", html, text);
    }

    public async Task<ComposedEmail> DropOffAsync(DropOffEmail m)
    {
        var noun = m.Files.Count == 1 ? "file" : "files";
        var html = await renderer.RenderAsync("DropOff", m);
        var lines = string.Join("\n", m.Files.Select(f => $"  - {f.Name} ({Format.Bytes(f.Bytes)})"));
        var text = $"{m.Files.Count} new {noun} dropped in \"{m.BoxName}\":\n{lines}"
                   + $"\n\n{m.Files.Count} {noun}, {Format.Bytes(m.Files.Sum(f => f.Bytes))} total."
                   + (m.Link is null ? "" : $"\n\nOpen the box: {m.Link}");
        return new ComposedEmail($"{m.Files.Count} new {noun} in {m.BoxName}", html, text);
    }

    public async Task<ComposedEmail> ExpiryReminderAsync(ExpiryReminderEmail m)
    {
        var noun = m.Items.Count == 1 ? "item" : "items";
        var html = await renderer.RenderAsync("ExpiryReminder", m);
        var lines = string.Join("\n", m.Items.Select(i => $"  - {i.Name} ({i.Kind}) - deletes {i.DeletesOn}"));
        var text = $"{m.Items.Count} {noun} in Boxy will be deleted soon:\n{lines}\n\nRestore anything you want to keep."
                   + (m.Link is null ? "" : $"\n\nOpen your dashboard: {m.Link}");
        return new ComposedEmail($"{m.Items.Count} {noun} in Boxy will be deleted soon", html, text);
    }
}
