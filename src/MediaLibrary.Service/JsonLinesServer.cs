using System.Text.Json;

namespace MediaLibrary.Service;

public sealed class JsonLinesServer
{
    private readonly LibraryDatabase database;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly TextWriter diagnostics;
    private readonly Action<string> scheduleArchive;
    private readonly MediaPreviews? previews;
    private readonly PhotoIndex? photos;
    private readonly SemaphoreSlim searchGate = new(1);
    private SearchSyncWorker? searchSync;
    private readonly Dictionary<string, Task<SearchPage>> searches = [];
    private readonly Dictionary<string, string> searchKeys = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DirectoryIndexProgress> indexProgress = new();

    public JsonLinesServer(LibraryDatabase database, TextReader input, TextWriter output, TextWriter diagnostics)
        : this(database, input, output, diagnostics, _ => { }, null, null) { }

    internal JsonLinesServer(LibraryDatabase database, TextReader input, TextWriter output, TextWriter diagnostics,
        Action<string> scheduleArchive, MediaPreviews? previews, PhotoIndex? photos)
    {
        this.database = database;
        this.input = input;
        this.output = output;
        this.diagnostics = diagnostics;
        this.scheduleArchive = scheduleArchive;
        this.previews = previews;
        this.photos = photos;
    }
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync()
    {
        await using var synchronization = new SearchSyncWorker(database.SearchDatabasePath, diagnostics);
        searchSync = synchronization;
        while (await input.ReadLineAsync() is { } line)
        {
            var response = Respond(line);
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            await output.FlushAsync();
        }
    }

