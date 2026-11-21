using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace MediaLibrary.Service;

public sealed record ArchiveDirectory(string DirectoryId, string? RootId, string DisplayName,
    string RelativePath, string FullPath, bool Available);
public sealed record ArchivePlan(string PlanToken, string SourcePath, string FinalPath);
public sealed record ArchiveStatus(string OperationId, string AssetId, string Phase, string SourcePath,
    string FinalPath, string? ActualPath, string? ErrorCode, string? Message, string Remark, string Authors);
internal sealed record FileStamp(MediaFile Identity, long Length, long LastWriteTicks)
{
    public static FileStamp Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(handle);
    }

    public static FileStamp Read(SafeFileHandle handle)
    {
        var info = MediaFile.ReadInfo(handle);
        return new(info.Identity, info.Length, info.WriteTicks);
    }
}
internal sealed record ArchiveTarget(string Id, string? RootId, string Path, MediaFile Identity);
internal sealed record LocatedAsset(string Path, string Extension, string Kind, MediaFile Identity);
internal sealed record ArchiveSnapshot(string AssetId, string DirectoryId, int ArchiveRevision, string Source,
    string Target, FileStamp SourceStamp, DraftData Draft);
internal sealed record ArchiveRecord(string Id, string Token, ArchiveSnapshot Plan, string Phase = "accepted",
    string? Hash = null, string? TempPath = null, MediaFile? TempIdentity = null, FileStamp? Published = null,
    string? Actual = null, string? ErrorCode = null, string? Message = null, string Remark = "unchanged",
    string Authors = "unchanged");

public sealed partial class LibraryDatabase
{
    private readonly Dictionary<string, (ArchiveSnapshot Plan, DateTime Expires)> plans = [];

