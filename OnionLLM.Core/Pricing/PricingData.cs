namespace OnionLLM.Core.Pricing;

/// <summary>Mnożniki stawek cache względem stawki wejścia, gdy wiersz cennika ich nie podaje.</summary>
public sealed record CacheDefaults(double WriteX, double Write1hX, double ReadX)
{
    /// <summary>Domyślne mnożniki dla klucza (rodzina modelu), potem dostawcy LiteLLM; w ostateczności Anthropic (ccusage #2258).</summary>
    public static CacheDefaults For(string key, string? provider) =>
        PricingData.ModelCacheDefaults(key) ?? PricingData.ProviderCacheDefaults(provider);
}

/// <summary>
/// Kuratorskie dane cennika przeniesione z ccusage <c>ccusage-core/src/pricing/data.rs</c>
/// (domyślne stawki cache, słowa neutralne cenowo, hosty, drogi rozliczenia, progi długiego kontekstu).
/// </summary>
internal static class PricingData
{
    // NOTE(ccusage 2026-11-20): OpenAI — brak dopłaty za zapis, odczyt 0,1×.
    public static readonly CacheDefaults WritesAtInputRate = new(1.0, 1.0, 0.1);

    // Anthropic: zapis 5 min 1,25×, 1 h 2×, odczyt 0,1×.
    public static readonly CacheDefaults WritesSurcharged = new(1.25, 2.0, 0.1);

    private static readonly CacheDefaults Anthropic55 = WritesSurcharged with { ReadX = 0.05 };
    private static readonly CacheDefaults AnthropicFableMythos5 = WritesSurcharged with { ReadX = 0.025 };

    // OpenAI od GPT-5.6: zapis 1,25×, zapis długi 2,5×, odczyt 0,1×.
    private static readonly CacheDefaults OpenAiGpt56 = new(1.25, 2.5, 0.1);
    private static readonly CacheDefaults OpenAiGpt6ReducedRead = OpenAiGpt56 with { ReadX = 0.05 };
    private static readonly CacheDefaults Gemini = new(1.0, 1.0, 0.1);
    private static readonly CacheDefaults Gemini4 = Gemini with { ReadX = 0.05 };

    // DeepSeek: zapis po stawce wejścia, odczyt 0,02× (szacunek z V4 Flash).
    private static readonly CacheDefaults DeepSeek = new(1.0, 1.0, 0.02);
    private static readonly CacheDefaults Xai = new(1.0, 1.0, 0.25);
    private static readonly CacheDefaults XaiGrok45 = Xai with { ReadX = 0.15 };
    private static readonly CacheDefaults MoonshotKimiK3 = new(1.0, 2.0, 0.1);
    private static readonly CacheDefaults ZaiGlm = new(1.0, 1.0, 0.2);

    // Alibaba (Qwen): niejawny cache — zapis 100%, odczyt 20%.
    private static readonly CacheDefaults Qwen = new(1.0, 1.0, 0.2);
    private static readonly CacheDefaults MiniMax = new(1.0, 1.0, 0.2);
    private static readonly CacheDefaults Mistral = new(1.0, 1.0, 0.1);
    private static readonly CacheDefaults AmazonNova = new(1.0, 1.0, 0.25);

