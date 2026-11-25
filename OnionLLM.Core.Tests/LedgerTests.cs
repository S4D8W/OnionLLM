using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class LedgerTests
{
    [Fact]
    public void Changed_file_is_reparsed_and_deleted_file_keeps_history()
    {
        using var directory = new TempDirectory();
        var file = directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m1", "r1", 1, 1));
        var config = TestData.ConfigWith("claude", directory.Path);
        config.Sources["codex"] = new SourceSettings { Enabled = false };
        config.Sources["copilot"] = new SourceSettings { Enabled = false };
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        IUsageSource[] sources = [new ClaudeSource()];

        ledger.Sync(sources, config);
        Assert.Single(ledger.Query());

        File.AppendAllText(file, "\n" + TestData.ClaudeLine("2026-11-24T10:05:00Z", "m2", "r2", 1, 1));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        var report = ledger.Sync(sources, config);
        Assert.Equal(1, report.Sources[0].ParsedFiles);
        Assert.Equal(2, ledger.Query().Count);

        var unchanged = ledger.Sync(sources, config);
        Assert.Equal(0, unchanged.Sources[0].ParsedFiles);

        File.Delete(file);
        ledger.Sync(sources, config);
        Assert.Equal(2, ledger.Query().Count);
    }

    [Fact]
    public void Source_data_survives_the_ledger_round_trip()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m1", "r1", 1, 1));
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        var parsed = Assert.Single(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)));

        ledger.Sync([new ClaudeSource()], TestData.ConfigWith("claude", directory.Path));
        var stored = Assert.Single(ledger.Query());

        Assert.NotNull(parsed.SourceData);
        Assert.Equal(parsed.DedupeKey, stored.DedupeKey);
        Assert.Equal(parsed.SourceData, stored.SourceData);
        Assert.DoesNotContain('\u001F', stored.SourceData!);
    }

    [Fact]
    public void Ledger_from_schema_4_gains_the_source_data_column()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "ledger.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Entries(id INTEGER PRIMARY KEY, file_path TEXT NOT NULL, source TEXT NOT NULL, ts INTEGER NOT NULL,
                    session_id TEXT, project TEXT, project_path TEXT, model TEXT, fallback_model INTEGER NOT NULL DEFAULT 0,
                    input INTEGER NOT NULL, output INTEGER NOT NULL, cache_create INTEGER NOT NULL, cache_create_1h INTEGER NOT NULL,
                    cache_read INTEGER NOT NULL, reasoning INTEGER NOT NULL, recorded_cost REAL, speed INTEGER NOT NULL DEFAULT 0,
                    credits REAL, dedupe_key TEXT, is_summary INTEGER NOT NULL DEFAULT 0, mirror_of TEXT,
                    aggregate INTEGER NOT NULL DEFAULT 0, provider TEXT, preference INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Entries(file_path, source, ts, input, output, cache_create, cache_create_1h, cache_read, reasoning, dedupe_key)
                VALUES ('a.jsonl', 'gemini', 1, 1, 1, 0, 0, 0, 0, 'k');
                """;
            command.ExecuteNonQuery();
        }

        var entry = Assert.Single(new UsageLedger(path).Query());

        Assert.Equal("k", entry.DedupeKey);
        Assert.Null(entry.SourceData);
    }
}
