using System.Windows.Media.Imaging;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class SearchPropertyTests
{
    [Fact]
    public void SizeFiltersUseRealBytesAndCombineWithOtherConditions()
    {
        using var directory = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(directory.FullName, "素材")).FullName;
        var small = Path.Combine(folder, "海边.jpg");
        var large = Path.Combine(folder, "山间.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), small);
        File.Copy(small, large);
        using (var stream = new FileStream(large, FileMode.Append)) stream.Write(new byte[1024]);
        var size = new FileInfo(small).Length;
        using var database = new LibraryDatabase(Path.Combine(directory.FullName, "library.sqlite3"));
        var filter = new SearchFilter(DirectoryPaths: [folder], ResultKind: "files",
            PropertyRanges: [new("sizeBytes", size, size)]);
        var item = Assert.Single(database.Filter(filter, TimeZoneInfo.Local).Items);
        Assert.Equal("海边.jpg", item.FileName);
        Assert.Equal(size, item.Properties!["sizeBytes"]);
        Assert.Contains(item.Matches, match => match.Field == "properties");
        Assert.Equal("山间.jpg", Assert.Single(database.Filter(filter with {
            PropertyRanges = [new("sizeBytes", size + 1)] }, TimeZoneInfo.Local).Items).FileName);
        Assert.Empty(database.Filter(filter with { KeywordTerms = ["山间"] }, TimeZoneInfo.Local).Items);
        Assert.Empty(database.Filter(filter with { PropertyRanges = [new("sizeBytes", size, size, MinExclusive: true)] }, TimeZoneInfo.Local).Items);
        Assert.Equal("海边.jpg", Assert.Single(database.Filter(filter with {
            PropertyRanges = [new("sizeBytes", null, size + 1024, MaxExclusive: true)] }, TimeZoneInfo.Local).Items).FileName);
        Assert.Throws<RequestError>(() => database.Filter(filter with {
            PropertyRanges = [new("sizeBytes", 20, 10)] }, TimeZoneInfo.Local));
        Assert.Throws<RequestError>(() => database.Filter(filter with {
            PropertyRanges = [new("unknown", 0)] }, TimeZoneInfo.Local));
    }

    [Fact]
    public void ExposureAndFocalLengthAreReadFromActualPhotoMetadataAndMissingValuesDoNotMatch()
    {
        using var directory = new TestDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(directory.FullName, "照片")).FullName;
        var photo = Path.Combine(folder, "人像.jpg");
        using (var original = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg")))
        using (var output = File.Create(photo))
        {
            var frame = BitmapFrame.Create(original, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var exif = new BitmapMetadata("jpg");
            exif.SetQuery("/app1/ifd/exif/{ushort=33434}", 1UL | (125UL << 32));
            exif.SetQuery("/app1/ifd/exif/{ushort=33437}", 28UL | (10UL << 32));
            exif.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)400);
            exif.SetQuery("/app1/ifd/exif/{ushort=37386}", 85UL | (1UL << 32));
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame, null, exif, null));
            encoder.Save(output);
        }
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coast.jpg"), Path.Combine(folder, "无参数.jpg"));
        var properties = LibraryDatabase.ReadSearchProperties(photo);
        Assert.Equal(.008, properties["exposureTimeSeconds"], 6);
        Assert.Equal(2.8, properties["fNumber"], 6);
        Assert.Equal(400, properties["iso"]);
        Assert.Equal(85, properties["focalLengthMm"]);
        using var database = new LibraryDatabase(Path.Combine(directory.FullName, "library.sqlite3"));
        var filter = new SearchFilter(DirectoryPaths: [folder], ResultKind: "files", PropertyRanges: [
            new("focalLengthMm", 80, 90), new("fNumber", null, 2.8), new("exposureTimeSeconds", .007, .009), new("iso", 400, 400)
        ]);
        Assert.Equal("人像.jpg", Assert.Single(database.Filter(filter, TimeZoneInfo.Local).Items).FileName);
        Assert.Empty(database.Filter(filter with { PropertyRanges = [new("focalLengthMm", 100)] }, TimeZoneInfo.Local).Items);
        Assert.Empty(database.Filter(filter with { PropertyRanges = [new("durationSeconds", 0)] }, TimeZoneInfo.Local).Items);
    }
}
