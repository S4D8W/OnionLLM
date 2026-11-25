using System.Globalization;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

/// <summary>Formatowanie liczb, kosztów i czasu w interfejsie.</summary>
public static class Fmt
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pl-PL");

    public static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.##", Culture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", Culture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", Culture) + "k",
        _ => value.ToString("N0", Culture)
    };

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string Cost(double value) => value switch
    {
        >= 1000 => "$" + value.ToString("N0", CultureInfo.InvariantCulture),
        >= 0.995 or 0 => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        _ => "$" + value.ToString("0.00##", CultureInfo.InvariantCulture)
    };

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min";
    }

    public static string Model(string model)
    {
        // Wiersze bez nazwy modelu (np. Copilot: same kredyty, model „unknown”).
        if (model is "unknown" or "(nieznany)" or "")
        {
            return "nieznany model";
        }

        var name = model;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            name = name[7..];
        }

        return System.Text.RegularExpressions.Regex.Replace(name, "-\\d{8}$", "");
    }

    public static string BurnLevel(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "wysokie",
        BurnRateLevel.Moderate => "umiarkowane",
        _ => "normalne"
    };

    public static string BurnColor(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "#E5484D",
        BurnRateLevel.Moderate => "#F5A524",
        _ => "#23C483"
    };

    public static string LocalTime(DateTimeOffset value, string format = "dd.MM HH:mm") =>
        TimeZoneInfo.ConvertTime(value, AppState.Engine.Reports.TimeZone).ToString(format, Culture);

    /// <summary>Znacznik kosztu: <c>*</c> — część modeli bez ceny, <c>~</c> — część cen szacowana (jak w ccusage).</summary>
    public static string Marker(IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated) =>
        (unpriced.Count > 0 ? "*" : "") + (estimated.Count > 0 ? "~" : "");

    public static string Marker(ModelBreakdown model) => (model.IsUnpriced ? "*" : "") + (model.IsEstimated ? "~" : "");

    public static string Credits(double value) => value.ToString("#,0.##", Culture);

    /// <summary>„cache zapis 1,2M (1 h 300k)” — z osobnym zapisem 1 h, gdy występuje.</summary>
    public static string CacheWrite(TokenCounts totals) =>
        $"cache zapis {Tokens(totals.CacheCreationTokens)}" +
        (totals.CacheCreation1hTokens > 0 ? $" (1 h {Tokens(totals.CacheCreation1hTokens)})" : "");

    /// <summary>Krótka linia szczegółów wiersza raportu lub sesji.</summary>
    public static string Summary(TokenCounts totals) =>
        $"we {Tokens(totals.InputTokens)} · wy {Tokens(totals.OutputTokens)} · {CacheWrite(totals)} · odczyt {Tokens(totals.CacheReadTokens)}" +
        (totals.Credits != 0 ? $" · kredyty {Credits(totals.Credits)}" : "");

    /// <summary>Pełne szczegóły (okno szczegółów wiersza w Raportach i Sesjach).</summary>
    public static IEnumerable<string> DetailLines(TokenCounts totals)
    {
        yield return $"Koszt: {Cost(totals.CostUsd)} · tokeny: {Number(totals.TotalTokens)} · wywołań: {Number(totals.Requests)}";
        if (totals.HasStoredCost)
        {
            yield return $"   zapisany przez narzędzie {Cost(totals.StoredCostUsd)} · wyliczony z cennika {Cost(totals.CalculatedCostUsd)}";
        }

        yield return $"Wejście {Number(totals.InputTokens)} · wyjście {Number(totals.OutputTokens)}" +
                     (totals.ReasoningTokens > 0 ? $" (w tym reasoning {Number(totals.ReasoningTokens)})" : "");
        var cache5m = totals.CacheCreationTokens - totals.CacheCreation1hTokens;
        yield return $"Cache zapis {Number(totals.CacheCreationTokens)}" +
                     (totals.CacheCreation1hTokens > 0 ? $" (5 min {Number(cache5m)} · 1 h {Number(totals.CacheCreation1hTokens)})" : "") +
                     $" · odczyt {Number(totals.CacheReadTokens)}";
        if (totals.Credits != 0)
        {
            yield return $"Kredyty: {Credits(totals.Credits)}";
        }
    }

    /// <summary>Stopka: lista modeli bez ceny (*) i z ceną szacowaną (~); pusty tekst, gdy brak.</summary>
    public static string PricingNote(IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated)
    {
        var parts = new List<string>();
        if (unpriced.Count > 0)
        {
            parts.Add("* brak ceny: " + string.Join(", ", unpriced.Select(Model)));
        }

        if (estimated.Count > 0)
        {
            parts.Add("~ cena szacowana: " + string.Join(", ", estimated.Select(Model)));
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Czytelne ID sesji: ID będące ścieżką (Codex: <c>2026/11/24/rollout-…-&lt;uuid&gt;</c>) skracane do nazwy pliku
    /// bez prefiksu <c>rollout-</c> i rozszerzenia; długie ID obcinane do <paramref name="max"/> znaków.
    /// </summary>
    public static string SessionId(string id, int max = 13)
    {
        var name = id;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
            foreach (var extension in new[] { ".jsonl.zst", ".jsonl", ".json" })
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^extension.Length];
                    break;
                }
            }

            if (name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
            {
                // rollout-2026-11-24T10-00-00-<uuid> → <uuid>
                var match = System.Text.RegularExpressions.Regex.Match(name, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}.*$");
                name = match.Success ? match.Value : name["rollout-".Length..];
            }
        }

        return name.Length > max ? name[..max] + "…" : name;
    }
}
