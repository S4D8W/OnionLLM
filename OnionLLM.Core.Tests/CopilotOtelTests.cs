using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class CopilotOtelTests
{
    private static string ChatSpan(string trace, string span, string response, long input, long output, long cacheRead, long seconds = 1_777_000_000) =>
        $"{{\"type\":\"span\",\"name\":\"chat gpt-5\",\"traceId\":\"{trace}\",\"spanId\":\"{span}\",\"endTime\":[{seconds},500000000]," +
        $"\"attributes\":{{\"gen_ai.operation.name\":\"chat\",\"gen_ai.response.model\":\"gpt-5\",\"gen_ai.response.id\":\"{response}\"," +
        $"\"gen_ai.conversation.id\":\"conv-1\",\"gen_ai.usage.input_tokens\":{input},\"gen_ai.usage.output_tokens\":{output}," +
        $"\"gen_ai.usage.cache_read.input_tokens\":{cacheRead}}}}}";

    private static string InferenceLog(string trace, long input, long output) =>
        $"{{\"traceId\":\"{trace}\",\"timestamp\":1777000001000,\"attributes\":{{\"event.name\":\"gen_ai.client.inference.operation.details\"," +
        $"\"gen_ai.usage.input_tokens\":{input},\"gen_ai.usage.output_tokens\":{output}}}}}";

    [Fact]
    public void Chat_span_outranks_inference_log_and_cache_is_subtracted()
    {
        using var directory = new TempDirectory();
        directory.Write("otel/export.jsonl", string.Join('\n',
            ChatSpan("t1", "s1", "r1", 1000, 50, 800),
            InferenceLog("t1", 1000, 50),
            InferenceLog("t2", 10, 5)));

        var entries = Deduplicator.Apply(TestData.ParseAll(new CopilotSource(), TestData.ConfigWith("copilot", directory.Path)));

        Assert.Equal(2, entries.Count);
        var span = entries.Single(entry => entry.SessionId == "conv-1");
        Assert.Equal(200, span.InputTokens);
        Assert.Equal(800, span.CacheReadTokens);
        Assert.Equal("gpt-5", span.Model);
        Assert.Equal(1, span.SessionTier);
        Assert.Equal(1_777_000_000_500, span.TimestampMs);
    }

    [Fact]
    public void Repeated_chat_spans_of_one_response_count_once()
    {
        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", string.Join('\n',
            ChatSpan("t1", "s1", "r1", 100, 10, 0),
            ChatSpan("t1", "s2", "r1", 100, 10, 0, 1_777_000_005)));

        var entries = Deduplicator.Apply(TestData.ParseAll(new CopilotSource(), TestData.ConfigWith("copilot", directory.Path)));

        Assert.Single(entries);
    }

    [Fact]
    public void Database_details_win_over_otel_and_otel_wins_over_shutdown_summary()
    {
        var database = TestData.Entry("2026-11-24T09:00:00Z", 10, 1, source: "copilot", session: "a") with { DedupeKey = "db" };
        var otelSameSession = TestData.Entry("2026-11-24T09:00:00Z", 10, 1, source: "copilot", session: "a") with { DedupeKey = "otel", SessionTier = 1 };
        var otelOnly = TestData.Entry("2026-11-24T09:00:00Z", 10, 1, source: "copilot", session: "b") with { DedupeKey = "otel2", SessionTier = 1 };
        var summaryB = TestData.Entry("2026-11-24T09:30:00Z", 10, 1, source: "copilot", session: "b") with { DedupeKey = "sum", IsSessionSummary = true };

        var result = Deduplicator.Apply([database, otelSameSession, otelOnly, summaryB]);

        Assert.Equal(["db", "otel2"], result.Select(entry => entry.DedupeKey));
    }
}
