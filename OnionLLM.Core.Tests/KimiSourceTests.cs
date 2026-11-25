using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class KimiSourceTests
{
    private static List<Models.UsageEntry> Load(TempDirectory directory, Sources.ParseDiagnostics? diagnostics = null) =>
        TestData.ParseAll(new KimiSource(), TestData.ConfigWith(KimiSource.SourceId, directory.Path), diagnostics);

    [Fact]
    public void Status_update_tokens_are_mapped_with_config_model()
    {
        using var directory = new TempDirectory();
        directory.Write("config.json", """{"model":"kimi-k2"}""");
        directory.Write("sessions/group/session-a/wire.jsonl", string.Join('\n',
            """{"type":"metadata","protocol_version":"1.3"}""",
            """{"timestamp":1770983426.420942,"message":{"type":"TurnBegin","payload":{"user_input":"hello"}}}""",
            """{"timestamp":1770983427.123,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":100,"output":50,"input_cache_read":10,"input_cache_creation":20},"message_id":"msg-1"}}}"""));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal("kimi-k2", entry.Model);
        Assert.Equal(1770983427123, entry.TimestampMs);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(50, entry.OutputTokens);
        Assert.Equal(20, entry.CacheCreationTokens);
        Assert.Equal(10, entry.CacheReadTokens);
    }

    [Fact]
    public void Kimi_code_usage_records_count_only_turn_scope()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/workspace/session-b/agents/agent-1/wire.jsonl", string.Join('\n',
            """{"type":"usage.record","model":"kimi-code/kimi-for-coding","usage":{"inputOther":3064,"output":76,"inputCacheRead":14848,"inputCacheCreation":0},"usageScope":"turn","time":1782113184943}""",
            """{"type":"usage.record","model":"kimi-code/kimi-for-coding","usage":{"inputOther":5000,"output":200,"inputCacheRead":20000,"inputCacheCreation":100},"usageScope":"session","time":1782113185000}"""));

        var entry = Assert.Single(Load(directory));

        Assert.Equal("session-b", entry.SessionId);
        Assert.Equal("kimi-for-coding", entry.Model);
        Assert.Equal(1782113184943, entry.TimestampMs);
        Assert.Equal(3064, entry.InputTokens);
        Assert.Equal(76, entry.OutputTokens);
        Assert.Equal(14848, entry.CacheReadTokens);
    }

    [Fact]
    public void Default_model_is_used_without_config_and_wrong_depths_are_ignored()
    {
        using var directory = new TempDirectory();
        const string line = """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":7,"output":3}}}}""";
        directory.Write("sessions/group/session/wire.jsonl", line);
        directory.Write("sessions/wire.jsonl", line);
        directory.Write("sessions/nested/path/session/wire.jsonl", line);
        directory.Write("sessions/group/session/other.jsonl", line);

        var entry = Assert.Single(Load(directory));

        Assert.Equal(KimiSource.DefaultModel, entry.Model);
    }

    [Fact]
    public void Duplicate_reports_keep_the_copy_with_recorded_output()
    {
        using var directory = new TempDirectory();
        const string partial = """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"message_id":"dup","token_usage":{"input_other":100,"total":150}}}}""";
        const string recorded = """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"message_id":"dup","token_usage":{"input_other":100,"output":50,"total":150}}}}""";
        directory.Write("sessions/group/session-a/wire.jsonl", string.Join('\n', partial, recorded));

        var entries = Deduplicator.Apply(Load(directory));

        Assert.Equal(50, Assert.Single(entries).OutputTokens);
    }

    [Fact]
    public void Malformed_and_zero_token_lines_are_skipped()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/group/session-a/wire.jsonl", string.Join('\n',
            "{\"token_usage\": not json",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":0,"output":0}}}}""",
            """{"type":"usage.record","model":"kimi-for-coding","usage":"SECRET","usageScope":"turn"}""",
            """{"type":"usage.record","model":"kimi-for-coding","usage":{"inputOther":100,"output":20},"usageScope":"turn","time":1770983427000}"""));
        var diagnostics = new Sources.ParseDiagnostics();

        var entry = Assert.Single(Load(directory, diagnostics));

        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(2, diagnostics.SkippedLines);
    }

    [Fact]
    public void Logged_kimi_code_model_is_the_first_pricing_candidate()
    {
        // ccusage kimi/src/parser.rs: prices_the_logged_wire_id_before_stripping_its_prefix, adapter_pricing_identity.
        using var directory = new TempDirectory();
        directory.Write("sessions/workspace/session-b/agents/agent-1/wire.jsonl", string.Join('\n',
            """{"type":"usage.record","model":"kimi-code/test-model","usage":{"inputOther":100},"usageScope":"turn","time":1794909600000}""",
            """{"type":"usage.record","model":"kimi-for-coding","usage":{"inputOther":1000},"usageScope":"turn","time":1794909600001}"""));
        var source = new KimiSource();

        var entries = Load(directory).OrderBy(entry => entry.TimestampMs).ToList();

        Assert.Equal("test-model", entries[0].Model);
        Assert.Equal(["kimi-code/test-model", "moonshot/test-model", "kimi/test-model", "test-model"], source.PricingCandidates(entries[0]));
        Assert.Equal(["moonshot/kimi-for-coding", "kimi/kimi-for-coding", "kimi-for-coding"], source.PricingCandidates(entries[1]));
    }
}
