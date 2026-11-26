using OnionLLM.Core.Config;

namespace OnionLLM.Core.Reports;

/// <summary>Wykorzystanie jednego budżetu (dzienny lub miesięczny) w USD.</summary>
public sealed record BudgetUsage(double LimitUsd, double SpentUsd)
{
    public double Percent => LimitUsd > 0 ? SpentUsd * 100 / LimitUsd : 0;
}

/// <summary>Budżety jednego źródła (lub globalne, gdy <see cref="Source"/> = null).</summary>
public sealed record BudgetLine(string? Source, BudgetUsage? Daily, BudgetUsage? Monthly, double? MonthForecastUsd);

/// <summary>
/// Stan budżetów (DESIGN.md §5.9): globalne i per źródło, z prognozą końca miesiąca
/// (średnia dzienna z ostatnich 7 dni × pozostałe dni + koszt dotychczasowy; od 3. dnia miesiąca).
/// </summary>
public sealed record BudgetReport(BudgetLine Global, IReadOnlyList<BudgetLine> PerSource, DateOnly Today)
{
    public bool HasAny => Global.Daily is not null || Global.Monthly is not null || PerSource.Count > 0;

    /// <summary>Najwyższe wykorzystanie spośród wszystkich budżetów (wskaźnik w Sidebarze, Banner na „Dziś”).</summary>
    public double PeakPercent => new[] { Global }.Concat(PerSource)
        .SelectMany(line => new[] { line.Daily?.Percent ?? 0, line.Monthly?.Percent ?? 0 })
        .DefaultIfEmpty(0).Max();
}

public static class BudgetCalculator
{
    /// <summary>Prognoza pokazywana od tego dnia miesiąca (§5.9).</summary>
    public const int ForecastFromDay = 3;

    /// <param name="todayBySource">Koszt dziś per źródło.</param>
    /// <param name="monthBySource">Koszt od początku miesiąca per źródło.</param>
    /// <param name="last7BySource">Koszt 7 dni przed dziś per źródło (do średniej dziennej).</param>
    public static BudgetReport Evaluate(BudgetSettings budgets, DateOnly today, IReadOnlyDictionary<string, double> todayBySource,
        IReadOnlyDictionary<string, double> monthBySource, IReadOnlyDictionary<string, double> last7BySource)
    {
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var remaining = daysInMonth - today.Day;

        double? Forecast(double month, double last7) => today.Day >= ForecastFromDay ? month + last7 / 7 * remaining : null;

        var global = new BudgetLine(null,
            budgets.DailyUsd is > 0 ? new BudgetUsage(budgets.DailyUsd.Value, todayBySource.Values.Sum()) : null,
            budgets.MonthlyUsd is > 0 ? new BudgetUsage(budgets.MonthlyUsd.Value, monthBySource.Values.Sum()) : null,
            budgets.MonthlyUsd is > 0 ? Forecast(monthBySource.Values.Sum(), last7BySource.Values.Sum()) : null);
        var perSource = budgets.PerSource
            .Where(pair => pair.Value.DailyUsd is > 0 || pair.Value.MonthlyUsd is > 0)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var month = monthBySource.GetValueOrDefault(pair.Key);
                return new BudgetLine(pair.Key,
                    pair.Value.DailyUsd is > 0 ? new BudgetUsage(pair.Value.DailyUsd.Value, todayBySource.GetValueOrDefault(pair.Key)) : null,
                    pair.Value.MonthlyUsd is > 0 ? new BudgetUsage(pair.Value.MonthlyUsd.Value, month) : null,
                    pair.Value.MonthlyUsd is > 0 ? Forecast(month, last7BySource.GetValueOrDefault(pair.Key)) : null);
            })
            .ToList();
        return new BudgetReport(global, perSource, today);
    }
}
