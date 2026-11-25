using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

/// <summary>Faza 11.6.4: dostawca trasy dsh, kandydaci cennika i reguła wyboru duplikatów.</summary>
public class DshSourcePricingTests
{
    private const string Header = """
        {"type":"session","id":"duplicate","createdAt":1785015039278}
        {"type":"request/header","data":{"header":{"config":{"model":"sample"}}}}
        """;

    private static List<Models.UsageEntry> Load(params TempDirectory[] roots)
    {
        var config = TestData.ConfigWith(DshSource.SourceId, roots[0].Path);
        config.Sources[DshSource.SourceId].Paths.AddRange(roots.Skip(1).Select(root => root.Path));
        return TestData.ParseAll(new DshSource(), config);
    }

    [Fact]
    public void Route_provider_is_recorded_and_deepseek_accounts_bill_directly()
    {
        using var directory = new TempDirectory();
        directory.Write("sessions/p/sid/session.v4.jsonl", """
            {"type":"session","version":4,"id":"sid","createdAt":1785015039278,"cwd":"/home/u/proj","isSeeded":false}
            {"type":"request/header","seq":3,"time":1785015040000,"data":{"header":{"config":{"provider":"deepseek-official","model":"deepseek-v4-pro"},"tools":[]}}}
            {"type":"assistant/message","seq":5,"time":1785015045000,"data":{"usage":{"inputTokens":1200,"outputTokens":300,"cacheReadTokens":8000,"totalTokens":9500}}}
            {"type":"request/context","seq":6,"time":1785015046000,"data":{"provider":"openrouter","model":"anthropic/claude-sonnet-4.5"}}
            {"type":"assistant/message","seq":7,"time":1785015047000,"data":{"usage":{"inputTokens":10,"outputTokens":5,"cacheWriteTokens":20}}}
            {"type":"request/context","seq":8,"time":1785015048000,"data":{"model":"deepseek-flash"}}
            {"type":"assistant/message","seq":9,"time":1785015049000,"data":{"usage":{"inputTokens":10,"outputTokens":5}}}
            {"type":"compaction/summary","seq":10,"time":1785015050000,"data":{"provider":"deepseek_account","model":"deepseek-flash","usage":{"inputTokens":40,"outputTokens":4}}}
            """);

        var entries = Load(directory);

        Assert.Equal(["deepseek", "openrouter", null, "deepseek"], entries.Select(entry => entry.Provider));
        Assert.Equal("anthropic/claude-sonnet-4.5", entries[1].Model);
    }

    [Fact]
    public void Billing_provider_folds_deepseek_accounts()
    {
        foreach (var logged in new[] { "deepseek", "DeepSeek", "deepseek-official", "deepseek_account", "deepseek-account" })
        {
            Assert.Equal(DshSource.DeepSeekProvider, DshSource.BillingProvider(logged));
        }

        Assert.Equal("openrouter", DshSource.BillingProvider("openrouter"));
        Assert.Equal("vertex_ai", DshSource.BillingProvider("google-vertex"));
        Assert.Null(DshSource.BillingProvider("unknown"));
        Assert.Null(DshSource.BillingProvider(null));
    }

