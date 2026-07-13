namespace Boxy.Web.Models;

/// <summary>A rendered email: subject line plus HTML and plain-text bodies.</summary>
public record ComposedEmail(string Subject, string Html, string Text);

/// <summary>A call-to-action button, rendered by the shared _EmailButton partial.</summary>
public record EmailButton(string Url, string Label);

/// <summary>"Someone shared a file with you" - the WeTransfer-style share link email.</summary>
public record ShareLinkEmail(string Sender, string Title, string Link, string? Note);

public record EmailFile(string Name, long Bytes);

/// <summary>"N new files dropped in your box" - the drop-off notification email.</summary>
public record DropOffEmail(string BoxName, IReadOnlyList<EmailFile> Files, string? Link);

public record ExpiringItemLine(string Name, string Kind, string DeletesOn);

/// <summary>"N items will be deleted soon" - the expiry reminder email.</summary>
public record ExpiryReminderEmail(IReadOnlyList<ExpiringItemLine> Items, string? Link);
