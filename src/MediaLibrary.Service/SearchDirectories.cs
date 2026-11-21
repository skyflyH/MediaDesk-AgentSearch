namespace MediaLibrary.Service;

internal sealed record DirectoryIndexProgress(string Phase, int Processed, int Discovered, int? Total = null);

public sealed partial class LibraryDatabase
{
    internal string SearchDatabasePath => connection.DataSource;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Modified, long XmpLength, long XmpModified)> searchStamps = new();

    internal SearchPage IndexDirectories(System.Text.Json.JsonElement parameters, Action<DirectoryIndexProgress>? progress = null)
    {
        var filter = NormalizeFilter(ParseFilter(parameters));
        if (filter.DirectoryPaths is not { } paths) throw new RequestError("INVALID_ARGUMENT", "请添加查找目录。");
        IndexSearchDirectories(paths, filter.IncludeSubdirectories, progress);
        return new([], null);
    }

    private static SearchDirectoryItem[]? FindSearchDirectories(SearchFilter filter, TimeZoneInfo timeZone)
    {
        if (filter.DirectoryPaths is null || filter.ResultKind == "files" || filter.MediaKind is not null || filter.PropertyRanges is { Length: > 0 } ||
            filter.Format is not null || filter.Author is not null || filter.RemarkTerms!.Length > 0 ||
            filter.CaptureMinuteFrom is not null || filter.CaptureMinuteToExclusive is not null ||
            (filter.DateField == "capturedAt" && (filter.DateFrom is not null || filter.DateToExclusive is not null))) return null;
        var found = new Dictionary<string, SearchDirectoryItem>(StringComparer.OrdinalIgnoreCase);
        var keywords = filter.KeywordTerms!.Select(term => (Term: term, Range: DateKeywordRange(term, timeZone))).ToArray();
        foreach (var root in filter.DirectoryPaths)
        {
            if (!Directory.Exists(root)) throw new RequestError("SOURCE_UNAVAILABLE", $"搜索目录不可访问：{root}");
            try
            {
                foreach (var path in Directory.EnumerateDirectories(root, "*", new EnumerationOptions {
                    RecurseSubdirectories = filter.IncludeSubdirectories, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    var name = Path.GetFileName(path);
                    var created = Directory.GetCreationTimeUtc(path).ToString("O");
                    var nameKey = TextKey(name);
                    if (!filter.NameTerms!.All(term => nameKey.Contains(term, StringComparison.Ordinal))) continue;
                    if (!keywords.All(keyword => keyword.Range is { } range
                        ? DateKeywordMatches(created, range)
                        : nameKey.Contains(keyword.Term, StringComparison.Ordinal))) continue;
                    if (filter.DateFrom is { } from && string.CompareOrdinal(created, UtcBoundary(from, timeZone)) < 0) continue;
                    if (filter.DateToExclusive is { } until && string.CompareOrdinal(created, UtcBoundary(until, timeZone)) >= 0) continue;
                    found[path] = new(name, path, created);
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                throw new RequestError("SOURCE_UNAVAILABLE", $"无法搜索目录：{root}");
            }
        }
        return found.Count == 0 ? null : found.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void IndexSearchDirectories(string[] paths, bool recursive, Action<DirectoryIndexProgress>? progress = null)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        progress?.Invoke(new("scanning", 0, 0));
        foreach (var root in paths)
        {
            if (!Directory.Exists(root))
                throw new RequestError("SOURCE_UNAVAILABLE", $"搜索目录不可访问：{root}");
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                // Do not follow directory junctions or symbolic links out of the selected tree.
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!visited.Add(path)) continue;
                    var extension = Path.GetExtension(path);
                    if (!PhotoExtensions.Contains(extension) && !VideoExtensions.Contains(extension) && !RawPhoto.Supports(extension))
                        continue;
                    files.Add(path);
                    if (files.Count % 100 == 0) progress?.Invoke(new("scanning", 0, files.Count));
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                throw new RequestError("SOURCE_UNAVAILABLE", $"无法扫描搜索目录：{root}");
            }
        }
        var processed = 0;
        progress?.Invoke(new("indexing", 0, files.Count, files.Count));
        foreach (var path in files)
        {
            try {
                    var extension = Path.GetExtension(path);
                    var file = new FileInfo(path);
                    var cacheKey = SearchDatabasePath + "|" + path;
                    var xmp = RawPhoto.Supports(extension) ? new FileInfo(RawSidecar.PathFor(path)) : null;
                    var stamp = (file.Length, file.LastWriteTimeUtc.Ticks, xmp?.Exists == true ? xmp.Length : -1,
                        xmp?.Exists == true ? xmp.LastWriteTimeUtc.Ticks : -1);
                    if (searchStamps.TryGetValue(cacheKey, out var previous) && previous == stamp) continue;
                    var imported = ImportOne(path, queue: false);
                    if (imported.AssetId is not { } id) continue;
                    using var transaction = connection.BeginTransaction(deferred: false);
                    var located = Locate(id, transaction);
                    if (WindowsPaths.SamePath(located.Path, path))
                    {
                        RefreshSearchProperties(id, path, transaction);
                        IndexAsset(id, transaction);
                    }
                    transaction.Commit();
                    if (searchStamps.Count > 100000) searchStamps.Clear();
                    searchStamps[cacheKey] = stamp;
            }
            catch (Exception error) when (IsFileError(error))
            {
                throw new RequestError("SOURCE_UNAVAILABLE", $"无法读取搜索素材：{path}");
            }
            finally {
                processed++;
                progress?.Invoke(new("indexing", processed, files.Count, files.Count));
            }
        }
        progress?.Invoke(new("complete", processed, files.Count, files.Count));
    }

    private void RefreshSearchProperties(string id, string path, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var properties = WindowsProperties.Read(path);
        var captured = properties.CapturedAt;
        if (captured is null && Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v")
            captured = EmbeddedMetadata.ReadVideoCaptureTime(path);
        using var update = Command("""
            UPDATE asset_details SET
                remark = CASE WHEN remark_state = 'library_only' AND $raw = 0 THEN remark ELSE $remark END,
                authors = CASE WHEN authors_state = 'library_only' THEN authors ELSE $authors END
            WHERE asset_id = $id;
            UPDATE assets SET captured_at = $captured WHERE id = $id;
            """, transaction, ("$id", id), ("$raw", RawPhoto.Supports(Path.GetExtension(path)) ? 1 : 0), ("$remark", properties.Remark),
            ("$authors", System.Text.Json.JsonSerializer.Serialize(properties.Authors, WorkspaceJson)),
            ("$captured", captured?.ToString("O")));
        update.ExecuteNonQuery();
    }
}
