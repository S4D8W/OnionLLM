using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Scenariusze z ccusage <c>codex/src/tests/loader/{records,service_tiers,auto_review,archived}.rs</c>,
/// <c>replay.rs</c> (history_base) i <c>aggregate/dedupe.rs</c>.
/// </summary>
public class CodexParserTests
{
    private static List<UsageEntry> Parse(TempDirectory directory, ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path), diagnostics);

    private static string Join(params string[] lines) => string.Join('\n', lines);

    private static string TokenCount(string timestamp, long input, string model = "gpt-5.6-sol") =>
        """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"@1@","last_token_usage":{"input_tokens":@2@,"cached_input_tokens":0,"output_tokens":1,"total_tokens":@3@}}}}""".Fill(timestamp, model, input, input + 1);

    private static string Settings(string timestamp, string threadSettings) =>
        """{"timestamp":"@0@","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":@1@}}""".Fill(timestamp, threadSettings);

    [Fact]
    public void Loads_saved_codex_exec_json_usage_from_a_directory_without_sessions()
    {
        using var directory = new TempDirectory();
        directory.Write("run.jsonl", Join(
            """{"type":"turn.completed","timestamp":"2026-01-02T03:04:05.000Z","model":"gpt-5.2-codex","usage":{"input_tokens":120,"cached_input_tokens":20,"output_tokens":30,"total_tokens":150}}""",
            """{"type":"result","data":{"timestamp":"2026-01-02T03:05:05.000Z","model_name":"gpt-5.2-codex","usage":{"prompt_tokens":50,"cached_tokens":5,"completion_tokens":12}}}""",
            """{"type":"turn.completed","timestamp":"2026-01-02T03:06:05.000Z","model":"gpt-5.2-codex","usage":{"input_tokens":9,"output_tokens":4,"reasoning_output_tokens":1,"total_tokens":0}}"""));

        var entries = Parse(directory);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal("run", entry.SessionId));
        Assert.All(entries, entry => Assert.Equal("gpt-5.2-codex", entry.Model));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z").ToUnixTimeMilliseconds(), entries[0].TimestampMs);
        Assert.Equal((100, 20, 30), (entries[0].InputTokens, entries[0].CacheReadTokens, entries[0].OutputTokens));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:05:05Z").ToUnixTimeMilliseconds(), entries[1].TimestampMs);
        Assert.Equal((45, 5, 12), (entries[1].InputTokens, entries[1].CacheReadTokens, entries[1].OutputTokens));
        Assert.Equal((9, 4, 1), (entries[2].InputTokens, entries[2].OutputTokens, entries[2].ReasoningTokens));
    }

    [Fact]
    public void Exec_usage_with_unexpected_field_types_uses_file_time_and_fallback_model()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("run.jsonl",
            """{"type":"turn.completed","timestamp":false,"model":{"name":"unexpected"},"usage":{"input_tokens":120,"cached_input_tokens":20,"output_tokens":30,"total_tokens":150}}""");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var entry = Assert.Single(Parse(directory));

        Assert.Equal(CodexSource.FallbackModel, entry.Model);
        Assert.True(entry.IsFallbackModel);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), entry.TimestampMs);
        Assert.Equal(SpeedTier.Unknown, entry.Speed);
        Assert.Equal(100, entry.InputTokens);
    }

    [Fact]
    public void Exec_usage_with_malformed_counts_is_reported_and_identical_lines_are_kept()
    {
        using var directory = new TempDirectory();
        var line = """{"type":"turn.completed","timestamp":"2026-01-02T03:04:05Z","model":"gpt-5","usage":{"input_tokens":"10","output_tokens":5}}""";
        directory.Write("run.jsonl", Join(
            line,
            line,
            """{"type":"turn.completed","timestamp":"2026-01-02T03:04:05Z","usage":{"input_tokens":{"bad":1},"output_tokens":5}}"""));
        var diagnostics = new ParseDiagnostics();

        var entries = Deduplicator.Apply(Parse(directory, diagnostics));

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(10, entry.InputTokens));
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Exec_logs_are_not_scanned_next_to_session_directories()
    {
        using var directory = new TempDirectory();
        directory.Write("history.jsonl", """{"timestamp":"2026-01-02T03:04:05Z","usage":{"input_tokens":10,"output_tokens":5}}""");
        directory.Write("archived_sessions/a.jsonl", TokenCount("2026-01-02T03:04:05Z", 7));

        var entry = Assert.Single(Parse(directory));

        Assert.Equal(7, entry.InputTokens);
        Assert.Equal("a", entry.SessionId);
    }

    [Fact]
    public void Codex_auto_review_is_renamed_at_documented_model_boundaries()
    {
        static string Line(string timestamp) =>
            """{"type":"turn.completed","timestamp":"@0@","model":"codex-auto-review","usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}""".Fill(timestamp);
        using var directory = new TempDirectory();
        directory.Write("run.jsonl", Join(
            Line("2026-02-05T00:00:00.000Z"),
            Line("2026-03-05T00:00:00.000Z"),
            Line("2026-04-23T00:00:00.000Z"),
            Line("2026-07-29T23:59:59.999Z"),
            Line("2026-07-30T00:00:00.000Z"),
            """{"type":"turn.completed","timestamp":"not-a-date","created_at":"2025-12-11T00:00:00.000Z","model":"codex-auto-review","usage":{"input_tokens":10,"output_tokens":5}}"""));

        var entries = Parse(directory);

        Assert.Equal(["gpt-5.3-codex", "gpt-5.4", "gpt-5.4", "gpt-5.4", "gpt-5.6-luna", "gpt-5.2-codex"], entries.Select(entry => entry.Model));
        Assert.All(entries, entry => Assert.True(entry.IsFallbackModel));
    }

    [Fact]
    public void Subtracts_cache_write_usage_from_cumulative_token_totals()
    {
        static string Total(string timestamp, int input, int cached, int write, int output) =>
            """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5.6-terra","total_token_usage":{"input_tokens":@1@,"cached_input_tokens":@2@,"cache_write_input_tokens":@3@,"output_tokens":@4@,"total_tokens":@5@}}}}""".Fill(timestamp, input, cached, write, output, input + output);
        using var directory = new TempDirectory();
        directory.Write("sessions/session.jsonl", Join(
            Total("2026-01-02T00:00:01.000Z", 100, 60, 20, 10),
            Total("2026-01-02T00:00:02.000Z", 200, 100, 50, 25)));

        var entries = Parse(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal((20, 20, 60), (entries[0].InputTokens, entries[0].CacheCreationTokens, entries[0].CacheReadTokens));
        Assert.Equal((30, 30, 40, 15), (entries[1].InputTokens, entries[1].CacheCreationTokens, entries[1].CacheReadTokens, entries[1].OutputTokens));
        Assert.Equal(115, entries[1].TotalTokens);
    }

    [Fact]
    public void Usage_field_aliases_and_cache_creation_are_read()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/s.jsonl",
            """{"timestamp":"2026-01-02T00:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5","last_token_usage":{"input":100,"cache_read_input_tokens":30,"cache_creation_input_tokens":90,"output":8,"reasoning_tokens":3}}}}""");

        var entry = Assert.Single(Parse(directory));

        // Zapis do cache przycięty do wejścia bez odczytu (100 − 30).
        Assert.Equal((0, 30, 70, 8, 3), (entry.InputTokens, entry.CacheReadTokens, entry.CacheCreationTokens, entry.OutputTokens, entry.ReasoningTokens));
    }

    [Fact]
    public void Records_service_tier_transitions_for_following_usage()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/session.jsonl", Join(
            Settings("2026-07-22T00:00:00.000Z", """{"service_tier":"default"}"""),
            TokenCount("2026-07-22T00:00:01.000Z", 10),
            Settings("2026-07-22T00:00:02.000Z", """{"service_tier":"priority"}"""),
            TokenCount("2026-07-22T00:00:03.000Z", 20),
            Settings("2026-07-22T00:00:04.000Z", """{"service_tier":"flex"}"""),
            TokenCount("2026-07-22T00:00:05.000Z", 30),
            Settings("2026-07-22T00:00:06.000Z", """{"service_tier":"ultrafast"}"""),
            TokenCount("2026-07-22T00:00:07.000Z", 40)));

        Assert.Equal([SpeedTier.Standard, SpeedTier.Fast, SpeedTier.Flex, SpeedTier.Ultrafast], Parse(directory).Select(entry => entry.Speed));
    }

    [Fact]
    public void Omitted_tier_keeps_the_recorded_one_and_an_unknown_tier_falls_back_to_config()
    {
        using var directory = new TempDirectory();
        directory.Write("config.toml", "service_tier = \"flex\"");
        directory.Write("sessions/session.jsonl", Join(
            TokenCount("2026-07-22T00:00:00.500Z", 5),
            Settings("2026-07-22T00:00:00.000Z", """{"service_tier":"priority"}"""),
            TokenCount("2026-07-22T00:00:01.000Z", 10),
            Settings("2026-07-22T00:00:02.000Z", """{"model":"codex-auto-review"}"""),
            TokenCount("2026-07-22T00:00:03.000Z", 20),
            Settings("2026-07-22T00:00:04.000Z", """{"service_tier":"standard"}"""),
            TokenCount("2026-07-22T00:00:05.000Z", 30),
            Settings("2026-07-22T00:00:06.000Z", """{"service_tier":"unknown"}"""),
            TokenCount("2026-07-22T00:00:07.000Z", 40),
            """{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}""",
            TokenCount("2026-07-22T00:00:08.000Z", 50),
            """{"timestamp":"2026-07-22T00:00:09.000Z","type":"turn_context","payload":{"model":"gpt-5.6-sol","service_tier":"priority"}}""",
            TokenCount("2026-07-22T00:00:10.000Z", 60)));

        // Nieustalony poziom → config.toml; ustawienia bez timestampu i service_tier w turn_context są ignorowane (jak w ccusage).
        Assert.Equal(
            [SpeedTier.Flex, SpeedTier.Fast, SpeedTier.Fast, SpeedTier.Standard, SpeedTier.Flex, SpeedTier.Flex, SpeedTier.Flex],
            Parse(directory).Select(entry => entry.Speed));
    }

    [Fact]
    public void Duplicates_prefer_the_standard_tier()
    {
        var comparer = new CodexSource().DuplicateComparer;
        UsageEntry Entry(SpeedTier tier) =>
            TestData.Entry("2026-07-22T00:00:01Z", 10, 1, "gpt-5", "codex") with { Speed = tier, DedupeKey = "k" };

        Assert.True(comparer!.Compare(Entry(SpeedTier.Standard), Entry(SpeedTier.Fast)) > 0);
        Assert.True(comparer.Compare(Entry(SpeedTier.Ultrafast), Entry(SpeedTier.Fast)) > 0);
        Assert.True(comparer.Compare(Entry(SpeedTier.Fast), Entry(SpeedTier.Flex)) > 0);
        Assert.True(comparer.Compare(Entry(SpeedTier.Flex), Entry(SpeedTier.Unknown)) > 0);

        var deduped = Deduplicator.Apply([Entry(SpeedTier.Fast), Entry(SpeedTier.Standard)], _ => new CodexSource());
        Assert.Equal(SpeedTier.Standard, Assert.Single(deduped).Speed);
    }

    [Fact]
    public void Session_key_is_the_relative_rollout_path_and_copies_are_read_once()
    {
        using var directory = new TempDirectory();
        var line = TokenCount("2026-07-22T00:00:01.000Z", 10);
        directory.Write("sessions/2026/07/22/rollout-a.jsonl", line);
        directory.Write("sessions/2026/07/22/rollout-a.jsonl.zst", "niepoprawny zstd");
        directory.Write("archived_sessions/2026/07/22/rollout-a.jsonl", line);
        directory.Write("archived_sessions/old.jsonl", TokenCount("2026-07-21T00:00:01.000Z", 3));

        var entries = Parse(directory);

        Assert.Equal(["2026/07/22/rollout-a", "old"], entries.Select(entry => entry.SessionId));
        Assert.EndsWith(Path.Combine("sessions", "2026", "07", "22", "rollout-a.jsonl"),
            new CodexSource().EnumerateFiles(TestData.ConfigWith("codex", directory.Path)).First().Path);
    }

    [Theory]
    [InlineData("child.jsonl", """{"thread_id":"01a0bb8c-c7c0-7630-9d82-860b875930f0","end_ordinal_exclusive":2}""", 300)]
    [InlineData("non-object-history.jsonl", "\"parent\"", 150)]
    [InlineData("rollout-2026-09-19T12-01-00-01a0cc9d-d8d1-8741-ae93-971c986041a1_01a0ddae-e9e2-7852-bfa4-a82da97152b2.jsonl", "null", 300)]
    [InlineData("backup-01a0cc9d-d8d1-8741-ae93-971c986041a1_01a0ddae-e9e2-7852-bfa4-a82da97152b2.jsonl", "null", 150)]
    [InlineData("rollout-backup-01a0cc9d-d8d1-8741-ae93-971c986041a1_01a0ddae-e9e2-7852-bfa4-a82da97152b2.jsonl", "null", 150)]
    public void Reference_backed_history_keeps_usage_while_physical_forks_dedupe(string childFile, string historyBase, long expected)
    {
        const string parentId = "01a0bb8c-c7c0-7630-9d82-860b875930f0";
        const string childId = "01a0cc9d-d8d1-8741-ae93-971c986041a1";
        static string Usage(string timestamp) =>
            """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5","last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}""".Fill(timestamp);
        using var directory = new TempDirectory();
        directory.Write("parent.jsonl", Join(
            """{"timestamp":"2026-09-19T12:00:00Z","type":"session_meta","payload":{"id":"@0@"}}""".Fill(parentId),
            Usage("2026-09-19T12:00:01Z")));
        directory.Write(childFile, Join(
            """{"timestamp":"2026-09-19T12:01:00Z","type":"session_meta","payload":{"id":"@0@","forked_from_id":"@1@","history_base":@2@}}""".Fill(childId, parentId, historyBase),
            Usage("2026-09-19T12:02:00Z")));

        Assert.Equal(expected, Deduplicator.Apply(Parse(directory)).Sum(entry => entry.TotalTokens));
    }

    [Theory]
    [InlineData("rollout-2026-09-19T12-01-00-01a0cc9d-d8d1-8741-ae93-971c986041a1_01a0ddae-e9e2-7852-bfa4-a82da97152b2.jsonl.zst", true)]
    [InlineData("rollout-2026-09-19T12-01-00-01a0cc9d-d8d1-8741-ae93-971c986041a1.jsonl", true)]
    [InlineData("rollout-2026-09-19-12-01-00-01a0cc9d-d8d1-8741-ae93-971c986041a1.jsonl", false)]
    [InlineData("rollout-2026-09-19T12-01-00-not-a-uuid.jsonl", false)]
    public void Rollout_identity_requires_a_timestamp_and_uuids(string name, bool valid) =>
        Assert.Equal(valid, CodexSource.RolloutIdentity(name) is not null);
}
