using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Storage;

public sealed record FileSyncProblem(string Source, string Path, string Error);

public sealed record SourceSyncReport
{
    public required string Source { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
    public int Files { get; init; }
    public int ParsedFiles { get; init; }
    public int SkippedLines { get; init; }
    public IReadOnlyDictionary<string, int> SkipReasons { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<FileSyncProblem> Problems { get; init; } = [];
}

public sealed record SyncReport(IReadOnlyList<SourceSyncReport> Sources, DateTimeOffset FinishedAt, TimeSpan Duration);

/// <summary>
/// Trwały rejestr wpisów zużycia w SQLite (<c>%LOCALAPPDATA%\OnionLLM\ledger.db</c>).
/// Pliki są przetwarzane ponownie tylko po zmianie rozmiaru/mtime albo wersji parsera.
/// Wpisy z plików, które zniknęły, są zachowywane (historia przetrwa czyszczenie logów przez narzędzia).
/// </summary>
public sealed class UsageLedger
{
    private const int SchemaVersion = 5;
    private const string MutexName = "Local\\OnionLLM.Ledger";
    private readonly string _connectionString;
    private bool _initialized;

    public UsageLedger(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(FileUtil.DataDirectory, "ledger.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString();
    }

    public string DatabasePath { get; }

    public SyncReport Sync(IEnumerable<IUsageSource> sources, OnionConfig config, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var reports = new List<SourceSyncReport>();
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            using var connection = Open();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!config.IsSourceEnabled(source.Id))
                {
                    continue;
                }

                reports.Add(SyncSource(connection, source, config, cancellationToken));
            }

            SetMeta(connection, "last_sync", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }

        return new SyncReport(reports, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - started);
    }

    private SourceSyncReport SyncSource(SqliteConnection connection, IUsageSource source, OnionConfig config, CancellationToken cancellationToken)
    {
        var versionKey = "parser:" + source.Id;
        var versionChanged = GetMeta(connection, versionKey) != source.ParserVersion.ToString();
        var known = LoadKnownFiles(connection, source.Id);
        var files = source.EnumerateFiles(config).ToArray();
        var parsed = 0;
        var skipped = 0;
        var reasons = new Dictionary<string, int>();
        var problems = source.ConfigurationProblems(config)
            .Select(problem => new FileSyncProblem(source.Id, "(konfiguracja)", problem))
            .ToList();

        if (versionChanged)
        {
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM Entries WHERE source = $source; DELETE FROM SourceFiles WHERE source = $source;";
            clear.Parameters.AddWithValue("$source", source.Id);
            clear.ExecuteNonQuery();
            known.Clear();
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.TryGetValue(file.Path, out var state) && state.Size == file.Size && state.ModifiedMs == file.ModifiedMs)
            {
                skipped += state.Skipped;
                continue;
            }

            var diagnostics = new ParseDiagnostics();
            List<UsageEntry> entries;
            try
            {
                entries = source.Parse(file, diagnostics, cancellationToken).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
            {
                problems.Add(new FileSyncProblem(source.Id, file.Path, exception.Message));
                continue;
            }

            ReplaceFile(connection, source.Id, file, entries, diagnostics.SkippedLines);
            parsed++;
            skipped += diagnostics.SkippedLines;
            foreach (var (reason, count) in diagnostics.Reasons)
            {
                reasons[reason] = reasons.TryGetValue(reason, out var existing) ? existing + count : count;
            }
        }

        if (versionChanged)
        {
            SetMeta(connection, versionKey, source.ParserVersion.ToString());
        }

        return new SourceSyncReport
        {
            Source = source.Id,
            Roots = source.ResolveRoots(config),
            Files = files.Length,
            ParsedFiles = parsed,
            SkippedLines = skipped,
            SkipReasons = reasons,
            Problems = problems
        };
    }

    /// <summary>Wpisy z przedziału [fromMs, toMs) — przed deduplikacją.</summary>
    public List<UsageEntry> Query(long? fromMs = null, long? toMs = null, IReadOnlyCollection<string>? sources = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (fromMs is not null)
        {
            conditions.Add("ts >= $from");
            command.Parameters.AddWithValue("$from", fromMs.Value);
        }

        if (toMs is not null)
        {
            conditions.Add("ts < $to");
            command.Parameters.AddWithValue("$to", toMs.Value);
        }

        if (sources is { Count: > 0 })
        {
            var names = sources.Select((source, index) =>
            {
                command.Parameters.AddWithValue("$s" + index, source);
                return "$s" + index;
            });
            conditions.Add($"source IN ({string.Join(',', names)})");
        }

        command.CommandText = $"""
            SELECT source, ts, session_id, project, project_path, model, fallback_model, input, output,
                   cache_create, cache_create_1h, cache_read, reasoning, recorded_cost, speed, credits,
                   dedupe_key, is_summary, mirror_of, aggregate, provider, preference, source_data
            FROM Entries
            {(conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "")}
            ORDER BY ts, file_path
            """;

        var result = new List<UsageEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new UsageEntry
            {
                Source = reader.GetString(0),
                TimestampMs = reader.GetInt64(1),
                SessionId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Project = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProjectPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                Model = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsFallbackModel = reader.GetInt64(6) != 0,
                InputTokens = reader.GetInt64(7),
                OutputTokens = reader.GetInt64(8),
                CacheCreationTokens = reader.GetInt64(9),
                CacheCreation1hTokens = reader.GetInt64(10),
                CacheReadTokens = reader.GetInt64(11),
                ReasoningTokens = reader.GetInt64(12),
                RecordedCostUsd = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                Speed = (SpeedTier)reader.GetInt64(14),
                Credits = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                DedupeKey = reader.IsDBNull(16) ? null : reader.GetString(16),
                SessionTier = (int)reader.GetInt64(17),
                MirrorOf = reader.IsDBNull(18) ? null : reader.GetString(18),
                IsAggregate = reader.GetInt64(19) != 0,
                Provider = reader.IsDBNull(20) ? null : reader.GetString(20),
                Preference = (int)reader.GetInt64(21),
                SourceData = reader.IsDBNull(22) ? null : reader.GetString(22)
            });
        }

