using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class CodebuffSourceTests
{
    private static long Ms(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void Assistant_usage_and_credits_are_read_from_chat_messages()
    {
        using var directory = new TempDirectory();
        var root = Path.Combine(directory.Path, "manicode");
        directory.Write("manicode/projects/project-a/chats/2026-01-02T03-04-05.000Z/chat-messages.json", """
            [
              {"role":"user","text":"hello"},
              {"id":"assistant-message","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z",
               "metadata":{"model":"claude-sonnet-4-20250514","usage":{"inputTokens":100,"outputTokens":50,"cacheCreationInputTokens":20,"cacheReadInputTokens":10}},
               "credits":1.25}
            ]
            """);

        var entry = Assert.Single(TestData.ParseAll(new CodebuffSource(), TestData.ConfigWith("codebuff", root)));

        Assert.Equal("manicode/project-a/2026-01-02T03-04-05.000Z", entry.SessionId);
        Assert.Equal("project-a", entry.Project);
        Assert.Equal("claude-sonnet-4-20250514", entry.Model);
        Assert.Equal(Ms("2026-01-02T03:04:06Z"), entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(50, entry.OutputTokens);
        Assert.Equal(20, entry.CacheCreationTokens);
        Assert.Equal(10, entry.CacheReadTokens);
        Assert.Equal(1.25, entry.Credits);
        Assert.Equal("codebuff:manicode/project-a/2026-01-02T03-04-05.000Z:assistant-message", entry.DedupeKey);
    }

    [Fact]
    public void Run_state_provider_usage_is_a_fallback_and_cached_prompt_tokens_are_split_out()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/project-a/chats/2026-01-02T03-04-05.000Z/chat-messages.json", """
            [
              {"variant":"agent","metadata":{"runState":{"sessionState":{"mainAgentState":{"messageHistory":[
                {"role":"user","providerOptions":{}},
                {"role":"assistant","providerOptions":{"codebuff":{"model":"openai/gpt-5","usage":{"prompt_tokens":100,"completion_tokens":50,"prompt_tokens_details":{"cached_tokens":10}}}}}
              ]}}}}}
            ]
            """);

        var entry = Assert.Single(TestData.ParseAll(new CodebuffSource(), TestData.ConfigWith("codebuff", directory.Path)));

        Assert.Equal("openai/gpt-5", entry.Model);
        Assert.Equal(90, entry.InputTokens); // prompt_tokens zawiera 10 tokenów z cache
        Assert.Equal(10, entry.CacheReadTokens);
        Assert.Equal(50, entry.OutputTokens);
        // Brak znacznika w wiadomości → czas z nazwy katalogu czatu.
        Assert.Equal(Ms("2026-01-02T03:04:05Z"), entry.TimestampMs);
    }

    [Fact]
    public void First_positive_output_wins_and_untimed_records_are_skipped()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json", """
            [
              {"role":"assistant","timestamp":"2026-01-02T03:04:01Z","metadata":{"model":"sample","usage":{"inputTokens":100,"outputTokens":0},"codebuff":{"usage":{"outputTokens":50}}}},
              {"role":"assistant","timestamp":"2026-01-02T03:04:02Z","metadata":{"model":"sample","usage":{"inputTokens":100,"outputTokens":0,"completionTokens":50}}},
              {"role":"assistant","timestamp":"2026-01-02T03:04:03Z","metadata":{"model":"sample","usage":{"inputTokens":100,"outputTokens":25,"completionTokens":50},"codebuff":{"usage":{"outputTokens":75}}}},
              {"role":"assistant","timestamp":1767323044,"metadata":{"usage":{"inputTokens":7}}}
            ]
            """);
        directory.Write("projects/p/chats/chat-without-time/chat-messages.json", """
            [{"role":"assistant","metadata":{"usage":{"inputTokens":1}}},{"role":"assistant","metadata":{"usage":{}}}]
            """);
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new CodebuffSource(), TestData.ConfigWith("codebuff", directory.Path), diagnostics);

        Assert.Equal([50L, 50L, 25L, 0L], entries.Select(entry => entry.OutputTokens).ToArray());
        Assert.Equal(CodebuffSource.FallbackModel, entries[3].Model);
        Assert.True(entries[3].IsFallbackModel);
        Assert.Equal(1767323044000, entries[3].TimestampMs);
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Repeated_message_id_within_a_chat_is_deduplicated()
    {
        using var directory = new TempDirectory();
        const string message = """{"id":"m1","role":"assistant","timestamp":"2026-01-02T03:04:06Z","metadata":{"model":"x","usage":{"inputTokens":5}}}""";
        directory.Write("projects/p/chats/c/chat-messages.json", $"[{message},{message}]");

        var entries = Deduplicator.Apply(TestData.ParseAll(new CodebuffSource(), TestData.ConfigWith("codebuff", directory.Path)));

        Assert.Equal(5, Assert.Single(entries).InputTokens);
    }
}
