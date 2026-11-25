using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Okno replay forków — przeniesione z ccusage <c>codex/src/tests/loader/fork_boundary.rs</c>
/// i <c>parser/replay_filter/tests.rs</c> (test wyczerpujący krótkich rolloutów).
/// </summary>
public class CodexForkReplayTests
{
    private const string Parent = "019a0000-0000-7000-8000-000000000001";
    private const string Child = "019a0000-0000-7000-8000-000000000002";

    private static string UserForkMeta(string timestamp) =>
        """{"timestamp":"@0@","type":"session_meta","payload":{"id":"@1@","forked_from_id":"@2@","timestamp":"@3@"}}""".Fill(timestamp, Child, Parent, timestamp);

    private static string SpawnedMeta(string timestamp) =>
        """{"timestamp":"@0@","type":"session_meta","payload":{"id":"@1@","timestamp":"@2@","source":{"subagent":{"thread_spawn":{"parent_thread_id":"@3@","depth":1}}}}}""".Fill(timestamp, Child, timestamp, Parent);

    private static string Metadata(string timestamp, string id, string? parent) =>
        """{"timestamp":"@0@","type":"session_meta","payload":{"id":"@1@"@2@}}""".Fill(timestamp, id, (parent is null ? "" : $",\"forked_from_id\":\"{parent}\""));

    private static string TokenCount(string timestamp, long input) =>
        """{"timestamp":"@0@","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5.2","last_token_usage":{"input_tokens":@1@,"output_tokens":1,"total_tokens":@2@}}}}""".Fill(timestamp, input, input + 1);

    private static string UsageRecord(string timestamp, string? threadId, string responseId, long input) =>
        """{"timestamp":"@0@","type":"token_usage_record","payload":{@1@"response_id":"@2@","usage":{"input_tokens":@3@,"output_tokens":1,"total_tokens":@4@}}}""".Fill(timestamp, (threadId is null ? "" : $"\"thread_id\":\"{threadId}\","), responseId, input, input + 1);

    private static string SettingsApplied(string timestamp, string? threadId) =>
        """{"timestamp":"@0@","type":"event_msg","payload":{"type":"thread_settings_applied","thread_id":@1@,"thread_settings":{"service_tier":"default"}}}""".Fill(timestamp, (threadId is null ? "null" : $"\"{threadId}\""));

    private static string[] ParentRollout() =>
    [
        Metadata("2026-11-01T08:00:00.000Z", Parent, null),
        TokenCount("2026-11-01T08:01:00.000Z", 100),
        TokenCount("2026-11-01T08:02:00.000Z", 200),
        TokenCount("2026-11-01T08:03:00.000Z", 300)
    ];

