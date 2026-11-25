using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class DroidSourceTests
{
    [Fact]
    public void Settings_file_yields_one_session_summary_with_thinking_in_output()
    {
        using var directory = new TempDirectory();
        // Fikstura z testu loads_usage_from_droid_settings_files w ccusage.
        directory.Write("-work-demo/session-a.settings.json", """
            {
              "model": "Claude-Sonnet-4-[Anthropic]",
              "providerLock": "anthropic",
              "providerLockTimestamp": "2026-05-01T01:02:03.000Z",
              "tokenUsage": {"inputTokens": 100, "outputTokens": 50, "cacheCreationTokens": 20, "cacheReadTokens": 10, "thinkingTokens": 5}
            }
            """);
        directory.Write("zero.settings.json", """{"model":"gpt-5","tokenUsage":{"inputTokens":0}}""");
        directory.Write("session-a.jsonl", "{}");

        var entry = Assert.Single(TestData.ParseAll(new DroidSource(), TestData.ConfigWith("droid", directory.Path)));

        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal("claude-sonnet-4", entry.Model);
        Assert.Equal("-work-demo", entry.Project);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(55, entry.OutputTokens);
        Assert.Equal(5, entry.ReasoningTokens);
        Assert.Equal(20, entry.CacheCreationTokens);
        Assert.Equal(10, entry.CacheReadTokens);
        Assert.True(entry.IsSessionSummary);
        Assert.Equal(DateTimeOffset.Parse("2026-05-01T01:02:03Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            entry.TimestampMs);
    }

    [Fact]
    public void Model_falls_back_to_sidecar_transcript_and_mtime()
    {
        using var directory = new TempDirectory();
        var settings = directory.Write("session-b.settings.json", """{"providerLock":"anthropic","tokenUsage":{"inputTokens":10,"outputTokens":20}}""");
        directory.Write("session-b.jsonl", """{"content":"Model: Claude Opus 4.5 Thinking [Anthropic]"}""");
        var modified = new DateTime(2026, 5, 2, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settings, modified);

        var entry = Assert.Single(TestData.ParseAll(new DroidSource(), TestData.ConfigWith("droid", directory.Path)));

        Assert.Equal("claude-opus-4.5-thinking", entry.Model); // kropki wersji zostają (cennik), zob. DroidSource.CleanModelName
        Assert.Null(entry.Project);
        Assert.Equal(new DateTimeOffset(modified).ToUnixTimeMilliseconds(), entry.TimestampMs);
    }

    [Fact]
    public void Archived_copy_of_a_session_is_deduplicated_and_latest_snapshot_wins()
    {
        using var directory = new TempDirectory();
        directory.Write("archive/session-c.settings.json",
            """{"model":"gpt-5","providerLockTimestamp":"2026-05-01T01:02:03.000Z","tokenUsage":{"inputTokens":10,"outputTokens":20}}""");
        directory.Write("session-c.settings.json",
            """{"model":"gpt-5","providerLockTimestamp":"2026-05-02T01:02:03.000Z","tokenUsage":{"inputTokens":100,"outputTokens":200}}""");
        directory.Write("broken.settings.json", "{broken");

        var diagnostics = new ParseDiagnostics();
        var entries = Deduplicator.Apply(TestData.ParseAll(new DroidSource(), TestData.ConfigWith("droid", directory.Path), diagnostics));

        var entry = Assert.Single(entries);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(200, entry.OutputTokens);
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Theory]
    [InlineData("custom:Claude-Opus-4.5-Thinking-[Anthropic]-0", "claude-opus-4-5-thinking-0")]
    [InlineData("Claude-Sonnet-4-[Anthropic]", "claude-sonnet-4")]
    [InlineData("gemini-2.5-pro", "gemini-2-5-pro")]
    public void Model_names_are_normalized_like_ccusage(string raw, string expected) =>
        Assert.Equal(expected, DroidSource.NormalizeModelName(raw));
}
