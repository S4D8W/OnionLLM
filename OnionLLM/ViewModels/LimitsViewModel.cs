using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Reports;
using OnionLLM.Design;

namespace OnionLLM.ViewModels;

/// <summary>Screen „Limity” (DESIGN.md §6.2): live limits, forecasts and 7-day history from the recorded readings.</summary>
public sealed class LimitsViewModel : ObservableObject
{
    private static readonly TimeSpan HistoryRange = TimeSpan.FromDays(7);
    private DashboardSnapshot? _snapshot;
    private IReadOnlyList<LimitSnapshot> _history = [];
    private bool _isBusy;

    public LimitsViewModel()
    {
        RefreshLimitsCommand = new Command(async () => await RefreshAsync(force: true));
    }

    public Command RefreshLimitsCommand { get; }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public string Subtitle => AppState.LimitsReadAt == DateTimeOffset.MinValue
        ? "Limity nie zostały jeszcze odczytane"
        : $"Ostatni odczyt limitów: {Fmt.Time(AppState.LimitsReadAt)} · co 5 min";

    private SourceSummary? Claude => _snapshot?.Sources.FirstOrDefault(source => source.Id == "claude");

    public bool HasClaude => Claude is { IsInstalled: true, IsEnabled: true };

    public bool LimitsAvailable => Claude?.Limits is { IsAvailable: true };

    public bool ShowNoLimitsBanner => HasClaude && !LimitsAvailable;

    public LimitState LimitState => LimitsAvailable ? LimitState.Live : LimitState.Unavailable;

    public string LiveTooltip => $"Dane na żywo z API Anthropic, odświeżone {Fmt.Time(AppState.LimitsReadAt)}";

    public double FiveHour => LimitsAvailable ? Claude!.Limits!.FiveHourUtilization * 100 : double.NaN;

    public double SevenDay => LimitsAvailable ? Claude!.Limits!.SevenDayUtilization * 100 : double.NaN;

    public string FiveHourReset => SourceCardViewModel.ResetText(Claude?.Limits?.FiveHourReset);

    public string SevenDayReset => SourceCardViewModel.ResetText(Claude?.Limits?.SevenDayReset);

    /// <summary>„Prognoza: 86% przed resetem” — linear forecast from the readings of the current window (§6.2).</summary>
    public string FiveHourForecast => ForecastText(Forecast(reading => reading.FiveHourPercent, Claude?.Limits?.FiveHourReset, TimeSpan.FromHours(5)));

    public string SevenDayForecast => ForecastText(Forecast(reading => reading.SevenDayPercent, Claude?.Limits?.SevenDayReset, TimeSpan.FromDays(7)));

    /// <summary>„Historia wykorzystania”: 5 h and 7 d percent over the last 7 days, hourly (last reading of each hour).</summary>
    public ChartData History
    {
        get
        {
            if (_history.Count < 2)
            {
                return ChartData.Empty;
            }

            var start = DateTimeOffset.Now - HistoryRange;
            var hours = (int)HistoryRange.TotalHours;
            var five = new double[hours];
            var seven = new double[hours];
            Array.Fill(five, double.NaN);
            Array.Fill(seven, double.NaN);
            foreach (var reading in _history)
            {
                var slot = (int)((reading.At - start).TotalHours);
                if (slot >= 0 && slot < hours)
                {
                    five[slot] = reading.FiveHourPercent;
                    seven[slot] = reading.SevenDayPercent;
                }
            }

            var times = Enumerable.Range(0, hours).Select(hour => Fmt.ToLocal(start.AddHours(hour))).ToList();
            return new ChartData
            {
                Categories = times.Select(time => Fmt.DayDate(time)).ToList(),
                CategoryDetails = times.Select(time => Fmt.DateTimeShort(time, Fmt.NowLocal)).ToList(),
                Series =
                [
                    new ChartSeries { Id = "5h", Label = "Limit 5-godzinny", ColorKey = "ColorChartCat4", Points = five },
                    new ChartSeries { Id = "7d", Label = "Limit 7-dniowy", ColorKey = "ColorChartCat1", Points = seven }
                ],
                Unit = ChartUnit.Percent,
                Thresholds = [new ChartThreshold(80, "80%", "ColorStatusWarning"), new ChartThreshold(100, "100%", "ColorStatusDanger")],
                Description = $"Historia wykorzystania limitów z {_history.Count} odczytów w ostatnich 7 dniach."
            };
        }
    }

    public bool HasHistory => _history.Count >= 2;

    // ===== §6.2 pkt 4 „Budżety” (§5.9) =====

    private BudgetReport? Budgets => AppState.Budgets;

    public bool HasBudgets => Budgets?.HasAny == true;

    public bool HasNoBudgets => !HasBudgets;

    public bool HasDailyBudget => Budgets?.Global.Daily is not null;

    public double DailyBudgetPercent => Budgets?.Global.Daily?.Percent ?? double.NaN;

    public string DailyBudgetText => Budgets?.Global.Daily is { } daily ? $"{Fmt.Cost(daily.SpentUsd)} z {Fmt.Cost(daily.LimitUsd)} dziś" : "";

    public bool HasMonthlyBudget => Budgets?.Global.Monthly is not null;

    public double MonthlyBudgetPercent => Budgets?.Global.Monthly?.Percent ?? double.NaN;

    public string MonthlyBudgetText => Budgets?.Global.Monthly is { } monthly ? $"{Fmt.Cost(monthly.SpentUsd)} z {Fmt.Cost(monthly.LimitUsd)} w tym miesiącu" : "";

