using Microsoft.Data.Sqlite;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class KiloSourceTests
{
    private static void CreateDatabase(string directory, params (string Id, string Session, string Data)[] rows)
    {
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, KiloSource.DatabaseFileName)};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE message (id TEXT, session_id TEXT, data TEXT);
                CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
                INSERT INTO session VALUES ('session-a', '/home/me/kilo-app');
                """;
            create.ExecuteNonQuery();
        }

        foreach (var (id, session, data) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO message (id, session_id, data) VALUES ($id, $s, $d)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$s", session);
            insert.Parameters.AddWithValue("$d", data);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Assistant_messages_map_tokens_cost_and_session()
    {
        using var directory = new TempDirectory();
        // Fixture z testów ccusage (loader.rs).
        CreateDatabase(directory.Path,
            ("row-1", "session-a", """{"id":"msg-1","role":"assistant","providerID":"anthropic","modelID":"claude-sonnet-4-20250514","time":{"created":1767312000000},"tokens":{"input":100,"output":50,"reasoning":5,"cache":{"read":10,"write":20}},"cost":0.02,"agent":"build"}"""),
            ("row-2", "session-a", """{"id":"msg-2","role":"user","time":{"created":1767312000000}}"""),
            ("row-3", "session-a", """{"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":100,"output":10,"cache":0},"cost":0}"""));

        var entries = TestData.ParseAll(new KiloSource(), TestData.ConfigWith(KiloSource.SourceId, directory.Path));

        Assert.Equal(2, entries.Count);
        var entry = entries[0];
        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal("claude-sonnet-4-20250514", entry.Model);
        Assert.Equal(1_767_312_000_000, entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(55, entry.OutputTokens);
        Assert.Equal(5, entry.ReasoningTokens);
        Assert.Equal(20, entry.CacheCreationTokens);
        Assert.Equal(10, entry.CacheReadTokens);
        Assert.Equal(0.02, entry.RecordedCostUsd);
        Assert.Equal("msg-1", entry.DedupeKey);
        Assert.Equal("/home/me/kilo-app", entry.ProjectPath);
        Assert.Equal("kilo-app", entry.Project);

        // Brak id w danych → klucz z bazy i wiersza; cache nie-obiekt = 0; cost 0 = brak kosztu.
        Assert.EndsWith(":row-3", entries[1].DedupeKey);
        Assert.Equal(0, entries[1].CacheReadTokens);
        Assert.Null(entries[1].RecordedCostUsd);
    }

    [Fact]
    public void Messages_without_creation_time_are_skipped_with_diagnostics()
    {
        using var directory = new TempDirectory();
        CreateDatabase(directory.Path,
            ("row-1", "session-a", """{"role":"assistant","providerID":"openai","modelID":"gpt-5","tokens":{"input":1,"output":1,"cache":{"read":0,"write":0}}}"""),
            ("row-2", "session-a", """{"role":,"id":"SECRET"}, "assistant" """),
            ("row-3", "session-a", """{"role":"assistant","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"total":234}}"""));
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new KiloSource(), TestData.ConfigWith(KiloSource.SourceId, directory.Path), diagnostics);

        Assert.Empty(entries);
        Assert.Equal(2, diagnostics.SkippedLines);
    }

    [Fact]
    public void Same_message_in_two_data_directories_counts_once()
    {
        using var directory = new TempDirectory();
        foreach (var (folder, input) in new[] { ("first", 10), ("second", 20) })
        {
            CreateDatabase(Path.Combine(directory.Path, folder),
                ("row-1", "session-a", "{\"id\":\"embedded-msg-1\",\"role\":\"assistant\",\"providerID\":\"openai\",\"modelID\":\"gpt-5\",\"time\":{\"created\":1767312000000},\"tokens\":{\"input\":" + input + ",\"output\":1,\"cache\":{\"read\":0,\"write\":0}}}"));
        }

        var config = TestData.ConfigWith(KiloSource.SourceId, Path.Combine(directory.Path, "first"));
        config.Sources[KiloSource.SourceId].Paths = [Path.Combine(directory.Path, "first"), Path.Combine(directory.Path, "second")];

        var parsed = TestData.ParseAll(new KiloSource(), config);

        Assert.Equal(2, parsed.Count);
        Assert.Single(Deduplicator.Apply(parsed));
    }

    [Fact]
    public void Database_without_message_table_is_reported()
    {
        using var directory = new TempDirectory();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, KiloSource.DatabaseFileName)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other (id TEXT)";
            command.ExecuteNonQuery();
        }

        var diagnostics = new ParseDiagnostics();

        Assert.Empty(TestData.ParseAll(new KiloSource(), TestData.ConfigWith(KiloSource.SourceId, directory.Path), diagnostics));
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Provider_is_recorded_and_priced_as_provider_qualified_model_first()
    {
        // ccusage kilo/src/parser.rs: adapter_pricing_identity (open-ai → open_ai/gpt-5.2, potem gpt-5.2).
        using var directory = new TempDirectory();
        CreateDatabase(directory.Path,
            ("row-1", "session-a", """{"id":"m1","role":"assistant","modelID":"gpt-5.2","providerID":"open-ai","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}"""),
            ("row-2", "session-a", """{"id":"m2","role":"assistant","modelID":"gpt-5.2","providerID":"kilo","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}"""),
            ("row-3", "session-a", """{"id":"m3","role":"assistant","modelID":"gpt-5.2","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}"""));
        var source = new KiloSource();

        var entries = TestData.ParseAll(source, TestData.ConfigWith(KiloSource.SourceId, directory.Path)).ToDictionary(entry => entry.DedupeKey!);

        Assert.Equal("open-ai", entries["m1"].Provider);
        Assert.Equal(["open_ai/gpt-5.2", "gpt-5.2"], source.PricingCandidates(entries["m1"]));
        Assert.Equal(["gpt-5.2"], source.PricingCandidates(entries["m2"]));
        Assert.Null(entries["m3"].Provider);
        Assert.Equal(["gpt-5.2"], source.PricingCandidates(entries["m3"]));
    }
}
