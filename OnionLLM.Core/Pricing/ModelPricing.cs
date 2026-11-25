using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnionLLM.Core.Pricing;

/// <summary>Stawki za token (USD) dla jednego poziomu cenowego.</summary>
public sealed record RateSet
{
    public double? Input { get; init; }
    public double? Output { get; init; }
    public double? CacheWrite { get; init; }
    public double? CacheWrite1h { get; init; }
    public double? CacheRead { get; init; }

    public bool IsEmpty => Input is null && Output is null && CacheWrite is null && CacheWrite1h is null && CacheRead is null;

    public RateSet Over(RateSet fallback) => new()
    {
        Input = Input ?? fallback.Input,
        Output = Output ?? fallback.Output,
        CacheWrite = CacheWrite ?? fallback.CacheWrite,
        CacheWrite1h = CacheWrite1h ?? fallback.CacheWrite1h,
        CacheRead = CacheRead ?? fallback.CacheRead
    };
}

/// <summary>
/// Próg długiego kontekstu. Domyślnie całe żądanie powyżej <see cref="AboveTokens"/> ma stawki <see cref="Rates"/>
/// (Anthropic, Google, xAI, progi models.dev); <see cref="Marginal"/> = stawki progu tylko dla tokenów ponad próg
/// (ccusage <c>LongContextMode::Marginal</c> dla pól <c>_above_200k_tokens</c> LiteLLM innych dostawców).
/// </summary>
public sealed record PricingTier(long AboveTokens, RateSet Rates)
{
    public bool Marginal { get; init; }
}

/// <summary>Pochodzenie wiersza cennika.</summary>
public enum PricingSource
{
    LiteLlm,
    Builtin,
    ModelsDev,
    Override
}

/// <summary>Cennik modelu (pola w konwencji LiteLLM).</summary>
public sealed partial record ModelPricing
{
    public required string Key { get; init; }
    public RateSet Base { get; init; } = new();
    public IReadOnlyList<PricingTier> Tiers { get; init; } = [];
    public long? MaxInputTokens { get; init; }

    /// <summary>Mnożnik trybu fast z cennika lub nadpisania; null = tabela ccusage albo 1.</summary>
    public double? FastMultiplier { get; init; }

    /// <summary>Mnożnik trybu flex; null = tabela ccusage (tylko wymienione modele) albo 1 (bez zniżki).</summary>
    public double? FlexMultiplier { get; init; }

    /// <summary>Mnożnik trybu ultrafast (Codex); null = tabela ccusage albo 1.</summary>
    public double? UltrafastMultiplier { get; init; }

    /// <summary>Dostawca LiteLLM (<c>litellm_provider</c>), np. <c>anthropic</c>, <c>vertex_ai-language-models</c>.</summary>
    public string? Provider { get; init; }

    public PricingSource Source { get; init; } = PricingSource.LiteLlm;

    /// <summary>Wiersz z API samego autora modelu (dostawca z <see cref="PricingData.DirectProviders"/>), a nie hosta.</summary>
    public bool IsDirect { get; init; }

    /// <summary>models.dev: wiersz dopasowywany tylko dokładną nazwą (ccusage <c>exactOnly</c>).</summary>
    public bool ExactOnly { get; init; }

    /// <summary>Cena szacowana: darmowy wiersz dostawcy użyty przez płatnego hosta dostał cenę najbliższego płatnego wiersza (ccusage #2228).</summary>
    public bool IsEstimated { get; init; }

    /// <summary>Harmonogram cen dostawcy zależny od daty (ccusage <c>SCHEDULES</c>); stosowany przez <see cref="At"/>.</summary>
    public PricingSchedule? Schedule { get; init; }

    /// <summary>Tryb fast nie płaci progu z harmonogramu (Anthropic 1M w becie, ccusage <c>flat_in_fast_mode</c>).</summary>
    public bool FlatLongContextInFastMode { get; init; }

    /// <summary>Stawki podane jawnie w <c>pricingOverrides</c> — harmonogram ich nie zmienia.</summary>
    public RateSet Explicit { get; init; } = new();

    /// <summary>Próg podany jawnie w <c>pricingOverrides</c> (<c>*Above200kTokens</c>).</summary>
    public PricingTier? ExplicitTier { get; init; }

    /// <summary>Host chmurowy wiersza bez prefiksu (np. Bedrock dla <c>us.anthropic.…</c>), indeks <see cref="PricingData.Hosts"/>.</summary>
    internal int? Host { get; init; }

    public bool IsPriced => Base.Input is not null || Base.Output is not null;

    /// <summary>Darmowy wiersz katalogu (0/0); nadpisanie użytkownika nigdy nie jest „darmowe”.</summary>
    public bool IsFree => Source != PricingSource.Override && (Base.Input ?? 0) == 0 && (Base.Output ?? 0) == 0;

    /// <summary>Czy wynik zależy od daty wpisu.</summary>
    public bool IsTimeDependent => Schedule is not null;

    /// <summary>Identyfikator modelu u dostawcy (klucz bez prefiksu <c>dostawca/</c>) dla wierszy bezpośrednich.</summary>
    internal string? VendorId
    {
        get
        {
            if (IsDirect && IsFree)
            {
                return PricingKeys.LastSegment(Key);
            }

            if (!IsDirect || Provider is null)
            {
                return null;
            }

            return Key.StartsWith(Provider + "/", StringComparison.OrdinalIgnoreCase) ? Key[(Provider.Length + 1)..] : null;
        }
    }

