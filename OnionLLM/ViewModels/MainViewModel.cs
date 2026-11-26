using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Reports;
using OnionLLM.Design;

namespace OnionLLM.ViewModels;

/// <summary>Source card on „Dziś” (DESIGN.md §6.1 SourceCard).</summary>
public sealed class SourceCardViewModel : ObservableObject
{
    private bool _isDetailsExpanded;

    public SourceCardViewModel(SourceSummary summary)
    {
        Summary = summary;
        ToggleDetailsCommand = new Command(() => IsDetailsExpanded = !IsDetailsExpanded);
    }

    public SourceSummary Summary { get; }

    public Command ToggleDetailsCommand { get; }

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        private set
        {
            if (Set(ref _isDetailsExpanded, value))
            {
                OnPropertyChanged(nameof(DetailsToggleText));
            }
        }
    }

    public string DetailsToggleText => IsDetailsExpanded ? "Mniej szczegółów" : "Więcej szczegółów";

    public string Id => Summary.Id;

    public string Name => Summary.DisplayName;

    public string Description => Summary.Vendor;

    public string Initial => Summary.DisplayName[..1].ToUpperInvariant();

    /// <summary>Colour token of the accent bar and avatar (never the raw brand hex).</summary>
    public string AccentKey => DesignTokens.SourceKey(Id);

    /// <summary>Not detected or disabled: compact card with „Skonfiguruj” (§6.1).</summary>
    public bool IsCompact => !Summary.IsEnabled || !Summary.IsInstalled;

    public SourceState BadgeState => !Summary.IsEnabled ? SourceState.Disabled : !Summary.IsInstalled ? SourceState.NotDetected : SourceState.Normal;

    public string StatusText => !Summary.IsEnabled ? "Wyłączone"
        : !Summary.IsInstalled ? "Nie wykryto"
        : Summary.HasDataToday ? "Aktywne"
        : "Wykryte · brak użycia dziś";

    public StatusKind StatusKind => Summary.IsEnabled && Summary.IsInstalled && Summary.HasDataToday ? StatusKind.Live : StatusKind.Neutral;

    public double CostValue => Summary.Today.CostUsd;

    public string Marker => (Summary.HasUnpricedUsage ? "*" : "") + (Summary.HasEstimatedModel ? "~" : "");

    public double TokensValue => Summary.Today.TotalTokens;

    public long Input => Summary.Today.InputTokens;

    public long Output => Summary.Today.OutputTokens;

    public long CacheWrite => Summary.Today.CacheCreationTokens;

    public long CacheRead => Summary.Today.CacheReadTokens;

    public string SessionsText =>
        $"{Fmt.Count(Summary.ActiveSessions, "aktywna", "aktywne", "aktywnych")} · " +
        $"{Fmt.Count(Summary.CompletedSessions, "zakończona", "zakończone", "zakończonych")} · aktywna < {AppState.Config.ActiveSessionMinutes} min";

    public bool HasActiveSessions => Summary.ActiveSessions > 0;

    public IReadOnlyList<string> Models => Summary.TopModels;

    public bool HasModels => Summary.TopModels.Count > 0;

    /// <summary>Credits stored by the tool (Amp, Codebuff, Copilot) — today, 7 and 30 days.</summary>
    public bool HasCredits => Summary.Last30Days.Credits != 0;

    public string CreditsText => HasCredits
        ? $"Kredyty: dziś {Fmt.CreditsValue(Summary.Today.Credits)} · 7 dni {Fmt.CreditsValue(Summary.Last7Days.Credits)} · 30 dni {Fmt.CreditsValue(Summary.Last30Days.Credits)}"
        : "";

    public string RecentText => $"7 dni: {Fmt.Cost(Summary.Last7Days.CostUsd)} · {Fmt.TokensWithUnit(Summary.Last7Days.TotalTokens)}   " +
                                $"30 dni: {Fmt.Cost(Summary.Last30Days.CostUsd)} · {Fmt.TokensWithUnit(Summary.Last30Days.TotalTokens)}";

    public bool ShowLimits => Summary.Limits is not null;

    public LimitState LimitState => Summary.Limits is { IsAvailable: true } ? LimitState.Live : LimitState.Unavailable;

    public double FiveHourPercent => Summary.Limits is { IsAvailable: true } limits ? limits.FiveHourUtilization * 100 : double.NaN;

    public double SevenDayPercent => Summary.Limits is { IsAvailable: true } limits ? limits.SevenDayUtilization * 100 : double.NaN;

    public string FiveHourReset => ResetText(Summary.Limits?.FiveHourReset);

    public string SevenDayReset => ResetText(Summary.Limits?.SevenDayReset);

    public string AutomationId => "Today.SourceCard." + Id;

    internal static string ResetText(DateTimeOffset? reset) => reset is { } at
        ? $"Reset {Fmt.Countdown(at - DateTimeOffset.UtcNow)} · {Fmt.Time(at)}"
        : string.Empty;
}

