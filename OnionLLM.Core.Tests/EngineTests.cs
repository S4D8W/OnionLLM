using OnionLLM.Core.Config;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class EngineTests
{
    [Fact]
    public async Task Dashboard_summarizes_today_week_month_sessions_and_active_block()
    {
        using var directory = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        string Ts(TimeSpan ago) => now.Subtract(ago).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        directory.Write("claude/projects/demo/a.jsonl", string.Join('\n',
            TestData.ClaudeLine(Ts(TimeSpan.FromMinutes(30)), "m1", "r1", 100, 1000, 2000, 50000, model: "claude-sonnet-4-5", session: "active"),
            TestData.ClaudeLine(Ts(TimeSpan.FromMinutes(5)), "m2", "r2", 100, 1000, 0, 50000, model: "claude-sonnet-4-5", session: "active"),
            TestData.ClaudeLine(Ts(TimeSpan.FromDays(3)), "m3", "r3", 100, 1000, 0, 0, model: "claude-sonnet-4-5", session: "old"),
            TestData.ClaudeLine(Ts(TimeSpan.FromDays(20)), "m4", "r4", 100, 1000, 0, 0, model: "claude-sonnet-4-5", session: "older")));

        var config = new OnionConfig { Timezone = "UTC", CostMode = CostMode.Calculate, Offline = true };
        config.Sources["claude"] = new SourceSettings { Paths = [Path.Combine(directory.Path, "claude")] };
        config.Sources["codex"] = new SourceSettings { Paths = [Path.Combine(directory.Path, "missing-codex")] };
        config.Sources["copilot"] = new SourceSettings { Enabled = false };

        using var engine = new UsageEngine(config, new UsageLedger(Path.Combine(directory.Path, "ledger.db")),
            [new ClaudeSource(), new CodexSource(), new CopilotSource()]);
        await engine.SyncAsync(force: true);
        var dashboard = engine.BuildDashboard(now: now);

        var claude = dashboard.Sources.Single(source => source.Id == "claude");
        Assert.True(claude.IsInstalled);
        Assert.Equal(2, claude.Today.Requests);
        Assert.Equal(1, claude.ActiveSessions);
        Assert.Equal(3, claude.Last7Days.Requests);
        Assert.Equal(4, claude.Last30Days.Requests);
        Assert.True(claude.Today.CostUsd > 0);
        Assert.Contains("claude-sonnet-4-5", claude.TopModels);

        var copilot = dashboard.Sources.Single(source => source.Id == "copilot");
        Assert.False(copilot.IsEnabled);

        Assert.NotNull(dashboard.ActiveBlock);
        Assert.Equal(2, dashboard.ActiveBlock!.EntryCount);
        Assert.Equal(dashboard.Today.TotalTokens, claude.Today.TotalTokens);

        var report = engine.BuildReport(ReportPeriod.Monthly, new ReportQuery());
        Assert.Equal(4, report.Rows.Sum(row => row.Totals.Requests));
    }
}
