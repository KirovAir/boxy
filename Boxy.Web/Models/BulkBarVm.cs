namespace Boxy.Web.Models;

/// <summary>Drives the _BulkBar partial: where to return after a bulk delete, and whether to offer a
/// "download selected" action (drop-offs, not shares).</summary>
public class BulkBarVm
{
    public required string ReturnUrl { get; init; }
    public bool ShowDownload { get; init; }
    public string DeleteConfirm { get; init; } = "Delete the selected items? This can't be undone.";
}
