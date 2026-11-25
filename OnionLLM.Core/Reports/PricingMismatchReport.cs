using System.Globalization;
using System.Text;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Reports;

/// <summary>Wpis, którego koszt zapisany przez narzędzie różni się od wyliczonego z cennika.</summary>
public sealed record PricingMismatchSample(UsageEntry Entry, double StoredCostUsd, double CalculatedCostUsd)
{
    public double Difference => CalculatedCostUsd - StoredCostUsd;

    /// <summary>Różnica względem kosztu zapisanego, w procentach (wartość bezwzględna).</summary>
    public double PercentDifference => StoredCostUsd == 0 ? 100 : Math.Abs(Difference) / StoredCostUsd * 100;
}

/// <summary>Statystyka rozbieżności dla pary źródło + model.</summary>
public sealed record PricingMismatchModelStats
{
    public required string Source { get; init; }
    public required string Model { get; init; }
    public int Compared { get; init; }
    public int Matches { get; init; }
    public int Mismatches { get; init; }
    public int Unpriced { get; init; }
    public double StoredCostUsd { get; init; }
    public double CalculatedCostUsd { get; init; }
    public double AveragePercentDifference { get; init; }

    public double MatchRate => Compared == 0 ? 0 : (double)Matches / Compared;
}

/// <summary>
/// Porównanie kosztu zapisanego przez narzędzie (np. <c>costUSD</c> Claude Code) z kosztem wyliczonym z cennika —
/// odpowiednik raportu ccusage <c>--debug</c> / <c>--debug-samples</c> (TS: <c>debug.ts</c>, „Pricing Mismatch Debug Report”).
/// Wpis jest zgodny, gdy różnica jest mniejsza niż <see cref="PricingMismatchAnalyzer.DefaultTolerancePercent"/>% kosztu zapisanego.
/// </summary>
public sealed record PricingMismatchReport
{
    /// <summary>Wszystkie przeanalizowane wpisy.</summary>
    public int TotalEntries { get; init; }

    /// <summary>Wpisy z kosztem zapisanym przez narzędzie.</summary>
    public int EntriesWithStoredCost { get; init; }

    /// <summary>Wpisy z kosztem zapisanym i ceną w cenniku (porównane).</summary>
    public int Compared { get; init; }

    public int Matches { get; init; }
    public int Mismatches { get; init; }

    /// <summary>Wpisy z kosztem zapisanym, ale bez ceny w cenniku.</summary>
    public int Unpriced { get; init; }

    public double StoredCostUsd { get; init; }
    public double CalculatedCostUsd { get; init; }
    public double TolerancePercent { get; init; }
    public IReadOnlyList<PricingMismatchModelStats> Models { get; init; } = [];

    /// <summary>Największe rozbieżności (wg kwoty), co najwyżej tyle, ile zażądano.</summary>
    public IReadOnlyList<PricingMismatchSample> Samples { get; init; } = [];

    public double MatchRate => Compared == 0 ? 0 : (double)Matches / Compared;

