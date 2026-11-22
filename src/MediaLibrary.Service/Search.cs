using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace MediaLibrary.Service;

public sealed record SearchEvidence(string Field, string Value, string[] Terms);
public sealed record SearchItem(string AssetId, string FileName, string MediaKind, string Format,
    string CreatedAt, string? CapturedAt, string[] Authors, string RemarkSummary, bool Available,
    SearchEvidence[] Matches, float? SceneScore = null, string? SourcePath = null, string? DirectoryPath = null,
    Dictionary<string, double>? Properties = null);
public sealed record SearchDirectoryItem(string Name, string Path, string CreatedAt);
public sealed record SearchPage(SearchItem[] Items, string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SearchDirectoryItem[]? Directories = null);
public sealed record SearchDetails(SearchItem Asset, string Path, string Remark, string RemarkState, string AuthorsState,
    string? CaptureTimeSource);

internal sealed record SearchFilter(string? MediaKind = null, string? DirectoryId = null,
    bool IncludeSubdirectories = true, string? Format = null, string? DateField = null, string? DateFrom = null,
    string? DateToExclusive = null, string[]? NameTerms = null, string[]? RemarkTerms = null, string? Author = null,
    int Limit = 50, string? Cursor = null, string[]? DirectoryPaths = null, string[]? KeywordTerms = null, string? ResultKind = null,
    int? CaptureMinuteFrom = null, int? CaptureMinuteToExclusive = null, PropertyRange[]? PropertyRanges = null,
    string[][]? KeywordGroups = null, bool IncludeDirectoryNames = false);

public sealed partial class LibraryDatabase
{
    private const string SearchColumns = """
        a.id, a.media_kind, a.extension, a.created_at, a.captured_at, l.current_path, l.file_identity,
        l.file_created_ticks, COALESCE(d.authors, '[]'), COALESCE(d.remark, ''),
        COALESCE(d.remark_state, 'unchanged'), COALESCE(d.authors_state, 'unchanged'), l.source_path
        """;
    private const string SearchFrom = """
        FROM assets a JOIN asset_locations l ON l.asset_id = a.id
        LEFT JOIN asset_details d ON d.asset_id = a.id
        """;
    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public SearchPage Filter(JsonElement parameters) => Filter(ParseFilter(parameters), TimeZoneInfo.Local);

    internal SearchPage Filter(SearchFilter input, TimeZoneInfo timeZone)
    {
        var filter = NormalizeFilter(input);
        if (filter.DirectoryPaths is not null) EnsureSearchRoots(filter.DirectoryPaths, filter.IncludeSubdirectories);
        var folders = filter.Cursor is null ? FindSearchDirectories(filter, timeZone) : null;
        if (filter.ResultKind == "directories") return new([], null, folders);
        var cursorKey = CursorKey(filter, timeZone);
        var cursor = filter.Cursor is null ? null : ReadCursor(filter.Cursor, cursorKey);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = SearchCommand(filter, timeZone, transaction, cursor);
        var rows = new List<SearchRow>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add(ReadSearchRow(reader));
        transaction.Commit();
        var page = rows.Take(filter.Limit).ToArray();
        var next = rows.Count > filter.Limit ? WriteCursor(new(page[^1].CreatedAt, page[^1].AssetId, cursorKey)) : null;
        return new(page.Select(row => DescribeSearch(row, filter)).ToArray(), next, folders);
    }