    public ArchiveDirectory AddDirectory(string path, bool root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new IOException();
            using var handle = WindowsPaths.OpenDirectory(path);
            var real = WindowsPaths.FinalPath(handle);
            var identity = MediaFile.Read(handle, directory: true);
            var candidates = AllDirectories();
            var known = candidates.FirstOrDefault(item => item.Identity == identity && WindowsPaths.SamePath(real, item.Path));
            if (known is not null && (!root || known.RootId is null))
                return DescribeDirectory(known);

            using var transaction = connection.BeginTransaction(deferred: false);
            var rootId = root ? null : candidates.Where(item => item.RootId is null && WindowsPaths.Inside(real, item.Path)
                    && DirectoryMatches(item)).MaxBy(item => item.Path.Length)?.Id;
            if (!root && rootId is null)
                throw new RequestError("INVALID_ARGUMENT", "请选择归档根目录或其中的子目录。");
            var target = new ArchiveTarget(known?.Id ?? Guid.NewGuid().ToString(), rootId, real, identity);
            using var command = Command("""
                INSERT INTO archive_directories(id, root_id, path, identity, created_ticks)
                VALUES($id, $root, $path, $identity, $ticks)
                ON CONFLICT(id) DO UPDATE SET root_id = excluded.root_id
                """, transaction, ("$id", target.Id), ("$root", rootId), ("$path", real),
                ("$identity", identity.Identity), ("$ticks", identity.CreatedTicks));
            command.ExecuteNonQuery();
            transaction.Commit();
            return DescribeDirectory(target);
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("DESTINATION_UNAVAILABLE", "无法读取所选目录。");
        }
    }

    public ArchiveDirectory[] ListDirectories() => AllDirectories().Select(DescribeDirectory).ToArray();

    private ArchiveTarget[] AllDirectories()
    {
        using var command = Command("""
            SELECT id, root_id, path, identity, created_ticks FROM archive_directories ORDER BY root_id IS NOT NULL, path
            """, null);
        using var reader = command.ExecuteReader();
        var items = new List<ArchiveTarget>();
        while (reader.Read()) items.Add(DirectoryRow(reader));
        return items.ToArray();
    }

    internal ArchiveTarget ReadDirectory(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT id, root_id, path, identity, created_ticks FROM archive_directories WHERE id = $id
            """, transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new RequestError("DESTINATION_UNAVAILABLE", "所选目录已不可用。");
        return DirectoryRow(reader);
    }

    private static ArchiveTarget DirectoryRow(SqliteDataReader reader) => new(reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
        new MediaFile(reader.GetString(3), reader.GetInt64(4)));

    private ArchiveDirectory DescribeDirectory(ArchiveTarget directory)
    {
        var root = directory.RootId is null ? directory : ReadDirectory(directory.RootId);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory.Path));
        return new(directory.Id, directory.RootId, name.Length == 0 ? directory.Path : name,
            directory.RootId is null ? "." : Path.GetRelativePath(root.Path, directory.Path), directory.Path,
            DirectoryMatches(directory) && (root == directory || DirectoryMatches(root) && WindowsPaths.Inside(directory.Path, root.Path)));
    }

    private static bool DirectoryMatches(ArchiveTarget directory)
    {
        try { using var handle = OpenTarget(directory); return true; }
        catch (Exception error) when (IsFileError(error)) { return false; }
    }

    private static SafeFileHandle OpenTarget(ArchiveTarget target)
    {
        var handle = WindowsPaths.OpenDirectory(target.Path);
        try
        {
            if (MediaFile.Read(handle, true) == target.Identity && WindowsPaths.SamePath(WindowsPaths.FinalPath(handle), target.Path))
                return handle;
            throw new IOException("Registered directory has changed.");
        }
        catch { handle.Dispose(); throw; }
    }

    internal (ArchiveTarget Target, SafeFileHandle Root, SafeFileHandle Directory) OpenArchiveDirectory(
        string id, SqliteTransaction? transaction = null)
    {
        var target = ReadDirectory(id, transaction);
        var baseDirectory = target.RootId is null ? target : ReadDirectory(target.RootId, transaction);
        try
        {
            if (!WindowsPaths.Inside(target.Path, baseDirectory.Path)) throw new IOException();
            var root = OpenTarget(baseDirectory);
            try { return (target, root, OpenTarget(target)); }
            catch { root.Dispose(); throw; }
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("DESTINATION_UNAVAILABLE", "归档目录已变更或不可用。");
        }
    }

    internal LocatedAsset Locate(string assetId, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT l.current_path, a.extension, a.media_kind, l.file_identity, l.file_created_ticks
            FROM asset_locations l JOIN assets a ON a.id = l.asset_id WHERE a.id = $id
            """, transaction, ("$id", assetId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new RequestError("INVALID_ARGUMENT", "素材不存在。");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            new(reader.GetString(3), reader.GetInt64(4)));
    }

    public string RevealPath(string assetId)
    {
        var asset = Locate(assetId);
        if (!MatchesFile(asset.Path, asset.Identity))
            throw new RequestError("SOURCE_UNAVAILABLE", "原件不可访问。最近记录的位置为 " + asset.Path);
        return asset.Path;
    }

    public ArchivePlan PreviewArchive(string assetId, int archiveRevision, string directoryId)
    {
        var draft = GetDraft(assetId);
        if (draft.ArchiveRevision != archiveRevision || draft.Data.DirectoryId != directoryId)
            throw new RequestError("REVISION_CONFLICT", "归档草稿已变化，请重新确认。");
        if (!IsValidBaseName(draft.Data.Name))
            throw new RequestError("INVALID_ARGUMENT", "请输入合法的文件名。");

        var asset = Locate(assetId);
        var name = draft.Data.Name + asset.Extension;
        if (name.Length > 255)
            throw new RequestError("INVALID_ARGUMENT", "文件名与扩展名合计不能超过 255 个字符。");
        var target = OpenArchiveDirectory(directoryId);
        using var rootHandle = target.Root;
        using var directoryHandle = target.Directory;
        var finalPath = Path.Combine(target.Target.Path, name);
        var (sourcePath, sourceStamp) = ReadSource(asset);
        CheckDestination(finalPath);
        if (RawPhoto.Supports(Path.GetExtension(finalPath))) CheckDestination(RawSidecar.PathFor(finalPath));
        CheckDestinationWritable(target.Target.Path);

        var now = DateTime.UtcNow;
        foreach (var expired in plans.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            plans.Remove(expired);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        plans[token] = (new(assetId, directoryId, archiveRevision, sourcePath, finalPath, sourceStamp, draft.Data),
            now.AddMinutes(10));
        if (plans.Count > 128) plans.Remove(plans.First().Key);
        return new(token, sourcePath, finalPath);
    }

    internal static void CheckDestination(string target)
    {
        try
        {
            if (!WindowsPaths.Exists(target)) return;
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("DESTINATION_UNAVAILABLE", "无法核对目标。");
        }
        throw new RequestError("DESTINATION_EXISTS", $"保存目录中已存在同名文件或文件夹，复制已中断：{target}。原件和已有文件均已保留。");
    }

    private static void CheckDestinationWritable(string directory)
    {
        try { using var writable = WindowsPaths.OpenDirectoryForCreate(directory); }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("DESTINATION_UNWRITABLE",
                $"无法在目标目录创建副本：{directory}。请选择应用可以写入的目录。");
        }
    }

    private static (string Path, FileStamp Stamp) ReadSource(LocatedAsset asset)
    {
        try
        {
            using var handle = File.OpenHandle(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stamp = FileStamp.Read(handle);
            if (stamp.Identity != asset.Identity) throw new IOException();
            return (WindowsPaths.FinalPath(handle), stamp);
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("SOURCE_UNAVAILABLE", "原件已变化或不可访问。");
        }
    }

    public ArchiveStatus AcceptArchive(string operationId, string token)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = ReadArchive(operationId, transaction);
        if (existing is not null)
        {
            if (existing.Token != token) throw new RequestError("INVALID_ARGUMENT", "同一操作编号只能对应一个归档计划。");
            return ToStatus(existing);
        }

        if (!plans.TryGetValue(token, out var entry) || entry.Expires < DateTime.UtcNow)
            throw new RequestError("INVALID_ARGUMENT", "归档计划已过期，请重新确认。");
        var plan = entry.Plan;
        if (ActiveArchive(plan.AssetId, transaction) is not null)
            throw new RequestError("NEEDS_REVIEW", "此素材已有归档操作。");
        var current = GetDraft(plan.AssetId, transaction);
        if (current.ArchiveRevision != plan.ArchiveRevision)
            throw new RequestError("REVISION_CONFLICT", "归档草稿已变更。");
        var file = ReadSource(Locate(plan.AssetId, transaction));
        if (file.Stamp != plan.SourceStamp || !WindowsPaths.SamePath(file.Path, plan.Source))
            throw new RequestError("SOURCE_UNAVAILABLE", "原件在确认前已变化。");
        var directory = OpenArchiveDirectory(plan.DirectoryId, transaction);
        using var root = directory.Root;
        using var target = directory.Directory;
        CheckDestination(plan.Target);
        if (RawPhoto.Supports(Path.GetExtension(plan.Target))) CheckDestination(RawSidecar.PathFor(plan.Target));

        var operation = new ArchiveRecord(operationId, token, plan);
        using var command = Command("""
            INSERT INTO file_operations(operation_id, asset_id, plan_token, phase, data)
            VALUES($id, $asset, $token, $phase, $data)
            """, transaction, ("$id", operationId), ("$asset", plan.AssetId), ("$token", token),
            ("$phase", operation.Phase), ("$data", JsonSerializer.Serialize(operation, WorkspaceJson)));
        command.ExecuteNonQuery();
        transaction.Commit();
        plans.Remove(token);
        return ToStatus(operation);
    }

    internal string? ActiveArchive(string assetId, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT operation_id FROM file_operations WHERE asset_id = $id AND phase NOT IN ('complete', 'failed')
            """, transaction, ("$id", assetId));
        return command.ExecuteScalar() as string;
    }

    internal ArchiveRecord? ReadArchive(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT data FROM file_operations WHERE operation_id = $id", transaction, ("$id", id));
        return command.ExecuteScalar() is string data ? JsonSerializer.Deserialize<ArchiveRecord>(data, WorkspaceJson) : null;
    }

    internal void SaveArchive(ArchiveRecord operation, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            UPDATE file_operations SET phase = $phase, data = $data WHERE operation_id = $id
            """, transaction, ("$id", operation.Id), ("$phase", operation.Phase),
            ("$data", JsonSerializer.Serialize(operation, WorkspaceJson)));
        command.ExecuteNonQuery();
    }

    internal string[] PendingArchives()
    {
        using var command = Command("""
            SELECT operation_id FROM file_operations WHERE phase NOT IN ('complete', 'failed', 'needs_review') ORDER BY rowid
            """, null);
        using var reader = command.ExecuteReader();
        var output = new List<string>();
        while (reader.Read()) output.Add(reader.GetString(0));
        return output.ToArray();
    }

    private ArchiveStatus[] ListArchives(bool completed, SqliteTransaction transaction)
    {
        var sql = completed
            ? "SELECT data FROM file_operations WHERE phase IN ('complete', 'failed') ORDER BY rowid DESC LIMIT 12"
            : "SELECT data FROM file_operations WHERE phase NOT IN ('complete', 'failed') ORDER BY rowid DESC";
        using var command = Command(sql, transaction);
        using var reader = command.ExecuteReader();
        var output = new List<ArchiveStatus>();
        while (reader.Read()) output.Add(ToStatus(JsonSerializer.Deserialize<ArchiveRecord>(reader.GetString(0), WorkspaceJson)!));
        return output.ToArray();
    }

    public ArchiveStatus GetArchive(string id) => ToStatus(ReadArchive(id)
        ?? throw new RequestError("INVALID_ARGUMENT", "归档操作不存在。"));

    public ArchiveStatus PreparePropertyRetry(string id)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var operation = ReadArchive(id, transaction)
            ?? throw new RequestError("INVALID_ARGUMENT", "归档操作不存在。");
        if (operation.Phase == "properties_pending" && operation.ErrorCode == "PROPERTY_WRITE_FAILED")
        {
            operation = operation with { ErrorCode = null, Message = null };
            SaveArchive(operation, transaction);
        }
        transaction.Commit();
        return ToStatus(operation);
    }

    internal static ArchiveStatus ToStatus(ArchiveRecord op) => new(op.Id, op.Plan.AssetId, op.Phase, op.Plan.Source,
        op.Plan.Target, op.Actual, op.ErrorCode, op.Message, op.Remark, op.Authors);

    internal void RegisterArchive(ArchiveRecord operation)
    {
        var identity = operation.Published!.Identity;
        var original = WindowsProperties.Read(operation.Actual!);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command("""
            UPDATE asset_locations SET current_path = $path, file_identity = $identity, file_created_ticks = $ticks
            WHERE asset_id = $asset;
            UPDATE assets SET archived = 1, fingerprint = $hash, captured_at = $captured WHERE id = $asset;
            UPDATE workspace_state SET current_asset_id = NULL WHERE current_asset_id = $asset;
            """, transaction, ("$path", operation.Actual), ("$identity", identity.Identity),
            ("$ticks", identity.CreatedTicks), ("$asset", operation.Plan.AssetId), ("$hash", operation.Hash),
            ("$captured", original.CapturedAt?.ToString("O")));
        command.ExecuteNonQuery();
        SavePropertyValues(operation, transaction, original);
        IndexAsset(operation.Plan.AssetId, transaction);
        SaveArchive(operation, transaction);
        transaction.Commit();
    }

    internal void UpdateArchiveProperties(ArchiveRecord operation, string? fingerprint)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var identity = operation.Published!.Identity;
        using (var location = Command("""
            UPDATE asset_locations SET file_identity = $identity, file_created_ticks = $ticks WHERE asset_id = $asset
            """, transaction, ("$identity", identity.Identity), ("$ticks", identity.CreatedTicks), ("$asset", operation.Plan.AssetId)))
            location.ExecuteNonQuery();
        if (fingerprint is not null)
        {
            using var command = Command("UPDATE assets SET fingerprint = $hash WHERE id = $asset", transaction,
                ("$hash", fingerprint), ("$asset", operation.Plan.AssetId));
            command.ExecuteNonQuery();
        }
        SavePropertyValues(operation, transaction);
        IndexAsset(operation.Plan.AssetId, transaction);
        SaveArchive(operation, transaction);
        transaction.Commit();
    }

    private void SavePropertyValues(ArchiveRecord operation, SqliteTransaction transaction, MediaProperties? original = null)
    {
        var draft = operation.Plan.Draft;
        using var command = Command("""
            INSERT INTO asset_details(asset_id, remark, authors, remark_state, authors_state)
            VALUES($asset, $remark, $authors, $remark_state, $author_state)
            ON CONFLICT(asset_id) DO UPDATE SET
                remark = CASE WHEN $remark_state = 'unchanged' AND $original = 0 THEN remark ELSE excluded.remark END,
                authors = CASE WHEN $author_state = 'unchanged' AND $original = 0 THEN authors ELSE excluded.authors END,
                remark_state = excluded.remark_state, authors_state = excluded.authors_state
            """, transaction, ("$asset", operation.Plan.AssetId),
            ("$remark", operation.Remark == "unchanged" && original is not null ? original.Remark : draft.Remark.Value),
            ("$authors", JsonSerializer.Serialize(
                operation.Authors == "unchanged" && original is not null ? original.Authors : draft.Authors.Value, WorkspaceJson)),
            ("$original", original is null ? 0 : 1),
            ("$remark_state", operation.Remark), ("$author_state", operation.Authors));
        command.ExecuteNonQuery();
    }
}
