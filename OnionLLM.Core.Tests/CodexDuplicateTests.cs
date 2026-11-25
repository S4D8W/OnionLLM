using System.Text.Json;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Duplikaty Codexa między rolloutami: nazwane żądania (<c>record_response_id</c>), kopie bez rekordu i głębokość forka —
/// przeniesione z ccusage <c>codex/src/aggregate/tests.rs</c> (#2208, #2366).
/// </summary>
public class CodexDuplicateTests
{
    private const string Day = "2026-11-01";

    /// <summary>Wpisy po deduplikacji w kolejności ledgera (czas, ścieżka pliku), z regułami tej samej instancji źródła.</summary>
    private static List<UsageEntry> Load(string root, params string[] roots)
    {
        var source = new CodexSource();
        var config = TestData.ConfigWith("codex", root);
        config.Sources["codex"].Paths = [root, .. roots];
        var diagnostics = new ParseDiagnostics();
        var entries = source.EnumerateFiles(config)
            .SelectMany(file => source.Parse(file, diagnostics, CancellationToken.None).Select(entry => (file.Path, Entry: entry)))
            .OrderBy(item => item.Entry.TimestampMs)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Select(item => item.Entry)
            .ToList();
        return Deduplicator.Apply(entries, _ => source);
    }

    /// <summary>Wejście jak w ccusage <c>input_tokens</c> (z odczytem i zapisem cache).</summary>
    private static long Input(UsageEntry entry) => entry.InputTokens + entry.CacheReadTokens + entry.CacheCreationTokens;

