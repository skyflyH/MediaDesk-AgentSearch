using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class RawSidecarTests
{
    private static JsonElement Changes(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void SavesRenamesAndClearsSidecarWithoutChangingRawBytesOrOtherMetadata()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var raw = Path.Combine(files.FullName, "photo.dng");
        byte[] bytes = [1, 2, 3, 4];
        File.WriteAllBytes(raw, bytes);
        var id = db.Import([raw])[0].AssetId!;
        var draft = db.SaveDraft(id, 0, Changes(new { remark = new { intent = "set", value = "青岛<&>海边 😀" } }));
        var saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        Assert.Equal("written", saved.Remark);
        Assert.Equal("青岛<&>海边 😀", RawSidecar.Read(raw));
        Assert.Equal(bytes, File.ReadAllBytes(raw));
        var document = XDocument.Load(RawSidecar.PathFor(raw));
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        document.Descendants().First(e => e.Name.LocalName == "Description").Add(new XElement(dc + "subject", "Keep me"));
        document.Save(RawSidecar.PathFor(raw));
        draft = db.SaveDraft(id, saved.Draft.Revision, Changes(new { name = "renamed", remark = new { intent = "set", value = "上海夜景" } }));
        saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        var renamed = Path.Combine(files.FullName, "renamed.dng");
        Assert.False(File.Exists(raw));
        Assert.False(File.Exists(RawSidecar.PathFor(raw)));
        Assert.Equal("上海夜景", RawSidecar.Read(renamed));
        Assert.Equal("Keep me", XDocument.Load(RawSidecar.PathFor(renamed)).Descendants(dc + "subject").Single().Value);
        Assert.Equal(bytes, File.ReadAllBytes(renamed));
        draft = db.SaveDraft(id, saved.Draft.Revision, Changes(new { remark = new { intent = "clear", value = "" } }));
        Assert.Equal("written", db.SaveSourceProperties(id, draft.Revision, new WindowsProperties()).Remark);
        Assert.Equal("", RawSidecar.Read(renamed));
    }

    [Fact]
    public void SaveAsCreatesOnlyDestinationSidecarAndPreservesSourceSidecar()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var raw = Path.Combine(files.FullName, "source.nef");
        File.WriteAllBytes(raw, [5, 7, 9]);
        RawSidecar.Write(raw, "原备注");
        var originalXmp = File.ReadAllBytes(RawSidecar.PathFor(raw));
        var id = db.Import([raw])[0].AssetId!;
        var target = db.AddDirectory(Directory.CreateDirectory(Path.Combine(files.FullName, "target")).FullName, true);
        var draft = db.SaveDraft(id, 0, Changes(new { name = "副本", directoryId = target.DirectoryId, remark = new { intent = "set", value = "副本备注" } }));
        var plan = db.PreviewArchive(id, draft.ArchiveRevision, target.DirectoryId);
        var operation = Guid.NewGuid().ToString();
        db.AcceptArchive(operation, plan.PlanToken);
        new ArchiveExecutor(db, new WindowsProperties()).Run(operation);
        Assert.Equal("complete", db.GetArchive(operation).Phase);
        Assert.Equal("副本备注", RawSidecar.Read(plan.FinalPath));
        Assert.Equal(originalXmp, File.ReadAllBytes(RawSidecar.PathFor(raw)));
        Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(plan.FinalPath));
    }

    [Fact]
    public void DirectorySearchDetectsSidecarOnlyEditsAndDeletion()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var folder = Directory.CreateDirectory(Path.Combine(files.FullName, "photos")).FullName;
        var raw = Path.Combine(folder, "photo.arw");
        File.WriteAllBytes(raw, [1, 3, 5]);
        var stamp = File.GetLastWriteTimeUtc(raw);
        RawSidecar.Write(raw, "青岛海边");
        var filter = new SearchFilter(DirectoryPaths: [folder], KeywordTerms: ["青岛"], ResultKind: "files");
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("青岛海边", Assert.Single(db.Filter(filter, zone).Items).RemarkSummary);
        RawSidecar.Write(raw, "上海外滩夜景");
        Assert.Empty(db.Filter(filter, zone).Items);
        Assert.Single(db.Filter(filter with { KeywordTerms = ["外滩"] }, zone).Items);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(raw));
        File.Delete(RawSidecar.PathFor(raw));
        Assert.Empty(db.Filter(filter with { KeywordTerms = ["外滩"] }, zone).Items);
    }

    [Fact]
    public void PreservesMalformedSidecarAndRejectsRenameCollision()
    {
        using var files = new TestDirectory();
        var raw = Path.Combine(files.FullName, "photo.dng");
        File.WriteAllBytes(raw, [1]);
        File.WriteAllText(RawSidecar.PathFor(raw), "not XML");
        var writer = new WindowsProperties();
        Assert.Equal("write_failed", writer.WriteRemark(raw, "New comment"));
        Assert.Equal("not XML", File.ReadAllText(RawSidecar.PathFor(raw)));
        var target = Path.Combine(files.FullName, "target.dng");
        File.WriteAllText(RawSidecar.PathFor(target), "existing target");
        Assert.Throws<RequestError>(() => RawSidecar.CheckRename(raw, target));
    }

    [Fact]
    public void NameOnlySaveAndUnchangedSaveAsCreateMatchingSidecars()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var raw = Path.Combine(files.FullName, "source.cr3");
        File.WriteAllBytes(raw, [2, 4, 6]);
        var id = db.Import([raw])[0].AssetId!;
        var draft = db.SaveDraft(id, 0, Changes(new { name = "renamed" }));
        var saved = db.SaveSourceProperties(id, draft.Revision, new WindowsProperties());
        var renamed = Path.Combine(files.FullName, "renamed.cr3");
        Assert.Equal("written", saved.Remark);
        Assert.Equal("", RawSidecar.Read(renamed));
        var target = db.AddDirectory(Directory.CreateDirectory(Path.Combine(files.FullName, "copies")).FullName, true);
        draft = db.SaveDraft(id, saved.Draft.Revision, Changes(new { directoryId = target.DirectoryId }));
        var plan = db.PreviewArchive(id, draft.ArchiveRevision, target.DirectoryId);
        var operation = Guid.NewGuid().ToString();
        db.AcceptArchive(operation, plan.PlanToken);
        new ArchiveExecutor(db, new WindowsProperties()).Run(operation);
        Assert.Equal("complete", db.GetArchive(operation).Phase);
        Assert.Equal("", RawSidecar.Read(plan.FinalPath));
        Assert.Equal(new byte[] { 2, 4, 6 }, File.ReadAllBytes(renamed));
    }

    [Fact]
    public void SaveAsWithoutSourceSidecarCreatesOnlyDestinationXmpAndProtectsExistingTargetXmp()
    {
        using var files = new TestDirectory();
        using var db = new LibraryDatabase(Path.Combine(files.FullName, "library.sqlite3"));
        var raw = Path.Combine(files.FullName, "source.raf");
        File.WriteAllBytes(raw, [10, 20, 30]);
        var id = db.Import([raw])[0].AssetId!;
        var target = db.AddDirectory(Directory.CreateDirectory(Path.Combine(files.FullName, "copies")).FullName, true);
        var draft = db.SaveDraft(id, 0, Changes(new { name = "copy", directoryId = target.DirectoryId,
            remark = new { intent = "set", value = "目标目录备注" } }));
        var targetRaw = Path.Combine(target.FullPath, "copy.raf");
        File.WriteAllText(RawSidecar.PathFor(targetRaw), "unrelated existing XMP");
        Assert.Equal("DESTINATION_EXISTS", Assert.Throws<RequestError>(() =>
            db.PreviewArchive(id, draft.ArchiveRevision, target.DirectoryId)).Code);
        Assert.Equal("unrelated existing XMP", File.ReadAllText(RawSidecar.PathFor(targetRaw)));
        File.Delete(RawSidecar.PathFor(targetRaw));
        var plan = db.PreviewArchive(id, draft.ArchiveRevision, target.DirectoryId);
        var operation = Guid.NewGuid().ToString();
        db.AcceptArchive(operation, plan.PlanToken);
        new ArchiveExecutor(db, new WindowsProperties()).Run(operation);
        Assert.Equal("complete", db.GetArchive(operation).Phase);
        Assert.Equal("目标目录备注", RawSidecar.Read(targetRaw));
        Assert.False(File.Exists(RawSidecar.PathFor(raw)));
        Assert.Equal(new byte[] { 10, 20, 30 }, File.ReadAllBytes(raw));
    }
}
