using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace MediaLibrary.Service.Tests;

public sealed class SearchSyncTests(ITestOutputHelper output)
{
    private static JsonElement Request(string root) => JsonSerializer.SerializeToElement(new { directoryPaths = new[] { root } });
    private static SearchFilter Files(string root) => new(DirectoryPaths: [root], ResultKind: "files");
    private static string Photo(string folder, string name = "coast.jpg")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), path);
        return path;
    }

    [Fact]
    public void ReopeningUsesPersistentMetadataWithoutScanningOrWriting()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        var photo = Photo(folder);
        var path = Path.Combine(temp.FullName, "library.sqlite3");
        using (var db = new LibraryDatabase(path)) db.IndexDirectories(Request(folder));
        using var reopened = new LibraryDatabase(path);
        // A new file cannot appear until synchronization: this proves a search did not enumerate the directory.
        Photo(folder, "later.jpg");
        var first = Assert.Single(reopened.Filter(Files(folder), TimeZoneInfo.Utc).Items);
        Assert.Equal("coast.jpg", first.FileName);
        Assert.True(first.Properties!["width"] > 0);
        Assert.Equal(0, reopened.SearchDirectoriesScanned);
        Assert.Equal(0, reopened.SearchFilesRefreshed);
        // Explicit validation also reuses unchanged snapshots across process restarts.
        reopened.IndexDirectories(Request(folder));
        Assert.Equal(1, reopened.SearchFilesRefreshed);
        Assert.Equal(2, reopened.Filter(Files(folder), TimeZoneInfo.Utc).Items.Length);
    }

    [Fact]
    public void DirectoryRenamePreservesIdsAndDoesNotReextractMetadata()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        var child = Path.Combine(folder, "old");
        Photo(child);
        using var db = new LibraryDatabase(Path.Combine(temp.FullName, "library.sqlite3"));
        db.IndexDirectories(Request(folder));
        var before = Assert.Single(db.Filter(Files(folder), TimeZoneInfo.Utc).Items);
        var destination = Path.Combine(folder, "new");
        Directory.Move(child, destination);
        db.RelocateSearchPath(child, destination, directory: true);
        var after = Assert.Single(db.Filter(Files(folder), TimeZoneInfo.Utc).Items);
        Assert.Equal(before.AssetId, after.AssetId);
        Assert.Equal(Path.Combine(destination, "coast.jpg"), after.SourcePath, ignoreCase: true);
        Assert.Equal(1, db.SearchFilesRefreshed);
        Assert.Single(db.Filter(new SearchFilter(DirectoryPaths: [folder], ResultKind: "directories", NameTerms: ["new"]), TimeZoneInfo.Utc).Directories!);
    }

    [Fact]
    public async Task WatcherUpdatesCreateMetadataRenameDeleteAndSidecarWithoutRootRescans()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        var photo = Photo(folder);
        var path = Path.Combine(temp.FullName, "library.sqlite3");
        using var db = new LibraryDatabase(path);
        await using var sync = new SearchSyncWorker(path, TextWriter.Null, enableJournal: false);
        await sync.EnsureIndexedAsync(Request(folder), null);
        Assert.Single(db.Filter(Files(folder), TimeZoneInfo.Utc).Items);
        var scans = sync.DirectoriesScanned;
        var second = Photo(folder, "second.jpg");
        await Eventually(() => db.Filter(Files(folder), TimeZoneInfo.Utc).Items.Length == 2);
        Assert.Equal("written", new WindowsProperties().WriteExplorerRemark(photo, "updated coastline"));
        await Eventually(() => db.Filter(Files(folder) with { RemarkTerms = ["updated"] }, TimeZoneInfo.Utc).Items.Length == 1);
        var beforeRename = Assert.Single(db.Filter(Files(folder) with { RemarkTerms = ["updated"] }, TimeZoneInfo.Utc).Items);
        var renamed = Path.Combine(folder, "renamed.jpg");
        File.Move(photo, renamed);
        await Eventually(() => db.Filter(Files(folder) with { NameTerms = ["renamed"] }, TimeZoneInfo.Utc).Items.Length == 1);
        Assert.Equal(beforeRename.AssetId, Assert.Single(db.Filter(Files(folder) with { NameTerms = ["renamed"] }, TimeZoneInfo.Utc).Items).AssetId);
        File.Delete(second);
        await Eventually(() => db.Filter(Files(folder), TimeZoneInfo.Utc).Items.Length == 1);
        var raw = Path.Combine(folder, "raw.arw");
        File.WriteAllBytes(raw, [1, 2, 3]);
        RawSidecar.Write(raw, "first remark");
        await Eventually(() => db.Filter(Files(folder) with { RemarkTerms = ["first"] }, TimeZoneInfo.Utc).Items.Length == 1);
        RawSidecar.Write(raw, "second remark");
        await Eventually(() => db.Filter(Files(folder) with { RemarkTerms = ["second"] }, TimeZoneInfo.Utc).Items.Length == 1);
        File.Delete(RawSidecar.PathFor(raw));
        await Eventually(() => db.Filter(Files(folder) with { RemarkTerms = ["second"] }, TimeZoneInfo.Utc).Items.Length == 0);
        Assert.Equal(scans, sync.DirectoriesScanned);
    }

    [Fact]
    public async Task UsnRestartReplaysOfflineChangesWithoutScanning()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        var original = Photo(folder);
        var nested = Path.Combine(folder, "nested");
        Photo(nested, "nested.jpg");
        var path = Path.Combine(temp.FullName, "library.sqlite3");
        using var db = new LibraryDatabase(path);
        using (var journal = WindowsSearchJournal.TryOpen(folder))
        {
            if (journal is null) { output.WriteLine("USN unavailable on test volume; watcher fallback is covered separately."); return; }
            output.WriteLine($"USN available: {journal.Query().Id}");
        }
        await using (var initial = new SearchSyncWorker(path, TextWriter.Null))
            await initial.EnsureIndexedAsync(Request(folder), null);
        var id = db.Filter(Files(folder), TimeZoneInfo.Utc).Items.Single(item => item.FileName == "coast.jpg").AssetId;
        var renamed = Path.Combine(folder, "offline-renamed.jpg");
        File.Move(original, renamed);
        Photo(folder, "offline-new.jpg");
        var moved = Path.Combine(folder, "moved");
        Directory.Move(nested, moved);
        using (var inspect = WindowsSearchJournal.TryOpen(folder))
        {
            var checkpoint = db.SearchRoots().Single();
            if (inspect is not null && checkpoint.NextUsn is { } usn && checkpoint.JournalId is { } journalId)
                foreach (var change in inspect.Read(usn, journalId).Changes.Where(change => change.Name.Contains(".jpg")))
                    output.WriteLine($"Journal {change.Name}: 0x{change.Reason:X8}");
        }
        await using var restarted = new SearchSyncWorker(path, TextWriter.Null);
        await restarted.EnsureIndexedAsync(Request(folder), null);
        var items = db.Filter(Files(folder), TimeZoneInfo.Utc).Items;
        Assert.Equal(3, items.Length);
        Assert.Equal(moved, items.Single(item => item.FileName == "nested.jpg").DirectoryPath, ignoreCase: true);
        Assert.Equal(id, items.Single(item => item.FileName == "offline-renamed.jpg").AssetId);
        Assert.Equal(0, restarted.DirectoriesScanned);
        Assert.Equal(1, restarted.FilesRefreshed);
        File.Delete(renamed);
        await Eventually(() => db.Filter(Files(folder), TimeZoneInfo.Utc).Items.Length == 2);
        Assert.Equal(0, restarted.DirectoriesScanned);
        var refreshed = restarted.FilesRefreshed;
        await restarted.EnsureIndexedAsync(Request(folder), null);
        Assert.Equal(refreshed, restarted.FilesRefreshed);
        Assert.Equal(0, restarted.DirectoriesScanned);
    }

    [Fact]
    public void IncompleteAndOfflineScansRetainExistingRecords()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        Photo(folder);
        using var db = new LibraryDatabase(Path.Combine(temp.FullName, "library.sqlite3"));
        db.IndexDirectories(Request(folder));
        Directory.Move(folder, folder + "-offline");
        Assert.Throws<RequestError>(() => db.IndexDirectories(Request(folder)));
        var retained = Assert.Single(db.Filter(Files(folder), TimeZoneInfo.Utc).Items);
        Assert.False(retained.Available);
        Assert.True(retained.Properties!["width"] > 0);
    }

    [Fact]
    public void JournalParserValidatesLengthsAndReadsUnicodeNames()
    {
        var name = Encoding.Unicode.GetBytes("照片.jpg");
        var bytes = new byte[8 + 60 + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 1234);
        var record = bytes.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(record, record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 12);
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 34);
        BinaryPrimitives.WriteUInt32LittleEndian(record[40..], 0x2000);
        BinaryPrimitives.WriteUInt16LittleEndian(record[56..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 60);
        name.CopyTo(record[60..]);
        var page = WindowsSearchJournal.Parse(bytes);
        Assert.Equal(1234, page.Next);
        Assert.Equal(new JournalChange(12, 34, 0x2000, false, "照片.jpg"), Assert.Single(page.Changes));
        bytes[8] = 255;
        Assert.Throws<IOException>(() => WindowsSearchJournal.Parse(bytes));
        Assert.Throws<IOException>(() => WindowsSearchJournal.Parse(new byte[7]));
    }

    [Fact]
    public async Task RemovedRootsStopMonitoringAndOverlappingRootsShareOneScan()
    {
        using var temp = new TestDirectory();
        var folder = Path.Combine(temp.FullName, "photos");
        var child = Path.Combine(folder, "child");
        Photo(child);
        var path = Path.Combine(temp.FullName, "library.sqlite3");
        using var db = new LibraryDatabase(path);
        db.SetSearchRoots([folder, child]);
        await using var sync = new SearchSyncWorker(path, TextWriter.Null, enableJournal: false);
        await sync.EnsureIndexedAsync(Request(folder), null);
        Assert.Equal(1, sync.DirectoriesScanned);
        Assert.Single(db.Filter(Files(child), TimeZoneInfo.Utc).Items);
        var refreshes = sync.FilesRefreshed;
        db.SetSearchRoots([]);
        sync.Schedule();
        await Task.Delay(700);
        Photo(child, "unmonitored.jpg");
        await Task.Delay(700);
        Assert.Empty(db.SearchRoots());
        Assert.Equal(refreshes, sync.FilesRefreshed);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < timeout)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.True(condition(), "Background synchronization did not converge within 15 seconds.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingSourceRenameAndRemarkIsImmediatelySearchableWithoutWatcherOrRestart(bool rename)
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.FullName, "photos");
        var photo = Photo(root);
        using var db = new LibraryDatabase(Path.Combine(temp.FullName, "library.sqlite3"));
        db.IndexDirectories(Request(root));
        var id = db.Import([photo])[0].AssetId!;
        var draft = db.SaveDraft(id, db.GetDraft(id).Revision, JsonSerializer.SerializeToElement(new {
            name = rename ? "renamed25B" : "coast", remark = new { intent = "set", value = "25B coastline" }
        }));
        var saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        Assert.Equal("written", saved.Remark);
        using var reader = new LibraryDatabase(db.SearchDatabasePath);
        var found = Assert.Single(reader.Filter(Files(root) with { RemarkTerms = ["25B"], NameTerms = [rename ? "renamed25B" : "coast"] }, TimeZoneInfo.Utc).Items);
        Assert.Equal(id, found.AssetId);
        Assert.Equal("25B coastline", found.RemarkSummary);
        Assert.Equal(0, reader.SearchDirectoriesScanned);
    }

    [Fact]
    public void RelatedGroupsCombineDirectoryAndFileEvidenceWithoutRelaxingProperties()
    {
        using var temp = new TestDirectory();
        var root = Path.Combine(temp.FullName, "青岛旅行");
        Photo(Path.Combine(root, "海岸"), "落日.jpg");
        Photo(Path.Combine(root, "山间"), "日出.jpg");
        using var db = new LibraryDatabase(Path.Combine(temp.FullName, "library.sqlite3"));
        var filter = Files(root) with { KeywordGroups = [["青岛"], ["海边", "海岸"], ["日落", "落日"]], IncludeDirectoryNames = true };
        var found = Assert.Single(db.Filter(filter, TimeZoneInfo.Utc).Items);
        Assert.Equal("落日.jpg", found.FileName);
        Assert.Contains(found.Matches, match => match.Field == "directory" && match.Terms.Contains("海岸"));
        Assert.Contains(found.Matches, match => match.Field == "name" && match.Terms.Contains("落日"));
        Assert.Empty(db.Filter(filter with { IncludeDirectoryNames = false }, TimeZoneInfo.Utc).Items);
        Assert.Empty(db.Filter(filter with { PropertyRanges = [new("sizeBytes", 1000000000)] }, TimeZoneInfo.Utc).Items);
        Assert.Empty(db.Filter(filter with { KeywordGroups = [["media-library-test"]] }, TimeZoneInfo.Utc).Items);
    }
}
