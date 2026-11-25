using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Odczyt i deduplikacja transkryptów Claude Code — przypadki z ccusage
/// (<c>rust/adapters/claude/src/tests.rs</c>, <c>daily/tests.rs</c>, <c>load/tests.rs</c>, <c>usage_wire.rs</c>).
/// </summary>
public class ClaudeTranscriptTests
{
    private const string FirstBlock = "2026-05-22T02:34:40.000Z";
    private const string LaterBlock = "2026-05-22T02:34:40.500Z";
    private const string ThirdBlock = "2026-05-22T02:34:41.000Z";

    private static (List<UsageEntry> Entries, ParseDiagnostics Diagnostics) Load(params (string Path, string[] Lines)[] files)
    {
        using var directory = new TempDirectory();
        foreach (var (path, lines) in files)
        {
            directory.Write(path, string.Join('\n', lines));
        }

        var diagnostics = new ParseDiagnostics();
        var parsed = TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path), diagnostics);
        return (Deduplicator.Apply(parsed), diagnostics);
    }

    private static List<UsageEntry> LoadChat(params string[] lines) =>
        Load(("projects/project-a/session-a/chat.jsonl", lines)).Entries;

    // --- łańcuchy odpowiedzi bez requestId (tests.rs:requestless_response_cases, #2255) ---

    private static string ChainedAssistant(string uuid, string? parent, string timestamp, string messageId, long input, string? toolUse)
    {
        var parentJson = parent is null ? "null" : $"\"{parent}\"";
        var content = toolUse is null
            ? """[{"type":"text","text":"ok"}]"""
            : $$$$"""[{"type":"tool_use","id":"{{{{toolUse}}}}","name":"Read","input":{}}]""";
        return $$$$"""{"parentUuid":{{{{parentJson}}}},"type":"assistant","uuid":"{{{{uuid}}}}","timestamp":"{{{{timestamp}}}}","sessionId":"session-a","message":{"id":"{{{{messageId}}}}","model":"claude-sonnet-4-20250514","role":"assistant","content":{{{{content}}}},"usage":{"input_tokens":{{{{input}}}},"output_tokens":25,"cache_read_input_tokens":0}}}""";
    }

    private static string ChainedUser(string uuid, string parent, string content) =>
        $$$$"""{"parentUuid":"{{{{parent}}}}","type":"user","uuid":"{{{{uuid}}}}","timestamp":"2026-05-22T02:34:40.250Z","sessionId":"session-a","message":{"role":"user","content":{{{{content}}}}}}""";

    private static string ToolResult(string toolUseId) =>
        $$$$"""[{"type":"tool_result","tool_use_id":"{{{{toolUseId}}}}","content":"ok"}]""";

    private static string WithToolInput(string line, string input) => line.Replace("\"input\":{}", $"\"input\":{input}");

    private static string Linkless(string timestamp, string messageId) =>
        $$$$"""{"type":"assistant","timestamp":"{{{{timestamp}}}}","sessionId":"session-a","message":{"id":"{{{{messageId}}}}","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":25,"cache_read_input_tokens":0}}}""";

    private static string Sidechain(string line) => line.Replace("\"type\":\"assistant\",", "\"type\":\"assistant\",\"isSidechain\":true,");

    private static string WithRequest(string line, string request) =>
        line.Replace("\"type\":\"assistant\",", $"\"type\":\"assistant\",\"requestId\":\"{request}\",");

    private static string InSessionB(string line) => line.Replace("\"sessionId\":\"session-a\"", "\"sessionId\":\"session-b\"");

    private static string MoreOutput(string line, int output) => line.Replace("\"output_tokens\":25", $"\"output_tokens\":{output}");

    public static TheoryData<string, string[], int> RequestlessResponseCases() => new()
    {
        { "chained anthropic message id", [ChainedAssistant("a1", null, FirstBlock, "msg_01AbC2DeF3", 100, null), ChainedAssistant("a2", "a1", LaterBlock, "msg_01AbC2DeF3", 100, null)], 1 },
        { "chained proxy message id", [ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null), ChainedAssistant("a2", "a1", LaterBlock, "gateway-7f3a", 100, null)], 1 },
        {
            "a block of another message does not bridge a response chain",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-1", 100, null),
                ChainedAssistant("b1", "a1", LaterBlock, "gateway-2", 200, null),
                ChainedAssistant("a2", "b1", ThirdBlock, "gateway-1", 100, null)
            ],
            3
        },
        {
            "blocks around their own tool result",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, "toolu_1"),
                ChainedUser("u1", "a1", ToolResult("toolu_1")),
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "blocks around a tool use on a later block",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                ChainedAssistant("a2", "a1", LaterBlock, "gateway-7f3a", 100, "toolu_1"),
                ChainedUser("u1", "a2", ToolResult("toolu_1")),
                ChainedAssistant("a3", "u1", ThirdBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "blocks around their own agent tool result",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, "toolu_1"),
                """{"parentUuid":"a1","type":"user","uuid":"u1","timestamp":"2026-05-22T02:34:40.250Z","sessionId":"session-a","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"done"}]},"toolUseResult":{"usage":{"input_tokens":7777,"output_tokens":7777}}}""",
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "blocks through a non-usage line",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                """{"parentUuid":"a1","type":"system","uuid":"s1","timestamp":"2026-05-22T02:34:40.250Z","sessionId":"session-a"}""",
                ChainedAssistant("a2", "s1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "reused id after a tool result with new input",
            [
                ChainedAssistant("a1", null, FirstBlock, "msg_01AbC2DeF3", 100, "toolu_1"),
                ChainedUser("u1", "a1", ToolResult("toolu_1")),
                ChainedAssistant("a2", "u1", LaterBlock, "msg_01AbC2DeF3", 150, null)
            ],
            2
        },
        {
            "reused id after a user prompt",
            [
                ChainedAssistant("a1", null, FirstBlock, "msg_01AbC2DeF3", 100, null),
                ChainedUser("u1", "a1", "\"next question\""),
                ChainedAssistant("a2", "u1", LaterBlock, "msg_01AbC2DeF3", 100, null)
            ],
            2
        },
        {
            "reused id after a tool result for another response",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, "toolu_1"),
                ChainedUser("u1", "a1", ToolResult("toolu_9")),
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            2
        },
        {
            "reused id after a tool result for an id named only in tool input",
            [
                WithToolInput(ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, "toolu_own"), """{"id":"toolu_foreign"}"""),
                ChainedUser("u1", "a1", ToolResult("toolu_foreign")),
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            2
        },
        {
            "reused id after a user prompt whose type is escaped",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                ChainedUser("u1", "a1", "\"next question\"").Replace("\"type\":\"user\"", "\"type\":\"\\u0075ser\""),
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            2
        },
        {
            "later block of a discarded same-timestamp rewrite",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                ChainedAssistant("a1-rewrite", null, FirstBlock, "gateway-7f3a", 100, null),
                ChainedAssistant("a2", "a1-rewrite", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "later block of the original after a surviving same-timestamp rewrite",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                MoreOutput(ChainedAssistant("a1-rewrite", null, FirstBlock, "gateway-7f3a", 100, null), 250),
                ChainedAssistant("a2", "a1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "linkless record at the timestamp of a replaced survivor",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                MoreOutput(ChainedAssistant("a2", "a1", LaterBlock, "gateway-7f3a", 100, null), 250),
                Linkless(FirstBlock, "gateway-7f3a")
            ],
            1
        },
        {
            "block that connects an earlier response and a same-timestamp record",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                ChainedAssistant("b1", null, LaterBlock, "gateway-7f3a", 100, null),
                ChainedAssistant("c1", "a1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            1
        },
        {
            "blocks whose parent belongs to another session",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                InSessionB(ChainedAssistant("b1", "a1", LaterBlock, "gateway-7f3a", 100, null)),
                InSessionB(ChainedAssistant("b2", "a1", ThirdBlock, "gateway-7f3a", 100, null))
            ],
            3
        },
        {
            "blocks around a tool result after another session reused the tool id",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, "toolu_1"),
                InSessionB(ChainedAssistant("b1", null, FirstBlock, "gateway-copy", 100, "toolu_1")),
                ChainedUser("u1", "a1", ToolResult("toolu_1")),
                ChainedAssistant("a2", "u1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            2
        },
        {
            "requestless record at the timestamp of a sidechain replay its parent absorbed",
            [
                Sidechain(Linkless(FirstBlock, "msg_parent")),
                WithRequest(Linkless(LaterBlock, "msg_parent"), "req_parent"),
                Linkless(FirstBlock, "msg_parent")
            ],
            1
        },
        {
            "sidechain replay of a response whose first block came from another session",
            [
                Sidechain(Linkless(FirstBlock, "msg_parent")),
                InSessionB(WithRequest(Linkless(LaterBlock, "msg_parent"), "req_parent")),
                WithRequest(Linkless(ThirdBlock, "msg_parent"), "req_parent")
            ],
            1
        },
        {
            "blocks around an interrupted write that the next record continued",
            [
                ChainedAssistant("a1", null, FirstBlock, "gateway-7f3a", 100, null),
                """{"parentUuid":"a1","isSidechain":false,"sessionId":"session-a","type":"attachment","uuid":"x1","times""" +
                """{"parentUuid":"a1","isSidechain":false,"sessionId":"session-a","type":"system","uuid":"s1","timestamp":"2026-05-22T02:34:40.250Z"}""",
                ChainedAssistant("a2", "x1", LaterBlock, "gateway-7f3a", 100, null)
            ],
            2
        },
        {
            "blocks without transcript links",
            [
                $$$$"""{"timestamp":"{{{{FirstBlock}}}}","message":{"id":"msg_01AbC2DeF3","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":25}}}""",
                $$$$"""{"timestamp":"{{{{LaterBlock}}}}","message":{"id":"msg_01AbC2DeF3","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":25}}}"""
            ],
            2
        }
    };

    [Theory]
    [MemberData(nameof(RequestlessResponseCases))]
    public void Requestless_content_blocks_collapse_only_along_their_transcript_chain(string name, string[] lines, int responses)
    {
        Assert.True(responses == LoadChat(lines).Count, name);
    }

    [Fact]
    public void Repeated_requestless_writes_at_the_same_timestamp_keep_the_larger_one()
    {
        var entries = LoadChat(
            """{"timestamp":"2026-05-22T02:34:40.000Z","message":{"id":"ocgo","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":25}}}""",
            """{"timestamp":"2026-05-22T02:34:40.000Z","message":{"id":"ocgo","model":"claude-sonnet-4-20250514","usage":{"input_tokens":100,"output_tokens":250,"speed":"standard"}}}""");

        Assert.Equal(250, Assert.Single(entries).OutputTokens);
    }

    [Fact]
    public void Gateway_responses_reusing_a_message_id_in_other_sessions_stay_separate()
    {
        var entries = LoadChat(Linkless(FirstBlock, "gw"), InSessionB(Linkless(FirstBlock, "gw")));

        Assert.Equal(2, entries.Count);
    }

    // --- sidechain (#913) ---

    private static string Record(string session, string messageId, string requestId, long cacheRead, bool sidechain = false,
        string timestamp = "2026-05-22T02:34:40.000Z", long output = 10) =>
        $$$$"""{"type":"assistant","timestamp":"{{{{timestamp}}}}","sessionId":"{{{{session}}}}","requestId":"{{{{requestId}}}}","isSidechain":{{{{(sidechain ? "true" : "false")}}}},"message":{"id":"{{{{messageId}}}}","model":"claude-sonnet-4-20250514","usage":{"input_tokens":1,"output_tokens":{{{{output}}}},"cache_read_input_tokens":{{{{cacheRead}}}}}}}""";

    [Fact]
    public void Parent_usage_is_kept_when_a_sidechain_replays_the_message_with_a_new_request_id()
    {
        // /btw zapisuje replay w subagents/ — inny plik, ta sama sesja, ten sam message.id, nowy requestId.
        var (entries, _) = Load(
            ("projects/project-a/session-a.jsonl", [Record("session-a", "msg-parent", "req-parent", 20)]),
            ("projects/project-a/session-a/subagents/agent-btw.jsonl",
            [
                Record("session-a", "msg-parent", "req-sidechain-replay", 50_000, sidechain: true, timestamp: "2026-05-22T03:00:00.000Z"),
                Record("session-a", "msg-sidechain-answer", "req-sidechain-answer", 700, sidechain: true, timestamp: "2026-05-22T03:00:01.000Z", output: 30)
            ]));

        Assert.Equal(2, entries.Count);
        var parent = Assert.Single(entries, entry => entry.CacheReadTokens == 20);
        Assert.Equal(1, parent.Preference);
        var answer = Assert.Single(entries, entry => entry.CacheReadTokens == 700);
        Assert.Equal(0, answer.Preference);
        Assert.All(entries, entry => Assert.Equal("session-a", entry.SessionId));
    }

    [Fact]
    public void Parent_replaces_a_sidechain_replay_seen_first()
    {
        var entries = LoadChat(
            Record("session-a", "msg-parent", "req-replay", 50_000, sidechain: true),
            Record("session-a", "msg-parent", "req-parent", 20));

        Assert.Equal(20, Assert.Single(entries).CacheReadTokens);
    }

    [Fact]
    public void Sidechain_replay_in_another_session_is_kept()
    {
        var entries = LoadChat(
            Record("session-a", "msg-parent", "req-parent", 20),
            Record("session-b", "msg-parent", "req-replay", 50_000, sidechain: true));

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Sidechain_replay_is_dropped_after_cross_session_survivor_replacement()
    {
        // tests.rs:dedupes_sidechain_replay_after_cross_session_survivor_replacement
        var (entries, _) = Load(
            ("projects/project-a/a.jsonl", [Record("session-b", "msg-parent", "req-parent", 20)]),
            ("projects/project-a/b.jsonl", [Record("session-a", "msg-parent", "req-parent", 30)]),
            ("projects/project-a/c.jsonl", [Record("session-b", "msg-parent", "req-sidechain-replay", 50_000, sidechain: true)]));

        var entry = Assert.Single(entries);
        Assert.Equal(30, entry.CacheReadTokens);
        Assert.Equal("b", entry.SessionId);
    }

    // --- kolejność wyboru duplikatów (dedupe.rs:462-475) ---

    private static UsageEntry Duplicate(long output = 10, SpeedTier speed = SpeedTier.Unknown, bool split = false, double? cost = null,
        bool sidechain = false) => new()
    {
        Source = ClaudeSource.SourceId,
        TimestampMs = 1_000,
        SessionId = "s",
        OutputTokens = output,
        Speed = speed,
        RecordedCostUsd = cost,
        Preference = sidechain ? 0 : 1,
        DedupeKey = new ClaudeDedupeKey("m", "r", "s", 1_000, null, sidechain, split).Format(),
        SourceData = new ClaudeDedupeKey("m", "r", "s", 1_000, null, sidechain, split).Data
    };

    [Fact]
    public void Duplicate_selection_order_matches_ccusage()
    {
        var comparer = new ClaudeSource().DuplicateComparer!;

        // Wątek główny przed liczbą tokenów.
        Assert.True(comparer.Compare(Duplicate(output: 1), Duplicate(output: 999, sidechain: true)) > 0);
        // Więcej tokenów przed szybkością.
        Assert.True(comparer.Compare(Duplicate(output: 11), Duplicate(output: 10, speed: SpeedTier.Fast)) > 0);
        // Fast > standard > brak.
        Assert.True(comparer.Compare(Duplicate(speed: SpeedTier.Fast), Duplicate(speed: SpeedTier.Standard, split: true, cost: 1)) > 0);
        Assert.True(comparer.Compare(Duplicate(speed: SpeedTier.Standard), Duplicate(split: true)) > 0);
        // Podział cache przed zapisanym kosztem.
        Assert.True(comparer.Compare(Duplicate(split: true), Duplicate(cost: 1)) > 0);
        Assert.True(comparer.Compare(Duplicate(cost: 0), Duplicate()) > 0);
        // Pełny remis — zostaje pierwszy wpis.
        Assert.Equal(0, comparer.Compare(Duplicate(), Duplicate()));
    }

    [Fact]
    public void Cache_ttl_split_wins_over_an_otherwise_equal_duplicate()
    {
        var entries = LoadChat(
            """{"timestamp":"2026-05-22T02:34:40.000Z","sessionId":"session-a","requestId":"req-a","message":{"id":"msg_a","model":"claude-sonnet-4-20250514","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":100}},"costUSD":1}""",
            """{"timestamp":"2026-05-22T02:34:40.000Z","sessionId":"session-a","requestId":"req-a","message":{"id":"msg_a","model":"claude-sonnet-4-20250514","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":100,"cache_creation":{"ephemeral_5m_input_tokens":40,"ephemeral_1h_input_tokens":60}}}}""");

        var entry = Assert.Single(entries);
        Assert.Equal(60, entry.CacheCreation1hTokens);
        Assert.Null(entry.RecordedCostUsd);
    }

    // --- advisor (#1423) i AgentProgress (#977) ---

    private const string AdvisorLine =
        """{"type":"assistant","timestamp":"2026-05-22T02:34:40.000Z","sessionId":"session-a","requestId":"req-1","costUSD":0.5,"message":{"id":"msg-1","model":"claude-sonnet-4-5","usage":{"input_tokens":4,"output_tokens":244,"cache_creation_input_tokens":12323,"cache_read_input_tokens":140030,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":11142},"iterations":[{"type":"message","input_tokens":2,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":11142}},{"type":"advisor_message","model":"claude-opus-4-6","input_tokens":77025,"output_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":999,"ephemeral_1h_input_tokens":999}},{"type":"message","input_tokens":2,"cache_creation":{"ephemeral_5m_input_tokens":1181,"ephemeral_1h_input_tokens":0}}]}}}""";

    [Fact]
    public void Advisor_iterations_are_separate_entries_with_their_own_model()
    {
        var entries = LoadChat(AdvisorLine, AdvisorLine);

        Assert.Equal(2, entries.Count);
        var main = Assert.Single(entries, entry => entry.Model == "claude-sonnet-4-5");
        var advisor = Assert.Single(entries, entry => entry.Model == "claude-opus-4-6");
        Assert.Equal(4, main.InputTokens);
        Assert.Equal(0.5, main.RecordedCostUsd);
        Assert.Equal(77025, advisor.InputTokens);
        Assert.Equal(300, advisor.OutputTokens);
        Assert.Equal(1998, advisor.CacheCreationTokens);
        Assert.Equal(999, advisor.CacheCreation1hTokens);
        Assert.Null(advisor.RecordedCostUsd);
        Assert.StartsWith("msg-1:advisor:0", advisor.DedupeKey);
        Assert.True(ClaudeSource.IsAdvisor(advisor));
        Assert.False(ClaudeSource.IsAdvisor(main));
    }

    [Fact]
    public void Cache_write_total_comes_from_message_iteration_splits()
    {
        // usage_wire.rs:sums_message_iteration_breakdowns_instead_of_the_truncated_top_level (#2085).
        var main = Assert.Single(LoadChat(AdvisorLine), entry => entry.Model == "claude-sonnet-4-5");

        Assert.Equal(12323, main.CacheCreationTokens);
        Assert.Equal(11142, main.CacheCreation1hTokens);
    }

    [Theory]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":999,"cache_creation":{"ephemeral_5m_input_tokens":3,"ephemeral_1h_input_tokens":4}}""", 7, 4)]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":12323,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":11142},"iterations":[{"type":"message","cache_creation":{"ephemeral_1h_input_tokens":11142}},{"type":"message","cache_creation_input_tokens":1181}]}""", 11142, 11142)]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":30,"iterations":[{"type":"message","cache_creation":{"ephemeral_5m_input_tokens":10}},{"type":"message","cache_creation":{"ephemeral_1h_input_tokens":20,"ephemeral_2h_input_tokens":5}}]}""", 30, 20)]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation":{"ephemeral_5m_input_tokens":3,"ephemeral_1h_input_tokens":4},"iterations":[{"type":"message","cache_creation":{"ephemeral_5m_input_tokens":-1}}]}""", 7, 4)]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation":{"ephemeral_5m_input_tokens":3,"ephemeral_1h_input_tokens":4},"iterations":"n/a"}""", 7, 4)]
    [InlineData("""{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":50}""", 50, 0)]
    public void Cache_write_split_decides_the_total(string usage, long expectedTotal, long expectedOneHour)
    {
        var entry = Assert.Single(LoadChat(
            $$$$"""{"timestamp":"2026-01-02T10:00:00Z","requestId":"r","message":{"id":"m","model":"claude-sonnet-4-5","usage":{{{{usage}}}}}}"""));

        Assert.Equal(expectedTotal, entry.CacheCreationTokens);
        Assert.Equal(expectedOneHour, entry.CacheCreation1hTokens);
    }

    [Fact]
    public void Malformed_advisor_is_diagnosed_and_parent_kept()
    {
        var (entries, diagnostics) = Load(("projects/project-a/s.jsonl",
        [
            """{"timestamp":"2026-01-02T10:00:00Z","requestId":"r","message":{"id":"m","model":"claude-sonnet-4-5","usage":{"input_tokens":1,"output_tokens":2,"iterations":[{"type":"advisor_message","model":"adv","input_tokens":"x","output_tokens":1},{"type":"advisor_message","model":"adv","input_tokens":5,"output_tokens":1},{"type":"advisor_message","model":"adv","input_tokens":5,"output_tokens":1,"speed":null},{"type":"message","model":null}]}}}"""
        ]));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Model == "adv" && entry.InputTokens == 5);
        Assert.Equal(1, diagnostics.SkippedLines);
        Assert.Contains(diagnostics.Reasons.Keys, reason => reason.Contains("advisor"));
    }

    [Fact]
    public void Agent_progress_usage_is_counted_with_sidechain_and_advisors()
    {
        var entries = LoadChat(
            """{"type":"progress","timestamp":null,"sessionId":"session-a","data":{"message":{"timestamp":"2026-01-02T10:00:00Z","requestId":"req-sub","isSidechain":true,"costUSD":0.25,"message":{"id":"msg-sub","model":"claude-haiku-4-5","usage":{"input_tokens":7,"output_tokens":3,"iterations":[{"type":"advisor_message","model":"claude-opus-4-6","input_tokens":11,"output_tokens":1}]}}}}}""");

        Assert.Equal(2, entries.Count);
        var sub = Assert.Single(entries, entry => entry.Model == "claude-haiku-4-5");
        Assert.Equal(7, sub.InputTokens);
        Assert.Equal(0.25, sub.RecordedCostUsd);
        Assert.Equal(0, sub.Preference);
        Assert.Contains(entries, entry => entry.Model == "claude-opus-4-6" && entry.InputTokens == 11);
    }

    [Fact]
    public void Direct_record_wins_over_an_agent_progress_wrapper_on_the_same_line()
    {
        var entry = Assert.Single(LoadChat(
            """{"timestamp":"2026-01-02T10:00:00Z","requestId":"r","message":{"id":"direct","model":"claude-sonnet-4-5","usage":{"input_tokens":1,"output_tokens":1}},"data":{"message":{"timestamp":"2026-01-02T10:00:00Z","message":{"id":"wrapped","model":"x","usage":{"input_tokens":9,"output_tokens":9}}}}}"""));

        Assert.StartsWith("direct", entry.DedupeKey);
    }

    // --- odrzucane rekordy (usage_wire.rs:92-127, record.rs:156-282) ---

    [Theory]
    [InlineData("sessionId")]
    [InlineData("requestId")]
    [InlineData("version")]
    [InlineData("costUSD")]
    [InlineData("isApiErrorMessage")]
    [InlineData("message.id")]
    [InlineData("message.model")]
    [InlineData("usage.speed")]
    [InlineData("usage.cache_read_input_tokens")]
    [InlineData("usage.cache_creation_input_tokens")]
    public void Null_in_a_read_field_rejects_the_record_with_a_reason(string field)
    {
        var values = new Dictionary<string, string>
        {
            ["sessionId"] = "\"session-a\"", ["requestId"] = "\"request-a\"", ["version"] = "\"1.2.3\"", ["costUSD"] = "1",
            ["isApiErrorMessage"] = "false", ["message.id"] = "\"msg-a\"", ["message.model"] = "\"model-a\"",
            ["usage.speed"] = "\"standard\"", ["usage.cache_read_input_tokens"] = "0", ["usage.cache_creation_input_tokens"] = "0"
        };
        values[field] = "null";
        var line = $$$$"""{"timestamp":"2026-01-02T10:00:00Z","sessionId":{{{{values["sessionId"]}}}},"requestId":{{{{values["requestId"]}}}},"version":{{{{values["version"]}}}},"costUSD":{{{{values["costUSD"]}}}},"isApiErrorMessage":{{{{values["isApiErrorMessage"]}}}},"message":{"id":{{{{values["message.id"]}}}},"model":{{{{values["message.model"]}}}},"usage":{"input_tokens":10,"output_tokens":1,"speed":{{{{values["usage.speed"]}}}},"cache_read_input_tokens":{{{{values["usage.cache_read_input_tokens"]}}}},"cache_creation_input_tokens":{{{{values["usage.cache_creation_input_tokens"]}}}}}}}""";

        var (entries, diagnostics) = Load(("projects/p/s.jsonl", [line]));

        Assert.Empty(entries);
        var reason = Assert.Single(diagnostics.Reasons.Keys);
        Assert.Contains("null w polu " + field, reason);
    }

    [Fact]
    public void Missing_optional_fields_and_nulls_in_unread_members_are_accepted()
    {
        var (entries, diagnostics) = Load(("projects/p/s.jsonl",
        [
            """{"timestamp":"2026-01-02T10:00:00Z","cwd":null,"isSidechain":null,"message":{"content":[{"id":null}],"usage":{"input_tokens":1,"output_tokens":2,"iterations":[{"type":"message","model":null}]}}}"""
        ]));

        Assert.Single(entries);
        Assert.Equal(0, diagnostics.SkippedLines);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"id":"m","usage":{"input_tokens":1,"output_tokens":2,"speed":"turbo"}}}""", "prędkość")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","version":"2.1","message":{"id":"m","usage":{"input_tokens":1,"output_tokens":2}}}""", "semver")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","sessionId":"","message":{"id":"m","usage":{"input_tokens":1,"output_tokens":2}}}""", "pusty sessionId")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","requestId":"","message":{"id":"m","usage":{"input_tokens":1,"output_tokens":2}}}""", "pusty requestId")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"id":"","usage":{"input_tokens":1,"output_tokens":2}}}""", "pusty message.id")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"id":"m","model":"","usage":{"input_tokens":1,"output_tokens":2}}}""", "pusty message.model")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"usage":{"input_tokens":null,"output_tokens":2}}}""", "input_tokens")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"usage":{"input_tokens":1}}}""", "input_tokens")]
    [InlineData("""{"timestamp":"not a date","message":{"usage":{"input_tokens":1,"output_tokens":2}}}""", "timestamp")]
    [InlineData("""{"timestamp":"2026-01-02T10:00:00Z","message":{"usage":{"input_tokens":9223372036854775807,"output_tokens":2}}}""", "zakresem")]
    public void Malformed_records_are_rejected_with_a_reason(string line, string reasonPart)
    {
        var (entries, diagnostics) = Load(("projects/p/s.jsonl", [line]));

        Assert.Empty(entries);
        Assert.Contains(reasonPart, Assert.Single(diagnostics.Reasons.Keys));
    }

    [Theory]
    [InlineData("1.0.33", true)]
    [InlineData("2.1.268-beta", true)]
    [InlineData("2.1", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2.", false)]
    public void Version_must_have_a_semver_prefix(string version, bool valid)
    {
        Assert.Equal(valid, ClaudeRecordReader.IsSemverPrefix(version));
    }

    // --- <synthetic>, fast, ścieżki ---

    [Fact]
    public void Synthetic_records_are_kept_without_a_display_model()
    {
        var entry = Assert.Single(LoadChat(
            """{"timestamp":"2026-01-02T10:00:00Z","requestId":"r","message":{"id":"m","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}"""));

        Assert.Null(entry.Model);
    }

    [Fact]
    public void Fast_entries_get_a_fast_model_and_are_priced_as_the_base_model()
    {
        var entry = Assert.Single(LoadChat(
            """{"timestamp":"2026-01-02T10:00:00Z","requestId":"r","message":{"id":"m","model":"claude-opus-4-6","usage":{"input_tokens":1000,"output_tokens":100,"speed":"fast"}}}"""));
        var source = new ClaudeSource();
        var calculator = new CostCalculator(PricingCatalog.Embedded(), CostMode.Calculate, _ => source);

        Assert.Equal("claude-opus-4-6-fast", entry.Model);
        Assert.Equal(SpeedTier.Fast, entry.Speed);
        Assert.Equal(["claude-opus-4-6"], source.PricingCandidates(entry));
        var cost = calculator.Calculate(entry);
        Assert.False(cost.IsUnpriced);
        Assert.True(cost.CostUsd > calculator.Calculate(entry with { Model = "claude-opus-4-6", Speed = SpeedTier.Standard }).CostUsd);
    }

    [Theory]
    [InlineData("project-a/session-a.jsonl", "session-a")]
    [InlineData("project-a/session-a/chat.jsonl", "session-a")]
    [InlineData("project-a/session-a/subagents/worker.jsonl", "session-a")]
    [InlineData("project-a/session-a/subagents/workflows/wf_1/agent-a.jsonl", "session-a")]
    public void Session_id_comes_from_the_file_path(string relative, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "projects");

        Assert.Equal(expected, ClaudeSource.SessionFromPath(root, Path.Combine(root, relative)));
    }

    [Fact]
    public void Main_transcript_keeps_the_claude_session_id_and_subagents_join_it()
    {
        var (entries, _) = Load(
            ("projects/project-a/0f1e.jsonl", [Record("0f1e", "m1", "r1", 1)]),
            ("projects/project-a/0f1e/subagents/agent-1.jsonl", [Record("other-recorded-id", "m2", "r2", 1, sidechain: true)]));

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("0f1e", entry.SessionId));
        Assert.All(entries, entry => Assert.Equal("project-a", entry.Project));
    }

    [Fact]
    public void Sidechain_preference_survives_the_ledger()
    {
        using var directory = new TempDirectory();
        directory.Write("claude/projects/p/s.jsonl", Record("s", "msg-parent", "req-parent", 20));
        directory.Write("claude/projects/p/s/subagents/btw.jsonl", Record("s", "msg-parent", "req-replay", 50_000, sidechain: true));
        var config = new OnionConfig { Timezone = "UTC", Offline = true };
        config.Sources["claude"] = new SourceSettings { Paths = [Path.Combine(directory.Path, "claude")] };
        using var engine = new UsageEngine(config, new UsageLedger(Path.Combine(directory.Path, "ledger.db")), [new ClaudeSource()]);
        engine.Ledger.Sync(engine.Sources, config);

        var entry = Assert.Single(engine.LoadEntries());

        Assert.Equal(20, entry.CacheReadTokens);
        Assert.Equal(1, entry.Preference);
    }
}