    private static readonly (string Prefix, CacheDefaults Defaults)[] CacheDefaultsBySegmentPrefix =
    [
        ("claude-fable-5-1", AnthropicFableMythos5), ("claude-fable-5-2", AnthropicFableMythos5),
        ("claude-mythos-5-1", AnthropicFableMythos5), ("claude-mythos-5-2", AnthropicFableMythos5),
        ("claude-opus-5-5", Anthropic55), ("claude-sonnet-5-5", Anthropic55),
        ("claude-", WritesSurcharged),
        ("gpt-6-2-astra", OpenAiGpt6ReducedRead), ("gpt-6-2-sol", OpenAiGpt6ReducedRead),
        ("gpt-6-1-sol", OpenAiGpt6ReducedRead), ("gpt-6-1-terra", OpenAiGpt6ReducedRead),
        ("openai-gpt-6-2-astra", OpenAiGpt6ReducedRead), ("openai-gpt-6-2-sol", OpenAiGpt6ReducedRead),
        ("openai-gpt-6-1-sol", OpenAiGpt6ReducedRead), ("openai-gpt-6-1-terra", OpenAiGpt6ReducedRead),
        ("gpt-5-6", OpenAiGpt56), ("gpt-5-7", OpenAiGpt56), ("gpt-5-8", OpenAiGpt56), ("gpt-5-9", OpenAiGpt56), ("gpt-6", OpenAiGpt56),
        ("openai-gpt-5-6", OpenAiGpt56), ("openai-gpt-5-7", OpenAiGpt56), ("openai-gpt-5-8", OpenAiGpt56),
        ("openai-gpt-5-9", OpenAiGpt56), ("openai-gpt-6", OpenAiGpt56),
        ("codex-", WritesAtInputRate), ("gpt-", WritesAtInputRate), ("chatgpt-", WritesAtInputRate),
        ("o1-", WritesAtInputRate), ("o3-", WritesAtInputRate), ("o4-", WritesAtInputRate),
        ("openai-codex-", WritesAtInputRate), ("openai-gpt-", WritesAtInputRate), ("openai-chatgpt-", WritesAtInputRate),
        ("openai-o1-", WritesAtInputRate), ("openai-o3-", WritesAtInputRate), ("openai-o4-", WritesAtInputRate),
        ("gemini-4", Gemini4), ("gemini-", Gemini),
        ("deepseek-", DeepSeek),
        ("grok-4-5", XaiGrok45), ("grok-", Xai),
        ("kimi-k3", MoonshotKimiK3),
        ("glm-", ZaiGlm),
        ("qwen", Qwen),
        ("minimax-m", MiniMax),
        ("mistral-", Mistral), ("devstral-", Mistral), ("codestral-", Mistral), ("ministral-", Mistral),
        ("magistral-", Mistral), ("pixtral-", Mistral),
        ("nova-", AmazonNova)
    ];

    private static readonly (string Segment, CacheDefaults Defaults)[] CacheDefaultsBySegment =
    [
        ("o1", WritesAtInputRate), ("o3", WritesAtInputRate), ("o4", WritesAtInputRate),
        ("openai-o1", WritesAtInputRate), ("openai-o3", WritesAtInputRate), ("openai-o4", WritesAtInputRate)
    ];

    private static readonly (string Provider, CacheDefaults Defaults)[] CacheDefaultsByProvider =
    [
        ("openai", WritesAtInputRate), ("text-completion-openai", WritesAtInputRate), ("azure", WritesAtInputRate),
        ("azure_text", WritesAtInputRate), ("anthropic", WritesSurcharged), ("bedrock", WritesSurcharged),
        ("bedrock_converse", WritesSurcharged), ("vertex_ai-anthropic_models", WritesSurcharged)
    ];

    /// <summary>
    /// Słowa po nazwie modelu, które nie zmieniają ceny: poziom effort, <c>latest</c>/<c>default</c>, <c>:beta</c> (ccusage #2236).
    /// </summary>
    public static readonly string[] PriceNeutralSuffixWords =
        ["minimal", "low", "medium", "high", "xhigh", "extra-low", "extra-high", "latest", "default", "beta"];

    /// <summary>Rodziny, które rozliczają tryb thinking/reasoning po stawkach modelu (ccusage <c>FAMILY_PRICE_NEUTRAL_SUFFIX_WORDS</c>).</summary>
    public static readonly (string Family, string[] Words)[] FamilyPriceNeutralSuffixWords =
    [
        ("claude-", ["thinking"]), ("gemini-", ["thinking"]), ("deepseek-", ["thinking"]), ("glm-", ["thinking"]),
        ("grok-", ["non-reasoning", "reasoning"]), ("kimi-", ["thinking"]), ("mistral-", ["thinking"]),
        // Qwen: tylko modele z jedną ceną wyjścia; qwen-plus, qwen-turbo i otwarte qwen3-8b/14b/30b-a3b/235b-a22b liczą thinking drożej.
        ("qwen4-max", ["thinking"]), ("qwen3.8-max", ["thinking"]), ("qwen3.7-max", ["thinking"]), ("qwen3.6-max", ["thinking"]),
        ("qwen3-max", ["thinking"]), ("qwen3.7-plus", ["thinking"]), ("qwen3.6-plus", ["thinking"]), ("qwen3.5-plus", ["thinking"]),
        ("qwen3.8-flash", ["thinking"]), ("qwen3.7-flash", ["thinking"]), ("qwen3.6-flash", ["thinking"]), ("qwen3.5-flash", ["thinking"]),
        ("qwen-flash", ["thinking"]), ("qwen3-vl-plus", ["thinking"]), ("qwen3-vl-flash", ["thinking"]), ("qwen3.8-27b", ["thinking"]),
        ("qwen3.6-35b-a3b", ["thinking"]), ("qwen3.6-27b", ["thinking"]), ("qwen3.5-397b-a17b", ["thinking"]),
        ("qwen3.5-122b-a10b", ["thinking"]), ("qwen3.5-35b-a3b", ["thinking"]), ("qwen3.5-27b", ["thinking"]), ("qwen3-32b", ["thinking"])
    ];

