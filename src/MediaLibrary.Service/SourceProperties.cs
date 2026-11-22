using System.ComponentModel;
using Microsoft.Data.Sqlite;

namespace MediaLibrary.Service;

internal sealed record SavedSourceProperties(AssetDraft Draft, string Remark, string Authors,
    string? RemarkFailure = null, string? AuthorsFailure = null, string? RenamedPath = null);

public sealed partial class LibraryDatabase
{
    internal SavedSourceProperties SaveSourceProperties(string assetId, int expectedRevision, WindowsProperties properties)
    {
        RecoverSourceRenames();
        using var prepare = connection.BeginTransaction(deferred: false);
        var draft = GetDraft(assetId, prepare);
        if (draft.Revision != expectedRevision)
            throw new RequestError("REVISION_CONFLICT", "草稿有更新，请重试保存。");
        using var archiveState = Command("SELECT archived FROM assets WHERE id = $asset", prepare, ("$asset", assetId));
        if (ActiveArchive(assetId, prepare) is not null || Convert.ToInt64(archiveState.ExecuteScalar()) != 0)
            throw new RequestError("NEEDS_REVIEW", "此素材已归档或有已确认的归档操作。");

        var (path, stamp) = ReadSource(Locate(assetId, prepare));
        string? renamedPath = null;
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), draft.Data.Name, StringComparison.Ordinal))
        {
            var name = draft.Data.Name + Path.GetExtension(path);
            if (!IsValidBaseName(draft.Data.Name) || name.Length > 255)
                throw new RequestError("INVALID_ARGUMENT", "请输入合法的 Windows 文件名。");
            renamedPath = Path.Combine(Path.GetDirectoryName(path)!, name);
            RawSidecar.CheckRename(path, renamedPath);
            if (!WindowsPaths.SamePath(path, renamedPath)) CheckDestination(renamedPath);
            using var pending = Command("UPDATE asset_locations SET pending_source_path = $path WHERE asset_id = $asset",
                prepare, ("$asset", assetId), ("$path", renamedPath));
            pending.ExecuteNonQuery();
        }
        prepare.Commit();

        if (renamedPath is not null)
        {
            using var rename = connection.BeginTransaction(deferred: false);
            try
            {
                using var source = WindowsPaths.OpenSource(path);
                if (FileStamp.Read(source) != stamp || !WindowsPaths.SamePath(WindowsPaths.FinalPath(source), path))
                    throw new RequestError("SOURCE_UNAVAILABLE", "原件已变化，无法重命名。");
                using var directory = WindowsPaths.OpenDirectory(Path.GetDirectoryName(path)!);
                WindowsPaths.Rename(source, directory, renamedPath);
            }
            catch (Exception error) when (error is RequestError || IsFileError(error))
            {
                using var clear = Command("UPDATE asset_locations SET pending_source_path = NULL WHERE asset_id = $asset",
                    rename, ("$asset", assetId));
                clear.ExecuteNonQuery();
                rename.Commit();
                if (error is RequestError) throw;
                var code = error.InnerException is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
                throw new RequestError("SOURCE_RENAME_FAILED", code switch
                {
                    5 => "无法重命名原件，Windows 拒绝访问文件或所在目录。",
                    32 or 33 => "原件正被占用，无法重命名。",
                    80 or 183 => "原件所在目录已有同名文件或文件夹。",
                    _ => $"无法重命名原件（Windows 错误 {code}）。"
                });
            }
            RawSidecar.Move(path, renamedPath);
            RegisterSourceRename(assetId, renamedPath, rename);
            rename.Commit();
            path = renamedPath;
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        if (renamedPath is not null) draft = GetDraft(assetId, transaction);
        var identity = stamp.Identity;
        var data = draft.Data;
        string? failure = null;
        var remark = data.Remark.Intent == "unchanged" &&
            (RawPhoto.Supports(Path.GetExtension(path)) ? RawSidecar.Read(path) == data.Remark.Value :
                data.Remark.Value.Length == 0 || WindowsProperties.MatchesExplorerRemark(path, data.Remark.Value))
            ? "unchanged" : WriteRemark();

        using (var update = Command("""
            UPDATE asset_locations SET file_identity = $identity, file_created_ticks = $ticks WHERE asset_id = $asset;
            UPDATE asset_details SET
                remark = CASE WHEN $remark_state = 'written' THEN $remark ELSE remark END,
                remark_state = CASE WHEN $remark_state = 'unchanged' THEN remark_state ELSE $remark_state END
            WHERE asset_id = $asset;
            """, transaction, ("$identity", identity.Identity), ("$ticks", identity.CreatedTicks), ("$asset", assetId),
            ("$remark_state", remark), ("$remark", data.Remark.Value)))
            update.ExecuteNonQuery();

        if (remark == "written" && data.Remark.Intent != "unchanged")
        {
            draft = draft with
            {
                Revision = draft.Revision + 1,
                ArchiveRevision = draft.ArchiveRevision + 1,
                Data = data with { Remark = data.Remark with { Intent = "unchanged" } }
            };
            UpdateDraft(draft, transaction);
        }
        // Publish the saved location/identity together with its text index. Directory
        // search requires a present snapshot at current_path, so waiting for a watcher
        // here temporarily hides renamed or property-handler-replaced files.
        using (var snapshot = Command("""
            DELETE FROM search_files WHERE path=$path COLLATE NOCASE AND asset_id<>$asset
                AND EXISTS (SELECT 1 FROM search_files WHERE asset_id=$asset);
            UPDATE search_files SET path=$path, identity=$identity, created=$created, present=1, modified=-1
                WHERE asset_id=$asset;
            """, transaction, ("$asset", assetId), ("$path", path),
            ("$identity", identity.Identity), ("$created", identity.CreatedTicks)))
            snapshot.ExecuteNonQuery();
        IndexAsset(assetId, transaction);
        transaction.Commit();
        return new(draft, remark, "unchanged", failure, RenamedPath: renamedPath);

        string WriteRemark()
        {
            try
            {
                using var guard = WindowsPaths.ProtectProperties(path);
                if (MediaFile.Read(guard) != identity || !WindowsPaths.SamePath(WindowsPaths.FinalPath(guard), path))
                {
                    failure = "原件已变化";
                    return "write_failed";
                }
                var state = properties.WriteExplorerRemark(path, data.Remark.Value);
                if (state == "write_failed") failure = properties.LastWriteFailure;
                using var current = WindowsPaths.OpenProtectedRead(path);
                var writtenIdentity = MediaFile.Read(current);
                if (!WindowsPaths.SamePath(WindowsPaths.FinalPath(current), path) ||
                    writtenIdentity != identity && (state != "written" || !WindowsProperties.MatchesExplorerRemark(path, data.Remark.Value)))
                {
                    failure ??= "无法确认写回后的原件";
                    return "write_failed";
                }
                identity = writtenIdentity;
                return state;
            }
            catch (Exception error) when (IsFileError(error))
            {
                failure ??= WindowsProperties.DescribeWriteFailure("访问原件", error.HResult);
                return "write_failed";
            }
        }
    }

    // Reconcile a rename that reached disk before the database transaction committed.
    private void RecoverSourceRenames()
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var pending = new List<(string AssetId, string Source, string Path, MediaFile Identity)>();
        using (var command = Command("""
            SELECT asset_id, current_path, pending_source_path, file_identity, file_created_ticks
            FROM asset_locations WHERE pending_source_path IS NOT NULL
            """, transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    new(reader.GetString(3), reader.GetInt64(4))));

        foreach (var (assetId, source, path, identity) in pending)
        {
            try
            {
                if (!WindowsPaths.SamePath(source, path) && MatchesFile(source, identity)) continue;
                using var file = WindowsPaths.OpenProtectedRead(path);
                var actual = WindowsPaths.FinalPath(file);
                if (MediaFile.Read(file) == identity && WindowsPaths.SamePath(actual, path) &&
                    string.Equals(Path.GetFileName(actual), Path.GetFileName(path), StringComparison.Ordinal)) {
                    RawSidecar.Move(source, path);
                    RegisterSourceRename(assetId, path, transaction);
                }
            }
            catch (Exception error) when (IsFileError(error)) { }
        }
        transaction.Commit();
    }

    private void RegisterSourceRename(string assetId, string destination, SqliteTransaction transaction)
    {
        using var update = Command("""
            UPDATE asset_locations SET source_path = $path, current_path = $path, pending_source_path = NULL WHERE asset_id = $asset;
            UPDATE assets SET original_name = $name WHERE id = $asset;
            UPDATE drafts SET revision = revision + 1, archive_revision = archive_revision + 1 WHERE asset_id = $asset;
            """, transaction, ("$asset", assetId), ("$path", destination), ("$name", Path.GetFileName(destination)));
        update.ExecuteNonQuery();
    }
}
