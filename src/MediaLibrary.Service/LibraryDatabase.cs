using Microsoft.Data.Sqlite;

namespace MediaLibrary.Service;

public sealed record DatabaseHealth(string Status, string SqliteVersion, int SchemaVersion,
    string JournalMode, bool ForeignKeys, bool Fts5Trigram);

public sealed partial class LibraryDatabase : IDisposable
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE workspace_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            current_asset_id TEXT,
            updated_at TEXT NOT NULL
        );
        INSERT INTO workspace_state (id, updated_at)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """,
        """
        CREATE TABLE assets (
            queue_position INTEGER PRIMARY KEY,
            id TEXT NOT NULL UNIQUE,
            original_name TEXT NOT NULL,
            extension TEXT NOT NULL,
            media_kind TEXT NOT NULL CHECK (media_kind IN ('photo', 'video')),
            created_at TEXT NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1))
        );
        CREATE TABLE asset_locations (
            asset_id TEXT PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            source_path TEXT NOT NULL,
            current_path TEXT NOT NULL,
            file_identity TEXT NOT NULL,
            file_created_ticks INTEGER NOT NULL,
            UNIQUE (file_identity, file_created_ticks)
        );
        CREATE TABLE drafts (
            asset_id TEXT PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            revision INTEGER NOT NULL DEFAULT 0,
            archive_revision INTEGER NOT NULL DEFAULT 0,
            data TEXT NOT NULL CHECK (json_valid(data))
        );
        """,
        """
        CREATE TABLE archive_directories (
            id TEXT PRIMARY KEY,
            root_id TEXT REFERENCES archive_directories(id),
            path TEXT NOT NULL,
            identity TEXT NOT NULL,
            created_ticks INTEGER NOT NULL,
            UNIQUE(path, identity, created_ticks)
        );
        CREATE TABLE file_operations (
            operation_id TEXT PRIMARY KEY,
            asset_id TEXT NOT NULL REFERENCES assets(id),
            plan_token TEXT NOT NULL,
            phase TEXT NOT NULL,
            data TEXT NOT NULL CHECK (json_valid(data))
        );
        CREATE UNIQUE INDEX file_operations_asset ON file_operations(asset_id) WHERE phase <> 'complete';
        CREATE TABLE asset_details (
            asset_id TEXT PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
            remark TEXT NOT NULL,
            authors TEXT NOT NULL CHECK (json_valid(authors)),
            remark_state TEXT NOT NULL,
            authors_state TEXT NOT NULL
        );
        ALTER TABLE assets ADD COLUMN fingerprint TEXT;
        """,
        """
        ALTER TABLE assets ADD COLUMN captured_at TEXT;
        CREATE INDEX assets_search_order ON assets(created_at DESC, id) WHERE archived = 1;
        CREATE TABLE asset_search (
            row_id INTEGER PRIMARY KEY REFERENCES assets(queue_position) ON DELETE CASCADE,
            name_key TEXT NOT NULL,
            remark_key TEXT NOT NULL,
            directory_key TEXT NOT NULL,
            format_key TEXT NOT NULL
        );
        CREATE INDEX asset_search_directory ON asset_search(directory_key);
        CREATE INDEX asset_search_format ON asset_search(format_key);
        CREATE TABLE asset_search_authors (
            author_key TEXT NOT NULL,
            row_id INTEGER NOT NULL REFERENCES asset_search(row_id) ON DELETE CASCADE,
            PRIMARY KEY (author_key, row_id)
        ) WITHOUT ROWID;
        CREATE INDEX asset_search_authors_row ON asset_search_authors(row_id);
        CREATE TABLE search_index_state (id INTEGER PRIMARY KEY CHECK (id = 1), fts_dirty INTEGER NOT NULL);
        INSERT INTO search_index_state VALUES (1, 1);
        """,
        """
        CREATE TABLE photo_jobs (
            job_id TEXT PRIMARY KEY,
            asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            model_version TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('queued', 'running', 'completed', 'failed')),
            error_code TEXT,
            updated_at TEXT NOT NULL,
            UNIQUE(asset_id)
        );
        CREATE INDEX photo_jobs_queue ON photo_jobs(model_version, state, updated_at, job_id);
        CREATE TABLE photo_embeddings (
            asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            model_version TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            length INTEGER NOT NULL,
            write_ticks INTEGER NOT NULL,
            vector BLOB NOT NULL,
            PRIMARY KEY(asset_id, model_version)
        ) WITHOUT ROWID;
        """,
        """
        CREATE TABLE workspace_import_folders (path TEXT PRIMARY KEY COLLATE NOCASE);
        CREATE INDEX file_operations_source_identity ON file_operations(
            json_extract(data, '$.plan.sourceStamp.identity.identity'),
            json_extract(data, '$.plan.sourceStamp.identity.createdTicks')
        );
        """,
        """
        ALTER TABLE assets ADD COLUMN queued INTEGER NOT NULL DEFAULT 1 CHECK (queued IN (0, 1));
        """,
        """
        CREATE TABLE workspace_removed_folders (path TEXT PRIMARY KEY COLLATE NOCASE);
        """,
        """
        ALTER TABLE asset_locations ADD COLUMN pending_source_path TEXT;
        """,
        """
        DROP INDEX file_operations_asset;
        CREATE UNIQUE INDEX file_operations_asset ON file_operations(asset_id)
            WHERE phase NOT IN ('complete', 'failed');
        UPDATE file_operations SET phase = 'failed', data = json_set(data,
            '$.phase', 'failed', '$.errorCode', 'COPY_FAILED',
            '$.message', '复制在生成副本前中断。请确认目标目录可写，或选择其他目录后重试。')
        WHERE phase = 'needs_review'
            AND json_extract(data, '$.errorCode') = 'NEEDS_REVIEW'
            AND json_extract(data, '$.tempPath') IS NOT NULL
            AND json_extract(data, '$.tempIdentity') IS NULL
            AND json_extract(data, '$.published') IS NULL
            AND json_extract(data, '$.actual') IS NULL;
        """
    ];

    private readonly SqliteConnection connection;
    private readonly bool fts5Trigram;
    internal string ProfileDirectory => Path.GetDirectoryName(Path.GetFullPath(connection.DataSource))!;

    public LibraryDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            if (!string.Equals(Scalar("PRAGMA journal_mode = WAL") as string, "wal", StringComparison.Ordinal))
                throw new InvalidOperationException("SQLite could not enable WAL.");
            // Bounded read mapping avoids a separate Windows file read for each vector page.
            Scalar("PRAGMA mmap_size = 536870912");
            fts5Trigram = ProbeFts();
            Migrate();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public DatabaseHealth Health() => new(
        "ready",
        (string)Scalar("SELECT sqlite_version()"),
        Convert.ToInt32(Scalar("PRAGMA user_version")),
        (string)Scalar("PRAGMA journal_mode"),
        Convert.ToInt64(Scalar("PRAGMA foreign_keys")) == 1,
        fts5Trigram);

    private void Migrate()
    {
        // Read the version under the same write lock as the migration.
        using var transaction = connection.BeginTransaction(deferred: false);
        var version = Convert.ToInt32(Scalar("PRAGMA user_version", transaction));
        if (version < 0 || version > Migrations.Length)
            throw new InvalidOperationException("Database schema is incompatible.");

        for (var index = version; index < Migrations.Length; index++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Migrations[index];
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA user_version = {index + 1}";
            command.ExecuteNonQuery();
        }
        InitializeSearchIndex(transaction, rebuild: version < 4);
        transaction.Commit();
    }

    private bool ProbeFts()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE temp.health_fts USING fts5(text, tokenize='trigram')";
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            (ex.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("no such tokenizer", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        command.CommandText = "DROP TABLE temp.health_fts";
        command.ExecuteNonQuery();
        return true;
    }

    private object Scalar(string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    public void Dispose() => connection.Dispose();
}
