using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

public class ClineSourceTests
{
    // Fikstury wzorowane na rust/adapters/cline/tests/fixtures w ccusage.
    private const string ExtensionMessages = """
        [
          {"ts":1790856000000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"claude-sonnet-4-20250514","providerId":"anthropic","mode":"act"},
           "text":"{\"tokensIn\":100,\"tokensOut\":20,\"cacheReads\":5,\"cacheWrites\":10,\"cost\":0.001}"},
          {"ts":1793491140000,"type":"say","say":"api_req_started","text":"{\"tokensIn\":200,\"tokensOut\":40,\"cacheReads\":20,\"cacheWrites\":30,\"cost\":0.002}"},
          {"ts":1793491170000,"type":"say","say":"api_req_started","text":"{\"request\":\"waiting for response\"}"},
          {"ts":1793491200000,"type":"say","say":"api_req_started","partial":true,"modelInfo":{"modelId":"claude-sonnet-4-20250514"},
           "text":"{\"tokensIn\":500,\"tokensOut\":50,\"cacheReads\":10,\"cacheWrites\":10,\"cost\":0.004}"},
          {"ts":1793491230000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"UNKNOWN"},
           "text":"{\"tokensIn\":500,\"tokensOut\":50,\"cacheReads\":10,\"cacheWrites\":10,\"cost\":0.004}"},
          {"ts":1793491240000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"vendor/custom"},
           "text":"{\"tokensIn\":\"1\",\"tokensOut\":1,\"cacheReads\":0,\"cacheWrites\":0}"},
          {"ts":1793491250000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"vendor/custom"},
           "text":"{\"tokensIn\":0,\"tokensOut\":0,\"cacheReads\":0,\"cacheWrites\":0,\"cost\":0.5}"},
          {"type":"say","say":"api_req_started","modelInfo":{"modelId":"vendor/custom"},
           "text":"{\"tokensIn\":1,\"tokensOut\":0,\"cacheReads\":0,\"cacheWrites\":0}"},
          {"ts":1793491560000,"type":"say","say":"subagent_usage","modelInfo":{"modelId":"claude-sonnet-4-20250514"},
           "text":"{\"tokensIn\":25,\"tokensOut\":10,\"cacheReads\":5,\"cacheWrites\":2}"},
          {"ts":1793491620000,"type":"say","say":"deleted_api_reqs","modelInfo":{"modelId":"claude-sonnet-4-20250514"},
           "text":"{\"tokensIn\":15,\"tokensOut\":5,\"cacheReads\":1,\"cacheWrites\":2,\"cost\":0.01}"}
        ]
        """;

    private const string CliSession = """
        {"version":1,"sessionId":"session-a","messages":[
          {"role":"user","timestamp":"2026-11-01T00:00:00Z","content":"hello"},
          {"role":"assistant","ts":1793491260000,"id":"assistant-1","modelInfo":{"id":"claude-sonnet-4-20250514","provider":"anthropic"},
           "metrics":{"inputTokens":100,"outputTokens":15,"cacheReadTokens":25,"cacheWriteTokens":10,"cost":0}},
          {"role":"assistant","timestamp":"2026-11-01T00:02:00Z","id":"assistant-2","modelInfo":{"id":"claude-sonnet-4-20250514"},
           "metrics":{"inputTokens":120,"outputTokens":16,"cacheReadTokens":20,"cacheWriteTokens":10,"cost":0.0002}},
          {"role":"assistant","ts":1793491320000,"modelInfo":{"id":"claude-sonnet-4-20250514"},"metrics":{"inputTokens":10,"outputTokens":2,"cacheWriteTokens":1}},
          {"role":"assistant","ts":1793491380000,"modelInfo":{"id":"unknown"},"metrics":{"inputTokens":10,"outputTokens":2,"cacheReadTokens":1,"cacheWriteTokens":1}},
          {"role":"assistant","ts":1793491440000,"modelInfo":{"id":"claude-sonnet-4-20250514"},"metrics":{"inputTokens":5,"outputTokens":2,"cacheReadTokens":4,"cacheWriteTokens":2}},
          {"role":"assistant","ts":1793491500000,"modelInfo":{"id":"claude-sonnet-4-20250514"},"metrics":{"inputTokens":18446744073709551615,"outputTokens":2,"cacheReadTokens":1,"cacheWriteTokens":1}},
          {"role":"assistant","ts":"bad","timestamp":"2026-11-01T00:00:00Z","modelInfo":{"id":"claude-sonnet-4-20250514"},"metrics":{"inputTokens":3,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}}
        ]}
        """;

