using System.Text.Json;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class SuggestionTests
{
    [Fact]
    public void PendingRequestsBecomeRetryableAndCompletedSuggestionsSurviveRestart()
    {
        using var files = new TestDirectory();
        var databasePath = Path.Combine(files.FullName, "library.sqlite3");
        var path = Path.Combine(files.FullName, "测试照片.jpg");
        File.WriteAllBytes(path, [1, 4, 9]);
        string id;
        using (var db = new LibraryDatabase(databasePath))
        {
            id = db.Import([path])[0].AssetId!;
            db.SetCurrent(id);
            var draft = db.SaveDraft(id, 0, JsonSerializer.SerializeToElement(new { finalized = true }));
            var name = Pending();
            draft = db.SaveSuggestion(id, draft.Revision, "name", name);
            draft = db.SaveSuggestion(id, draft.Revision, "name", name with { Status = "ready", Names = ["海边晚霞"] });
            db.SaveSuggestion(id, draft.Revision, "directory", Pending());
        }

        using var reopened = new LibraryDatabase(databasePath);
        reopened.RecoverPendingSuggestions();
        var draftAfter = reopened.LoadWorkspace().Draft!;
        Assert.Equal(id, draftAfter.AssetId);
        Assert.Equal("测试照片", draftAfter.Data.Name);
        Assert.Equal(1, draftAfter.ArchiveRevision);
        Assert.Equal(new[] { "海边晚霞" }, draftAfter.Data.Suggestions?.Name?.Names ?? []);
        Assert.Equal("retry", draftAfter.Data.Suggestions?.Directory?.Status);
        Assert.Equal("MODEL_INTERRUPTED", draftAfter.Data.Suggestions?.Directory?.ErrorCode);
        Assert.Equal<byte>([1, 4, 9], File.ReadAllBytes(path));
    }

    [Fact]
    public void RemarkChangeInvalidatesDirectoryRequestWithoutChangingManualDirectory()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var path = Path.Combine(files.FullName, "青岛照片.jpg");
        File.WriteAllBytes(path, [1, 4, 9]);
        var id = db.Import([path])[0].AssetId!;
        var root = db.AddDirectory(Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName, true);
        var subdirectory = Directory.CreateDirectory(Path.Combine(root.FullPath, "青岛"));
        var draft = db.SaveDraft(id, 0, JsonSerializer.SerializeToElement(new { finalized = true, directoryId = root.DirectoryId }));
        var candidate = Assert.Single(db.RecommendationDirectories(id), item => item.FullPath == subdirectory.FullName);
        var request = Pending();
        var pending = db.SaveSuggestion(id, draft.Revision, "directory", request);
        var updated = db.SaveDraft(id, pending.Revision,
            JsonSerializer.SerializeToElement(new { remark = new { intent = "set", value = "后来修改的备注" } }));
        Assert.Equal(root.DirectoryId, updated.Data.DirectoryId);
        Assert.Null(updated.Data.Suggestions?.Directory);
        var stale = Assert.Throws<RequestError>(() => db.SaveSuggestion(id, updated.Revision, "directory",
            request with { Status = "ready", Candidates = [new(candidate.DirectoryId, "青岛")] }));
        Assert.Equal("REVISION_CONFLICT", stale.Code);
    }

    private static SuggestionState Pending() => new(Guid.NewGuid().ToString(), "pending", true, true);
}
