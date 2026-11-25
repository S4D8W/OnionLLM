namespace OnionLLM.Core.Pricing;

/// <summary>
/// Ceny wbudowane w kod, uzupełniające LiteLLM o modele, których tam brakuje (np. starsze Claude: Sonnet 4, Opus 4/4.1, 3.x).
/// Odpowiednik ccusage <c>ccusage-core/src/pricing/builtin_pricing.rs</c> (<c>BUILTIN_PRICING</c>, <c>CURATED_LIST_PRICES</c>) oraz wierszy
/// Claude, które ccusage bierze z models.dev. Wiersz jest dodawany tylko wtedy, gdy katalogu nie ma takiego klucza; istniejącemu
/// wierszowi uzupełnia się wyłącznie brakujące stawki cache i limit kontekstu (jak <c>supplement_pricing</c> w ccusage).
/// </summary>
internal static class BuiltinPricing
{
    private const double M = 1e-6;

    private sealed record Row(string Provider, string Key, double Input, double Output, double? CacheWrite, double? CacheRead, long? MaxInputTokens)
    {
        public PricingTier? Tier { get; init; }
    }

    // NOTE(ccusage builtin_pricing.rs, 2026-11-20): Alibaba — qwen-plus w trybie thinking powyżej 256K: całe żądanie $1.2 / $12.
    private static readonly PricingTier QwenPlusThinkingTier =
        new(256_000, new RateSet { Input = 1.2 * M, Output = 12 * M, CacheWrite = 1.5 * M, CacheRead = 0.24 * M });

    private static readonly Row[] Rows =
    [
        // Anthropic (ccusage BUILTIN_PRICING).
        new("anthropic", "claude-opus-4-5", 5, 25, 6.25, 0.5, 200_000),
        new("anthropic", "claude-opus-4-6", 5, 25, 6.25, 0.5, 1_000_000),
        new("anthropic", "claude-opus-4-7", 5, 25, 6.25, 0.5, 1_000_000),
        new("anthropic", "claude-opus-4-8", 5, 25, 6.25, 0.5, 1_000_000),
        new("anthropic", "claude-haiku-4-5", 1, 5, 1.25, 0.1, 200_000),
        new("anthropic", "claude-opus-4", 15, 75, 18.75, 1.5, 200_000),
        new("anthropic", "claude-sonnet-4-6", 3, 15, 3.75, 0.3, 1_000_000),
        new("anthropic", "claude-sonnet-4", 3, 15, 3.75, 0.3, 200_000),
        new("anthropic", "claude-3-5-haiku", 0.8, 4, 1, 0.08, 200_000),
        new("anthropic", "claude-3-5-haiku-20241022", 0.8, 4, 1, 0.08, 200_000),
        new("anthropic", "claude-3-opus", 15, 75, 18.75, 1.5, 200_000),
        new("anthropic", "claude-3-sonnet", 3, 15, 3.75, 0.3, 200_000),
        new("anthropic", "claude-3-haiku", 0.25, 1.25, 0.3, 0.03, 200_000),

        // Anthropic — w ccusage z models.dev (models-dev-pricing.json), w LiteLLM już ich nie ma.
        new("anthropic", "claude-opus-4-1", 15, 75, 18.75, 1.5, 200_000),
        new("anthropic", "claude-3-7-sonnet", 3, 15, 3.75, 0.3, 200_000),
        // Cennik Anthropic dla 3.5 Sonnet (brak w LiteLLM, models.dev i ccusage) — bez tego stare logi wychodzą za $0.
        new("anthropic", "claude-3-5-sonnet", 3, 15, 3.75, 0.3, 200_000),

        // OpenAI: zapis do cache po stawce wejścia (ccusage WRITES_AT_INPUT_RATE) — tu null, kalkulator przyjmie domyślne mnożniki.
        new("openai", "gpt-5", 1.25, 10, null, 0.125, null),
        new("openai", "gpt-5.1", 1.25, 10, null, 0.125, null),
        new("openai", "gpt-5.5", 5, 30, null, 0.5, 1_050_000),
        new("openai", "gpt-5.3-codex", 1.75, 14, null, 0.175, null),
        new("openai", "gpt-5.4", 2.5, 15, null, 0.25, 1_050_000),
        new("openai", "gpt-5.4-mini", 0.75, 4.5, null, 0.075, null),
        new("openai", "gpt-5.4-nano", 0.2, 1.25, null, 0.02, null),
        new("openai", "gpt-5.6-sol", 5, 30, 6.25, 0.5, 1_050_000),
        new("openai", "gpt-5.6-terra", 2.5, 15, 3.125, 0.25, 1_050_000),
        new("openai", "gpt-5.6-luna", 1, 6, 1.25, 0.1, 1_050_000),

        // xAI: ccusage nie podaje stawki odczytu cache.
        new("xai", "grok-4.3", 1.25, 2.5, 1.25, null, 1_000_000),

        // Moonshot: zapis do cache z dopłatą 1,25× wejścia (ccusage WRITES_SURCHARGED).
        new("moonshot", "moonshot/kimi-k2.5", 0.6, 3, 0.75, 0.1, 262_144),
        new("moonshot", "moonshot/kimi-k2.6", 0.95, 4, 1.1875, 0.16, 262_144),

        // Z.AI: zapis do cache bezpłatny (ccusage #1541).
        new("zai", "glm-4.5", 0.6, 2.2, 0, 0.11, null),
        new("zai", "zai/glm-4.5", 0.6, 2.2, 0, 0.11, null),
        new("zai", "glm-4.6", 0.6, 2.2, 0, 0.11, null),
        new("zai", "glm-4.7", 0.6, 2.2, 0, 0.11, null),
        new("zai", "zai/glm-4.5-x", 2.2, 8.9, 0, 0.45, null),
        new("zai", "zai/glm-4.5-air", 0.2, 1.1, 0, 0.03, null),
        new("zai", "zai/glm-4.5-airx", 1.1, 4.5, 0, 0.22, null),
        new("zai", "zai/glm-4.5v", 0.6, 1.8, 0, 0.11, null),
        new("zai", "zai/glm-4-32b-0414-128k", 0.1, 0.1, 0, 0, null),
        new("zai", "zai/glm-4.5-flash", 0, 0, 0, 0, null),
        new("zai", "glm-5", 1, 3.2, 0, 0.2, null),
        new("zai", "glm-5-turbo", 1.2, 4, 0, 0.24, null),
        new("zai", "glm-5.1", 1.4, 4.4, 0, 0.26, null),

        // NOTE(ccusage, 2026-11-20): Alibaba liczy wyjście thinking drożej dla qwen-plus, qwen-turbo i qwen3-8b/14b/30b-a3b —
        // id z sufiksem -thinking ma własny wiersz (słowo „thinking” nie jest dla tych modeli neutralne cenowo).
        new("dashscope", "qwen-plus-thinking", 0.4, 4, 0.5, 0.08, null) { Tier = QwenPlusThinkingTier },
        new("dashscope", "qwen-plus-latest-thinking", 0.4, 4, 0.5, 0.08, null) { Tier = QwenPlusThinkingTier },
        new("dashscope", "qwen-plus-2025-12-01-thinking", 0.4, 4, 0.5, 0.08, null) { Tier = QwenPlusThinkingTier },
        new("dashscope", "qwen-plus-2025-09-11-thinking", 0.4, 4, 0.5, 0.08, null) { Tier = QwenPlusThinkingTier },
        new("dashscope", "qwen-plus-2025-07-28-thinking", 0.4, 4, 0.5, 0.08, null) { Tier = QwenPlusThinkingTier },
        new("dashscope", "qwen-plus-2025-07-14-thinking", 0.4, 4, 0.5, 0.08, null),
        new("dashscope", "qwen-plus-2025-04-28-thinking", 0.4, 4, 0.5, 0.08, null),
        new("dashscope", "qwen-turbo-thinking", 0.05, 0.5, 0.0625, 0.005, null),
        new("dashscope", "qwen3-30b-a3b-thinking", 0.2, 2.4, 0.25, 0.02, null),
        new("dashscope", "qwen3-14b-thinking", 0.35, 4.2, 0.4375, 0.035, null),
        new("dashscope", "qwen3-8b-thinking", 0.18, 2.1, 0.225, 0.018, null)
    ];