/// <summary>Screen „Dziś” (DESIGN.md §6.1).</summary>
public sealed class MainViewModel : ObservableObject
{
    private const string ProviderOrderKey = "provider_order";
    private bool _isRefreshing;
    private bool _showUndetected;
    private bool _bannerClosed;
    private DashboardSnapshot? _snapshot;
    private IReadOnlyList<OnionRing> _rings = [];

    public MainViewModel()
    {
        ToggleUndetectedCommand = new Command(() =>
        {
            ShowUndetected = !ShowUndetected;
            ApplySnapshot();
        });
        ResetOrderCommand = new Command(() =>
        {
            Preferences.Remove(ProviderOrderKey);
            ApplySnapshot();
            SemanticScreenReader.Announce("Przywrócono domyślną kolejność źródeł.");
        });
        CloseBannerCommand = new Command(() => { _bannerClosed = true; OnPropertyChanged(nameof(HasBanner)); });
    }

    public ObservableCollection<SourceCardViewModel> Providers { get; } = [];

    public Command ToggleUndetectedCommand { get; }

    public Command ResetOrderCommand { get; }

    public Command CloseBannerCommand { get; }

    public bool IsLoading => _snapshot is null;

    /// <summary>PageHeader subtitle (§6.1): „czwartek, 26 listopada”.</summary>
    public string DateSubtitle => Fmt.LongDate(Fmt.NowLocal);

    // ===== Hero =====

    public double TodayCost => _snapshot?.Today.CostUsd ?? double.NaN;

    public string TodayMarker => _snapshot is null ? "" :
        (_snapshot.Sources.Any(source => source.HasUnpricedUsage) ? "*" : "") + (_snapshot.Sources.Any(source => source.HasEstimatedModel) ? "~" : "");

    public string TodayCaption => _snapshot switch
    {
        null => "",
        { Today.TotalTokens: 0 } => "Brak aktywności dziś",
        _ => $"{Fmt.TokensWithUnit(_snapshot.Today.TotalTokens)} · {Fmt.Count(SessionsToday, "sesja", "sesje", "sesji")}"
    };

    /// <summary>S-STALE: last successful sync older than 2× the refresh interval.</summary>
    public bool IsStale => AppState.LastSyncSucceededAt is { } last &&
                           DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(Math.Max(5, AppState.Config.RefreshIntervalSeconds) * 2);

    /// <summary>OnionRings (§4.4.3): 7 dni → 5 h → aktywny blok.</summary>
    public IReadOnlyList<OnionRing> LimitRings { get => _rings; private set => Set(ref _rings, value); }

    public bool HasLimitRings => LimitRings.Count > 0;

    // ===== KPI =====

    public string WeekCost => _snapshot is null ? "" : Fmt.Cost(_snapshot.Last7Days.CostUsd);

    public string WeekTokens => _snapshot is null ? "" : Fmt.TokensWithUnit(_snapshot.Last7Days.TotalTokens);

    public string MonthCost => _snapshot is null ? "" : Fmt.Cost(_snapshot.Last30Days.CostUsd);

    public string MonthTokens => _snapshot is null ? "" : Fmt.TokensWithUnit(_snapshot.Last30Days.TotalTokens);

    public string TodayTokens => _snapshot is null ? "" : Fmt.Tokens(_snapshot.Today.TotalTokens);

    public long TodayInput => _snapshot?.Today.InputTokens ?? 0;

    public long TodayOutput => _snapshot?.Today.OutputTokens ?? 0;

    public long TodayCacheWrite => _snapshot?.Today.CacheCreationTokens ?? 0;

    public long TodayCacheRead => _snapshot?.Today.CacheReadTokens ?? 0;

    public int ActiveSessions => _snapshot?.Sources.Sum(source => source.ActiveSessions) ?? 0;

    public int SessionsToday => _snapshot?.Sources.Sum(source => source.ActiveSessions + source.CompletedSessions) ?? 0;

    public string SessionsValue => _snapshot is null ? "" : Fmt.Number(SessionsToday);

    public string SessionsCaption => _snapshot is null ? "" :
        $"{Fmt.Count(ActiveSessions, "aktywna", "aktywne", "aktywnych")} · {Fmt.Count(SessionsToday - ActiveSessions, "zakończona", "zakończone", "zakończonych")}";

    public bool HasActiveSessions => ActiveSessions > 0;

