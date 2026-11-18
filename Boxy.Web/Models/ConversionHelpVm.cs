using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>Drives the _ConversionHelp partial: which select it explains, what that select starts on, and
/// what an empty value means there (a box's "use the default" option).</summary>
public class ConversionHelpVm
{
    public required string SelectId { get; init; }
    public required ConversionProfile Selected { get; init; }
    public ConversionProfile? EmptyMeans { get; init; }
}
