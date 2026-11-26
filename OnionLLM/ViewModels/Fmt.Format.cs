using System.Globalization;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

/// <summary>
/// Pure formatting rules (DESIGN.md §5.1, §5.2, §8.3). No dependency on application state, so this file is also
/// compiled into <c>OnionLLM.Core.Tests</c>. Time-zone conversion lives in <c>Fmt.cs</c>.
/// </summary>
public static partial class Fmt
{
    /// <summary>Non-breaking space (U+00A0) between a number and its unit or currency.</summary>
    public const string Nbsp = " ";

    /// <summary>Minus sign (U+2212) for negative values.</summary>
    public const string Minus = "−";

    private static readonly CultureInfo Culture = CreateCulture();

    private static readonly string[] DayShort = ["nd", "pn", "wt", "śr", "cz", "pt", "sb"];
    private static readonly string[] DayLong = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];
    private static readonly string[] MonthShort = ["sty", "lut", "mar", "kwi", "maj", "cze", "lip", "sie", "wrz", "paź", "lis", "gru"];
    private static readonly string[] MonthNominative =
        ["styczeń", "luty", "marzec", "kwiecień", "maj", "czerwiec", "lipiec", "sierpień", "wrzesień", "październik", "listopad", "grudzień"];
    private static readonly string[] MonthGenitive =
        ["stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia"];

    // ===== Numbers (§5.1) =====

    /// <summary>Compact tokens: „842”, „12,4 k”, „842 k”, „1,24 M”, „2,05 B”.</summary>
    public static string Tokens(long value)
    {
        var sign = value < 0 ? Minus : "";
        var absolute = Math.Abs((double)value);
        return sign + absolute switch
        {
            >= 1_000_000_000 => (absolute / 1_000_000_000).ToString("0.00", Culture) + Nbsp + "B",
            >= 1_000_000 => (absolute / 1_000_000).ToString("0.00", Culture) + Nbsp + "M",
            >= 100_000 => (absolute / 1_000).ToString("0", Culture) + Nbsp + "k",
            >= 1_000 => (absolute / 1_000).ToString("0.0", Culture) + Nbsp + "k",
            _ => absolute.ToString("0", Culture)
        };
    }

    /// <summary>Full number with non-breaking thousands separators: „12 412”.</summary>
    public static string Number(long value) => (value < 0 ? Minus : "") + Math.Abs(value).ToString("N0", Culture);

    /// <summary>Compact cost: „0,00 $”, „0,0042 $”, „12,34 $”, „1 234 $”.</summary>
    public static string Cost(double value) => FormatCost(value, compact: true);

    /// <summary>Full cost (tooltip, drawer, export): „1 234,50 $”.</summary>
    public static string CostFull(double value) => FormatCost(value, compact: false);

    /// <summary>Price per 1 M tokens: „3,00 $ / 1 M”.</summary>
    public static string PricePerMillion(double perToken) => CostFull(perToken * 1_000_000) + " /" + Nbsp + "1" + Nbsp + "M";

    /// <summary>Percent from a 0–100 value: „62%”, „4,2%”, „&lt; 0,1%”.</summary>
    public static string Percent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return "—";
        }

        var sign = percent < 0 ? Minus : "";
        var absolute = Math.Abs(percent);
        return absolute switch
        {
            0 => "0%",
            < 0.1 => "<" + Nbsp + "0,1%",
            < 10 => sign + absolute.ToString("0.#", Culture) + "%",
            _ => sign + absolute.ToString("0", Culture) + "%"
        };
    }

    /// <summary>Percent from a 0–1 fraction.</summary>
    public static string Share(double fraction) => Percent(fraction * 100);

    /// <summary>Burn rate: „4,20 $/h”.</summary>
    public static string CostPerHour(double value) => Cost(value) + "/h";

    /// <summary>Token rate: „1,8 k tok/min”.</summary>
    public static string TokensPerMinute(double value) => Tokens((long)Math.Round(value)) + Nbsp + "tok/min";

    /// <summary>Multiplier: „×1,5”.</summary>
    public static string Multiplier(double value) => "×" + value.ToString("0.##", Culture);

    /// <summary>Credits with declension: „1 kredyt”, „3 kredyty”, „12,5 kredytu”.</summary>
    public static string Credits(double value)
    {
        var text = value.ToString("#,0.##", Culture);
        if (value % 1 != 0)
        {
            return text + Nbsp + "kredytu";
        }

        return text + Nbsp + Plural((long)value, "kredyt", "kredyty", "kredytów");
    }

    /// <summary>Number without unit for credits columns.</summary>
    public static string CreditsValue(double value) => value.ToString("#,0.##", Culture);

    // ===== Declension (§8.3) =====

    /// <summary>Polish plural form for <paramref name="n"/>: 1 → one, 2–4 (not 12–14) → few, otherwise many.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n == 1)
        {
            return one;
        }

        var lastTwo = n % 100;
        var last = n % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }

    /// <summary>„5 tokenów”, „2 sesje” — number (full format) followed by the declined noun.</summary>
    public static string Count(long n, string one, string few, string many) => Number(n) + Nbsp + Plural(n, one, few, many);

    /// <summary>„1,2 M tokenów” — compact token count with the noun (abbreviations always take „tokenów”).</summary>
    public static string TokensWithUnit(long value) =>
        Tokens(value) + Nbsp + (Math.Abs(value) >= 1_000 ? "tokenów" : Plural(value, "token", "tokeny", "tokenów"));

    // ===== Durations and dates (§5.2) — values already in the display time zone =====

    /// <summary>„2 h 14 min”, „45 min”, „&lt; 1 min”.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "<" + Nbsp + "1" + Nbsp + "min";
        }

        var hours = (long)span.TotalHours;
        return hours >= 1
            ? $"{hours}{Nbsp}h {span.Minutes}{Nbsp}min"
            : $"{span.Minutes}{Nbsp}min";
    }

    /// <summary>Countdown: „za 2 h 14 min”.</summary>
    public static string Countdown(TimeSpan remaining) => "za " + Duration(remaining);

    /// <summary>„14:02”.</summary>
    public static string Time(DateTime local) => local.ToString("HH:mm", Culture);

    /// <summary>Table date: „26 lis” (or „3 gru 2025” in another year than <paramref name="today"/>).</summary>
    public static string ShortDate(DateTime local, DateTime today) =>
        local.Year == today.Year
            ? $"{local.Day}{Nbsp}{MonthShort[local.Month - 1]}"
            : $"{local.Day}{Nbsp}{MonthShort[local.Month - 1]} {local.Year}";

    /// <summary>Axis / daily period: „wt 24 lis”.</summary>
    public static string DayDate(DateTime local) =>
        $"{DayShort[(int)local.DayOfWeek]} {local.Day}{Nbsp}{MonthShort[local.Month - 1]}";

    /// <summary>Axis tick: „pn 24”.</summary>
    public static string DayTick(DateTime local) => $"{DayShort[(int)local.DayOfWeek]} {local.Day}";

    /// <summary>„26 lis, 14:02”.</summary>
    public static string DateTimeShort(DateTime local, DateTime today) => ShortDate(local, today) + ", " + Time(local);

    /// <summary>Page subtitle: „czwartek, 26 listopada”.</summary>
    public static string LongDate(DateTime local) =>
        $"{DayLong[(int)local.DayOfWeek]}, {local.Day}{Nbsp}{MonthGenitive[local.Month - 1]}";

    /// <summary>„listopad 2026”.</summary>
    public static string Month(DateTime local) => $"{MonthNominative[local.Month - 1]} {local.Year}";

    /// <summary>Short month for axes: „lis”.</summary>
    public static string MonthShortName(int month) => MonthShort[month - 1];

    /// <summary>„Tydz. 47 · 17–23 lis”.</summary>
    public static string Week(DateTime start, DateTime end)
    {
        var week = ISOWeek.GetWeekOfYear(start);
        return $"Tydz.{Nbsp}{week} · {Range(start, end, withYear: false)}";
    }

    /// <summary>„1–26 lis 2026”, „28 paź – 3 lis 2026”, „28 gru 2025 – 3 sty 2026”.</summary>
    public static string Range(DateTime start, DateTime end, bool withYear = true)
    {
        if (start.Date == end.Date)
        {
            return $"{start.Day}{Nbsp}{MonthShort[start.Month - 1]}" + (withYear ? $" {start.Year}" : "");
        }

        var year = withYear ? $" {end.Year}" : "";
        if (start.Year != end.Year)
        {
            return $"{start.Day}{Nbsp}{MonthShort[start.Month - 1]} {start.Year} – {end.Day}{Nbsp}{MonthShort[end.Month - 1]} {end.Year}";
        }

        return start.Month == end.Month
            ? $"{start.Day}–{end.Day}{Nbsp}{MonthShort[end.Month - 1]}{year}"
            : $"{start.Day}{Nbsp}{MonthShort[start.Month - 1]} – {end.Day}{Nbsp}{MonthShort[end.Month - 1]}{year}";
    }

    /// <summary>Relative time (status bar, last activity): „przed chwilą”, „5 min temu”, „o 14:02”, „wczoraj, 18:40”, date.</summary>
    public static string Relative(DateTime local, DateTime now)
    {
        var age = now - local;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "przed chwilą";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}{Nbsp}min temu";
        }

        if (local.Date == now.Date)
        {
            return "o " + Time(local);
        }

        return local.Date == now.Date.AddDays(-1)
            ? "wczoraj, " + Time(local)
            : DateTimeShort(local, now);
    }

    /// <summary>Date field value: „26.11.2026”.</summary>
    public static string DateField(DateTime local) => local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // ===== Spoken variants for screen readers (§9.3) =====

    /// <summary>„12,34 dolara”, „1 dolar”, „5 dolarów”.</summary>
    public static string CostSpoken(double value)
    {
        var rounded = Math.Round(value, 2);
        if (rounded % 1 != 0)
        {
            return rounded.ToString("0.00", Culture) + " dolara";
        }

        return Number((long)rounded) + " " + Plural((long)rounded, "dolar", "dolary", "dolarów");
    }

    /// <summary>„1,2 miliona tokenów”, „842 tysiące tokenów”, „5 tokenów”.</summary>
    public static string TokensSpoken(long value)
    {
        var absolute = Math.Abs(value);
        return absolute switch
        {
            >= 1_000_000_000 => Scaled(value / 1_000_000_000d, "miliard", "miliardy", "miliardów", "miliarda"),
            >= 1_000_000 => Scaled(value / 1_000_000d, "milion", "miliony", "milionów", "miliona"),
            >= 1_000 => Scaled(value / 1_000d, "tysiąc", "tysiące", "tysięcy", "tysiąca"),
            _ => Number(value) + " " + Plural(value, "token", "tokeny", "tokenów")
        };

        static string Scaled(double amount, string one, string few, string many, string fraction)
        {
            var rounded = Math.Round(amount, 1);
            var noun = rounded % 1 != 0 ? fraction : Plural((long)rounded, one, few, many);
            return rounded.ToString("0.#", Culture) + " " + noun + " tokenów";
        }
    }

    /// <summary>„62 procent”.</summary>
    public static string PercentSpoken(double percent) => Math.Round(percent).ToString("0", Culture) + " procent";

    // ===== Domain helpers =====

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

    /// <summary>Burn level label (§8.6): „normalne”, „umiarkowane”, „wysokie”.</summary>
    public static string BurnLevel(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "wysokie",
        BurnRateLevel.Moderate => "umiarkowane",
        _ => "normalne"
    };

    /// <summary>Token key of the burn colour (<c>color.burn.*</c>) — resolve with <c>DesignTokens.Color</c>.</summary>
    public static string BurnColorKey(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "ColorBurnHigh",
        BurnRateLevel.Moderate => "ColorBurnModerate",
        _ => "ColorBurnNormal"
    };

    /// <summary>Znacznik kosztu: <c>*</c> — część modeli bez ceny, <c>~</c> — część cen szacowana (jak w ccusage).</summary>
    public static string Marker(IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated) =>
        (unpriced.Count > 0 ? "*" : "") + (estimated.Count > 0 ? "~" : "");

    public static string Marker(ModelBreakdown model) => (model.IsUnpriced ? "*" : "") + (model.IsEstimated ? "~" : "");

    /// <summary>„zapis cache 1,2 M (1 h 300 k)” — z osobnym zapisem 1 h, gdy występuje.</summary>
    public static string CacheWrite(TokenCounts totals) =>
        $"zapis cache {Tokens(totals.CacheCreationTokens)}" +
        (totals.CacheCreation1hTokens > 0 ? $" (1{Nbsp}h {Tokens(totals.CacheCreation1hTokens)})" : "");

    /// <summary>Krótka linia szczegółów wiersza raportu lub sesji.</summary>
    public static string Summary(TokenCounts totals) =>
        $"we {Tokens(totals.InputTokens)} · wy {Tokens(totals.OutputTokens)} · {CacheWrite(totals)} · odczyt {Tokens(totals.CacheReadTokens)}" +
        (totals.Credits != 0 ? $" · {Credits(totals.Credits)}" : "");

    /// <summary>Pełne szczegóły wiersza w Raportach i Sesjach.</summary>
    public static IEnumerable<string> DetailLines(TokenCounts totals)
    {
        yield return $"Koszt: {CostFull(totals.CostUsd)} · tokeny: {Number(totals.TotalTokens)} · wywołania: {Number(totals.Requests)}";
        if (totals.HasStoredCost)
        {
            yield return $"Zapisany przez narzędzie: {CostFull(totals.StoredCostUsd)} · wyliczony z cennika: {CostFull(totals.CalculatedCostUsd)}";
        }

        yield return $"Wejście {Number(totals.InputTokens)} · wyjście {Number(totals.OutputTokens)}" +
                     (totals.ReasoningTokens > 0 ? $" (w tym rozumowanie {Number(totals.ReasoningTokens)})" : "");
        var cache5m = totals.CacheCreationTokens - totals.CacheCreation1hTokens;
        yield return $"Zapis cache {Number(totals.CacheCreationTokens)}" +
                     (totals.CacheCreation1hTokens > 0 ? $" (5{Nbsp}min {Number(cache5m)} · 1{Nbsp}h {Number(totals.CacheCreation1hTokens)})" : "") +
                     $" · odczyt cache {Number(totals.CacheReadTokens)}";
        if (totals.Credits != 0)
        {
            yield return $"Kredyty: {CreditsValue(totals.Credits)}";
        }
    }

    /// <summary>Stopka (§4.7.6): modele bez ceny i z ceną szacowaną; pusty tekst, gdy brak.</summary>
    public static string PricingNote(IReadOnlyCollection<string> unpriced, IReadOnlyCollection<string> estimated)
    {
        var parts = new List<string>();
        if (unpriced.Count > 0)
        {
            parts.Add($"Koszt niepełny: brak ceny dla {Count(unpriced.Count, "modelu", "modeli", "modeli")} ({string.Join(", ", unpriced.Select(Model))})");
        }

        if (estimated.Count > 0)
        {
            parts.Add($"Szacowany: {Count(estimated.Count, "model", "modele", "modeli")} ({string.Join(", ", estimated.Select(Model))})");
        }

        return string.Join(" · ", parts);
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

    private static string FormatCost(double value, bool compact)
    {
        var sign = value < 0 ? Minus : "";
        var absolute = Math.Abs(value);
        var number = absolute switch
        {
            0 => "0,00",
            < 0.995 => absolute.ToString("0.00##", Culture),
            < 1000 => absolute.ToString("0.00", Culture),
            _ => absolute.ToString(compact ? "N0" : "N2", Culture)
        };
        return sign + number + Nbsp + "$";
    }

    private static CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("pl-PL").Clone();
        culture.NumberFormat.NumberGroupSeparator = Nbsp;
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }
}
