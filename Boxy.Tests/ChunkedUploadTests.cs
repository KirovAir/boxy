using System.Security.Cryptography;
using Boxy.Data;
using Boxy.Data.Entities;
using Boxy.Web.Models;
using Boxy.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boxy.Tests;

/// <summary>
/// The chunked upload engine against a real filesystem store and a real database, because the things that
/// break a multi-GB upload are all in the seams: parts arriving out of order, a stale part left over from a
/// build that cut the file differently, two tabs writing the same chunk at once, and the assembled file
/// being copied one more time than the disk can afford.
/// </summary>
[TestClass]
public class ChunkedUploadTests
{
    private const int OwnerId = 1;

    private string _root = null!;
    private SqliteConnection _conn = null!;
    private TestDbFactory _dbFactory = null!;
    private FileSystemBlobStore _storage = null!;
    private ChunkedUploadService _chunked = null!;

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

        _dbFactory = new TestDbFactory(options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StoragePath"] = _root })
            .Build();

        _storage = new FileSystemBlobStore(config, new TestEnv(_root), NullLogger<FileSystemBlobStore>.Instance);
        // No free-space reserve by default: the guard has its own test, and how full the temp volume happens
        // to be isn't something the rest of these should depend on.
        _chunked = NewChunked(new StorageSettings { MinFreeDiskMb = 0 });
    }

    private ChunkedUploadService NewChunked(StorageSettings settings)
    {
        var ingestion = new IngestionService(_dbFactory, _storage, new MediaProcessingQueue(),
            new QuotaService(_dbFactory), NullLogger<IngestionService>.Instance);
        return new ChunkedUploadService(_storage, ingestion, settings, NullLogger<ChunkedUploadService>.Instance);
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

    // ── Layout arithmetic: what pins every part to an exact length ──────────
    [TestMethod]
    public void UploadLayout_IsValid_OnlyWhenTheThreeNumbersAgree()
    {
        Assert.IsTrue(new UploadLayout(300, 100, 3).IsValid);
        Assert.IsTrue(new UploadLayout(301, 100, 4).IsValid); // short final chunk
        Assert.IsTrue(new UploadLayout(1, 100, 1).IsValid);

        Assert.IsFalse(new UploadLayout(300, 100, 4).IsValid); // one part too many
        Assert.IsFalse(new UploadLayout(300, 100, 2).IsValid); // one part too few
        Assert.IsFalse(new UploadLayout(0, 100, 1).IsValid);
        Assert.IsFalse(new UploadLayout(300, 0, 3).IsValid);
        Assert.IsFalse(new UploadLayout(300, 100, 0).IsValid);
        Assert.IsFalse(new UploadLayout(-1, 100, 1).IsValid);
    }

    [TestMethod]
    public void ExpectedChunkLength_IsFullExceptTheLast_AndNegativePastTheEnd()
    {
        Assert.AreEqual(100, ChunkedUploadService.ExpectedChunkLength(0, 250, 100));
        Assert.AreEqual(100, ChunkedUploadService.ExpectedChunkLength(1, 250, 100));
        Assert.AreEqual(50, ChunkedUploadService.ExpectedChunkLength(2, 250, 100)); // remainder
        Assert.AreEqual(-1, ChunkedUploadService.ExpectedChunkLength(3, 250, 100)); // past the end
        Assert.AreEqual(-1, ChunkedUploadService.ExpectedChunkLength(-1, 250, 100));
    }

    // ── Resume ──────────────────────────────────────────────────────────────
    [TestMethod]
    public async Task ExistingChunks_ReportsOnlyPartsThatFitTheirSlot()
    {
        var id = NewUploadId();
        await WriteChunk(id, 0, new byte[100]);
        await WriteChunk(id, 1, new byte[100]);
        await WriteChunk(id, 2, new byte[50]); // the short final chunk

        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, _chunked.ExistingChunks(id, 250, 100).ToArray());
    }

    [TestMethod]
    public async Task ExistingChunks_IgnoresPartsCutToADifferentChunkSize()
    {
        // The corruption this guards: parts staged by a build using 100-byte chunks, then picked up by a
        // client now using 50-byte chunks. Trusting them would splice part 0's 100 bytes into a 50-byte
        // slot and silently assemble a file that is not the one the user chose.
        var id = NewUploadId();
        await WriteChunk(id, 0, new byte[100]);
        await WriteChunk(id, 1, new byte[100]);

        Assert.AreEqual(0, _chunked.ExistingChunks(id, 250, 50).Count);
    }

    [TestMethod]
    public async Task ExistingChunks_IgnoresAShortPart()
    {
        var id = NewUploadId();
        await WriteChunk(id, 0, new byte[100]);
        await WriteChunk(id, 1, new byte[7]); // not the last chunk, so it must be full length

        CollectionAssert.AreEquivalent(new[] { 0 }, _chunked.ExistingChunks(id, 250, 100).ToArray());
    }

    [TestMethod]
    public void ExistingChunks_WithoutALayout_ReportsNothing()
    {
        // Fail safe: with no layout to check parts against, re-send everything rather than trust them.
        Assert.AreEqual(0, _chunked.ExistingChunks(NewUploadId(), 0, 0).Count);
    }