    internal bool IsBareHosted => !IsDirect && Source == PricingSource.LiteLlm && !Key.Contains('/');

    internal bool IsBedrock =>
        Host == PricingData.BedrockHost ||
        (!IsDirect && Key.Contains('/') && (Key.StartsWith("bedrock_mantle/", StringComparison.OrdinalIgnoreCase) ||
                                            PricingData.HostListingIndex(Key[..Key.IndexOf('/')]) == PricingData.BedrockHost));

    public CacheDefaults CacheDefaults => CacheDefaults.For(Key, Provider);

    /// <summary>Stawki obowiązujące w chwili <paramref name="timestampMs"/> (harmonogram dostawcy; jawne nadpisania mają pierwszeństwo).</summary>
    public ModelPricing At(long timestampMs) => Schedule is null ? this : Schedule.ApplyTo(this, timestampMs);

    /// <summary>Tworzy cennik z obiektu w formacie LiteLLM (<c>input_cost_per_token</c>, <c>..._above_200k_tokens</c>, <c>litellm_provider</c>…).</summary>
    public static ModelPricing FromLiteLlm(string key, JsonElement element)
    {
        var baseRates = new Dictionary<string, double>();
        var tiers = new Dictionary<long, Dictionary<string, double>>();
        long? maxInput = null;
        double? fast = null;
        double? flex = null;
        string? provider = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "litellm_provider" && property.Value.ValueKind == JsonValueKind.String)
            {
                provider = property.Value.GetString();
                continue;
            }

            if (property.Name == "provider_specific_entry" && property.Value.ValueKind == JsonValueKind.Object)
            {
                fast = Number(property.Value, "fast") ?? fast;
                flex = Number(property.Value, "flex") ?? flex;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value))
            {
                continue;
            }

            switch (property.Name)
            {
                case "max_input_tokens":
                    maxInput = (long)value;
                    continue;
                case "fast_multiplier":
                    fast = value;
                    continue;
                case "flex_multiplier":
                    flex = value;
                    continue;
            }

            var match = FieldRegex().Match(property.Name);
            if (!match.Success)
            {
                continue;
            }

            var hour = match.Groups["hour"].Success;
            if (hour && match.Groups["kind"].Value != "cache_creation_input_token_cost")
            {
                continue;
            }

            var kind = match.Groups["kind"].Value switch
            {
                "input_cost_per_token" => "in",
                "output_cost_per_token" => "out",
                "cache_read_input_token_cost" => "cr",
                _ => hour ? "cw1h" : "cw"
            };

            if (match.Groups["tier"].Success)
            {
                var threshold = long.Parse(match.Groups["tier"].Value) * 1000;
                if (!tiers.TryGetValue(threshold, out var tier))
                {
                    tiers[threshold] = tier = new Dictionary<string, double>();
                }

                tier[kind] = value;
            }
            else
            {
                baseRates[kind] = value;
            }
        }

        var isDirect = provider is not null && PricingData.DirectProviders.Contains(provider);
        var wholeRequest = PricingData.WholeRequestThreshold(key);
        var pricing = new ModelPricing
        {
            Key = key,
            Base = ToRates(baseRates),
            Tiers = tiers.OrderBy(pair => pair.Key).Select(pair =>
            {
                // ccusage litellm.rs: pola _above_200k_tokens są krańcowe, chyba że rodzina rozlicza całe żądanie (Claude, Gemini, Grok).
                // Inne progi (np. 272k GPT-5.x) ccusage bierze z models.dev jako próg całego żądania.
                var marginal = pair.Key == 200_000 && wholeRequest is null;
                var threshold = pair.Key == 200_000 && wholeRequest is { } whole ? whole : pair.Key;
                return new PricingTier(threshold, ToRates(pair.Value)) { Marginal = marginal };
            }).ToArray(),
            MaxInputTokens = maxInput,
            FastMultiplier = fast,
            FlexMultiplier = flex,
            Provider = provider,
            IsDirect = isDirect,
            Host = !isDirect && provider is not null && !key.Contains('/')
                ? Array.FindIndex(PricingData.Hosts, host => host.BareProviders.Contains(provider)) is var index and >= 0 ? index : null
                : null
        };

        return pricing.IsDirect ? pricing with { Schedule = PricingSchedules.For(pricing.VendorId ?? key) } : pricing;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static RateSet ToRates(Dictionary<string, double> values) => new()
    {
        Input = values.TryGetValue("in", out var input) ? input : null,
        Output = values.TryGetValue("out", out var output) ? output : null,
        CacheWrite = values.TryGetValue("cw", out var write) ? write : null,
        CacheWrite1h = values.TryGetValue("cw1h", out var write1h) ? write1h : null,
        CacheRead = values.TryGetValue("cr", out var read) ? read : null
    };

    [GeneratedRegex("^(?<kind>input_cost_per_token|output_cost_per_token|cache_creation_input_token_cost|cache_read_input_token_cost)(?<hour>_above_1hr)?(_above_(?<tier>\\d+)k_tokens)?$")]
    private static partial Regex FieldRegex();
}
