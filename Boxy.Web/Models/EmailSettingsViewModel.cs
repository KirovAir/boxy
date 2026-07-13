namespace Boxy.Web.Models;

/// <summary>The admin email-settings form. The password value is never sent to the view - only whether
/// one is set. <see cref="FromDb"/> tells the admin whether these came from in-app config or the
/// environment fallback.</summary>
public class EmailSettingsViewModel
{
    public required bool Enabled { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Security { get; init; }
    public required string From { get; init; }
    public required string FromName { get; init; }
    public required string? User { get; init; }
    public required bool PasswordSet { get; init; }
    public required bool FromDb { get; init; }
    public required string? AdminEmail { get; init; }
}
