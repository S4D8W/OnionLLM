using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;

namespace OnionLLM.Core.Tests;

public class PricingTests
{
    private static readonly PricingCatalog Catalog = PricingCatalog.Embedded();

    [Theory]
    [InlineData("claude-sonnet-4-5-20991231", "claude-sonnet-4-5")]
    [InlineData("anthropic/claude-opus-4-6", "claude-opus-4-6")]
    [InlineData("claude-sonnet-4.6", "claude-sonnet-4-6")]
    [InlineData("gpt-5-codex", "gpt-5-codex")]
    public void Resolves_model_name_variants(string model, string expectedKey)
    {
        var pricing = Catalog.Resolve(model);

        Assert.NotNull(pricing);
        Assert.Equal(expectedKey, pricing.Key, ignoreCase: true);
    }

    [Fact]
    public void Unknown_model_is_unpriced()
    {
        var calculator = new CostCalculator(Catalog, CostMode.Calculate);
        var result = calculator.Calculate(TestData.Entry("2026-11-24T10:00:00Z", 1000, 1000, model: "totally-unknown-model"));

        Assert.True(result.IsUnpriced);
        Assert.Equal(0, result.CostUsd);
    }

    [Fact]
    public void Cost_uses_all_token_buckets()
    {
        var pricing = new ModelPricing
        {
            Key = "claude-test",
            Base = new RateSet { Input = 3e-6, Output = 15e-6, CacheWrite = 3.75e-6, CacheWrite1h = 6e-6, CacheRead = 0.3e-6 }
        };
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 1_000, 2_000, cacheWrite: 10_000, cacheRead: 100_000) with { CacheCreation1hTokens = 4_000 };

        var cost = CostCalculator.Price(entry, pricing);

