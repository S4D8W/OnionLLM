using System.Text.Json;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class GrokSourceTests
{
    private static List<Models.UsageEntry> Load(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new GrokSource(), TestData.ConfigWith(GrokSource.SourceId, directory.Path), diagnostics);

    private static string Turn(string? eventId, string model, long input, long output, long cache, long reasoning,
        long seconds, long? agentMs = null, long ticks = 0, long cacheCreation = 0)
    {
        var meta = new Dictionary<string, object>();
        if (eventId is not null)
        {
            meta["eventId"] = eventId;
        }

        if (agentMs is not null)
        {
            meta["agentTimestampMs"] = agentMs.Value;
        }

        var usage = new Dictionary<string, object>
        {
            ["inputTokens"] = input,
            ["outputTokens"] = output,
            ["cachedReadTokens"] = cache,
            ["cacheCreationTokens"] = cacheCreation,
            ["reasoningTokens"] = reasoning,
            ["costUsdTicks"] = ticks
        };
        return JsonSerializer.Serialize(new
        {
            timestamp = seconds,
            @params = new
            {
                sessionId = "sess-1",
                update = new { sessionUpdate = "turn_completed", usage = new Dictionary<string, object>(usage) { ["modelUsage"] = new Dictionary<string, object> { [model] = usage } } },
                _meta = meta
            }
        });
    }

    [Fact]
    public void Turns_are_aggregates_and_flag_survives_the_ledger()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/C%3A%5Cproj/sess-1/updates.jsonl",
            Turn("evt-1", "grok-4.5-build", 300_000, 20, 0, 0, 1750000000));
        var config = TestData.ConfigWith(GrokSource.SourceId, directory.Path);

        Assert.True(Assert.Single(Load(directory)).IsAggregate);

        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        ledger.Sync([new GrokSource()], config);
        var stored = Assert.Single(ledger.Query());
        Assert.True(stored.IsAggregate);
        Assert.True(stored.SpansMultipleRequests);
        Assert.False(stored.IsSessionSummary);
    }

    [Fact]
    public void Cache_is_carved_out_of_input_and_reasoning_stays_in_output()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/C%3A%5Cproj/sess-1/updates.jsonl", string.Join('\n',
            """{"timestamp":1750000000,"params":{"sessionId":"sess-1","update":{"sessionUpdate":"agent_message_chunk"}}}""",
            Turn("evt-1", "grok-4.5-build", 100, 20, 40, 10, 1750000000, ticks: 25_000_000, cacheCreation: 15)));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("grok-4.5-build", entry.Model);
        Assert.Equal("sess-1", entry.SessionId);
        Assert.Equal(@"C:\proj", entry.ProjectPath);
        Assert.Equal(45, entry.InputTokens);
        Assert.Equal(40, entry.CacheReadTokens);
        Assert.Equal(15, entry.CacheCreationTokens);
        Assert.Equal(20, entry.OutputTokens);
        Assert.Equal(10, entry.ReasoningTokens);
        Assert.Equal(0.0025, entry.RecordedCostUsd!.Value, 12);
        Assert.Equal(1750000000000, entry.TimestampMs);
    }

    [Fact]
    public void Agent_timestamp_ms_wins_and_summary_overrides_metadata()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/proj/sess-1/updates.jsonl",
            """{"timestamp":1750000000,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":10,"outputTokens":1}},"_meta":{"agentTimestampMs":1750000000123}}}""");
        directory.Write("sessions/proj/sess-1/summary.json",
            """{"info":{"id":"canonical-session","cwd":"D:\\canonical\\cwd"},"git_root_dir":"should-not-win","current_model_id":"grok-code-fast-1"}""");

        var entry = Assert.Single(Load(directory));

        Assert.Equal(1750000000123, entry.TimestampMs);
        Assert.Equal("canonical-session", entry.SessionId);
        Assert.Equal(@"D:\canonical\cwd", entry.ProjectPath);
        Assert.Equal("grok-code-fast-1", entry.Model);
        Assert.Null(entry.RecordedCostUsd);
    }

    [Fact]
    public void Event_exported_to_two_sessions_is_counted_once()
    {
        using var directory = new TempDirectory();
        var line = Turn("evt-shared", "grok-4.5-build", 100, 20, 0, 0, 1750000000);
        directory.Write("sessions/proj/sess-1/updates.jsonl", line + "\n" + line);
        directory.Write("sessions/proj/sess-2/updates.jsonl", line);

        var entries = Load(directory);

        Assert.Equal(2, entries.Count); // w pliku duplikat odrzuca parser
        Assert.Single(Deduplicator.Apply(entries));
    }

    [Fact]
    public void Turns_without_timestamp_are_diagnosed_and_other_files_ignored()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/proj/sess-1/updates.jsonl",
            """{"params":{"update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":10,"outputTokens":1}}}}""");
        directory.Write("sessions/proj/sess-1/events.jsonl", Turn("e", "grok-4", 1, 1, 0, 0, 1750000000));
        directory.Write("logs/unified.jsonl", Turn("e2", "grok-4", 1, 1, 0, 0, 1750000000));
        var diagnostics = new ParseDiagnostics();

        Assert.Empty(Load(directory, diagnostics));
        Assert.Equal(1, diagnostics.SkippedLines);
    }
}
