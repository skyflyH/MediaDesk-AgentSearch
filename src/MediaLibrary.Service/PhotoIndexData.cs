using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace MediaLibrary.Service;

public sealed record PhotoIndexState(string Status, string? Version, string? Message);
public sealed record PhotoJobInfo(string JobId, string AssetId, string FileName, string State, string? ErrorCode);
public sealed record PhotoJobList(PhotoIndexState Model, int Queued, int Running, int Completed, int Failed, PhotoJobInfo[] Items);
public sealed record PhotoSceneResult(PhotoIndexState Model, SearchItem[] Items);
internal sealed record PhotoWork(string JobId, string AssetId, string Fingerprint);

public sealed partial class LibraryDatabase
{
    internal void RecoverPhotoJobs()
    {
        using var command = Command("UPDATE photo_jobs SET state = 'queued', error_code = NULL WHERE state = 'running'", null);
        command.ExecuteNonQuery();
    }

    internal void SyncPhotoJobs(string version)
    {
        using var command = Command("""
            INSERT INTO photo_jobs(job_id, asset_id, model_version, fingerprint, state, error_code, updated_at)
            SELECT a.id, a.id, $version, a.fingerprint,
                CASE WHEN e.asset_id IS NULL THEN 'queued' ELSE 'completed' END, NULL,
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM assets a LEFT JOIN photo_embeddings e ON e.asset_id = a.id
                AND e.model_version = $version AND e.fingerprint = a.fingerprint
            WHERE a.archived = 1 AND a.media_kind = 'photo' AND a.fingerprint IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM file_operations o WHERE o.asset_id = a.id AND o.phase = 'properties_pending'
                        AND json_extract(o.data, '$.errorCode') IS NULL
                )
            ON CONFLICT(asset_id) DO UPDATE SET model_version = excluded.model_version,
                fingerprint = excluded.fingerprint, state = excluded.state, error_code = NULL,
                updated_at = excluded.updated_at
            WHERE photo_jobs.model_version <> excluded.model_version OR photo_jobs.fingerprint <> excluded.fingerprint
            """, null, ("$version", version));
        command.ExecuteNonQuery();
    }

    internal PhotoWork? ClaimPhotoJob(string version)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command("""
            UPDATE photo_jobs SET state = 'running', error_code = NULL,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE job_id = (
                SELECT j.job_id FROM photo_jobs j JOIN assets a ON a.id = j.asset_id
                WHERE j.model_version = $version AND j.state = 'queued' AND j.fingerprint = a.fingerprint
                ORDER BY j.updated_at, j.job_id LIMIT 1
            ) RETURNING job_id, asset_id, fingerprint
            """, transaction, ("$version", version));
        PhotoWork? job = null;
        using (var row = command.ExecuteReader())
            if (row.Read()) job = new(row.GetString(0), row.GetString(1), row.GetString(2));
        transaction.Commit();
        return job;
    }

    internal void CompletePhotoJob(string version, PhotoWork job, FileStamp stamp, float[] vector)
    {
        if (vector.Length != ChineseClip.Dimensions) throw new InvalidDataException("Unexpected photo vector size.");
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command("""
            INSERT INTO photo_embeddings (asset_id, model_version, fingerprint, length, write_ticks, vector)
            SELECT id, $version, $fingerprint, $length, $written, $vector FROM assets
            WHERE id = $asset AND fingerprint = $fingerprint
            ON CONFLICT(asset_id, model_version) DO UPDATE SET fingerprint = excluded.fingerprint,
                length = excluded.length, write_ticks = excluded.write_ticks, vector = excluded.vector
            """, transaction, ("$asset", job.AssetId), ("$version", version), ("$fingerprint", job.Fingerprint),
            ("$length", stamp.Length), ("$written", stamp.LastWriteTicks), ("$vector", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray()));
        var changed = command.ExecuteNonQuery() == 1;
        SetPhotoJobState(job, version, changed ? "completed" : "queued", null, transaction);
        transaction.Commit();
    }

    internal void FailPhotoJob(PhotoWork job, string version, string code) => SetPhotoJobState(job, version, "failed", code, null);

    private void SetPhotoJobState(PhotoWork job, string version, string state, string? code, SqliteTransaction? transaction)
    {
        using var command = Command("""
            UPDATE photo_jobs SET state = $state, error_code = $code, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE job_id = $id AND model_version = $version AND fingerprint = $fingerprint AND state = 'running'
            """, transaction, ("$state", state), ("$code", code), ("$id", job.JobId), ("$version", version),
            ("$fingerprint", job.Fingerprint));
        command.ExecuteNonQuery();
    }

    internal bool RetryPhotoJob(string id, string version)
    {
        using var command = Command("""
            UPDATE photo_jobs SET state = 'queued', error_code = NULL, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE job_id = $id AND model_version = $version AND state = 'failed'
                AND fingerprint = (SELECT fingerprint FROM assets WHERE id = photo_jobs.asset_id)
            """, null, ("$id", id), ("$version", version));
        return command.ExecuteNonQuery() != 0;
    }

    internal PhotoJobList PhotoJobs(PhotoIndexState model, string? state)
    {
        var counts = new Dictionary<string, int>();
        var items = new List<PhotoJobInfo>();
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var command = Command("""
            SELECT j.state, count(*) FROM photo_jobs j JOIN assets a ON a.id = j.asset_id AND a.fingerprint = j.fingerprint
            WHERE j.model_version = $version GROUP BY j.state
            """, transaction, ("$version", model.Version)))
        using (var row = command.ExecuteReader())
            while (row.Read()) counts.Add(row.GetString(0), row.GetInt32(1));
        using (var command = Command("""
            SELECT j.job_id, j.asset_id, l.current_path, j.state, j.error_code FROM photo_jobs j
                JOIN asset_locations l ON l.asset_id = j.asset_id
                JOIN assets a ON a.id = j.asset_id AND a.fingerprint = j.fingerprint
            WHERE j.model_version = $version AND ($state IS NULL OR j.state = $state)
            ORDER BY CASE j.state WHEN 'failed' THEN 0 WHEN 'running' THEN 1 WHEN 'queued' THEN 2 ELSE 3 END,
                j.updated_at DESC, j.job_id LIMIT 80
            """, transaction, ("$version", model.Version), ("$state", state)))
        using (var row = command.ExecuteReader())
            while (row.Read())
                items.Add(new(row.GetString(0), row.GetString(1), Path.GetFileName(row.GetString(2)),
                    row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4)));
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM assets WHERE archived = 1 AND media_kind = 'photo'", transaction));
        transaction.Commit();
        var completed = counts.GetValueOrDefault("completed");
        var failed = counts.GetValueOrDefault("failed");
        var running = counts.GetValueOrDefault("running");
        return new(model, Math.Max(0, total - completed - failed - running), running, completed, failed, items.ToArray());
    }
}
