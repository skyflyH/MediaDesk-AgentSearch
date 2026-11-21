using Xunit;
using Xunit.Abstractions;
using System.Text.Json;

namespace MediaLibrary.Service.Tests;

public sealed class MediaTests(ITestOutputHelper output)
{
    [Fact]
    public void CacheDirectoryChangesPersistAndClearOnlyTheSelectedDirectory()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var original = Path.Combine(files.FullName, "cache");
        var selected = Path.Combine(files.FullName, "新缓存");
        var settings = Path.Combine(files.FullName, "preview-cache-path.txt");
        using var previews = new MediaPreviews(db, original, settings);
        Directory.CreateDirectory(original);
        var name = $"{Guid.NewGuid():D}_{new string('A', 32)}.jpg";
        File.WriteAllText(Path.Combine(original, name), "old cache");
        Assert.Equal(selected, previews.SetCacheDirectory(selected));
        File.WriteAllText(Path.Combine(selected, name), "new cache");
        File.WriteAllText(Path.Combine(selected, "unrelated.txt"), "keep");
        Assert.Equal(1, previews.ClearCache());
        Assert.True(File.Exists(Path.Combine(original, name)));
        Assert.True(File.Exists(Path.Combine(selected, "unrelated.txt")));
        using var reopened = new MediaPreviews(db, original, settings);
        Assert.Equal(selected, reopened.CacheDirectory);
        Assert.Throws<RequestError>(() => previews.SetCacheDirectory("relative"));
        var blocked = Path.Combine(files.FullName, "file.txt");
        File.WriteAllText(blocked, "keep");
        Assert.Throws<RequestError>(() => previews.SetCacheDirectory(blocked));
        Assert.Equal(selected, previews.CacheDirectory);
        Assert.Equal(selected, File.ReadAllText(settings));
    }

    [Theory]
    [InlineData(".CR2")]
    [InlineData(".CR3")]
    [InlineData(".NEF")]
    [InlineData(".NRW")]
    [InlineData(".ARW")]
    [InlineData(".RAF")]
    [InlineData(".RW2")]
    [InlineData(".ORF")]
    [InlineData(".PEF")]
    [InlineData(".DNG")]
    public void InvalidRawImportsAsPhotoAndCanStoreSidecarWithoutChangingRaw(string extension)
    {
        using var files = new TestDirectory();
        var original = Path.Combine(files.FullName, "sample" + extension);
        byte[] rawBytes = [0, 1, 2];
        File.WriteAllBytes(original, rawBytes);
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        using var previews = new MediaPreviews(db, Path.Combine(files.FullName, "cache"));
        var imported = Assert.Single(db.Import([original]));
        Assert.Equal("imported", imported.Status);
        var id = Assert.IsType<string>(imported.AssetId);
        Assert.Equal("photo", db.Locate(id).Kind);
        Assert.Equal("PREVIEW_UNAVAILABLE", Assert.Throws<RequestError>(() => previews.Resolve(id, "image")).Code);

        var draft = db.SaveDraft(id, 0, JsonSerializer.SerializeToElement(new
        {
            remark = new { intent = "set", value = "Sample comment" },
            authors = new { intent = "set", value = new[] { "Sample author" } }
        }));
        var saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        Assert.Equal("written", saved.Remark);
        Assert.Equal("Sample comment", RawSidecar.Read(original));
        Assert.Equal("unchanged", saved.Authors);
        Assert.Equal("Sample comment", db.GetDraft(id).Data.Remark.Value);
        Assert.Equal(rawBytes, File.ReadAllBytes(original));
    }

    [Fact]
    public void DngWithoutEmbeddedPreviewProducesJpegAndThumbnail()
    {
        using var files = new TestDirectory();
        var original = Path.Combine(files.FullName, "相机样片.dng");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic.dng"));
        File.WriteAllBytes(original, bytes);
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        using var previews = new MediaPreviews(db, Path.Combine(files.FullName, "cache"));
        var id = Assert.IsType<string>(Assert.Single(db.Import([original])).AssetId);

        var preview = previews.Resolve(id, "image");
        var thumbnail = previews.Resolve(id, "thumbnail");
        Assert.NotEqual(preview.Path, thumbnail.Path);
        foreach (var image in new[] { preview, thumbnail })
        {
            Assert.Equal("image/jpeg", image.Mime);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(image.Path)[..2]);
        }
        Assert.Equal(bytes, File.ReadAllBytes(original));
    }

    [ExifToolFact]
    public void DngWritesSidecarRemarksAndEmbeddedAuthorsAtTheOriginalPath()
    {
        using var files = new TestDirectory();
        var original = Path.Combine(files.FullName, "相机样片.dng");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic.dng"), original);
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        using var previews = new MediaPreviews(db, Path.Combine(files.FullName, "cache"));
        var id = Assert.IsType<string>(Assert.Single(db.Import([original])).AssetId);
        Assert.Equal("image/jpeg", previews.Resolve(id, "image").Mime);
        using var guard = WindowsPaths.ProtectProperties(original);
        var writer = new WindowsProperties();
        Assert.Equal("library_only", writer.WriteWithFallback(original, false, ["Windows remark"],
            () => WindowsProperties.NativeWrite.Unsupported, explorerOnly: true));
        Assert.False(EmbeddedMetadata.Matches(original, false, ["Windows remark"]));
        foreach (var remark in new[] { "备注第一行\n第二行", "" })
        {
            string[] authors = remark.Length == 0 ? [] : ["摄影师甲", "Photographer B"];
            Assert.Equal("written", writer.WriteRemark(original, remark));
            Assert.Equal("written", writer.WriteAuthors(original, authors));
            Assert.True(File.Exists(original));
            Assert.Equal(remark, RawSidecar.Read(original));
            Assert.True(EmbeddedMetadata.Matches(original, true, authors));
        }
    }

    [Fact]
    public void JpegAndWicThumbnailResolveOnlyRegisteredSource()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        using var previews = new MediaPreviews(db, Path.Combine(files.FullName, "cache"));
        var original = CopySample(files);
        var id = db.Import([original])[0].AssetId!;
        Assert.Equal("image/jpeg", previews.Resolve(id, "image").Mime);
        var thumbnail = previews.Resolve(id, "thumbnail");
        Assert.True(thumbnail.Size > 0);
        Assert.NotEqual(original, thumbnail.Path);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(thumbnail.Path)[..2]);
        Assert.Throws<RequestError>(() => previews.Resolve(Guid.NewGuid().ToString(), "image"));
        Assert.Throws<RequestError>(() => previews.Resolve(id, "video"));
        File.Delete(original);
        Assert.Throws<RequestError>(() => previews.Resolve(id, "thumbnail"));
    }

    [Fact]
    public void WindowsPropertyHandlerReadsBackWrittenAndClearedJpegValues()
    {
        using var files = new TestDirectory();
        CheckPropertyRoundTrip(CopySample(files));
    }

    [Fact]
    public void SourcePropertiesReportSharingConflictAndCanBeRetried()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var original = CopySample(files);
        var id = Assert.IsType<string>(Assert.Single(db.Import([original])).AssetId);
        var draft = db.SaveDraft(id, 0, JsonSerializer.SerializeToElement(new
        {
            remark = new { intent = "set", value = "Example remark" },
            authors = new { intent = "set", value = new[] { "Example author" } }
        }));
        using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
            Assert.Equal("write_failed", failed.Remark);
            Assert.Equal("unchanged", failed.Authors);
            Assert.Equal("原件正被占用", failed.RemarkFailure);
            Assert.Null(failed.AuthorsFailure);
        }

        var saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        Assert.Equal("written", saved.Remark);
        Assert.Equal("unchanged", saved.Authors);
        Assert.Null(saved.RemarkFailure);
        Assert.Null(saved.AuthorsFailure);
    }

    [Theory]
    [InlineData("打开属性存储", unchecked((int)0x80070005), "0x80070005")]
    [InlineData("提交属性", unchecked((int)0x80030005), "0x80030005")]
    public void AccessDeniedPropertyFailureIncludesOperationAndErrorCode(string operation, int code, string hex)
    {
        Assert.Equal($"Windows 在{operation}时拒绝访问（{hex}）", WindowsProperties.DescribeWriteFailure(operation, code));
    }

    [ExifToolFact]
    public void DeniedWindowsPropertyStoreFallsBackToReadableJpegRemark()
    {
        using var files = new TestDirectory();
        var photo = CopySample(files);
        var properties = new WindowsProperties(files.FullName);
        Assert.Equal("written", properties.WriteRemark(photo, "旧备注"));
        using var guard = WindowsPaths.ProtectProperties(photo);
        const string remark = "青岛备注\n第二行 🌊";
        foreach (string[] values in new string[][] { [remark], [] })
        {
            Assert.Equal("written", properties.WriteWithFallback(photo, false, values,
                () => WindowsProperties.NativeWrite.OpenAccessDenied, explorerOnly: true));
            Assert.True(properties.LastWriteUsedEmbeddedMetadata);
            Assert.Null(properties.LastWriteFailure);
            Assert.True(EmbeddedMetadata.Matches(photo, false, values));
            Assert.True(WindowsProperties.MatchesExplorerRemark(photo, values.FirstOrDefault() ?? ""));
        }
        Assert.Empty(Directory.GetDirectories(files.FullName, "apm-metadata-*"));
    }

    [FixtureFormats]
    [InlineData("sample.png")]
    [InlineData("sample.mp4")]
    [InlineData("sample.mov")]
    [InlineData("sample.heic")]
    public void WindowsPropertyHandlerReportsRealFormatBehavior(string name)
    {
        using var files = new TestDirectory();
        var location = Path.Combine(files.FullName, name);
        File.Copy(Path.Combine(Environment.GetEnvironmentVariable("APM_TEST_FORMATS")!, name), location);
        CheckPropertyRoundTrip(location);
    }

    private void CheckPropertyRoundTrip(string location)
    {
        var format = Path.GetExtension(location);
        var property = new WindowsProperties();
        var comment = property.WriteRemark(location, "青岛海边测试");
        var author = property.WriteAuthors(location, ["林安", "李宁"]);
        output.WriteLine($"{format} set: remark={comment}; authors={author}");
        var values = WindowsProperties.Read(location);
        if (comment == "written") Assert.Equal("青岛海边测试", values.Remark);
        if (author == "written") Assert.Equal(["林安", "李宁"], values.Authors);

        var removedComment = property.WriteRemark(location, "");
        var removedAuthor = property.WriteAuthors(location, []);
        output.WriteLine($"{format} clear: remark={removedComment}; authors={removedAuthor}");
        values = WindowsProperties.Read(location);
        if (removedComment == "written") Assert.Empty(values.Remark);
        if (removedAuthor == "written") Assert.Empty(values.Authors);
        Assert.Contains(comment, new[] { "written", "library_only", "write_failed" });
        Assert.Contains(author, new[] { "written", "library_only", "write_failed" });
    }

    [Fact]
    public void JpegArchiveWritesBothFieldsAndReportsLockedFile()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var location = CopySample(files);
        var id = db.Import([location])[0].AssetId!;
        var directory = db.AddDirectory(Directory.CreateDirectory(Path.Combine(files.FullName, "归档")).FullName, root: true);
        var changes = JsonSerializer.SerializeToElement(new
        {
            name = "晚霞", finalized = true, directoryId = directory.DirectoryId,
            remark = new { intent = "set", value = "海边测试" },
            authors = new { intent = "set", value = new[] { "林安" } }
        });
        var draft = db.SaveDraft(id, 0, changes);
        var plan = db.PreviewArchive(id, draft.ArchiveRevision, directory.DirectoryId);
        var operationId = Guid.NewGuid().ToString();
        db.AcceptArchive(operationId, plan.PlanToken);
        new ArchiveExecutor(db, new WindowsProperties()).Run(operationId);
        var status = db.GetArchive(operationId);
        output.WriteLine($"JPEG archive: {status.Phase}; remark={status.Remark}; authors={status.Authors}");
        Assert.NotNull(status.ActualPath);
        Assert.Equal("complete", status.Phase);
        Assert.Equal("written", status.Remark);
        Assert.Equal("written", status.Authors);
        Assert.Equal(status.ActualPath, db.RevealPath(id));
        var properties = WindowsProperties.Read(status.ActualPath);
        if (status.Remark == "written") Assert.Equal("海边测试", properties.Remark);
        if (status.Authors == "written") Assert.Equal(["林安"], properties.Authors);

        using var locked = File.Open(status.ActualPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("write_failed", new WindowsProperties().WriteRemark(status.ActualPath, "被锁定"));
    }

    private static string CopySample(TestDirectory files)
    {
        var location = Path.Combine(files.FullName, "实拍样本.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), location);
        return location;
    }

    private sealed class FixtureFormatsAttribute : TheoryAttribute
    {
        public FixtureFormatsAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APM_TEST_FORMATS")))
                Skip = "Set APM_TEST_FORMATS to the directory with PNG, MP4, MOV and HEIC fixtures.";
        }
    }

    private sealed class ExifToolFactAttribute : FactAttribute
    {
        public ExifToolFactAttribute()
        {
            if (!EmbeddedMetadata.Available) Skip = "ExifTool is required to verify embedded metadata.";
        }
    }
}
