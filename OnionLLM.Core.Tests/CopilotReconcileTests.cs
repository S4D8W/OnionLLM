using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;
using static OnionLLM.Core.Tests.CopilotFixture;

namespace OnionLLM.Core.Tests;

/// <summary>Uzgadnianie OTel, przedziałów shutdown, usage_record i kredytów między plikami — wg ccusage <c>copilot/src/loader/tests.rs</c>.</summary>
public class CopilotReconcileTests
{
    private static List<(string Timestamp, long Input)> Rows(IEnumerable<UsageEntry> entries) => entries.Select(Row).ToList();

    private static TempDirectory CrashedProcess()
    {
        var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00Z", 3)));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("first-1", "2026-01-02T09:00:00Z"),
            OtelCall("first-2", "2026-01-02T09:30:00Z"),
            OtelCall("crashed-1", "2026-01-02T20:00:00Z"),
            OtelCall("crashed-2", "2026-01-02T21:00:00Z"),
            OtelCall("crashed-3", "2026-01-03T08:00:00Z"),
            OtelCall("restored-1", "2026-01-03T09:00:00Z"),
            OtelCall("after-shutdown", "2026-01-03T11:00:00Z")));
        return directory;
    }

    [Fact]
    public void Otel_replaces_shutdowns_that_miss_calls_of_a_process_ended_without_a_shutdown()
    {
        using var directory = CrashedProcess();

        Assert.Equal(
        [
            ("2026-01-02T09:00:00", 1L), ("2026-01-02T09:30:00", 1L), ("2026-01-02T20:00:00", 1L), ("2026-01-02T21:00:00", 1L),
            ("2026-01-03T08:00:00", 1L), ("2026-01-03T09:00:00", 1L), ("2026-01-03T11:00:00", 1L)
        ], Rows(Load(directory)));
    }

    [Fact]
    public void Shutdown_usage_from_before_the_otel_export_started_is_kept()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00Z", 3)));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("crashed-1", "2026-01-02T20:00:00Z"),
            OtelCall("crashed-2", "2026-01-02T21:00:00Z"),
            OtelCall("restored-1", "2026-01-03T09:00:00Z")));

        Assert.Equal(
        [
            ("2026-01-02T10:00:00", 200L), ("2026-01-02T20:00:00", 1L), ("2026-01-02T21:00:00", 1L), ("2026-01-03T09:00:00", 1L)
        ], Rows(Load(directory)));
    }

    [Fact]
    public void Shutdown_intervals_stay_when_otel_has_no_calls_they_miss_and_later_otel_rows_are_added()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00Z", 3)));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("first-1", "2026-01-02T09:00:00Z"),
            OtelCall("first-2", "2026-01-02T09:30:00Z"),
            OtelCall("second-1", "2026-01-03T09:00:00Z"),
            OtelCall("after-shutdown", "2026-01-03T11:00:00Z")));

        Assert.Equal([("2026-01-02T10:00:00", 200L), ("2026-01-03T10:00:00", 100L), ("2026-01-03T11:00:00", 1L)], Rows(Load(directory)));
    }

    [Fact]
    public void Calls_a_concurrent_process_shuts_down_with_are_not_counted_twice()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 1),
            ShutdownWithRequests("shutdown-b", "2026-01-02T10:55:00Z", 1),
            ShutdownWithRequests("shutdown-a", "2026-01-02T11:00:00Z", 3)));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("first-1", "2026-01-02T09:00:00Z"),
            OtelCall("a-1", "2026-01-02T10:40:00Z"),
            OtelCall("a-2", "2026-01-02T10:50:00Z")));

        Assert.Equal([("2026-01-02T10:00:00", 100L), ("2026-01-02T11:00:00", 200L)], Rows(Load(directory)));
    }

    [Fact]
    public void Shutdowns_without_request_counts_keep_their_breakdown()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl",
            """{"type":"session.shutdown","id":"shutdown-1","timestamp":"2026-01-02T10:00:00Z","data":{"modelMetrics":{"test-model":{"usage":{"inputTokens":100}}}}}""");
        directory.Write("otel/otel.jsonl", Lines(OtelCall("first-1", "2026-01-02T09:00:00Z"), OtelCall("first-2", "2026-01-02T09:30:00Z")));

        Assert.Equal([("2026-01-02T10:00:00", 100L)], Rows(Load(directory)));
    }

    [Fact]
    public void Otel_rows_of_other_models_and_sessions_survive_shutdown_reconciliation()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Shutdown("shutdown-1", "2026-04-15T09:52:27Z", null, null, input: 10, output: 20),
            Shutdown("shutdown-2", "2026-04-15T09:53:27Z", null, null, input: 30, output: 40)));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("covered", "2026-04-15T09:53:00Z", input: 100),
            OtelCall("post-shutdown", "2026-04-15T09:54:00Z", input: 7),
            OtelCall("other-model", "2026-04-15T09:00:00Z", input: 3).Replace("\"test-model\"", "\"other-model\""),
            OtelCall("other-session", "2026-04-15T09:00:00Z", session: "session-2", input: 5)));

        var rows = Load(directory).Select(entry => (entry.SessionId, entry.Model, entry.InputTokens)).OrderBy(row => row.InputTokens);

        Assert.Equal(
        [
            ("session-1", "other-model", 3L), ("session-2", "test-model", 5L), ("session-1", "test-model", 7L),
            ("session-1", "test-model", 10L), ("session-1", "test-model", 20L)
        ], rows);
    }

    [Fact]
    public void Usage_records_match_otel_calls_by_service_request_before_shared_response_ids()
    {
        string Record(string id, string timestamp, string serviceRequest)
        {
            var usage = new System.Text.Json.Nodes.JsonObject { ["apiCallId"] = "turn-response", ["serviceRequestId"] = serviceRequest };
            return UsageRecord(id, timestamp, usage);
        }

        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Record("record-a", "2026-01-02T09:10:00Z", "service-a"),
            Record("record-b", "2026-01-02T09:20:00Z", "service-b")));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("call-a", "2026-01-02T09:05:00Z", attributes: ("github.copilot.service_request_id", "service-a")),
            OtelCall("call-b", "2026-01-02T09:11:00Z", attributes: ("gen_ai.response.id", "turn-response"))));

        Assert.Equal([("2026-01-02T09:10:00", 100L), ("2026-01-02T09:20:00", 100L)], Rows(Load(directory)));
    }

    [Fact]
    public void A_usage_record_replaces_only_the_nearest_otel_call_of_a_shared_response_id()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl",
            UsageRecord("record-1", "2026-01-02T09:09:00Z", new System.Text.Json.Nodes.JsonObject { ["apiCallId"] = "turn-response" }));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("first", "2026-01-02T09:00:00Z", input: 1, attributes: ("gen_ai.response.id", "turn-response")),
            OtelCall("recorded", "2026-01-02T09:10:00Z", input: 2, attributes: ("gen_ai.response.id", "turn-response"))));

        Assert.Equal([("2026-01-02T09:00:00", 1L), ("2026-01-02T09:09:00", 100L)], Rows(Load(directory)));
    }

    [Fact]
    public void Call_ids_match_only_within_the_same_kind()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl",
            UsageRecord("record-1", "2026-01-02T09:00:00Z", new System.Text.Json.Nodes.JsonObject { ["apiCallId"] = "same-value" }));
        directory.Write("otel/otel.jsonl", OtelCall("span-1", "2026-01-02T09:00:00Z", attributes: ("github.copilot.service_request_id", "same-value")));

        Assert.Equal([100L, 1L], Load(directory).Select(entry => entry.InputTokens).OrderDescending());
    }

    [Fact]
    public void A_usage_record_claims_only_the_call_its_service_request_names()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-parent/events.jsonl", ShutdownWithRequests("shutdown-1", "2026-01-02T11:00:00Z", 1));
        directory.Write("session-state/session-child/events.jsonl", UsageRecord("record-a", "2026-01-02T10:00:00Z",
            new System.Text.Json.Nodes.JsonObject { ["apiCallId"] = "turn-response", ["serviceRequestId"] = "service-a" }));
        directory.Write("otel/otel.jsonl", Lines(
            OtelCall("named", "2026-01-02T10:00:00Z", "session-parent", 1,
                ("gen_ai.response.id", "turn-response"), ("github.copilot.service_request_id", "service-a")),
            OtelCall("sibling", "2026-01-02T10:05:00Z", "session-child", 2, ("gen_ai.response.id", "turn-response"))));

        Assert.Equal([("2026-01-02T10:00:00", 100L), ("2026-01-02T10:05:00", 2L), ("2026-01-02T11:00:00", 100L)], Rows(Load(directory)));
    }

    [Fact]
    public void An_otel_call_exported_to_two_files_counts_once_and_a_re_exported_chat_span_too()
    {
        using var directory = new TempDirectory();
        var span = OtelCall("span-1", "2026-01-02T09:00:00Z", attributes: ("gen_ai.response.id", "response-1"));
        directory.Write("otel/a.jsonl", span);
        directory.Write("otel/b.jsonl", Lines(span, OtelCall("span-2", "2026-01-02T09:05:00Z", attributes: ("gen_ai.response.id", "response-1"))));
        directory.Write("otel/c.jsonl", OtelCall("span-3", "2026-01-02T09:06:00Z", attributes: ("gen_ai.response.id", "response-1")));

        Assert.Equal([("2026-01-02T09:00:00", 1L)], Rows(Load(directory)));
    }

    [Fact]
    public void A_kept_span_copy_takes_the_request_ids_of_a_later_copy_in_another_file()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl",
            UsageRecord("record-1", "2026-01-02T09:00:00Z", new System.Text.Json.Nodes.JsonObject { ["serviceRequestId"] = "request-1" }));
        directory.Write("otel/0.jsonl", OtelCall("span-1", "2026-01-02T09:00:00Z"));
        directory.Write("otel/1.jsonl", OtelCall("span-1", "2026-01-02T09:05:00Z", attributes: ("github.copilot.service_request_id", "request-1")));

        Assert.Equal([("2026-01-02T09:00:00", 100L)], Rows(Load(directory)));
    }

    [Theory]
    [InlineData(false, 30L, 0.20)]
    [InlineData(true, 10L, 0.0)]
    public void Residual_credits_are_reported_only_where_no_uncovered_otel_call_could_explain_them(bool complete, long total, double expectedGap)
    {
        using var directory = new TempDirectory();
        var summary = Shutdown("shutdown-1", "2026-01-03T10:00:00Z", total, 10, input: 200, output: 20, requests: 2);
        directory.Write("session-state/session-1/events.jsonl", Lines(
            Checkpoint("initial", "2026-01-02T10:00:00Z", total),
            summary,
            Checkpoint("resumed", "2026-01-04T10:00:00Z", total + 5)));
        var spans = new List<string> { OtelCall("span-1", "2026-01-03T09:00:00Z"), OtelCall("resumed-span", "2026-01-04T09:00:00Z") };
        if (complete)
        {
            spans.Add(OtelCall("span-2", "2026-01-03T09:30:00Z"));
        }

        directory.Write("otel/otel.jsonl", Lines([.. spans]));

        var rows = Load(directory).Select(entry => (Row(entry).Timestamp, entry.Model, entry.TotalTokens, Math.Round(entry.RecordedCostUsd ?? 0, 9))).ToList();

        var expected = new List<(string, string?, long, double)> { ("2026-01-03T10:00:00", "test-model", 220, 0.10) };
        if (expectedGap > 0)
        {
            expected.Add(("2026-01-03T10:00:00", "unknown", 0, expectedGap));
        }

        expected.Add(("2026-01-04T09:00:00", "test-model", 2, 0));
        Assert.Equal(expected, rows);
    }

    [Fact]
    public void An_unmatched_otel_call_leaves_the_credit_gap_unreported()
    {
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", Checkpoint("checkpoint-1", "2026-01-02T10:00:00Z", 20));
        directory.Write("otel/otel.jsonl", OtelCall("span-1", "2026-01-02T10:00:00Z", input: 11));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("test-model", entry.Model);
    }

    [Fact]
    public void Database_rows_win_over_session_state_and_otel_of_the_same_session()
    {
        var database = TestData.Entry("2026-01-02T09:00:00Z", 10, 1, model: "test-model", source: "copilot", session: "session-1") with { DedupeKey = "session-1:db" };
        using var directory = new TempDirectory();
        directory.Write("session-state/session-1/events.jsonl", ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00Z", 2));
        directory.Write("session-state/session-2/events.jsonl", ShutdownWithRequests("shutdown-2", "2026-01-02T10:00:00Z", 1));
        var source = new CopilotSource(() => null);
        var parsed = TestData.ParseAll(source, TestData.ConfigWith("copilot", directory.Path));

        var result = Deduplicator.Apply(parsed.Append(database), id => id == CopilotSource.SourceId ? source : null);

        Assert.Equal([("session-1", 10L), ("session-2", 100L)], result.Select(entry => (entry.SessionId, entry.InputTokens)).OrderBy(row => row.SessionId));
    }
}
