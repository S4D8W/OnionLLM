using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

/// <summary>
/// Przebiegi dopasowania z rozróżnianiem wielkości liter przed przebiegami bez rozróżniania (plan 11.9.4; ccusage
/// <c>pricing/tests/resolver.rs</c>, <c>pricing/tests/overrides.rs</c>).
/// </summary>
public class PricingCaseTests
{
    private static PricingOverride Rates(double? input, double? output = null) => new() { InputCostPerToken = input, OutputCostPerToken = output };

    [Fact]
    public void Case_insensitive_identity_beats_a_hosted_fuzzy_hit()
    {
        var pricing = PricingCatalog.Embedded().Resolve("GLM-4.7");

        Assert.NotNull(pricing);
        Assert.Equal("glm-4.7", pricing.Key);
        Assert.Equal(0.6e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(2.2e-6, pricing.Base.Output!.Value, 12);
    }

    [Theory]
    // ccusage resolver.rs case_sensitive_hosted_identity_beats_a_folded_vendor_match.
    [InlineData("host/DeepSeek-Fixture.1", 9.0, 2000)]
    [InlineData("host/DeepSeek-Fixture-1", 9.0, 2000)]
    [InlineData("DEEPSEEK-FIXTURE.1", 1.0, 1000)]
    [InlineData("host/case-only.1", 3.0, 3000)]
    public void Case_sensitive_hosted_identity_beats_a_folded_vendor_match(string model, double input, long limit)
    {
        var catalog = PricingCatalog.FromJson("""
            { "deepseek/deepseek-fixture.1": { "litellm_provider": "deepseek", "input_cost_per_token": 1, "output_cost_per_token": 2,
                                               "max_input_tokens": 1000 } }
            """, """
            {
              "host/DeepSeek-Fixture.1": { "cost": { "input": 9000000, "output": 10000000 }, "limit": { "context": 2000 } },
              "host/Case-Only.1": { "cost": { "input": 3000000, "output": 4000000 }, "limit": { "context": 3000 } }
            }
            """);

        var pricing = catalog.Resolve(model);

        Assert.NotNull(pricing);
        Assert.Equal(input, pricing.Base.Input!.Value, 9);
        Assert.Equal(limit, pricing.MaxInputTokens);
    }

    [Fact]
    public void Case_fallback_keeps_the_direct_vendors_schedule()
    {
        var at = DateTimeOffset.Parse("2026-11-17T03:00:00Z").ToUnixTimeMilliseconds();

        var pricing = PricingCatalog.Embedded().ResolveAt(["DEEPSEEK-V4-PRO"], "DEEPSEEK-V4-PRO", null, "deepseek", at)!.Pricing;

        Assert.Equal(1.32e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(3.96e-6, pricing.Base.Output!.Value, 12);
    }

    [Theory]
    [InlineData("AmAzOn-BeDrOcK/Amazon.Nova")]
    [InlineData("Amazon.Nova")]
    public void Mixed_case_dotted_ids_and_host_prefixes_select_the_same_listing(string model)
    {
        var catalog = PricingCatalog.FromJson("""
            {
              "bedrock/Amazon.Nova": { "litellm_provider": "bedrock", "input_cost_per_token": 2, "output_cost_per_token": 2 },
              "nova": { "input_cost_per_token": 1, "output_cost_per_token": 1 }
            }
            """);

        Assert.Equal(2.0, catalog.ResolveAt(["nova", model], model, null, null, null)!.Pricing.Base.Input);
    }

    [Fact]
    public void Case_sensitive_fuzzy_match_comes_before_a_folded_one()
    {
        // ccusage dopasowuje przybliżenie tylko z rozróżnianiem wielkości liter; OnionLLM próbuje potem także bez (łagodniej).
        var catalog = PricingCatalog.FromJson("""
            {
              "Acme-1": { "input_cost_per_token": 1, "output_cost_per_token": 1 },
              "vendor/acme-1": { "input_cost_per_token": 5, "output_cost_per_token": 5 }
            }
            """);

        Assert.Equal("Acme-1", catalog.Resolve("Vendor/Acme-1-20250101")!.Key);
        Assert.Equal("vendor/acme-1", catalog.Resolve("vendor/acme-1-20250101")!.Key);
        // Bez dopasowania z tą samą wielkością liter zostaje przebieg bez rozróżniania (jak przed 11.9.4).
        Assert.Equal("claude-opus-4-6", PricingCatalog.Embedded().Resolve("Claude-Opus-4-6-Thinking")!.Key);
    }

    [Theory]
    [InlineData("gpt-5.4", "gpt-5.4-high", true)]
    [InlineData("gpt-5.4", "GPT-5.4-high", false)]
    [InlineData("claude-opus-4-6", "claude-opus-4-6-Thinking", false)]
    [InlineData("Acme-1", "Vendor/Acme-1-20250101", true)]
    public void Case_sensitive_key_matching_follows_ccusage(string key, string model, bool expected)
    {
        Assert.Equal(expected, PricingKeys.Matches(key, model, fold: false));
        Assert.True(PricingKeys.Matches(key, model));
    }

    [Fact]
    public void Case_insensitive_override_prices_a_custom_model()
    {
        var catalog = PricingCatalog.FromJson("{}", overrides: new Dictionary<string, PricingOverride> { ["GLM-Custom"] = Rates(1e-6, 2e-6) });

        var pricing = catalog.Resolve("glm-custom")!;

        Assert.Equal((1e-6, 2e-6), (pricing.Base.Input, pricing.Base.Output));
    }

    [Fact]
    public void Case_insensitive_override_wins_over_an_exact_catalogue_match()
    {
        var catalog = PricingCatalog.FromJson("""{ "claude-sonnet-4-5": { "litellm_provider": "anthropic", "input_cost_per_token": 3e-6, "output_cost_per_token": 15e-6 } }""",
            overrides: new Dictionary<string, PricingOverride> { ["Claude-Sonnet-4-5"] = Rates(2e-6, 10e-6) });

        var pricing = catalog.Resolve("claude-sonnet-4-5")!;

        Assert.Equal((2e-6, 10e-6), (pricing.Base.Input, pricing.Base.Output));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", "Claude-Sonnet-4-5")]
    [InlineData("Claude-Sonnet-4-5", "claude-sonnet-4-5")]
    public void Case_insensitive_partial_override_inherits_catalogue_rates(string catalogue, string key)
    {
        var catalog = PricingCatalog.FromJson($$"""{ "{{catalogue}}": { "litellm_provider": "anthropic", "input_cost_per_token": 3e-6, "output_cost_per_token": 15e-6 } }""",
            overrides: new Dictionary<string, PricingOverride> { [key] = Rates(2e-6) });

        var pricing = catalog.Resolve("claude-sonnet-4-5")!;

        Assert.Equal(2e-6, pricing.Base.Input);
        Assert.Equal(15e-6, pricing.Base.Output!.Value, 12);
    }

    [Theory]
    [InlineData("Glm-Custom", 1e-6, 3e-6)]
    [InlineData("GLM-Custom", 1e-6, 3e-6)]
    [InlineData("glm-custom", 2e-6, 4e-6)]
    public void Exact_case_wins_when_overrides_differ_only_by_case(string model, double input, double output)
    {
        // Konfiguracja OnionLLM nie przechowuje kluczy różniących się tylko wielkością liter; katalog przyjmuje je jak ccusage.
        var catalog = PricingCatalog.FromJson("{}", overrides: new Dictionary<string, PricingOverride>(StringComparer.Ordinal)
        {
            ["GLM-Custom"] = Rates(1e-6, 3e-6),
            ["glm-custom"] = Rates(2e-6, 4e-6)
        });

        var pricing = catalog.Resolve(model)!;

        Assert.Equal((input, output), (pricing.Base.Input!.Value, pricing.Base.Output!.Value));
    }

    [Fact]
    public void Labeled_override_matches_across_case()
    {
        var catalog = PricingCatalog.FromJson("{}", overrides: new Dictionary<string, PricingOverride> { ["[pi] GLM-Custom"] = Rates(1e-6, 2e-6) });

        var pricing = catalog.ResolveAt(["glm-custom"], "glm-custom", "pi", null, null)!.Pricing;

        Assert.Equal((1e-6, 2e-6), (pricing.Base.Input, pricing.Base.Output));
        Assert.Null(catalog.Resolve("glm-custom"));
    }

    [Fact]
    public void Exact_bare_override_beats_folded_labeled_override()
    {
        var catalog = PricingCatalog.FromJson("{}", overrides: new Dictionary<string, PricingOverride>
        {
            ["GLM-Custom"] = Rates(1e-6, 2e-6),
            ["[pi] glm-custom"] = Rates(3e-6, 4e-6)
        });

        var pricing = catalog.ResolveAt(["GLM-Custom"], "GLM-Custom", "pi", null, null)!.Pricing;

        Assert.Equal((1e-6, 2e-6), (pricing.Base.Input, pricing.Base.Output));
        Assert.True(catalog.HasOverride("glm-custom", "pi"));
    }

    [Fact]
    public void Exact_case_override_of_a_later_candidate_beats_a_folded_override_of_an_earlier_one()
    {
        // ccusage resolve.rs search_request: nadpisanie dopasowane bez rozróżniania wielkości liter czeka na pozostałych kandydatów.
        var catalog = PricingCatalog.FromJson("{}", overrides: new Dictionary<string, PricingOverride>
        {
            ["Alpha-Model"] = Rates(1e-6, 1e-6),
            ["beta-model"] = Rates(2e-6, 2e-6)
        });

        var pricing = catalog.ResolveAt(["alpha-model", "beta-model"], "alpha-model", null, null, null)!;

        Assert.Equal(2e-6, pricing.Pricing.Base.Input);
        Assert.Equal("beta-model", pricing.Candidate);
    }
}
