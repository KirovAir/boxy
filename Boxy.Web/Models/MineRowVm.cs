using Boxy.Data.Entities;

namespace Boxy.Web.Models;

/// <summary>One row in an anonymous uploader's "your uploads" list on a box page. Rendered by the
/// _MineRow partial both on first page load and (fetched) by upload.js as files finish, so the row markup
/// lives once instead of being rebuilt in JavaScript.</summary>
public sealed record MineRowVm(MediaItem Item, string BucketSlug);
