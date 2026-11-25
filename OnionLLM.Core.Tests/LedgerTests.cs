using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class LedgerTests
{
    [Fact]
    public void Changed_file_is_reparsed_and_deleted_file_keeps_history()
    {
        using var directory = new TempDirectory();
        var file = directory.Write("projects/demo/a.jsonl", TestData.ClaudeLine("2026-11-24T10:00:00Z", "m1", "r1", 1, 1));
        var config = TestData.ConfigWith("claude", directory.Path);
        config.Sources["codex"] = new SourceSettings { Enabled = false };
        config.Sources["copilot"] = new SourceSettings { Enabled = false };
        var ledger = new UsageLedger(Path.Combine(directory.Path, "ledger.db"));
        IUsageSource[] sources = [new ClaudeSource()];

        ledger.Sync(sources, config);
        Assert.Single(ledger.Query());

        File.AppendAllText(file, "\n" + TestData.ClaudeLine("2026-11-24T10:05:00Z", "m2", "r2", 1, 1));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        var report = ledger.Sync(sources, config);
        Assert.Equal(1, report.Sources[0].ParsedFiles);
        Assert.Equal(2, ledger.Query().Count);

        var unchanged = ledger.Sync(sources, config);
        Assert.Equal(0, unchanged.Sources[0].ParsedFiles);

        File.Delete(file);
        ledger.Sync(sources, config);
        Assert.Equal(2, ledger.Query().Count);
    }
}
