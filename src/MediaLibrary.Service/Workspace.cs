using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaLibrary.Service;

public sealed record AssetSummary(string AssetId, string OriginalName, string Extension, string MediaKind,
    string CreatedAt, bool Available, long? SizeBytes = null, string? ModifiedAt = null, string? SourcePath = null);
public sealed record ImportItem(string Name, string Status, string? AssetId, string? Reason);
public sealed record FieldChange<T>(string Intent, T Value);
public sealed record DraftData(string Name, bool Finalized, FieldChange<string> Remark, FieldChange<string[]> Authors,
    string Keywords, long PreviewPositionMs, string? DirectoryId = null, DraftSuggestions? Suggestions = null);
public sealed record AssetDraft(string AssetId, int Revision, int ArchiveRevision, DraftData Data);
public sealed record WorkspaceState(string[] QueueIds, string? CurrentAssetId, AssetDraft? Draft,
    AssetSummary[] Items, ArchiveStatus[] IncompleteOperations, ArchiveStatus[] RecentArchives, string[] ImportFolders);
public sealed record FolderEntry(string Name, string Path, bool Directory, string? ModifiedAt = null, long? SizeBytes = null);
public sealed record ImportFolderDetails(string ModifiedAt);

public sealed class RequestError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class LibraryDatabase
{
    private static readonly JsonSerializerOptions WorkspaceJson = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi" };

    public ImportItem[] Import(string[] paths) => paths.Select(path => ImportOne(path)).ToArray();

    private ImportItem ImportOne(string path, bool queue = true)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (!Path.IsPathFullyQualified(path) || name.Length == 0)
                return new(name, "unavailable", null, "请选择可访问的本地文件。");

            path = Path.GetFullPath(path);
            var extension = Path.GetExtension(name);
            var kind = PhotoExtensions.Contains(extension) || RawPhoto.Supports(extension) ? "photo" :
                VideoExtensions.Contains(extension) ? "video" : null;
            if (kind is null)
                return new(name, "unsupported", null, "暂不支持此文件格式。");

            var file = MediaFile.Read(path);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var find = Command("""
                SELECT asset_id FROM asset_locations
                WHERE file_identity = $identity AND file_created_ticks = $created
                UNION ALL
                SELECT f.asset_id FROM file_operations f JOIN assets a ON a.id = f.asset_id
                WHERE $queue = 1 AND a.archived = 1 AND json_extract(f.data, '$.plan.sourceStamp.identity.identity') = $identity
                    AND json_extract(f.data, '$.plan.sourceStamp.identity.createdTicks') = $created
                LIMIT 1
                """, transaction, ("$identity", file.Identity), ("$created", file.CreatedTicks), ("$queue", queue ? 1 : 0));
            if (find.ExecuteScalar() is string existing)
            {
                if (!queue) return new(name, "duplicate", existing, null);
                using var requeue = Command("""
                    UPDATE assets SET queued = 1, original_name = $name, extension = $extension, media_kind = $kind
                    WHERE id = $id AND archived = 0 AND queued = 0;
                    """, transaction, ("$id", existing), ("$name", name), ("$extension", extension), ("$kind", kind));
                if (requeue.ExecuteNonQuery() == 0) return new(name, "duplicate", existing, "已登记此文件。");
                using var location = Command("""
                    UPDATE asset_locations SET source_path = $path, current_path = $path WHERE asset_id = $id;
                    """, transaction, ("$path", path), ("$id", existing));
                location.ExecuteNonQuery();
                transaction.Commit();
                return new(name, "imported", existing, null);
            }

