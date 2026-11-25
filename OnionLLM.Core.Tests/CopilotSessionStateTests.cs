using System.Text.Json.Nodes;
using OnionLLM.Core.Sources;
using static OnionLLM.Core.Tests.CopilotFixture;

namespace OnionLLM.Core.Tests;

/// <summary>
/// <c>session-state/&lt;id&gt;/events.jsonl</c>: usage_record, przedziały shutdown, kredyty — wg testów ccusage
/// <c>copilot/src/loader/tests.rs</c>, <c>parser/session_state_tests.rs</c> i fixture <c>tests/fixtures/session-state</c>.
/// </summary>
public class CopilotSessionStateTests
{
    // ccusage tests/fixtures/session-state/session-a/events.jsonl
    private const string ShutdownOnly = """
        not json
        {"type":"session.start","timestamp":"2026-01-02T01:20:00.000Z","data":{"prompt":"private prompt must not appear in reports"}}
        {"type":"session.shutdown","id":"shutdown-a-old","timestamp":"2026-01-02T01:20:00.000Z","data":{"modelMetrics":{"claude-opus-4.6-1m":{"usage":{"inputTokens":100,"outputTokens":50,"cacheReadTokens":10,"cacheWriteTokens":20,"reasoningTokens":5},"requests":{"count":1,"cost":999}}},"privatePrompt":"do not expose"}}
        {"type":"session.shutdown","id":"shutdown-a-latest","timestamp":"2026-01-03T01:20:00.000Z","data":{"modelMetrics":{"claude-opus-4.6-1m":{"usage":{"inputTokens":200,"outputTokens":80,"cacheReadTokens":20,"cacheWriteTokens":30,"reasoningTokens":10},"requests":{"count":3}}}}}
        """;

    // ccusage tests/fixtures/session-state/session-receipts/events.jsonl
    private const string ReceiptsOnly = """
        {"type":"session.start","id":"start-r","timestamp":"2026-02-05T08:59:00.000Z","parentId":null,"data":{"sessionId":"session-receipts"}}
        {"type":"session.usage_record","id":"record-r-1","timestamp":"2026-02-05T09:00:00.000Z","parentId":"start-r","data":{"usage":{"model":"claude-sonnet-4.6","interactionType":"conversation-agent","inputTokens":1200,"outputTokens":80,"cacheReadTokens":1000,"cacheWriteTokens":100,"reasoningTokens":20,"accounting":{"sourceSessionId":"session-receipts","sequence":1,"usageId":"call-r-1"},"apiCallId":"chatcmpl-r-1","serviceRequestId":"service-r-1"}}}
        {"type":"session.usage_checkpoint","id":"checkpoint-r-1","timestamp":"2026-02-05T09:00:01.000Z","parentId":"record-r-1","data":{"totalNanoAiu":4000000000,"usageAccountingWatermarks":{"session-receipts":1}}}
        {"type":"session.usage_record","id":"record-r-2","timestamp":"2026-02-05T09:05:00.000Z","parentId":"checkpoint-r-1","data":{"usage":{"model":"gpt-5.4","interactionType":"conversation-agent","inputTokens":300,"outputTokens":40,"cacheReadTokens":200,"cacheWriteTokens":0,"accounting":{"sourceSessionId":"session-receipts","sequence":2,"usageId":"call-r-2"},"apiCallId":"chatcmpl-r-2","serviceRequestId":"service-r-2"}}}
        {"type":"session.usage_record","id":"record-r-3","timestamp":"2026-02-06T10:00:00.000Z","parentId":"record-r-2","agentId":"agent-1","data":{"usage":{"model":"claude-sonnet-4.6-1m","interactionType":"conversation-subagent","inputTokens":500,"outputTokens":60,"cacheReadTokens":400,"cacheWriteTokens":50,"accounting":{"sourceSessionId":"session-receipts","sequence":3,"usageId":"call-r-3"},"apiCallId":"chatcmpl-r-3","serviceRequestId":"service-r-3"}}}
        {"type":"session.usage_checkpoint","id":"checkpoint-r-2","timestamp":"2026-02-06T10:00:01.000Z","parentId":"record-r-3","data":{"totalNanoAiu":9000000000,"usageAccountingWatermarks":{"session-receipts":3}}}
        """;

