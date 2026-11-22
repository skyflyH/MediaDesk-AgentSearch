using System.Text.Json;

namespace MediaLibrary.Service;

public sealed partial class LibraryDatabase
{
    internal sealed record SearchRoot(string Path, bool Recursive, bool Ready, string? JournalId, long? NextUsn);
    internal SearchRoot[] SearchRoots()
    {
        using var command = Command("SELECT path,recursive,ready,journal_id,next_usn FROM search_roots", null);
        using var rows = command.ExecuteReader();
        var roots = new List<SearchRoot>();
        while (rows.Read()) roots.Add(new(rows.GetString(0), rows.GetBoolean(1), rows.GetBoolean(2),
            rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetInt64(4)));
        return roots.ToArray();
    }

    internal bool RegisterSearchRoots(string[] paths, bool recursive)
    {
        var changed = false;
        foreach (var path in paths)
        {
            using (var existing = Command("SELECT recursive FROM search_roots WHERE path=$path", null, ("$path", path)))
                if (existing.ExecuteScalar() is long mode && mode >= (recursive ? 1 : 0)) continue;
            using var command = Command("""
                INSERT INTO search_roots(path,recursive) VALUES($path,$recursive)
                ON CONFLICT(path) DO UPDATE SET recursive = excluded.recursive,
                    ready = 0, journal_id = NULL, next_usn = NULL
                WHERE excluded.recursive > search_roots.recursive
                """, null, ("$path", path), ("$recursive", recursive ? 1 : 0));
            changed |= command.ExecuteNonQuery() != 0;
        }
        return changed;
    }

    internal static (string[] Paths, bool Recursive) SearchIndexRequest(JsonElement input)
    {
        var filter = NormalizeFilter(ParseFilter(input));
        return (filter.DirectoryPaths ?? throw new RequestError("INVALID_ARGUMENT", "请添加查找目录。"), filter.IncludeSubdirectories);
    }

    internal void SetSearchRoots(string[] paths)
    {
        var normalized = paths.Length == 0 ? [] : NormalizeSearchPaths(paths)!;
        foreach (var root in SearchRoots())
        {
            if (normalized.Contains(root.Path, StringComparer.OrdinalIgnoreCase)) continue;
            using var remove = Command("DELETE FROM search_roots WHERE path=$path", null, ("$path", root.Path));
            remove.ExecuteNonQuery();
        }
        RegisterSearchRoots(normalized, recursive: true);
    }

    private void EnsureSearchRoots(string[] paths, bool recursive)
    {
        // Direct callers get a cold initial index. Registered roots are owned by the background worker.
        foreach (var path in paths)
        {
            using var exists = Command("SELECT COUNT(*) FROM search_roots WHERE path=$path COLLATE NOCASE AND recursive >= $recursive",
                null, ("$path", path), ("$recursive", recursive ? 1 : 0));
            var registered = Convert.ToInt64(exists.ExecuteScalar()) != 0;
            exists.Dispose();
            if (!registered) IndexSearchDirectories([path], recursive);
        }
    }

    private void IndexSearchDirectories(string[] paths, bool recursive, Action<DirectoryIndexProgress>? progress = null)
    {
        RegisterSearchRoots(paths, recursive);
        var processed = 0;
        foreach (var path in paths.OrderBy(path => path.Length))
        {
            if (recursive && paths.Any(parent => parent.Length < path.Length && WindowsPaths.Inside(path, parent))) continue;
            ScanSearchTree(path, recursive, progress, ref processed);
            using var ready = Command("UPDATE search_roots SET ready=1 WHERE path=$path COLLATE NOCASE", null, ("$path", path));
            ready.ExecuteNonQuery();
        }
        progress?.Invoke(new("complete", processed, processed, processed));
    }

    internal int SearchFilesRefreshed { get; private set; }
    internal int SearchDirectoriesScanned { get; private set; }