    private static long DailyInput(List<UsageEntry> entries, string day) =>
        entries.Where(entry => entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd") == day).Sum(Input);

    private static Dictionary<string, long> InputBySession(IEnumerable<UsageEntry> entries) =>
        entries.GroupBy(entry => entry.SessionId).ToDictionary(group => group.Key, group => group.Sum(Input));

    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static string Join(params string[] lines) => string.Join('\n', lines);

    private static string SessionMeta(string timestamp, string id, string? parent) => parent is null
        ? Line(new { timestamp, type = "session_meta", payload = new { id } })
        : Line(new { timestamp, type = "session_meta", payload = new { id, forked_from_id = parent } });

    /// <summary>ccusage <c>fork_token_count_line</c>.</summary>
    private static string ForkTokenCount(string timestamp, long last, long total) => Line(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                model = "gpt-5.2",
                last_token_usage = new { input_tokens = last, output_tokens = 1, total_tokens = last + 1 },
                total_token_usage = new { input_tokens = total, output_tokens = 1, total_tokens = total + 1 }
            }
        }
    });

    private const string ParentRollout = "2026/05/12/rollout-2026-05-12T08-00-00-parent-xyz";
    private const string ForkRollout = "2026/05/12/rollout-2026-05-12T08-30-00-fork-abc";

    private static (string Parent, string Fork) ParentAndForkRequestingAtTheSameInstant() => (
        Join(SessionMeta("2026-05-12T08:00:00.000Z", "parent-xyz", null),
            ForkTokenCount("2026-05-12T08:01:00.000Z", 1_000, 1_000),
            ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 1_100)),
        Join(SessionMeta("2026-05-12T08:30:00.000Z", "fork-abc", "parent-xyz"),
            ForkTokenCount("2026-05-12T08:30:00.000Z", 1_000, 1_000),
            ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 1_100)));

    [Fact]
    public void Counts_a_fork_and_its_parent_requesting_at_the_same_instant_twice()
    {
        var (parent, fork) = ParentAndForkRequestingAtTheSameInstant();
        using var directory = new TempDirectory();
        directory.Write($"sessions/{ParentRollout}.jsonl", parent);
        directory.Write($"sessions/{ForkRollout}.jsonl", fork);

        var entries = Load(directory.Path);

        Assert.Equal(1_200, DailyInput(entries, "2026-05-12"));
        Assert.Equal(new Dictionary<string, long> { [ParentRollout] = 1_100, [ForkRollout] = 100 }, InputBySession(entries));
    }

    [Fact]
    public void Counts_a_copy_without_fork_metadata_once_in_every_report()
    {
        var request = ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 1_100);
        using var directory = new TempDirectory();
        directory.Write("sessions/a-original.jsonl", Join(SessionMeta("2026-05-12T08:00:00.000Z", "original", null), request));
        directory.Write("sessions/b-copy.jsonl", Join(SessionMeta("2026-05-12T08:00:00.000Z", "copy", null), request));

        var entries = Load(directory.Path);

        Assert.Equal(100, DailyInput(entries, "2026-05-12"));
        Assert.Equal(new Dictionary<string, long> { ["a-original"] = 100 }, InputBySession(entries));
    }

    [Fact]
    public void Collapses_a_parent_copy_while_keeping_the_fork_request()
    {
        var (parent, fork) = ParentAndForkRequestingAtTheSameInstant();
        using var directory = new TempDirectory();
        directory.Write("sessions/0-copy.jsonl", ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 1_100));
        directory.Write($"sessions/{ParentRollout}.jsonl", parent);
        directory.Write($"sessions/{ForkRollout}.jsonl", fork);

        var entries = Load(directory.Path);

        Assert.Equal(1_200, DailyInput(entries, "2026-05-12"));
        Assert.Equal(new Dictionary<string, long> { ["0-copy"] = 100, [ParentRollout] = 1_000, [ForkRollout] = 100 },
            InputBySession(entries));
    }

    private static (string Parent, string Fork) ForkPair(string parent, string fork, long prefix)
    {
        var request = ForkTokenCount("2026-05-12T09:00:00.000Z", 100, prefix + 100);
        return (
            Join(SessionMeta("2026-05-12T08:00:00.000Z", parent, null), ForkTokenCount("2026-05-12T08:01:00.000Z", prefix, prefix), request),
            Join(SessionMeta("2026-05-12T08:30:00.000Z", fork, parent), ForkTokenCount("2026-05-12T08:30:00.000Z", prefix, prefix), request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_of_an_ancestor_does_not_add_a_request_across_fork_pairs(bool withCopy)
    {
        var (parentA, forkB) = ForkPair("parent-a", "fork-b", 1_000);
        var (parentC, forkD) = ForkPair("parent-c", "fork-d", 2_000);
        using var directory = new TempDirectory();
        directory.Write("sessions/a.jsonl", parentA);
        directory.Write("sessions/b.jsonl", forkB);
        directory.Write("sessions/c.jsonl", parentC);
        directory.Write("sessions/d.jsonl", forkD);
        if (withCopy)
        {
            directory.Write("sessions/archived-a.jsonl", parentA);
        }

        var entries = Load(directory.Path);

        Assert.Equal(3_200, DailyInput(entries, "2026-05-12"));
        Assert.Equal(3_200, InputBySession(entries).Values.Sum());
    }

    [Fact]
    public void Counts_a_reference_backed_fork_and_its_parent_requesting_at_the_same_instant_twice()
    {
        var request = ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 100);
        var forkMeta = Line(new
        {
            timestamp = "2026-05-12T08:30:00.000Z",
            type = "session_meta",
            payload = new { id = "fork-abc", forked_from_id = "parent-xyz", history_base = new { thread_id = "parent-xyz", end_ordinal_exclusive = 1 } }
        });
        using var directory = new TempDirectory();
        directory.Write("sessions/parent.jsonl", Join(SessionMeta("2026-05-12T08:00:00.000Z", "parent-xyz", null), request));
        directory.Write("sessions/fork.jsonl", Join(forkMeta, request));

        var entries = Load(directory.Path);

        Assert.Equal(200, DailyInput(entries, "2026-05-12"));
        Assert.Equal(new Dictionary<string, long> { ["fork"] = 100, ["parent"] = 100 }, InputBySession(entries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keeps_lineage_through_an_ancestor_outside_the_reported_day(bool referenceBackedLeaf)
    {
        var request = ForkTokenCount("2026-05-12T09:00:00.000Z", 100, 1_150);
        string[] leaf = referenceBackedLeaf
            ?
            [
                Line(new
                {
                    timestamp = "2026-05-12T08:00:00.000Z",
                    type = "session_meta",
                    payload = new { id = "leaf-c", forked_from_id = "middle-b", history_base = new { thread_id = "middle-b", end_ordinal_exclusive = 2 } }
                }),
                request
            ]
            :
            [
                SessionMeta("2026-05-12T08:00:00.000Z", "leaf-c", "middle-b"),
                ForkTokenCount("2026-05-12T08:00:00.000Z", 1_000, 1_000),
                ForkTokenCount("2026-05-12T08:00:00.000Z", 50, 1_050),
                request
            ];
        using var directory = new TempDirectory();
        directory.Write("sessions/2026/05/01/rollout-2026-05-01T08-00-00-root-a.jsonl", Join(
            SessionMeta("2026-05-01T08:00:00.000Z", "root-a", null),
            ForkTokenCount("2026-05-01T08:01:00.000Z", 1_000, 1_000),
            request));
        directory.Write("sessions/2026/05/01/rollout-2026-05-01T09-00-00-middle-b.jsonl", Join(
            SessionMeta("2026-05-01T09:00:00.000Z", "middle-b", "root-a"),
            ForkTokenCount("2026-05-01T09:00:00.000Z", 1_000, 1_000),
            ForkTokenCount("2026-05-01T10:00:00.000Z", 50, 1_050)));
        directory.Write("sessions/2026/05/12/rollout-2026-05-12T08-00-00-leaf-c.jsonl", Join(leaf));

        var day = Load(directory.Path).Where(entry => entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd") == "2026-05-12").ToList();

        Assert.Equal(200, day.Sum(Input));
        Assert.Equal(new Dictionary<string, long>
        {
            ["2026/05/01/rollout-2026-05-01T08-00-00-root-a"] = 100,
            ["2026/05/12/rollout-2026-05-12T08-00-00-leaf-c"] = 100
        }, InputBySession(day));
    }

    /// <summary>ccusage <c>IDENTICAL_REQUESTS_ROLLOUT</c>: subagent z dwoma żądaniami o identycznym zużyciu w tej samej chwili.</summary>
    private const string IdenticalRequestsRollout = """
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"session_meta","payload":{"id":"01a1967c-0000-7000-8000-00000000000a","forked_from_id":"01a1967b-0000-7000-8000-000000000001","timestamp":"2026-11-01T09:42:09.923Z","originator":"codex_vscode","cli_version":"0.164.0-alpha.11.1","source":{"subagent":{"thread_spawn":{"parent_thread_id":"01a1967b-0000-7000-8000-000000000001","depth":1}}},"model_provider":"openai"}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"compacted","payload":{"message":"","replacement_history":[]}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_id":"01a1967b-0000-7000-8000-000000000001","thread_settings":{}}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"turn_context","payload":{"model":"gpt-6-astra"}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":990000,"cached_input_tokens":0,"output_tokens":10000,"reasoning_output_tokens":0,"total_tokens":1000000},"last_token_usage":{"input_tokens":990000,"cached_input_tokens":0,"output_tokens":10000,"reasoning_output_tokens":0,"total_tokens":1000000}}}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"token_usage_record","payload":{"thread_id":"01a1967c-0000-7000-8000-00000000000a","response_id":"resp_child_1","usage":{"input_tokens":10000,"cached_input_tokens":2000,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":10100}}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000000,"cached_input_tokens":2000,"output_tokens":10100,"reasoning_output_tokens":0,"total_tokens":1010100},"last_token_usage":{"input_tokens":10000,"cached_input_tokens":2000,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":10100}}}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"token_usage_record","payload":{"thread_id":"01a1967c-0000-7000-8000-00000000000a","response_id":"resp_child_2","usage":{"input_tokens":10000,"cached_input_tokens":2000,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":10100}}}
        {"timestamp":"2026-11-01T09:42:09.923Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1010000,"cached_input_tokens":4000,"output_tokens":10200,"reasoning_output_tokens":0,"total_tokens":1020200},"last_token_usage":{"input_tokens":10000,"cached_input_tokens":2000,"output_tokens":100,"reasoning_output_tokens":0,"total_tokens":10100}}}}
        """;

    private const string IdenticalRolloutPath = "sessions/2026/11/01/rollout-2026-11-01T09-42-09-01a1967c-0000-7000-8000-00000000000a.jsonl";

    [Fact]
    public void Counts_identical_requests_within_one_rollout()
    {
        using var directory = new TempDirectory();
        directory.Write(IdenticalRolloutPath, IdenticalRequestsRollout);

        var entries = Load(directory.Path);

        Assert.Equal([(8_000L, 2_000L, 100L), (8_000L, 2_000L, 100L)],
            entries.Select(entry => (entry.InputTokens, entry.CacheReadTokens, entry.OutputTokens)));
    }

    private static string IdenticalRequestLine() => Line(new
    {
        timestamp = "2026-11-01T09:00:00.000Z",
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new { model = "gpt-5.2", last_token_usage = new { input_tokens = 1_000, cached_input_tokens = 100, output_tokens = 10, total_tokens = 1_010 } }
        }
    });

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Counts_each_copied_request_once_across_rollouts(int copiedRequests)
    {
        var request = IdenticalRequestLine();
        using var directory = new TempDirectory();
        directory.Write("sessions/a-original.jsonl", Join(request, request));
        directory.Write("sessions/b-copy.jsonl", Join(Enumerable.Repeat(request, copiedRequests).ToArray()));

        Assert.Equal(2_000, DailyInput(Load(directory.Path), Day));
    }

    [Theory]
    [InlineData(false, 32_000, 8_000, 400)]
    [InlineData(true, 16_000, 4_000, 200)]
    public void Counts_spawned_siblings_with_identical_usage_and_dedupes_copied_records(bool copy, long input, long cached, long output)
    {
        var second = copy
            ? IdenticalRequestsRollout.Replace("00000000000a", "00000000000c")
            : IdenticalRequestsRollout.Replace("00000000000a", "00000000000b")
                .Replace("resp_child_1", "resp_child_3")
                .Replace("resp_child_2", "resp_child_4");
        using var directory = new TempDirectory();
        directory.Write(IdenticalRolloutPath, IdenticalRequestsRollout);
        directory.Write(IdenticalRolloutPath.Replace("00000000000a", "00000000000b"), second);

        var entries = Load(directory.Path);

        Assert.Equal((input, cached, output),
            (entries.Sum(entry => entry.InputTokens), entries.Sum(entry => entry.CacheReadTokens), entries.Sum(entry => entry.OutputTokens)));
    }

    /// <summary>ccusage <c>recorded_requests</c>: identyczne żądania o 09:01, nazwane (<c>response_id</c>) lub bez rekordu.</summary>
    private static string RecordedRequests(string? serviceTier, params string?[] responseIds)
    {
        var lines = new List<string> { Line(new { timestamp = "2026-11-01T09:00:00.000Z", type = "turn_context", payload = new { model = "gpt-5.2" } }) };
        if (serviceTier is not null)
        {
            lines.Add(ServiceTier("2026-11-01T09:00:00.000Z", serviceTier));
        }

        const string timestamp = "2026-11-01T09:01:00.000Z";
        var usage = new { input_tokens = 1_000, cached_input_tokens = 100, output_tokens = 10, total_tokens = 1_010 };
        for (var index = 0; index < responseIds.Length; index++)
        {
            if (responseIds[index] is { } responseId)
            {
                lines.Add(Line(new { timestamp, type = "token_usage_record", payload = new { response_id = responseId, usage } }));
            }

            var total = (index + 1) * 1_000;
            lines.Add(Line(new
            {
                timestamp,
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = usage,
                        total_token_usage = new { input_tokens = total, cached_input_tokens = total / 10, output_tokens = total / 100, total_tokens = total + total / 100 }
                    }
                }
            }));
        }

        return Join(lines.ToArray());
    }

    private static string ServiceTier(string timestamp, string tier) =>
        Line(new { timestamp, type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { service_tier = tier } } });

    private static List<UsageEntry> LoadInOrder(params string[] files)
    {
        using var directory = new TempDirectory();
        for (var index = 0; index < files.Length; index++)
        {
            directory.Write($"sessions/{index}-rollout.jsonl", files[index]);
        }

        return Load(directory.Path);
    }

    public static TheoryData<string, string[], int> RecordlessCopyScenarios()
    {
        var original = RecordedRequests(null, "resp_a1", "resp_a2");
        var sibling = RecordedRequests(null, "resp_b1", "resp_b2");
        var copy = RecordedRequests(null, null, null);
        var partial = RecordedRequests(null, "resp_a2");
        return new TheoryData<string, string[], int>
        {
            { "recorded first", [original, copy], 2 },
            { "recordless first", [copy, original], 2 },
            { "siblings, then copy", [original, sibling, copy], 4 },
            { "copy between siblings", [original, copy, sibling], 4 },
            { "copy first, then siblings", [copy, original, sibling], 4 },
            { "partial copy last", [original, partial], 2 },
            { "partial copy first", [partial, original], 2 },
            { "partial and recordless copies", [partial, copy, original], 2 }
        };
    }

    [Theory]
    [MemberData(nameof(RecordlessCopyScenarios))]
    public void Matches_recordless_copies_to_recorded_requests_in_either_file_order(string scenario, string[] files, int requests) =>
        Assert.True(1_000L * requests == DailyInput(LoadInOrder(files), Day), scenario);

    [Fact]
    public void Keeps_recorded_tiers_when_a_recordless_copy_cannot_name_its_request()
    {
        var fast = RecordedRequests("priority", "resp_fast");
        var flex = RecordedRequests("flex", "resp_flex");
        var copy = RecordedRequests("priority", [null]);
        string[][] orders = [[fast, flex, copy], [fast, copy, flex], [flex, fast, copy], [flex, copy, fast], [copy, fast, flex], [copy, flex, fast]];

        foreach (var order in orders)
        {
            var entries = LoadInOrder(order);

            Assert.Equal(2_000, entries.Sum(Input));
            Assert.Equal(1_000, entries.Where(entry => entry.Speed == SpeedTier.Fast).Sum(Input));
            Assert.Equal(1_000, entries.Where(entry => entry.Speed == SpeedTier.Flex).Sum(Input));
        }
    }

    [Theory]
    [InlineData("0-child", "1-parent")]
    [InlineData("0-parent", "1-child")]
    public void Credits_a_replaced_stand_in_to_the_recording_session(string first, string second)
    {
        var child = RecordedRequests(null, [null]);
        var parent = RecordedRequests(null, "resp_parent");
        using var directory = new TempDirectory();
        foreach (var name in new[] { first, second })
        {
            directory.Write($"sessions/{name}.jsonl", name.EndsWith("-parent", StringComparison.Ordinal) ? parent : child);
        }

        var entries = Load(directory.Path);

        var parentSession = new[] { first, second }.Single(name => name.EndsWith("-parent", StringComparison.Ordinal));
        Assert.Equal(new Dictionary<string, long> { [parentSession] = 1_000 }, InputBySession(entries));
    }

    [Fact]
    public void Preserves_recordless_requests_that_cooccur_with_their_recorded_match()
    {
        var recorded = RecordedRequests("priority", "resp_x");
        var mixedAfter = RecordedRequests("standard", "resp_x", null);
        var mixedBefore = RecordedRequests("standard", null, "resp_x");
        var recordless = RecordedRequests("standard", null, null);
        foreach (var mixed in new[] { mixedAfter, mixedBefore })
        {
            string[][] orders = [[recorded, mixed], [mixed, recorded], [recorded, recordless, mixed], [mixed, recorded, recordless], [recordless, mixed, recorded]];
            foreach (var order in orders)
            {
                Assert.Equal(2_000, LoadInOrder(order).Sum(Input));
            }
        }
    }

    /// <summary>ccusage <c>same_instant_rollout</c>: żądania o 09:15 z identycznym zużyciem, nazwane albo bez rekordu.</summary>
    private static string SameInstantRollout(string id, string? parent, int requests, bool recorded)
    {
        const string timestamp = "2026-11-01T09:15:00.000Z";
        var usage = new { input_tokens = 100, cached_input_tokens = 0, output_tokens = 1, total_tokens = 101 };
        var lines = new List<string>
        {
            parent is null
                ? Line(new { timestamp = "2026-11-01T09:00:00.000Z", type = "session_meta", payload = new { id } })
                : Line(new
                {
                    timestamp = "2026-11-01T09:00:00.000Z",
                    type = "session_meta",
                    payload = new { id, forked_from_id = parent, source = new { subagent = new { thread_spawn = new { parent_thread_id = parent, depth = 1 } } } }
                }),
            Line(new { timestamp = "2026-11-01T09:00:00.000Z", type = "turn_context", payload = new { model = "gpt-5.2" } })
        };
        if (parent is not null)
        {
            lines.Add(Line(new { timestamp = "2026-11-01T09:10:00.000Z", type = "event_msg", payload = new { type = "thread_settings_applied", thread_id = id, thread_settings = new { } } }));
        }

        for (var request = 1; request <= requests; request++)
        {
            if (recorded)
            {
                lines.Add(Line(new { timestamp, type = "token_usage_record", payload = new { thread_id = id, response_id = $"resp_{id}_{request}", usage } }));
            }

            var total = new { input_tokens = 100 * request, cached_input_tokens = 0, output_tokens = request, total_tokens = 101 * request };
            lines.Add(Line(new { timestamp, type = "event_msg", payload = new { type = "token_count", info = new { last_token_usage = usage, total_token_usage = total } } }));
        }

        return Join(lines.ToArray());
    }

    /// <summary>ccusage <c>assert_same_instant_requests</c> — obie kolejności plików.</summary>
    private static void AssertSameInstantRequests((string Name, string Contents)[] rollouts, long daily, long[] sessions)
    {
        foreach (var order in new[] { rollouts, rollouts.Reverse().ToArray() })
        {
            using var directory = new TempDirectory();
            for (var position = 0; position < order.Length; position++)
            {
                directory.Write($"sessions/{position}-{order[position].Name}.jsonl", order[position].Contents);
            }

            var entries = Load(directory.Path);

            Assert.Equal(daily, DailyInput(entries, Day));
            Assert.Equal(sessions, InputBySession(entries).Values.Order());
        }
    }

    [Fact]
    public void Keeps_a_spawned_childs_own_request_beside_a_matching_recorded_parent_request() =>
        AssertSameInstantRequests(
        [
            ("parent", SameInstantRollout("parent-xyz", null, 1, recorded: true)),
            ("child", SameInstantRollout("child-abc", "parent-xyz", 1, recorded: false))
        ], 200, [100, 100]);

    [Fact]
    public void Settles_repeated_fork_requests_at_the_depth_used_for_pairing() =>
        AssertSameInstantRequests(
        [
            ("parent", SameInstantRollout("parent-xyz", null, 1, recorded: true)),
            ("child", SameInstantRollout("child-abc", "parent-xyz", 2, recorded: false)),
            ("unrelated", SameInstantRollout("other-def", null, 2, recorded: false))
        ], 400, [100, 100, 200]);
}
