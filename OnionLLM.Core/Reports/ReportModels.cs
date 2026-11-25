using System.Globalization;
using OnionLLM.Core.Models;

namespace OnionLLM.Core.Reports;

public enum ReportPeriod
{
    Daily,
    Weekly,
    Monthly
}

public enum SortOrder
{
    Asc,
    Desc
}

public sealed record ReportQuery
{
    public DateOnly? Since { get; init; }
    public DateOnly? Until { get; init; }

    /// <summary>N ostatnich okresów grupowania (łącznie z bieżącym).</summary>
    public int? Last { get; init; }

    /// <summary>Filtr źródeł (puste = wszystkie).</summary>
    public IReadOnlyCollection<string> Sources { get; init; } = [];

    /// <summary>Filtr projektu (klucz, alias lub nazwa katalogu).</summary>
    public string? Project { get; init; }

    public SortOrder Order { get; init; } = SortOrder.Asc;

    /// <summary>
    /// Podział wierszy na modele w widoku (tabela, UI). <see cref="ReportRow.ModelBreakdowns"/> są liczone zawsze,
    /// bo eksport JSON zgodny z ccusage zawiera je niezależnie od tej opcji.
    /// </summary>
    public bool Breakdown { get; init; }

    /// <summary>Podział wierszy na źródła w widoku (ccusage <c>--by-agent</c>).</summary>
    public bool BySource { get; init; }

    /// <summary>Grupowanie wierszy po projekcie (ccusage <c>--instances</c>).</summary>
    public bool GroupByProject { get; init; }

    public void Validate()
    {
        if (Since is not null && Until is not null && Since > Until)
        {
            throw new ArgumentException($"Data początkowa {Since:yyyy-MM-dd} jest późniejsza niż końcowa {Until:yyyy-MM-dd}.");
        }

        if (Last is <= 0)
        {
            throw new ArgumentException("--last musi być dodatnie.");
        }
    }
}

/// <param name="IsUnpriced">Część wpisów modelu nie miała ceny (ccusage <c>missingPricing</c>).</param>
/// <param name="IsEstimated">Cena części wpisów jest szacowana (ccusage <c>estimatedPricing</c>).</param>
public sealed record ModelBreakdown(string Model, TokenCounts Totals, bool IsUnpriced, bool IsEstimated = false);

public sealed record SourceBreakdown(string Source, TokenCounts Totals)
{
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<ModelBreakdown> ModelBreakdowns { get; init; } = [];
}

public sealed record ReportRow
{
    /// <summary>Klucz okresu: <c>2026-11-24</c>, <c>2026-11-23</c> (początek tygodnia) lub <c>2026-11</c>.</summary>
    public required string Period { get; init; }
    public DateOnly PeriodStart { get; init; }
    public string? Project { get; init; }
    public TokenCounts Totals { get; init; } = new();
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];
    public IReadOnlyList<string> EstimatedModels { get; init; } = [];
    public IReadOnlyList<ModelBreakdown> ModelBreakdowns { get; init; } = [];
    public IReadOnlyList<SourceBreakdown> SourceBreakdowns { get; init; } = [];
}

public sealed record UsageReport
{
    public required ReportPeriod Period { get; init; }
    public IReadOnlyList<ReportRow> Rows { get; init; } = [];
    public TokenCounts Totals { get; init; } = new();
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];
    public IReadOnlyList<string> EstimatedModels { get; init; } = [];
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
}

public sealed record SessionRow
{
    public required string SessionId { get; init; }
    public required string Source { get; init; }
    public string? Project { get; init; }
    public string? ProjectPath { get; init; }
    public DateTimeOffset FirstActivity { get; init; }
    public DateTimeOffset LastActivity { get; init; }
    public TokenCounts Totals { get; init; } = new();
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];
    public IReadOnlyList<string> EstimatedModels { get; init; } = [];
    public IReadOnlyList<ModelBreakdown> ModelBreakdowns { get; init; } = [];

    public TimeSpan Duration => LastActivity - FirstActivity;
}

public sealed record SessionReport
{
    public IReadOnlyList<SessionRow> Sessions { get; init; } = [];
    public TokenCounts Totals { get; init; } = new();
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];
    public IReadOnlyList<string> EstimatedModels { get; init; } = [];
}

/// <summary>Parsowanie dat w formatach ccusage: <c>YYYY-MM-DD</c> lub <c>YYYYMMDD</c>.</summary>
public static class DateArgs
{
    private static readonly string[] Formats = ["yyyy-MM-dd", "yyyyMMdd"];

    public static DateOnly Parse(string value, string name = "data")
    {
        if (DateOnly.TryParseExact(value.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new ArgumentException($"Niepoprawna {name} '{value}': oczekiwano YYYY-MM-DD lub YYYYMMDD.");
    }

    public static DateOnly? ParseOptional(string? value, string name = "data") =>
        string.IsNullOrWhiteSpace(value) ? null : Parse(value, name);
}
