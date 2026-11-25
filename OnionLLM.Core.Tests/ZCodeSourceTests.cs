using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class ZCodeSourceTests
{
    private static string CreateDatabase(string root, bool withCacheColumns = true)
    {
        var path = System.IO.Path.Combine(root, "cli", "db", "db.sqlite");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var create = connection.CreateCommand();
        var cache = withCacheColumns ? "cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER, computed_total_tokens INTEGER, provider_id TEXT," : "";
        create.CommandText = $"""
            CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT, version TEXT);
            CREATE TABLE model_usage (id TEXT PRIMARY KEY, session_id TEXT, started_at INTEGER, model_id TEXT, status TEXT,
                input_tokens INTEGER, output_tokens INTEGER, {cache} prompt TEXT);
            INSERT INTO session VALUES ('session-1', '/workspace/project', '0.16.3');
            """;
        create.ExecuteNonQuery();
        return path;
    }

    private static void Insert(string path, string id, string status, long input, long output, long cacheCreation, long cacheRead,
        string model = "GLM-5.3", long startedAt = 1_786_909_042_666)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO model_usage (id, session_id, started_at, model_id, status, input_tokens, output_tokens,
                cache_creation_input_tokens, cache_read_input_tokens, computed_total_tokens, provider_id)
            VALUES ($id, 'session-1', $started, $model, $status, $input, $output, $cw, $cr, $input + $output, 'builtin:zai-coding-plan')
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$started", startedAt);
        insert.Parameters.AddWithValue("$model", model);
        insert.Parameters.AddWithValue("$status", status);
        insert.Parameters.AddWithValue("$input", input);
        insert.Parameters.AddWithValue("$output", output);
        insert.Parameters.AddWithValue("$cw", cacheCreation);
        insert.Parameters.AddWithValue("$cr", cacheRead);
        insert.ExecuteNonQuery();
    }

    [Fact]
    public void Completed_rows_are_read_and_inclusive_cache_is_subtracted()
    {
        using var directory = new TempDirectory();
        var database = CreateDatabase(directory.Path);
        Insert(database, "usage-1", "completed", 100, 10, 15, 25);
        Insert(database, "usage-2", "failed", 100, 10, 0, 0);
        Insert(database, "usage-3", "completed", 0, 0, 0, 0);

        var entry = Assert.Single(TestData.ParseAll(new ZCodeSource(), TestData.ConfigWith(ZCodeSource.SourceId, directory.Path)));

        Assert.Equal(60, entry.InputTokens);
        Assert.Equal(15, entry.CacheCreationTokens);
        Assert.Equal(25, entry.CacheReadTokens);
        Assert.Equal(10, entry.OutputTokens);
        Assert.Equal("GLM-5.3", entry.Model);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Equal("/workspace/project", entry.ProjectPath);
        Assert.Equal("project", entry.Project);
        Assert.Equal(1_786_909_042_666, entry.TimestampMs);
        Assert.Equal("usage-1", entry.DedupeKey);
    }

    [Fact]
    public void Legacy_schema_without_cache_columns_is_supported()
    {
        using var directory = new TempDirectory();
        var database = CreateDatabase(directory.Path, withCacheColumns: false);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO model_usage (id, session_id, started_at, model_id, status, input_tokens, output_tokens) VALUES ('u', 'session-1', 1786909042666, 'glm-4.6', 'completed', 50, 5)";
            insert.ExecuteNonQuery();
        }

        var entry = Assert.Single(TestData.ParseAll(new ZCodeSource(), TestData.ConfigWith(ZCodeSource.SourceId, directory.Path)));

        Assert.Equal(50, entry.InputTokens);
        Assert.Equal(0, entry.CacheReadTokens);
    }

    [Fact]
    public void Incompatible_schema_is_skipped()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "cli", "db", "db.sqlite");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE model_usage (id TEXT)";
            create.ExecuteNonQuery();
        }

        var diagnostics = new ParseDiagnostics();
        Assert.Empty(TestData.ParseAll(new ZCodeSource(), TestData.ConfigWith(ZCodeSource.SourceId, directory.Path), diagnostics));
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Same_usage_id_in_two_roots_is_counted_once()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        Insert(CreateDatabase(first.Path), "usage-1", "completed", 100, 10, 0, 0);
        Insert(CreateDatabase(second.Path), "usage-1", "completed", 100, 10, 0, 0);
        var config = TestData.ConfigWith(ZCodeSource.SourceId, first.Path);
        config.Sources[ZCodeSource.SourceId].Paths.Add(second.Path);

        Assert.Single(Deduplicator.Apply(TestData.ParseAll(new ZCodeSource(), config)));
    }
}
