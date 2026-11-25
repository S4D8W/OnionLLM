using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class CodexSourceTests
{
    private static string TokenCount(string timestamp, string? last, string? total) =>
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{" +
        string.Join(',', new[] { last is null ? null : $"\"last_token_usage\":{last}", total is null ? null : $"\"total_token_usage\":{total}" }.Where(part => part is not null)) +
        "}}}";

    private static string Usage(long input, long cached, long output, long reasoning = 0) =>
        $"{{\"input_tokens\":{input},\"cached_input_tokens\":{cached},\"output_tokens\":{output},\"reasoning_output_tokens\":{reasoning},\"total_tokens\":{input + output}}}";

    [Fact]
    public void Uses_last_token_usage_and_skips_repeated_totals()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/2026/11/24/rollout-2026-11-24T10-00-00-0199a213-81c0-7800-8aa1-bbab2a035a53.jsonl", string.Join('\n',
            "{\"timestamp\":\"2026-11-24T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"0199a213-81c0-7800-8aa1-bbab2a035a53\",\"cwd\":\"C:\\\\code\\\\app\"}}",
            "{\"timestamp\":\"2026-11-24T10:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5-codex\"}}",
            TokenCount("2026-11-24T10:00:02Z", Usage(1000, 800, 50, 10), Usage(1000, 800, 50, 10)),
            TokenCount("2026-11-24T10:00:03Z", Usage(1000, 800, 50, 10), Usage(1000, 800, 50, 10)), // powtórzenie
            TokenCount("2026-11-24T10:00:04Z", Usage(500, 400, 20), Usage(1500, 1200, 70, 10))));

        var entries = TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path));

        Assert.Equal(2, entries.Count);
        Assert.Equal(200, entries[0].InputTokens);   // 1000 - 800 cached
        Assert.Equal(800, entries[0].CacheReadTokens);
        Assert.Equal(50, entries[0].OutputTokens);
        Assert.Equal(10, entries[0].ReasoningTokens);
        Assert.Equal("gpt-5-codex", entries[0].Model);
        Assert.False(entries[0].IsFallbackModel);
        Assert.Equal("2026/11/24/rollout-2026-11-24T10-00-00-0199a213-81c0-7800-8aa1-bbab2a035a53", entries[0].SessionId);
        Assert.Equal("app", entries[0].Project);
        Assert.Equal(100, entries[1].InputTokens);
    }

    [Fact]
    public void Falls_back_to_total_delta_and_default_model()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/a.jsonl", string.Join('\n',
            TokenCount("2026-11-23T23:59:00Z", null, Usage(100, 0, 10)),
            TokenCount("2026-11-24T00:01:00Z", null, Usage(250, 50, 30))));

        var entries = TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path));

        Assert.Equal(2, entries.Count);
        Assert.Equal(100, entries[0].InputTokens);
        Assert.Equal(100, entries[1].InputTokens); // (250-100) - 50 cached
        Assert.Equal(50, entries[1].CacheReadTokens);
        Assert.Equal(20, entries[1].OutputTokens);
        Assert.True(entries[1].IsFallbackModel);
        Assert.Equal(CodexSource.FallbackModel, entries[1].Model);
    }

    [Fact]
    public void Fork_copies_of_parent_events_are_deduplicated()
    {
        using var directory = new TempDirectory();
        var shared = TokenCount("2026-11-24T10:00:02Z", Usage(100, 0, 10), Usage(100, 0, 10));
        directory.Write("sessions/parent.jsonl", shared);
        directory.Write("sessions/fork.jsonl", string.Join('\n', shared, TokenCount("2026-11-24T11:00:00Z", Usage(5, 0, 1), Usage(105, 0, 11))));

        var entries = Deduplicator.Apply(TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path)));

        Assert.Equal(2, entries.Count);
    }
}