    /// <summary>„Prognoza na koniec listopada: 312 $ (104% budżetu)” — from the 3rd day of the month (§5.9).</summary>
    public string MonthlyForecastText => Budgets is { Global: { Monthly: { } monthly, MonthForecastUsd: { } forecast } } budgets
        ? $"Prognoza na koniec {Fmt.MonthNameGenitive(budgets.Today.Month)}: {Fmt.Cost(forecast)} ({Fmt.Percent(forecast * 100 / monthly.LimitUsd)} budżetu)"
        : "";

    /// <summary>Per-source budgets (DataTable: źródło, budżet, wykorzystanie, prognoza).</summary>
    public IReadOnlyList<BudgetRowViewModel> SourceBudgets => Budgets?.PerSource.Select(line => new BudgetRowViewModel(line)).ToList() ?? [];

    public bool HasSourceBudgets => SourceBudgets.Count > 0;

    public Command OpenBudgetSettingsCommand { get; } = new(() => _ = Navigation.ShellViewModel.Instance.NavigateAsync(Navigation.Screen.Settings, "Budżety"));

    public string HistoryEmpty => "Historia pojawi się po kilku odczytach limitów (co 5 min, gdy aplikacja działa).";

    private LimitForecast? Forecast(Func<LimitSnapshot, double> value, DateTimeOffset? reset, TimeSpan window) =>
        LimitsAvailable ? LimitForecasting.Project(_history.Select(reading => (reading.At, value(reading))).ToList(), reset, window, DateTimeOffset.Now) : null;

    private static string ForecastText(LimitForecast? forecast) =>
        forecast is null ? "" : $"Prognoza: {Fmt.Percent(forecast.Percent)} przed resetem ({Fmt.Time(forecast.At)})";

    public bool HasActiveBlock => _snapshot?.ActiveBlock is not null;

    public bool HasBlockLimit => _snapshot is { ActiveBlock: not null, BlockTokenLimit: > 0 };

    public double BlockPercent => _snapshot is { ActiveBlock: { } block, BlockTokenLimit: { } limit and > 0 } ? block.Totals.TotalTokens * 100.0 / limit : double.NaN;

    public string BlockReset => _snapshot?.ActiveBlock is { } block ? $"Koniec bloku {Fmt.Countdown(block.Remaining(DateTimeOffset.Now))} · {Fmt.Time(block.End)}" : "";

    public string BlockProjection => _snapshot is { ActiveBlock.Projection: { } projection, BlockTokenLimit: { } limit and > 0 }
        ? $"Przy obecnym tempie: {Fmt.Percent(projection.TotalTokens * 100.0 / limit)} przed końcem bloku"
        : "";

    public bool HasBurnRate => _snapshot?.ActiveBlock?.BurnRate is not null;

    public string BurnCostPerHour => _snapshot?.ActiveBlock?.BurnRate is { } burn ? Fmt.CostPerHour(burn.CostPerHour) : "";

    public string BurnTokensPerMinute => _snapshot?.ActiveBlock?.BurnRate is { } burn ? Fmt.TokensPerMinute(burn.TokensPerMinute) : "";

    public BurnRateLevel BurnLevel => _snapshot?.ActiveBlock?.BurnRate?.Level ?? BurnRateLevel.Normal;

    public IReadOnlyList<OnionRing> Rings
    {
        get
        {
            if (!HasClaude)
            {
                return [];
            }

            var rings = new List<OnionRing>
            {
                new("7 dni", SevenDay, "ColorChartCat1", SevenDayReset),
                new("5 h", FiveHour, "ColorChartCat4", FiveHourReset)
            };
            if (HasBlockLimit)
            {
                rings.Add(new OnionRing("Aktywny blok", BlockPercent, DesignTokens.SourceKey("claude"), BlockReset));
            }

            return rings;
        }
    }

    /// <summary>Active sources without limit information (§6.2 pkt 5).</summary>
    public IReadOnlyList<SourceSummary> OtherSources =>
        _snapshot?.Sources.Where(source => source.Id != "claude" && source.IsEnabled && source.IsInstalled && source.Limits is null).ToList() ?? [];

    public bool HasOtherSources => OtherSources.Count > 0;

    public async Task RefreshAsync(bool force = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await AppState.RefreshDataAsync(force);
            _snapshot = await Task.Run(() => AppState.Engine.BuildDashboard(AppState.Limits));
            _history = await Task.Run(() => AppState.Engine.Ledger.LimitHistory("claude", DateTimeOffset.Now - HistoryRange));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(string.Empty);
        }
    }
}

/// <summary>Row of the per-source budget table on Limity (§6.2 pkt 4).</summary>
public sealed class BudgetRowViewModel(BudgetLine line)
{
    public string Source => AppState.Engine.FindSource(line.Source ?? "")?.DisplayName ?? line.Source ?? "";

    public string SourceId => line.Source ?? "";

    public string Daily => line.Daily is { } daily ? $"{Fmt.Cost(daily.SpentUsd)} / {Fmt.Cost(daily.LimitUsd)}" : "—";

    public string Monthly => line.Monthly is { } monthly ? $"{Fmt.Cost(monthly.SpentUsd)} / {Fmt.Cost(monthly.LimitUsd)}" : "—";

    public string Usage => Fmt.Percent(Math.Max(line.Daily?.Percent ?? 0, line.Monthly?.Percent ?? 0));

    public double UsageShare => Math.Max(line.Daily?.Percent ?? 0, line.Monthly?.Percent ?? 0) / 100;

    public string Forecast => line.MonthForecastUsd is { } forecast ? Fmt.Cost(forecast) : "—";
}
