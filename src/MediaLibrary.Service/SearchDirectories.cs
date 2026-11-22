namespace MediaLibrary.Service;

internal sealed record DirectoryIndexProgress(string Phase, int Processed, int Discovered, int? Total = null);

public sealed partial class LibraryDatabase
{
    internal string SearchDatabasePath => connection.DataSource;
    internal SearchPage IndexDirectories(System.Text.Json.JsonElement parameters, Action<DirectoryIndexProgress>? progress = null)
    {
        var filter = NormalizeFilter(ParseFilter(parameters));
        if (filter.DirectoryPaths is not { } paths) throw new RequestError("INVALID_ARGUMENT", "请添加查找目录。");
        IndexSearchDirectories(paths, filter.IncludeSubdirectories, progress);
        return new([], null);
    }

    private SearchDirectoryItem[]? FindSearchDirectories(SearchFilter filter, TimeZoneInfo timeZone)
    {
        if (filter.DirectoryPaths is null || filter.ResultKind == "files" || filter.MediaKind is not null || filter.PropertyRanges is { Length: > 0 } ||
            filter.Format is not null || filter.Author is not null || filter.RemarkTerms!.Length > 0 ||
            filter.CaptureMinuteFrom is not null || filter.CaptureMinuteToExclusive is not null ||
            (filter.DateField == "capturedAt" && (filter.DateFrom is not null || filter.DateToExclusive is not null))) return null;
        var found = new Dictionary<string, SearchDirectoryItem>(StringComparer.OrdinalIgnoreCase);
        var keywords = filter.KeywordTerms!.Select(term => (Term: term, Range: DateKeywordRange(term, timeZone))).ToArray();
        foreach (var root in filter.DirectoryPaths)
        {
            using var command = Command("SELECT path, created FROM search_folders WHERE substr(path,1,length($prefix)) = $prefix COLLATE NOCASE", null,
                ("$prefix", Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                    var path = reader.GetString(0);
                    if (!filter.IncludeSubdirectories && !WindowsPaths.SamePath(Path.GetDirectoryName(path)!, root)) continue;
                    var name = Path.GetFileName(path);
                    var created = reader.GetString(1);
                    var nameKey = TextKey(name);
                    if (!filter.NameTerms!.All(term => nameKey.Contains(term, StringComparison.Ordinal))) continue;
                    if (!(filter.KeywordGroups ?? []).All(group => group.Any(term => nameKey.Contains(term, StringComparison.Ordinal)))) continue;
                    if (!keywords.All(keyword => keyword.Range is { } range
                        ? DateKeywordMatches(created, range)
                        : nameKey.Contains(keyword.Term, StringComparison.Ordinal))) continue;
                    if (filter.DateFrom is { } from && string.CompareOrdinal(created, UtcBoundary(from, timeZone)) < 0) continue;
                    if (filter.DateToExclusive is { } until && string.CompareOrdinal(created, UtcBoundary(until, timeZone)) >= 0) continue;
                    found[path] = new(name, path, created);
            }
        }
        return found.Count == 0 ? null : found.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RefreshSearchProperties(string id, string path, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        MediaProperties properties, DateTime? captured)
    {
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
