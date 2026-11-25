using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class OpenClawSourceTests
{
    private static List<Models.UsageEntry> Load(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new OpenClawSource(), TestData.ConfigWith(OpenClawSource.SourceId, directory.Path), diagnostics);

    private static void CreateAgentDatabase(string path, params (string Session, long CreatedAt, string Json)[] rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE transcript_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, event_json TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq))";
            create.ExecuteNonQuery();
        }

        var seq = 0;
        foreach (var (session, createdAt, json) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO transcript_events VALUES ($s, $q, $j, $c)";
            insert.Parameters.AddWithValue("$s", session);
            insert.Parameters.AddWithValue("$q", seq++);
            insert.Parameters.AddWithValue("$j", json);
            insert.Parameters.AddWithValue("$c", createdAt);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Model_change_sets_model_for_assistant_usage()
    {
        using var directory = new TempDirectory();
        directory.Write("agents/main/sessions/abc.jsonl", string.Join('\n',
            """{"type":"model_change","provider":"openai-codex","modelId":"gpt-5.2"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1660,"output":55,"cacheRead":108928,"cost":{"total":0.02}},"timestamp":1769753935279}}"""));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("abc", entry.SessionId);
        Assert.Equal("main", entry.Project);
        Assert.Equal("gpt-5.2", entry.Model);
        Assert.Equal(1769753935279, entry.TimestampMs);
        Assert.Equal(1660, entry.InputTokens);
        Assert.Equal(55, entry.OutputTokens);
        Assert.Equal(108928, entry.CacheReadTokens);
        Assert.Equal(0.02, entry.RecordedCostUsd);
    }

    [Fact]
    public void Archived_transcripts_are_read_and_duplicates_collapse()
    {
        using var directory = new TempDirectory();
        const string line = """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":1,"output":1,"totalTokens":2},"timestamp":1769753935279}}""";
        directory.Write("agents/main/sessions/session.jsonl", line + "\n" + line);
        directory.Write("agents/main/sessions/old.jsonl.deleted.1700000000000", line.Replace("1769753935279", "1769753900000"));
        directory.Write("agents/main/sessions/notes.json", line);

        var entries = Deduplicator.Apply(Load(directory));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.SessionId == "old");
    }

    [Fact]
    public void Model_snapshot_and_envelope_timestamp_fallbacks()
    {
        using var directory = new TempDirectory();
        directory.Write("agents/main/sessions/s.jsonl", string.Join('\n',
            """{"type":"custom","customType":"model-snapshot","data":{"provider":"anthropic","modelId":"claude-sonnet-4"}}""",
            """{"type":"message","timestamp":"2026-01-02T00:00:00Z","message":{"role":"assistant","timestamp":"invalid","usage":{"input":10,"output":2}}}""",
            """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":2}}}"""));
        var diagnostics = new ParseDiagnostics();

        var entry = Assert.Single(Load(directory, diagnostics));

        Assert.Equal("claude-sonnet-4", entry.Model);
        Assert.Equal(1767312000000, entry.TimestampMs);
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Sqlite_events_are_read_and_win_over_migrated_jsonl_copies()
    {
        using var directory = new TempDirectory();
        CreateAgentDatabase(System.IO.Path.Combine(directory.Path, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1769753935279, """{"id":"evt-1","type":"model_change","modelId":"gpt-5.2","provider":"openai-codex"}"""),
            ("session-a", 1769753935279, """{"id":"evt-2","type":"message","message":{"role":"assistant","usage":{"input":1660,"output":55,"cacheRead":108928,"cost":{"total":0.03}}}}"""));
        // Kopia JSONL po migracji: ten sam czas, model i tokeny, ale bez kosztu.
        directory.Write("agents/main/sessions/session-a.jsonl", string.Join('\n',
            """{"type":"model_change","modelId":"gpt-5.2"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1660,"output":55,"cacheRead":108928},"timestamp":1769753935279}}"""));

        var entries = Load(directory);
        var deduplicated = Deduplicator.Apply(entries);

        Assert.Equal(2, entries.Count);
        var entry = Assert.Single(deduplicated);
        Assert.Equal(0.03, entry.RecordedCostUsd);
        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal(1769753935279, entry.TimestampMs);
    }

    // Fixture z ccusage openclaw/src/loader.rs (assistant_event).
    private static string AssistantEvent(string? id, double cost) =>
        "{" + (id is null ? "" : $"\"id\":\"{id}\",") +
        "\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"model\":\"gpt-5.2\",\"usage\":{\"input\":10,\"output\":20,\"cost\":{\"total\":" +
        cost.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}},\"timestamp\":1769753935279}}";

    private static List<Models.UsageEntry> LoadDeduplicated(TempDirectory directory)
    {
        var source = new OpenClawSource();
        return Deduplicator.Apply(TestData.ParseAll(source, TestData.ConfigWith(OpenClawSource.SourceId, directory.Path)), _ => source);
    }

    private static string AgentDatabase(TempDirectory directory) =>
        System.IO.Path.Combine(directory.Path, "agents", "main", "agent", "openclaw-agent.sqlite");

    [Fact]
    public void Jsonl_lines_that_differ_only_in_cost_are_kept_without_a_sqlite_copy()
    {
        using var directory = new TempDirectory();
        directory.Write("agents/main/sessions/session.jsonl", AssistantEvent(null, 0.50) + "\n" + AssistantEvent(null, 0.49));

        var entries = LoadDeduplicated(directory);

        Assert.Equal([0.49, 0.50], entries.Select(entry => entry.RecordedCostUsd!.Value).Order().ToArray());
    }

    [Fact]
    public void Sqlite_event_replaces_every_migrated_jsonl_copy()
    {
        using var directory = new TempDirectory();
        directory.Write("agents/main/sessions/session.jsonl", AssistantEvent(null, 0.50) + "\n" + AssistantEvent(null, 0.49));
        CreateAgentDatabase(AgentDatabase(directory), ("session", 1769753935279, AssistantEvent("evt-sqlite", 0.51)));

        var entry = Assert.Single(LoadDeduplicated(directory));

        Assert.Equal(0.51, entry.RecordedCostUsd);
        Assert.Equal(1, entry.Preference);
    }

    [Fact]
    public void Sqlite_events_with_equal_content_and_distinct_ids_are_kept()
    {
        using var directory = new TempDirectory();
        CreateAgentDatabase(AgentDatabase(directory),
            ("session", 1769753935279, AssistantEvent("evt-1", 0.51)),
            ("session", 1769753935279, AssistantEvent("evt-2", 0.51)));

        Assert.Equal(2, LoadDeduplicated(directory).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Id_less_sqlite_event_matching_an_identified_one_is_dropped_in_either_order(bool identifiedFirst)
    {
        using var directory = new TempDirectory();
        var identified = ("session", 1769753935279L, AssistantEvent("evt-x", 0.51));
        var idLess = ("session", 1769753935279L, AssistantEvent(null, 0.51));
        CreateAgentDatabase(AgentDatabase(directory), identifiedFirst ? [identified, idLess] : [idLess, identified]);

        var entry = Assert.Single(LoadDeduplicated(directory));

        Assert.EndsWith("evt-x", entry.SourceData);
    }

    [Fact]
    public void Provider_comes_from_the_message_or_the_last_model_change()
    {
        // ccusage openclaw/src/parser.rs: version = provider; a_vertex_provider_bills_the_vertex_listing_in_every_spelling.
        using var directory = new TempDirectory();
        directory.Write("agents/main/sessions/abc.jsonl", string.Join('\n',
            """{"type":"model_change","provider":"openai-codex","modelId":"gpt-5.2"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}""",
            """{"type":"message","message":{"role":"assistant","model":"gemini-4-argon","provider":"Vertex-AI","usage":{"input":2,"output":1},"timestamp":1769753935280}}"""));
        var source = new OpenClawSource();

        var entries = Load(directory).OrderBy(entry => entry.TimestampMs).ToList();

        Assert.Equal("openai-codex", entries[0].Provider);
        Assert.Equal(["[openclaw] gpt-5.2", "gpt-5.2"], source.PricingCandidates(entries[0]));
        Assert.Equal("vertex_ai", entries[1].Provider);
        Assert.Equal(["[openclaw] gemini-4-argon", "vertex_ai/gemini-4-argon", "gemini-4-argon"], source.PricingCandidates(entries[1]));
    }
}