    /// <summary>Wejście wpisów dziecka i czy zgłoszono brak rodzica — dla obu kolejności odkrycia plików.</summary>
    private static void AssertChildUsage(string[]? parent, string[] child, long[] expected, bool warned)
    {
        foreach (var (parentName, childName) in new[] { ("01-parent.jsonl", "02-child.jsonl"), ("02-parent.jsonl", "01-child.jsonl") })
        {
            using var directory = new TempDirectory();
            if (parent is not null)
            {
                directory.Write(parentName, string.Join('\n', parent));
            }

            directory.Write(childName, string.Join('\n', child));
            var diagnostics = new ParseDiagnostics();
            var entries = TestData.ParseAll(new CodexSource(), TestData.ConfigWith("codex", directory.Path), diagnostics);

            var session = childName[..^".jsonl".Length];
            Assert.Equal(expected, entries.Where(entry => entry.SessionId == session).Select(entry => entry.InputTokens));
            Assert.Equal(warned, diagnostics.Reasons.Keys.Any(reason => reason.Contains("rodzica", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Counts_only_own_usage_after_the_copied_history_of_a_missing_parent() =>
        AssertChildUsage(null,
        [
            UserForkMeta("2026-11-01T09:00:00.000Z"),
            UsageRecord("2026-11-01T09:00:00.010Z", Parent, "resp_parent_1", 100),
            TokenCount("2026-11-01T09:00:00.010Z", 100),
            UsageRecord("2026-11-01T09:00:00.020Z", Parent, "resp_parent_2", 200),
            TokenCount("2026-11-01T09:00:00.020Z", 200),
            SettingsApplied("2026-11-01T09:00:00.040Z", Child),
            UsageRecord("2026-11-01T09:00:00.500Z", Child, "resp_child_1", 400),
            TokenCount("2026-11-01T09:00:00.500Z", 400),
            UsageRecord("2026-11-01T09:05:00.000Z", Child, "resp_child_2", 500),
            TokenCount("2026-11-01T09:05:00.000Z", 500)
        ], [400, 500], false);

    [Fact]
    public void Counts_a_dense_first_request_pair_of_a_spawned_child_with_own_records()
    {
        string[] child =
        [
            SpawnedMeta("2026-11-01T09:00:00.000Z"),
            TokenCount("2026-11-01T09:00:00.200Z", 400),
            TokenCount("2026-11-01T09:00:00.400Z", 500),
            UsageRecord("2026-11-01T09:05:00.000Z", Child, "resp_child_3", 600),
            TokenCount("2026-11-01T09:05:00.000Z", 600)
        ];

        AssertChildUsage(null, child, [400, 500, 600], true);
        AssertChildUsage(ParentRollout(), child, [400, 500, 600], false);
    }

    [Fact]
    public void Counts_a_spawned_child_usage_that_precedes_its_first_record() =>
        AssertChildUsage(null,
        [
            SpawnedMeta("2026-11-01T09:00:00.000Z"),
            SettingsApplied("2026-11-01T09:00:00.000Z", Child),
            TokenCount("2026-11-01T09:00:05.000Z", 400),
            UsageRecord("2026-11-01T09:05:00.000Z", Child, "resp_child_2", 500),
            TokenCount("2026-11-01T09:05:00.000Z", 500)
        ], [400, 500], false);

    [Fact]
    public void Skips_a_leading_run_that_replays_a_suffix_of_the_parent() =>
        AssertChildUsage(ParentRollout(),
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            TokenCount("2026-11-01T09:00:00.000Z", 200),
            TokenCount("2026-11-01T09:00:03.000Z", 300),
            TokenCount("2026-11-01T09:05:00.000Z", 400)
        ], [400], false);

    [Fact]
    public void Counts_dense_usage_at_the_fork_instant_that_the_parent_never_had() =>
        AssertChildUsage(ParentRollout(),
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            TokenCount("2026-11-01T09:00:00.100Z", 400),
            TokenCount("2026-11-01T09:00:00.300Z", 500),
            TokenCount("2026-11-01T09:05:00.000Z", 600)
        ], [400, 500, 600], false);

    [Fact]
    public void Keeps_the_settings_boundary_when_a_spawned_child_logs_an_untagged_record() =>
        AssertChildUsage(null,
        [
            SpawnedMeta("2026-11-01T09:00:00.000Z"),
            SettingsApplied("2026-11-01T09:00:00.000Z", Parent),
            TokenCount("2026-11-01T09:00:00.010Z", 100),
            TokenCount("2026-11-01T09:00:00.020Z", 200),
            SettingsApplied("2026-11-01T09:00:00.040Z", Child),
            UsageRecord("2026-11-01T09:00:05.000Z", null, "resp_child_1", 400),
            TokenCount("2026-11-01T09:00:05.000Z", 400)
        ], [400], false);

    [Fact]
    public void Keeps_own_turns_a_legacy_fork_wrote_before_an_upgraded_codex_resumed_it()
    {
        string[] child =
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            SettingsApplied("2026-11-01T09:00:00.000Z", null),
            TokenCount("2026-11-01T09:01:00.000Z", 700),
            TokenCount("2026-11-01T09:02:00.000Z", 800),
            UsageRecord("2026-11-03T09:00:00.000Z", Child, "resp_child_9", 900),
            TokenCount("2026-11-03T09:00:00.000Z", 900)
        ];

        AssertChildUsage(ParentRollout(), child, [700, 800, 900], false);
        AssertChildUsage(null, child, [700, 800, 900], true);
    }

    [Fact]
    public void Matches_the_parent_before_an_own_marker_that_follows_threadless_copies() =>
        AssertChildUsage(ParentRollout(),
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            SettingsApplied("2026-11-01T09:00:00.000Z", null),
            TokenCount("2026-11-01T09:00:00.000Z", 100),
            TokenCount("2026-11-01T09:00:00.000Z", 200),
            SettingsApplied("2026-11-01T09:00:00.010Z", Child),
            TokenCount("2026-11-01T09:05:00.000Z", 400)
        ], [400], false);

    [Fact]
    public void Counts_and_warns_when_a_fork_names_no_child_id() =>
        AssertChildUsage(null,
        [
            """{"timestamp":"2026-11-01T09:00:00.000Z","type":"session_meta","payload":{"forked_from_id":"@0@"}}""".Fill(Parent),
            TokenCount("2026-11-01T09:00:01.000Z", 100),
            SettingsApplied("2026-11-01T09:00:02.000Z", Child),
            TokenCount("2026-11-01T09:00:03.000Z", 200)
        ], [100, 200], true);

    [Fact]
    public void Treats_a_blank_thread_id_as_absent() =>
        AssertChildUsage(null,
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            SettingsApplied("2026-11-01T09:00:00.000Z", ""),
            TokenCount("2026-11-01T09:00:01.000Z", 100)
        ], [100], true);