    /// <summary>Tekst raportu (Ustawienia → Diagnostyka, CLI <c>--debug</c>).</summary>
    public string Format(TimeZoneInfo? zone = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Rozbieżności kosztu: zapisany przez narzędzie vs wyliczony z cennika");
        builder.AppendLine($"Wpisów: {TotalEntries} · z kosztem zapisanym: {EntriesWithStoredCost} · porównanych: {Compared}" +
                           (Unpriced > 0 ? I($" · bez ceny w cenniku: {Unpriced}") : ""));
        if (Compared == 0)
        {
            builder.Append(EntriesWithStoredCost == 0
                ? "Brak wpisów z kosztem zapisanym przez narzędzie w wybranym zakresie."
                : "Żaden wpis z kosztem zapisanym nie ma ceny w cenniku.");
            return builder.ToString();
        }

        builder.AppendLine((
            I($"Zgodne (< {TolerancePercent:0.###}%): {Matches} ({MatchRate:P1}) · rozbieżne: {Mismatches}")));
        builder.AppendLine((
            I($"Suma zapisana ${StoredCostUsd:0.####} · wyliczona ${CalculatedCostUsd:0.####} · różnica {Signed(CalculatedCostUsd - StoredCostUsd)}")));

        if (Models.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Według modeli:");
            foreach (var model in Models)
            {
                builder.AppendLine((
                    I($"  [{model.Source}] {model.Model}: {model.Compared} porówn., zgodne {model.MatchRate:P0}, śr. różnica {model.AveragePercentDifference:0.##}% ") +
                    I($"(zapisany ${model.StoredCostUsd:0.####} / wyliczony ${model.CalculatedCostUsd:0.####})") +
                    (model.Unpriced > 0 ? I($", bez ceny: {model.Unpriced}") : "")));
            }
        }

        if (Samples.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Przykłady ({Samples.Count}):");
            foreach (var sample in Samples)
            {
                var time = zone is null ? sample.Entry.Timestamp : TimeZoneInfo.ConvertTime(sample.Entry.Timestamp, zone);
                var entry = sample.Entry;
                builder.AppendLine((
                    I($"  {time:yyyy-MM-dd HH:mm:ss} [{entry.Source}] {entry.Model ?? ReportBuilder.UnknownModel}: zapisany ${sample.StoredCostUsd:0.######} · ") +
                    I($"wyliczony ${sample.CalculatedCostUsd:0.######} · {Signed(sample.Difference)} ({sample.PercentDifference:0.##}%)")));
                builder.AppendLine((
                    I($"      we {entry.InputTokens} · wy {entry.OutputTokens} · cache zapis {entry.CacheCreationTokens}") +
                    (entry.CacheCreation1hTokens > 0 ? I($" (1 h {entry.CacheCreation1hTokens})") : "") +
                    I($" · odczyt {entry.CacheReadTokens}") + (entry.Speed is SpeedTier.Unknown or SpeedTier.Standard ? "" : I($" · {entry.Speed}")) +
                    (string.IsNullOrEmpty(entry.SessionId) ? "" : I($" · sesja {entry.SessionId}"))));
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string I(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Signed(double value) =>
        (value >= 0 ? "+$" : "−$") + Math.Abs(value).ToString("0.######", CultureInfo.InvariantCulture);
}

public static class PricingMismatchAnalyzer
{
    /// <summary>Próg zgodności ccusage: różnica &lt; 0,1% kosztu zapisanego.</summary>
    public const double DefaultTolerancePercent = 0.1;

    /// <summary>Domyślna liczba przykładów (ccusage <c>debugSamples</c> = 5).</summary>
    public const int DefaultSamples = 5;

    /// <param name="calculator">Kalkulator, którego cennik i reguły źródeł mają być użyte; tryb kosztu jest ignorowany
    /// (zawsze wyliczenie z cennika).</param>
    /// <param name="sources">Reguły cennika źródeł (domyślnie rejestr źródeł).</param>
    public static PricingMismatchReport Analyze(IEnumerable<UsageEntry> entries, CostCalculator calculator, int samples = DefaultSamples,
        double tolerancePercent = DefaultTolerancePercent, Func<string, Sources.IUsageSource?>? sources = null)
    {
        var calculate = calculator.Mode == CostMode.Calculate ? calculator : new CostCalculator(calculator.Catalog, CostMode.Calculate, sources);
        var total = 0;
        var withStored = 0;
        var unpriced = 0;
        var all = new List<PricingMismatchSample>();
        var models = new Dictionary<(string Source, string Model), ModelState>();

        foreach (var entry in entries)
        {
            total++;
            if (entry.RecordedCostUsd is not { } stored || stored <= 0)
            {
                continue;
            }

            withStored++;
            var key = (entry.Source, entry.Model ?? ReportBuilder.UnknownModel);
            if (!models.TryGetValue(key, out var state))
            {
                models[key] = state = new ModelState();
            }

            var cost = calculate.Calculate(entry);
            if (cost.IsUnpriced)
            {
                unpriced++;
                state.Unpriced++;
                continue;
            }

            var sample = new PricingMismatchSample(entry, stored, cost.CostUsd);
            state.Add(sample, sample.PercentDifference < tolerancePercent);
            all.Add(sample);
        }

        var mismatches = all.Where(sample => sample.PercentDifference >= tolerancePercent).ToList();
        return new PricingMismatchReport
        {
            TotalEntries = total,
            EntriesWithStoredCost = withStored,
            Compared = all.Count,
            Matches = all.Count - mismatches.Count,
            Mismatches = mismatches.Count,
            Unpriced = unpriced,
            StoredCostUsd = all.Sum(sample => sample.StoredCostUsd),
            CalculatedCostUsd = all.Sum(sample => sample.CalculatedCostUsd),
            TolerancePercent = tolerancePercent,
            Models = models
                .Select(pair => pair.Value.ToStats(pair.Key.Source, pair.Key.Model))
                .OrderByDescending(stats => stats.Mismatches)
                .ThenByDescending(stats => stats.Compared)
                .ThenBy(stats => stats.Model, StringComparer.Ordinal)
                .ToArray(),
            Samples = mismatches
                .OrderByDescending(sample => Math.Abs(sample.Difference))
                .ThenByDescending(sample => sample.Entry.TimestampMs)
                .Take(Math.Max(0, samples))
                .ToArray()
        };
    }

    /// <summary>Raport dla zakresu i filtrów zapytania (te same wpisy co raport okresowy).</summary>
    public static PricingMismatchReport Analyze(UsageEngine engine, ReportQuery query, int samples = DefaultSamples, DateTimeOffset? now = null)
    {
        var reports = engine.Reports;
        var moment = now ?? DateTimeOffset.Now;
        var (from, to) = reports.ResolveRange(query, ReportPeriod.Daily, moment);
        var (fromMs, toMs) = reports.ToUtcRange(from?.AddDays(-1), to?.AddDays(1));
        var entries = reports.Filter(engine.LoadEntries(fromMs, toMs, query.Sources), query, from, to);
        return Analyze(entries, reports.Calculator, samples, DefaultTolerancePercent, engine.FindSource);
    }

    private sealed class ModelState
    {
        public int Compared;
        public int Matches;
        public int Unpriced;
        public double Stored;
        public double Calculated;
        public double PercentSum;

        public void Add(PricingMismatchSample sample, bool match)
        {
            Compared++;
            Matches += match ? 1 : 0;
            Stored += sample.StoredCostUsd;
            Calculated += sample.CalculatedCostUsd;
            PercentSum += sample.PercentDifference;
        }

        public PricingMismatchModelStats ToStats(string source, string model) => new()
        {
            Source = source,
            Model = model,
            Compared = Compared,
            Matches = Matches,
            Mismatches = Compared - Matches,
            Unpriced = Unpriced,
            StoredCostUsd = Stored,
            CalculatedCostUsd = Calculated,
            AveragePercentDifference = Compared == 0 ? 0 : PercentSum / Compared
        };
    }
}