        // 1000*3 + 2000*15 + 6000*3.75 + 4000*6 + 100000*0.3 (µ$)
        Assert.Equal((3_000 + 30_000 + 22_500 + 24_000 + 30_000) / 1e6, cost, 9);
    }

    [Fact]
    public void Long_context_prices_whole_request_at_tier_rates()
    {
        var pricing = new ModelPricing
        {
            Key = "gemini-test",
            Base = new RateSet { Input = 1e-6, Output = 10e-6, CacheRead = 0.1e-6 },
            Tiers = [new PricingTier(200_000, new RateSet { Input = 2e-6, Output = 15e-6, CacheRead = 0.2e-6 })]
        };

        var small = CostCalculator.Price(TestData.Entry("2026-11-24T10:00:00Z", 100_000, 1_000), pricing);
        var large = CostCalculator.Price(TestData.Entry("2026-11-24T10:00:00Z", 150_000, 1_000, cacheRead: 100_000), pricing);

        Assert.Equal(0.1 + 0.01, small, 9);
        Assert.Equal(0.3 + 0.015 + 0.02, large, 9);
    }

    [Theory]
    // Starsze modele Claude, których nie ma już w LiteLLM — ceny wbudowane (ccusage builtin_pricing.rs + models.dev).
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4", 3, 15)]
    [InlineData("claude-opus-4-20250514", "claude-opus-4", 15, 75)]
    [InlineData("claude-opus-4-1-20250805", "claude-opus-4-1", 15, 75)]
    [InlineData("claude-3-7-sonnet-20250219", "claude-3-7-sonnet", 3, 15)]
    [InlineData("claude-3-5-haiku-20241022", "claude-3-5-haiku-20241022", 0.8, 4)]
    [InlineData("claude-3-5-sonnet-20241022", "claude-3-5-sonnet", 3, 15)]
    [InlineData("claude-3-opus-20240229", "claude-3-opus", 15, 75)]
    public void Older_claude_models_use_builtin_prices(string model, string expectedKey, double inputPerM, double outputPerM)
    {
        var pricing = Catalog.Resolve(model);

        // Datowane ID trafiają w wiersz models.dev o tej samej tożsamości (np. claude-sonnet-4@20250514) — jak w ccusage.
        Assert.NotNull(pricing);
        Assert.StartsWith(expectedKey, pricing.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(inputPerM * 1e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(outputPerM * 1e-6, pricing.Base.Output!.Value, 12);
        Assert.Equal(200_000, pricing.MaxInputTokens);
    }

    [Fact]
    public void Builtin_prices_do_not_replace_litellm_rows()
    {
        // claude-sonnet-4-5 jest w snapshocie LiteLLM razem z progiem > 200k — wbudowana tabela go nie nadpisuje.
        var pricing = Catalog.Resolve("claude-sonnet-4-5");

        Assert.NotNull(pricing);
        Assert.Equal(3e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(Catalog.Resolve("claude-sonnet-4-5-20250929")!.Tiers.Count, pricing.Tiers.Count);
    }

    [Fact]
    public void Entries_spanning_many_requests_skip_long_context_tier()
    {
        var pricing = new ModelPricing
        {
            Key = "gemini-test",
            Base = new RateSet { Input = 1e-6, Output = 10e-6, CacheRead = 0.1e-6 },
            Tiers = [new PricingTier(200_000, new RateSet { Input = 2e-6, Output = 15e-6, CacheRead = 0.2e-6 })]
        };
        var request = TestData.Entry("2026-11-24T10:00:00Z", 150_000, 1_000, cacheRead: 100_000);
        var baseCost = 0.15 + 0.01 + 0.01;

        // Jedno żądanie > 200k — stawka progu; suma sesji lub tura z wielu żądań — stawka bazowa (ccusage #2365).
        Assert.Equal(0.3 + 0.015 + 0.02, CostCalculator.Price(request, pricing), 9);
        Assert.Equal(baseCost, CostCalculator.Price(request with { IsSessionSummary = true }, pricing), 9);
        Assert.Equal(baseCost, CostCalculator.Price(request with { IsAggregate = true }, pricing), 9);
    }

    [Fact]
    public void Fast_mode_uses_multiplier_only_and_unlisted_models_pay_standard()
    {
        // ccusage report/cost.rs: stawki _priority nie są używane; mnożnik z cennika lub fast-multiplier-overrides.json, inaczej 1×.
        var gpt51 = new ModelPricing { Key = "gpt-5.1", Base = new RateSet { Input = 1e-6, Output = 10e-6 } };
        var gpt55 = new ModelPricing { Key = "gpt-5.5", Base = new RateSet { Input = 1e-6, Output = 10e-6 } };
        var claude = new ModelPricing { Key = "claude-test", Base = new RateSet { Input = 1e-6, Output = 10e-6 }, FastMultiplier = 6 };
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 1_000_000, 0) with { Speed = SpeedTier.Fast };

        Assert.Equal(1.0, CostCalculator.Price(entry, gpt51), 9);
        Assert.Equal(2.5, CostCalculator.Price(entry, gpt55), 9);
        Assert.Equal(6.0, CostCalculator.Price(entry, claude), 9);
    }

    [Fact]
    public void Cost_modes_follow_ccusage_semantics()
    {
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 100_000, 0, model: "claude-sonnet-4-5", recorded: 9.99);

        Assert.Equal(9.99, new CostCalculator(Catalog, CostMode.Auto).Calculate(entry).CostUsd, 6);
        Assert.Equal(0.3, new CostCalculator(Catalog, CostMode.Calculate).Calculate(entry).CostUsd, 6);
        Assert.Equal(0, new CostCalculator(Catalog, CostMode.Display).Calculate(entry with { RecordedCostUsd = null }).CostUsd);
        // ccusage: 0 zapisane przez Claude Code to prawdziwy koszt; Kilo/Hermes/OpenCode zapisują 0 także bez danych → liczymy z tokenów (#2199).
        Assert.Equal(0, new CostCalculator(Catalog, CostMode.Auto).Calculate(entry with { RecordedCostUsd = 0 }).CostUsd, 6);
        Assert.Equal(0.3, new CostCalculator(Catalog, CostMode.Auto).Calculate(entry with { RecordedCostUsd = 0, Source = "opencode" }).CostUsd, 6);
    }

    [Fact]
    public void Overrides_replace_rates_per_model_and_per_source()
    {
        var overrides = new Dictionary<string, PricingOverride>
        {
            ["my-model"] = new() { InputCostPerToken = 1e-6, OutputCostPerToken = 2e-6 },
            ["[codex] my-model"] = new() { InputCostPerToken = 5e-6, OutputCostPerToken = 5e-6 }
        };
        var catalog = PricingCatalog.Embedded(overrides);

        Assert.Equal(1e-6, catalog.Resolve("my-model", "claude")!.Base.Input);
        Assert.Equal(5e-6, catalog.Resolve("my-model", "codex")!.Base.Input);
    }

    [Fact]
    public void Embedded_snapshot_parses_tiers_and_cache_rates()
    {
        var gemini = Catalog.Resolve("gemini-2.5-pro");
        var opus = Catalog.Resolve("claude-opus-4-6");

        Assert.NotNull(gemini);
        Assert.Contains(gemini.Tiers, tier => tier.AboveTokens == 200_000);
        Assert.NotNull(opus?.Base.CacheWrite1h);
    }
}
