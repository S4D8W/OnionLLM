using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core;
using OnionLLM.Core.Reports;
using OnionLLM.Design;

namespace OnionLLM.ViewModels;

/// <summary>Screen „Limity” (DESIGN.md §6.2). Usage history, budgets and notification summary need data (§11.4) and are not rendered.</summary>
public sealed class LimitsViewModel : ObservableObject
{
    private DashboardSnapshot? _snapshot;
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
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(string.Empty);
        }
    }
}
