using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web;
using Boxy.Web.Extensions;
using Boxy.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace Boxy.Tests;

/// <summary>Filtering/sorting against a real SQLite database - the one place a DB earns its keep here,
/// because an EF translation regression (a query that silently switches to client evaluation, or won't
/// translate at all) is a runtime failure that pure tests never see.</summary>
[TestClass]
public class MediaQueryTests
{
    private static AppDbContext NewDb()
    {
        // A private in-memory DB kept alive by an open connection for the test's lifetime.
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        // Every media item now requires an owner (FK), so seed the one the fixtures reference.
        db.Users.Add(new User { Id = OwnerId, Email = "t@t", PasswordHash = "x", Role = UserRole.Admin, IsActive = true });
        db.SaveChanges();
        return db;
    }

    private const int OwnerId = 1;

    private static MediaItem Item(int id, MediaKind kind, string ext, long size = 1)
    {
        return new MediaItem
        {
            Id = id, Kind = kind, Extension = ext, SizeBytes = size, OwnerId = OwnerId,
            Slug = "s" + id, Title = "t", ContentHash = "h" + id, OriginalFileName = "f" + ext,
            ContentType = "application/octet-stream", Status = MediaStatus.Ready
        };
    }

    [TestMethod]
    public async Task WhereFacets_FiltersByKind_TranslatesToSql()
    {
        using var db = NewDb();
        db.MediaItems.AddRange(Item(1, MediaKind.Video, ".mp4"), Item(2, MediaKind.Image, ".jpg"), Item(3, MediaKind.Image, ".png"));
        await db.SaveChangesAsync();

        var images = await db.MediaItems.WhereFacets(new MediaFilter(MediaKind.Image, null)).ToListAsync();

        Assert.AreEqual(2, images.Count);
        Assert.IsTrue(images.All(m => m.Kind == MediaKind.Image));
    }

    [TestMethod]
    public async Task WhereFacets_EmptyFilter_ReturnsAll()
    {
        using var db = NewDb();
        db.MediaItems.AddRange(Item(1, MediaKind.Video, ".mp4"), Item(2, MediaKind.Pdf, ".pdf"));
        await db.SaveChangesAsync();

        Assert.AreEqual(2, await db.MediaItems.WhereFacets(MediaFilter.None).CountAsync());
    }

    [TestMethod]
    public async Task KindCounts_CountsEveryKind_IgnoringTheKindFacet()
    {
        using var db = NewDb();
        db.MediaItems.AddRange(
            Item(1, MediaKind.Video, ".mp4"), Item(2, MediaKind.Image, ".jpg"),
            Item(3, MediaKind.Image, ".png"), Item(4, MediaKind.Pdf, ".pdf"));
        await db.SaveChangesAsync();

        // Selecting Video must not zero the other chips: the kind facet is dropped for the count.
        var counts = await db.MediaItems.KindCountsAsync(new MediaFilter(MediaKind.Video, null), default);

        Assert.AreEqual(1, counts[MediaKind.Video]);
        Assert.AreEqual(2, counts[MediaKind.Image]);
        Assert.AreEqual(1, counts[MediaKind.Pdf]);
    }

    [TestMethod]
    public async Task SortBy_WithTies_TiebreaksById_SoPagingHasNoGapsOrDupes()
    {
        using var db = NewDb();
        for (var i = 1; i <= 30; i++)
        {
            db.MediaItems.Add(Item(i, MediaKind.File, ".bin", size: 1)); // identical size -> every row ties
        }

        await db.SaveChangesAsync();

        // Page through in 3 pages of 10 with the "size" sort; identical sizes would give an unstable order
        // (dropped/duplicated rows across pages) without the .ThenBy(Id) tiebreak.
        var seen = new List<int>();
        for (var page = 0; page < 3; page++)
        {
            var ids = await db.MediaItems.SortBy("size").Skip(page * 10).Take(10).Select(m => m.Id).ToListAsync();
            seen.AddRange(ids);
        }

        CollectionAssert.AreEquivalent(Enumerable.Range(1, 30).ToList(), seen);
    }

    [TestMethod]
    public async Task SortBy_Captured_NewestDatedFirst_UndatedLast()
    {
        using var db = NewDb();
        var older = Item(1, MediaKind.Image, ".jpg");
        older.CapturedAt = new DateTime(2020, 1, 1);
        var newer = Item(2, MediaKind.Image, ".jpg");
        newer.CapturedAt = new DateTime(2023, 1, 1);
        var undated = Item(3, MediaKind.File, ".zip"); // no capture metadata
        db.MediaItems.AddRange(older, newer, undated);
        await db.SaveChangesAsync();

        var order = await db.MediaItems.SortBy("captured").Select(m => m.Id).ToListAsync();

        CollectionAssert.AreEqual(new[] { 2, 1, 3 }, order); // 2023, then 2020, then the undated file
    }

    [TestMethod]
    public void MediaFilter_From_Parses_IsTotal_AndIsolatesPrefixes()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["vt"] = "image", ["ft"] = "video", ["fst"] = "failed", ["t"] = "bogus"
        });

        Assert.AreEqual(MediaKind.Image, MediaFilter.From(q, "v").Kind);   // vt=image
        var f = MediaFilter.From(q, "f");
        Assert.AreEqual(MediaKind.Video, f.Kind);                          // ft=video (not the "v" list's)
        Assert.AreEqual(MediaStatus.Failed, f.Status);                     // fst=failed
        Assert.IsNull(MediaFilter.From(q, "").Kind);                       // t=bogus -> dropped, no throw
        Assert.IsTrue(MediaFilter.From(new QueryCollection(), "v").IsEmpty);
    }
}