    private object Respond(string line)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object)
                return Error(null, "INVALID_ARGUMENT", "A request must be an object.");

            id = ReadString(request, "id");
            var method = ReadString(request, "method");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method) ||
                !request.TryGetProperty("v", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != Version ||
                !request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            {
                return Error(id, "INVALID_ARGUMENT", "Invalid request envelope.");
            }

            var result = Dispatch(method, parameters);
            return new { v = Version, id, result };
        }
        catch (JsonException)
        {
            return Error(id, "INVALID_ARGUMENT", "Malformed JSON.");
        }
        catch (RequestError error)
        {
            return Error(id, error.Code, error.Message);
        }
        catch (Exception error)
        {
            diagnostics.WriteLine($"Request failed ({error.GetType().Name}).");
            return Error(id, "INTERNAL_ERROR", "Local service could not complete the request.");
        }
    }

    private object? Dispatch(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "app.health":
                RequireKeys(parameters);
                return new
                {
                    protocolVersion = Version,
                    runtime = Environment.Version.ToString(),
                    database = database.Health(),
                    capabilities = new
                    {
                        wic = previews is not null && OperatingSystem.IsWindows() ? "available" : "unavailable",
                        ffmpeg = previews?.FfmpegAvailable == true ? "available" : "unavailable",
                        photoModel = photos?.State.Status ?? "not_implemented"
                    }
                };
            case "asset.import":
                RequireKeys(parameters, "paths");
                var paths = parameters.GetProperty("paths");
                if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() is 0 or > 1000)
                    throw InvalidParameters();
                var pathList = paths.EnumerateArray().Select(path => path.ValueKind == JsonValueKind.String
                    && path.GetString() is { Length: > 0 and <= 32767 } value ? value : throw InvalidParameters());
                return database.Import(pathList.ToArray());
            case "workspace.load":
                RequireKeys(parameters);
                return database.LoadWorkspace();
            case "workspace.removeAssets":
                RequireKeys(parameters, "assetIds");
                var removalIds = parameters.GetProperty("assetIds");
                if (removalIds.ValueKind != JsonValueKind.Array || removalIds.GetArrayLength() == 0)
                    throw InvalidParameters();
                return database.RemoveFromQueue(removalIds.EnumerateArray().Select(value =>
                    value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var id)
                        ? id.ToString() : throw InvalidParameters()).ToArray());
            case "workspace.addImportFolder":
                RequireKeys(parameters, "path");
                database.AddImportFolder(RequiredString(parameters, "path"));
                return null;
            case "workspace.removeFolder":
                RequireKeys(parameters, "root", "path");
                return database.RemoveImportFolder(RequiredString(parameters, "root"), RequiredString(parameters, "path"));
            case "workspace.browseFolder":
                RequireKeys(parameters, "root", "path");
                return database.BrowseImportFolder(RequiredString(parameters, "root"), RequiredString(parameters, "path"));
            case "workspace.describeFolder":
                RequireKeys(parameters, "root", "path");
                return database.DescribeImportFolder(RequiredString(parameters, "root"), RequiredString(parameters, "path"));
            case "workspace.setCurrent":
                RequireKeys(parameters, "assetId");
                return database.SetCurrent(parameters.GetProperty("assetId").ValueKind == JsonValueKind.Null
                    ? null : AssetId(parameters));
            case "draft.get":
                RequireKeys(parameters, "assetId");
                return database.GetDraft(AssetId(parameters));
            case "draft.assetContext":
                RequireKeys(parameters, "assetId");
                return database.GetPendingAsset(AssetId(parameters));
            case "draft.save":
                RequireKeys(parameters, "assetId", "expectedRevision", "changes");
                var revision = parameters.GetProperty("expectedRevision");
                var changes = parameters.GetProperty("changes");
                if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var expected)
                    || expected < 0 || changes.ValueKind != JsonValueKind.Object || !changes.EnumerateObject().MoveNext())
                    throw InvalidParameters();
                return database.SaveDraft(AssetId(parameters), expected, changes);
            case "draft.saveSourceProperties":
                RequireKeys(parameters, "assetId", "expectedRevision");
                var sourceRevision = parameters.GetProperty("expectedRevision");
                if (sourceRevision.ValueKind != JsonValueKind.Number || !sourceRevision.TryGetInt32(out var sourceVersion) || sourceVersion < 0)
                    throw InvalidParameters();
                return database.SaveSourceProperties(AssetId(parameters), sourceVersion, new WindowsProperties(database.ProfileDirectory));
            case "draft.saveRecommendation":
                RequireKeys(parameters, "assetId", "expectedRevision", "kind", "requestState");
                var suggestionRevision = parameters.GetProperty("expectedRevision");
                if (!suggestionRevision.TryGetInt32(out var suggestionVersion) || suggestionVersion < 0) throw InvalidParameters();
                var suggestionKind = RequiredString(parameters, "kind");
                if (suggestionKind is not ("name" or "remark" or "directory")) throw InvalidParameters();
                var suggestion = ParseSuggestion(parameters.GetProperty("requestState"), suggestionKind);
                return database.SaveSuggestion(AssetId(parameters), suggestionVersion, suggestionKind, suggestion);
            case "archive.addRoot":
                RequireKeys(parameters, "path");
                return database.AddDirectory(RequiredString(parameters, "path"), root: true);
            case "archive.addDirectory":
                RequireKeys(parameters, "path");
                return database.AddDirectory(RequiredString(parameters, "path"), root: false);
            case "archive.listDirectories":
                if (parameters.TryGetProperty("rootId", out _))
                {
                    RequireKeys(parameters, "rootId");
                    var root = RequiredId(parameters, "rootId");
                    return database.ListDirectories().Where(directory => directory.RootId == root || directory.DirectoryId == root);
                }
                RequireKeys(parameters);
                return database.ListDirectories();
            case "archive.recommendationDirectories":
                RequireKeys(parameters, "assetId");
                return database.RecommendationDirectories(AssetId(parameters));
            case "archive.preview":
                RequireKeys(parameters, "assetId", "archiveRevision", "directoryId");
                var archiveRevision = parameters.GetProperty("archiveRevision");
                if (archiveRevision.ValueKind != JsonValueKind.Number || !archiveRevision.TryGetInt32(out var archiveVersion) || archiveVersion < 0)
                    throw InvalidParameters();
                return database.PreviewArchive(AssetId(parameters), archiveVersion, RequiredId(parameters, "directoryId"));
            case "archive.commit":
                RequireKeys(parameters, "operationId", "planToken");
                var accepted = database.AcceptArchive(RequiredId(parameters, "operationId"), RequiredString(parameters, "planToken"));
                if (accepted.Phase == "accepted") scheduleArchive(accepted.OperationId);
                return accepted;
            case "archive.status":
                RequireKeys(parameters, "operationId");
                return database.GetArchive(RequiredId(parameters, "operationId"));
            case "archive.retryProperties":
                RequireKeys(parameters, "operationId");
                var status = database.PreparePropertyRetry(RequiredId(parameters, "operationId"));
                if (status.Phase == "properties_pending" && status.ErrorCode is null)
                    scheduleArchive(status.OperationId);
                return status;
            case "asset.revealPath":
                RequireKeys(parameters, "assetId");
                return new { path = database.RevealPath(AssetId(parameters)) };
            case "search.filter":
                PrepareSearchScope(parameters);
                return database.Filter(parameters);
            case "search.setRoots":
                RequireKeys(parameters, "directoryPaths");
                var rootPaths = parameters.GetProperty("directoryPaths");
                if (rootPaths.ValueKind != JsonValueKind.Array || rootPaths.GetArrayLength() > 50) throw InvalidParameters();
                database.SetSearchRoots(rootPaths.EnumerateArray().Select(value =>
                    value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidParameters()).ToArray());
                searchSync?.Schedule();
                return null;
            case "search.start":
                return StartSearch(parameters);
            case "search.indexStart":
                return StartSearch(parameters, indexOnly: true);
            case "search.status":
                RequireKeys(parameters, "jobId");
                return SearchStatus(RequiredString(parameters, "jobId"));
            case "search.getDetails":
                RequireKeys(parameters, "assetId");
                return database.GetSearchDetails(AssetId(parameters));
            case "search.photosByScene":
                RequireKeys(parameters, "query", "filter");
                var scene = RequiredString(parameters, "query").Trim();
                var sceneFilter = parameters.GetProperty("filter");
                if (scene.Length is 0 or > 240 || sceneFilter.ValueKind != JsonValueKind.Object ||
                    scene.EnumerateRunes().Count() < 2) throw InvalidParameters();
                return Photos().Search(scene, sceneFilter);
            case "job.list":
                if (!parameters.TryGetProperty("state", out _))
                {
                    RequireKeys(parameters);
                    return Photos().List(null);
                }
                RequireKeys(parameters, "state");
                var photoState = RequiredString(parameters, "state");
                if (photoState is not ("queued" or "running" or "completed" or "failed")) throw InvalidParameters();
                return Photos().List(photoState);
            case "job.retry":
                RequireKeys(parameters, "jobId");
                return Photos().Retry(RequiredId(parameters, "jobId"));
            case "preview.resolve":
                RequireKeys(parameters, "assetId", "kind");
                return PreviewService().Resolve(AssetId(parameters), RequiredString(parameters, "kind"));
            case "preview.requestProxy":
                RequireKeys(parameters, "assetId");
                return PreviewService().RequestProxy(AssetId(parameters));
            case "preview.cancelProxy":
                RequireKeys(parameters, "assetId");
                PreviewService().CancelProxy(AssetId(parameters));
                return null;
            case "preview.proxyStatus":
                RequireKeys(parameters, "assetId");
                return PreviewService().ProxyStatus(AssetId(parameters));
            case "preview.clearCache":
                RequireKeys(parameters);
                return PreviewService().ClearCache();
            case "preview.cacheDirectory":
                RequireKeys(parameters);
                return PreviewService().CacheDirectory;
            case "preview.setCacheDirectory":
                RequireKeys(parameters, "path");
                return PreviewService().SetCacheDirectory(RequiredString(parameters, "path"));
            default:
                throw new RequestError("METHOD_NOT_FOUND", "Unknown method.");
        }
    }

    private static void RequireKeys(JsonElement parameters, params string[] keys)
    {
        if (parameters.EnumerateObject().Count() != keys.Length || keys.Any(key => !parameters.TryGetProperty(key, out _)))
            throw InvalidParameters();
    }

    private MediaPreviews PreviewService() => previews ?? throw new RequestError("PREVIEW_UNAVAILABLE", "预览服务不可用。");
    private PhotoIndex Photos() => photos ?? throw new RequestError("PHOTO_MODEL_UNAVAILABLE", "本地照片模型暂不可用。");

    private static SuggestionState ParseSuggestion(JsonElement value, string kind)
    {
        var state = value.Deserialize<SuggestionState>(JsonOptions) ?? throw InvalidParameters();
        if (!Guid.TryParseExact(state.RequestId, "D", out _) || state.Status is not ("pending" or "ready" or "retry"))
            throw InvalidParameters();
        if (state.Status != "ready")
        {
            if (state.Names is not null || state.Remark is not null || state.Candidates is not null ||
                (state.Status == "pending" && state.ErrorCode is not null) ||
                (state.Status == "retry" && state.ErrorCode is not
                    ("MODEL_UNAVAILABLE" or "MODEL_INVALID_OUTPUT" or "MODEL_NOT_CONFIGURED" or "MODEL_INTERRUPTED")))
                throw InvalidParameters();
            return state;
        }
        if (state.ErrorCode is not null) throw InvalidParameters();
        var valid = kind switch
        {
            "name" => state.Names is { Length: <= 3 } names &&
                names.All(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 255) &&
                state.Remark is null && state.Candidates is null,
            "remark" => state.Remark is { Length: <= 4000 } && state.Names is null && state.Candidates is null,
            _ => state.Candidates is { Length: <= 5 } directories && state.Names is null && state.Remark is null &&
                directories.All(item => item is not null && Guid.TryParseExact(item.DirectoryId, "D", out _) &&
                    item.Reason is { Length: > 0 and <= 240 }) &&
                directories.Select(item => item.DirectoryId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == directories.Length
        };
        return valid ? state : throw InvalidParameters();
    }

    private static string AssetId(JsonElement parameters) => RequiredId(parameters, "assetId");

    private object StartSearch(JsonElement parameters, bool indexOnly = false)
    {
        PrepareSearchScope(parameters);
        var key = (indexOnly ? "index:" : "search:") + parameters.GetRawText();
        // Coalesce repeated submissions while the same search is still running.
        if (searchKeys.TryGetValue(key, out var existing) && searches.TryGetValue(existing, out var pending) && !pending.IsCompleted)
            return new { jobId = existing };
        // Retain finished jobs so a new submission cannot invalidate an outstanding status poll.
        foreach (var id in searches.Where(item => item.Value.IsCompleted).Take(Math.Max(0, searches.Count - 128)).Select(item => item.Key).ToArray())
        {
            searches.Remove(id);
            indexProgress.TryRemove(id, out _);
            foreach (var oldKey in searchKeys.Where(item => item.Value == id).Select(item => item.Key).ToArray()) searchKeys.Remove(oldKey);
        }
        if (searches.Count(item => !item.Value.IsCompleted) >= 16) throw new RequestError("SEARCH_BUSY", "搜索任务较多，请等待当前搜索完成。");
        var jobId = Guid.NewGuid().ToString();
        var input = parameters.Clone();
        var path = database.SearchDatabasePath;
        if (indexOnly) indexProgress[jobId] = new("queued", 0, 0);
        searches[jobId] = Task.Run(async () =>
        {
            if (indexOnly && searchSync is not null)
            {
                await searchSync.EnsureIndexedAsync(input, progress => indexProgress[jobId] = progress);
                return new SearchPage([], null);
            }
            await searchGate.WaitAsync();
            try
            {
                // Each worker owns its SQLite connection; the command loop remains responsive.
                using var worker = new LibraryDatabase(path);
                return indexOnly ? worker.IndexDirectories(input, progress => indexProgress[jobId] = progress) : worker.Filter(input);
            }
            finally { searchGate.Release(); }
        });
        searchKeys[key] = jobId;
        return new { jobId };
    }

    private void PrepareSearchScope(JsonElement parameters)
    {
        if (searchSync is null || !parameters.TryGetProperty("directoryPaths", out _)) return;
        var (paths, recursive) = LibraryDatabase.SearchIndexRequest(parameters);
        if (database.RegisterSearchRoots(paths, recursive)) searchSync.Schedule();
    }

    private object SearchStatus(string jobId)
    {
        if (!searches.TryGetValue(jobId, out var task)) throw new RequestError("INVALID_ARGUMENT", "搜索任务已失效，请重新搜索。");
        indexProgress.TryGetValue(jobId, out var progress);
        if (!task.IsCompleted) return new { state = "running", progress };
        if (task.IsCompletedSuccessfully) return new { state = "complete", page = task.Result, progress };
        var failure = task.Exception?.GetBaseException();
        return new { state = "failed", error = new {
            code = failure is RequestError request ? request.Code : "SEARCH_FAILED",
            message = failure is RequestError ? failure.Message : "无法完成目录搜索，请重试。",
            retryable = true
        } };
    }

    private static string RequiredId(JsonElement parameters, string key) =>
        Guid.TryParseExact(ReadString(parameters, key), "D", out var id)
            ? id.ToString() : throw InvalidParameters();

    private static string RequiredString(JsonElement parameters, string key) =>
        ReadString(parameters, key) is { Length: > 0 and <= 32767 } value ? value : throw InvalidParameters();

    private static RequestError InvalidParameters() => new("INVALID_ARGUMENT", "本地请求参数无效。");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static object Error(string? id, string code, string message) =>
        new { v = Version, id, error = new { code, message, retryable = code is "REVISION_CONFLICT" or "DESTINATION_UNAVAILABLE" or "SOURCE_UNAVAILABLE" } };
}
