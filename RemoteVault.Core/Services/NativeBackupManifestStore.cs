using Microsoft.Data.Sqlite;

namespace RemoteVault.Core.Services;

public sealed class NativeBackupManifestStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public NativeBackupManifestStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();

        InitializeSchema();
    }

    public int BeginRun()
    {
        using var transaction = _connection.BeginTransaction();
        using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT value FROM meta WHERE key='run_id';";
        var raw = select.ExecuteScalar()?.ToString();
        var runId = (int.TryParse(raw, out var parsed) ? parsed : 0) + 1;

        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE meta SET value=$value WHERE key='run_id';";
        update.Parameters.AddWithValue("$value", runId.ToString());
        update.ExecuteNonQuery();

        transaction.Commit();
        return runId;
    }

    public Dictionary<string, UploadedManifestRow> GetManyUploaded(IReadOnlyList<string> relPaths)
    {
        var results = new Dictionary<string, UploadedManifestRow>(StringComparer.Ordinal);
        if (relPaths.Count == 0)
        {
            return results;
        }

        const int chunkSize = 500;
        for (var offset = 0; offset < relPaths.Count; offset += chunkSize)
        {
            var chunk = relPaths.Skip(offset).Take(chunkSize).ToArray();
            using var command = _connection.CreateCommand();
            var parameterNames = new List<string>(chunk.Length);

            for (var index = 0; index < chunk.Length; index++)
            {
                var parameterName = $"$p{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, chunk[index]);
            }

            command.CommandText =
                $"SELECT rel, uploaded_size, uploaded_mtime_ns FROM files WHERE rel IN ({string.Join(",", parameterNames)}) AND uploaded_ok = 1;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results[reader.GetString(0)] = new UploadedManifestRow
                {
                    Size = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    ModifiedTimeNs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
                };
            }
        }

        return results;
    }

    public void UpsertScanMany(IEnumerable<ManifestScanRow> rows)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files(
                    rel, size, mtime_ns, last_seen_run,
                    uploaded_size, uploaded_mtime_ns, uploaded_ok, uploaded_run)
                VALUES($rel, $size, $mtimeNs, $lastSeenRun, NULL, NULL, 0, NULL)
                ON CONFLICT(rel) DO UPDATE SET
                    size = excluded.size,
                    mtime_ns = excluded.mtime_ns,
                    last_seen_run = excluded.last_seen_run;
                """;
            command.Parameters.AddWithValue("$rel", row.RelativePath);
            command.Parameters.AddWithValue("$size", row.Size);
            command.Parameters.AddWithValue("$mtimeNs", row.ModifiedTimeNs);
            command.Parameters.AddWithValue("$lastSeenRun", row.LastSeenRun);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void EndRun(int runId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE last_seen_run <> $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        command.ExecuteNonQuery();
    }

    public void MarkUploadedMany(IEnumerable<ManifestUploadedRow> rows, int runId)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE files
                SET uploaded_size = $size,
                    uploaded_mtime_ns = $mtimeNs,
                    uploaded_ok = 1,
                    uploaded_run = $runId
                WHERE rel = $rel;
                """;
            command.Parameters.AddWithValue("$size", row.Size);
            command.Parameters.AddWithValue("$mtimeNs", row.ModifiedTimeNs);
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$rel", row.RelativePath);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void InitializeSchema()
    {
        ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery("PRAGMA temp_store=MEMORY;");

        ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS files (
                rel TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime_ns INTEGER NOT NULL,
                last_seen_run INTEGER NOT NULL,
                uploaded_size INTEGER,
                uploaded_mtime_ns INTEGER,
                uploaded_ok INTEGER NOT NULL DEFAULT 0,
                uploaded_run INTEGER
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        ExecuteNonQuery("INSERT OR IGNORE INTO meta(key, value) VALUES('run_id', '0');");
        ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_files_last_seen ON files(last_seen_run);");
        ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_files_uploaded_ok ON files(uploaded_ok);");
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

public sealed class ManifestScanRow
{
    public string RelativePath { get; init; } = string.Empty;

    public long Size { get; init; }

    public long ModifiedTimeNs { get; init; }

    public int LastSeenRun { get; init; }
}

public sealed class ManifestUploadedRow
{
    public string RelativePath { get; init; } = string.Empty;

    public long Size { get; init; }

    public long ModifiedTimeNs { get; init; }
}

public sealed class UploadedManifestRow
{
    public long Size { get; init; }

    public long ModifiedTimeNs { get; init; }
}
