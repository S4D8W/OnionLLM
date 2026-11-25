using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class HermesSourceTests
{
    private static void CreateStateDatabase(string path, params object?[][] rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT, started_at REAL NOT NULL,
                    message_count INTEGER DEFAULT 0, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0,
                    cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0, reasoning_tokens INTEGER DEFAULT 0,
                    billing_provider TEXT, estimated_cost_usd REAL, actual_cost_usd REAL)
                """;
            create.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions (id, source, model, started_at, message_count, input_tokens, output_tokens,
                    cache_read_tokens, cache_write_tokens, reasoning_tokens, billing_provider, estimated_cost_usd, actual_cost_usd)
                VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11, $p12)
                """;
            for (var i = 0; i < row.Length; i++)
            {
                insert.Parameters.AddWithValue("$p" + i, row[i] ?? DBNull.Value);
            }

            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Session_rows_become_summary_entries_with_recorded_cost()
    {
        using var directory = new TempDirectory();
        CreateStateDatabase(System.IO.Path.Combine(directory.Path, "state.db"),
            ["s1", "cli", "claude-sonnet-4-5", 1767312000.5, 4, 1000, 500, 2000, 300, 120, "anthropic", 0.5, 0.42],
            ["s2", "telegram", "gpt-5", 1767312100.0, 2, 10, 5, 0, 0, 0, null, 0.01, null],
            ["empty", "cli", "gpt-5", 1767312100.0, 0, 0, 0, 0, 0, 0, null, null, null],
            ["no-model", "cli", "  ", 1767312100.0, 1, 10, 5, 0, 0, 0, null, null, null]);

        var entries = TestData.ParseAll(new HermesSource(), TestData.ConfigWith(HermesSource.SourceId, directory.Path))
            .OrderBy(entry => entry.SessionId).ToList();

        Assert.Equal(2, entries.Count);
        var first = entries[0];
        Assert.Equal("s1", first.SessionId);
        Assert.Equal("cli", first.Project);
        Assert.Equal("claude-sonnet-4-5", first.Model);
        Assert.Equal(1767312000500, first.TimestampMs);
        Assert.Equal(1000, first.InputTokens);
        Assert.Equal(500, first.OutputTokens);
        Assert.Equal(2000, first.CacheReadTokens);
        Assert.Equal(300, first.CacheCreationTokens);
        Assert.Equal(120, first.ReasoningTokens);
        Assert.Equal(0.42, first.RecordedCostUsd);
        Assert.True(first.IsSessionSummary);
        Assert.Equal(0.01, entries[1].RecordedCostUsd); // brak actual → szacunek
    }

    [Fact]
    public void Same_session_in_two_homes_is_counted_once()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        object?[] row = ["s1", "cli", "gpt-5", 1767312000.0, 1, 10, 5, 0, 0, 0, null, null, null];
        CreateStateDatabase(System.IO.Path.Combine(first.Path, "state.db"), row);
        CreateStateDatabase(System.IO.Path.Combine(second.Path, "state.db"), row);
        var config = TestData.ConfigWith(HermesSource.SourceId, first.Path);
        config.Sources[HermesSource.SourceId].Paths.Add(second.Path);

        var entries = Deduplicator.Apply(TestData.ParseAll(new HermesSource(), config));

        Assert.Single(entries);
    }
}
