using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boxy.Tests;

/// <summary>
/// Replacing a share's file, against a real store. The dangerous case is the replace that changes
/// nothing: identical bytes dedup onto the blob the item already has, and the old-files cleanup must
/// not treat that blob as "old" and delete the only copy.
/// </summary>
[TestClass]
public class ReplaceTests
{
    private const int OwnerId = 1;

    private string _root = null!;
    private SqliteConnection _conn = null!;
    private FileSystemBlobStore _storage = null!;
    private IngestionService _ingestion = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "boxy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.EnsureCreated();
            db.Users.Add(new User { Id = OwnerId, Email = "t@t", PasswordHash = "x", Role = UserRole.Admin, IsActive = true });
            db.SaveChanges();
        }

        var dbFactory = new TestDbFactory(options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StoragePath"] = _root })
            .Build();
        _storage = new FileSystemBlobStore(config, new TestEnv(_root), NullLogger<FileSystemBlobStore>.Instance);
        _ingestion = new IngestionService(dbFactory, _storage, new MediaProcessingQueue(),
            new QuotaService(dbFactory), NullLogger<IngestionService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _conn.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            /* best-effort */
        }
    }

    [TestMethod]
    public async Task Replace_WithIdenticalBytes_KeepsTheOriginalBlob()
    {
        var bytes = "the very same video"u8.ToArray();
        var item = await Ingest(bytes, "clip.mp4");

        var replaced = await Replace(item.Id, bytes, "clip.mp4");

        Assert.IsNotNull(replaced);
        Assert.AreEqual(item.ContentHash, replaced.ContentHash);
        Assert.IsTrue(await _storage.ExistsAsync(replaced.ContentHash + ".mp4"),
            "the deduped original must survive a replace with the same bytes");
    }

    [TestMethod]
    public async Task Replace_WithIdenticalBytes_NewExtension_DropsOnlyTheOldName()
    {
        var bytes = "the very same video"u8.ToArray();
        var item = await Ingest(bytes, "clip.mp4");

        var replaced = await Replace(item.Id, bytes, "clip.m4v");

        Assert.IsNotNull(replaced);
        Assert.IsTrue(await _storage.ExistsAsync(replaced.ContentHash + ".m4v"));
        Assert.IsFalse(await _storage.ExistsAsync(replaced.ContentHash + ".mp4"),
            "the old container name is unreferenced and should be gone");
    }

    [TestMethod]
    public async Task Replace_WithDifferentBytes_DropsTheOldOriginal()
    {
        var item = await Ingest("the first video"u8.ToArray(), "clip.mp4");
        var oldHash = item.ContentHash;

        var replaced = await Replace(item.Id, "a different video"u8.ToArray(), "clip.mp4");

        Assert.IsNotNull(replaced);
        Assert.AreNotEqual(oldHash, replaced.ContentHash);
        Assert.IsTrue(await _storage.ExistsAsync(replaced.ContentHash + ".mp4"));
        Assert.IsFalse(await _storage.ExistsAsync(oldHash + ".mp4"));
    }

    private async Task<MediaItem> Ingest(byte[] bytes, string name)
    {
        using var stream = new MemoryStream(bytes);
        return await _ingestion.IngestAsync(UploadSource.FromStream(stream), name, null, true, null, OwnerId);
    }

    private async Task<MediaItem?> Replace(int itemId, byte[] bytes, string name)
    {
        using var stream = new MemoryStream(bytes);
        return await _ingestion.ReplaceAsync(itemId, UploadSource.FromStream(stream), name);
    }

    private sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(options);
        }
    }

    private sealed class TestEnv(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Boxy.Tests";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
