using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Wiersze <c>-thinking</c> (plan 11.9.5; ccusage <c>pricing/builtin_pricing.rs</c>, <c>pricing/tests/fuzzy.rs</c>,
/// <c>pricing/tests/curated.rs</c>, <c>cost/tests.rs</c>). Żadne źródło nie rozpoznaje trybu thinking — wiersz pasuje tylko do id
/// z sufiksem <c>-thinking</c> zapisanego w logu.
/// </summary>
public class PricingThinkingTests
{
    private static readonly PricingCatalog Catalog = PricingCatalog.Embedded();

    [Theory]
    [InlineData("qwen-plus-thinking", 0.4, 4)]
    [InlineData("dashscope/qwen-plus-thinking", 0.4, 4)]
    [InlineData("qwen-plus-latest-thinking", 0.4, 4)]
    [InlineData("qwen-plus-2025-12-01-thinking", 0.4, 4)]
    [InlineData("qwen-plus-2025-09-11-thinking", 0.4, 4)]
    [InlineData("qwen-plus-2025-07-28-thinking", 0.4, 4)]
    [InlineData("qwen-plus-2025-07-14-thinking", 0.4, 4)]
    [InlineData("qwen-plus-2025-04-28-thinking", 0.4, 4)]
    [InlineData("qwen-turbo-thinking", 0.05, 0.5)]
    [InlineData("qwen3-30b-a3b-thinking", 0.2, 2.4)]
    [InlineData("qwen3-14b-thinking", 0.35, 4.2)]
    [InlineData("qwen3-8b-thinking", 0.18, 2.1)]
    public void A_qwen_thinking_run_bills_at_the_thinking_rate_where_alibaba_lists_one(string model, double input, double output)
    {
        var pricing = Catalog.Resolve(model);

        Assert.NotNull(pricing);
        Assert.Equal(input * 1e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(output * 1e-6, pricing.Base.Output!.Value, 12);
    }

    [Fact]
    public void A_qwen_plus_thinking_cache_hit_bills_at_the_implicit_cache_rate()
    {
        var pricing = Catalog.Resolve("qwen-plus-thinking")!;

        Assert.Equal(0.5e-6, pricing.Base.CacheWrite!.Value, 12);
        Assert.Equal(0.08e-6, pricing.Base.CacheRead!.Value, 12);
    }

    [Theory]
    [InlineData(255_999, 0.4, 4.0)]
    [InlineData(256_000, 0.4, 4.0)]
    [InlineData(256_001, 1.2, 12.0)]
    public void Qwen_plus_thinking_request_above_256k_bills_whole_request_at_long_context_rates(long input, double inputRate, double outputRate)
    {
        var entry = TestData.Entry("2026-11-24T10:00:00Z", input, 1_000, model: "qwen-plus-thinking");

        var cost = CostCalculator.Price(entry, Catalog.Resolve("qwen-plus-thinking")!);

        Assert.Equal((input * inputRate + 1_000 * outputRate) / 1_000_000, cost, 12);
    }

    [Theory]
    [InlineData("qwen-plus-2025-07-14-thinking")]
    [InlineData("qwen-plus-2025-04-28-thinking")]
    public void Qwen_plus_snapshots_without_tiered_pricing_have_no_256k_tier(string model)
    {
        Assert.Empty(Catalog.Resolve(model)!.Tiers);
        Assert.Equal(256_000, Assert.Single(Catalog.Resolve("qwen-plus-2025-07-28-thinking")!.Tiers).AboveTokens);
    }

    [Theory]
    [InlineData("kimi-k2-thinking")]
    [InlineData("moonshot/kimi-k2-thinking")]
    public void Kimi_k2_thinking_bills_the_curated_list_price(string model)
    {
        var pricing = Catalog.Resolve(model)!;

        Assert.Equal("moonshot/kimi-k2-thinking", pricing.Key);
        Assert.Equal(0.6e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(2.5e-6, pricing.Base.Output!.Value, 12);
        Assert.Equal(0.15e-6, pricing.Base.CacheRead!.Value, 12);
    }

    [Theory]
    // Rodziny, które liczą thinking po stawce wyjścia modelu: sufiks jest neutralny cenowo (ccusage FAMILY_PRICE_NEUTRAL_SUFFIX_WORDS).
    [InlineData("claude-sonnet-5-5-thinking", "claude-sonnet-5-5")]
    [InlineData("claude-sonnet-5-5:thinking-high", "claude-sonnet-5-5")]
    [InlineData("claude-opus-4-6-thinking", "claude-opus-4-6")]
    [InlineData("gemini-4-argon-thinking", "gemini-4-argon")]
    [InlineData("gemini-4-argon-thinking-low", "gemini-4-argon")]
    [InlineData("deepseek-v4.1-flash-thinking", "deepseek-v4.1-flash")]
    [InlineData("zai/glm-5.3:thinking", "zai/glm-5.3")]
    [InlineData("grok-4.7-reasoning", "grok-4.7")]
    [InlineData("grok-4-7-non-reasoning", "grok-4.7")]
    [InlineData("alibaba/qwen3.8-max:thinking", "alibaba/qwen3.8-max")]
    [InlineData("qwen3-8-max-thinking", "alibaba/qwen3.8-max")]
    [InlineData("qwen3.5-plus-thinking", "qwen3.5-plus")]
    [InlineData("kimi-k3-thinking", "kimi-k3")]
    [InlineData("moonshot/kimi-k2.6-thinking", "moonshot/kimi-k2.6")]
    [InlineData("mistral/mistral-large-4-thinking", "mistral/mistral-large-4")]
    [InlineData("mistral-medium-3.5-thinking", "mistral-medium-3.5")]
    public void A_thinking_run_bills_at_the_listed_rate_for_vendors_that_bill_it_as_output(string model, string listed)
    {
        var found = Catalog.Resolve(model);
        var expected = Catalog.Resolve(listed)!;

        Assert.NotNull(found);
        Assert.Equal((expected.Base.Input, expected.Base.Output), (found.Base.Input, found.Base.Output));
    }

    [Theory]
    // qwen-plus nie ma neutralnego słowa thinking: inny id bez własnego wiersza zostaje bez ceny zamiast stawki non-thinking.
    [InlineData("qwen-plus-2025-01-25-thinking")]
    [InlineData("qwen-turbo-latest-thinking")]
    [InlineData("gemma-4-31b-it-thinking")]
    [InlineData("mistral-large-4-reasoning")]
    [InlineData("mistral.devstral-2-123b-thinking")]
    [InlineData("claude-sonnet-5-5-reasoning")]
    [InlineData("grok-4.7-thinking")]
    public void A_thinking_variant_without_a_documented_billing_rule_stays_unpriced(string model)
    {
        Assert.Null(Catalog.Resolve(model));
    }

    [Fact]
    public void Thinking_ids_do_not_fall_back_to_the_non_thinking_row()
    {
        Assert.False(PricingKeys.Matches("qwen-plus", "qwen-plus-thinking"));
        Assert.False(PricingKeys.Matches("qwen-turbo", "qwen-turbo-thinking"));
        Assert.False(PricingKeys.Matches("qwen3-8b", "qwen3-8b-thinking"));
        Assert.Equal("qwen-plus-thinking", Catalog.Resolve("qwen-plus-thinking")!.Key);
        Assert.Equal("qwen-plus-2025-07-28-thinking", Catalog.Resolve("qwen-plus-2025-07-28-thinking")!.Key);
    }
}
