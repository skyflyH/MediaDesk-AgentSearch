using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace MediaLibrary.Service.Tests;

public sealed class PhotoIndexTests(ITestOutputHelper output)
{
    [Fact]
    public async Task IndexJobsRecoverRetryAndKeepTheRemarkConstraint()
    {
        using var files = new TestDirectory();
        var databasePath = Path.Combine(files.FullName, "library.sqlite3");
        using var database = new LibraryDatabase(databasePath);
        var valid = Archive(database, files.FullName, "有青岛备注", "青岛", [8]);
        var elsewhere = Archive(database, files.FullName, "其他地点", "三亚", [8]);
        var broken = Archive(database, files.FullName, "待重试", "青岛", [3]);
        _ = Archive(database, files.FullName, "当地视频", "青岛", [8], extension: ".mp4");
        database.SyncPhotoJobs("test-v1");
        Assert.NotNull(database.ClaimPhotoJob("test-v1"));

        var encoder = new TestPhotoEncoder();
        await using (var index = new PhotoIndex(database, databasePath, () => encoder, TextWriter.Null))
        {
            await Eventually(() => index.List(null).Completed == 2 && index.List(null).Failed == 1);
            var result = index.Search("海边晚霞", JsonSerializer.SerializeToElement(new { remarkTerms = new[] { "青岛" } }));
            var match = Assert.Single(result.Items);
            Assert.Equal(valid.AssetId, match.AssetId);
            Assert.Equal(1, match.SceneScore);
            Assert.Contains(match.Matches, item => item.Field == "remark" && item.Value == "青岛");
            Assert.Contains(match.Matches, item => item.Field == "scene" && item.Value == "海边晚霞");
            Assert.DoesNotContain(result.Items, item => item.AssetId == elsewhere.AssetId);
            var failed = Assert.Single(index.List("failed").Items);
            Assert.Equal(broken.AssetId, failed.AssetId);
            encoder.AllowBroken = true;
            index.Retry(failed.JobId);
            await Eventually(() => index.List(null).Completed == 3);
        }

        var reusable = new TestPhotoEncoder();
        await using var reopened = new PhotoIndex(database, databasePath, () => reusable, TextWriter.Null);
        await Eventually(() => reopened.State.Status == "ready" && reopened.List(null).Completed == 3);
        Assert.Equal(0, reusable.ImageCalls);
        // An in-place edit must not be ranked against a previously indexed image.
        File.WriteAllBytes(valid.FinalPath, [1, 2, 3, 4]);
        Assert.DoesNotContain(reopened.Search("海边晚霞", JsonSerializer.SerializeToElement(new { })).Items,
            item => item.AssetId == valid.AssetId);
        Assert.NotEmpty(database.Filter(JsonSerializer.SerializeToElement(new { remarkTerms = new[] { "青岛" } })).Items);
    }

    [Fact]
    public async Task MissingPhotoModelLeavesLocalTextQueriesAvailable()
    {
        using var files = new TestDirectory();
        var databasePath = Path.Combine(files.FullName, "library.sqlite3");
        using var database = new LibraryDatabase(databasePath);
        var known = Archive(database, files.FullName, "海边照片", "青岛", [1]);
        await using var index = new PhotoIndex(database, databasePath, Path.Combine(files.FullName, "missing"), TextWriter.Null);
        await Eventually(() => index.State.Status == "missing");
        var condition = JsonSerializer.SerializeToElement(new { remarkTerms = new[] { "青岛" } });
        Assert.Empty(index.Search("海边晚霞", condition).Items);
        Assert.Equal(known.AssetId, Assert.Single(database.Filter(condition).Items).AssetId);
        Assert.Equal(1, index.List(null).Queued);
    }

