using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class ClaudeSourceTests
{
    [Fact]
    public void Duplicate_lines_of_one_response_are_counted_once()
    {
        using var directory = new TempDirectory();
        // Claude Code zapisuje jedną odpowiedź w kilku liniach (po jednej na blok treści) z tym samym usage.
        var line = TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_1", "req_1", 10, 500, 1000, 20000);
        directory.Write("projects/demo/session-a.jsonl", string.Join('\n', line, line, line));

        var entries = Deduplicator.Apply(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)));

        var entry = Assert.Single(entries);
        Assert.Equal(10, entry.InputTokens);
        Assert.Equal(500, entry.OutputTokens);
        Assert.Equal(1000, entry.CacheCreationTokens);
        Assert.Equal(20000, entry.CacheReadTokens);
        Assert.Equal("demo", entry.Project);
        Assert.Equal(@"C:\work\demo", entry.ProjectPath);
    }

    [Fact]
    public void Copies_across_transcripts_are_deduplicated_and_fast_record_wins()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_1", "req_1", 10, 5, session: "a"));
        directory.Write("projects/demo/b.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_1", "req_1", 10, 5, session: "b", speed: "fast"));

        var entries = Deduplicator.Apply(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)));

        Assert.Equal(SpeedTier.Fast, Assert.Single(entries).Speed);
    }

    [Fact]
    public void Missing_request_id_keeps_distinct_timestamps_separate()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", string.Join('\n',
            TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_1", null, 1, 1),
            TestData.ClaudeLine("2026-11-24T10:05:00Z", "msg_1", null, 1, 1)));

        var entries = Deduplicator.Apply(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)));

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Synthetic_messages_are_kept_without_model_and_invalid_lines_are_skipped()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", string.Join('\n',
            TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_1", "r", 0, 0, model: "<synthetic>"),
            "{\"usage\": broken json",
            "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
            TestData.ClaudeLine("2026-11-24T10:00:00Z", "msg_2", "r2", 3, 4)));
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path), diagnostics);

        // ccusage daily.rs:171-173 — <synthetic> zostaje, tylko bez nazwy modelu do wyświetlenia (11.2.8).
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].Model);
        Assert.StartsWith("msg_2\u001Fr2\u001F", entries[1].DedupeKey);
        Assert.Equal("a", entries[1].SessionId);
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Xdg_config_home_replaces_dot_config_in_default_roots()
    {
        var previousXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var previousClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        using var directory = new TempDirectory();
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory.Path);

            var roots = new ClaudeSource().ResolveRoots(new Core.Config.OnionConfig());

            Assert.Contains(Path.Combine(directory.Path, "claude"), roots, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine(Util.FileUtil.Home, ".claude"), roots, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine(Util.FileUtil.Home, ".config", "claude"), roots, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousXdg);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaude);
        }
    }

    [Fact]
    public void Invalid_claude_config_dir_is_reported_as_a_sync_problem()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        using var directory = new TempDirectory();
        directory.Write("valid/projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m", "r", 1, 1));
        var missing = Path.Combine(directory.Path, "missing");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(directory.Path, "valid") + "," + missing);
            var source = new ClaudeSource();
            var config = new Core.Config.OnionConfig();

            var problem = Assert.Single(source.ConfigurationProblems(config));
            Assert.Contains(missing, problem);

            var report = new UsageLedger(Path.Combine(directory.Path, "ledger.db")).Sync([source], config);
            var sourceReport = Assert.Single(report.Sources);
            Assert.Contains(sourceReport.Problems, item => item.Error.Contains("CLAUDE_CONFIG_DIR") && item.Error.Contains(missing));
            Assert.Equal(1, sourceReport.ParsedFiles);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    [Fact]
    public void Linked_project_directories_are_followed_once()
    {
        using var directory = new TempDirectory();
        directory.Write("elsewhere/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m", "r", 1, 1));
        directory.Write("projects/local/b.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m2", "r2", 1, 1));
        var link = Path.Combine(directory.Path, "projects", "demo");
        var loop = Path.Combine(directory.Path, "projects", "local", "loop");
        if (!TryLinkDirectory(link, Path.Combine(directory.Path, "elsewhere", "demo")) ||
            !TryLinkDirectory(loop, Path.Combine(directory.Path, "projects")))
        {
            return; // Brak uprawnień do tworzenia dowiązań na tej maszynie.
        }

        var files = new ClaudeSource().EnumerateFiles(TestData.ConfigWith("claude", directory.Path)).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Path == Path.Combine(link, "a.jsonl"));
        var entry = Assert.Single(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)), item => item.InputTokens == 1 && item.Project == "demo");
        Assert.Equal("a", entry.SessionId);
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Junction nie wymaga uprawnień administratora.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void One_hour_cache_writes_are_split_out()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m", "r", 1, 1, cacheWrite: 300, cache1h: 200));

        var entry = Assert.Single(TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path)));

        Assert.Equal(300, entry.CacheCreationTokens);
        Assert.Equal(200, entry.CacheCreation1hTokens);
    }

    [Fact]
    public void Config_directory_or_projects_directory_both_work_as_root()
    {
        using var directory = new TempDirectory();
        directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m", "r", 1, 1));

        var fromConfigDir = TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", directory.Path));
        var fromProjectsDir = TestData.ParseAll(new ClaudeSource(), TestData.ConfigWith("claude", Path.Combine(directory.Path, "projects")));

        Assert.Single(fromConfigDir);
        Assert.Single(fromProjectsDir);
    }
}
