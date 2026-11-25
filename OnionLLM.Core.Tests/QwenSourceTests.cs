using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class QwenSourceTests
{
    private static List<Models.UsageEntry> Parse(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new QwenSource(), TestData.ConfigWith(QwenSource.SourceId, directory.Path), diagnostics);

    private static string Line(string usage, string timestamp = "2026-02-23T14:24:56.857Z", string? session = "s", string extra = "") =>
        "{\"type\":\"assistant\",\"model\":\"qwen3-coder-plus\",\"timestamp\":\"" + timestamp + "\"," +
        (session is null ? "" : "\"sessionId\":\"" + session + "\",") + extra + "\"usageMetadata\":" + usage + "}";

    [Fact]
    public void Cached_prompt_tokens_are_split_out_and_thoughts_outside_candidates_go_to_output()
    {
        using var directory = new TempDirectory();
        // Fixture z testów ccusage (lib.rs).
        directory.Write("projects/myProject/chats/chat-a.jsonl", string.Join('\n',
            "{\"type\":\"user\",\"text\":\"hello\"}",
            Line("{\"promptTokenCount\":100,\"candidatesTokenCount\":50,\"thoughtsTokenCount\":10,\"cachedContentTokenCount\":5,\"totalTokenCount\":160}",
                session: "session-json", extra: "\"cwd\":\"C:\\\\work\\\\myProject\",")));

        var entry = Assert.Single(Parse(directory));

        Assert.Equal("session-json", entry.SessionId);
        Assert.Equal("myProject", entry.Project);
        Assert.Equal(@"C:\work\myProject", entry.ProjectPath);
        Assert.Equal("qwen3-coder-plus", entry.Model);
        Assert.Equal(95, entry.InputTokens);
        Assert.Equal(5, entry.CacheReadTokens);
        Assert.Equal(60, entry.OutputTokens);
        Assert.Equal(10, entry.ReasoningTokens);
        Assert.Equal(160, entry.TotalTokens);
    }

    [Theory]
    [InlineData("{\"promptTokenCount\":1000,\"candidatesTokenCount\":200,\"thoughtsTokenCount\":50,\"cachedContentTokenCount\":400,\"totalTokenCount\":1200}", 600, 400, 200, 50)]
    [InlineData("{\"promptTokenCount\":1000,\"candidatesTokenCount\":200,\"thoughtsTokenCount\":50,\"cachedContentTokenCount\":400,\"totalTokenCount\":1250}", 600, 400, 250, 50)]
    [InlineData("{\"promptTokenCount\":1000,\"totalTokenCount\":1500,\"thoughtsTokenCount\":60}", 1000, 0, 500, 60)]
    [InlineData("{\"promptTokenCount\":100,\"thoughtsTokenCount\":20}", 100, 0, 20, 20)]
    [InlineData("{\"promptTokenCount\":500,\"candidatesTokenCount\":120,\"thoughtsTokenCount\":30,\"cachedContentTokenCount\":80}", 420, 80, 120, 30)]
    [InlineData("{\"promptTokenCount\":1000,\"candidatesTokenCount\":0,\"thoughtsTokenCount\":50,\"totalTokenCount\":1050}", 1000, 0, 50, 50)]
    public void Usage_metadata_variants_map_like_ccusage(string usage, long input, long cacheRead, long output, long reasoning)
    {
        using var directory = new TempDirectory();
        directory.Write("projects/p/chats/c.jsonl", Line(usage));

        var entry = Assert.Single(Parse(directory));

        Assert.Equal(input, entry.InputTokens);
        Assert.Equal(cacheRead, entry.CacheReadTokens);
        Assert.Equal(output, entry.OutputTokens);
        Assert.Equal(reasoning, entry.ReasoningTokens);
    }

    [Fact]
    public void Archived_chats_are_discovered_and_nested_folders_ignored()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/myProject/chats/live.jsonl", Line("{\"promptTokenCount\":100,\"candidatesTokenCount\":50}", "2026-02-24T02:00:00.000Z", "live-0001"));
        directory.Write("projects/myProject/chats/archive/archived.jsonl", Line("{\"promptTokenCount\":10,\"candidatesTokenCount\":5}", "2026-02-24T03:00:00.000Z", "arch-0002"));
        directory.Write("projects/myProject/chats/other/ignored.jsonl", Line("{\"promptTokenCount\":1000,\"candidatesTokenCount\":500}", "2026-02-24T04:00:00.000Z", "other"));
        directory.Write("projects/myProject/chats/archive/nested/deep.jsonl", Line("{\"promptTokenCount\":1000,\"candidatesTokenCount\":500}", "2026-02-24T05:00:00.000Z", "deep"));
        directory.Write("projects/myProject/chats/archive/file-stem.jsonl", Line("{\"promptTokenCount\":1,\"candidatesTokenCount\":1}", "2026-02-24T06:00:00.000Z", null));

        var entries = Parse(directory).OrderBy(entry => entry.TimestampMs).ToList();

        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal("myProject", entry.Project));
        Assert.Equal(165, entries.Take(2).Sum(entry => entry.TotalTokens));
        Assert.Equal("myProject-file-stem", entries[2].SessionId);
    }

    [Fact]
    public void Session_in_live_and_archive_counts_once()
    {
        using var directory = new TempDirectory();
        var transcript = string.Join('\n',
            Line("{\"promptTokenCount\":100,\"candidatesTokenCount\":50}", "2026-02-24T02:00:00.000Z", "session-a"),
            Line("{\"promptTokenCount\":10,\"candidatesTokenCount\":5}", "2026-02-24T02:01:00.000Z", null));
        directory.Write("projects/myProject/chats/session-a.jsonl", transcript);
        directory.Write("projects/myProject/chats/archive/session-a.jsonl", transcript);

        var entries = Deduplicator.Apply(Parse(directory));

        Assert.Equal(["session-a", "myProject-session-a"], entries.Select(entry => entry.SessionId).ToArray());
    }

    [Fact]
    public void Records_without_timestamp_or_with_bad_usage_are_skipped()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/p/chats/c.jsonl", string.Join('\n',
            "{\"type\":\"assistant\",\"usageMetadata\":{\"promptTokenCount\":1}}",
            "{\"type\":\"assistant\",\"timestamp\":\"invalid\",\"usageMetadata\":{\"promptTokenCount\":1}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"usageMetadata\":{\"promptTokenCount\":1}}",
            "{\"type\":\"assistant\",\"usageMetadata\":{}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"usageMetadata\":{\"totalTokenCount\":321}}",
            "{\"type\":\"assistant\",\"usageMetadata\":\"SECRET\"}",
            "{\"type\":\"user\",\"usageMetadata\":\"SECRET\"}"));
        var diagnostics = new ParseDiagnostics();

        var entry = Assert.Single(Parse(directory, diagnostics));

        Assert.Equal(1, entry.InputTokens);
        Assert.True(entry.IsFallbackModel);
        Assert.Equal(3, diagnostics.SkippedLines);
    }

    [Fact]
    public void Null_candidates_count_is_present_and_zero()
    {
        // ccusage qwen/src/parser.rs: present_u64 — pole obecne z null daje Some(0), więc wyjście nie jest liczone z total − prompt.
        using var directory = new TempDirectory();
        directory.Write("projects/myProject/chats/chat-a.jsonl", string.Join('\n',
            Line("{\"promptTokenCount\":100,\"candidatesTokenCount\":null,\"totalTokenCount\":130}", "2026-02-24T06:00:00.000Z", "s1"),
            Line("{\"promptTokenCount\":100,\"totalTokenCount\":130}", "2026-02-24T06:01:00.000Z", "s2")));

        var entries = Parse(directory).ToDictionary(entry => entry.SessionId);

        Assert.Equal(100, entries["s1"].InputTokens);
        Assert.Equal(0, entries["s1"].OutputTokens - entries["s1"].ReasoningTokens);
        Assert.Equal(30, entries["s2"].OutputTokens);
    }
}