    public string ActiveSessionsBadge => Fmt.Count(ActiveSessions, "aktywna", "aktywne", "aktywnych");

    // ===== Active Claude block =====

    public bool HasActiveBlock => _snapshot?.ActiveBlock is not null;

    public string BlockRemaining => _snapshot?.ActiveBlock is { } block
        ? $"Pozostało {Fmt.Duration(block.Remaining(DateTimeOffset.Now))} · do {Fmt.Time(block.End)}"
        : "";

    public double BlockCost => _snapshot?.ActiveBlock?.Totals.CostUsd ?? double.NaN;

    public string BlockTokens => _snapshot?.ActiveBlock is { } block ? Fmt.TokensWithUnit(block.Totals.TotalTokens) : "";

    public bool HasBurnRate => _snapshot?.ActiveBlock?.BurnRate is not null;

    public string BurnCostPerHour => _snapshot?.ActiveBlock?.BurnRate is { } burn ? Fmt.CostPerHour(burn.CostPerHour) : "";

    public string BurnTokensPerMinute => _snapshot?.ActiveBlock?.BurnRate is { } burn ? Fmt.TokensPerMinute(burn.TokensPerMinute) : "";

    public BurnRateLevel BurnLevel => _snapshot?.ActiveBlock?.BurnRate?.Level ?? BurnRateLevel.Normal;

    /// <summary>Elapsed share of the block for the timeline („teraz”).</summary>
    public double BlockNow => _snapshot?.ActiveBlock is { } block && block.End > block.Start
        ? Math.Clamp((DateTimeOffset.Now - block.Start) / (block.End - block.Start), 0, 1)
        : 1;

    /// <summary>Cumulative cost samples; only start and now are known (per-entry series: Wymaga danych).</summary>
    public IReadOnlyList<double> BlockSeries => _snapshot?.ActiveBlock is { } block ? [0, block.Totals.CostUsd] : [];

    public double BlockProjection => _snapshot?.ActiveBlock?.Projection?.TotalCostUsd ?? double.NaN;

    public string BlockProjectionText => _snapshot?.ActiveBlock?.Projection is { } projection
        ? $"Prognoza do końca bloku: {Fmt.Cost(projection.TotalCostUsd)} · {Fmt.TokensWithUnit(projection.TotalTokens)}"
        : "";

    public bool HasBlockLimit => _snapshot is { BlockTokenLimit: > 0 };

    public double BlockLimitPercent => _snapshot is { ActiveBlock: { } block, BlockTokenLimit: { } limit and > 0 }
        ? block.Totals.TotalTokens * 100.0 / limit
        : double.NaN;

    public string BlockLimitTitle => _snapshot is { BlockTokenLimit: { } limit and > 0 }
        ? $"Limit tokenów bloku · {Fmt.Tokens(limit)}{(AppState.Config.ParseTokenLimitSafe() is null ? " (maksimum z historii)" : "")}"
        : "Limit tokenów bloku";

    // ===== Sources =====

    public bool ShowUndetected
    {
        get => _showUndetected;
        private set
        {
            if (Set(ref _showUndetected, value))
            {
                OnPropertyChanged(nameof(UndetectedToggleText));
            }
        }
    }

    public string UndetectedToggleText => ShowUndetected ? "Ukryj niewykryte" : $"Pokaż niewykryte ({HiddenCount})";

    public int HiddenCount { get; private set; }

    // ===== Banner (§6.1 pkt 2: S-PARTIAL, S-NOLIMITS; one at a time, warning first) =====

    public int SkippedFiles => AppState.Engine.LastSync?.Sources.Sum(source => source.Problems.Count) ?? 0;

    public bool LimitsUnavailable => _snapshot?.Sources.FirstOrDefault(source => source.Id == "claude") is { IsInstalled: true, Limits: { IsAvailable: false } or null };

    public bool HasBanner => !_bannerClosed && (SkippedFiles > 0 || LimitsUnavailable);

    public BannerKind BannerKind => SkippedFiles > 0 ? BannerKind.Warning : BannerKind.Info;

    public string BannerTitle => SkippedFiles > 0 ? "Dane częściowe" : "Limity Claude są niedostępne";

