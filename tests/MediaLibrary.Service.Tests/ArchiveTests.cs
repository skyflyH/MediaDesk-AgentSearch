using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class ArchiveTests
{
    private static readonly byte[] SourceBytes = [2, 3, 5, 7, 11, 13];

    [Fact]
    public void UnchangedAttributesCanBeCopiedWithoutFinalizingTheName()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var source = MakeSource(files, "未经修改的原件");
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "保存目录")).FullName;
        var asset = database.Import([source])[0].AssetId!;
        var directory = database.AddDirectory(target, root: true);
        var draft = database.SaveDraft(asset, 0, JsonSerializer.Deserialize<JsonElement>(
            $$"""{"directoryId":"{{directory.DirectoryId}}"}"""));
        Assert.False(draft.Data.Finalized);
        var plan = database.PreviewArchive(asset, draft.ArchiveRevision, directory.DirectoryId);
        var id = Guid.NewGuid().ToString();
        database.AcceptArchive(id, plan.PlanToken);
        new ArchiveExecutor(database, new TestProperties()).Run(id);
        Assert.Equal("complete", database.GetArchive(id).Phase);
        Assert.Equal(Path.Combine(target, Path.GetFileName(source)), plan.FinalPath);
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureBeforeCreatingTheCopyReleasesTheDraftAndAllowsAnotherDestination(bool legacy)
    {
        using var files = new TestDirectory();
        var source = MakeSource(files, "原件");
        var denied = Directory.CreateDirectory(Path.Combine(files.FullName, "目标一")).FullName;
        var writable = Directory.CreateDirectory(Path.Combine(files.FullName, "目标二")).FullName;
        var operationId = Guid.NewGuid().ToString();
        string assetId;
        string failedPath;
        using (var database = Open(files))
        {
            var plan = Plan(database, source, denied);
            var accepted = database.AcceptArchive(operationId, plan.PlanToken);
            assetId = accepted.AssetId;
            failedPath = accepted.FinalPath;
            if (legacy)
            {
                var record = database.ReadArchive(operationId)!;
                database.SaveArchive(record with
                {
                    Phase = "needs_review", ErrorCode = "NEEDS_REVIEW",
                    TempPath = Path.Combine(denied, $".apm-{operationId}.tmp")
                });
            }
            else
            {
                new ArchiveExecutor(database, new TestProperties(), phase =>
                {
                    if (phase == "copying") throw new UnauthorizedAccessException();
                }).Run(operationId);
                Assert.Equal("DESTINATION_UNWRITABLE", database.GetArchive(operationId).ErrorCode);
            }
        }
        if (legacy)
        {
            using var sqlite = new SqliteConnection($"Data Source={Path.Combine(files.FullName, "library.sqlite3")};Pooling=False");
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = """
                DROP INDEX file_operations_asset;
                CREATE UNIQUE INDEX file_operations_asset ON file_operations(asset_id) WHERE phase <> 'complete';
                PRAGMA user_version = 9;
                """;
            command.ExecuteNonQuery();
        }

        using var reopened = Open(files);
        Assert.Equal("failed", reopened.GetArchive(operationId).Phase);
        Assert.Null(reopened.ActiveArchive(assetId));
        Assert.Contains(reopened.LoadWorkspace().RecentArchives, status => status.OperationId == operationId);
        Assert.False(File.Exists(failedPath));
        var directory = reopened.AddDirectory(writable, root: true);
        var draft = reopened.SaveDraft(assetId, reopened.GetDraft(assetId).Revision, JsonSerializer.SerializeToElement(new
        {
            name = "重试后的副本", directoryId = directory.DirectoryId
        }));
        var retry = reopened.PreviewArchive(assetId, draft.ArchiveRevision, directory.DirectoryId);
        var retryId = Guid.NewGuid().ToString();
        reopened.AcceptArchive(retryId, retry.PlanToken);
        new ArchiveExecutor(reopened, new TestProperties()).Run(retryId);
        Assert.Equal("complete", reopened.GetArchive(retryId).Phase);
        Assert.Equal(SourceBytes, File.ReadAllBytes(retry.FinalPath));
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void SavesEditedJpegAsACopyWithoutChangingTheOriginal()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var source = Path.Combine(files.FullName, "原件.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), source);
        var original = File.ReadAllBytes(source);
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "另存为")).FullName;
        var assetId = Assert.Single(database.Import([source])).AssetId!;
        var directory = database.AddDirectory(target, root: true);
        const string remark = "另存为备注";
        var draft = database.SaveDraft(assetId, 0, JsonSerializer.SerializeToElement(new
        {
            name = "新文件名", directoryId = directory.DirectoryId, remark = new { intent = "set", value = remark }
        }));

        Assert.False(draft.Data.Finalized);
        var plan = database.PreviewArchive(assetId, draft.ArchiveRevision, directory.DirectoryId);
        var operation = Guid.NewGuid().ToString();
        database.AcceptArchive(operation, plan.PlanToken);
        new ArchiveExecutor(database, new WindowsProperties(files.FullName)).Run(operation);

        var status = database.GetArchive(operation);
        Assert.Equal("complete", status.Phase);
        Assert.Equal(Path.Combine(target, "新文件名.jpg"), status.ActualPath);
        Assert.True(WindowsProperties.MatchesExplorerRemark(status.ActualPath!, remark));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void EditingAnUnfinalizedNameInvalidatesTheCopyPlan()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var source = MakeSource(files, "原件");
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "保存目录")).FullName;
        var asset = database.Import([source])[0].AssetId!;
        var directory = database.AddDirectory(target, root: true);
        var draft = database.SaveDraft(asset, 0, JsonSerializer.Deserialize<JsonElement>(
            $$"""{"directoryId":"{{directory.DirectoryId}}"}"""));
        var plan = database.PreviewArchive(asset, draft.ArchiveRevision, directory.DirectoryId);
        database.SaveDraft(asset, draft.Revision, JsonSerializer.Deserialize<JsonElement>("""{"name":"新名称"}"""));
        var error = Assert.Throws<RequestError>(() => database.AcceptArchive(Guid.NewGuid().ToString(), plan.PlanToken));
        Assert.Equal("REVISION_CONFLICT", error.Code);
        Assert.False(File.Exists(plan.FinalPath));
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void ConfirmationIsIdempotentAndNeverOverwritesADifferentFile()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName;
        var source = MakeSource(files, "原件");
        var plan = Plan(database, source, target);
        var operationId = Guid.NewGuid().ToString();
        Assert.False(File.Exists(plan.FinalPath));
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
        Assert.Equal("accepted", database.AcceptArchive(operationId, plan.PlanToken).Phase);
        Assert.Equal("accepted", database.AcceptArchive(operationId, plan.PlanToken).Phase);
        Assert.Throws<RequestError>(() => database.AcceptArchive(operationId, new string('a', 48)));

        var executor = new ArchiveExecutor(database, new TestProperties());
        executor.Run(operationId);
        var status = database.GetArchive(operationId);
        Assert.True(status.Phase == "complete", $"{status.Phase} {status.ErrorCode} {status.Message}");
        Assert.Equal(status, database.AcceptArchive(operationId, plan.PlanToken));
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
        Assert.Equal(plan.FinalPath, database.RevealPath(status.AssetId));
        Assert.Equal("duplicate", database.Import([source])[0].Status);

        var second = MakeSource(files, "另一个原件");
        var conflict = Assert.Throws<RequestError>(() => Plan(database, second, target));
        Assert.Equal("DESTINATION_EXISTS", conflict.Code);
        Assert.Equal(SourceBytes, File.ReadAllBytes(second));
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
    }

    [Fact]
    public void ReplacedDirectoryInvalidatesAnUnconfirmedPlan()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName;
        var source = MakeSource(files, "原件");
        var plan = Plan(database, source, target);
        Directory.Move(target, Path.Combine(files.FullName, "原目录"));
        Directory.CreateDirectory(target);
        var error = Assert.Throws<RequestError>(() => database.AcceptArchive(Guid.NewGuid().ToString(), plan.PlanToken));
        Assert.Equal("DESTINATION_UNAVAILABLE", error.Code);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(plan.FinalPath));
    }

    [Fact]
    public void SamePathArchiveLeavesTheOriginalUntouched()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var source = MakeSource(files, "晚霞");
        var conflict = Assert.Throws<RequestError>(() => Plan(database, source, files.FullName));
        Assert.Equal("DESTINATION_EXISTS", conflict.Code);
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("ready_to_publish")]
    [InlineData("renamed_before_log")]
    [InlineData("destination_published")]
    public void SameVolumeRecoveryUsesRecordedIdentity(string stopAfter)
    {
        using var files = new TestDirectory();
        var source = MakeSource(files, "原件");
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName;
        var operationId = Guid.NewGuid().ToString();
        ArchivePlan plan;
        using (var database = Open(files))
        {
            plan = Plan(database, source, target);
            database.AcceptArchive(operationId, plan.PlanToken);
            var executor = new ArchiveExecutor(database, new TestProperties(), phase =>
            { if (phase == stopAfter) throw new Interrupted(); });
            Assert.Throws<Interrupted>(() => executor.Run(operationId));
        }
        using var restarted = Open(files);
        new ArchiveExecutor(restarted, new TestProperties()).Run(operationId);
        Assert.Equal("complete", restarted.GetArchive(operationId).Phase);
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
        File.Delete(plan.FinalPath);
        var unavailable = Assert.Throws<RequestError>(() => restarted.RevealPath(restarted.GetArchive(operationId).AssetId));
        Assert.Equal("SOURCE_UNAVAILABLE", unavailable.Code);
        Assert.Contains(plan.FinalPath, unavailable.Message);
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task StartupWorkerRecoversPreviouslyAcceptedArchive()
    {
        using var files = new TestDirectory();
        var source = MakeSource(files, "启动恢复原件");
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName;
        var path = Path.Combine(files.FullName, "library.sqlite3");
        var operationId = Guid.NewGuid().ToString();
        ArchivePlan plan;
        using (var database = new LibraryDatabase(path))
        {
            plan = Plan(database, source, target);
            database.AcceptArchive(operationId, plan.PlanToken);
        }

        await using (var worker = new ArchiveWorker(path, Console.Error)) { }

        using var restarted = new LibraryDatabase(path);
        Assert.Equal("complete", restarted.GetArchive(operationId).Phase);
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
    }

    [CrossVolumeTheory]
    [InlineData("stage_flushed", false)]
    [InlineData("renamed_before_log", false)]
    [InlineData("destination_published", false)]
    [InlineData("properties_pending", false)]
    [InlineData("renamed_before_log", true)]
    public void CrossVolumeRecoveryUsesRealVolumeWhenConfigured(string stopAfter, bool replaceDestination)
    {
        var otherVolume = Environment.GetEnvironmentVariable("APM_TEST_SECOND_VOLUME");
        AssertCopyRecovery(stopAfter, replaceDestination, otherVolume);
    }

    [Theory]
    [InlineData("stage_flushed", false)]
    [InlineData("renamed_before_log", false)]
    [InlineData("destination_published", false)]
    [InlineData("properties_pending", false)]
    [InlineData("renamed_before_log", true)]
    public void CopyRecoveryPreservesFilesAtEveryDurableBoundary(string stopAfter, bool replaceDestination)
    {
        AssertCopyRecovery(stopAfter, replaceDestination, null);
    }

    private static void AssertCopyRecovery(string stopAfter, bool replaceDestination, string? otherVolume)
    {
        using var files = new TestDirectory();
        using var target = new TestDirectory(otherVolume);
        var source = MakeSource(files, "跨卷原件");
        var operationId = Guid.NewGuid().ToString();
        ArchivePlan plan;
        using (var database = Open(files))
        {
            plan = Plan(database, source, target.FullName);
            var accepted = database.AcceptArchive(operationId, plan.PlanToken);
            var recorded = database.ReadArchive(operationId)!;
            if (otherVolume is not null)
                Assert.NotEqual(recorded.Plan.SourceStamp.Identity.Volume, database.ReadDirectory(recorded.Plan.DirectoryId).Identity.Volume);
            var executor = new ArchiveExecutor(database, new TestProperties(), phase =>
            { if (phase == stopAfter) throw new Interrupted(); }, diagnostics: Console.Error);
            var interruption = Record.Exception(() => executor.Run(operationId));
            var status = database.GetArchive(operationId);
            Assert.True(interruption is Interrupted, $"{stopAfter}: {status.Phase} {status.ErrorCode} {status.Message} {interruption}");
            Assert.Equal("accepted", accepted.Phase);
        }

        if (replaceDestination)
        {
            File.Move(plan.FinalPath, Path.Combine(target.FullName, "被外部移动.jpg"));
            File.WriteAllBytes(plan.FinalPath, SourceBytes);
        }
        using var restarted = Open(files);
        new ArchiveExecutor(restarted, new TestProperties(), diagnostics: Console.Error).Run(operationId);
        var recovered = restarted.GetArchive(operationId);
        Assert.True(recovered.Phase == (replaceDestination ? "needs_review" : "complete"),
            $"{recovered.Phase} {recovered.ErrorCode} {recovered.Message}");
        Assert.Equal(SourceBytes, File.ReadAllBytes(source));
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
    }

    [Fact]
    public void PartialPropertyFailureLeavesFileSearchableAndOnlyRetriesTheFailedField()
    {
        using var files = new TestDirectory();
        using var database = Open(files);
        var target = Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName;
        var source = MakeSource(files, "属性原件");
        var plan = Plan(database, source, target, includeProperties: true);
        var id = Guid.NewGuid().ToString();
        database.AcceptArchive(id, plan.PlanToken);
        var properties = new TestProperties { FailRemark = true };
        var executor = new ArchiveExecutor(database, properties);
        executor.Run(id);
        var pending = database.GetArchive(id);
        Assert.Equal("properties_pending", pending.Phase);
        Assert.Equal("PROPERTY_WRITE_FAILED", pending.ErrorCode);
        Assert.Equal("write_failed", pending.Remark);
        Assert.Equal("written", pending.Authors);
        Assert.Equal(plan.FinalPath, database.RevealPath(pending.AssetId));
        Assert.Empty(database.LoadWorkspace().Items);

        properties.FailRemark = false;
        Assert.Null(database.PreparePropertyRetry(id).ErrorCode);
        executor.Run(id);
        Assert.Equal("complete", database.GetArchive(id).Phase);
        Assert.Equal(2, properties.RemarkCalls);
        Assert.Equal(1, properties.AuthorCalls);
        Assert.Equal(SourceBytes, File.ReadAllBytes(plan.FinalPath));
    }

    private static LibraryDatabase Open(TestDirectory files) => new(Path.Combine(files.FullName, "library.sqlite3"));

    private static string MakeSource(TestDirectory files, string name)
    {
        var path = Path.Combine(files.FullName, $"{name}.jpg");
        File.WriteAllBytes(path, SourceBytes);
        return path;
    }

    private static ArchivePlan Plan(LibraryDatabase db, string source, string target, bool includeProperties = false)
    {
        var asset = db.Import([source])[0].AssetId!;
        var directory = db.AddDirectory(target, root: true);
        var properties = includeProperties ? """
            ,"remark":{"intent":"set","value":"青岛海边"},"authors":{"intent":"set","value":["林安"]}
            """ : "";
        var changes = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"name":"晚霞","finalized":true,"directoryId":"{{directory.DirectoryId}}"{{properties}}}""");
        var draft = db.SaveDraft(asset, 0, changes);
        return db.PreviewArchive(asset, draft.ArchiveRevision, directory.DirectoryId);
    }

    private sealed class Interrupted : Exception;

    private sealed class CrossVolumeTheoryAttribute : TheoryAttribute
    {
        public CrossVolumeTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APM_TEST_SECOND_VOLUME")))
                Skip = "Set APM_TEST_SECOND_VOLUME to a writable directory on a second volume.";
        }
    }

    private sealed class TestProperties : IArchiveProperties
    {
        public int RemarkCalls;
        public int AuthorCalls;
        public bool FailRemark;
        public string WriteRemark(string path, string value) { RemarkCalls++; return FailRemark ? "write_failed" : "written"; }
        public string WriteAuthors(string path, string[] value) { AuthorCalls++; return "written"; }
    }
}
