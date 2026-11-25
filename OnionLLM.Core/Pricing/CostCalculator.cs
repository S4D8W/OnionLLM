using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Pricing;

/// <param name="StoredCostUsd">Część <paramref name="CostUsd"/> pochodząca z kosztu zapisanego przez narzędzie (ccusage <c>storedCost</c>).</param>
/// <param name="IsEstimated">Cena szacowana (ccusage <c>estimated</c>, #2228) albo model przyjęty domyślnie (<see cref="UsageEntry.IsFallbackModel"/>).</param>
public readonly record struct CostResult(double CostUsd, bool IsUnpriced, bool IsEstimated, double StoredCostUsd = 0);

/// <summary>
/// Wylicza koszt wpisu (ccusage <c>ccusage-core/src/cost.rs</c>).
/// <list type="bullet">
/// <item>Cennik: kandydaci nazw ze źródła, dostawca z logu jako droga rozliczenia, harmonogram cen w chwili wpisu.</item>
/// <item>Długi kontekst: próg całego żądania (Anthropic, Google, xAI, progi models.dev) albo krańcowy (pola <c>_above_200k</c> innych
/// dostawców). Wpis łączący wiele żądań — bez progu (#2365).</item>
/// <item>Brakujące stawki cache: mnożniki dostawcy/rodziny (<see cref="CacheDefaults"/>).</item>
/// <item>Fast/flex/ultrafast: mnożnik z cennika lub tabel ccusage (modele spoza tabel 1×); fast pomija próg tylko tam, gdzie
/// harmonogram tak stanowi (Anthropic 1M w becie).</item>
/// </list>
/// </summary>
/// <param name="sources">Źródła z regułami cennika (<see cref="IUsageSource.PricingCandidates"/>, <see cref="IUsageSource.AdjustPricing"/>);
/// domyślnie <see cref="SourceRegistry.Find"/>.</param>
public sealed class CostCalculator(PricingCatalog catalog, CostMode mode, Func<string, IUsageSource?>? sources = null)
{
    /// <summary>
    /// Źródła, dla których zapisany koszt 0 jest prawdziwy (darmowe użycie). Kilo, Hermes i OpenCode zapisują 0 także przy braku danych
    /// (ccusage <c>recorded_cost</c>, #2199) — tam i w pozostałych źródłach 0 oznacza brak kosztu.
    /// </summary>
    private static readonly HashSet<string> ZeroCostIsRecorded = new(StringComparer.OrdinalIgnoreCase) { "claude", "amp", "cline", "openclaw", "pi" };

    private readonly Func<string, IUsageSource?> _sources = sources ?? SourceRegistry.Find;

    public PricingCatalog Catalog { get; } = catalog;

    public CostMode Mode { get; } = mode;

    public CostResult Calculate(UsageEntry entry)
    {
        var recorded = RecordedCost(entry);

        switch (Mode)
        {
            case CostMode.Display:
                return new CostResult(recorded ?? 0, false, false);
            case CostMode.Auto when recorded is not null:
                return new CostResult(recorded.Value, false, false, recorded.Value);
        }

        var pricing = ResolvePricing(entry);
        if (pricing is null || !pricing.IsPriced)
        {
            // W trybie calculate model bez ceny = 0 i flaga; w auto zostaje zapisany koszt, jeśli był.
            return new CostResult(Mode == CostMode.Calculate ? 0 : recorded ?? 0, entry.TotalTokens > 0, false);
        }

        return new CostResult(Price(entry, pricing), false, pricing.IsEstimated || entry.IsFallbackModel);
    }

    private double? RecordedCost(UsageEntry entry)
    {
        if (entry.RecordedCostUsd is not { } cost || !double.IsFinite(cost) || cost < 0 ||
            (cost == 0 && !ZeroCostIsRecorded.Contains(entry.Source)))
        {
            return null;
        }

        // ccusage pi/src/parser.rs:463-472: nadpisanie ceny modelu unieważnia koszt zapisany przez pi (tryb auto).
        if (Mode == CostMode.Auto && entry.Source.Equals("pi", StringComparison.OrdinalIgnoreCase) && Catalog.HasOverride(entry.Model, entry.Source))
        {
            return null;
        }

        return cost;
    }