    [Fact]
    public void Extension_tasks_count_only_complete_api_requests()
    {
        using var directory = new TempDirectory();
        directory.Write("tasks/task-a/ui_messages.json", ExtensionMessages);
        directory.Write("tasks/task-a/task_metadata.json", """{"model_usage":[{"ts":1,"model_id":"x"}]}""");
        directory.Write("tasks/nested/task-b/ui_messages.json", ExtensionMessages);
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new ClineSource(), TestData.ConfigWith("cline", directory.Path), diagnostics);

        var entry = Assert.Single(entries);
        Assert.Equal("task:task-a", entry.SessionId);
        Assert.Equal("claude-sonnet-4-20250514", entry.Model);
        Assert.Equal(1790856000000, entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);   // tokensIn rozszerzenia nie zawiera cache
        Assert.Equal(20, entry.OutputTokens);
        Assert.Equal(10, entry.CacheCreationTokens);
        Assert.Equal(5, entry.CacheReadTokens);
        Assert.Equal(0.001, entry.RecordedCostUsd);
        Assert.Equal(1, diagnostics.SkippedLines); // rekord bez ts
    }

    [Fact]
    public void Cli_input_has_cache_subtracted_and_invalid_records_are_dropped()
    {
        using var directory = new TempDirectory();
        directory.Write("data/sessions/session-a/session-a.messages.json", CliSession);
        directory.Write("data/sessions/session-b/other.messages.json", CliSession);
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new ClineSource(), TestData.ConfigWith("cline", directory.Path), diagnostics);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("cli:session-a", entry.SessionId));
        Assert.Equal(65, entries[0].InputTokens);
        Assert.Equal(15, entries[0].OutputTokens);
        Assert.Equal(25, entries[0].CacheReadTokens);
        Assert.Equal(10, entries[0].CacheCreationTokens);
        Assert.Null(entries[0].RecordedCostUsd); // zapisane 0 = brak kosztu
        Assert.Equal(90, entries[1].InputTokens);
        Assert.Equal(0.0002, entries[1].RecordedCostUsd);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T00:02:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            entries[1].TimestampMs);
        Assert.Equal(1, diagnostics.SkippedLines); // niepoprawne ts nie jest zastępowane polem timestamp
    }

    [Fact]
    public void All_layouts_are_discovered_and_files_reachable_from_two_roots_are_read_once()
    {
        using var directory = new TempDirectory();
        var line = """[{"ts":1793491140000,"type":"say","say":"api_req_started","modelInfo":{"modelId":"m"},"text":"{\"tokensIn\":1,\"tokensOut\":1,\"cacheReads\":0,\"cacheWrites\":0}"}]""";
        var cli = """{"messages":[{"role":"assistant","ts":1793491140000,"modelInfo":{"id":"m"},"metrics":{"inputTokens":3,"outputTokens":1,"cacheReadTokens":1,"cacheWriteTokens":1}}]}""";
        directory.Write("tasks/ext/ui_messages.json", line);
        directory.Write("data/tasks/legacy/ui_messages.json", line);
        directory.Write("data/sessions/s1/s1.messages.json", cli);
        directory.Write("sessions/s2/s2.messages.json", cli);

        var config = TestData.ConfigWith("cline", directory.Path);
        config.Sources["cline"].Paths = [directory.Path, Path.Combine(directory.Path, "data")];
        var entries = TestData.ParseAll(new ClineSource(), config);

        Assert.Equal(["cli:s1", "cli:s2", "task:ext", "task:legacy"], entries.Select(entry => entry.SessionId).Order().ToArray());
    }
}