    [Fact]
    public void Needs_an_own_boundary_before_dropping_usage_after_a_foreign_marker()
    {
        string[] child =
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            SettingsApplied("2026-11-01T09:00:00.000Z", Parent),
            TokenCount("2026-11-01T09:00:00.010Z", 200),
            TokenCount("2026-11-01T09:00:00.020Z", 300),
            TokenCount("2026-11-01T09:05:00.000Z", 400)
        ];

        AssertChildUsage(null, child, [200, 300, 400], true);
        AssertChildUsage(ParentRollout(), child, [400], false);
    }

    [Fact]
    public void Keeps_usage_logged_before_the_first_foreign_marker()
    {
        string[] child =
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            TokenCount("2026-11-01T09:00:00.000Z", 700),
            SettingsApplied("2026-11-01T09:00:01.000Z", Parent),
            TokenCount("2026-11-01T09:00:01.010Z", 100),
            TokenCount("2026-11-01T09:00:01.020Z", 200),
            SettingsApplied("2026-11-01T09:00:01.040Z", Child),
            TokenCount("2026-11-01T09:05:00.000Z", 400)
        ];

        AssertChildUsage(null, child, [700, 400], true);
        AssertChildUsage(ParentRollout(), child, [700, 400], false);
    }

    [Fact]
    public void Skips_a_parent_suffix_that_a_copied_marker_splits() =>
        AssertChildUsage(ParentRollout(),
        [
            Metadata("2026-11-01T09:00:00.000Z", Child, Parent),
            TokenCount("2026-11-01T09:00:00.010Z", 200),
            SettingsApplied("2026-11-01T09:00:00.020Z", Parent),
            TokenCount("2026-11-01T09:00:00.030Z", 300),
            SettingsApplied("2026-11-01T09:00:00.040Z", Child),
            TokenCount("2026-11-01T09:05:00.000Z", 400)
        ], [400], false);

    // --- Test wyczerpujący filtra (ccusage parser/replay_filter/tests.rs) ---

    private enum Line
    {
        Count,
        OwnSettings,
        ForeignSettings,
        OwnRecord,
        UntaggedRecord
    }

    private enum ParentKind
    {
        Missing,
        Unrelated,
        ReplayedAsPrefix,
        ReplayedAsSuffix
    }

    private const int ParentLength = 3;

    private static CodexSource.RawUsage Usage(long input) => new(input, 0, 0, 0, input);

    private static List<CodexSource.RawUsage> ParentUsage(ParentKind parent) => parent switch
    {
        ParentKind.Missing => [],
        ParentKind.Unrelated => Enumerable.Range(1, ParentLength).Select(value => Usage(value * 7)).ToList(),
        ParentKind.ReplayedAsPrefix => Enumerable.Range(0, ParentLength).Select(value => Usage(100 + value)).ToList(),
        _ => Enumerable.Range(0, ParentLength).Select(value => Usage(99 + value)).ToList()
    };

    private static List<long> CountValues(IReadOnlyList<Line> lines, ParentKind parent)
    {
        var plain = 0;
        var values = new List<long>();
        for (var index = 0; index < lines.Count; index++)
        {
            switch (lines[index])
            {
                case Line.Count:
                    var replayed = parent switch
                    {
                        ParentKind.ReplayedAsPrefix => ParentLength,
                        ParentKind.ReplayedAsSuffix => ParentLength - 1,
                        _ => 0
                    };
                    values.Add(plain < replayed ? 100 + plain : 1_000 + index);
                    plain++;
                    break;
                case Line.OwnRecord:
                    values.Add(5_000 + index);
                    break;
            }
        }

        return values;
    }

    private static (List<long> Emitted, bool Warned) Run(IReadOnlyList<Line> lines, bool spawned, bool childKnown, ParentKind parent)
    {
        var emitted = new List<long>();
        var filter = new CodexReplayFilter<CodexSource.RawUsage>(
            new CodexForkReplay(ParentUsage(parent), parent == ParentKind.Missing ? "parent" : null, childKnown ? "child" : null, spawned),
            _ => true,
            usage => usage,
            usage => emitted.Add(usage.Input));
        var values = new Queue<long>(CountValues(lines, parent));
        var warned = false;
        foreach (var line in lines)
        {
            switch (line)
            {
                case Line.Count:
                    filter.Visit(Usage(values.Dequeue()));
                    break;
                case Line.OwnSettings:
                    warned |= filter.ObserveMarker(CodexOwnershipMarker.ThreadSettings, "child") is not null;
                    break;
                case Line.ForeignSettings:
                    warned |= filter.ObserveMarker(CodexOwnershipMarker.ThreadSettings, "parent") is not null;
                    break;
                case Line.OwnRecord:
                    warned |= filter.ObserveMarker(CodexOwnershipMarker.UsageRecord, "child") is not null;
                    filter.Visit(Usage(values.Dequeue()));
                    break;
                case Line.UntaggedRecord:
                    warned |= filter.ObserveMarker(CodexOwnershipMarker.UsageRecord, null) is not null;
                    break;
            }
        }

        warned |= filter.Finish() is not null;
        return (emitted, warned);
    }

    private static (List<long> Emitted, bool Warned) DocumentedOutcome(IReadOnlyList<Line> lines, bool spawned, bool childKnown, ParentKind parent)
    {
        var values = CountValues(lines, parent);
        bool IsOwn(Line line) => childKnown && line is Line.OwnSettings or Line.OwnRecord;
        var resolution = Enumerable.Range(0, lines.Count)
            .Where(index => (childKnown && lines[index] == Line.OwnRecord) || (spawned && lines[index] == Line.UntaggedRecord))
            .DefaultIfEmpty(lines.Count)
            .First();
        int? boundary = Enumerable.Range(0, Math.Min(resolution + 1, lines.Count)).Where(index => IsOwn(lines[index])).Select(index => (int?)index).FirstOrDefault();
        int? foreign = boundary is { } b
            ? Enumerable.Range(0, b).Where(index => childKnown && lines[index] == Line.ForeignSettings).Select(index => (int?)index).FirstOrDefault()
            : null;
        var windowEnd = boundary ?? resolution;
        var unprovenEnd = foreign ?? windowEnd;
        var usageLines = Enumerable.Range(0, lines.Count)
            .Where(index => lines[index] is Line.Count or Line.OwnRecord)
            .Zip(values)
            .ToList();
        var window = usageLines.Where(pair => pair.First < windowEnd).Select(pair => pair.Second).ToList();
        var parentValues = ParentUsage(parent).Select(usage => usage.Input).ToList();
        var matched = Enumerable.Range(0, Math.Min(parentValues.Count, window.Count) + 1)
            .Where(length => parentValues.Take(length).SequenceEqual(window.Take(length)) ||
                             parentValues.Skip(parentValues.Count - length).SequenceEqual(window.Take(length)))
            .Max();
        var emitted = new List<long>();
        for (var ordinal = 0; ordinal < usageLines.Count; ordinal++)
        {
            var (index, value) = usageLines[ordinal];
            var replayed = index < windowEnd && ordinal < matched;
            var copied = foreign is { } f && boundary is { } own && f < index && index < own;
            if (!replayed && !copied)
            {
                emitted.Add(value);
            }
        }

        var warned = parent == ParentKind.Missing && usageLines.Any(pair => pair.First < unprovenEnd);
        return (emitted, warned);
    }

    private static IEnumerable<Line[]> Sequences(int maxLength)
    {
        var frontier = new List<Line[]> { Array.Empty<Line>() };
        yield return [];
        for (var length = 0; length < maxLength; length++)
        {
            frontier = frontier.SelectMany(prefix => Enum.GetValues<Line>().Select(line => prefix.Append(line).ToArray())).ToList();
            foreach (var sequence in frontier)
            {
                yield return sequence;
            }
        }
    }

    [Fact]
    public void Filter_follows_the_documented_fork_rule_for_every_short_rollout()
    {
        var failures = new List<string>();
        foreach (var lines in Sequences(6))
        {
            foreach (var spawned in new[] { false, true })
            {
                foreach (var childKnown in new[] { false, true })
                {
                    foreach (var parent in Enum.GetValues<ParentKind>())
                    {
                        var actual = Run(lines, spawned, childKnown, parent);
                        var expected = DocumentedOutcome(lines, spawned, childKnown, parent);
                        if (!actual.Emitted.SequenceEqual(expected.Emitted) || actual.Warned != expected.Warned)
                        {
                            failures.Add($"[{string.Join(',', lines)}] spawned={spawned} child={childKnown} {parent}: " +
                                         $"[{string.Join(',', actual.Emitted)}]/{actual.Warned} vs [{string.Join(',', expected.Emitted)}]/{expected.Warned}");
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} przypadków: {string.Join('\n', failures.Take(10))}");
    }
}
