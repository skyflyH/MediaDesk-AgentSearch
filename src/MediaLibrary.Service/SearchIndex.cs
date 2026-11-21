using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaLibrary.Service;

public sealed partial class LibraryDatabase
{
    private static string TextKey(string text) => text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static string DirectoryKey(string path) => path.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private void InitializeSearchIndex(SqliteTransaction transaction, bool rebuild)
    {
        if (rebuild)
        {
            using var command = Command("SELECT id FROM assets WHERE archived = 1 ORDER BY queue_position", transaction);
            var assets = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) assets.Add(reader.GetString(0));

            foreach (var id in assets)
            {
                var asset = Locate(id, transaction);
                if (MatchesFile(asset.Path, asset.Identity) && WindowsProperties.Read(asset.Path).CapturedAt is { } captured)
                {
                    using var update = Command("UPDATE assets SET captured_at = $captured WHERE id = $id", transaction,
                        ("$captured", captured.ToString("O")), ("$id", id));
                    update.ExecuteNonQuery();
                }
                IndexAsset(id, transaction, updateFts: false);
            }
        }

        if (!fts5Trigram) return;
        using (var create = Command("CREATE VIRTUAL TABLE IF NOT EXISTS asset_search_fts USING fts5(name_key, remark_key, tokenize='trigram')",
            transaction))
            create.ExecuteNonQuery();
        if (Convert.ToInt64(Scalar("SELECT fts_dirty FROM search_index_state WHERE id = 1", transaction)) == 0) return;

        using var restore = Command("""
            DELETE FROM asset_search_fts;
            INSERT INTO asset_search_fts(rowid, name_key, remark_key)
            SELECT row_id, name_key, remark_key FROM asset_search;
            UPDATE search_index_state SET fts_dirty = 0 WHERE id = 1;
            """, transaction);
        restore.ExecuteNonQuery();
    }

    private void IndexAsset(string id, SqliteTransaction transaction, bool updateFts = true)
    {
        long rowId;
        string path, format, remark, serializedAuthors;
        using (var read = Command("""
            SELECT a.queue_position, l.current_path, a.extension, COALESCE(d.remark, ''), COALESCE(d.authors, '[]')
            FROM assets a JOIN asset_locations l ON l.asset_id = a.id
            LEFT JOIN asset_details d ON d.asset_id = a.id WHERE a.id = $id
            """, transaction, ("$id", id)))
        using (var row = read.ExecuteReader())
        {
            if (!row.Read()) return;
            rowId = row.GetInt64(0);
            path = row.GetString(1);
            format = row.GetString(2);
            remark = row.GetString(3);
            serializedAuthors = row.GetString(4);
        }

        using var upsert = Command("""
            INSERT INTO asset_search(row_id, name_key, remark_key, directory_key, format_key)
            VALUES($row, $name, $remark, $directory, $format)
            ON CONFLICT(row_id) DO UPDATE SET name_key = excluded.name_key, remark_key = excluded.remark_key,
                directory_key = excluded.directory_key, format_key = excluded.format_key;
            DELETE FROM asset_search_authors WHERE row_id = $row;
            """, transaction, ("$row", rowId), ("$name", TextKey(Path.GetFileName(path))), ("$remark", TextKey(remark)),
            ("$directory", DirectoryKey(Path.GetDirectoryName(path)!)), ("$format", TextKey(format.TrimStart('.'))));
        upsert.ExecuteNonQuery();

        using var authors = Command("INSERT INTO asset_search_authors (author_key, row_id) VALUES ($author, $row)",
            transaction, ("$author", ""), ("$row", rowId));
        foreach (var author in (JsonSerializer.Deserialize<string[]>(serializedAuthors) ?? []).Select(name => TextKey(name.Trim())).Distinct())
        {
            authors.Parameters["$author"].Value = author;
            authors.ExecuteNonQuery();
        }

        if (updateFts && fts5Trigram)
        {
            using var text = Command("""
                DELETE FROM asset_search_fts WHERE rowid = $row;
                INSERT INTO asset_search_fts(rowid, name_key, remark_key)
                SELECT row_id, name_key, remark_key FROM asset_search WHERE row_id = $row;
                """, transaction, ("$row", rowId));
            text.ExecuteNonQuery();
        }
        else if (updateFts)
        {
            using var dirty = Command("UPDATE search_index_state SET fts_dirty = 1 WHERE id = 1", transaction);
            dirty.ExecuteNonQuery();
        }
    }
}