            var id = Guid.NewGuid().ToString();
            var createdAt = new DateTime(file.CreatedTicks, DateTimeKind.Utc).ToString("O");
            var existingProperties = WindowsProperties.Read(path);
            var initial = new DraftData(Path.GetFileNameWithoutExtension(name), false,
                new("unchanged", existingProperties.Remark), new("unchanged", existingProperties.Authors), "", 0);
            using var insert = Command("""
                INSERT INTO assets (id, original_name, extension, media_kind, created_at, queued)
                VALUES ($id, $name, $extension, $kind, $date, $queue);
                INSERT INTO asset_locations (asset_id, source_path, current_path, file_identity, file_created_ticks)
                VALUES ($id, $path, $path, $identity, $created);
                INSERT INTO drafts (asset_id, data) VALUES ($id, $data);
                INSERT INTO asset_details (asset_id, remark, authors, remark_state, authors_state)
                VALUES ($id, $remark, $authors, 'unchanged', 'unchanged');
                """, transaction, ("$id", id), ("$name", name), ("$extension", extension), ("$kind", kind),
                ("$date", createdAt), ("$path", path), ("$identity", file.Identity), ("$created", file.CreatedTicks),
                ("$data", JsonSerializer.Serialize(initial, WorkspaceJson)), ("$remark", existingProperties.Remark),
                ("$authors", JsonSerializer.Serialize(existingProperties.Authors, WorkspaceJson)), ("$queue", queue ? 1 : 0));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(name, "imported", id, null);
        }
        catch (Exception error) when (IsFileError(error))
        {
            return new(name, "unavailable", null, "文件不可访问。云端占位文件需先下载。");
        }
    }

    public WorkspaceState LoadWorkspace()
    {
        RecoverSourceRenames();
        using var transaction = connection.BeginTransaction(deferred: true);
        var items = new List<AssetSummary>();
        using (var command = Command("""
            SELECT a.id, a.original_name, a.extension, a.media_kind, a.created_at,
                l.source_path, l.file_identity, l.file_created_ticks
            FROM assets a JOIN asset_locations l ON l.asset_id = a.id
            WHERE a.archived = 0 AND a.queued = 1 ORDER BY a.queue_position
            """, transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var identity = new MediaFile(reader.GetString(6), reader.GetInt64(7));
                var file = ReadMatchingFile(reader.GetString(5), identity);
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), file.HasValue, file?.Length,
                    file is { } info ? new DateTime(info.WriteTicks, DateTimeKind.Utc).ToString("O") : null, reader.GetString(5)));
            }
        }
        var current = Scalar("SELECT current_asset_id FROM workspace_state WHERE id = 1", transaction) as string;
        if (current is not null && !items.Exists(item => item.AssetId == current))
            current = null;
        var state = new WorkspaceState(items.Select(item => item.AssetId).ToArray(), current,
            current is null ? null : GetDraft(current, transaction), items.ToArray(),
            ListArchives(completed: false, transaction), ListArchives(completed: true, transaction), ImportFolders(transaction));
        transaction.Commit();
        return state;
    }

    public int RemoveFromQueue(string[] assetIds)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var count = RemoveFromQueue(assetIds, transaction);
        transaction.Commit();
        return count;
    }

    private int RemoveFromQueue(string[] assetIds, SqliteTransaction transaction)
    {
        var ids = JsonSerializer.Serialize(assetIds, WorkspaceJson);
        using var active = Command("""
            SELECT 1 FROM file_operations f JOIN assets a ON a.id = f.asset_id
            WHERE a.archived = 0 AND a.queued = 1 AND f.phase NOT IN ('complete', 'failed')
                AND f.asset_id IN (SELECT value FROM json_each($ids)) LIMIT 1
            """, transaction, ("$ids", ids));
        if (active.ExecuteScalar() is not null)
            throw new RequestError("NEEDS_REVIEW", "选中的素材有未完成的归档操作，请处理归档记录后再移除。");
        using var remove = Command("""
            UPDATE assets SET queued = 0 WHERE archived = 0 AND queued = 1
                AND id IN (SELECT value FROM json_each($ids));
            """, transaction, ("$ids", ids));
        var count = remove.ExecuteNonQuery();
        using var clear = Command("""
            UPDATE workspace_state SET current_asset_id = NULL WHERE id = 1
                AND current_asset_id IN (SELECT id FROM assets WHERE archived = 0 AND queued = 0);
            """, transaction);
        clear.ExecuteNonQuery();
        return count;
    }

    public int RemoveImportFolder(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)) throw FolderUnavailable();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        using var transaction = connection.BeginTransaction(deferred: false);
        var folders = ImportFolders(transaction);
        if (!folders.Contains(root, StringComparer.OrdinalIgnoreCase) || !WindowsPaths.Inside(path, root))
            throw FolderUnavailable();

        var ids = new List<string>();
        using (var command = Command("""
            SELECT a.id, l.source_path FROM assets a JOIN asset_locations l ON l.asset_id = a.id
            WHERE a.archived = 0 AND a.queued = 1
            """, transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                if (WindowsPaths.Inside(reader.GetString(1), path)) ids.Add(reader.GetString(0));
        }
        var count = RemoveFromQueue(ids.ToArray(), transaction);
        foreach (var folder in folders.Where(folder => WindowsPaths.Inside(folder, path)))
        {
            using var remove = Command("DELETE FROM workspace_import_folders WHERE path = $path", transaction, ("$path", folder));
            remove.ExecuteNonQuery();
        }
        using var hide = Command("INSERT OR IGNORE INTO workspace_removed_folders(path) VALUES ($path)", transaction, ("$path", path));
        hide.ExecuteNonQuery();
        transaction.Commit();
        return count;
    }

    public void AddImportFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw FolderUnavailable();
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            using var handle = WindowsPaths.OpenDirectory(path);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var statement = Command("INSERT OR IGNORE INTO workspace_import_folders(path) VALUES ($path)",
                transaction, ("$path", path));
            statement.ExecuteNonQuery();
            foreach (var folder in FolderPaths("SELECT path FROM workspace_removed_folders", transaction)
                .Where(folder => WindowsPaths.Inside(folder, path) || WindowsPaths.Inside(path, folder)))
            {
                using var restore = Command("DELETE FROM workspace_removed_folders WHERE path = $path", transaction, ("$path", folder));
                restore.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception error) when (IsFileError(error)) { throw FolderUnavailable(); }
    }

    private string[] ImportFolders(SqliteTransaction? transaction = null)
        => FolderPaths("SELECT path FROM workspace_import_folders ORDER BY rowid", transaction);

    private string[] FolderPaths(string sql, SqliteTransaction? transaction = null)
    {
        using var command = Command(sql, transaction);
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths.ToArray();
    }

    public FolderEntry[] BrowseImportFolder(string root, string path)
        => ReadImportFolder(root, path, (physical, logical) =>
        {
            var removed = FolderPaths("SELECT path FROM workspace_removed_folders").ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new DirectoryInfo(physical).EnumerateFileSystemInfos()
                .Where(item => !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(item => new FolderEntry(item.Name, Path.Combine(logical, item.Name),
                    item.Attributes.HasFlag(FileAttributes.Directory), item.LastWriteTimeUtc.ToString("O"),
                    item is FileInfo file ? file.Length : null))
                .Where(item => !item.Directory || !removed.Contains(item.Path))
                .OrderByDescending(item => item.Directory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        });

    public ImportFolderDetails DescribeImportFolder(string root, string path)
        => ReadImportFolder(root, path, (physical, _) => new ImportFolderDetails(
            new DirectoryInfo(physical).LastWriteTimeUtc.ToString("O")));

    private T ReadImportFolder<T>(string root, string path, Func<string, string, T> read)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path) ||
            !ImportFolders().Contains(root, StringComparer.OrdinalIgnoreCase)) throw FolderUnavailable();
        try
        {
            path = Path.GetFullPath(path);
            if (!WindowsPaths.Inside(path, root)) throw FolderUnavailable();
            using var rootHandle = WindowsPaths.OpenDirectory(root);
            using var childHandle = WindowsPaths.OpenDirectory(path);
            var physicalRoot = WindowsPaths.FinalPath(rootHandle);
            var physicalChild = WindowsPaths.FinalPath(childHandle);
            if (!WindowsPaths.Inside(physicalChild, physicalRoot)) throw FolderUnavailable();
            return read(physicalChild, path);
        }
        catch (Exception error) when (IsFileError(error)) { throw FolderUnavailable(); }
    }

    private static RequestError FolderUnavailable() => new("SOURCE_UNAVAILABLE", "无法读取导入的原始文件夹。");

    public AssetDraft? SetCurrent(string? assetId)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = assetId is null ? null : GetDraft(assetId, transaction);
        using var command = Command("""
            UPDATE workspace_state
            SET current_asset_id = $id, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = 1
            """, transaction, ("$id", assetId));
        command.ExecuteNonQuery();
        transaction.Commit();
        return draft;
    }

    public AssetDraft GetDraft(string assetId, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT d.revision, d.archive_revision, d.data FROM drafts d JOIN assets a ON a.id = d.asset_id
            WHERE d.asset_id = $id AND a.archived = 0 AND a.queued = 1
            """, transaction, ("$id", assetId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new RequestError("INVALID_ARGUMENT", "待整理素材不存在。");
        return new(assetId, reader.GetInt32(0), reader.GetInt32(1),
            JsonSerializer.Deserialize<DraftData>(reader.GetString(2), WorkspaceJson)!);
    }

    public AssetDraft SaveDraft(string assetId, int expectedRevision, JsonElement changes)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = GetDraft(assetId, transaction);
        if (previous.Revision != expectedRevision)
            throw new RequestError("REVISION_CONFLICT", "草稿有更新，请重试保存。");
        if (ActiveArchive(assetId, transaction) is not null)
            throw new RequestError("NEEDS_REVIEW", "此素材有已确认的归档操作。");

        var next = ApplyChanges(previous.Data, changes);
        next = next with { Suggestions = KeepSuggestions(previous.Data, next) };
        if (next.DirectoryId != previous.Data.DirectoryId && next.DirectoryId is not null)
            _ = ReadDirectory(next.DirectoryId, transaction);
        var archiveChanged = previous.Data.Name != next.Name ||
            previous.Data.Finalized != next.Finalized || previous.Data.Remark != next.Remark ||
            previous.Data.Authors.Intent != next.Authors.Intent ||
            !previous.Data.Authors.Value.SequenceEqual(next.Authors.Value) ||
            previous.Data.DirectoryId != next.DirectoryId;
        var result = new AssetDraft(assetId, previous.Revision + 1,
            previous.ArchiveRevision + (archiveChanged ? 1 : 0), next);

        UpdateDraft(result, transaction);
        transaction.Commit();
        return result;
    }

    private void UpdateDraft(AssetDraft draft, SqliteTransaction transaction)
    {
        using var command = Command("""
            UPDATE drafts SET revision = $revision, archive_revision = $archive, data = $data WHERE asset_id = $id
            """, transaction, ("$id", draft.AssetId), ("$revision", draft.Revision), ("$archive", draft.ArchiveRevision),
            ("$data", JsonSerializer.Serialize(draft.Data, WorkspaceJson)));
        command.ExecuteNonQuery();
    }

    private static DraftData ApplyChanges(DraftData data, JsonElement changes)
    {
        var next = data;
        var explicitlyFinalized = false;
        foreach (var property in changes.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name":
                    next = next with { Name = BoundedString(property.Value, 255) };
                    break;
                case "finalized":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw InvalidDraft();
                    next = next with { Finalized = property.Value.GetBoolean() };
                    explicitlyFinalized = true;
                    break;
                case "remark":
                    var remark = FieldValue(property.Value, out var remarkIntent);
                    var comment = BoundedString(remark, 4000);
                    ValidateFieldIntent(remarkIntent, comment.Length > 0);
                    next = next with { Remark = new(remarkIntent, comment) };
                    break;
                case "authors":
                    var authors = FieldValue(property.Value, out var authorIntent);
                    if (authors.ValueKind != JsonValueKind.Array || authors.GetArrayLength() > 50)
                        throw InvalidDraft();
                    var names = authors.EnumerateArray().Select(value => BoundedString(value, 120)).ToArray();
                    if (names.Any(string.IsNullOrWhiteSpace))
                        throw InvalidDraft();
                    ValidateFieldIntent(authorIntent, names.Length > 0);
                    next = next with { Authors = new(authorIntent, names) };
                    break;
                case "keywords":
                    next = next with { Keywords = BoundedString(property.Value, 1000) };
                    break;
                case "previewPositionMs":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt64(out var position) || position < 0 || position > 9007199254740991L)
                        throw InvalidDraft();
                    next = next with { PreviewPositionMs = position };
                    break;
                case "directoryId":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                        next = next with { DirectoryId = null };
                    else if (property.Value.ValueKind == JsonValueKind.String &&
                        Guid.TryParseExact(property.Value.GetString(), "D", out var directory))
                        next = next with { DirectoryId = directory.ToString() };
                    else
                        throw InvalidDraft();
                    break;
                default:
                    throw InvalidDraft();
            }
        }
        if (!explicitlyFinalized && next.Name != data.Name)
            next = next with { Finalized = false };
        if (next.Finalized && !IsValidBaseName(next.Name))
            throw new RequestError("INVALID_ARGUMENT", "请输入合法的 Windows 文件名。");
        return next;
    }

    private static JsonElement FieldValue(JsonElement field, out string intent)
    {
        if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("intent", out var kind) ||
            kind.ValueKind != JsonValueKind.String || !field.TryGetProperty("value", out var value))
            throw InvalidDraft();
        intent = kind.GetString()!;
        return value;
    }

    private static void ValidateFieldIntent(string intent, bool hasValue)
    {
        if (intent is not ("unchanged" or "set" or "clear") ||
            (intent == "clear" && hasValue) || (intent == "set" && !hasValue))
            throw InvalidDraft();
    }

    private static string BoundedString(JsonElement value, int length) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= length
            ? text : throw InvalidDraft();

    private static bool IsValidBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)))
            return false;

        var prefix = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return prefix is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(prefix.Length == 4 && (prefix.StartsWith("COM", StringComparison.Ordinal) ||
                prefix.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(prefix[3]));
    }

    private static RequestError InvalidDraft() => new("INVALID_ARGUMENT", "草稿字段格式无效。");

    private static bool MatchesFile(string path, MediaFile identity) => ReadMatchingFile(path, identity).HasValue;

    private static (long Length, long WriteTicks)? ReadMatchingFile(string path, MediaFile identity)
    {
        try
        {
            var file = MediaFile.ReadInfo(path);
            return file.Identity == identity ? (file.Length, file.WriteTicks) : null;
        }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
