using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class GeminiSourceTests
{
    private static List<Models.UsageEntry> Parse(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new GeminiSource(), TestData.ConfigWith(GeminiSource.SourceId, directory.Path), diagnostics);

    [Fact]
    public void Jsonl_separates_cached_input_and_adds_thoughts_to_output()
    {
        using var directory = new TempDirectory();
        // Fixture z testów ccusage (loader.rs).
        directory.Write("project/chats/session-a.jsonl", string.Join('\n',
            "{\"sessionId\":\"session-a\",\"projectHash\":\"project-a\",\"startTime\":\"2026-05-17T11:07:00.000Z\"}",
            "{\"id\":\"msg-a\",\"timestamp\":\"2026-05-17T11:07:32.000Z\",\"type\":\"gemini\",\"model\":\"gemini-3-flash-preview\",\"tokens\":{\"input\":15327,\"output\":23,\"cached\":11526,\"thoughts\":919,\"tool\":7,\"total\":16276}}"));

        var entry = Assert.Single(Parse(directory));

        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal("gemini-3-flash-preview", entry.Model);
        Assert.Equal(3808, entry.InputTokens);
        Assert.Equal(11526, entry.CacheReadTokens);
        Assert.Equal(23 + 919, entry.OutputTokens);
        Assert.Equal(919, entry.ReasoningTokens);
        Assert.Equal("project", entry.Project);
        Assert.Equal(DateTimeOffset.Parse("2026-05-17T11:07:32Z").ToUnixTimeMilliseconds(), entry.TimestampMs);
        Assert.Equal("session-a:msg-a", entry.DedupeKey);
    }

    [Fact]
    public void Json_session_document_reads_gemini_messages_with_session_time_fallback()
    {
        using var directory = new TempDirectory();
        directory.Write("abc123/chats/session-2026.json", """
            {"sessionId":"s-json","projectHash":"abc123","startTime":"2026-05-17T10:00:00.000Z","lastUpdated":"2026-05-17T10:30:00.000Z",
             "messages":[
               {"id":"u1","type":"user","content":"hi","timestamp":"2026-05-17T10:00:01.000Z"},
               {"id":"g1","type":"gemini","model":"gemini-2.5-pro","timestamp":"2026-05-17T10:00:05.000Z","tokens":{"input":100,"output":10,"cached":0,"thoughts":0,"tool":0,"total":110}},
               {"id":"g2","type":"gemini","model":"gemini-2.5-pro","tokens":{"input":50,"output":5}},
               {"id":"g3","type":" gemini ","model":"gemini-2.5-pro","timestamp":"2026-05-17T10:00:09.000Z","tokens":{"input":1,"output":1}},
               {"id":"g4","type":"gemini","timestamp":"2026-05-17T10:00:09.000Z","tokens":{"input":1,"output":1}}
             ]}
            """);

        var entries = Parse(directory).OrderBy(entry => entry.TimestampMs).ToList();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("s-json", entry.SessionId));
        Assert.Equal(DateTimeOffset.Parse("2026-05-17T10:00:00Z").ToUnixTimeMilliseconds(), entries[0].TimestampMs);
        Assert.Equal(50, entries[0].InputTokens);
        Assert.Equal(100, entries[1].InputTokens);
        Assert.Equal("abc123", entries[1].Project);
    }

    [Fact]
    public void Jsonl_updates_of_the_same_message_keep_the_last_version()
    {
        using var directory = new TempDirectory();
        directory.Write("p/chats/s.jsonl", string.Join('\n',
            "{\"sessionId\":\"s\"}",
            "{\"id\":\"m1\",\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:00:00Z\",\"model\":\"gemini-2.5-flash\",\"tokens\":{\"input\":10,\"output\":1}}",
            "{\"id\":\"m1\",\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:00:01Z\",\"tokens\":{\"input\":10,\"output\":7}}",
            "{\"id\":\"m2\",\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:00:02Z\",\"tokens\":{\"input\":3,\"output\":2}}"));

        var entries = Parse(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal(7, entries[0].OutputTokens);
        Assert.Equal("gemini-2.5-flash", entries[1].Model); // model z poprzedniego rekordu
    }

    [Fact]
    public void Stats_records_produce_one_entry_per_model_and_subtract_cache()
    {
        using var directory = new TempDirectory();
        directory.Write("p/chats/run.jsonl", string.Join('\n',
            "{\"timestamp\":\"2026-05-17T12:00:00Z\",\"result\":{\"stats\":{\"models\":{\"gemini-2.5-pro\":{\"tokens\":{\"prompt\":100,\"candidates\":20,\"cached\":40,\"thoughts\":5}},\"gemini-2.5-flash\":{\"tokens\":{\"prompt\":0}}}}}}",
            "{\"timestamp\":\"2026-05-17T12:05:00Z\",\"model\":\"gemini-2.5-flash\",\"stats\":{\"input_tokens\":30,\"output_tokens\":3,\"cached_tokens\":10}}"));

        var entries = Parse(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal("gemini-2.5-pro", entries[0].Model);
        Assert.Equal(60, entries[0].InputTokens);
        Assert.Equal(40, entries[0].CacheReadTokens);
        Assert.Equal(25, entries[0].OutputTokens);
        Assert.Equal("gemini-2.5-flash", entries[1].Model);
        Assert.Equal(20, entries[1].InputTokens);
        Assert.Null(entries[0].DedupeKey);
    }

    [Fact]
    public void Non_object_json_and_broken_lines_are_handled()
    {
        using var directory = new TempDirectory();
        directory.Write("p/logs.json", "[{\"sessionId\":\"s1\",\"messageId\":0,\"type\":\"user\",\"message\":\"hi\"}]");
        directory.Write("p/scalar.json", "7");
        directory.Write("p/notes.txt", "{\"type\":\"gemini\"}");
        directory.Write("p/chats/s.jsonl", string.Join('\n',
            "{\"type\":\"gemini\",\"tokens\":}",
            "{\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:07:32.000Z\",\"model\":\"gemini-3-flash-preview\",\"tokens\":{\"input\":18446744073709551615,\"output\":1}}",
            "{\"type\":\"gemini\",\"model\":\"gemini-3-flash-preview\",\"tokens\":{\"input\":10,\"output\":2}}",
            "{\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:07:33.000Z\",\"model\":\"gemini-3-flash-preview\",\"tokens\":{\"input\":100,\"output\":20}}"));
        var diagnostics = new ParseDiagnostics();

        var entry = Assert.Single(Parse(directory, diagnostics));

        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(3, diagnostics.SkippedLines); // zły JSON, przepełnienie, brak timestampu
    }

    [Fact]
    public void Message_ids_deduplicate_copies_across_files()
    {
        using var directory = new TempDirectory();
        var line = "{\"sessionId\":\"s\",\"id\":\"m1\",\"type\":\"gemini\",\"timestamp\":\"2026-05-17T11:00:00Z\",\"model\":\"gemini-2.5-pro\",\"tokens\":{\"input\":10,\"output\":1}}";
        directory.Write("p/chats/a.jsonl", line);
        directory.Write("p/chats/backup/a.jsonl", line);

        Assert.Single(Deduplicator.Apply(Parse(directory)));
    }
}
