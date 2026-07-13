using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>The admin user-management list, with each account's content tally and the guards the
/// view needs (which row is you, and whether an admin is the last one standing).</summary>
public class SettingsUsersViewModel
{
    public required IReadOnlyList<UserRow> Users { get; init; }
    public required int CurrentUserId { get; init; }
    public required int ActiveAdminCount { get; init; }

    /// <summary>The platform default quota in bytes (0 = unlimited), shown as the fallback for accounts
    /// without an override.</summary>
    public required long DefaultQuotaBytes { get; init; }
}

public record UserRow(
    int Id,
    string Email,
    string? Username,
    string? Name,
    UserRole Role,
    bool IsActive,
    DateTime CreatedDate,
    int BoxCount,
    int ShareCount,
    long UsageBytes,
    long? QuotaBytes);