    /// <summary>Wbudowane aliasy cennika (ccusage <c>ALIASES</c>); porównanie z <c>.</c>/<c>@</c> = <c>-</c>.</summary>
    public static readonly (string Model, string Alias)[] Aliases =
    [
        ("gpt-reserve", "gpt-5.6-luna"),
        ("gpt-5.6", "gpt-5.6-sol"),
        ("gpt-5.3-spark", "gpt-5.3-codex-spark")
    ];

    /// <summary>Dostawcy LiteLLM będący API samego autora modelu (ccusage <c>litellm-sources.json</c> <c>directProviders</c>).</summary>
    public static readonly string[] DirectProviders =
        ["anthropic", "openai", "text-completion-openai", "deepseek", "zai", "moonshot", "xai", "dashscope", "gemini"];

    /// <summary>Wiersze 0/0 od tych dostawców nie są ceną za token (Gemma, Lyria) — pomijane (ccusage #2226).</summary>
    public static readonly string[] ZeroTokenRateUnpricedProviders = ["gemini"];

    /// <summary>Host chmurowy: zapisy w logach, prefiksy kluczy LiteLLM, dostawcy wierszy bez prefiksu (ccusage <c>HOSTS</c>, #2229).</summary>
    public sealed record HostListing(string[] Spellings, string[] Prefixes, string[] BareProviders, string[] ModelIdWords);

    public static readonly HostListing[] Hosts =
    [
        new(["bedrock", "amazon_bedrock", "aws_bedrock", "bedrock_converse"], ["bedrock", "bedrock_converse"], ["bedrock_converse", "bedrock"],
            ["ai21", "amazon", "anthropic", "apac", "au", "ca", "cohere", "deepseek", "eu", "global", "google", "in", "jp", "meta", "minimax",
             "mistral", "moonshot", "moonshotai", "nvidia", "openai", "qwen", "stability", "twelvelabs", "us", "us-gov", "writer", "xai", "zai"]),
        new(["azure", "azure_openai"], ["azure"], [], []),
        new(["vertex_ai", "google_vertex"], ["vertex_ai"], [], [])
    ];

    public const int BedrockHost = 0;

    /// <summary>Google zapisuje swoje API jako <c>google/…</c>, LiteLLM jako <c>gemini/…</c> (ccusage <c>VENDOR_SPELLINGS</c>).</summary>
    public static readonly (string Spelling, string Vendor)[] VendorSpellings = [("google", "gemini")];

    /// <summary>Identyfikatory dostawców oznaczające API samego dostawcy → jego harmonogramy cen (ccusage <c>DIRECT_BILLING_PROVIDERS</c>, #2455).</summary>
    public static readonly (string Provider, string Vendor)[] DirectBillingProviders =
    [
        ("deepseek", "deepseek"), ("deepseek-official", "deepseek"), ("deepseek-account", "deepseek"), ("xai", "xai"),
        ("anthropic", "anthropic"), ("google", "google"), ("gemini", "google"), ("zai-coding-plan", "zai")
    ];

    /// <summary>Hosty stosujące datowane zmiany cen dostawcy, gdy rozliczenie spada na wiersz dostawcy (Vertex AI → Google).</summary>
    public static readonly (string Provider, string Vendor)[] HostsFollowingVendorPrices = [("vertex-ai", "google")];

    /// <summary>Rodziny modeli, których próg długiego kontekstu obejmuje całe żądanie (ccusage <c>WHOLE_REQUEST_LONG_CONTEXT_VENDORS</c>).</summary>
    public static readonly (string[] Families, string[] Namespaces, long Threshold)[] WholeRequestVendors =
    [
        (["claude", "anthropic-claude", "anthropic--claude", "databricks-claude"], ["anthropic"], 200_000),
        (["gemini", "google-gemini", "databricks-gemini"], ["gemini", "google"], 200_000),
        // xAI: żądanie od 200 000 tokenów jest długie (#2227).
        (["grok", "grok4", "xai-grok", "x-ai-grok", "spacexai-grok", "databricks-grok"], ["xai"], 199_999)
    ];

