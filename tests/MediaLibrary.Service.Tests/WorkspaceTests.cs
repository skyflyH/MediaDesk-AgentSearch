using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly TestDirectory temporary = new();

    [Fact]
    public void MigratesExistingWorkspaceAndResumesDraftWithoutChangingSource()
    {
        var dbPath = Path.Combine(temporary.FullName, "素材库.sqlite3");
        using (var v1 = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            v1.Open();
            using var command = v1.CreateCommand();
            command.CommandText = """
                CREATE TABLE workspace_state (id INTEGER PRIMARY KEY, current_asset_id TEXT, updated_at TEXT NOT NULL);
                INSERT INTO workspace_state VALUES (1, NULL, '2026-11-19T00:00:00Z');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var source = Path.Combine(temporary.FullName, "原片.jpg");
        var sameBytes = Path.Combine(temporary.FullName, "原片副本.jpg");
        var text = Path.Combine(temporary.FullName, "文档.txt");
        var missing = Path.Combine(temporary.FullName, "遗失.mov");
        byte[] contents = [1, 2, 3, 4];
        File.WriteAllBytes(source, contents);
        File.WriteAllBytes(sameBytes, contents);
        File.WriteAllText(text, "测试");
        string selected;
        using (var db = new LibraryDatabase(dbPath))
        {
            var flags = db.Import([source, source, text, missing, sameBytes]);
            Assert.Equal(["imported", "duplicate", "unsupported", "unavailable", "imported"],
                flags.Select(item => item.Status));
            Assert.Equal(flags[0].AssetId, flags[1].AssetId);
            Assert.NotEqual(flags[0].AssetId, flags[4].AssetId);
            selected = flags[4].AssetId!;
            Assert.Equal("原片副本", db.SetCurrent(selected)?.Data.Name);
            var saved = db.SaveDraft(selected, 0, Changes("""
                {"name":"青岛晚霞","finalized":true,"remark":{"intent":"set","value":"青岛海边"},
                 "authors":{"intent":"set","value":["林安","李宁"]}}
                """));
            Assert.Equal(1, saved.Revision);
            Assert.Equal(1, saved.ArchiveRevision);
            var stale = Assert.Throws<RequestError>(() => db.SaveDraft(selected, 0, Changes("""{"name":"旧请求"}""")));
            Assert.Equal("REVISION_CONFLICT", stale.Code);
            var progress = db.SaveDraft(selected, 1, Changes("""{"previewPositionMs":700}"""));
            Assert.Equal(1, progress.ArchiveRevision);
        }

        using var restarted = new LibraryDatabase(dbPath);
        var workspace = restarted.LoadWorkspace();
        Assert.Equal(2, workspace.QueueIds.Length);
        Assert.Equal(selected, workspace.CurrentAssetId);
        Assert.Equal("青岛晚霞", workspace.Draft?.Data.Name);
        Assert.True(workspace.Draft?.Data.Finalized);
        Assert.Equal("青岛海边", workspace.Draft?.Data.Remark.Value);
        Assert.NotNull(workspace.Draft);
        Assert.Equal(["林安", "李宁"], workspace.Draft.Data.Authors.Value);
        Assert.Equal(700, workspace.Draft?.Data.PreviewPositionMs);
        Assert.Equal(contents, File.ReadAllBytes(source));
        Assert.Equal(contents, File.ReadAllBytes(sameBytes));
    }

    [Fact]
    public void QueueShowsCurrentMetadataOnlyWhileOriginalIsAvailable()
    {
        var source = Path.Combine(temporary.FullName, "queue.jpg");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        using var db = new LibraryDatabase(Path.Combine(temporary.FullName, "library.sqlite3"));
        var id = db.Import([source])[0].AssetId!;
        var modified = new DateTime(2026, 11, 19, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var available = Assert.Single(db.LoadWorkspace().Items);
        Assert.True(available.Available);
        Assert.Equal(4L, available.SizeBytes);
        Assert.Equal(modified.ToString("O"), available.ModifiedAt);
        Assert.Equal(source, db.RevealPath(id));

        File.Move(source, Path.Combine(temporary.FullName, "moved.jpg"));
        var unavailable = Assert.Single(db.LoadWorkspace().Items);
        Assert.False(unavailable.Available);
        Assert.Null(unavailable.SizeBytes);
        Assert.Null(unavailable.ModifiedAt);
        Assert.Equal("SOURCE_UNAVAILABLE", Assert.Throws<RequestError>(() => db.RevealPath(id)).Code);
    }

    [Fact]
    public void BrowsesImportedFoldersFromTheirOriginalLocationAfterRestart()
    {
        var original = Directory.CreateDirectory(Path.Combine(temporary.FullName, "原始目录")).FullName;
        var album = Directory.CreateDirectory(Path.Combine(original, "相册")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(original, "空目录")).FullName;
        var source = Path.Combine(album, "照片.jpg");
        File.WriteAllBytes(source, [1, 2, 3]);
        var databasePath = Path.Combine(temporary.FullName, "library.sqlite3");
        using (var db = new LibraryDatabase(databasePath))
        {
            db.AddImportFolder(original);
            Assert.Equal("imported", db.Import([source])[0].Status);
        }
        using var reopened = new LibraryDatabase(databasePath);
        var workspace = reopened.LoadWorkspace();
        Assert.Equal([original], workspace.ImportFolders);
        Assert.Equal(source, Assert.Single(workspace.Items).SourcePath);
        Assert.Contains(reopened.BrowseImportFolder(original, original), entry => entry.Directory && entry.Path == album);
        Assert.Equal(source, Assert.Single(reopened.BrowseImportFolder(original, album)).Path);
        Assert.Empty(reopened.BrowseImportFolder(original, empty));
        Assert.Throws<RequestError>(() => reopened.BrowseImportFolder(original, temporary.FullName));
    }

    [Fact]
    public void RenamesOriginalAndSavesExplorerRemarkWithoutWritingDraftAuthors()
    {
        var source = Path.Combine(temporary.FullName, "source.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), source);
        var writer = new WindowsProperties();
        Assert.Equal("written", writer.WriteAuthors(source, ["Original author"]));
        using var db = new LibraryDatabase(Path.Combine(temporary.FullName, "library.sqlite3"));
        var id = db.Import([source])[0].AssetId!;
        var draft = db.SaveDraft(id, 0, Changes("""
            {"name":"Future copy","remark":{"intent":"set","value":"Comment"},
             "authors":{"intent":"set","value":["Author"]}}
            """));
        Assert.Equal("REVISION_CONFLICT",
            Assert.Throws<RequestError>(() => db.SaveSourceProperties(id, 0, writer)).Code);
        Assert.False(WindowsProperties.MatchesExplorerRemark(source, "Comment"));

        var saved = db.SaveSourceProperties(id, draft.Revision, writer);
        var renamed = Path.Combine(temporary.FullName, "Future copy.jpg");
        Assert.Equal(renamed, saved.RenamedPath);
        Assert.False(File.Exists(source));
        Assert.Equal("written", saved.Remark);
        Assert.Equal("unchanged", saved.Authors);
        Assert.True(WindowsProperties.MatchesExplorerRemark(renamed, "Comment"));
        Assert.Equal(["Original author"], WindowsProperties.Read(renamed).Authors);
        Assert.Equal("unchanged", saved.Draft.Data.Remark.Intent);
        Assert.Equal("set", saved.Draft.Data.Authors.Intent);
        Assert.Equal("Future copy", db.GetDraft(id).Data.Name);
        Assert.Equal(renamed, db.RevealPath(id));
        var asset = Assert.Single(db.LoadWorkspace().Items);
        Assert.True(asset.Available);
        Assert.Equal(renamed, asset.SourcePath);
        Assert.Equal("Future copy.jpg", asset.OriginalName);
        Assert.Equal([renamed], Directory.GetFiles(temporary.FullName, "*.jpg"));
        Assert.Equal("written", writer.WriteRemark(renamed, "Other comment"));
        Assert.Equal("written", db.SaveSourceProperties(id, saved.Draft.Revision, writer).Remark);
        Assert.True(WindowsProperties.MatchesExplorerRemark(renamed, "Comment"));
    }

    [Fact]
    public void RenamesWithoutARemarkAndProtectsExistingFiles()
    {
        var source = Path.Combine(temporary.FullName, "source.jpg");
        var destination = Path.Combine(temporary.FullName, "target.jpg");
        File.WriteAllBytes(source, [1, 2, 3]);
        using var db = new LibraryDatabase(Path.Combine(temporary.FullName, "library.sqlite3"));
        var id = db.Import([source])[0].AssetId!;
        var draft = db.SaveDraft(id, 0, Changes("""{"name":"target"}"""));
        var writer = new WindowsProperties();

        File.WriteAllBytes(destination, [4, 5, 6]);
        Assert.Equal("DESTINATION_EXISTS", Assert.Throws<RequestError>(() =>
            db.SaveSourceProperties(id, draft.Revision, writer)).Code);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
        Assert.Equal(source, db.RevealPath(id));
        File.Delete(destination);
        var saved = db.SaveSourceProperties(id, draft.Revision, writer);
        Assert.Equal(destination, saved.RenamedPath);
        Assert.Equal("unchanged", saved.Remark);
        Assert.Equal("target", saved.Draft.Data.Name);
        Assert.False(File.Exists(source));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.Equal(destination, db.RevealPath(id));
    }

    [Fact]
    public void RestoresOriginalLocationAfterInterruptionDuringRename()
    {
        var source = Path.Combine(temporary.FullName, "source.jpg");
        var destination = Path.Combine(temporary.FullName, "renamed.jpg");
        var databasePath = Path.Combine(temporary.FullName, "library.sqlite3");
        File.WriteAllBytes(source, [1, 2, 3]);
        string id;
        using (var db = new LibraryDatabase(databasePath))
            id = db.Import([source])[0].AssetId!;
        using (var sqlite = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = "UPDATE asset_locations SET pending_source_path = $path";
            command.Parameters.AddWithValue("$path", destination);
            command.ExecuteNonQuery();
        }
        File.Move(source, destination);
        using var reopened = new LibraryDatabase(databasePath);
        var asset = Assert.Single(reopened.LoadWorkspace().Items);
        Assert.Equal(destination, asset.SourcePath);
        Assert.Equal("renamed.jpg", asset.OriginalName);
        Assert.True(asset.Available);
        Assert.Equal(destination, reopened.RevealPath(id));
    }

    [Fact]
    public void RemovingMultipleQueueEntriesPreservesFilesAndDraftsForReimport()
    {
        var first = Path.Combine(temporary.FullName, "first.jpg");
        var second = Path.Combine(temporary.FullName, "second.jpg");
        File.WriteAllBytes(first, [1, 2]);
        File.WriteAllBytes(second, [3, 4]);
        using var db = new LibraryDatabase(Path.Combine(temporary.FullName, "library.sqlite3"));
        var imported = db.Import([first, second]);
        var firstId = imported[0].AssetId!;
        var secondId = imported[1].AssetId!;
        db.SetCurrent(firstId);
        db.SaveDraft(firstId, 0, Changes("""{"name":"Saved draft"}"""));

        Assert.Equal(2, db.RemoveFromQueue([firstId, secondId, firstId]));
        Assert.Equal(0, db.RemoveFromQueue([firstId, secondId]));
        Assert.Empty(db.LoadWorkspace().Items);
        Assert.Null(db.LoadWorkspace().CurrentAssetId);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(second));

        var restored = Assert.Single(db.Import([first]));
        Assert.Equal("imported", restored.Status);
        Assert.Equal(firstId, restored.AssetId);
        Assert.Equal("Saved draft", db.GetDraft(firstId).Data.Name);
        Assert.Equal(firstId, Assert.Single(db.LoadWorkspace().QueueIds));
    }

    [Fact]
    public void RemovingQueueFoldersHidesTheBranchAcrossRestartsAndAllowsReimport()
    {
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "素材")).FullName;
        var branch = Directory.CreateDirectory(Path.Combine(root, "相册")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(branch, "子目录")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(root, "相册备份")).FullName;
        var source = Path.Combine(nested, "source.jpg");
        var other = Path.Combine(sibling, "other.jpg");
        File.WriteAllBytes(source, [1, 2]);
        File.WriteAllBytes(other, [3, 4]);
        var databasePath = Path.Combine(temporary.FullName, "library.sqlite3");
        string sourceId;
        using (var db = new LibraryDatabase(databasePath))
        {
            db.AddImportFolder(root);
            db.AddImportFolder(nested);
            sourceId = db.Import([source, other])[0].AssetId!;
            db.SetCurrent(sourceId);
            db.SaveDraft(sourceId, 0, Changes("""{"name":"Saved draft"}"""));

            Assert.Throws<RequestError>(() => db.RemoveImportFolder(root, temporary.FullName));
            Assert.Equal(1, db.RemoveImportFolder(root, branch));
            Assert.Null(db.LoadWorkspace().CurrentAssetId);
        }

        using var reopened = new LibraryDatabase(databasePath);
        Assert.Equal([root], reopened.LoadWorkspace().ImportFolders);
        Assert.Equal(other, Assert.Single(reopened.LoadWorkspace().Items).SourcePath);
        Assert.Equal(sibling, Assert.Single(reopened.BrowseImportFolder(root, root)).Path);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(source));

        reopened.AddImportFolder(nested);
        Assert.Equal(sourceId, Assert.Single(reopened.Import([source])).AssetId);
        Assert.Equal("Saved draft", reopened.GetDraft(sourceId).Data.Name);
        Assert.Contains(reopened.BrowseImportFolder(root, root), entry => entry.Path == branch);
        Assert.Equal(2, reopened.RemoveImportFolder(root, root));
        Assert.Empty(reopened.LoadWorkspace().ImportFolders);
        Assert.Empty(reopened.LoadWorkspace().Items);
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(other));
    }

    [Fact]
    public void FinalizingInvalidWindowsNameDoesNotAlterTheLastSavedEdit()
    {
        var source = Path.Combine(temporary.FullName, "待整理.mp4");
        File.WriteAllBytes(source, [1]);
        using var db = new LibraryDatabase(Path.Combine(temporary.FullName, "library.sqlite3"));
        var id = db.Import([source])[0].AssetId!;
        _ = db.SaveDraft(id, 0, Changes("""{"name":"CON"}"""));
        var error = Assert.Throws<RequestError>(() => db.SaveDraft(id, 1, Changes("""{"finalized":true}""")));
        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.False(db.GetDraft(id).Data.Finalized);
        Assert.Equal(1, db.GetDraft(id).Revision);
        Assert.Equal("CON", db.GetDraft(id).Data.Name);
    }

    private static JsonElement Changes(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public void Dispose() => temporary.Dispose();
}
