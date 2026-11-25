using OnionLLM.Core.Config;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class CrossSourceTests
{
    private static string ClaudeAgentMessage(string id, string nativeSession, long input) =>
        $"{{\"id\":\"{id}\",\"sessionID\":\"oc-session\",\"role\":\"assistant\",\"modelID\":\"claude-opus-4-6\",\"providerID\":\"claude-agent\"," +
        $"\"time\":{{\"created\":1777000000000}},\"content\":[{{\"type\":\"text\",\"text\":\"hi\",\"native\":{{\"sessionID\":\"{nativeSession}\",\"messageID\":\"m\"}}}}]," +
        $"\"tokens\":{{\"input\":{input},\"output\":50,\"reasoning\":0,\"cache\":{{\"read\":0,\"write\":0}}}},\"cost\":0}}";

    [Fact]
    public async Task OpenCode_claude_agent_turns_are_skipped_when_claude_counts_the_session()
    {
        using var directory = new TempDirectory();
        directory.Write("claude/projects/demo/claude-session.jsonl",
            TestData.ClaudeLine("2026-04-24T03:06:40Z", "msg-1", "req-1", 100, 50, session: "claude-session"));
        directory.Write("opencode/storage/message/oc-session/msg_a.json", ClaudeAgentMessage("msg_a", "claude-session", 100));
        directory.Write("opencode/storage/message/oc-session/msg_b.json", ClaudeAgentMessage("msg_b", "unknown-claude-session", 7));

        var config = new OnionConfig { Timezone = "UTC", Offline = true };
        config.Sources["claude"] = new SourceSettings { Paths = [Path.Combine(directory.Path, "claude")] };
        config.Sources["opencode"] = new SourceSettings { Paths = [Path.Combine(directory.Path, "opencode")] };
        using var engine = new UsageEngine(config, new UsageLedger(Path.Combine(directory.Path, "ledger.db")),
            [new ClaudeSource(), new OpenCodeSource()]);
        await engine.SyncAsync(force: true);

        var entries = engine.LoadEntries();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Source == "claude");
        var kept = Assert.Single(entries, entry => entry.Source == "opencode");
        Assert.Equal(7, kept.InputTokens);
        Assert.Equal("claude:unknown-claude-session", kept.MirrorOf);
    }
}
