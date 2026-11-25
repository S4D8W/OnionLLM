using System.Text.Json;

namespace OnionLLM.Core.Pricing;

/// <summary>
/// Drugie źródło cen: models.dev (ccusage <c>pricing/models_dev.rs</c>, <c>models_dev/catalog.rs</c>). Wiersze są używane tylko wtedy,
/// gdy LiteLLM (i ceny wbudowane) nie zna modelu. Obsługuje dwa formaty: snapshot ccusage (<c>klucz → {cost, limit, provider, exactOnly}</c>,
/// ceny za 1M tokenów) oraz <c>https://models.dev/api.json</c> (<c>dostawca → {models}</c>).
/// </summary>
internal static class ModelsDev
{
    public const string ApiUrl = "https://models.dev/api.json";

    private const int TrustOwner = 3;
    private const int TrustPlatform = 2;
    private const int TrustReseller = 1;

    private sealed class CatalogRules
    {
        public HashSet<string> Owners { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Platforms { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AuthoredModelIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AssetPricedModelIds { get; } = new(StringComparer.Ordinal);
        public List<string> NormalizedAuthored { get; } = [];
    }

    private static readonly Lazy<CatalogRules> Rules = new(LoadRules);

    /// <summary>Segment oznaczający autora modelu lub dostawcę bezpośredniego (nie hosta), ccusage <c>is_vendor_segment</c>.</summary>
    public static bool IsVendorSegment(string segment)
    {
        var dashed = segment.ToLowerInvariant().Replace('_', '-');
        return PricingData.DirectProviders.Any(id => PricingData.SameHostSpelling(id, segment)) ||
               PricingData.DirectBillingProviders.Any(pair => pair.Provider == dashed) ||
               Rules.Value.Owners.Contains(dashed) || Rules.Value.Owners.Contains(segment);
    }

    /// <summary>Platformy models.dev (Bedrock, Azure, Vertex) — hosty pobierające opłaty.</summary>
    public static IReadOnlyCollection<string> Platforms => Rules.Value.Platforms;

    public static Dictionary<string, ModelPricing> Parse(JsonElement root)
    {
        var result = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var entries = root.EnumerateObject().ToArray();
        if (entries.Length == 0)
        {
            return result;
        }

        var providersFormat = entries.Any(entry => entry.Value.ValueKind == JsonValueKind.Object &&
                                                   entry.Value.TryGetProperty("models", out var models) &&
                                                   models.ValueKind == JsonValueKind.Object);
        if (!providersFormat)
        {
            foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                if (ToPricing(entry.Name, entry.Value, deriveExactOnly: false) is { } row)
                {
                    result.TryAdd(row.Key, row);
                }
            }

            return Curated(result);
        }

        var rules = Rules.Value;
        var claims = new Dictionary<string, (Claim Claim, string StoredId, string Provider, string SourceKey)>(StringComparer.Ordinal);
        var providers = entries
            .Where(entry => entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("models", out _))
            .Select(entry => (Id: entry.Value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : entry.Name,
                Key: entry.Name, Value: entry.Value))
            .OrderByDescending(provider => Rank(provider.Id))
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ThenBy(provider => provider.Key, StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            var trust = Rank(provider.Id);
            var models = provider.Value.GetProperty("models");
            if (models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var model in models.EnumerateObject().OrderBy(model => model.Name, StringComparer.Ordinal))
            {
                if (ToPricing(model.Name, model.Value, deriveExactOnly: true) is not { } row)
                {
                    continue;
                }

                var normalized = NormalizedId(row.Key);
                var claimed = claims.TryGetValue(normalized, out var previous);
                if (!claimed && result.ContainsKey(row.Key))
                {
                    continue;
                }

                // Lepsze roszczenie wygrywa: zaufanie (autor > platforma > odsprzedawca), próg, stawki cache, limit kontekstu.
                var claim = new Claim(trust, row.Tiers.Count > 0, row.Base.CacheRead is not null, row.Base.CacheWrite is not null, row.MaxInputTokens is not null);
                if (claimed)
                {
                    var order = previous.Claim.CompareTo(claim);
                    var replacesEqual = order == 0 && previous.Provider == provider.Id &&
                                        string.CompareOrdinal(model.Name, previous.SourceKey) < 0;
                    if (order > 0 || (order == 0 && !replacesEqual))
                    {
                        continue;
                    }

                    if (!previous.StoredId.Equals(row.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Remove(previous.StoredId);
                    }
                }

                result[row.Key] = row;
                claims[normalized] = (claim, row.Key, provider.Id, model.Name);
            }
        }

        return Curated(result);
    }

    private readonly record struct Claim(int Trust, bool HasTier, bool HasCacheRead, bool HasCacheWrite, bool HasContextLimit) : IComparable<Claim>
    {
        public int CompareTo(Claim other)
        {
            var result = Trust.CompareTo(other.Trust);
            if (result == 0) result = HasTier.CompareTo(other.HasTier);
            if (result == 0) result = HasCacheRead.CompareTo(other.HasCacheRead);
            if (result == 0) result = HasCacheWrite.CompareTo(other.HasCacheWrite);
            if (result == 0) result = HasContextLimit.CompareTo(other.HasContextLimit);
            return result;
        }
    }

    private static Dictionary<string, ModelPricing> Curated(Dictionary<string, ModelPricing> rows)
    {
        foreach (var key in rows.Keys.ToArray())
        {
            if (PricingSchedules.Curate(rows[key]) is { } curated)
            {
                rows[key] = curated;
            }
        }

        return rows;
    }

    private static ModelPricing? ToPricing(string sourceKey, JsonElement model, bool deriveExactOnly)
    {
        if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!IsTokenPriced(sourceKey, model))
        {
            return null;
        }

        var input = Number(cost, "input");
        var output = Number(cost, "output");
        if (input is null || output is null || (input == 0 && output == 0))
        {
            return null;
        }

        var declaredId = model.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String &&
                         idElement.GetString() is { Length: > 0 } id ? id : null;
        var modelId = declaredId ?? sourceKey;
        var exactOnly = model.TryGetProperty("exactOnly", out var exact) && exact.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? exact.GetBoolean()
            : deriveExactOnly && IsExactOnly(sourceKey, modelId);

        PricingTier? tier = null;
        if (cost.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in tiers.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("tier", out var bound) ||
                    bound.ValueKind != JsonValueKind.Object ||
                    !(bound.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "context") ||
                    Number(bound, "size") is not { } size || size <= 0)
                {
                    continue;
                }

                if (tier is null || size < tier.AboveTokens)
                {
                    tier = new PricingTier((long)size, new RateSet
                    {
                        Input = PerToken(Number(candidate, "input")),
                        Output = PerToken(Number(candidate, "output")),
                        CacheWrite = PerToken(Number(candidate, "cache_write")),
                        CacheRead = PerToken(Number(candidate, "cache_read"))
                    });
                }
            }
        }

