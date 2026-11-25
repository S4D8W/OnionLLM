using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class OpenCodeSourceTests
{
    private const long Created = 1_767_312_000_000; // 2026-01-02T00:00:00Z

    private static List<Models.UsageEntry> Parse(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new OpenCodeSource(), TestData.ConfigWith(OpenCodeSource.SourceId, directory.Path), diagnostics);

    private static void Execute(string database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string V2Payload(long input, long output, double cost = 0.01, long created = Created, string extra = "") =>
        $"{{{extra}\"model\":{{\"id\":\"gpt-test\",\"providerID\":\"openai\"}},\"time\":{{\"created\":{created}}},\"tokens\":{{\"input\":{input},\"output\":{output}}},\"cost\":{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private static void CreateV2(string database) => Execute(database, """
        CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER NOT NULL DEFAULT 0, time_created INTEGER, data TEXT);
        CREATE TABLE session_v2 (id TEXT PRIMARY KEY, time_created INTEGER, cost REAL, tokens_input INTEGER, tokens_output INTEGER,
            tokens_cache_read INTEGER, tokens_cache_write INTEGER, tokens_reasoning INTEGER, model TEXT, directory TEXT,
            fork_session_id TEXT, fork_boundary TEXT);
        """);

    private static void InsertV2(string database, string id, string session, string type, long seq, string data) =>
        Execute(database, "INSERT INTO session_message (id, session_id, type, seq, time_created, data) VALUES ($id, $s, $t, $seq, $c, $d)",
            ("$id", id), ("$s", session), ("$t", type), ("$seq", seq), ("$c", Created + seq), ("$d", data));

    [Fact]
    public void Legacy_json_message_maps_tokens_reasoning_and_recorded_cost()
    {
        using var directory = new TempDirectory();
        directory.Write("storage/message/ses_1/msg_1.json", """
            {"id":"msg_1","sessionID":"ses_1","role":"assistant","modelID":"claude-sonnet-4-5","providerID":"anthropic",
             "time":{"created":1767312000000,"completed":1767312005000},
             "tokens":{"input":100,"output":50,"reasoning":5,"cache":{"read":10,"write":20}},"cost":0.02}
            """);
        directory.Write("storage/message/ses_1/msg_2.json", """
            {"id":"msg_2","sessionID":"ses_1","role":"user","time":{"created":1767312000000}}
            """);
        directory.Write("storage/message/ses_1/msg_3.json", """
            {"id":"msg_3","sessionID":"ses_1","modelID":"gpt-5","providerID":"openai","time":{"created":1767312000000},"tokens":{"input":1,"output":1},"cost":0}
            """);

        var entries = Parse(directory).OrderBy(entry => entry.DedupeKey).ToList();

        Assert.Equal(2, entries.Count);
        var entry = entries[0];
        Assert.Equal("ses_1", entry.SessionId);
        Assert.Equal("claude-sonnet-4-5", entry.Model);
        Assert.Equal(Created, entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(55, entry.OutputTokens);
        Assert.Equal(5, entry.ReasoningTokens);
        Assert.Equal(10, entry.CacheReadTokens);
        Assert.Equal(20, entry.CacheCreationTokens);
        Assert.Equal(0.02, entry.RecordedCostUsd);
        Assert.Equal("msg_1", entry.DedupeKey);
        Assert.Null(entries[1].RecordedCostUsd); // cost 0 = nieznany
    }

    [Fact]
    public void V1_database_rows_and_legacy_json_copies_are_deduplicated()
    {
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "opencode.db");
        const string payload = """{"id":"msg_1","sessionID":"ses_1","modelID":"gpt-5","providerID":"openai","time":{"created":1767312000000},"tokens":{"input":10,"output":2,"cache":{"read":0,"write":0}}}""";
        Execute(database, """
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);
            CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
            INSERT INTO session VALUES ('ses_1', 'C:\repo\shop');
            """);
        Execute(database, "INSERT INTO message VALUES ('msg_1', 'ses_1', 1767312000000, $d)", ("$d", payload));
        directory.Write("storage/message/ses_1/msg_1.json", payload);

        var parsed = Parse(directory);
        var entries = Deduplicator.Apply(parsed);

        Assert.Equal(2, parsed.Count);
        var entry = Assert.Single(entries);
        Assert.Equal(@"C:\repo\shop", entry.ProjectPath);
        Assert.Equal("shop", entry.Project);
    }

    [Fact]
    public void V2_rows_count_assistant_and_completed_compactions_only()
    {
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "opencode.db");
        CreateV2(database);
        InsertV2(database, "a1", "s1", "assistant", 0, V2Payload(10, 2));
        InsertV2(database, "c1", "s1", "compaction", 1, V2Payload(5, 1, extra: "\"status\":\"completed\","));
        InsertV2(database, "c2", "s1", "compaction", 2, V2Payload(500, 100, extra: "\"status\":\"failed\","));
        InsertV2(database, "u1", "s1", "user", 3, "{\"text\":\"hi\"}");
        // Brak time.created w danych → czas z kolumny time_created.
        InsertV2(database, "a2", "s1", "assistant", 4, "{\"modelID\":\"gpt-test\",\"providerID\":\"openai\",\"tokens\":{\"input\":3,\"output\":1}}");
        // Brak dostawcy → wpis pominięty (jak w ccusage).
        InsertV2(database, "a3", "s1", "assistant", 5, "{\"model\":{\"id\":\"gpt-test\"},\"time\":{\"created\":1767312000000},\"tokens\":{\"input\":3,\"output\":1}}");

        var entries = Parse(directory).OrderBy(entry => entry.DedupeKey).ToList();

        Assert.Equal(["a1", "a2", "compaction:c1"], entries.Select(entry => entry.DedupeKey!).ToArray());
        Assert.Equal(Created + 4, entries[1].TimestampMs);
        Assert.All(entries, entry => Assert.Equal("gpt-test", entry.Model));
    }

    [Theory]
    [InlineData("through", new[] { "fork-own" })]
    [InlineData("before", new[] { "fork-copy-boundary", "fork-own" })]
    public void Fork_copies_of_parent_history_are_skipped(string boundaryType, string[] expectedForkIds)
    {
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "opencode.db");
        CreateV2(database);
        foreach (var (id, seq, input) in new[] { ("parent-msg-1", 0L, 100L), ("parent-msg-2", 1L, 200L), ("boundary-msg", 2L, 300L), ("parent-msg-4", 3L, 400L) })
        {
            InsertV2(database, id, "parent", "assistant", seq, V2Payload(input, 10));
        }

        foreach (var (id, seq, input) in new[] { ("fork-copy-1", 0L, 100L), ("fork-copy-2", 1L, 200L), ("fork-copy-boundary", 2L, 300L) })
        {
            InsertV2(database, id, "fork", "assistant", seq, V2Payload(input, 10));
        }

        InsertV2(database, "fork-own", "fork", "assistant", 4, V2Payload(40, 4, 0.02));
        Execute(database, "INSERT INTO session_v2 (id, fork_session_id, fork_boundary) VALUES ('fork', 'parent', $b)",
            ("$b", $"{{\"type\":\"{boundaryType}\",\"messageID\":\"boundary-msg\"}}"));

        var entries = Deduplicator.Apply(Parse(directory));

        Assert.Equal(1000, entries.Where(entry => entry.SessionId == "parent").Sum(entry => entry.InputTokens));
        Assert.Equal(expectedForkIds, entries.Where(entry => entry.SessionId == "fork").Select(entry => entry.DedupeKey!).Order().ToArray());
    }

    [Fact]
    public void Session_counters_are_summaries_used_only_without_detailed_rows()
    {
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "opencode.db");
        CreateV2(database);
        InsertV2(database, "a1", "with-rows", "assistant", 0, V2Payload(10, 2));
        Execute(database, """
            INSERT INTO session_v2 (id, time_created, cost, tokens_input, tokens_output, tokens_cache_read, tokens_cache_write, tokens_reasoning, model, directory)
            VALUES ('with-rows', 1767312000000, 0.5, 999, 99, 0, 0, 0, '{"id":"gpt-test","providerID":"openai"}', NULL),
                   ('reverted', 1767312000000, 0.25, 300, 30, 100, 50, 7, '{"id":"gpt-test","providerID":"openai"}', '/home/me/api'),
                   ('empty', 1767312000000, 0, 0, 0, 0, 0, 0, NULL, NULL);
            """);

        var parsed = Parse(directory);
        var entries = Deduplicator.Apply(parsed);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(2, entries.Count);
        var summary = Assert.Single(entries, entry => entry.IsSessionSummary);
        Assert.Equal("reverted", summary.SessionId);
        Assert.Equal(300, summary.InputTokens);
        Assert.Equal(37, summary.OutputTokens);
        Assert.Equal(100, summary.CacheReadTokens);
        Assert.Equal(50, summary.CacheCreationTokens);
        Assert.Equal(0.25, summary.RecordedCostUsd);
        Assert.Equal("api", summary.Project);
    }

    [Fact]
    public void Channel_database_is_used_when_the_default_one_is_missing()
    {
        using var directory = new TempDirectory();
        directory.Write("opencode-x.txt", "");
        var database = Path.Combine(directory.Path, "opencode-beta.db");
        CreateV2(database);
        InsertV2(database, "a1", "s1", "assistant", 0, V2Payload(10, 2));

        Assert.Equal(database, OpenCodeSource.DatabasePath(directory.Path));
        Assert.Single(Parse(directory));
    }

    [Theory]
    [InlineData("{\"id\":\"gpt-5\",\"providerID\":\"openai\"}", "gpt-5", "openai")]
    [InlineData("\"gpt-5\"", "gpt-5", "unknown")]
    [InlineData("gpt-5", "gpt-5", "unknown")]
    [InlineData(null, "unknown", "unknown")]
    public void Session_model_column_is_parsed(string? value, string model, string provider)
    {
        Assert.Equal((model, provider), OpenCodeSource.ParseSessionModel(value));
    }

    [Fact]
    public void Pricing_candidates_follow_opencode_aliases_normalization_and_provider()
    {
        // ccusage opencode/src/parser/tests.rs: creates_open_code_provider_and_normalized_model_candidates + snapshot geminiAlias.
        Assert.Equal(
            ["claude-sonnet-4.5", "claude-sonnet-4-5", "github_copilot/claude-sonnet-4.5", "github_copilot/claude-sonnet-4-5"],
            OpenCodeSource.ModelCandidates("claude-sonnet-4.5", "github-copilot"));
        Assert.Equal(
            ["gemini-3-pro-high", "gemini-3-pro-preview", "google/gemini-3-pro-high", "google/gemini-3-pro-preview"],
            OpenCodeSource.ModelCandidates("gemini-3-pro-high", "google"));
        Assert.Equal(["k2p6", "kimi-k2.6"], OpenCodeSource.ModelCandidates("k2p6", "unknown"));
        Assert.Equal(["claude-opus-46", "claude-opus-4-6"], OpenCodeSource.ModelCandidates("claude-opus-46", null));
        Assert.Equal(["gpt-test"], OpenCodeSource.ModelCandidates("gpt-test", "unknown"));
        Assert.Equal(["gemini-4", "vertex_ai/gemini-4"], OpenCodeSource.ModelCandidates("gemini-4", "google-vertex"));
    }

    [Fact]
    public void Provider_is_recorded_and_used_for_candidates()
    {
        using var directory = new TempDirectory();
        directory.Write("storage/message/ses_1/msg_1.json", """
            {"id":"msg_1","sessionID":"ses_1","modelID":"claude-sonnet-4.5","providerID":"GitHub-Copilot","time":{"created":1767312000000},"tokens":{"input":1,"output":1}}
            """);
        var source = new OpenCodeSource();

        var entry = Assert.Single(Parse(directory));

        Assert.Equal("github-copilot", entry.Provider);
        Assert.Equal("github_copilot/claude-sonnet-4-5", source.PricingCandidates(entry).Last());
    }

    [Fact]
    public void Include_claude_agent_setting_clears_the_mirror_link()
    {
        var mirrored = TestData.Entry("2026-01-02T00:00:00Z", 1, source: OpenCodeSource.SourceId) with { MirrorOf = "claude:abc" };
        var source = new OpenCodeSource();

        Assert.Equal("claude:abc", source.ApplySettings(mirrored, new Config.SourceSettings()).MirrorOf);
        Assert.Null(source.ApplySettings(mirrored, new Config.SourceSettings { IncludeClaudeAgent = true }).MirrorOf);
    }

    [Fact]
    public async Task Engine_counts_claude_agent_turns_when_the_setting_is_on()
    {
        using var directory = new TempDirectory();
        directory.Write("claude/projects/demo/claude-session.jsonl",
            TestData.ClaudeLine("2026-04-24T03:06:40Z", "msg-1", "req-1", 100, 50, session: "claude-session"));
        directory.Write("opencode/storage/message/oc/msg_a.json",
            "{\"id\":\"msg_a\",\"sessionID\":\"oc\",\"modelID\":\"claude-opus-4-6\",\"providerID\":\"claude-agent\",\"time\":{\"created\":1777000000000}," +
            "\"content\":[{\"type\":\"text\",\"native\":{\"sessionID\":\"claude-session\"}}],\"tokens\":{\"input\":100,\"output\":50}}");
        var config = new Config.OnionConfig { Timezone = "UTC", Offline = true };
        config.Sources["claude"] = new Config.SourceSettings { Paths = [Path.Combine(directory.Path, "claude")] };
        config.Sources["opencode"] = new Config.SourceSettings { Paths = [Path.Combine(directory.Path, "opencode")] };
        using var engine = new UsageEngine(config, new UsageLedger(Path.Combine(directory.Path, "ledger.db")),
            [new ClaudeSource(), new OpenCodeSource()]);
        await engine.SyncAsync(force: true);

        Assert.Empty(engine.LoadEntries(sources: ["opencode"]));

        config.Sources["opencode"].IncludeClaudeAgent = true;
        engine.UpdateConfig(config);

        Assert.Single(engine.LoadEntries(sources: ["opencode"]));
    }

    [Fact]
    public void Session_summary_replaces_compactions_of_a_session_without_messages()
    {
        // ccusage opencode/src/loader.rs EntryCollector::finish (#2050): suma obejmuje kompakcję.
        using var directory = new TempDirectory();
        var database = Path.Combine(directory.Path, "opencode.db");
        CreateV2(database);
        InsertV2(database, "a1", "with-rows", "assistant", 0, V2Payload(10, 2));
        InsertV2(database, "c1", "with-rows", "compaction", 1, V2Payload(5, 1, extra: "\"status\":\"completed\","));
        InsertV2(database, "c2", "reverted", "compaction", 0, V2Payload(7, 1, extra: "\"status\":\"completed\","));
        InsertV2(database, "c3", "no-summary", "compaction", 0, V2Payload(9, 1, extra: "\"status\":\"completed\","));
        Execute(database, """
            INSERT INTO session_v2 (id, time_created, cost, tokens_input, tokens_output, tokens_cache_read, tokens_cache_write, tokens_reasoning, model)
            VALUES ('with-rows', 1767312000000, 0.5, 999, 99, 0, 0, 0, '{"id":"gpt-test","providerID":"openai"}'),
                   ('reverted', 1767312000000, 0.25, 300, 30, 0, 0, 0, '{"id":"gpt-test","providerID":"openai"}');
            """);

        var entries = Deduplicator.Apply(Parse(directory));

        Assert.Equal(["a1", "compaction:c1"], entries.Where(entry => entry.SessionId == "with-rows").Select(entry => entry.DedupeKey!).Order().ToArray());
        var reverted = Assert.Single(entries, entry => entry.SessionId == "reverted");
        Assert.True(reverted.IsSessionSummary);
        Assert.True(OpenCodeSource.IsSessionTotal(reverted));
        Assert.Equal(9, Assert.Single(entries, entry => entry.SessionId == "no-summary").InputTokens);

        // Bez sum (raport z zakresem dat) kompakcja sesji po revercie zostaje.
        var withoutSummaries = Deduplicator.Apply(Parse(directory).Where(entry => !OpenCodeSource.IsSessionTotal(entry)));
        Assert.Equal("compaction:c2", Assert.Single(withoutSummaries, entry => entry.SessionId == "reverted").DedupeKey);
    }
}