    internal void ScanSearchTree(string root, bool recursive, Action<DirectoryIndexProgress>? progress, ref int processed,
        CancellationToken cancellation = default)
    {
        // No all-files list, and no hash set proportional to the size of the library.
        if (!Directory.Exists(root)) throw new RequestError("SOURCE_UNAVAILABLE", $"搜索目录不可访问：{root}");
        SearchDirectoriesScanned++;
        progress?.Invoke(new("scanning", processed, processed));
        using (var prepare = Command("CREATE TEMP TABLE IF NOT EXISTS search_seen(path TEXT PRIMARY KEY COLLATE NOCASE); DELETE FROM search_seen;", null))
            prepare.ExecuteNonQuery();
        var processedLocal = processed;
        try
        {
            Visit(root);
            processed = processedLocal;
            // Prune only after a complete, accessible traversal. Offline/inaccessible trees retain their index.
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            using var prune = Command("""
                UPDATE search_files SET present=0 WHERE substr(path,1,length($prefix))=$prefix COLLATE NOCASE
                    AND ($recursive=1 OR instr(substr(path,length($prefix)+1), char(92))=0)
                    AND present=1 AND path NOT IN (SELECT path FROM search_seen);
                DELETE FROM search_folders WHERE substr(path,1,length($prefix))=$prefix COLLATE NOCASE
                    AND ($recursive=1 OR instr(substr(path,length($prefix)+1), char(92))=0)
                    AND path NOT IN (SELECT path FROM search_seen);
                """, null, ("$prefix", prefix), ("$recursive", recursive ? 1 : 0));
            cancellation.ThrowIfCancellationRequested();
            prune.ExecuteNonQuery();
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new RequestError("SOURCE_UNAVAILABLE", $"目录校验未完成，已保留现有索引：{root}");
        }

        void Visit(string folder)
        {
            cancellation.ThrowIfCancellationRequested();
            SaveSearchFolder(folder);
            Seen(folder);
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions {
                RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellation.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive) Visit(entry.FullName);
                    else { SaveSearchFolder(entry.FullName); Seen(entry.FullName); }
                }
                else if (IsSearchMedia(entry.FullName))
                {
                    RefreshSearchFile(entry.FullName);
                    Seen(entry.FullName);
                    processedLocal++;
                    if (processedLocal % 100 == 0)
                    {
                        progress?.Invoke(new("indexing", processedLocal, processedLocal));
                        // Bound continuous background CPU / IO bursts and let foreground work run.
                        cancellation.WaitHandle.WaitOne(5);
                    }
                }
            }
        }
        void Seen(string path)
        {
            using var insert = Command("INSERT OR IGNORE INTO search_seen VALUES($path)", null, ("$path", path));
            insert.ExecuteNonQuery();
        }
    }

    internal static bool IsSearchMedia(string path) => PhotoExtensions.Contains(Path.GetExtension(path)) ||
        VideoExtensions.Contains(Path.GetExtension(path)) || RawPhoto.Supports(Path.GetExtension(path));

    internal void SaveSearchFolder(string path)
    {
        using var handle = WindowsPaths.OpenDirectory(path);
        var identity = MediaFile.Read(handle, directory: true);
        using var command = Command("""
            INSERT INTO search_folders(path,created,identity) VALUES($path,$created,$identity)
            ON CONFLICT(path) DO UPDATE SET created=excluded.created,identity=excluded.identity
            WHERE created<>excluded.created OR identity<>excluded.identity
            """, null, ("$path", path), ("$created", new DateTime(identity.CreatedTicks, DateTimeKind.Utc).ToString("O")),
            ("$identity", identity.Identity));
        command.ExecuteNonQuery();
    }

    internal void RefreshSearchFile(string path, bool force = false)
    {
        if (!IsSearchMedia(path)) return;
        var file = new FileInfo(path);
        var sidecar = RawPhoto.Supports(file.Extension) ? new FileInfo(RawSidecar.PathFor(path)) : null;
        var length = file.Length;
        var modified = file.LastWriteTimeUtc.Ticks;
        var created = file.CreationTimeUtc.Ticks;
        var attributes = (long)file.Attributes;
        var xmpLength = sidecar?.Exists == true ? sidecar.Length : -1;
        var xmpModified = sidecar?.Exists == true ? sidecar.LastWriteTimeUtc.Ticks : -1;
        using (var cached = Command("""
            SELECT COUNT(*) FROM search_files WHERE path=$path AND length=$length AND modified=$modified
                AND created=$created AND attributes=$attributes AND xmp_length=$xl AND xmp_modified=$xm AND present=1
            """, null, ("$path", path), ("$length", length), ("$modified", modified), ("$created", created),
            ("$attributes", attributes), ("$xl", xmpLength), ("$xm", xmpModified)))
            if (!force && Convert.ToInt64(cached.ExecuteScalar()) != 0) return;

        var identity = MediaFile.Read(path);
        // A rename preserves the snapshot and its expensive metadata.
        var oldPath = SearchIdentityPath(identity.Identity, folder: false);
        if (oldPath is not null && !WindowsPaths.SamePath(oldPath, path) && !WindowsPaths.Exists(oldPath))
        {
            RelocateSearchPath(oldPath, path, directory: false);
            if (!force) { RefreshSearchFile(path); return; }
        }
        var imported = ImportOne(path, queue: false);
        if (imported.AssetId is not { } id) throw new IOException("Cannot read indexed file.");
        // Attribute readers run outside the write transaction. Search readers keep their WAL snapshot.
        var numbers = ReadSearchProperties(path, refresh: true);
        var properties = WindowsProperties.Read(path);
        var captured = properties.CapturedAt;
        if (captured is null && file.Extension.ToLowerInvariant() is ".mp4" or ".mov" or ".m4v")
            captured = EmbeddedMetadata.ReadVideoCaptureTime(path);
        // Do not commit a snapshot assembled while the file was changing.
        file.Refresh();
        if (file.Length != length || file.LastWriteTimeUtc.Ticks != modified || MediaFile.Read(path) != identity)
            throw new IOException("File changed during indexing.");
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var move = Command("""
            UPDATE asset_locations SET current_path=$path, source_path=CASE WHEN source_path=current_path THEN $path ELSE source_path END
                WHERE asset_id=$id;
            UPDATE assets SET original_name=$name, extension=$extension, media_kind=$kind WHERE id=$id;
            DELETE FROM search_files WHERE asset_id=$id AND path<>$path COLLATE NOCASE;
            """, transaction, ("$id", id), ("$path", path), ("$name", file.Name), ("$extension", file.Extension),
            ("$kind", VideoExtensions.Contains(file.Extension) ? "video" : "photo"))) move.ExecuteNonQuery();
        RefreshSearchProperties(id, path, transaction, properties, captured);
        IndexAsset(id, transaction);
        using var upsert = Command("""
            INSERT INTO search_files(path,asset_id,identity,length,modified,created,attributes,xmp_length,xmp_modified,numbers,present)
                VALUES($path,$id,$identity,$length,$modified,$created,$attributes,$xl,$xm,$numbers,1)
            ON CONFLICT(path) DO UPDATE SET asset_id=excluded.asset_id, identity=excluded.identity,length=excluded.length,
                modified=excluded.modified,created=excluded.created,attributes=excluded.attributes,xmp_length=excluded.xmp_length,
                xmp_modified=excluded.xmp_modified,numbers=excluded.numbers,present=1;
            """, transaction, ("$path", path), ("$id", id), ("$identity", identity.Identity), ("$length", length),
            ("$modified", modified), ("$created", created), ("$attributes", attributes), ("$xl", xmpLength),
            ("$xm", xmpModified), ("$numbers", JsonSerializer.Serialize(numbers)));
        upsert.ExecuteNonQuery();
        transaction.Commit();
        SearchFilesRefreshed++;
    }

    private Dictionary<string,double>? CachedSearchNumbers(string id)
    {
        using var read = Command("SELECT numbers FROM search_files WHERE asset_id=$id AND present=1 LIMIT 1", null, ("$id", id));
        return read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Dictionary<string,double>>(json) : null;
    }

    internal string? SearchIdentityPath(string identity, bool folder)
    {
        using var read = Command(folder ? "SELECT path FROM search_folders WHERE identity=$identity LIMIT 1" :
            "SELECT path FROM search_files WHERE identity=$identity AND present=1 LIMIT 1", null, ("$identity", identity));
        return read.ExecuteScalar() as string;
    }

    internal void MarkSearchMissing(string path)
    {
        using var command = Command("""
            UPDATE search_files SET present=0 WHERE present=1 AND (path=$path COLLATE NOCASE OR substr(path,1,length($prefix))=$prefix COLLATE NOCASE);
            DELETE FROM search_folders WHERE path=$path COLLATE NOCASE OR substr(path,1,length($prefix))=$prefix COLLATE NOCASE;
            """, null, ("$path", path), ("$prefix", Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar));
        command.ExecuteNonQuery();
    }

    internal void RelocateSearchPath(string oldPath, string newPath, bool directory)
    {
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;
        var oldPrefix = Path.TrimEndingDirectorySeparator(oldPath) + Path.DirectorySeparatorChar;
        var newPrefix = Path.TrimEndingDirectorySeparator(newPath) + Path.DirectorySeparatorChar;
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command("""
            UPDATE asset_locations SET
                source_path=CASE WHEN source_path=current_path THEN
                    CASE WHEN $directory=0 THEN $new ELSE $np || substr(current_path,length($op)+1) END ELSE source_path END,
                current_path=CASE WHEN $directory=0 THEN $new ELSE $np || substr(current_path,length($op)+1) END
                WHERE ($directory=0 AND current_path=$old COLLATE NOCASE)
                    OR ($directory=1 AND substr(current_path,1,length($op))=$op COLLATE NOCASE);
            UPDATE search_files SET path=CASE WHEN $directory=0 THEN $new ELSE $np || substr(path,length($op)+1) END
                WHERE ($directory=0 AND path=$old COLLATE NOCASE)
                    OR ($directory=1 AND substr(path,1,length($op))=$op COLLATE NOCASE);
            UPDATE search_folders SET path=CASE WHEN path=$old COLLATE NOCASE THEN $new ELSE $np || substr(path,length($op)+1) END
                WHERE $directory=1 AND (path=$old COLLATE NOCASE OR substr(path,1,length($op))=$op COLLATE NOCASE);
            UPDATE asset_search SET directory_key=CASE WHEN directory_key=$oldkey THEN $newkey
                ELSE $newkey || substr(directory_key,length($oldkey)+1) END
                WHERE $directory=1 AND (directory_key=$oldkey OR substr(directory_key,1,length($oldkey)+1)=$oldkey || char(92));
            """, transaction, ("$old", oldPath), ("$new", newPath), ("$op", oldPrefix), ("$np", newPrefix),
            ("$directory", directory ? 1 : 0), ("$oldkey", DirectoryKey(oldPath)), ("$newkey", DirectoryKey(newPath)));
        command.ExecuteNonQuery();
        if (!directory)
        {
            using var find = Command("SELECT asset_id FROM search_files WHERE path=$path", transaction, ("$path", newPath));
            if (find.ExecuteScalar() is string id)
            {
                using var rename = Command("UPDATE assets SET original_name=$name,extension=$extension WHERE id=$id", transaction,
                    ("$name", Path.GetFileName(newPath)), ("$extension", Path.GetExtension(newPath)), ("$id", id));
                rename.ExecuteNonQuery();
                IndexAsset(id, transaction);
            }
        }
        transaction.Commit();
    }

    internal void SaveSearchCheckpoint(string root, string? journalId, long? next)
    {
        using var command = Command("UPDATE search_roots SET ready=1,journal_id=$journal,next_usn=$next WHERE path=$path", null,
            ("$path", root), ("$journal", journalId), ("$next", next));
        command.ExecuteNonQuery();
    }
}