    // ccusage tests/fixtures/session-state/session-resumed-receipts/events.jsonl
    private const string ShutdownThenReceipts = """
        {"type":"session.shutdown","id":"shutdown-m-1","timestamp":"2026-02-07T09:00:00.000Z","parentId":null,"data":{"totalNanoAiu":2000000000,"modelMetrics":{"claude-sonnet-4.6":{"usage":{"inputTokens":500,"outputTokens":40,"cacheReadTokens":300,"cacheWriteTokens":100},"requests":{"count":2},"totalNanoAiu":2000000000}}}}
        {"type":"session.usage_record","id":"record-m-1","timestamp":"2026-02-08T23:00:00.000Z","parentId":"shutdown-m-1","data":{"usage":{"model":"claude-sonnet-4.6","interactionType":"conversation-agent","inputTokens":250,"outputTokens":30,"cacheReadTokens":200,"cacheWriteTokens":0,"accounting":{"sourceSessionId":"session-resumed-receipts","sequence":1,"usageId":"call-m-1"},"apiCallId":"chatcmpl-m-1","serviceRequestId":"service-m-1"}}}
        {"type":"session.usage_checkpoint","id":"checkpoint-m-1","timestamp":"2026-02-08T23:00:01.000Z","parentId":"record-m-1","data":{"totalNanoAiu":3000000000,"usageAccountingWatermarks":{"session-resumed-receipts":1}}}
        {"type":"session.shutdown","id":"shutdown-m-2","timestamp":"2026-02-09T01:00:00.000Z","parentId":"checkpoint-m-1","data":{"totalNanoAiu":3000000000,"usageAccountingWatermarks":{"session-resumed-receipts":1},"modelMetrics":{"claude-sonnet-4.6":{"usage":{"inputTokens":750,"outputTokens":70,"cacheReadTokens":500,"cacheWriteTokens":100},"requests":{"count":3},"totalNanoAiu":3000000000}}}}
        """;

    private static (string Timestamp, string? Model, long Input, long Output, long CacheWrite, long CacheRead) Tokens(Models.UsageEntry entry) =>
        (Row(entry).Timestamp, entry.Model, entry.InputTokens, entry.OutputTokens, entry.CacheCreationTokens, entry.CacheReadTokens);

    [Fact]
    public void Each_shutdown_is_an_interval_dated_by_its_own_timestamp()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-a/events.jsonl", ShutdownOnly);

        var entries = Load(directory);