        return result;
    }

    public DateTimeOffset? LastSync()
    {
        using var connection = Open();
        return long.TryParse(GetMeta(connection, "last_sync"), out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : null;
    }

    /// <summary>Usuwa wszystkie wpisy (pełna przebudowa przy następnej synchronizacji).</summary>
    public void Reset()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Entries; DELETE FROM SourceFiles; DELETE FROM Meta WHERE key LIKE 'parser:%';";
        command.ExecuteNonQuery();
    }

    public (int Files, long Entries) Statistics()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM SourceFiles), (SELECT COUNT(*) FROM Entries)";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    private void ReplaceFile(SqliteConnection connection, string source, SourceFile file, List<UsageEntry> entries, int skipped)
    {
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Entries WHERE file_path = $path";
            delete.Parameters.AddWithValue("$path", file.Path);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Entries(file_path, source, ts, session_id, project, project_path, model, fallback_model,
                    input, output, cache_create, cache_create_1h, cache_read, reasoning, recorded_cost, speed, credits,
                    dedupe_key, is_summary, mirror_of, aggregate, provider, preference, source_data)
                VALUES ($path, $source, $ts, $session, $project, $projectPath, $model, $fallback,
                    $input, $output, $cc, $cc1h, $cr, $reasoning, $cost, $speed, $credits, $dedupe, $summary, $mirror, $aggregate, $provider, $preference, $sourceData)
                """;
            var parameters = new[]
            {
                "$path", "$source", "$ts", "$session", "$project", "$projectPath", "$model", "$fallback", "$input",
                "$output", "$cc", "$cc1h", "$cr", "$reasoning", "$cost", "$speed", "$credits", "$dedupe", "$summary", "$mirror", "$aggregate", "$provider", "$preference", "$sourceData"
            }.ToDictionary(name => name, name => insert.Parameters.Add(name, SqliteType.Text));
            insert.Prepare();

            foreach (var entry in entries)
            {
                parameters["$path"].Value = file.Path;
                parameters["$source"].Value = source;
                parameters["$ts"].Value = entry.TimestampMs;
                parameters["$session"].Value = entry.SessionId;
                parameters["$project"].Value = (object?)entry.Project ?? DBNull.Value;
                parameters["$projectPath"].Value = (object?)entry.ProjectPath ?? DBNull.Value;
                parameters["$model"].Value = (object?)entry.Model ?? DBNull.Value;
                parameters["$fallback"].Value = entry.IsFallbackModel ? 1 : 0;
                parameters["$input"].Value = entry.InputTokens;
                parameters["$output"].Value = entry.OutputTokens;
                parameters["$cc"].Value = entry.CacheCreationTokens;
                parameters["$cc1h"].Value = entry.CacheCreation1hTokens;
                parameters["$cr"].Value = entry.CacheReadTokens;
                parameters["$reasoning"].Value = entry.ReasoningTokens;
                parameters["$cost"].Value = (object?)entry.RecordedCostUsd ?? DBNull.Value;
                parameters["$speed"].Value = (int)entry.Speed;
                parameters["$credits"].Value = (object?)entry.Credits ?? DBNull.Value;
                parameters["$dedupe"].Value = (object?)entry.DedupeKey ?? DBNull.Value;
                parameters["$summary"].Value = entry.SessionTier;
                parameters["$mirror"].Value = (object?)entry.MirrorOf ?? DBNull.Value;
                parameters["$aggregate"].Value = entry.IsAggregate ? 1 : 0;
                parameters["$provider"].Value = (object?)entry.Provider ?? DBNull.Value;
                parameters["$preference"].Value = entry.Preference;
                parameters["$sourceData"].Value = (object?)entry.SourceData ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO SourceFiles(path, source, size, mtime_ms, parsed_at, skipped_lines, entries)
                VALUES ($path, $source, $size, $mtime, $parsed, $skipped, $entries)
                ON CONFLICT(path) DO UPDATE SET source = excluded.source, size = excluded.size, mtime_ms = excluded.mtime_ms,
                    parsed_at = excluded.parsed_at, skipped_lines = excluded.skipped_lines, entries = excluded.entries
                """;
            upsert.Parameters.AddWithValue("$path", file.Path);
            upsert.Parameters.AddWithValue("$source", source);
            upsert.Parameters.AddWithValue("$size", file.Size);
            upsert.Parameters.AddWithValue("$mtime", file.ModifiedMs);
            upsert.Parameters.AddWithValue("$parsed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            upsert.Parameters.AddWithValue("$skipped", skipped);
            upsert.Parameters.AddWithValue("$entries", entries.Count);
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private readonly record struct KnownFile(long Size, long ModifiedMs, int Skipped);

    private static Dictionary<string, KnownFile> LoadKnownFiles(SqliteConnection connection, string source)
    {
        var result = new Dictionary<string, KnownFile>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, size, mtime_ms, skipped_lines FROM SourceFiles WHERE source = $source";
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new KnownFile(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3));
        }

        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_initialized)
        {
            Initialize(connection);
            _initialized = true;
        }

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 30000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS SourceFiles(
                path TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                size INTEGER NOT NULL,
                mtime_ms INTEGER NOT NULL,
                parsed_at INTEGER NOT NULL,
                skipped_lines INTEGER NOT NULL DEFAULT 0,
                entries INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Entries(
                id INTEGER PRIMARY KEY,
                file_path TEXT NOT NULL,
                source TEXT NOT NULL,
                ts INTEGER NOT NULL,
                session_id TEXT,
                project TEXT,
                project_path TEXT,
                model TEXT,
                fallback_model INTEGER NOT NULL DEFAULT 0,
                input INTEGER NOT NULL,
                output INTEGER NOT NULL,
                cache_create INTEGER NOT NULL,
                cache_create_1h INTEGER NOT NULL,
                cache_read INTEGER NOT NULL,
                reasoning INTEGER NOT NULL,
                recorded_cost REAL,
                speed INTEGER NOT NULL DEFAULT 0,
                credits REAL,
                dedupe_key TEXT,
                is_summary INTEGER NOT NULL DEFAULT 0,
                mirror_of TEXT,
                aggregate INTEGER NOT NULL DEFAULT 0,
                provider TEXT,
                preference INTEGER NOT NULL DEFAULT 0,
                source_data TEXT);
            CREATE INDEX IF NOT EXISTS IX_Entries_ts ON Entries(ts);
            CREATE INDEX IF NOT EXISTS IX_Entries_file ON Entries(file_path);
            CREATE INDEX IF NOT EXISTS IX_SourceFiles_source ON SourceFiles(source);
            """;
        command.ExecuteNonQuery();

        // Migracje: kolumny dodane po pierwszej wersji schematu (mirror_of — 2, aggregate — 3, provider i preference — 4,
        // source_data — 5; źródła, które z niego korzystają, mają podniesioną wersję parsera, więc ich wpisy są wczytywane ponownie).
        (string Column, string Definition)[] addedColumns =
        [
            ("mirror_of", "TEXT"),
            ("aggregate", "INTEGER NOT NULL DEFAULT 0"),
            ("provider", "TEXT"),
            ("preference", "INTEGER NOT NULL DEFAULT 0"),
            ("source_data", "TEXT")
        ];
        foreach (var (column, definition) in addedColumns)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Entries') WHERE name = $column";
            check.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Entries ADD COLUMN {column} {definition}";
                alter.ExecuteNonQuery();
            }
        }

        if (GetMeta(connection, "schema") != SchemaVersion.ToString())
        {
            SetMeta(connection, "schema", SchemaVersion.ToString());
        }
    }

    private static string? GetMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM Meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
