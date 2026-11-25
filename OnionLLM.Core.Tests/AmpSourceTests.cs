using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Tests;

public class AmpSourceTests
{
    private static long Ms(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    // Fikstury wzorowane na testach adaptera amp w ccusage.
    private const string LedgerThread = """
        {"id":"T-ledger","usageLedger":{"events":[
        {"id":"e1","timestamp":"2026-01-30T23:30:00.000Z","model":"claude-sonnet-4-20250514","tokens":{"input":1200,"output":340},"toMessageId":1,"credits":1.25},
        {"id":"e2","timestamp":"2026-01-31T10:00:00+09:00","model":"gpt-5","tokens":{"total":1500},"toMessageId":2},
        {"id":"e3","timestamp":"2026-01-31T11:00:00Z","tokens":{"input":5,"output":5}}
        ]},"messages":[
        {"role":"user","content":"hi"},
        {"role":"assistant","messageId":1,"usage":{"cacheCreationInputTokens":500,"cacheReadInputTokens":7000}},
        {"role":"assistant","messageId":2,"usage":{"cacheReadInputTokens":12000}}
        ]}
        """;

    private const string MessagesThread = """
        {"id":"T-messages","messages":[
        {"role":"assistant","messageId":10,"timestamp":"2026-01-31T11:42:10.652Z","model":"gpt-5","usage":{"inputTokens":10,"outputTokens":178,"cacheCreationInputTokens":986,"cacheReadInputTokens":11372,"credits":0.75}},
        {"role":"assistant","messageId":"m11","usage":{"model":"claude-haiku-4-5-20251001","inputTokens":3,"outputTokens":4,"timestamp":"2026-02-01T23:59:59Z"}},
        {"role":"user","usage":{"inputTokens":99}}
        ]}
        """;

    [Fact]
    public void Ledger_events_take_cache_breakdown_from_target_message()
    {
        using var directory = new TempDirectory();
        directory.Write("threads/T-ledger.json", LedgerThread);
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new AmpSource(), TestData.ConfigWith("amp", directory.Path), diagnostics);

        Assert.Equal(2, entries.Count);
        var first = entries[0];
        Assert.Equal("T-ledger", first.SessionId);
        Assert.Equal("claude-sonnet-4-20250514", first.Model);
        Assert.Equal(1200, first.InputTokens);
        Assert.Equal(340, first.OutputTokens);
        Assert.Equal(500, first.CacheCreationTokens);
        Assert.Equal(7000, first.CacheReadTokens);
        Assert.Equal(1.25, first.Credits);
        Assert.Equal(Ms("2026-01-30T23:30:00Z"), first.TimestampMs);

        // Zdarzenie z samym „total” liczy się (cache z wiadomości 2), a strefa +09:00 jest uwzględniona.
        var second = entries[1];
        Assert.Equal(0, second.InputTokens);
        Assert.Equal(12000, second.CacheReadTokens);
        Assert.Equal(Ms("2026-01-31T01:00:00Z"), second.TimestampMs);

        // e3 bez modelu jest pomijane z diagnostyką.
        Assert.Equal(1, diagnostics.SkippedLines);
    }

    [Fact]
    public void Message_usage_is_used_when_ledger_is_missing()
    {
        using var directory = new TempDirectory();
        directory.Write("threads/T-messages.json", MessagesThread);

        var entries = TestData.ParseAll(new AmpSource(), TestData.ConfigWith("amp", directory.Path));

        Assert.Equal(2, entries.Count);
        Assert.Equal("gpt-5", entries[0].Model);
        Assert.Equal(10, entries[0].InputTokens);
        Assert.Equal(178, entries[0].OutputTokens);
        Assert.Equal(986, entries[0].CacheCreationTokens);
        Assert.Equal(11372, entries[0].CacheReadTokens);
        Assert.Equal(0.75, entries[0].Credits);
        Assert.Equal("claude-haiku-4-5-20251001", entries[1].Model);
        Assert.Equal(Ms("2026-02-01T23:59:59Z"), entries[1].TimestampMs);
    }

    [Fact]
    public void Truncated_or_anonymous_threads_are_skipped()
    {
        using var directory = new TempDirectory();
        directory.Write("threads/T-truncated.json", """{"id":"T-truncated","usageLedger":{"events":[{"id":"e1","timest""");
        directory.Write("threads/T-noid.json", MessagesThread.Replace("\"id\":\"T-messages\",", ""));
        directory.Write("threads/T-ok.json", MessagesThread);
        var diagnostics = new ParseDiagnostics();

        var entries = TestData.ParseAll(new AmpSource(), TestData.ConfigWith("amp", directory.Path), diagnostics);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("T-messages", entry.SessionId));
        Assert.Equal(2, diagnostics.SkippedLines);
    }
}
