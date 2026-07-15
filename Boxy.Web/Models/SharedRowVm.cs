using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>One row in a shared-view box's "everyone's uploads" list: another visitor's finished file,
/// shown read-only. Same visual language as _MineRow, but the delete action is replaced by the uploader's
/// colour/name chip and a download link, and the row opens the shared lightbox on click.</summary>
public sealed record SharedRowVm(MediaItem Item);
