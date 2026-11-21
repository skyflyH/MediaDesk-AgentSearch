using System.Security.Cryptography;

namespace MediaLibrary.Service;

internal sealed class PhotoIndex : IAsyncDisposable
{
    private readonly LibraryDatabase database;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private volatile IPhotoEncoder? encoder;
    private volatile PhotoIndexState state = new("loading", null, "正在检查本地照片模型。");
    public PhotoIndexState State => state;

    public PhotoIndex(LibraryDatabase database, string databasePath, string modelDirectory, TextWriter diagnostics)
        : this(database, databasePath, () => new ChineseClip(modelDirectory), diagnostics,
            () => File.Exists(Path.Combine(modelDirectory, "manifest.json"))) { }

    internal PhotoIndex(LibraryDatabase database, string databasePath, Func<IPhotoEncoder> load, TextWriter diagnostics,
        Func<bool>? exists = null)
    {
        this.database = database;
        worker = Task.Run(async () =>
        {
            using var background = new LibraryDatabase(databasePath);
            background.RecoverPhotoJobs();
            try
            {
                if (exists?.Invoke() == false)
                {
                    state = new("missing", null, "未找到本地照片模型。");
                    return;
                }
                encoder = load();
            }
            catch (Exception error)
            {
                diagnostics.WriteLine($"Photo model startup failed ({error.GetType().Name}).");
                state = new("invalid", null, "本地照片模型无法加载，请检查文件完整性后重开应用。");
                return;
            }

            var local = encoder;
            state = new("ready", local.Version, null);
            while (!stop.IsCancellationRequested)
            {
                var nextSync = DateTime.UtcNow.AddSeconds(3);
                background.SyncPhotoJobs(local.Version);
                while (!stop.IsCancellationRequested && DateTime.UtcNow < nextSync && background.ClaimPhotoJob(local.Version) is { } job)
                    Index(background, local, job, diagnostics);
                if (stop.IsCancellationRequested) break;
                await wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, (nextSync - DateTime.UtcNow).TotalMilliseconds)), stop.Token);
            }
        }, stop.Token);
    }

    public PhotoJobList List(string? status) => database.PhotoJobs(State, status);

    public PhotoJobList Retry(string id)
    {
        if (State.Version is not { } version)
            throw new RequestError("PHOTO_MODEL_UNAVAILABLE", "本地照片模型暂不可用。");
        if (!database.RetryPhotoJob(id, version))
            throw new RequestError("INVALID_ARGUMENT", "该照片任务目前无法重试。");
        try { wake.Release(); } catch (SemaphoreFullException) { }
        return List(null);
    }

    public PhotoSceneResult Search(string query, System.Text.Json.JsonElement filters)
    {
        var current = State;
        if (current.Status != "ready" || encoder is not { } local) return new(current, []);
        float[] text;
        try { text = local.Text(query); }
        catch (Exception)
        {
            return new(new("invalid", current.Version, "本地照片模型无法处理该查询。"), []);
        }
        var matches = database.RankPhotos(filters, local.Version, text, query);
        return new(matches.Length == 0 && database.PhotoJobs(current, null).Completed == 0
            ? new("indexing", current.Version, "还没有可用的照片索引。") : current, matches);
    }

    private static void Index(LibraryDatabase database, IPhotoEncoder encoder, PhotoWork job, TextWriter diagnostics)
    {
        try
        {
            var asset = database.Locate(job.AssetId);
            using var file = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stamp = FileStamp.Read(file.SafeFileHandle);
            if (stamp.Identity != asset.Identity)
                throw new RequestError("SOURCE_UNAVAILABLE", "原件已被替换。");
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(job.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new RequestError("SOURCE_CHANGED", "原件在归档后发生变化。");
            file.Position = 0;
            var vector = encoder.Image(file);
            database.CompletePhotoJob(encoder.Version, job, stamp, vector);
        }
        catch (Exception error)
        {
            var code = error switch
            {
                RequestError request => request.Code,
                FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => "SOURCE_UNAVAILABLE",
                _ => "PHOTO_INDEX_FAILED"
            };
            diagnostics.WriteLine($"Photo indexing failed ({code}, {error.GetType().Name}).");
            database.FailPhotoJob(job, encoder.Version, code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { encoder?.Dispose(); stop.Dispose(); wake.Dispose(); }
    }
}