    private SqliteCommand SearchCommand(SearchFilter filter, TimeZoneInfo timeZone, SqliteTransaction transaction,
        SearchCursor? cursor = null, string? photoVersion = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var predicates = new List<string> { filter.DirectoryPaths is null ? "a.archived = 1" : "EXISTS (SELECT 1 FROM search_files sf WHERE sf.asset_id = a.id AND sf.path = l.current_path COLLATE NOCASE AND sf.present = 1)" };

        void Where(string condition, string name, object value)
        {
            predicates.Add(condition);
            command.Parameters.AddWithValue(name, value);
        }

        if (filter.MediaKind is { } kind) Where("a.media_kind = $kind", "$kind", kind);
        if (filter.Format is { } format) Where("s.format_key = $format", "$format", format);
        if (filter.Author is { } author)
            Where("s.row_id IN (SELECT row_id FROM asset_search_authors WHERE author_key = $author)", "$author", author);
        if (filter.DirectoryId is { } directoryId)
        {
            var directory = DirectoryKey(ReadDirectory(directoryId, transaction).Path);
            predicates.Add(DirectoryPredicate(directory, "$registered"));
        }
        if (filter.DirectoryPaths is { } paths)
            predicates.Add("(" + string.Join(" OR ", paths.Select((path, index) => DirectoryPredicate(path, $"$dir{index}"))) + ")");
        var dateColumn = filter.DateField == "capturedAt" ? "a.captured_at" : "a.created_at";
        if (filter.DateFrom is { } from)
            Where($"{dateColumn} >= $dateFrom", "$dateFrom", UtcBoundary(from, timeZone));
        if (filter.DateToExclusive is { } until)
            Where($"{dateColumn} < $dateTo", "$dateTo", UtcBoundary(until, timeZone));
        if (filter.CaptureMinuteFrom is not null || filter.CaptureMinuteToExclusive is not null)
        {
            connection.CreateFunction<string?, int?>("local_capture_minute", value =>
            {
                if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured)) return null;
                var local = TimeZoneInfo.ConvertTime(captured, timeZone);
                return local.Hour * 60 + local.Minute;
            }, isDeterministic: true);
            Where("local_capture_minute(a.captured_at) >= $captureFrom", "$captureFrom", filter.CaptureMinuteFrom ?? 0);
            Where("local_capture_minute(a.captured_at) < $captureUntil", "$captureUntil", filter.CaptureMinuteToExclusive ?? 1440);
        }
        if (cursor is not null)
        {
            Where("(a.created_at < $lastDate OR (a.created_at = $lastDate AND a.id > $lastId))",
                "$lastDate", cursor.CreatedAt);
            command.Parameters.AddWithValue("$lastId", cursor.AssetId);
        }

        AddTextTerms("name_key", filter.NameTerms!);
        AddTextTerms("remark_key", filter.RemarkTerms!);
        if (filter.PropertyRanges is { Length: > 0 } ranges)
        {
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                var prefix = $"$property{index}";
                command.Parameters.AddWithValue(prefix + "_field", range.Field);
                if (range.Min is { } min) Where($"(SELECT json_extract(sf.numbers, '$.' || {prefix}_field) FROM search_files sf WHERE sf.asset_id = a.id AND sf.present = 1 LIMIT 1) {(range.MinExclusive ? ">" : ">=")} {prefix}_min", prefix + "_min", min);
                if (range.Max is { } max) Where($"(SELECT json_extract(sf.numbers, '$.' || {prefix}_field) FROM search_files sf WHERE sf.asset_id = a.id AND sf.present = 1 LIMIT 1) {(range.MaxExclusive ? "<" : "<=")} {prefix}_max", prefix + "_max", max);
            }
        }
        for (var index = 0; index < filter.KeywordTerms!.Length; index++)
        {
            var parameter = $"$keyword{index}";
            if (DateKeywordRange(filter.KeywordTerms[index], timeZone) is { } range)
            {
                command.Parameters.AddWithValue(parameter + "_until", range.Until);
                Where($"((a.created_at >= {parameter}_from AND a.created_at < {parameter}_until)" +
                    $" OR (a.captured_at >= {parameter}_from AND a.captured_at < {parameter}_until))",
                    parameter + "_from", range.From);
                continue;
            }
            var directoryMatch = filter.ResultKind == "files" ? "" : $" OR instr(s.directory_key, {parameter}) > 0";
            Where($"(instr(s.name_key, {parameter}) > 0 OR instr(s.remark_key, {parameter}) > 0{directoryMatch})",
                parameter, filter.KeywordTerms[index]);
        }
        for (var group = 0; group < (filter.KeywordGroups?.Length ?? 0); group++)
        {
            var alternatives = new List<string>();
            for (var index = 0; index < filter.KeywordGroups![group].Length; index++)
            {
                var parameter = $"$related{group}_{index}";
                command.Parameters.AddWithValue(parameter, filter.KeywordGroups[group][index]);
                var directoryText = "s.directory_key";
                if (filter.DirectoryPaths is { Length: > 0 } selected)
                {
                    var cases = selected.OrderBy(path => path.Length).Select((path, rootIndex) => {
                        var rootParameter = $"$relatedRoot{group}_{index}_{rootIndex}";
                        command.Parameters.AddWithValue(rootParameter, path);
                        var offset = Math.Max(1, path.Length - Path.GetFileName(path).Length + 1);
                        return $"WHEN s.directory_key={rootParameter} OR substr(s.directory_key,1,length({rootParameter})+1)={rootParameter} || char(92) THEN substr(s.directory_key,{offset})";
                    });
                    directoryText = "(CASE " + string.Join(" ", cases) + " ELSE '' END)";
                }
                var directory = filter.IncludeDirectoryNames ? $" OR instr({directoryText}, {parameter}) > 0" : "";
                alternatives.Add($"(instr(s.name_key, {parameter}) > 0 OR instr(s.remark_key, {parameter}) > 0{directory})");
            }
            predicates.Add("(" + string.Join(" OR ", alternatives) + ")");
        }
        var joins = SearchFrom + " JOIN asset_search s ON s.row_id = a.queue_position";
        var where = string.Join(" AND ", predicates);
        const string order = "ORDER BY a.created_at DESC, a.id";
        if (photoVersion is null)
        {
            command.Parameters.AddWithValue("$limit", filter.Limit + 1);
            command.CommandText = $"SELECT {SearchColumns} {joins} WHERE {where} {order} LIMIT $limit";
        }
        else
        {
            command.Parameters.AddWithValue("$photoVersion", photoVersion);
            command.CommandText = $"""
                SELECT {SearchColumns}, e.length, e.write_ticks, e.vector {joins}
                CROSS JOIN photo_embeddings e ON e.asset_id = a.id AND e.model_version = $photoVersion AND e.fingerprint = a.fingerprint
                WHERE {where} {order}
                """;
        }
        return command;

        string DirectoryPredicate(string directory, string parameter)
        {
            command.Parameters.AddWithValue(parameter, directory);
            if (!filter.IncludeSubdirectories) return $"s.directory_key = {parameter}";
            command.Parameters.AddWithValue(parameter + "_first", directory + '\\');
            command.Parameters.AddWithValue(parameter + "_after", directory + ']');
            return $"(s.directory_key = {parameter} OR (s.directory_key >= {parameter}_first AND s.directory_key < {parameter}_after))";
        }

        void AddTextTerms(string column, string[] terms)
        {
            for (var index = 0; index < terms.Length; index++)
            {
                var term = terms[index];
                var parameter = $"${column}_{index}";
                if (fts5Trigram && term.EnumerateRunes().Count() >= 3)
                    Where($"s.row_id IN (SELECT rowid FROM asset_search_fts WHERE {column} MATCH {parameter}_fts)",
                        parameter + "_fts", "\"" + term.Replace("\"", "\"\"") + "\"");
                // Use the same literal comparison for both short terms and FTS candidates.
                Where($"instr(s.{column}, {parameter}) > 0", parameter, term);
            }
        }
    }

    internal SearchItem[] RankPhotos(JsonElement parameters, string photoVersion, float[] vector, string query)
    {
        var input = ParseFilter(parameters);
        if (input.MediaKind == "video" || input.Cursor is not null)
            throw new RequestError("INVALID_ARGUMENT", "画面检索仅支持照片，返回最相近的一页。");
        var filter = NormalizeFilter(input with { MediaKind = "photo", Limit = Math.Min(input.Limit, 30) });
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = SearchCommand(filter, TimeZoneInfo.Local, transaction, photoVersion: photoVersion);
        var best = new PriorityQueue<SearchRow, (float Score, int Order)>();
        using (var reader = command.ExecuteReader())
        {
            var order = 0;
            while (reader.Read())
            {
                var position = --order;
                var embedding = MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(15));
                if (embedding.Length != vector.Length) continue;
                var score = 0f;
                for (var index = 0; index < vector.Length; index++) score += vector[index] * embedding[index];
                if (!float.IsFinite(score)) continue;
                var priority = (Math.Clamp(score, -1, 1), position);
                if (best.Count == filter.Limit && best.TryPeek(out _, out var previous) && priority.CompareTo(previous) <= 0)
                    continue;
                var row = ReadSearchRow(reader);
                try
                {
                    var stamp = FileStamp.Read(row.Path);
                    if (stamp.Identity != row.Identity || stamp.Length != reader.GetInt64(13) || stamp.LastWriteTicks != reader.GetInt64(14))
                        continue;
                }
                catch (Exception error) when (IsFileError(error)) { /* Offline photos retain their last indexed content. */ }
                best.Enqueue(row, priority);
                if (best.Count > filter.Limit) best.Dequeue();
            }
        }
        transaction.Commit();
        var output = new List<SearchItem>(best.Count);
        while (best.TryDequeue(out var row, out var priority))
        {
            var matched = DescribeSearch(row, filter);
            output.Add(matched with { SceneScore = priority.Score,
                Matches = [.. matched.Matches, new SearchEvidence("scene", query, [])] });
        }
        output.Reverse();
        return output.ToArray();
    }

    public SearchDetails GetSearchDetails(string id)
    {
        SearchRow row;
        using (var command = Command($"SELECT {SearchColumns} {SearchFrom} WHERE a.id = $id AND EXISTS (SELECT 1 FROM asset_search s WHERE s.row_id = a.queue_position)",
            null, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new RequestError("INVALID_ARGUMENT", "已归档素材不存在。");
            row = ReadSearchRow(reader);
        }
        return new(DescribeSearch(row, null), row.Path, row.Remark, row.RemarkState, row.AuthorsState,
            row.CapturedAt is null ? null : row.MediaKind == "video" ? "视频拍摄时间元数据" : "System.Photo.DateTaken");
    }

    private static SearchRow ReadSearchRow(SqliteDataReader row) => new(row.GetString(0), row.GetString(1),
        row.GetString(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.GetString(5),
        new MediaFile(row.GetString(6), row.GetInt64(7)), JsonSerializer.Deserialize<string[]>(row.GetString(8)) ?? [],
        row.GetString(9), row.GetString(10), row.GetString(11), row.GetString(12));

    private SearchItem DescribeSearch(SearchRow row, SearchFilter? filter)
    {
        var name = Path.GetFileName(row.Path);
        var evidence = new List<SearchEvidence>();
        if (filter is not null)
        {
            if (filter.NameTerms!.Length != 0) evidence.Add(new("name", name, filter.NameTerms));
            if (filter.RemarkTerms!.Length != 0)
            {
                foreach (var quote in filter.RemarkTerms.GroupBy(term => Excerpt(row.Remark, term)))
                    evidence.Add(new("remark", quote.Key, quote.ToArray()));
            }
            foreach (var term in filter.KeywordTerms!)
            {
                if (DateKeywordRange(term, TimeZoneInfo.Local) is not null) continue;
                if (TextKey(name).Contains(term, StringComparison.Ordinal)) evidence.Add(new("name", name, [term]));
                if (TextKey(row.Remark).Contains(term, StringComparison.Ordinal))
                    evidence.Add(new("remark", Excerpt(row.Remark, term), [term]));
                if (filter.ResultKind != "files" && TextKey(Path.GetDirectoryName(row.Path)!).Contains(term, StringComparison.Ordinal))
                    evidence.Add(new("directory", Path.GetDirectoryName(row.Path)!, [term]));
            }
            foreach (var term in (filter.KeywordGroups ?? []).SelectMany(group => group).Distinct())
            {
                if (TextKey(name).Contains(term, StringComparison.Ordinal)) evidence.Add(new("name", name, [term]));
                if (TextKey(row.Remark).Contains(term, StringComparison.Ordinal)) evidence.Add(new("remark", Excerpt(row.Remark, term), [term]));
                if (filter.IncludeDirectoryNames && TextKey(SearchDirectoryLabel(row.Path, filter.DirectoryPaths)).Contains(term, StringComparison.Ordinal))
                    evidence.Add(new("directory", SearchDirectoryLabel(row.Path, filter.DirectoryPaths), [term]));
            }
            if (filter.Author is { } author)
                evidence.Add(new("author", string.Join("、", row.Authors.Where(value => TextKey(value.Trim()) == author)), []));
            if (filter.DirectoryId is not null) evidence.Add(new("directory", Path.GetDirectoryName(row.Path)!, []));
            if (filter.MediaKind is not null) evidence.Add(new("mediaKind", row.MediaKind, []));
            if (filter.Format is not null) evidence.Add(new("format", row.Format, []));
            if (filter.DateFrom is not null || filter.DateToExclusive is not null)
                evidence.Add(filter.DateField == "capturedAt"
                    ? new("capturedAt", row.CapturedAt!, []) : new("createdAt", row.CreatedAt, []));
            if ((filter.CaptureMinuteFrom is not null || filter.CaptureMinuteToExclusive is not null) &&
                !evidence.Any(match => match.Field == "capturedAt"))
                evidence.Add(new("capturedAt", row.CapturedAt!, []));
        }
        var properties = CachedSearchNumbers(row.AssetId) ?? ReadSearchProperties(row.Path, filter?.PropertyRanges?.Any(range => range.Field != "sizeBytes") == true || filter is null);
        foreach (var range in filter?.PropertyRanges ?? [])
            if (properties.TryGetValue(range.Field, out var number))
                evidence.Add(new("properties", DescribeSearchProperty(range.Field, number), []));
        return new(row.AssetId, name, row.MediaKind, row.Format, row.CreatedAt, row.CapturedAt, row.Authors,
            Excerpt(row.Remark), MatchesFile(row.Path, row.Identity), evidence.ToArray(), SourcePath: row.SourcePath,
            DirectoryPath: Path.GetDirectoryName(row.Path), Properties: properties);
    }

    private static string Excerpt(string text, string? term = null)
    {
        const int length = 160;
        if (text.Length <= length) return text;
        var offset = term is null ? -1 : text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        // Compatibility normalization can change offsets. Keep the original evidence in that case.
        if (term is not null && (offset < 0 || term.Length > length)) return text;
        var start = Math.Max(0, offset - 24);
        if (char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(text.Length, start + length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    private static string SearchDirectoryLabel(string path, string[]? roots)
    {
        var directory = Path.GetDirectoryName(path)!;
        var root = roots?.OrderBy(value => value.Length).FirstOrDefault(value => WindowsPaths.Inside(directory, value));
        if (root is null) return Path.GetFileName(directory);
        var relative = Path.GetRelativePath(root, directory);
        return Path.GetFileName(root) + (relative == "." ? "" : Path.DirectorySeparatorChar + relative);
    }

    private static SearchFilter ParseFilter(JsonElement parameters)
    {
        try { return parameters.Deserialize<SearchFilter>(FilterJson) ?? throw InvalidSearch(); }
        catch (JsonException) { throw InvalidSearch(); }
    }

    private static SearchFilter NormalizeFilter(SearchFilter input)
    {
        if (input.KeywordGroups is { } groups && (groups.Length > 6 || groups.Any(group => group is null || group.Length is < 1 or > 6)))
            throw InvalidSearch();
        if (input.PropertyRanges is { } propertyRanges && (propertyRanges.Length > 12 || propertyRanges.Any(range =>
            range is null || (range.Field != "sizeBytes" && !SearchPropertyNames.ContainsKey(range.Field)) ||
            (range.Min is null && range.Max is null) ||
            (range.Min is { } min && !double.IsFinite(min)) || (range.Max is { } max && !double.IsFinite(max)) ||
            (range.Min is not null && range.Max is not null && range.Min > range.Max)))) throw InvalidSearch();
        if (input.ResultKind is not (null or "all" or "files" or "directories")) throw InvalidSearch();
        if (input.MediaKind is not (null or "photo" or "video") || input.DateField is not (null or "createdAt" or "capturedAt")
            || input.Limit is < 1 or > 100 || input.Cursor is { Length: 0 or > 600 })
            throw InvalidSearch();
        string? directoryId = null;
        if (input.DirectoryId is not null)
        {
            if (!Guid.TryParseExact(input.DirectoryId, "D", out var id)) throw InvalidSearch();
            directoryId = id.ToString();
        }
        var format = input.Format is null ? null : BoundedKey(input.Format, 17);
        if (format?.StartsWith('.') == true) format = format[1..];
        if (format is not null && (format.Length is 0 or > 16 || format.Any(character => !char.IsAsciiLetterOrDigit(character))))
            throw InvalidSearch();
        foreach (var bound in new[] { input.DateFrom, input.DateToExclusive })
            if (bound is not null && !DateOnly.TryParseExact(bound, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) throw InvalidSearch();
        if (input.DateFrom is not null && input.DateToExclusive is not null &&
            string.CompareOrdinal(input.DateFrom, input.DateToExclusive) >= 0)
            throw new RequestError("INVALID_ARGUMENT", "截止日期必须晚于起始日期。");
        if (input.CaptureMinuteFrom is < 0 or > 1439 || input.CaptureMinuteToExclusive is < 1 or > 1440 ||
            (input.CaptureMinuteFrom ?? 0) >= (input.CaptureMinuteToExclusive ?? 1440))
            throw new RequestError("INVALID_ARGUMENT", "拍摄时段的结束时间必须晚于开始时间。");
        return input with
        {
            DirectoryId = directoryId, Format = format, DateField = input.DateField ?? "createdAt",
            DirectoryPaths = NormalizeSearchPaths(input.DirectoryPaths), KeywordTerms = TextTerms(input.KeywordTerms),
            Author = input.Author is null ? null : BoundedKey(input.Author, 120),
            NameTerms = TextTerms(input.NameTerms), RemarkTerms = TextTerms(input.RemarkTerms),
            KeywordGroups = input.KeywordGroups?.Select(TextTerms).ToArray()
        };
    }

    private static string[]? NormalizeSearchPaths(string[]? paths)
    {
        if (paths is null) return null;
        if (paths.Length is < 1 or > 50) throw InvalidSearch();
        try
        {
            if (paths.Any(path => string.IsNullOrEmpty(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)))
                throw InvalidSearch();
            return paths.Select(path => DirectoryKey(Path.GetFullPath(path))).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidSearch();
        }
    }

    private static string[] TextTerms(string[]? values)
    {
        if (values is null) return [];
        if (values.Length > 12) throw InvalidSearch();
        return values.Select(value => BoundedKey(value, 240)).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    private static string BoundedKey(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Contains('\0')) throw InvalidSearch();
        try { return TextKey(value.Trim()); }
        catch (ArgumentException) { throw InvalidSearch(); }
    }

    private static string UtcBoundary(string date, TimeZoneInfo timeZone)
    {
        var midnight = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
        while (timeZone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        var utc = timeZone.IsAmbiguousTime(midnight)
            ? new DateTimeOffset(midnight, timeZone.GetAmbiguousTimeOffsets(midnight).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(midnight, timeZone);
        return utc.ToString("O");
    }

    private static string CursorKey(SearchFilter filter, TimeZoneInfo timezone) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(timezone.Id + "|" + JsonSerializer.Serialize(filter with { Cursor = null, Limit = 50 }, FilterJson))));

    private static string WriteCursor(SearchCursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, FilterJson))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SearchCursor ReadCursor(string text, string key)
    {
        try
        {
            var base64 = text.Replace('-', '+').Replace('_', '/');
            var cursor = JsonSerializer.Deserialize<SearchCursor>(Convert.FromBase64String(base64.PadRight(
                (base64.Length + 3) / 4 * 4, '=')), FilterJson);
            if (cursor?.FilterKey == key && Guid.TryParseExact(cursor.AssetId, "D", out _) &&
                DateTime.TryParseExact(cursor.CreatedAt, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var date) && date.Kind == DateTimeKind.Utc)
                return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException) { }
        throw new RequestError("INVALID_ARGUMENT", "分页游标无效，请重新筛选。");
    }

    private static RequestError InvalidSearch() => new("INVALID_ARGUMENT", "筛选条件无效。");

    private sealed record SearchCursor(string CreatedAt, string AssetId, string FilterKey);
    private sealed record SearchRow(string AssetId, string MediaKind, string Format, string CreatedAt,
        string? CapturedAt, string Path, MediaFile Identity, string[] Authors, string Remark, string RemarkState,
        string AuthorsState, string SourcePath);
}
