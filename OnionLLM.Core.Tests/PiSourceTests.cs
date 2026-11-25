using System.Text.Json;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class PiSourceTests
{
    private static List<Models.UsageEntry> Load(TempDirectory directory, string store = "sessions") =>
        TestData.ParseAll(new PiSource(), TestData.ConfigWith(PiSource.SourceId, System.IO.Path.Combine(directory.Path, store)));

    private static string Session(string id, string timestamp, string? parent = null) =>
        JsonSerializer.Serialize(parent is null
            ? new Dictionary<string, object> { ["type"] = "session", ["id"] = id, ["timestamp"] = timestamp, ["cwd"] = "C:\\work\\demo" }
            : new Dictionary<string, object> { ["type"] = "session", ["id"] = id, ["timestamp"] = timestamp, ["cwd"] = "C:\\work\\demo", ["parentSession"] = parent });

    private static string Usage(string timestamp, long input, long output, long cacheRead, long cacheWrite, string model = "gpt-5", double? cost = null) =>
        JsonSerializer.Serialize(new
        {
            type = "message",
            timestamp,
            message = new
            {
                role = "assistant",
                model,
                usage = new
                {
                    input,
                    output,
                    cacheRead,
                    cacheWrite,
                    totalTokens = input + output + cacheRead + cacheWrite,
                    cost = new { total = cost ?? 0 }
                }
            }
        });

    private static string Linked(string id, string? parentId, string timestamp, long input) =>
        JsonSerializer.Serialize(new
        {
            type = "message",
            id,
            parentId,
            timestamp,
            message = new
            {
                role = "assistant",
                model = "gpt-5",
                usage = new { input, output = 10, cacheRead = 20, cacheWrite = 3, totalTokens = input + 33 }
            }
        });

    [Fact]
    public void Assistant_usage_is_mapped_with_project_session_and_cost()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/--work-demo--/2025-12-30T10-00-00-000Z_abc-123.jsonl", string.Join('\n',
            Session("abc-123", "2025-12-30T10:00:00.000Z"),
            """{"type":"message","timestamp":"2025-12-30T10:00:01.000Z","message":{"role":"user","content":"hi"}}""",
            Usage("2025-12-30T10:00:02.000Z", 270, 90, 8000, 400, "claude-sonnet-4", 1.1)));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("--work-demo--", entry.Project);
        Assert.Equal("abc-123", entry.SessionId);
        Assert.Equal(@"C:\work\demo", entry.ProjectPath);
        Assert.Equal("claude-sonnet-4", entry.Model);
        Assert.Equal(270, entry.InputTokens);
        Assert.Equal(90, entry.OutputTokens);
        Assert.Equal(8000, entry.CacheReadTokens);
        Assert.Equal(400, entry.CacheCreationTokens);
        Assert.Equal(1.1, entry.RecordedCostUsd);
    }

    [Fact]
    public void Fork_skips_replayed_parent_prefix_but_keeps_child_usage()
    {
        using var directory = new TempDirectory();
        var parent = directory.Write("sessions/project-a/root.jsonl", string.Join('\n',
            Session("root", "2026-01-01T00:00:00.000Z"),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            Usage("2026-01-03T00:00:00.000Z", 200, 20, 30, 4),
            Usage("2026-01-04T00:00:00.000Z", 50, 5, 6, 1)));
        directory.Write("sessions/project-a/child-a.jsonl", string.Join('\n',
            Session("child-a", "2026-01-03T00:00:00.000Z", parent),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            Usage("2026-01-03T00:00:00.000Z", 200, 20, 30, 4),
            Usage("2026-01-04T00:00:00.000Z", 50, 5, 6, 1)));
        directory.Write("sessions/project-a/child-b.jsonl", string.Join('\n',
            Session("child-b", "2026-01-03T00:00:00.000Z", parent),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            Usage("2026-01-03T00:00:00.000Z", 200, 20, 30, 999),
            Usage("2026-01-03T01:00:00.000Z", 70, 7, 8, 2)));

        var entries = Deduplicator.Apply(Load(directory));

        Assert.Equal(6, entries.Count);
        Assert.Equal(50, Assert.Single(entries, entry => entry.SessionId == "child-a").InputTokens);
        var childB = entries.Where(entry => entry.SessionId == "child-b").ToList();
        Assert.Equal(2, childB.Count);
        Assert.Equal(999, childB[0].CacheCreationTokens);
    }

    [Fact]
    public void Fork_follows_active_parent_branch_after_abandoned_sibling()
    {
        using var directory = new TempDirectory();
        var parent = directory.Write("sessions/project-a/root.jsonl", string.Join('\n',
            Session("root", "2026-01-01T00:00:00.000Z"),
            Linked("a", null, "2026-01-02T10:00:00.000Z", 100),
            Linked("x", "a", "2026-01-02T11:00:00.000Z", 200),
            Linked("y", "a", "2026-01-02T12:00:00.000Z", 300),
            Linked("z", "y", "2026-01-02T13:00:00.000Z", 400)));
        directory.Write("sessions/project-a/child.jsonl", string.Join('\n',
            Session("child", "2026-01-03T00:00:00.000Z", parent),
            Linked("copy-a", null, "2026-01-02T10:00:00.000Z", 100),
            Linked("copy-y", "copy-a", "2026-01-02T12:00:00.000Z", 300),
            Linked("copy-z", "copy-y", "2026-01-02T13:00:00.000Z", 400),
            Linked("child-only", "copy-z", "2026-01-03T01:00:00.000Z", 500)));

        var entries = Deduplicator.Apply(Load(directory));

        Assert.Equal(5, entries.Count);
        Assert.Contains(entries, entry => entry.SessionId == "root" && entry.InputTokens == 200);
        Assert.Equal(500, Assert.Single(entries, entry => entry.SessionId == "child").InputTokens);
    }

    [Fact]
    public void Missing_parent_keeps_all_child_usage()
    {
        using var directory = new TempDirectory();
        var missing = System.IO.Path.Combine(directory.Path, "sessions", "project-a", "missing.jsonl");
        directory.Write("sessions/project-a/child.jsonl", string.Join('\n',
            Session("child", "2026-01-03T00:00:00.000Z", missing),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3)));

        Assert.Single(Load(directory));
    }

    [Fact]
    public void Subagent_artifacts_and_root_files_are_handled()
    {
        using var directory = new TempDirectory();
        var line = Usage("2026-01-02T10:00:00.000Z", 1, 1, 0, 0);
        directory.Write("sessions/project-a/subagent-artifacts/run/debug.jsonl", line);
        directory.Write("sessions/project-a/run-1/child.jsonl", line);
        directory.Write("sessions/loose.jsonl", line);

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Project == "unknown");
        Assert.Contains(entries, entry => entry.Project == "project-a");
    }

    private static Config.OnionConfig StoreConfig(Action<Config.SourceSettings> configure)
    {
        var config = new Config.OnionConfig { Timezone = "UTC", Offline = true };
        var settings = new Config.SourceSettings();
        configure(settings);
        config.Sources[PiSource.SourceId] = settings;
        return config;
    }

    [Fact]
    public void Agent_directories_read_their_sessions_store_together_with_explicit_paths()
    {
        // ccusage pi/src/paths.rs: agent_directory_option_reads_each_sessions_store.
        using var directory = new TempDirectory();
        directory.Write(".pi/agent/sessions/project-a/a_s1.jsonl", Usage("2026-01-02T00:00:00.000Z", 10, 1, 0, 0));
        directory.Write("other-store/project-b/b_s2.jsonl", Usage("2026-01-02T00:00:00.000Z", 20, 1, 0, 0));
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "empty-agent"));
        var config = StoreConfig(settings =>
        {
            settings.Paths = [System.IO.Path.Combine(directory.Path, "other-store")];
            settings.AgentDirs = [System.IO.Path.Combine(directory.Path, ".pi", "agent") + "," + System.IO.Path.Combine(directory.Path, "empty-agent")];
        });
        var source = new PiSource();

        var entries = TestData.ParseAll(source, config);

        Assert.Equal(["project-a", "project-b"], entries.Select(entry => entry.Project!).Order().ToArray());
        Assert.Equal(System.IO.Path.Combine(directory.Path, ".pi", "agent", "sessions"), source.DefaultStoreRoots(config)[1]);
    }

    [Fact]
    public void Named_store_labels_models_and_matches_parents_across_its_paths()
    {
        // ccusage pi/src/loader/tests.rs: matches_parent_files_across_multiple_paths_of_one_named_store.
        using var directory = new TempDirectory();
        var parent = directory.Write("first/project-a/root.jsonl", string.Join('\n',
            Session("root", "2026-01-01T00:00:00.000Z"),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3)));
        directory.Write("second/project-a/child.jsonl", string.Join('\n',
            Session("child", "2026-01-03T00:00:00.000Z", parent),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            Usage("2026-01-03T01:00:00.000Z", 50, 5, 6, 1)));
        directory.Write("sessions/project-a/own.jsonl", Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        var config = StoreConfig(settings =>
        {
            settings.Paths = [System.IO.Path.Combine(directory.Path, "sessions")];
            settings.Stores =
            [
                new Config.SessionStore
                {
                    Name = "omp",
                    Path = System.IO.Path.Combine(directory.Path, "first") + ", " + System.IO.Path.Combine(directory.Path, "second")
                }
            ];
        });
        var source = new PiSource();

        var entries = Deduplicator.Apply(TestData.ParseAll(source, config), _ => source);

        Assert.Equal(3, entries.Count);
        var child = Assert.Single(entries, entry => entry.SessionId == "child");
        Assert.Equal("[omp] gpt-5", child.Model);
        Assert.Equal(50, child.InputTokens);
        Assert.Equal(["[omp] gpt-5", "gpt-5"], source.PricingCandidates(child));
        var own = Assert.Single(entries, entry => entry.SessionId == "own");
        Assert.Equal("gpt-5", own.Model);
        Assert.Equal(["gpt-5"], source.PricingCandidates(own));
    }

    [Fact]
    public void Invalid_and_overlapping_stores_are_reported_and_skipped()
    {
        // ccusage ccusage-config/src/config/pi_stores.rs i ccusage-adapter-all/src/loader/pi_stores.rs.
        using var directory = new TempDirectory();
        var sessions = System.IO.Path.Combine(directory.Path, "sessions");
        directory.Write("sessions/project-a/s.jsonl", Usage("2026-01-02T00:00:00.000Z", 10, 1, 0, 0));
        directory.Write("omp/project-a/s.jsonl", Usage("2026-01-02T00:00:00.000Z", 20, 1, 0, 0));
        var config = StoreConfig(settings =>
        {
            settings.Paths = [sessions];
            settings.Stores =
            [
                new Config.SessionStore { Name = "omp", Path = System.IO.Path.Combine(directory.Path, "omp") },
                new Config.SessionStore { Name = "nested", Path = System.IO.Path.Combine(sessions, "project-a") },
                new Config.SessionStore { Name = "Bad", Path = sessions },
                new Config.SessionStore { Name = "claude", Path = System.IO.Path.Combine(directory.Path, "x") },
                new Config.SessionStore { Name = "omp", Path = System.IO.Path.Combine(directory.Path, "y") },
                new Config.SessionStore { Name = "empty", Path = " " },
                new Config.SessionStore { Name = "copy", Path = System.IO.Path.Combine(directory.Path, "omp") }
            ];
        });
        var source = new PiSource();
        var diagnostics = new ParseDiagnostics();

        var errors = source.ValidateStores(config);
        var entries = TestData.ParseAll(source, config, diagnostics);

        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, error => error.Contains("'nested'") && error.Contains("domyślny magazyn pi"));
        Assert.Contains(errors, error => error.Contains("pi.stores[2].name"));
        Assert.Contains(errors, error => error.Contains("'claude'") && error.Contains("wbudowanym"));
        Assert.Contains(errors, error => error.Contains("Zduplikowana") && error.Contains("'omp'"));
        Assert.Contains(errors, error => error.Contains("pi.stores[5]") && error.Contains("pusta"));
        Assert.Contains(errors, error => error.Contains("'copy'") && error.Contains("pi.stores 'omp'"));
        Assert.Equal(6, diagnostics.SkippedLines);
        Assert.Equal(["[omp] gpt-5", "gpt-5"], entries.Select(entry => entry.Model!).Order().ToArray());
    }

    [Fact]
    public async Task Fork_is_recounted_when_its_parent_appears_after_the_first_sync()
    {
        // ccusage liczy forki przy każdym odczycie (pi/src/loader.rs PiReplayPlan) — tu odcisk pliku obejmuje rodzica.
        using var directory = new TempDirectory();
        var parentPath = System.IO.Path.Combine(directory.Path, "sessions", "project-a", "root.jsonl");
        directory.Write("sessions/project-a/child.jsonl", string.Join('\n',
            Session("child", "2026-01-03T00:00:00.000Z", parentPath),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            Usage("2026-01-03T01:00:00.000Z", 50, 5, 6, 1)));
        var config = StoreConfig(settings => settings.Paths = [System.IO.Path.Combine(directory.Path, "sessions")]);
        using var engine = new UsageEngine(config, new UsageLedger(System.IO.Path.Combine(directory.Path, "ledger.db")), [new PiSource()]);

        await engine.SyncAsync(force: true);
        Assert.Equal(2, engine.LoadEntries().Count(entry => entry.SessionId == "child"));

        directory.Write("sessions/project-a/root.jsonl", string.Join('\n',
            Session("root", "2026-01-01T00:00:00.000Z"),
            Usage("2026-01-02T10:00:00.000Z", 100, 10, 20, 3)));
        await engine.SyncAsync(force: true);

        var entries = engine.LoadEntries();
        Assert.Equal(50, Assert.Single(entries, entry => entry.SessionId == "child").InputTokens);
        Assert.Equal(100, Assert.Single(entries, entry => entry.SessionId == "root").InputTokens);
    }
}
