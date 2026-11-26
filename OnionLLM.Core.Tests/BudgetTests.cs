using OnionLLM.Core.Config;
using OnionLLM.Core.Reports;

namespace OnionLLM.Core.Tests;

/// <summary>Budgets (DESIGN.md §5.9): usage, month-end forecast from the 3rd day, per-source budgets, validation.</summary>
public class BudgetTests
{
    private static readonly Dictionary<string, double> Today = new() { ["claude"] = 6, ["codex"] = 2 };
    private static readonly Dictionary<string, double> Month = new() { ["claude"] = 60, ["codex"] = 20 };
    private static readonly Dictionary<string, double> Last7 = new() { ["claude"] = 35, ["codex"] = 7 };

    [Fact]
    public void Global_budgets_sum_all_sources_and_forecast_the_month()
    {
        var budgets = new BudgetSettings { DailyUsd = 10, MonthlyUsd = 200 };

        var report = BudgetCalculator.Evaluate(budgets, new DateOnly(2026, 11, 20), Today, Month, Last7);

        Assert.Equal(80, report.Global.Daily!.Percent, 3);
        Assert.Equal(80, report.Global.Monthly!.SpentUsd, 3);
        Assert.Equal(80 + 42.0 / 7 * 10, report.Global.MonthForecastUsd!.Value, 3); // 10 days left in November
        Assert.True(report.HasAny);
        Assert.Equal(80, report.PeakPercent, 3);
    }

    [Fact]
    public void Forecast_starts_on_the_third_day_and_sources_have_their_own_budgets()
    {
        var budgets = new BudgetSettings { MonthlyUsd = 100, PerSource = { ["codex"] = new SourceBudget { DailyUsd = 1 } } };

        var report = BudgetCalculator.Evaluate(budgets, new DateOnly(2026, 11, 2), Today, Month, Last7);

        Assert.Null(report.Global.MonthForecastUsd);
        Assert.Null(report.Global.Daily);
        var codex = Assert.Single(report.PerSource);
        Assert.Equal("codex", codex.Source);
        Assert.Equal(200, codex.Daily!.Percent, 3);
        Assert.Equal(200, report.PeakPercent, 3);
    }

    [Fact]
    public void No_budgets_and_invalid_amounts()
    {
        Assert.False(BudgetCalculator.Evaluate(new BudgetSettings(), new DateOnly(2026, 11, 20), Today, Month, Last7).HasAny);
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new OnionConfig { Budgets = new BudgetSettings { DailyUsd = 0 } }));
        ConfigLoader.Validate(new OnionConfig { Budgets = new BudgetSettings { DailyUsd = 5, MonthlyUsd = null } });
    }
}