    /// <summary>Cennik wpisu w chwili wpisu: kandydaci źródła (po aliasach zależnych od daty), droga rozliczenia z dostawcy, korekta źródła.</summary>
    public ModelPricing? ResolvePricing(UsageEntry entry)
    {
        var source = _sources(entry.Source);
        var candidates = (source?.PricingCandidates(entry) ?? (entry.Model is null ? [] : [entry.Model]))
            .Select(candidate => ModelAliases.ForPricing(candidate, entry.TimestampMs))
            .OfType<string>()
            .ToArray();
        // Źródło może odmówić wyceny z katalogu (np. ZCode u dostawcy spoza Z.ai), ale jawne nadpisanie użytkownika
        // nadal obowiązuje — jak w ccusage zcode/src/parser.rs (wycena tylko z pricingOverrides).
        if (candidates.Length == 0 && entry.Model is not null && Catalog.HasOverride(entry.Model, entry.Source))
        {
            candidates = [entry.Model];
        }

        var resolution = Catalog.ResolveAt(candidates, entry.Model, entry.Source, entry.Provider, entry.TimestampMs);
        if (resolution is null)
        {
            return null;
        }

        return source?.AdjustPricing(entry, resolution.Pricing) ?? resolution.Pricing;
    }

    public static double Price(UsageEntry entry, ModelPricing pricing)
    {
        var multiplier = entry.Speed switch
        {
            SpeedTier.Fast => PricingCatalog.FastMultiplier(pricing),
            SpeedTier.Flex => PricingCatalog.FlexMultiplier(pricing),
            SpeedTier.Ultrafast => PricingCatalog.UltrafastMultiplier(pricing),
            _ => 1.0
        };

        IReadOnlyList<PricingTier> tiers = pricing.Tiers;
        if (entry.SpansMultipleRequests)
        {
            // Wpis łączący wiele żądań nie mówi, które z nich przekroczyło próg — stawka bazowa (ccusage #2365).
            tiers = [];
        }
        else if (entry.Speed == SpeedTier.Fast && pricing.FlatLongContextInFastMode)
        {
            // Fast nie płacił dopłaty za 1M w becie (ccusage #2451); zostaje tylko próg z nadpisania.
            tiers = pricing.ExplicitTier is { } own ? [own] : [];
        }

        var defaults = pricing.CacheDefaults;
        var input = pricing.Base.Input ?? 0;
        var output = pricing.Base.Output ?? 0;
        var cacheWrite = pricing.Base.CacheWrite ?? input * defaults.WriteX;
        var cacheWrite1h = pricing.Base.CacheWrite1h ?? input * defaults.Write1hX;
        var cacheRead = pricing.Base.CacheRead ?? input * defaults.ReadX;

        var cache1h = Math.Min(entry.CacheCreation1hTokens, entry.CacheCreationTokens);
        var cache5m = entry.CacheCreationTokens - cache1h;
        var context = entry.InputTokens + entry.CacheCreationTokens + entry.CacheReadTokens;

        // Brakująca stawka cache progu: mnożnik × stawka wejścia progu, nie mniej niż stawka bazowa (ccusage whole_request_rates, #2496).
        static double FromTier(double? own, double? tierInput, double factor, double baseRate) =>
            own ?? (tierInput is { } rate ? Math.Max(rate * factor, baseRate) : baseRate);

        var whole = tiers.Where(tier => !tier.Marginal && context > tier.AboveTokens).MaxBy(tier => tier.AboveTokens);
        if (whole is not null)
        {
            var rates = whole.Rates;
            return multiplier * (
                entry.InputTokens * (rates.Input ?? input) +
                entry.OutputTokens * (rates.Output ?? output) +
                cache5m * FromTier(rates.CacheWrite, rates.Input, defaults.WriteX, cacheWrite) +
                cache1h * FromTier(rates.CacheWrite1h, rates.Input, defaults.Write1hX, cacheWrite1h) +
                entry.CacheReadTokens * FromTier(rates.CacheRead, rates.Input, defaults.ReadX, cacheRead));
        }

        var marginal = tiers.FirstOrDefault(tier => tier.Marginal);
        if (marginal is not null)
        {
            var rates = marginal.Rates;
            var threshold = marginal.AboveTokens;
            var above1h = rates.CacheWrite1h ?? (rates.Input is { } longInput ? Math.Max(longInput * defaults.Write1hX, cacheWrite1h) : null);
            return multiplier * (
                Tiered(entry.InputTokens, input, rates.Input, threshold) +
                Tiered(entry.OutputTokens, output, rates.Output, threshold) +
                Tiered(cache5m, cacheWrite, rates.CacheWrite, threshold) +
                Tiered(cache1h, cacheWrite1h, above1h, threshold) +
                Tiered(entry.CacheReadTokens, cacheRead, rates.CacheRead, threshold));
        }

        return multiplier * (
            entry.InputTokens * input +
            entry.OutputTokens * output +
            cache5m * cacheWrite +
            cache1h * cacheWrite1h +
            entry.CacheReadTokens * cacheRead);
    }

    /// <summary>Rozliczenie krańcowe: tokeny ponad próg po stawce progu (ccusage <c>tiered_cost</c>).</summary>
    private static double Tiered(long tokens, double baseRate, double? above, long threshold) =>
        above is { } rate && tokens > threshold ? threshold * baseRate + (tokens - threshold) * rate : tokens * baseRate;
}
