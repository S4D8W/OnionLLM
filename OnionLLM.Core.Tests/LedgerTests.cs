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
    public void Key_prefix_query_returns_only_matching_entries_outside_the_range()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", string.Join('\n',
            TestData.ClaudeLine("2026-05-19T10:00:00Z", "m1", "r1", 1, 1),
            TestData.ClaudeLine("2026-05-19T11:00:00Z", "m10", "r10", 1, 1),
            TestData.ClaudeLine("2026-05-23T10:00:00Z", "m1", "r1b", 1, 1),
            TestData.ClaudeLine("2026-05-26T10:00:00Z", "m1", "r1c", 1, 1)));
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        ledger.Sync([new ClaudeSource()], TestData.ConfigWith("claude", directory.Path));
        var prefix = ClaudeDedupeKey.MessagePrefix(ledger.Query().First(entry => entry.TimestampMs == Ms("2026-05-23T10:00:00Z")))!;

        // „m1␟” nie obejmuje „m10␟”; wpis z zakresu [05-22, 05-24) nie jest zwracany.
        var outside = ledger.QueryKeyPrefixesOutside("claude", [prefix], Ms("2026-05-22T00:00:00Z"), Ms("2026-05-24T00:00:00Z"));

        Assert.Equal([Ms("2026-05-19T10:00:00Z"), Ms("2026-05-26T10:00:00Z")], outside.Select(entry => entry.TimestampMs));
        Assert.Empty(ledger.QueryKeyPrefixesOutside("codex", [prefix], null, null));
    }

    private static long Ms(string timestamp) =>
        DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

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
    public void Source_statistics_count_files_entries_and_last_entry()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m1", "r1", 1, 1) + "\n" +
            TestData.ClaudeLine("2026-11-24T11:30:00Z", "m2", "r2", 1, 1));
        var config = TestData.ConfigWith("claude", directory.Path);
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        ledger.Sync([new ClaudeSource()], config);

        var statistics = ledger.SourceStatistics();

        var claude = statistics["claude"];
        Assert.Equal(1, claude.Files);
        Assert.Equal(2, claude.Entries);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T11:30:00Z"), claude.LastEntry);
        Assert.False(statistics.ContainsKey("codex"));
    }

    [Fact]
    public void Sync_reports_progress_per_file_and_source()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m1", "r1", 1, 1));
        directory.Write("projects/demo/b.jsonl", TestData.ClaudeLine("2026-11-24T11:00:00Z", "m2", "r2", 1, 1));
        var config = TestData.ConfigWith("claude", directory.Path);
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        var reports = new Collector();

        ledger.Sync([new ClaudeSource()], config, progress: reports);

        Assert.Equal(new SyncProgress("claude", 0, 1, 0, 2, 0), reports.Items[0]);
        Assert.Equal(new SyncProgress("claude", 0, 1, 2, 2, 2), reports.Items[^1]);
        Assert.Contains(reports.Items, item => item.Files == 1 && item.Entries == 1);
    }

    private sealed class Collector : IProgress<SyncProgress>
    {
        public List<SyncProgress> Items { get; } = [];

        public void Report(SyncProgress value) => Items.Add(value);
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
