using System.Text.Json;

namespace MediaLibrary.Service;

public sealed record DirectorySuggestion(string DirectoryId, string Reason);
public sealed record SuggestionState(string RequestId, string Status, bool UsesRemark, bool UsesAuthors,
    string? ErrorCode = null, string[]? Names = null, string? Remark = null, DirectorySuggestion[]? Candidates = null);
public sealed record DraftSuggestions(SuggestionState? Name = null, SuggestionState? Remark = null,
    SuggestionState? Directory = null);

public sealed partial class LibraryDatabase
{
    private static DraftSuggestions? KeepSuggestions(DraftData before, DraftData after)
    {
        var previous = before.Suggestions;
        if (previous is null) return null;
        var nameChanged = before.Name != after.Name;
        var remarkChanged = before.Remark.Value != after.Remark.Value;
        var authorsChanged = !before.Authors.Value.SequenceEqual(after.Authors.Value);
        return new(
            nameChanged || remarkChanged && previous.Name?.UsesRemark == true ? null : previous.Name,
            before.Keywords != after.Keywords || remarkChanged && previous.Remark?.UsesRemark == true ? null : previous.Remark,
            !after.Finalized || nameChanged || remarkChanged && previous.Directory?.UsesRemark == true ||
                authorsChanged && previous.Directory?.UsesAuthors == true ? null : previous.Directory);
    }

    internal AssetDraft SaveSuggestion(string assetId, int expectedRevision, string kind, SuggestionState input)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = GetDraft(assetId, transaction);
        if (draft.Revision != expectedRevision)
            throw new RequestError("REVISION_CONFLICT", "草稿已经更新。");
        if (ActiveArchive(assetId, transaction) is not null || kind == "directory" && !draft.Data.Finalized)
            throw new RequestError("REVISION_CONFLICT", "素材目前不能更新建议。");
        var suggestions = draft.Data.Suggestions ?? new();
        if (input.Status != "pending")
        {
            var current = kind switch { "name" => suggestions.Name, "remark" => suggestions.Remark, _ => suggestions.Directory };
            if (current?.RequestId != input.RequestId || current.Status != "pending" ||
                current.UsesRemark != input.UsesRemark || current.UsesAuthors != input.UsesAuthors)
                throw new RequestError("REVISION_CONFLICT", "建议已过期。");
        }

        foreach (var candidate in input.Candidates ?? [])
        {
            var directory = OpenArchiveDirectory(candidate.DirectoryId, transaction);
            using var root = directory.Root;
            using var handle = directory.Directory;
        }
        var next = kind switch
        {
            "name" => suggestions with { Name = input },
            "remark" => suggestions with { Remark = input },
            "directory" => suggestions with { Directory = input },
            _ => throw new RequestError("INVALID_ARGUMENT", "建议类型不支持。")
        };
        var updated = SaveSuggestionData(draft, next, transaction);
        transaction.Commit();
        return updated;
    }

    internal void RecoverPendingSuggestions()
    {
        // The desktop owns model requests. Once it starts a new service, pending
        // work from an interrupted session must remain available for manual retry.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var read = Command("""
            SELECT d.asset_id, d.revision, d.archive_revision, d.data
            FROM drafts d JOIN assets a ON a.id = d.asset_id
            WHERE a.archived = 0 AND a.queued = 1 AND (
                json_extract(d.data, '$.suggestions.name.status') = 'pending' OR
                json_extract(d.data, '$.suggestions.remark.status') = 'pending' OR
                json_extract(d.data, '$.suggestions.directory.status') = 'pending')
            """, transaction);
        var pending = new List<AssetDraft>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                pending.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    JsonSerializer.Deserialize<DraftData>(reader.GetString(3), WorkspaceJson)!));

        SuggestionState? Recover(SuggestionState? state) =>
            state?.Status == "pending" ? state with { Status = "retry", ErrorCode = "MODEL_INTERRUPTED" } : state;
        foreach (var draft in pending)
        {
            var value = draft.Data.Suggestions!;
            SaveSuggestionData(draft, new(Recover(value.Name), Recover(value.Remark), Recover(value.Directory)), transaction);
        }
        transaction.Commit();
    }

    private AssetDraft SaveSuggestionData(AssetDraft draft, DraftSuggestions suggestions, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var updated = draft with { Revision = draft.Revision + 1, Data = draft.Data with { Suggestions = suggestions } };
        using var command = Command("UPDATE drafts SET revision = $revision, data = $data WHERE asset_id = $id", transaction,
            ("$revision", updated.Revision), ("$data", JsonSerializer.Serialize(updated.Data, WorkspaceJson)),
            ("$id", updated.AssetId));
        command.ExecuteNonQuery();
        return updated;
    }

    internal AssetSummary GetPendingAsset(string id)
    {
        using var command = Command("""
            SELECT a.original_name, a.extension, a.media_kind, a.created_at, l.current_path,
                l.file_identity, l.file_created_ticks
            FROM assets a JOIN asset_locations l ON l.asset_id = a.id WHERE a.id = $id AND a.archived = 0 AND a.queued = 1
            """, null, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new RequestError("INVALID_ARGUMENT", "待整理素材不存在。");
        return new(id, row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
            MatchesFile(row.GetString(4), new(row.GetString(5), row.GetInt64(6))));
    }

    internal ArchiveDirectory[] RecommendationDirectories(string id)
    {
        var draft = GetDraft(id);
        var known = AllDirectories();
        var paths = new Dictionary<string, ArchiveTarget?>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in known)
            if (DirectoryMatches(directory)) paths.TryAdd(directory.Path, directory);

        var queue = new Queue<(string Path, int Depth)>(known
            .Where(item => item.RootId is null && paths.ContainsKey(item.Path)).Select(item => (item.Path, 0)));
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var node) && discovered.Count < 240)
        {
            if (node.Depth >= 6) continue;
            try
            {
                foreach (var directory in new DirectoryInfo(node.Path).EnumerateDirectories())
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || !discovered.Add(directory.FullName)) continue;
                    paths.TryAdd(directory.FullName, null);
                    queue.Enqueue((directory.FullName, node.Depth + 1));
                    if (discovered.Count >= 240) break;
                }
            }
            catch (Exception error) when (IsFileError(error)) { }
        }

        var context = (draft.Data.Name + " " + draft.Data.Remark.Value).ToUpperInvariant();
        var result = new List<ArchiveDirectory>();
        foreach (var entry in paths.OrderByDescending(item =>
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Key));
            return leaf.Length > 0 && context.Contains(leaf.ToUpperInvariant(), StringComparison.Ordinal) ? leaf.Length : 0;
        }).ThenBy(item => item.Key.Length).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = entry.Value is { } existing ? DescribeDirectory(existing) : AddDirectory(entry.Key, root: false);
                if (directory.Available) result.Add(directory);
                if (result.Count == 30) break;
            }
            catch (RequestError) { }
        }
        return result.ToArray();
    }
}