    /// <summary>
    /// Ceny listowe zastępujące stawki wiersza (ccusage <c>CURATED_LIST_PRICES</c>, #2231): wycofane modele, dla których LiteLLM
    /// podaje cenę hosta albo nieaktualną. Zapis do cache zostaje z wiersza, jeśli go podaje.
    /// </summary>
    private static readonly Row[] CuratedListPrices =
    [
        new("moonshot", "moonshot/kimi-k2-thinking", 0.6, 2.5, 0.6, 0.15, 262_144),
        new("moonshot", "moonshot/kimi-k2-turbo-preview", 1.15, 8, 1.15, 0.15, 262_144),
        new("deepseek", "deepseek/deepseek-v3.2", 0.28, 0.42, null, 0.028, null),
        new("gemini", "gemini-2.0-flash", 0.1, 0.4, null, 0.025, null)
    ];

    /// <summary>Dodaje brakujące modele i uzupełnia brakujące stawki cache / limit kontekstu w istniejących; potem ceny listowe.</summary>
    public static void Apply(Dictionary<string, ModelPricing> models)
    {
        foreach (var row in Rows)
        {
            var rates = Rates(row);
            if (!models.TryGetValue(row.Key, out var existing))
            {
                models[row.Key] = Create(row, rates);
                continue;
            }

            models[row.Key] = existing with
            {
                Base = existing.Base with
                {
                    CacheWrite = existing.Base.CacheWrite ?? rates.CacheWrite,
                    CacheRead = existing.Base.CacheRead ?? rates.CacheRead
                },
                MaxInputTokens = existing.MaxInputTokens ?? row.MaxInputTokens
            };
        }

        foreach (var row in CuratedListPrices)
        {
            var rates = Rates(row);
            models[row.Key] = models.TryGetValue(row.Key, out var existing)
                ? Create(row, rates with { CacheWrite = existing.Base.CacheWrite ?? rates.CacheWrite }) with
                {
                    MaxInputTokens = existing.MaxInputTokens ?? row.MaxInputTokens,
                    Tiers = existing.Tiers,
                    FastMultiplier = existing.FastMultiplier,
                    FlexMultiplier = existing.FlexMultiplier
                }
                : Create(row, rates);
        }
    }

    private static RateSet Rates(Row row) => new()
    {
        Input = row.Input * M,
        Output = row.Output * M,
        CacheWrite = row.CacheWrite * M,
        CacheRead = row.CacheRead * M
    };

    private static ModelPricing Create(Row row, RateSet rates)
    {
        var vendorId = row.Key.StartsWith(row.Provider + "/", StringComparison.Ordinal) ? row.Key[(row.Provider.Length + 1)..] : row.Key;
        return new ModelPricing
        {
            Key = row.Key,
            Base = rates,
            Tiers = row.Tier is null ? [] : [row.Tier],
            MaxInputTokens = row.MaxInputTokens,
            Provider = row.Provider,
            Source = PricingSource.Builtin,
            IsDirect = true,
            Schedule = PricingSchedules.For(vendorId)
        };
    }
}