    // ── Assembly ────────────────────────────────────────────────────────────
    [TestMethod]
    public async Task CompleteAsync_AssemblesOutOfOrderChunks_IntoTheExactOriginalBytes()
    {
        var data = RandomBytes(300_000);
        const int chunk = 128 * 1024;
        var id = NewUploadId();

        // Arrive out of order, as parallel chunks do.
        foreach (var i in new[] { 2, 0, 1 })
        {
            await WriteChunk(id, i, Slice(data, i, chunk));
        }

        var item = await _chunked.CompleteAsync(id, new UploadLayout(data.Length, chunk, 3), "clip.mp4",
            null, true, null, OwnerId);

        Assert.AreEqual(data.Length, item.SizeBytes);
        Assert.AreEqual(Sha256Hex(data), item.ContentHash);
        CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(Path.Combine(_root, item.ContentHash + ".mp4")));
    }

    [TestMethod]
    public async Task CompleteAsync_ConsumesTheAssembledFile_LeavingNoScratchBehind()
    {
        // The disk cost of a multi-GB upload: the assembled file must be moved into place, not copied and
        // then left (or copied again by the store). Nothing may survive under the scratch dir.
        var data = RandomBytes(200_000);
        const int chunk = 64 * 1024;
        var id = NewUploadId();
        for (var i = 0; i < 4; i++)
        {
            await WriteChunk(id, i, Slice(data, i, chunk));
        }

        await _chunked.CompleteAsync(id, new UploadLayout(data.Length, chunk, 4), "clip.mp4", null, true, null, OwnerId);

        AssertScratchIsEmpty();
        // One stored blob, and no leftover tmp_* copy from a second pass over the bytes.
        var stored = Directory.GetFiles(_root).Select(Path.GetFileName).ToArray();
        CollectionAssert.AreEqual(new[] { Sha256Hex(data) + ".mp4" }, stored);
    }

    [TestMethod]
    public async Task CompleteAsync_RejectsAPartOfTheWrongLength_AndDiscardsTheUpload()
    {
        var data = RandomBytes(300_000);
        const int chunk = 128 * 1024;
        var id = NewUploadId();
        await WriteChunk(id, 0, Slice(data, 0, chunk));
        await WriteChunk(id, 1, Slice(data, 1, chunk)[..1000]); // truncated
        await WriteChunk(id, 2, Slice(data, 2, chunk));

        await Assert.ThrowsExactlyAsync<UploadIncompleteException>(() =>
            _chunked.CompleteAsync(id, new UploadLayout(data.Length, chunk, 3), "clip.mp4", null, true, null, OwnerId));

        AssertScratchIsEmpty();
        Assert.AreEqual(0, Directory.GetFiles(_root).Length, "a rejected upload must not leave bytes behind");
    }

    [TestMethod]
    public async Task CompleteAsync_RejectsAMissingChunk()
    {
        var data = RandomBytes(300_000);
        const int chunk = 128 * 1024;
        var id = NewUploadId();
        await WriteChunk(id, 0, Slice(data, 0, chunk));
        await WriteChunk(id, 2, Slice(data, 2, chunk)); // chunk 1 never arrived

        await Assert.ThrowsExactlyAsync<UploadIncompleteException>(() =>
            _chunked.CompleteAsync(id, new UploadLayout(data.Length, chunk, 3), "clip.mp4", null, true, null, OwnerId));

        AssertScratchIsEmpty();
    }

    [TestMethod]
    public async Task CompleteAsync_RejectsALayoutThatCannotAddUp()
    {
        var id = NewUploadId();
        await WriteChunk(id, 0, new byte[100]);

        await Assert.ThrowsExactlyAsync<UploadIncompleteException>(() =>
            _chunked.CompleteAsync(id, new UploadLayout(300, 100, 9), "clip.mp4", null, true, null, OwnerId));
    }

    // ── Concurrency ─────────────────────────────────────────────────────────
    [TestMethod]
    public async Task WriteChunkAsync_ConcurrentWritesOfTheSameIndex_DoNotClobberEachOther()
    {
        // Two tabs resume the same file and so share an upload id, or a retry races a request the server is
        // still reading. Every attempt writes a private temp, so the part that lands is always whole.
        var id = NewUploadId();
        var payload = RandomBytes(64 * 1024);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => WriteChunk(id, 3, payload)));

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(_storage.ScratchDir, id, "3")));
    }

    [TestMethod]
    public async Task WriteChunkAsync_RefusesToStage_WhenTheVolumeIsNearlyFull()
    {
        // Staged chunks have no other bound: a drop-off box is open to anyone with the link, and the per-file
        // cap neither applies to an admin's box nor spans more than one upload id. Only the free-space floor
        // stops a visitor filling the disk, which would take down far more than their own upload.
        var greedy = NewChunked(new StorageSettings { MinFreeDiskMb = int.MaxValue }); // more than any disk has

        await Assert.ThrowsExactlyAsync<StorageFullException>(() =>
            greedy.WriteChunkAsync(NewUploadId(), 0, new MemoryStream(new byte[10])));

        AssertScratchIsEmpty();
    }

    [TestMethod]
    public async Task Abort_RemovesEveryStagedPart()
    {
        var id = NewUploadId();
        await WriteChunk(id, 0, new byte[100]);

        _chunked.Abort(id);

        AssertScratchIsEmpty();
    }

    [TestMethod]
    public void PartDir_RejectsAMalformedUploadId()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _chunked.ExistingChunks("../../etc", 100, 100));
        Assert.ThrowsExactly<ArgumentException>(() => _chunked.ExistingChunks("short", 100, 100));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private Task WriteChunk(string uploadId, int index, byte[] bytes)
    {
        return _chunked.WriteChunkAsync(uploadId, index, new MemoryStream(bytes));
    }

    private void AssertScratchIsEmpty()
    {
        var scratch = _storage.ScratchDir;
        Assert.AreEqual(0, Directory.GetFileSystemEntries(scratch).Length,
            "scratch should hold nothing once an upload is finished or discarded");
    }

    private static string NewUploadId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static byte[] Slice(byte[] data, int index, int chunkSize)
    {
        var start = index * chunkSize;
        return data[start..Math.Min(start + chunkSize, data.Length)];
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(count).NextBytes(bytes); // seeded, so a failure reproduces
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
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
