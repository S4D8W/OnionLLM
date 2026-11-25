using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

public class GooseSourceTests
{
    private static void CreateDatabase(string path, string inserts, bool withWorkingDir = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        // Schemat jak w testach adaptera goose w ccusage (+ working_dir z prawdziwej bazy Goose).
        command.CommandText = $"""
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, model_config_json TEXT, provider_name TEXT, created_at TEXT,
                total_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER,
                accumulated_total_tokens INTEGER, accumulated_input_tokens INTEGER, accumulated_output_tokens INTEGER
                {(withWorkingDir ? ", working_dir TEXT" : "")});
            {inserts}
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Accumulated_tokens_are_read_per_session()
    {
        using var directory = new TempDirectory();
        CreateDatabase(Path.Combine(directory.Path, "sessions.db"), """
            INSERT INTO sessions VALUES ('session-a', '{"model_name":"claude-sonnet-4-20250514"}', 'anthropic', '2026-05-01 01:02:03',
                10, 5, 5, 180, 100, 50, 'C:\work\goose-demo');
            INSERT INTO sessions VALUES ('session-b', '{"model_name":"gpt-5"}', NULL, '1767312000', 30, 20, 10, 0, NULL, NULL, NULL);
            INSERT INTO sessions VALUES ('empty', '{"model_name":"gpt-5"}', NULL, '2026-05-01 01:02:03', 0, 0, 0, 0, 0, 0, NULL);
            INSERT INTO sessions VALUES ('no-config', NULL, NULL, '2026-05-01 01:02:03', 9, 9, 0, 9, 9, 0, NULL);
            """);

        var entries = TestData.ParseAll(new GooseSource(), TestData.ConfigWith("goose", directory.Path));

        Assert.Equal(2, entries.Count);
        var a = entries.Single(entry => entry.SessionId == "session-a");
        Assert.Equal("claude-sonnet-4-20250514", a.Model);
        Assert.Equal(100, a.InputTokens);
        Assert.Equal(50, a.OutputTokens);
        Assert.Equal(0, a.CacheReadTokens);
        Assert.True(a.IsSessionSummary);
        Assert.Equal("goose-demo", a.Project);
        Assert.Equal(DateTimeOffset.Parse("2026-05-01T01:02:03Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), a.TimestampMs);

        // Brak liczników skumulowanych → kolumny bieżące; created_at jako sekundy epoki.
        var b = entries.Single(entry => entry.SessionId == "session-b");
        Assert.Equal(20, b.InputTokens);
        Assert.Equal(10, b.OutputTokens);
        Assert.Equal(1767312000000, b.TimestampMs);
    }

    [Fact]
    public void Rows_without_model_or_timestamp_are_diagnosed()
    {
        using var directory = new TempDirectory();
        CreateDatabase(Path.Combine(directory.Path, "data", "sessions", "sessions.db"), """
            INSERT INTO sessions VALUES ('healthy', '{"model_name":"gpt-5"}', NULL, '2026-05-01T01:02:03Z', NULL, NULL, NULL, 15, 10, 5);
            INSERT INTO sessions VALUES ('bad-time', '{"model_name":"gpt-5"}', NULL, 'yesterday', NULL, NULL, NULL, 15, 10, 5);
            INSERT INTO sessions VALUES ('no-model', '{"provider":"x"}', NULL, '2026-05-01T01:02:03Z', NULL, NULL, NULL, 15, 10, 5);
            """, withWorkingDir: false);
        var diagnostics = new ParseDiagnostics();

        // Katalog w stylu GOOSE_PATH_ROOT (baza w data/sessions).
        var entry = Assert.Single(TestData.ParseAll(new GooseSource(), TestData.ConfigWith("goose", directory.Path), diagnostics));

        Assert.Equal("healthy", entry.SessionId);
        Assert.Null(entry.ProjectPath);
        Assert.Equal(2, diagnostics.SkippedLines);
    }

    [Fact]
    public void Goose_path_root_is_a_single_path_with_data_sessions_suffix()
    {
        const string variable = "GOOSE_PATH_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, @"C:\goose,custom");
            var roots = new GooseSource().ResolveRoots(new Config.OnionConfig());
            Assert.Equal([Path.Combine(@"C:\goose,custom", "data", "sessions")], roots);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
