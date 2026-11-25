using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class CopilotSourceTests
{
    [Fact]
    public void Session_shutdown_subtracts_cache_from_input()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/sess-1/events.jsonl", string.Join('\n',
            "{\"type\":\"session.start\",\"data\":{\"context\":{\"cwd\":\"C:\\\\repo\\\\web\"}},\"timestamp\":\"2026-11-24T09:00:00Z\"}",
            "{\"type\":\"session.shutdown\",\"data\":{\"modelMetrics\":{\"claude-sonnet-4.6\":{\"usage\":{\"inputTokens\":140809,\"outputTokens\":2302,\"cacheReadTokens\":109840,\"cacheWriteTokens\":0}}}},\"timestamp\":\"2026-11-24T09:38:49Z\"}"));

        var entry = Assert.Single(TestData.ParseAll(new CopilotSource(), TestData.ConfigWith("copilot", directory.Path)));

        Assert.Equal(30969, entry.InputTokens);
        Assert.Equal(109840, entry.CacheReadTokens);
        Assert.True(entry.SpansMultipleRequests); // brak liczby żądań → przedział bez progu długiego kontekstu
        Assert.Equal(1, entry.SessionTier);
        Assert.Equal("web", entry.Project);
    }

    [Fact]
    public void Summary_is_dropped_when_detailed_entries_exist_for_the_session()
    {
        var detailed = TestData.Entry("2026-11-24T09:00:00Z", 10, 1, source: "copilot", session: "s") with { DedupeKey = "a" };
        var summary = TestData.Entry("2026-11-24T09:10:00Z", 10, 1, source: "copilot", session: "s") with { IsSessionSummary = true, DedupeKey = "b" };
        var otherSummary = TestData.Entry("2026-11-24T09:10:00Z", 10, 1, source: "copilot", session: "t") with { IsSessionSummary = true, DedupeKey = "c" };

        var result = Deduplicator.Apply([detailed, summary, otherSummary]);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(summary, result);
    }

    [Fact]
    public void Database_rows_are_parsed_with_cost_from_nano_aiu()
    {
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "session-store.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions(id TEXT PRIMARY KEY, cwd TEXT);
                CREATE TABLE assistant_usage_events(id INTEGER PRIMARY KEY, session_id TEXT, model TEXT, input_tokens INTEGER, output_tokens INTEGER,
                    cache_read_tokens INTEGER, cache_write_tokens INTEGER, reasoning_tokens INTEGER, total_nano_aiu INTEGER, created_at TEXT);
                INSERT INTO sessions VALUES('s1', 'C:\repo\api');
                INSERT INTO assistant_usage_events VALUES(1, 's1', 'gpt-5.4-mini', 30072, 156, 29696, 0, 38, 321120000, '2026-10-12T07:34:23.592Z');
                """;
            command.ExecuteNonQuery();
        }

        var entry = Assert.Single(TestData.ParseAll(new CopilotSource(), TestData.ConfigWith("copilot", directory.Path)));

        Assert.Equal(376, entry.InputTokens);
        Assert.Equal(29696, entry.CacheReadTokens);
        Assert.Equal(0.0032112, entry.RecordedCostUsd!.Value, 7);
        Assert.Equal("api", entry.Project);
    }
    [Fact]
    public void Otel_exporter_file_from_the_environment_is_read_even_outside_the_copilot_home()
    {
        using var home = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var exporter = elsewhere.Write("copilot-otel.jsonl", CopilotFixture.OtelCall("span-1", "2026-01-02T09:00:00Z", input: 42));
        home.Write("otel/" + "trace.jsonl", CopilotFixture.OtelCall("span-2", "2026-01-02T10:00:00Z", input: 7));

        var entries = CopilotFixture.Load(home, () => exporter);

        Assert.Equal([42L, 7L], entries.Select(entry => entry.InputTokens));
        Assert.Single(CopilotFixture.Load(home, () => Path.Combine(elsewhere.Path, "missing.jsonl")));
    }

    [Fact]
    public void Only_direct_session_state_directories_are_scanned()
    {
        using var directory = new TempDirectory();
        var shutdown = CopilotFixture.ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 1);
        directory.Write("session-state/session-a/events.jsonl", shutdown);
        directory.Write("session-state/session-a/other.jsonl", shutdown);
        directory.Write("session-state/nested/session-c/events.jsonl", shutdown);

        var files = new CopilotSource(() => null).EnumerateFiles(TestData.ConfigWith("copilot", directory.Path)).ToList();

        Assert.Equal(Path.Combine(directory.Path, "session-state", "session-a", "events.jsonl"), Assert.Single(files).Path);
    }
}