    [Fact]
    public void Candidates_are_ordered_by_route()
    {
        // ccusage dsh/src/parser.rs: orders_pricing_candidates_by_route, adapter_pricing_identity
        Assert.Equal(["deepseek-v4-pro", "deepseek/deepseek-v4-pro"], DshSource.ModelCandidates("deepseek", "deepseek-v4-pro"));
        Assert.Equal(["deepseek.v4.flash", "deepseek/deepseek.v4.flash"], DshSource.ModelCandidates("deepseek", "deepseek.v4.flash"));
        Assert.Equal(["openrouter/deepseek/deepseek-v4-flash", "deepseek/deepseek-v4-flash"],
            DshSource.ModelCandidates("openrouter", "deepseek/deepseek-v4-flash"));
        Assert.Equal(["openai/gpt-5.2", "gpt-5.2"], DshSource.ModelCandidates("openai", "gpt-5.2"));
        Assert.Equal(["deepseek-flash"], DshSource.ModelCandidates(null, "deepseek-flash"));
    }

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-flash")]
    public void Default_deepseek_models_are_priced_on_the_direct_route(string model)
    {
        // ccusage: prices_default_deepseek_models_offline
        var entry = new Models.UsageEntry
        {
            Source = DshSource.SourceId,
            TimestampMs = 1_785_015_045_000,
            Model = model,
            Provider = DshSource.DeepSeekProvider,
            InputTokens = 1_000_000
        };

        var result = new CostCalculator(PricingCatalog.Embedded(), CostMode.Calculate).Calculate(entry);

        Assert.False(result.IsUnpriced);
        Assert.True(result.CostUsd > 0);
    }

    [Fact]
    public void Duplicate_sequences_prefer_billable_total_then_output_then_the_earlier_copy()
    {
        // ccusage dsh/src/loader.rs: duplicate_dsh_sequences_prefer_billable_total_then_output_in_either_order
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        first.Write("sessions/p/duplicate/session.v4.jsonl", Header + """

            {"type":"assistant/message","seq":1,"time":1785015045001,"data":{"usage":{"inputTokens":1000,"outputTokens":5}}}
            {"type":"assistant/message","seq":2,"time":1785015045002,"data":{"usage":{"inputTokens":100,"outputTokens":5}}}
            {"type":"assistant/message","seq":3,"time":1785015045003,"data":{"usage":{"inputTokens":10,"outputTokens":5}}}
            {"type":"assistant/message","seq":4,"time":1785015045004,"data":{"usage":{"inputTokens":100,"outputTokens":5}}}
            """);
        second.Write("sessions/p/duplicate/session.v4.jsonl", Header + """

            {"type":"assistant/message","seq":1,"time":1785015045001,"data":{"usage":{"inputTokens":0,"outputTokens":6}}}
            {"type":"assistant/message","seq":2,"time":1785015045002,"data":{"usage":{"inputTokens":200,"outputTokens":5}}}
            {"type":"assistant/message","seq":3,"time":1785015045003,"data":{"usage":{"inputTokens":9,"outputTokens":6}}}
            {"type":"assistant/message","seq":4,"time":1785015045005,"data":{"usage":{"inputTokens":100,"outputTokens":5}}}
            """);

        foreach (var roots in new[] { new[] { first, second }, new[] { second, first } })
        {
            var rows = Deduplicator.Apply(Load(roots))
                .OrderBy(entry => entry.TimestampMs)
                .Select(entry => (entry.TimestampMs, entry.InputTokens, entry.OutputTokens))
                .ToList();

            Assert.Equal(
            [
                (1785015045001L, 1000L, 5L),
                (1785015045002L, 200L, 5L),
                (1785015045003L, 9L, 6L),
                (1785015045004L, 100L, 5L)
            ], rows);
        }
    }

    [Fact]
    public void Text_seq_is_ignored_like_ccusage_and_the_content_key_is_used()
    {
        // ccusage lenient_i64 (serde_json as_i64): "seq":"5" nie jest numerem sekwencji.
        using var directory = new TempDirectory();
        directory.Write("sessions/p/duplicate/session.v4.jsonl", Header + """

            {"type":"assistant/message","seq":"5","time":1785015045001,"data":{"usage":{"inputTokens":10,"outputTokens":5}}}
            {"type":"assistant/message","seq":5,"time":1785015045002,"data":{"usage":{"inputTokens":20,"outputTokens":5}}}
            """);

        var entries = Load(directory);

        Assert.Equal(2, entries.Count);
        Assert.Equal("duplicate:1785015045001:sample:10:5:0:0", entries[0].DedupeKey);
        Assert.Equal("duplicate:5", entries[1].DedupeKey);
    }
}
