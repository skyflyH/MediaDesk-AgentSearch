using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class SearchTests
{
    [Fact]
    public void FileKeywordSearchDoesNotTreatTheContainingDirectoryAsFileEvidence()
    {
        using var samples = new SearchSamples();
        var directory = samples.Database.AddDirectory(samples.Folder("海景"), true);
        var named = samples.Archive("海景照片.jpg", "", [], directory);
        var remarked = samples.Archive("V001.mp4", "海景中的帆船", [], directory);
        samples.Archive("山林.jpg", "山间树林", [], directory);
        var result = samples.Database.Filter(new SearchFilter(ResultKind: "files", KeywordTerms: ["海景"]), China);
        Assert.Equal(new[] { named.AssetId, remarked.AssetId }.Order(), result.Items.Select(item => item.AssetId).Order());
        Assert.All(result.Items, item => Assert.Contains(item.Matches, match => match.Field is "name" or "remark"));
    }

    [Fact]
    public void DirectorySearchRefreshesFilePropertyRemarksAfterAnExternalEdit()
    {
        using var files = new TestDirectory();
        using var database = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var folder = Directory.CreateDirectory(Path.Combine(files.FullName, "照片")).FullName;
        var path = Path.Combine(folder, "IMG_001.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), path);
        var properties = new WindowsProperties();
        Assert.Equal("written", properties.WriteExplorerRemark(path, "青岛海边日落"));
        var filter = new SearchFilter(DirectoryPaths: [folder], KeywordTerms: ["青岛"]);
        Assert.Equal("青岛海边日落", Assert.Single(database.Filter(filter, China).Items).RemarkSummary);
        Assert.Single(database.Filter(filter with { KeywordTerms = null, RemarkTerms = ["海边"] }, China).Items);

        Assert.Equal("written", properties.WriteExplorerRemark(path, "杭州西湖夜景"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        database.RefreshSearchFile(path, force: true);
        Assert.Empty(database.Filter(filter, China).Items);
        Assert.Equal("杭州西湖夜景", Assert.Single(database.Filter(filter with { KeywordTerms = ["西湖"] }, China).Items).RemarkSummary);
        Assert.Empty(database.LoadWorkspace().Items);
    }

    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    [Theory]
    [InlineData("2026-11-19", "2026-11-18T16:00:00.0000000Z", "2026-11-19T16:00:00.0000000Z")]
    [InlineData("2026/11/19", "2026-11-18T16:00:00.0000000Z", "2026-11-19T16:00:00.0000000Z")]
    [InlineData("20261119", "2026-11-18T16:00:00.0000000Z", "2026-11-19T16:00:00.0000000Z")]
    [InlineData("2026年11月19日", "2026-11-18T16:00:00.0000000Z", "2026-11-19T16:00:00.0000000Z")]
    [InlineData("2026年11月", "2026-10-31T16:00:00.0000000Z", "2026-11-30T16:00:00.0000000Z")]
    [InlineData("2026", "2025-12-31T16:00:00.0000000Z", "2026-12-31T16:00:00.0000000Z")]
    public void DateKeywordsUseLocalCalendarBounds(string term, string from, string until)
    {
        var range = LibraryDatabase.DateKeywordRange(term, China);
        Assert.NotNull(range);
        Assert.Equal(from, range.Value.From);
        Assert.Equal(until, range.Value.Until);
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("20261301")]
    [InlineData("照片2026")]
    public void InvalidDateKeywordsRemainLiteralText(string term) =>
        Assert.Null(LibraryDatabase.DateKeywordRange(term, China));

    [Fact]
    public void RelativeDateKeywordsResolveToLocalDaysAndMonths()
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, China).Date;
        var todayRange = LibraryDatabase.DateKeywordRange("今天", China)!.Value;
        var yesterdayRange = LibraryDatabase.DateKeywordRange("昨天", China)!.Value;
        var monthRange = LibraryDatabase.DateKeywordRange("本月", China)!.Value;
        Assert.Equal(today, TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(todayRange.From), China).Date);
        Assert.Equal(yesterdayRange.Until, todayRange.From);
        Assert.Equal(new DateTime(today.Year, today.Month, 1),
            TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(monthRange.From), China).Date);
    }

    [Fact]
    public void DateKeywordsMatchCreationOrCaptureWithoutMatchingLiteralNames()
    {
        using var samples = new SearchSamples();
        var created = samples.Archive("创建.jpg", "海边", [], created: new(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc));
        var captured = samples.Archive("拍摄.jpg", "海边", [], created: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        samples.Archive("2026-11-19.jpg", "山间", [], created: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        samples.Archive("次日.jpg", "海边", [], created: new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc));
        using (var connection = samples.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE assets SET captured_at = '2026-11-19T08:00:00.0000000Z' WHERE id = $id";
            command.Parameters.AddWithValue("$id", captured.AssetId);
            command.ExecuteNonQuery();
        }
        var filter = new SearchFilter(KeywordTerms: ["2026-11-19"]);
        Assert.Equal(new[] { created.AssetId, captured.AssetId }.Order(),
            samples.Database.Filter(filter, China).Items.Select(item => item.AssetId).Order());
        Assert.Equal(new[] { created.AssetId, captured.AssetId }.Order(),
            samples.Database.Filter(filter with { KeywordTerms = ["2026-11-19", "海边"] }, China)
                .Items.Select(item => item.AssetId).Order());
    }

    [Fact]
    public void ChineseDateKeywordDoesNotMatchUnrelatedDatesThroughNamesOrRemarks()
    {
        using var samples = new SearchSamples();
        var expected = samples.Archive("当天.jpg", "", [], created: new(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc));
        samples.Archive("2026年11月19日.jpg", "", [], created: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        samples.Archive("其他日期.jpg", "2026年11月19日", [], created: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = samples.Database.Filter(new SearchFilter(KeywordTerms: ["2026年11月19日"]), China);
        Assert.Equal(expected.AssetId, Assert.Single(result.Items).AssetId);
    }

    [Fact]
    public void DateKeywordsMatchDirectoryCreation()
    {
        using var samples = new SearchSamples();
        var root = samples.Folder("日期目录搜索");
        var child = Directory.CreateDirectory(Path.Combine(root, "照片")).FullName;
        Directory.SetCreationTimeUtc(child, new(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc));
        var other = Directory.CreateDirectory(Path.Combine(root, "2026年11月19日")).FullName;
        Directory.SetCreationTimeUtc(other, new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = samples.Database.Filter(new SearchFilter(DirectoryPaths: [root],
            ResultKind: "directories", KeywordTerms: ["2026年11月19日"]), China);
        Assert.Equal(child, Assert.Single(result.Directories!).Path, ignoreCase: true);
    }

    [Fact]
    public void ChineseTermsMatchLiteralRemarkWithTheWholeAuthorName()
    {
        using var samples = new SearchSamples();
        var match = samples.Archive("V001.mp4", "青岛海边日落，保留 \"OR 画面\" ＡＢＣ", ["林安"]);
        samples.Archive("V002.mov", "三亚海边日落", ["林安"]);
        samples.Archive("晚霞.jpg", "青岛海边日落", ["林安"]);
        samples.Archive("V003.mp4", "青岛海边日落", ["林安宁"]);
        samples.Import("未归档.jpg");

        foreach (var terms in new[] { new[] { "青岛" }, ["青岛海边"], ["青岛", "海边日落"], ["\"OR 画面\""], ["abc"] })
        {
            var result = Assert.Single(samples.Database.Filter(new SearchFilter(MediaKind: "video",
                Author: "林安", RemarkTerms: terms), China).Items);
            Assert.Equal(match.AssetId, result.AssetId);
            Assert.Equal("V001.mp4", result.FileName);
            Assert.Contains(result.Matches, item => item.Field == "remark" && item.Value.Contains("青岛海边日落"));
        }
        Assert.Empty(samples.Database.Filter(new SearchFilter(MediaKind: "video", Author: "安"), China).Items);
        Assert.Empty(samples.Database.Filter(new SearchFilter(Author: "林安", RemarkTerms: ["青岛", "三亚"]), China).Items);
        Assert.Empty(samples.Database.Filter(new SearchFilter(NameTerms: ["未归档"]), China).Items);
        var photo = Assert.Single(samples.Database.Filter(new SearchFilter(NameTerms: ["晚霞.JPG"]), China).Items);
        Assert.Equal("晚霞.jpg", photo.FileName);
    }

    [Fact]
    public void DirectorySettingMatchesActualHierarchyAndExtension()
    {
        using var samples = new SearchSamples();
        var root = samples.Database.AddDirectory(samples.Folder("项目"), root: true);
        var child = samples.Database.AddDirectory(samples.Folder(Path.Combine("项目", "相机")), root: false);
        var sibling = samples.Database.AddDirectory(samples.Folder("项目旧"), root: true);
        samples.Archive("层级直接.jpg", "", [], root);
        var nested = samples.Archive("子目录.MOV", "青岛海边", ["林安"], child);
        samples.Archive("无关.MOV", "青岛海边", ["林安"], sibling);

        var exact = samples.Database.Filter(new SearchFilter(DirectoryId: root.DirectoryId, IncludeSubdirectories: false), China);
        Assert.Equal("层级直接.jpg", Assert.Single(exact.Items).FileName);
        var recursive = samples.Database.Filter(new SearchFilter(DirectoryId: root.DirectoryId, Format: ".mov"), China);
        Assert.Equal(nested.AssetId, Assert.Single(recursive.Items).AssetId);
    }

    [Fact]
    public void SearchPathsAreCombinedAndKeywordsCanMatchNamesOrRemarks()
    {
        using var samples = new SearchSamples();
        var first = samples.Database.AddDirectory(samples.Folder("项目"), true);
        var second = samples.Database.AddDirectory(samples.Folder("活动"), true);
        var child = samples.Database.AddDirectory(samples.Folder(Path.Combine("项目", "照片")), false);
        var sibling = samples.Database.AddDirectory(samples.Folder("项目备份"), true);
        var byName = samples.Archive("晚霞.jpg", "", [], first);
        var byRemark = samples.Archive("海边.mp4", "晚霞", [], second);
        var nested = samples.Archive("岛屿.jpg", "晚霞", [], child);
        samples.Archive("备份.jpg", "晚霞", [], sibling);
        var filter = new SearchFilter(DirectoryPaths: [first.FullPath, second.FullPath], KeywordTerms: ["晚霞"]);

        var results = samples.Database.Filter(filter, China).Items;
        Assert.Equal(new[] { byName.AssetId, byRemark.AssetId, nested.AssetId }.Order(), results.Select(item => item.AssetId).Order());
        Assert.Equal(byName.SourcePath, results.Single(item => item.AssetId == byName.AssetId).SourcePath);
        Assert.Equal(2, samples.Database.Filter(filter with { IncludeSubdirectories = false }, China).Items.Length);
        Assert.Throws<RequestError>(() => samples.Database.Filter(filter with { DirectoryPaths = [] }, China));
    }

    [Fact]
    public void DateBoundsUseLocalTimeAndMissingCaptureDatesStayUnknown()
    {
        using var samples = new SearchSamples();
        var lower = samples.Archive("起点.jpg", "", [], created: new(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc));
        samples.Archive("上界.jpg", "", [], created: new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc));
        var recorded = samples.Archive("有拍摄日期.jpg", "", [], created: new(2026, 11, 18, 15, 0, 0, DateTimeKind.Utc));
        using (var connection = samples.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE assets SET captured_at = $capture WHERE id = $id";
            command.Parameters.AddWithValue("$capture", "2026-11-19T08:15:00.0000000Z");
            command.Parameters.AddWithValue("$id", recorded.AssetId);
            command.ExecuteNonQuery();
        }

        var range = new SearchFilter(DateFrom: "2026-11-19", DateToExclusive: "2026-11-20");
        Assert.Equal(lower.AssetId, Assert.Single(samples.Database.Filter(range, China).Items).AssetId);
        var captured = Assert.Single(samples.Database.Filter(range with { DateField = "capturedAt" }, China).Items);
        Assert.Equal(recorded.AssetId, captured.AssetId);
        Assert.Contains(captured.Matches, match => match.Field == "capturedAt");
        var afternoon = range with
        {
            DateField = "capturedAt", KeywordTerms = ["拍摄日期"],
            CaptureMinuteFrom = 16 * 60 + 15, CaptureMinuteToExclusive = 18 * 60
        };
        Assert.Equal(recorded.AssetId, Assert.Single(samples.Database.Filter(afternoon, China).Items).AssetId);
        Assert.Empty(samples.Database.Filter(afternoon with { CaptureMinuteFrom = 16 * 60 + 16 }, China).Items);
        Assert.Empty(samples.Database.Filter(afternoon with { CaptureMinuteFrom = 0, CaptureMinuteToExclusive = 16 * 60 + 15 }, China).Items);
        Assert.Empty(samples.Database.Filter(afternoon with { KeywordTerms = ["无关"] }, China).Items);
        Assert.Throws<RequestError>(() => samples.Database.Filter(afternoon with { CaptureMinuteToExclusive = 720 }, China));
        Assert.Throws<RequestError>(() => samples.Database.Filter(range with { DateToExclusive = range.DateFrom }, China));
        Assert.Throws<RequestError>(() => samples.Database.Filter(range with { DateFrom = "2026-02-30" }, China));
    }

    [Fact]
    public void ArchiveRecordsWindowsPhotoCaptureDate()
    {
        using var samples = new SearchSamples();
        var file = Path.Combine(samples.Folder("拍摄日期"), "带拍摄日期.jpg");
        using (var original = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg")))
        using (var output = File.Create(file))
        {
            var frame = BitmapFrame.Create(original, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var exif = new BitmapMetadata("jpg");
            exif.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:11:19 12:00:00");
            var jpeg = new JpegBitmapEncoder();
            jpeg.Frames.Add(BitmapFrame.Create(frame, null, exif, null));
            jpeg.Save(output);
        }

        var result = samples.ArchiveFile(file, "照片.jpg", "", []);
        var details = samples.Database.GetSearchDetails(result.AssetId);
        Assert.Equal("System.Photo.DateTaken", details.CaptureTimeSource);
        Assert.NotNull(details.Asset.CapturedAt);
        var captured = DateTime.Parse(details.Asset.CapturedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateOnly(2026, 11, 19), DateOnly.FromDateTime(captured.ToLocalTime()));
    }

    [Fact]
    public void PaginationIsStableAndDisconnectedFilesRemainSearchable()
    {
        using var samples = new SearchSamples();
        var date = new DateTime(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc);
        var archives = Enumerable.Range(1, 3).Select(index =>
            samples.Archive($"视频{index}.mp4", "海边日落", ["林安"], created: date)).ToArray();
        File.Delete(archives[1].FinalPath);
        var expected = archives.Select(entry => entry.AssetId).Order(StringComparer.Ordinal).ToArray();
        var filter = new SearchFilter(Author: "林安", Limit: 1);
        var first = samples.Database.Filter(filter, China);
        var second = samples.Database.Filter(filter with { Cursor = first.NextCursor }, China);
        var last = samples.Database.Filter(filter with { Cursor = second.NextCursor }, China);
        Assert.Equal(expected, new[] { first, second, last }.Select(page => Assert.Single(page.Items).AssetId));
        Assert.Null(last.NextCursor);
        Assert.Throws<RequestError>(() => samples.Database.Filter(filter with { Cursor = first.NextCursor, Author = "其他" }, China));
        Assert.Throws<RequestError>(() => samples.Database.Filter(filter with { Limit = 101 }, China));

        var details = samples.Database.GetSearchDetails(archives[1].AssetId);
        Assert.False(details.Asset.Available);
        Assert.Equal(archives[1].FinalPath, details.Path);
        Assert.Equal("海边日落", details.Remark);
        var failure = Assert.Throws<RequestError>(() => samples.Database.RevealPath(archives[1].AssetId));
        Assert.Equal("SOURCE_UNAVAILABLE", failure.Code);
        Assert.Contains(details.Path, failure.Message);
    }

    [Fact]
    public void MigrationBackfillsPreviouslyArchivedItemsUsingTheirNewName()
    {
        using var samples = new SearchSamples();
        var archived = samples.Archive("新名称.jpg", "迁移后青岛海边", ["林安"]);
        using (var connection = samples.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE search_roots;
                DROP TABLE search_files;
                DROP TABLE search_folders;
                ALTER TABLE asset_locations DROP COLUMN pending_source_path;
                DROP TABLE workspace_removed_folders;
                ALTER TABLE assets DROP COLUMN queued;
                DROP INDEX file_operations_source_identity;
                DROP TABLE workspace_import_folders;
                DROP TABLE photo_embeddings;
                DROP TABLE photo_jobs;
                DROP TABLE asset_search_fts;
                DROP TABLE search_index_state;
                DROP TABLE asset_search_authors;
                DROP TABLE asset_search;
                DROP INDEX assets_search_order;
                ALTER TABLE assets DROP COLUMN captured_at;
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }
        using var reopened = new LibraryDatabase(samples.DatabasePath);
        Assert.Equal(11, reopened.Health().SchemaVersion);
        var found = Assert.Single(reopened.Filter(new SearchFilter(NameTerms: ["新名称"], RemarkTerms: ["青岛海边"], Author: "林安"), China).Items);
        Assert.Equal(archived.AssetId, found.AssetId);
        Assert.Equal(archived.FinalPath, reopened.RevealPath(found.AssetId));
    }

    private sealed class SearchSamples : IDisposable
    {
        private readonly TestDirectory temporary = new();
        private readonly ArchiveDirectory root;
        public LibraryDatabase Database { get; }
        public string DatabasePath => Path.Combine(temporary.FullName, "library.sqlite3");

        public SearchSamples()
        {
            Database = new LibraryDatabase(DatabasePath);
            root = Database.AddDirectory(Folder("归档"), true);
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(temporary.FullName, name)).FullName;

        public string Import(string name) => Database.Import([MakeFile(name)])[0].AssetId!;

        public ArchiveStatus Archive(string name, string remark, string[] authors, ArchiveDirectory? directory = null,
            DateTime? created = null)
        {
            var path = MakeFile("原片" + Path.GetExtension(name));
            if (created is not null) File.SetCreationTimeUtc(path, created.Value);
            return ArchiveFile(path, name, remark, authors, directory);
        }

        private string MakeFile(string name)
        {
            var path = Path.Combine(Folder(Guid.NewGuid().ToString("N")), name);
            File.WriteAllBytes(path, [10, 20, 30, 40]);
            return path;
        }

        public ArchiveStatus ArchiveFile(string path, string name, string remark, string[] authors, ArchiveDirectory? directory = null)
        {
            directory ??= root;
            var assetId = Database.Import([path])[0].AssetId!;
            var draft = Database.SaveDraft(assetId, 0, JsonSerializer.SerializeToElement(new
            {
                name = Path.GetFileNameWithoutExtension(name), finalized = true, directoryId = directory.DirectoryId,
                remark = new { intent = remark.Length == 0 ? "unchanged" : "set", value = remark },
                authors = new { intent = authors.Length == 0 ? "unchanged" : "set", value = authors }
            }));
            var plan = Database.PreviewArchive(assetId, draft.ArchiveRevision, directory.DirectoryId);
            var operation = Guid.NewGuid().ToString();
            Database.AcceptArchive(operation, plan.PlanToken);
            new ArchiveExecutor(Database, new LibraryOnly()).Run(operation);
            var status = Database.GetArchive(operation);
            Assert.Equal("complete", status.Phase);
            return status;
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            Database.Dispose();
            temporary.Dispose();
        }
    }

    private sealed class LibraryOnly : IArchiveProperties
    {
        public string WriteRemark(string path, string value) => "library_only";
        public string WriteAuthors(string path, string[] value) => "library_only";
    }
}
