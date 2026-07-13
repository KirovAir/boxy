namespace Boxy.Web.Models;

/// <summary>The password prompt shown in place of a protected share until the visitor unlocks it.</summary>
public class ShareLockedViewModel
{
    /// <summary>The URL to POST the password to - the same path the visitor is on.</summary>
    public required string PostPath { get; init; }

    /// <summary>True after a wrong-password attempt, to show the error.</summary>
    public bool HasError { get; init; }
}