    public string BannerMessage => SkippedFiles > 0
        ? $"Pominięto {Fmt.Count(SkippedFiles, "plik", "pliki", "plików")}, których nie udało się odczytać. Koszt może być zaniżony."
        : "Zaloguj się ponownie w Claude Code (claude /login), aby odczytać limity konta.";

    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }

    public string? Error { get; private set; }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await AppState.RefreshDataAsync(force, cancellationToken);
            _snapshot = await Task.Run(() => AppState.Engine.BuildDashboard(AppState.Limits), cancellationToken);
            Error = null;
            ApplySnapshot();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ConfigException)
        {
            Error = exception.Message;
            OnPropertyChanged(nameof(Error));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void SaveProviderOrder() =>
        Preferences.Set(ProviderOrderKey, string.Join('|', Providers.Select(provider => provider.Id)));

    /// <summary>Moves a card (menu, drag &amp; drop) and announces the new position (§5.11).</summary>
    public void Move(SourceCardViewModel card, int newIndex)
    {
        var index = Providers.IndexOf(card);
        newIndex = Math.Clamp(newIndex, 0, Providers.Count - 1);
        if (index < 0 || index == newIndex)
        {
            return;
        }

        Providers.Move(index, newIndex);
        SaveProviderOrder();
        SemanticScreenReader.Announce($"{card.Name} przeniesiono na pozycję {newIndex + 1} z {Providers.Count}");
    }

    private void ApplySnapshot()
    {
        if (_snapshot is null)
        {
            return;
        }

        var cards = _snapshot.Sources.Select(summary => new SourceCardViewModel(summary)).ToList();
        var visible = cards.Where(card => ShowUndetected || (card.Summary.IsEnabled && (card.Summary.IsInstalled || card.Summary.HasDataToday))).ToList();
        HiddenCount = cards.Count - visible.Count;

        Providers.Clear();
        foreach (var card in ApplySavedOrder(visible))
        {
            Providers.Add(card);
        }

        LimitRings = BuildRings();
        foreach (var name in new[]
                 {
                     nameof(IsLoading), nameof(DateSubtitle), nameof(TodayCost), nameof(TodayMarker), nameof(TodayCaption), nameof(IsStale),
                     nameof(HasLimitRings), nameof(WeekCost), nameof(WeekTokens), nameof(MonthCost), nameof(MonthTokens), nameof(TodayTokens),
                     nameof(TodayInput), nameof(TodayOutput), nameof(TodayCacheWrite), nameof(TodayCacheRead), nameof(ActiveSessions),
                     nameof(SessionsToday), nameof(SessionsValue), nameof(SessionsCaption), nameof(HasActiveSessions), nameof(ActiveSessionsBadge),
                     nameof(HasActiveBlock), nameof(BlockRemaining), nameof(BlockCost), nameof(BlockTokens), nameof(HasBurnRate),
                     nameof(BurnCostPerHour), nameof(BurnTokensPerMinute), nameof(BurnLevel), nameof(BlockNow), nameof(BlockSeries),
                     nameof(BlockProjection), nameof(BlockProjectionText), nameof(HasBlockLimit), nameof(BlockLimitPercent), nameof(BlockLimitTitle),
                     nameof(UndetectedToggleText), nameof(HiddenCount), nameof(SkippedFiles), nameof(LimitsUnavailable), nameof(HasBanner),
                     nameof(BannerKind), nameof(BannerTitle), nameof(BannerMessage)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private IReadOnlyList<OnionRing> BuildRings()
    {
        var claude = _snapshot?.Sources.FirstOrDefault(source => source.Id == "claude");
        if (claude is not { IsInstalled: true })
        {
            return [];
        }

        var limits = claude.Limits;
        var available = limits is { IsAvailable: true };
        var rings = new List<OnionRing>
        {
            new("7 dni", available ? limits!.SevenDayUtilization * 100 : double.NaN, "ColorChartCat1", SourceCardViewModel.ResetText(limits?.SevenDayReset)),
            new("5 h", available ? limits!.FiveHourUtilization * 100 : double.NaN, "ColorChartCat4", SourceCardViewModel.ResetText(limits?.FiveHourReset))
        };
        if (HasActiveBlock && HasBlockLimit)
        {
            rings.Add(new OnionRing("Aktywny blok", BlockLimitPercent, DesignTokens.SourceKey("claude")));
        }

        return rings;
    }

    private static IEnumerable<SourceCardViewModel> ApplySavedOrder(IEnumerable<SourceCardViewModel> providers)
    {
        var saved = Preferences.Get(ProviderOrderKey, string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);
        var positions = saved.Select((name, index) => (name, index))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        return providers
            .Select((provider, index) => (provider, index))
            .OrderBy(item => positions.TryGetValue(item.provider.Id, out var position) ? position : 1000 + item.index)
            .Select(item => item.provider);
    }
}

internal static class ConfigExtensions
{
    public static long? ParseTokenLimitSafe(this OnionConfig config)
    {
        try
        {
            return config.ParseTokenLimit();
        }
        catch (ConfigException)
        {
            return null;
        }
    }
}