        Assert.Equal(
        [
            ("2026-01-02T01:20:00", "claude-opus-4.6", 70L, 50L, 20L, 10L),
            ("2026-01-03T01:20:00", "claude-opus-4.6", 80L, 30L, 10L, 10L)
        ], entries.Select(Tokens));
        // ccusage loader.rs:184-189 (#2365): tylko przedział z jednym żądaniem może użyć progu długiego kontekstu.
        Assert.False(entries[0].SpansMultipleRequests);
        Assert.True(entries[1].SpansMultipleRequests);
        Assert.All(entries, entry => Assert.Equal(1, entry.SessionTier));
    }

    [Fact]
    public void Repeated_shutdown_lines_count_once_and_tokenless_intervals_with_credits_are_kept()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", null, 10, requests: 1),
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", null, 10, requests: 1),
            Shutdown("shutdown-2", "2026-01-02T11:00:00Z", null, 25, requests: 1)));

        var entries = Load(directory);

        Assert.Equal([150L, 0L], entries.Select(entry => entry.TotalTokens));
        Assert.Equal(0.15, entries[1].RecordedCostUsd!.Value, 9);
    }

    [Fact]
    public void Shutdown_with_requests_but_no_tokens_is_a_row()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl",
            """{"type":"session.shutdown","id":"s","timestamp":"2026-01-02T10:00:00Z","data":{"modelMetrics":{"gpt-5.4":{"usage":{"inputTokens":0},"requests":{"count":2}},"empty":{"usage":{"inputTokens":0}}}}}""");

        var entry = Assert.Single(Load(directory));

        Assert.Equal("gpt-5.4", entry.Model);
        Assert.Equal(0, entry.TotalTokens);
        Assert.Equal("2", CopilotKey.Field(entry, CopilotKey.Requests));
    }

    [Fact]
    public void Each_usage_record_is_one_call_at_its_own_time()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-receipts/events.jsonl", ReceiptsOnly);

        var entries = Load(directory);

        Assert.Equal(
        [
            ("2026-02-05T09:00:00", "claude-sonnet-4.6", 100L, 80L, 100L, 1000L),
            ("2026-02-05T09:05:00", "gpt-5.4", 100L, 40L, 0L, 200L),
            ("2026-02-06T10:00:00", "claude-sonnet-4.6", 50L, 60L, 50L, 400L)
        ], entries.Select(Tokens));
        Assert.All(entries, entry => Assert.False(entry.SpansMultipleRequests));
    }

    [Fact]
    public void Only_shutdowns_before_the_first_usage_record_are_counted()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-resumed-receipts/events.jsonl", ShutdownThenReceipts);

        var entries = Load(directory);

        Assert.Equal(
        [
            ("2026-02-07T09:00:00", "claude-sonnet-4.6", 100L, 40L, 100L, 300L),
            ("2026-02-08T23:00:00", "claude-sonnet-4.6", 50L, 30L, 0L, 200L)
        ], entries.Select(Tokens));
    }

    [Fact]
    public void Usage_record_delivered_twice_counts_once()
    {
        var duplicate = ReceiptsOnly.Split('\n').Single(line => line.Contains("\"id\":\"record-r-2\""))
            .Replace("\"id\":\"record-r-2\"", "\"id\":\"record-r-2-again\"");
        using var directory = new TempDirectory();
        directory.Write("session-state/session-receipts/events.jsonl", ReceiptsOnly + "\n" + duplicate);

        Assert.Equal(3, Load(directory).Count);
    }

    [Theory]
    [InlineData("a-parent", "b-child")]
    [InlineData("b-parent", "a-child")]
    public void Usage_record_in_parent_and_child_files_counts_once_for_its_source_session(string parent, string child)
    {
        using var directory = new TempDirectory();
        directory.Write($"session-state/{parent}/events.jsonl", UsageRecord("parent-event", "2026-01-02T10:00:00Z", Accounting(child, 7)));
        directory.Write($"session-state/{child}/events.jsonl", UsageRecord("child-event", "2026-01-02T10:00:00Z", Accounting(child, 7)));

        var entry = Assert.Single(Load(directory));

        Assert.Equal(child, entry.SessionId);
        Assert.Equal(100, entry.InputTokens);
    }

    [Fact]
    public void Usage_record_without_tokens_counts_as_a_call()
    {
        using var directory = new TempDirectory();
        var usage = Accounting("session-1", 1);
        usage["model"] = "gpt-5.4";
        usage["inputTokens"] = 0;
        directory.Write("session-state/session-1/events.jsonl", Lines(
            UsageRecord("record-1", "2026-02-05T09:00:00Z", usage),
            Checkpoint("checkpoint-1", "2026-02-05T09:00:01Z", 3),
            """{"type":"session.shutdown","id":"shutdown-1","timestamp":"2026-02-06T09:00:00Z","data":{"totalNanoAiu":3000000000,"modelMetrics":{"gpt-5.4":{"usage":{"inputTokens":0,"outputTokens":0},"requests":{"count":1}}}}}"""));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("gpt-5.4", entry.Model);
        Assert.Equal(0, entry.TotalTokens);
        Assert.Equal(CopilotKey.UsageRecordPrefix + "session-1:1", entry.DedupeKey);
    }

    [Fact]
    public void Usage_record_cost_comes_from_recorded_credits()
    {
        using var directory = new TempDirectory();
        var usage = Accounting("session-1", 1);
        usage["outputTokens"] = 10;
        usage["copilotUsage"] = new JsonObject { ["totalNanoAiu"] = 5 * NanoAiuPerCredit };
        directory.Write("session-state/session-1/events.jsonl", UsageRecord("record-1", "2026-02-05T09:00:00Z", usage));

        var entry = Assert.Single(Load(directory));

        Assert.Equal(0.05, entry.RecordedCostUsd!.Value, 9);
        Assert.Equal(5, entry.Credits!.Value, 9);
    }

    [Fact]
    public void Each_shutdown_interval_records_the_credits_it_adds()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", null, null, input: 100, output: 0, requests: 1),
            Shutdown("shutdown-2", "2026-01-03T10:00:00Z", null, 25, input: 300, output: 0, requests: 2),
            Shutdown("shutdown-3", "2026-01-04T10:00:00Z", null, 40, input: 600, output: 0, requests: 3)));

        var entries = Load(directory);

        // Przedział, w którym któraś migawka nie ma kredytów, wyceniany jest z tokenów (ccusage loader/tests.rs:2955).
        Assert.Equal(new double?[] { null, null, 0.15 }, entries.Select(entry => entry.RecordedCostUsd is { } cost ? Math.Round(cost, 9) : (double?)null));
        Assert.Equal([100L, 200L, 300L], entries.Select(entry => entry.InputTokens));
    }

    [Fact]
    public void Session_credits_missing_from_model_metrics_become_an_unknown_row()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Checkpoint("checkpoint-1", "2026-01-02T10:00:00Z", 51),
            Checkpoint("checkpoint-2", "2026-01-02T10:10:00Z", 73),
            Shutdown("shutdown-1", "2026-01-02T10:20:00Z", 93, 42)));

        var entries = Load(directory);

        Assert.Equal([("test-model", 150L, 0.42), ("unknown", 0L, 0.51)],
            entries.Select(entry => (entry.Model!, entry.TotalTokens, Math.Round(entry.RecordedCostUsd!.Value, 9))));
        Assert.True(entries[1].IsFallbackModel);
    }

    [Fact]
    public void Checkpoint_credits_are_reported_at_the_first_snapshot_where_they_persist()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", 10, 10),
            Checkpoint("checkpoint-1", "2026-01-02T11:00:00Z", 25),
            Checkpoint("checkpoint-2", "2026-01-03T11:00:00Z", 40)));
        directory.Write("session-state/session-2/events.jsonl", Checkpoint("checkpoint-3", "2026-01-03T12:00:00Z", 7));

        var rows = Load(directory).Select(entry => (Row(entry).Timestamp, entry.Model, Math.Round(entry.RecordedCostUsd!.Value, 9)));

        Assert.Equal(
        [
            ("2026-01-02T10:00:00", "test-model", 0.10),
            ("2026-01-02T11:00:00", "unknown", 0.15),
            ("2026-01-03T11:00:00", "unknown", 0.15),
            ("2026-01-03T12:00:00", "unknown", 0.07)
        ], rows);
    }

    [Fact]
    public void Credits_a_later_shutdown_attributes_are_not_counted_twice()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Checkpoint("checkpoint-1", "2026-01-02T09:00:00Z", 30),
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", 30, 30),
            Checkpoint("checkpoint-2", "2026-01-02T11:00:00Z", 50),
            Shutdown("shutdown-2", "2026-01-02T12:00:00Z", 50, 35),
            Shutdown("shutdown-3", "2026-01-02T13:00:00Z", 50, 50)));

        var rows = Load(directory).Select(entry => (entry.Model, entry.TotalTokens, Math.Round(entry.RecordedCostUsd!.Value, 9)));

        Assert.Equal([("test-model", 150L, 0.30), ("test-model", 0L, 0.05), ("test-model", 0L, 0.15)], rows);
    }

    [Fact]
    public void Sessions_without_checkpoints_or_with_incomplete_totals_get_no_unknown_row()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", 60, 40),
            Shutdown("shutdown-2", "2026-01-03T10:00:00Z", 25, 25)));
        directory.Write("session-state/session-2/events.jsonl", Lines(
            Checkpoint("checkpoint-1", "2026-01-02T09:00:00Z", 20),
            Shutdown("shutdown-1", "2026-01-02T10:00:00Z", 93, null),
            Checkpoint("checkpoint-2", "2026-01-02T11:00:00Z", 120)));

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Model == "unknown");
    }

    [Theory]
    [InlineData(5L, 2L, 0.20)]
    [InlineData(null, 2L, 0.0)]
    [InlineData(5L, 3L, 0.0)]
    public void Watermarked_receipts_are_reconciled_before_reporting_residual_credits(long? secondCredits, long watermark, double expectedGap)
    {
        string Receipt(string id, string timestamp, long sequence, long? credits)
        {
            var usage = Accounting("session-1", sequence);
            usage["apiCallId"] = id;
            usage["copilotUsage"] = new JsonObject { ["totalNanoAiu"] = credits * NanoAiuPerCredit };
            return UsageRecord(id, timestamp, usage);
        }

        var first = Receipt("response-a", "2026-01-02T09:00:00Z", 1, 10);
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("legacy", "2026-01-01T09:00:00Z", 4, 4),
            first,
            Checkpoint("first", "2026-01-02T10:00:00Z", 14, new JsonObject { ["session-1"] = 1 }),
            Receipt("response-b", "2026-01-03T09:00:00Z", 2, secondCredits),
            Checkpoint("second", "2026-01-03T10:00:00Z", 39, new JsonObject { ["session-1"] = watermark })));
        directory.Write("session-state/copy/events.jsonl", first);
        directory.Write("otel/otel.jsonl", OtelCall("span-1", "2026-01-02T09:00:00Z", attributes: ("gen_ai.response.id", "response-a")));

        var rows = Load(directory).Select(entry => (Row(entry).Timestamp, entry.Model, entry.TotalTokens, Math.Round(entry.RecordedCostUsd ?? 0, 9))).ToList();

        var expected = new List<(string, string?, long, double)>
        {
            ("2026-01-01T09:00:00", "test-model", 150, 0.04),
            ("2026-01-02T09:00:00", "test-model", 100, 0.10),
            ("2026-01-03T09:00:00", "test-model", 100, (secondCredits ?? 0) / 100.0)
        };
        if (expectedGap > 0)
        {
            expected.Add(("2026-01-03T10:00:00", "unknown", 0, expectedGap));
        }

        Assert.Equal(expected, rows);
    }

    [Fact]
    public void Model_ids_lose_one_million_context_suffixes_but_pricing_tries_the_logged_name_first()
    {
        Assert.Equal(("claude-opus-4.7", "claude-opus-4.7-1m-internal"), CopilotSource.ModelNames(" claude-opus-4.7-1m-internal "));
        Assert.Equal(("claude-opus-4.6", "claude-opus-4.6-1m"), CopilotSource.ModelNames("claude-opus-4.6-1m"));
        Assert.Equal(("gpt-5.4", null), CopilotSource.ModelNames("gpt-5.4"));

        using var directory = new TempDirectory();
        directory.Write("session-state/session-a/events.jsonl", ShutdownOnly);
        var source = new CopilotSource(() => null);
        var entry = Load(directory)[0];

        Assert.Equal(["claude-opus-4.6-1m", "claude-opus-4.6"], source.PricingCandidates(entry));
        Assert.Equal(["gpt-5"], source.PricingCandidates(TestData.Entry("2026-01-02T00:00:00Z", model: "gpt-5", source: "copilot")));
    }
}