    [PhotoWeights]
    public void OnnxVectorsMatchPytorchAndRankChineseQueries()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("APM_TEST_PHOTO_MODEL")!;
        var started = Stopwatch.StartNew();
        using var model = new ChineseClip(modelDirectory);
        output.WriteLine($"Model load (CPU, including hashes): {started.Elapsed.TotalMilliseconds:F0} ms");
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "parity.json")));
        var root = reference.RootElement;
        var sampleText = root.GetProperty("sampleText").GetString()!;
        var tokenizer = new ChineseClipTokenizer(Path.Combine(modelDirectory, "vocab.txt"));
        Assert.Equal(root.GetProperty("tokens").EnumerateArray().Select(item => item.GetInt64()),
            tokenizer.Encode(sampleText, ChineseClip.TextLength).Ids);
        started.Restart();
        var csharpText = model.Text(sampleText);
        var textSimilarity = Similarity(csharpText, Reference("text"));
        output.WriteLine($"Text inference (CPU): {started.Elapsed.TotalMilliseconds:F0} ms. PyTorch cosine: {textSimilarity:F7}");
        Assert.True(textSimilarity >= 0.99999f, "Text vector diverged from PyTorch");
        using (var festival = File.OpenRead(Path.Combine(modelDirectory, "festival.jpg")))
        {
            started.Restart();
            var actual = model.Image(festival);
            var similarity = Similarity(actual, Reference("image"));
            output.WriteLine($"Image inference (CPU, WIC + resize): {started.Elapsed.TotalMilliseconds:F0} ms. PyTorch cosine: {similarity:F7}");
            Assert.True(similarity >= 0.999f, $"Photo vector diverged from PyTorch: {similarity:F7}");
        }

        var samples = new[] { "coast", "mountains", "forest", "city", "desert", "beach" };
        var vectors = samples.ToDictionary(name => name, name =>
        {
            using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".jpg"));
            return model.Image(file);
        });
        var queries = new (string Text, string Expected)[]
        {
            ("海边晚霞", "coast"), ("远处的雪山", "mountains"), ("森林小路", "forest"),
            ("城市夜景", "city"), ("起伏的沙丘", "desert"), ("阳光下的海滩", "beach")
        };
        var hits = 0;
        foreach (var (query, expected) in queries)
        {
            var text = model.Text(query);
            var ranked = vectors.Select(item => (item.Key, Score: Similarity(item.Value, text)))
                .OrderByDescending(item => item.Score).ToArray();
            if (ranked[0].Key == expected) hits++;
            output.WriteLine($"{query}: {string.Join(", ", ranked.Select(item => $"{item.Key}={item.Score:F3}"))}");
        }
        output.WriteLine($"Small sample Hit@1 {hits}/{queries.Length}");
        Assert.True(hits >= 4, "Chinese image search should find relevant demo photos");

        float[] Reference(string key) => ChineseClip.Normalize(root.GetProperty(key).EnumerateArray()
            .Select(item => item.GetSingle()).ToArray());
    }

    private static float Similarity(float[] first, float[] second) => first.Zip(second, (left, right) => left * right).Sum();

    private static ArchiveStatus Archive(LibraryDatabase database, string folder, string name, string remark,
        byte[] bytes, string extension = ".jpg")
    {
        var file = Path.Combine(folder, name + extension);
        File.WriteAllBytes(file, bytes);
        var target = database.AddDirectory(Directory.CreateDirectory(Path.Combine(folder, "归档")).FullName, true);
        var id = database.Import([file])[0].AssetId!;
        var draft = database.SaveDraft(id, 0, JsonSerializer.SerializeToElement(new
        {
            directoryId = target.DirectoryId, finalized = true, remark = new { intent = "set", value = remark }
        }));
        var plan = database.PreviewArchive(id, draft.ArchiveRevision, target.DirectoryId);
        var operation = Guid.NewGuid().ToString();
        database.AcceptArchive(operation, plan.PlanToken);
        new ArchiveExecutor(database, new TestProperties()).Run(operation);
        var status = database.GetArchive(operation);
        Assert.Equal("complete", status.Phase);
        return status;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "Photo indexing task did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private sealed class TestPhotoEncoder : IPhotoEncoder
    {
        public volatile bool AllowBroken;
        public int ImageCalls;
        public string Version => "test-v1";
        public float[] Image(Stream file)
        {
            Interlocked.Increment(ref ImageCalls);
            if (file.ReadByte() == 3 && !AllowBroken) throw new InvalidDataException();
            return Text("");
        }
        public float[] Text(string query)
        {
            var result = new float[ChineseClip.Dimensions];
            result[0] = 1;
            return result;
        }
        public void Dispose() { }
    }

    private sealed class TestProperties : IArchiveProperties
    {
        public string WriteRemark(string path, string value) => "library_only";
        public string WriteAuthors(string path, string[] value) => "library_only";
    }

    private sealed class PhotoWeightsAttribute : FactAttribute
    {
        public PhotoWeightsAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APM_TEST_PHOTO_MODEL")))
                Skip = "Set APM_TEST_PHOTO_MODEL to the exported ONNX package and parity sample.";
        }
    }
}
