using System.IO.Compression;
using System.Text;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Scenariusze przeniesione z ccusage <c>codex/src/tests/compaction.rs</c> i filtra replay forków.</summary>
public class CodexCompactionTests
{
    private const string ParentId = "11111111-1111-1111-1111-111111111111";
    private const string ChildId = "22222222-2222-2222-2222-222222222222";

    private static string CompactionLog(string timestamp, string model, long input, string responseId = "response-1") => string.Join('\n',
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"turn_context\",\"payload\":{{\"model\":\"{model}\"}}}}",
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"response_id\":\"{responseId}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":20}}}}}}",
        $"{{\"type\":\"compacted\",\"payload\":{{\"compaction_response_id\":\"{responseId}\"}}}}");

    private static string TokenCount(string timestamp, long input) =>
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"model\":\"gpt-5\",\"last_token_usage\":{{\"input_tokens\":{input},\"output_tokens\":20}}}}}}}}";

    private static List<Models.UsageEntry> Load(TempDirectory directory) =>
        Deduplicator.Apply(TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path)));

    [Fact]
    public void Compaction_record_without_token_count_is_emitted_when_compacted()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/a.jsonl", CompactionLog("2026-09-01T00:00:01Z", "gpt-5", 300));

        var entry = Assert.Single(Load(directory));

        Assert.Equal(300, entry.InputTokens);
        Assert.Equal(20, entry.OutputTokens);
        Assert.Equal("resp:response-1", entry.DedupeKey);
    }

    [Fact]
    public void Record_confirmed_by_token_count_is_not_counted_twice()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/parent.jsonl", string.Join('\n',
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5\"}}",
            "{\"timestamp\":\"2026-09-01T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":20}}}}",
            "{\"timestamp\":\"2026-09-01T00:00:02Z\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"response-1\",\"usage\":{\"input_tokens\":300,\"output_tokens\":20}}}",
            "{\"timestamp\":\"2026-09-01T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":400,\"output_tokens\":40},\"last_token_usage\":{\"input_tokens\":300,\"output_tokens\":20}}}}",
            "{\"type\":\"compacted\",\"payload\":{\"compaction_response_id\":\"response-1\"}}"));

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal(440, entries.Sum(entry => entry.TotalTokens));
    }

    [Fact]
    public void Pending_record_without_confirmation_or_compaction_is_dropped()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/a.jsonl",
            "{\"timestamp\":\"2026-09-01T00:00:02Z\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"r\",\"usage\":{\"input_tokens\":300,\"output_tokens\":20}}}");

        Assert.Empty(Load(directory));
    }

    [Fact]
    public void Fork_skips_history_replayed_from_parent_even_with_new_timestamps()
    {
        using var directory = new TempDirectory();
        directory.Write($"sessions/rollout-2026-09-01T00-00-00-{ParentId}.jsonl", string.Join('\n',
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{ParentId}\"}}}}",
            CompactionLog("2026-09-01T00:00:01Z", "gpt-5", 300),
            TokenCount("2026-09-01T00:00:02Z", 100)));
        directory.Write($"sessions/rollout-2026-09-01T00-01-00-{ChildId}.jsonl", string.Join('\n',
            $"{{\"timestamp\":\"2026-09-01T00:01:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{ChildId}\",\"forked_from_id\":\"{ParentId}\"}}}}",
            TokenCount("2026-09-01T00:01:00Z", 100), // kopia historii rodzica (inny timestamp)
            TokenCount("2026-09-01T00:02:00Z", 50)));

        var entries = Load(directory);

        Assert.Equal(3, entries.Count);
        Assert.Equal(510, entries.Sum(entry => entry.TotalTokens));
        Assert.Single(entries, entry => entry.SessionId == $"rollout-2026-09-01T00-01-00-{ChildId}");
    }

    [Fact]
    public void Fork_with_missing_parent_is_counted_fully_with_diagnostic()
    {
        using var directory = new TempDirectory();
        directory.Write($"sessions/rollout-x-{ChildId}.jsonl", string.Join('\n',
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{ChildId}\",\"forked_from_id\":\"{ParentId}\"}}}}",
            TokenCount("2026-09-01T00:01:00Z", 100)));
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path), diagnostics);

        Assert.Single(entries);
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Compressed_rollouts_are_read()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "archived_sessions", "rollout-z.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var zstd = new ZstandardStream(file, CompressionMode.Compress))
        {
            zstd.Write(Encoding.UTF8.GetBytes(TokenCount("2026-09-01T00:00:02Z", 100) + "\n" + TokenCount("2026-09-01T00:00:03Z", 7)));
        }

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal(107, entries.Sum(entry => entry.InputTokens));
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 3, 4 }, 3)]
    [InlineData(new long[] { 9, 1, 2 }, new long[] { 1, 2, 5 }, 2)]
    [InlineData(new long[] { 1, 2 }, new long[] { 3, 4 }, 0)]
    public void Replayed_parent_length_uses_prefix_or_suffix_overlap(long[] parent, long[] child, int expected)
    {
        static CodexSource.RawUsage U(long input) => new(input, 0, 1, 0, input + 1);

        Assert.Equal(expected, CodexSource.ReplayedParentLength(parent.Select(U).ToList(), child.Select(U).ToList()));
    }
}