        long? context = model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object && Number(limit, "context") is { } value
            ? (long)value
            : null;

        return new ModelPricing
        {
            Key = modelId,
            Base = new RateSet
            {
                Input = PerToken(input),
                Output = PerToken(output),
                CacheWrite = PerToken(Number(cost, "cache_write")),
                CacheRead = PerToken(Number(cost, "cache_read"))
            },
            Tiers = tier is null ? [] : [tier],
            MaxInputTokens = context,
            Source = PricingSource.ModelsDev,
            // ccusage: wiersze models.dev mają zakres Direct, ale bez dostawcy LiteLLM (domyślne stawki cache wg rodziny modelu).
            IsDirect = true,
            ExactOnly = exactOnly
        };
    }

    // NOTE(ccusage #1541): models.dev publikuje ceny za 1M tokenów.
    private static double? PerToken(double? perMillion) => perMillion / 1_000_000.0;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int Rank(string providerId) =>
        Rules.Value.Owners.Contains(providerId) ? TrustOwner : Rules.Value.Platforms.Contains(providerId) ? TrustPlatform : TrustReseller;

    private static bool IsExactOnly(string sourceModelId, string pricingKey)
    {
        // Wariant (tier) modelu autora albo ID bez numeru wersji — tylko dokładne dopasowanie.
        if (!sourceModelId.Contains('/'))
        {
            var normalized = NormalizedId(sourceModelId);
            if (Rules.Value.NormalizedAuthored.Any(authored => normalized.StartsWith(authored + "-", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return !pricingKey.Any(char.IsAsciiDigit);
    }

    private static bool IsTokenPriced(string sourceModelId, JsonElement model)
    {
        var normalized = NormalizedId(sourceModelId);
        if (Rules.Value.AssetPricedModelIds.Contains(normalized))
        {
            return false;
        }

        if (Rules.Value.AuthoredModelIds.Contains(normalized) ||
            !model.TryGetProperty("modalities", out var modalities) || modalities.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        static string[]? List(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : null;

        var output = List(modalities, "output");
        var input = List(modalities, "input");
        var textOnlyOutput = output is null || (output.Length == 1 && output[0] == "text");
        var acceptsText = input is null || input.Contains("text");
        return textOnlyOutput && acceptsText;
    }

    private static string NormalizedId(string modelId) => PricingKeys.Identity(modelId);

    private static CatalogRules LoadRules()
    {
        var rules = new CatalogRules();
        using var stream = typeof(ModelsDev).Assembly.GetManifestResourceStream("OnionLLM.Core.models-dev-catalog-rules.json");
        if (stream is null)
        {
            return rules;
        }

        using var document = JsonDocument.Parse(stream);
        void Fill(string name, HashSet<string> target)
        {
            if (document.RootElement.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        target.Add(item.GetString()!);
                    }
                }
            }
        }

        Fill("owners", rules.Owners);
        Fill("platforms", rules.Platforms);
        Fill("authoredModelIds", rules.AuthoredModelIds);
        Fill("assetPricedModelIds", rules.AssetPricedModelIds);
        rules.NormalizedAuthored.AddRange(rules.AuthoredModelIds.Select(NormalizedId).Order(StringComparer.Ordinal));
        return rules;
    }
}
