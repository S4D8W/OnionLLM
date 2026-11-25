using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Tests;

/// <summary>Dopasowanie nazw, models.dev, nadpisania, domyślne stawki cache i poziomy szybkości (ccusage <c>pricing/tests/*</c>).</summary>
public class PricingMatchingTests
{
    private static readonly PricingCatalog Catalog = PricingCatalog.Embedded();

    [Theory]
    // ccusage keys.rs / #2236: tylko różnice neutralne cenowo.
    [InlineData("gpt-5.4", "gpt-5.4-codex", false)]
    [InlineData("gpt-5.4", "chat-gpt-5.4", false)]
    [InlineData("gpt-5.4", "gpt-5.4-high", true)]
    [InlineData("gpt-5.4", "gpt-5-4-xhigh", true)]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4-5-20250929", true)]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4.5-2025-09-29", true)]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4-5[1m]", true)]
    [InlineData("claude-sonnet-4-5", "us.anthropic.claude-sonnet-4-5-20250929-v1:0", true)]
    [InlineData("claude-opus-4-6", "claude-opus-4-6-thinking", true)]
    [InlineData("claude-sonnet-4", "claude-sonnet-4-5", false)]
    [InlineData("qwen-plus", "qwen-plus-thinking", false)]
    [InlineData("qwen3-max", "qwen3-max-thinking", true)]
    [InlineData("qwen3.5-plus", "qwen3-5-plus-thinking", true)]
    [InlineData("grok-4", "grok-4-fast-reasoning", false)]
    [InlineData("grok-4-fast", "grok-4-fast-reasoning", true)]
    [InlineData("moonshot/kimi-k3", "kimi-k3", true)]
    [InlineData("openrouter/x-kimi-k3", "kimi-k3", false)]
    public void Pricing_keys_match_only_price_neutral_variants(string key, string model, bool expected)
    {
        Assert.Equal(expected, PricingKeys.Matches(key, model));
    }

    [Fact]
    public void Longer_name_with_another_model_word_stays_unpriced()
    {
        // Dawniej gpt-5.4-codex dostawał cenę gpt-5.4 (plan 11.1.5).
        Assert.Null(Catalog.Resolve("gpt-5.4-codex"));
        Assert.Equal("gpt-5.4", Catalog.Resolve("gpt-5.4-high")!.Key);
    }

    [Theory]
    [InlineData("qwen-plus-thinking", 0.4, 4)]
    [InlineData("qwen3-8b-thinking", 0.18, 2.1)]
    [InlineData("qwen-turbo-thinking", 0.05, 0.5)]
    public void Qwen_thinking_ids_have_their_own_rows(string model, double input, double output)
    {
        var pricing = Catalog.Resolve(model);

        Assert.NotNull(pricing);
        Assert.Equal(model, pricing.Key);
        Assert.Equal(input * 1e-6, pricing.Base.Input!.Value, 12);
        Assert.Equal(output * 1e-6, pricing.Base.Output!.Value, 12);
    }

    [Fact]
    public void Builtin_aliases_resolve_reserved_names()
    {
        Assert.Equal("gpt-5.6-luna", Catalog.Resolve("gpt-reserve")!.Key);
        Assert.Equal("gpt-5.3-codex-spark", Catalog.Resolve("gpt-5.3-spark")!.Key, ignoreCase: true);
    }

    [Fact]
    public void Direct_vendor_row_wins_over_bare_host_listing()
    {
        // LiteLLM: gemini-2.5-pro to wiersz Vertex AI; gemini/gemini-2.5-pro to API Google (ccusage authoritative, vendor ID).
        Assert.Equal("gemini/gemini-2.5-pro", Catalog.Resolve("gemini-2.5-pro")!.Key);
        Assert.Equal("gemini/gemini-2.5-pro", Catalog.Resolve("google/gemini-2.5-pro")!.Key);
    }

    [Fact]
    public void Models_dev_fills_models_missing_from_litellm()
    {
        var pricing = Catalog.Resolve("minimax-m3-preview");

        Assert.NotNull(pricing);
        Assert.Equal(PricingSource.ModelsDev, pricing.Source);
        Assert.True(Catalog.ModelsDevCount > 1000);
    }

    [Fact]
    public void Models_dev_api_format_prefers_the_model_owner_and_respects_exact_only()
    {
        using var document = JsonDocument.Parse("""
            {
              "openrouter": { "id": "openrouter", "models": {
                "acme-1": { "id": "acme-1", "cost": { "input": 9, "output": 9 }, "limit": { "context": 1000 } },
                "acme-beta": { "id": "acme-beta", "cost": { "input": 1, "output": 2 } }
              } },
              "openai": { "id": "openai", "models": {
                "acme-1": { "id": "acme-1", "cost": { "input": 1, "output": 2, "cache_read": 0.1,
                  "tiers": [ { "input": 2, "output": 4, "tier": { "type": "context", "size": 272000 } } ] } },
                "acme-image": { "id": "acme-image", "cost": { "input": 1, "output": 2 }, "modalities": { "input": ["text"], "output": ["image"] } }
              } }
            }
            """);

        var rows = ModelsDev.Parse(document.RootElement);

        var acme = rows["acme-1"];
        Assert.Equal(1e-6, acme.Base.Input!.Value, 12);
        Assert.Equal(272_000, Assert.Single(acme.Tiers).AboveTokens);
        Assert.False(acme.ExactOnly);
        // ID bez numeru wersji = tylko dokładne dopasowanie (ccusage catalog.rs is_unversioned_models_dev_model_id).
        Assert.True(rows["acme-beta"].ExactOnly);
        // Model z wyjściem obrazowym nie ma ceny za token.
        Assert.False(rows.ContainsKey("acme-image"));
    }

    [Fact]
    public void Cache_rates_default_per_vendor_when_row_omits_them()
    {
        var entry = TestData.Entry("2026-11-24T10:00:00Z", cacheRead: 1_000_000, cacheWrite: 1_000_000);
        double Cost(string key) => CostCalculator.Price(entry, new ModelPricing { Key = key, Base = new RateSet { Input = 1e-6, Output = 1e-6 } });

        // ccusage data.rs CACHE_DEFAULTS_*: OpenAI zapis 1×/odczyt 0,1×, Qwen 0,2×, DeepSeek 0,02×, Anthropic 1,25×/0,1×.
        Assert.Equal(1.0 + 0.1, Cost("gpt-5.4"), 9);
        Assert.Equal(1.0 + 0.2, Cost("qwen3-max"), 9);
        Assert.Equal(1.0 + 0.02, Cost("deepseek-v4-pro"), 9);
        Assert.Equal(1.25 + 0.1, Cost("claude-sonnet-4-5"), 9);
        Assert.Equal(1.25 + 0.1, Cost("totally-unknown"), 9);

        // GPT-5.6+: zapis 1 h 2,5×.
        var oneHour = entry with { CacheReadTokens = 0, CacheCreation1hTokens = 1_000_000 };
        Assert.Equal(2.5, CostCalculator.Price(oneHour, new ModelPricing { Key = "gpt-5.6-sol", Base = new RateSet { Input = 1e-6 } }), 9);
    }

    [Fact]
    public void Flex_and_ultrafast_apply_only_to_listed_models()
    {
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 1_000_000);
        var gpt54 = new ModelPricing { Key = "gpt-5.4", Base = new RateSet { Input = 1e-6, Output = 1e-6 } };
        var claude = new ModelPricing { Key = "claude-sonnet-4-5", Base = new RateSet { Input = 1e-6, Output = 1e-6 } };
        var astra = new ModelPricing { Key = "gpt-6-astra", Base = new RateSet { Input = 1e-6, Output = 1e-6 } };

        Assert.Equal(0.5, CostCalculator.Price(entry with { Speed = SpeedTier.Flex }, gpt54), 9);
        Assert.Equal(1.0, CostCalculator.Price(entry with { Speed = SpeedTier.Flex }, claude), 9);
        Assert.Equal(6.0, CostCalculator.Price(entry with { Speed = SpeedTier.Ultrafast }, astra), 9);
        Assert.Equal(1.0, CostCalculator.Price(entry with { Speed = SpeedTier.Ultrafast }, gpt54), 9);
    }

    [Fact]
    public void Fast_keeps_the_long_context_tier()
    {
        // ccusage cost.rs: fast pomija próg tylko przy harmonogramie z flat_in_fast_mode (Anthropic 1M w becie).
        var pricing = new ModelPricing
        {
            Key = "gpt-5.4",
            Base = new RateSet { Input = 1e-6, Output = 1e-6 },
            Tiers = [new PricingTier(272_000, new RateSet { Input = 2e-6 })]
        };
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 300_000) with { Speed = SpeedTier.Fast };

        Assert.Equal(300_000 * 2e-6 * 2, CostCalculator.Price(entry, pricing), 9);
    }

    [Fact]
    public void Marginal_tier_bills_only_tokens_above_the_threshold()
    {
        var pricing = new ModelPricing
        {
            Key = "qwen-test",
            Base = new RateSet { Input = 1e-6, Output = 1e-6 },
            Tiers = [new PricingTier(200_000, new RateSet { Input = 3e-6 }) { Marginal = true }]
        };

        Assert.Equal(200_000 * 1e-6 + 100_000 * 3e-6, CostCalculator.Price(TestData.Entry("2026-11-24T10:00:00Z", 300_000), pricing), 9);
    }

    [Fact]
    public void Free_vendor_row_through_paid_host_is_estimated_from_closest_paid_listing()
    {
        var catalog = PricingCatalog.FromJson("""
            {
              "deepseek/deepseek-free": { "litellm_provider": "deepseek", "input_cost_per_token": 0, "output_cost_per_token": 0 },
              "deepseek-free": { "litellm_provider": "together_ai", "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6 }
            }
            """);

        // ccusage #2228: darmowy wiersz dostawcy użyty przez płatnego hosta (Bedrock) → najbliższy płatny wiersz + flaga estimated.
        var hosted = catalog.ResolveAt(["bedrock/deepseek-free"], "bedrock/deepseek-free", null, null, null)!.Pricing;
        Assert.True(hosted.IsEstimated);
        Assert.Equal(1e-6, hosted.Base.Input);

        // Bezpośrednio u dostawcy — darmowe, bez szacunku.
        var direct = catalog.ResolveAt(["deepseek/deepseek-free"], "deepseek/deepseek-free", null, null, null)!.Pricing;
        Assert.True(direct.IsFree);
        Assert.False(direct.IsEstimated);

        var calculator = new CostCalculator(catalog, CostMode.Calculate, _ => null);
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 1_000_000, model: "bedrock/deepseek-free", source: "opencode");
        Assert.True(calculator.Calculate(entry).IsEstimated);
        Assert.Equal(1.0, calculator.Calculate(entry).CostUsd, 9);
    }

    [Fact]
    public void Free_google_rows_are_unpriced()
    {
        // ccusage litellm-sources.json zeroTokenRateUnpricedProviders: Gemma/Lyria 0/0 od Google to nie cena za token.
        var catalog = PricingCatalog.FromJson("""
            { "gemini/gemma-3-27b-it": { "litellm_provider": "gemini", "input_cost_per_token": 0, "output_cost_per_token": 0 } }
            """);

        Assert.Null(catalog.Resolve("gemini/gemma-3-27b-it"));
    }

    [Fact]
    public void Estimated_pricing_marks_cost_result()
    {
        var catalog = PricingCatalog.Embedded();
        var calculator = new CostCalculator(catalog, CostMode.Calculate, _ => null);
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 1_000, 1_000, model: "claude-sonnet-4-5");

        Assert.False(calculator.Calculate(entry).IsEstimated);
        Assert.True(calculator.Calculate(entry with { IsFallbackModel = true }).IsEstimated);
    }

    [Fact]
    public void Overrides_match_provider_prefixed_names_and_shadow_catalog_rows()
    {
        var catalog = PricingCatalog.Embedded(new Dictionary<string, PricingOverride>
        {
            ["my-model"] = new() { InputCostPerToken = 1e-6, OutputCostPerToken = 2e-6 },
            ["moonshot/kimi-k3"] = new() { InputCostPerToken = 7e-6 },
            ["[zcode] private-model"] = new() { InputCostPerToken = 4e-6, OutputCostPerToken = 4e-6 }
        });

        // Sufiks ścieżki: openrouter/acme/my-model → my-model.
        Assert.Equal(1e-6, catalog.Resolve("openrouter/acme/my-model")!.Base.Input);
        // Nadpisanie wiersza dostawcy obowiązuje też dla nazwy bez prefiksu; pozostałe stawki z cennika.
        var kimi = catalog.Resolve("kimi-k3")!;
        Assert.Equal(7e-6, kimi.Base.Input);
        Assert.Equal(PricingCatalog.Embedded().Resolve("kimi-k3")!.Base.Output, kimi.Base.Output);
        // Przybliżone dopasowanie klucza nadpisania.
        Assert.Equal(1e-6, catalog.Resolve("my-model-20260101")!.Base.Input);
        Assert.True(catalog.HasOverride("private-model", "zcode"));
        Assert.False(catalog.HasOverride("private-model", "claude"));
        Assert.Null(catalog.Resolve("private-model", "claude"));
        Assert.Equal(4e-6, catalog.Resolve("private-model", "zcode")!.Base.Input);
    }

    [Fact]
    public void Override_long_context_and_speed_fields_follow_ccusage()
    {
        var catalog = PricingCatalog.Embedded(new Dictionary<string, PricingOverride>
        {
            ["claude-custom"] = new() { InputCostPerToken = 1e-6, OutputCostPerToken = 1e-6, InputCostPerTokenAbove200kTokens = 2e-6, FastMultiplier = 3 },
            ["custom-model"] = new() { InputCostPerToken = 1e-6, OutputCostPerToken = 1e-6, InputCostPerTokenAbove200kTokens = 2e-6, FlexMultiplier = 0.25 },
            ["claude-sonnet-4-5"] = new() { CacheCreationInputTokenCostAbove1h = 9e-6 }
        });
        var entry = TestData.Entry("2026-11-24T10:00:00Z", 300_000);

        // Claude: próg całego żądania; inny model: krańcowy.
        Assert.Equal(300_000 * 2e-6, CostCalculator.Price(entry, catalog.Resolve("claude-custom")!), 9);
        Assert.Equal(200_000 * 1e-6 + 100_000 * 2e-6, CostCalculator.Price(entry, catalog.Resolve("custom-model")!), 9);
        Assert.Equal(3 * 300_000 * 2e-6, CostCalculator.Price(entry with { Speed = SpeedTier.Fast }, catalog.Resolve("claude-custom")!), 9);
        Assert.Equal(0.25 * (200_000 * 1e-6 + 100_000 * 2e-6), CostCalculator.Price(entry with { Speed = SpeedTier.Flex }, catalog.Resolve("custom-model")!), 9);

        // Tylko stawka 1 h — reszta z cennika; jawna stawka bazowa usuwa próg cennika (ccusage replace_tier_with_explicit_rates).
        var sonnet = catalog.Resolve("claude-sonnet-4-5")!;
        Assert.Equal(9e-6, sonnet.Base.CacheWrite1h);
        Assert.Equal(3e-6, sonnet.Base.Input);
        Assert.Empty(sonnet.Tiers);
        Assert.NotEmpty(PricingCatalog.Embedded().Resolve("claude-sonnet-4-5")!.Tiers);
    }

    [Fact]
    public void Configured_model_aliases_route_pricing()
    {
        var catalog = PricingCatalog.Embedded(null, new Dictionary<string, string> { ["private-alpha"] = "gpt-5.5" });

        Assert.Equal("gpt-5.5", catalog.Resolve("private-alpha")!.Key);
        Assert.Null(PricingCatalog.Embedded().Resolve("private-alpha"));
    }

    [Fact]
    public void Litellm_rows_record_provider_scope_and_speed_multipliers()
    {
        using var document = JsonDocument.Parse("""
            { "litellm_provider": "openai", "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6,
              "provider_specific_entry": { "fast": 2, "flex": 0.5 }, "input_cost_per_token_above_200k_tokens": 2e-6 }
            """);

        var row = ModelPricing.FromLiteLlm("gpt-test", document.RootElement);

        Assert.True(row.IsDirect);
        Assert.Equal(2, row.FastMultiplier);
        Assert.Equal(0.5, row.FlexMultiplier);
        Assert.True(Assert.Single(row.Tiers).Marginal);
        Assert.False(Assert.Single(ModelPricing.FromLiteLlm("claude-test", document.RootElement).Tiers).Marginal);
    }

    [Fact]
    public void Serialized_snapshot_keeps_provider_and_speed_entries()
    {
        using var document = JsonDocument.Parse("""
            {
              "claude-test": { "litellm_provider": "anthropic", "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "supports_vision": true,
                               "provider_specific_entry": { "fast": 6 } },
              "azure/gpt-test": { "litellm_provider": "azure", "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6 },
              "someone/other": { "litellm_provider": "someone", "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6 },
              "gpt-no-output": { "input_cost_per_token": 1e-6 }
            }
            """);

        using var filtered = JsonDocument.Parse(PricingCatalog.SerializeFiltered(document.RootElement));

        var keys = filtered.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["azure/gpt-test", "claude-test"], keys);
        var claude = filtered.RootElement.GetProperty("claude-test");
        Assert.Equal("anthropic", claude.GetProperty("litellm_provider").GetString());
        Assert.Equal(6, claude.GetProperty("provider_specific_entry").GetProperty("fast").GetDouble());
        Assert.False(claude.TryGetProperty("supports_vision", out _));
    }
}
