using System.Text.Json.Nodes;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;
using static OnionLLM.Core.Tests.CopilotFixture;

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

    // --- Uzgadnianie wywołań OTel między plikami i reszta z total_tokens (ccusage copilot/src/loader/tests.rs) ---

    private static string With(string record, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(record)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static JsonObject Attributes(JsonObject record) => record["attributes"]!.AsObject();

    private static List<(string Timestamp, long Input)> Rows(IEnumerable<UsageEntry> entries) => entries.Select(Row).ToList();

    /// <summary>Te same rekordy w jednym pliku i każdy w osobnym (ccusage <c>joined_and_split_otel_rows</c>).</summary>
    private static (List<UsageEntry> Joined, List<UsageEntry> Split) JoinedAndSplit(params string[] lines)
    {
        using var joined = new TempDirectory();
        joined.Write("otel/a.jsonl", Lines(lines));
        using var split = new TempDirectory();
        for (var index = 0; index < lines.Length; index++)
        {
            split.Write($"otel/{index}.jsonl", lines[index]);
        }

        return (Load(joined), Load(split));
    }

    private static string Span(string trace, string span, long seconds, string name, JsonObject attributes) => new JsonObject
    {
        ["type"] = "span",
        ["traceId"] = trace,
        ["spanId"] = span,
        ["name"] = name,
        ["endTime"] = new JsonArray(seconds, 0),
        ["attributes"] = attributes
    }.ToJsonString();

    /// <summary>ccusage <c>chat_span</c>: span chat modelu <c>test-model</c> z 10 tokenami wyjścia.</summary>
    private static string TestChatSpan(string trace, string span, long seconds, string response, long input) =>
        Span(trace, span, seconds, "chat test-model", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "test-model",
            ["gen_ai.conversation.id"] = "conv-1",
            ["gen_ai.response.id"] = response,
            ["gen_ai.usage.input_tokens"] = input,
            ["gen_ai.usage.output_tokens"] = 10
        });

    [Fact]
    public void A_span_and_a_log_whose_ids_spell_the_same_string_in_two_files_are_both_kept()
    {
        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", Span("log:trace-1", "span-1", 1_767_320_400, "chat test-model", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "test-model",
            ["gen_ai.conversation.id"] = "session-1",
            ["gen_ai.usage.input_tokens"] = 11,
            ["gen_ai.usage.output_tokens"] = 12
        }));
        directory.Write("otel/b.jsonl", new JsonObject
        {
            ["hrTime"] = new JsonArray(1_767_320_460, 0),
            ["traceId"] = "trace-1",
            ["spanId"] = "span-1",
            ["attributes"] = new JsonObject
            {
                ["event.name"] = "gen_ai.client.inference.operation.details",
                ["gen_ai.response.model"] = "test-model",
                ["gen_ai.conversation.id"] = "session-2",
                ["gen_ai.usage.input_tokens"] = 21,
                ["gen_ai.usage.output_tokens"] = 22
            },
            ["_body"] = "GenAI inference: test-model"
        }.ToJsonString());

        Assert.Equal([11L, 21L], Load(directory).Select(entry => entry.InputTokens).Order());
    }

    [Fact]
    public void Lower_priority_records_of_the_same_response_are_suppressed_in_one_file_or_several()
    {
        var agent = Span("trace-dupe", "agent-1", 1_775_934_263, "invoke_agent GitHub Copilot Chat", new JsonObject
        {
            ["gen_ai.operation.name"] = "invoke_agent",
            ["gen_ai.response.model"] = "gpt-5.4-mini",
            ["gen_ai.conversation.id"] = "conv-dupe",
            ["gen_ai.response.id"] = "resp-dupe",
            ["gen_ai.usage.input_tokens"] = 100,
            ["gen_ai.usage.output_tokens"] = 30
        });
        var log = new JsonObject
        {
            ["hrTime"] = new JsonArray(1_775_934_263, 0),
            ["attributes"] = new JsonObject
            {
                ["event.name"] = "gen_ai.client.inference.operation.details",
                ["gen_ai.response.model"] = "gpt-5.4-mini",
                ["gen_ai.response.id"] = "resp-dupe",
                ["gen_ai.usage.input_tokens"] = 80,
                ["gen_ai.usage.output_tokens"] = 20
            },
            ["_body"] = "GenAI inference: gpt-5.4-mini"
        }.ToJsonString();
        var chat = Span("trace-dupe", "chat-1", 1_775_934_263, "chat gpt-5.4-mini", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "gpt-5.4-mini",
            ["gen_ai.conversation.id"] = "conv-dupe",
            ["gen_ai.response.id"] = "resp-dupe",
            ["gen_ai.usage.input_tokens"] = 60,
            ["gen_ai.usage.output_tokens"] = 10
        });

        var (joined, split) = JoinedAndSplit(agent, log, chat);

        foreach (var entries in new[] { joined, split })
        {
            var entry = Assert.Single(entries);
            Assert.Equal("otel:span:trace-dupe:chat-1", entry.DedupeKey);
            Assert.Equal(60, entry.InputTokens);
            Assert.Equal(10, entry.OutputTokens);
        }
    }

    [Fact]
    public void A_re_exported_chat_span_counts_once_and_the_earliest_copy_is_kept()
    {
        using var directory = new TempDirectory();
        directory.Write("otel/copilot.jsonl", Lines(
            TestChatSpan("trace-1", "span-1", 1_775_934_264, "resp-1", 100),
            TestChatSpan("trace-1", "span-2", 1_775_934_300, "resp-2", 200),
            TestChatSpan("trace-2", "span-3", 1_775_934_368, "resp-1", 100),
            TestChatSpan("trace-1", "span-4", 1_775_934_370, "resp-2", 200),
            TestChatSpan("trace-3", "span-5", 1_775_934_500, "resp-3", 300),
            TestChatSpan("trace-1", "span-6", 1_775_934_400, "resp-3", 300)));

        Assert.Equal(
            ["otel:span:trace-1:span-1", "otel:span:trace-1:span-2", "otel:span:trace-1:span-6"],
            Load(directory).Select(entry => entry.DedupeKey));
    }

    [Theory]
    [InlineData("trace-1", "span-1", false)]
    [InlineData("trace-2", "span-2", false)]
    [InlineData("trace-1", "span-1", true)]
    [InlineData("trace-2", "span-2", true)]
    public void Output_recorded_by_any_copy_of_an_otel_call_is_billed(string laterTrace, string laterSpan, bool split)
    {
        string Copy(string trace, string span, long seconds) => With(TestChatSpan(trace, span, seconds, "resp-1", 100), record =>
        {
            Attributes(record)["github.copilot.service_request_id"] = "request-1";
            Attributes(record)["gen_ai.usage.total_tokens"] = 110;
        });
        var partial = With(Copy("trace-1", "span-1", 1_775_934_264), record => Attributes(record).Remove("gen_ai.usage.output_tokens"));
        var recorded = Copy(laterTrace, laterSpan, 1_775_934_300);

        var (joined, separate) = JoinedAndSplit(partial, recorded);

        var entry = Assert.Single(split ? separate : joined);
        Assert.Equal("otel:span:trace-1:span-1", entry.DedupeKey);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(10, entry.OutputTokens);
    }

    [Fact]
    public void Unconfirmed_total_only_output_stays_out_of_the_output_tokens()
    {
        // Kopia z innymi licznikami nie potwierdza reszty jako wyjścia (#2492).
        var partial = With(TestChatSpan("trace-1", "span-1", 1_775_934_264, "resp-1", 100), record =>
        {
            Attributes(record).Remove("gen_ai.usage.output_tokens");
            Attributes(record)["gen_ai.usage.total_tokens"] = 110;
        });
        var different = With(TestChatSpan("trace-1", "span-1", 1_775_934_300, "resp-1", 100), record =>
            Attributes(record)["gen_ai.usage.output_tokens"] = 9);

        var (joined, split) = JoinedAndSplit(partial, different);

        foreach (var entries in new[] { joined, split })
        {
            var entry = Assert.Single(entries);
            Assert.Equal(1_775_934_264_000, entry.TimestampMs);
            Assert.Equal(0, entry.OutputTokens);
        }
    }

    [Fact]
    public void Reasoning_is_not_double_counted_and_only_reasoning_in_the_total_remainder_is_billed()
    {
        JsonObject Usage(long? total)
        {
            var attributes = new JsonObject
            {
                ["gen_ai.operation.name"] = "chat",
                ["gen_ai.response.model"] = "test-model",
                ["gen_ai.conversation.id"] = "conv-1",
                ["gen_ai.usage.input_tokens"] = 100,
                ["gen_ai.usage.output_tokens"] = 50,
                ["gen_ai.usage.cache_read.input_tokens"] = 10,
                ["gen_ai.usage.cache_creation.input_tokens"] = 20,
                ["gen_ai.usage.reasoning.output_tokens"] = 5
            };
            if (total is not null)
            {
                attributes["gen_ai.usage.total_tokens"] = total;
            }

            return attributes;
        }

        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", Span("trace-1", "span-1", 1_775_934_264, "chat test-model", Usage(null)));
        directory.Write("otel/b.jsonl", Span("trace-2", "span-1", 1_775_934_265, "chat test-model", Usage(162)));

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal((70L, 10L, 20L, 5L),
            (entry.InputTokens, entry.CacheReadTokens, entry.CacheCreationTokens, entry.ReasoningTokens)));
        Assert.Equal(50, entries[0].OutputTokens); // bez total_tokens reasoning jest częścią output_tokens
        Assert.Equal(55, entries[1].OutputTokens); // reszta 12 z total: 5 reasoning rozliczane, 7 niesklasyfikowane
    }

    [Fact]
    public void Total_only_otel_usage_is_kept_as_a_call_without_output_tokens()
    {
        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", Span("trace-1", "span-1", 1_775_934_264, "chat test-model", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "test-model",
            ["gen_ai.conversation.id"] = "conv-1",
            ["gen_ai.usage.total_tokens"] = 567,
            ["gen_ai.usage.reasoning_tokens"] = 5
        }));

        var entry = Assert.Single(Load(directory));

        Assert.Equal(0, entry.InputTokens);
        Assert.Equal(5, entry.OutputTokens); // tylko reasoning z reszty (ccusage: koszt 5 × stawka wyjścia)
        Assert.Equal(5, entry.ReasoningTokens);
    }

    [Fact]
    public void A_re_export_inherits_its_trace_model_from_another_file()
    {
        var withModel = OtelCall("span-1", "2026-01-02T09:00:00Z", attributes: ("gen_ai.response.id", "response-1"));
        var withoutModel = With(OtelCall("span-2", "2026-01-02T09:05:00Z", attributes: ("gen_ai.response.id", "response-1")),
            record => Attributes(record).Remove("gen_ai.response.model"));

        foreach (var lines in new[] { new[] { withModel, withoutModel }, new[] { withoutModel, withModel } })
        {
            var (joined, split) = JoinedAndSplit(lines);

            Assert.Equal([("2026-01-02T09:00:00", 1L)], Rows(joined));
            Assert.Equal([("2026-01-02T09:00:00", 1L)], Rows(split));
        }
    }

    [Fact]
    public void Model_and_session_come_from_records_of_the_same_trace_in_other_files()
    {
        using var directory = new TempDirectory();
        // Span invoke_agent w innym pliku niesie model i identyfikator rozmowy; span chat nie ma ani modelu, ani sesji.
        directory.Write("otel/a.jsonl", Span("trace-1", "agent-1", 1_775_934_400, "invoke_agent GitHub Copilot Chat", new JsonObject
        {
            ["gen_ai.operation.name"] = "invoke_agent",
            ["gen_ai.request.model"] = "claude-opus-4.6-1m",
            ["gen_ai.conversation.id"] = "conv-1",
            ["gen_ai.usage.input_tokens"] = 100,
            ["gen_ai.usage.output_tokens"] = 10
        }));
        directory.Write("otel/b.jsonl", Span("trace-1", "chat-1", 1_775_934_300, "chat", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.usage.input_tokens"] = 100,
            ["gen_ai.usage.output_tokens"] = 10
        }));

        var source = new CopilotSource(() => null);
        var entry = Assert.Single(Load(directory));

        Assert.Equal("otel:span:trace-1:chat-1", entry.DedupeKey); // invoke_agent tego trace ustępuje spanowi chat
        Assert.Equal("claude-opus-4.6", entry.Model);
        Assert.False(entry.IsFallbackModel);
        Assert.Equal("conv-1", entry.SessionId);
        Assert.Equal(["claude-opus-4.6-1m", "claude-opus-4.6"], source.PricingCandidates(entry));
    }

    [Fact]
    public void The_trace_context_of_the_records_own_file_wins_over_other_files()
    {
        var chat = Span("trace-1", "chat-1", 1_775_934_300, "chat", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.usage.input_tokens"] = 100,
            ["gen_ai.usage.output_tokens"] = 10
        });
        // Log tury bez zużycia (same zera) — nie jest wywołaniem, ale wnosi kontekst trace swojego pliku.
        var turn = new JsonObject
        {
            ["hrTime"] = new JsonArray(1_775_934_200, 0),
            ["traceId"] = "trace-1",
            ["attributes"] = new JsonObject
            {
                ["event.name"] = "copilot_chat.agent.turn",
                ["gen_ai.response.model"] = "model-a",
                ["gen_ai.conversation.id"] = "conv-a",
                ["gen_ai.usage.input_tokens"] = 0
            }
        }.ToJsonString();
        var other = Span("trace-1", "agent-1", 1_775_934_100, "invoke_agent x", new JsonObject
        {
            ["gen_ai.operation.name"] = "invoke_agent",
            ["gen_ai.response.model"] = "model-b",
            ["gen_ai.conversation.id"] = "conv-b",
            ["gen_ai.usage.input_tokens"] = 1
        });
        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", Lines(turn, chat));
        directory.Write("otel/b.jsonl", other);

        var entry = Assert.Single(Load(directory));

        Assert.Equal(("model-a", "conv-a"), (entry.Model, entry.SessionId));
    }

    [Fact]
    public void The_highest_priority_session_of_the_trace_wins_across_files()
    {
        using var directory = new TempDirectory();
        // Plik a: span chat bez sesji i log bez zużycia, który daje trace tylko response id (priorytet 1).
        directory.Write("otel/a.jsonl", Lines(
            Span("trace-1", "chat-1", 1_775_934_300, "chat", new JsonObject
            {
                ["gen_ai.operation.name"] = "chat",
                ["gen_ai.response.model"] = "test-model",
                ["gen_ai.usage.input_tokens"] = 100
            }),
            new JsonObject
            {
                ["hrTime"] = new JsonArray(1_775_934_300, 0),
                ["traceId"] = "trace-1",
                ["attributes"] = new JsonObject
                {
                    ["event.name"] = "gen_ai.client.inference.operation.details",
                    ["gen_ai.response.id"] = "resp-0",
                    ["gen_ai.usage.input_tokens"] = 0
                }
            }.ToJsonString()));
        // Plik b: span chat tego samego trace z identyfikatorem sesji (priorytet 3).
        directory.Write("otel/b.jsonl", Span("trace-1", "chat-2", 1_775_934_400, "chat", new JsonObject
        {
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "test-model",
            ["copilot_chat.session_id"] = "session-1",
            ["gen_ai.usage.input_tokens"] = 200
        }));

        Assert.Equal(["session-1", "session-1"], Load(directory).Select(entry => entry.SessionId));
    }

    [Fact]
    public void Copies_of_one_span_identity_count_once_keeping_the_earliest_in_one_file_or_several()
    {
        var (sameJoined, sameSplit) = JoinedAndSplit(OtelCall("span-1", "2026-01-02T09:00:00Z"), OtelCall("span-1", "2026-01-02T09:00:00Z"));
        var (joined, split) = JoinedAndSplit(OtelCall("span-1", "2026-01-02T09:05:00Z"), OtelCall("span-1", "2026-01-02T09:00:00Z"));

        foreach (var entries in new[] { sameJoined, sameSplit, joined, split })
        {
            Assert.Equal([("2026-01-02T09:00:00", 1L)], Rows(entries));
        }
    }

    [Fact]
    public void Distinct_calls_of_one_response_id_are_kept_in_one_file_or_several()
    {
        string Request(string span, string request) => OtelCall(span, "2026-01-02T09:00:00Z",
            attributes: [("gen_ai.response.id", "turn-response"), ("github.copilot.service_request_id", request)]);
        string Usage(string span, long input) => OtelCall(span, "2026-01-02T09:00:00Z", input: input,
            attributes: ("gen_ai.response.id", "turn-response"));

        var (requestsJoined, requestsSplit) = JoinedAndSplit(Request("span-1", "request-1"), Request("span-2", "request-2"));
        var (usageJoined, usageSplit) = JoinedAndSplit(Usage("span-1", 1), Usage("span-2", 2));

        Assert.Equal([("2026-01-02T09:00:00", 1L), ("2026-01-02T09:00:00", 1L)], Rows(requestsJoined));
        Assert.Equal([("2026-01-02T09:00:00", 1L), ("2026-01-02T09:00:00", 1L)], Rows(requestsSplit));
        Assert.Equal([1L, 2L], usageJoined.Select(entry => entry.InputTokens).Order());
        Assert.Equal([1L, 2L], usageSplit.Select(entry => entry.InputTokens).Order());
    }

    [Fact]
    public void Otel_calls_without_trace_and_span_ids_are_kept_from_every_file()
    {
        string Call(long input) => With(OtelCall("unused", "2026-01-02T09:00:00Z", input: input), record =>
        {
            record.Remove("traceId");
            record.Remove("spanId");
        });
        using var directory = new TempDirectory();
        directory.Write("otel/a.jsonl", Call(1));
        directory.Write("otel/b.jsonl", Call(1));
        directory.Write("otel/c.jsonl", Call(2));

        Assert.Equal([1L, 1L, 2L], Load(directory).Select(entry => entry.InputTokens).Order());
    }
}