    public static CacheDefaults? ModelCacheDefaults(string model)
    {
        (int Length, CacheDefaults Defaults)? best = null;
        foreach (var part in model.Split('/'))
        {
            var suffixes = new List<string> { part };
            for (var dot = part.IndexOf('.'); dot >= 0; dot = part.IndexOf('.', dot + 1))
            {
                suffixes.Add(part[(dot + 1)..]);
            }

            foreach (var suffix in suffixes)
            {
                var identity = PricingKeys.Identity(suffix);
                (int, CacheDefaults)? found = null;
                foreach (var (prefix, defaults) in CacheDefaultsBySegmentPrefix)
                {
                    if (identity.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        found = (prefix.Length, defaults);
                        break;
                    }
                }

                if (found is null)
                {
                    foreach (var (segment, defaults) in CacheDefaultsBySegment)
                    {
                        if (identity == segment)
                        {
                            found = (segment.Length, defaults);
                            break;
                        }
                    }
                }

                // Najniższy mnożnik zapisu wygrywa, przy remisie dłuższy prefiks (ccusage model_cache_defaults).
                if (found is { } candidate &&
                    (best is null || candidate.Item2.WriteX < best.Value.Defaults.WriteX ||
                     (candidate.Item2.WriteX == best.Value.Defaults.WriteX && candidate.Item1 > best.Value.Length)))
                {
                    best = candidate;
                }
            }
        }

        return best?.Defaults;
    }

    public static CacheDefaults ProviderCacheDefaults(string? provider)
    {
        foreach (var (key, defaults) in CacheDefaultsByProvider)
        {
            if (key == provider)
            {
                return defaults;
            }
        }

        return WritesSurcharged;
    }

    /// <summary>Próg całego żądania dla rodziny modelu albo null (rozliczenie krańcowe), ccusage <c>whole_request_threshold</c>.</summary>
    public static long? WholeRequestThreshold(string model)
    {
        var segment = PricingKeys.LastSegment(model);
        var parts = new List<string> { segment };
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] is '.' or '@')
            {
                parts.Add(segment[(index + 1)..]);
            }
        }

        foreach (var part in parts)
        {
            foreach (var (families, _, threshold) in WholeRequestVendors)
            {
                foreach (var family in families)
                {
                    if (part.Length > family.Length && part.StartsWith(family, StringComparison.OrdinalIgnoreCase) &&
                        part[family.Length] is '-' or '.' or '@')
                    {
                        return threshold;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Nadpisanie progów: rodzina lub przestrzeń nazw dostawcy rozliczającego całe żądanie.</summary>
    public static bool IsWholeRequestOverrideModel(string model)
    {
        if (WholeRequestThreshold(model) is not null)
        {
            return true;
        }

        var slash = model.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var space = PricingKeys.LastSegment(model[..slash]);
        return WholeRequestVendors.Any(vendor => vendor.Namespaces.Contains(space, StringComparer.OrdinalIgnoreCase));
    }

    public static string? PricingAlias(string model)
    {
        var identity = PricingKeys.Identity(model);
        foreach (var (key, alias) in Aliases)
        {
            if (PricingKeys.Identity(key) == identity)
            {
                return alias;
            }
        }

        return null;
    }

    public static string? VendorSpelling(string model)
    {
        foreach (var (spelling, vendor) in VendorSpellings)
        {
            if (model.StartsWith(spelling + "/", StringComparison.OrdinalIgnoreCase))
            {
                return $"{vendor}/{model[(spelling.Length + 1)..]}";
            }
        }

        return null;
    }

    /// <summary>Host (indeks w <see cref="Hosts"/>) dla zapisu segmentu; <c>-</c> i <c>_</c> są równoważne.</summary>
    public static int? HostListingIndex(string segment)
    {
        for (var index = 0; index < Hosts.Length; index++)
        {
            if (Hosts[index].Spellings.Any(spelling => SameHostSpelling(segment, spelling)))
            {
                return index;
            }
        }

        return null;
    }

    public static bool SameHostSpelling(string left, string right) =>
        left.Length == right.Length && left.Replace('-', '_').Equals(right.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);
}
