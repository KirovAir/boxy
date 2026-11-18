using Boxy.Data.Entities;

namespace Boxy.Web.Models;

public class AccountViewModel
{
    public required User User { get; init; }
    public required string BaseUrl { get; init; }

    /// <summary>A key generated on the previous request, shown this once.</summary>
    public string? NewApiKey { get; init; }
}
