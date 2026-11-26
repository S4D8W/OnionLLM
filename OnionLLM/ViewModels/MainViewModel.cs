using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

/// <summary>Karta źródła na ekranie „Dziś”.</summary>
public sealed class SourceCardViewModel : ObservableObject
{
    private bool _isDetailsExpanded;

    public SourceCardViewModel(SourceSummary summary)
    {
        Summary = summary;
        ToggleDetailsCommand = new Command(() =>
        {
            IsDetailsExpanded = !IsDetailsExpanded;
            OnPropertyChanged(nameof(DetailsToggleText));
        });
    }

    public SourceSummary Summary { get; }
    public Command ToggleDetailsCommand { get; }
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        private set => Set(ref _isDetailsExpanded, value);
    }
    public string DetailsToggleText => IsDetailsExpanded ? "Mniej szczegółów" : "Więcej szczegółów";

    public string Id => Summary.Id;
    public string Name => Summary.DisplayName;
    public string Description => Summary.Vendor;
    public string AccentColor => Summary.AccentColor;
    public string Initial => Summary.DisplayName[..1].ToUpperInvariant();

    public string TotalTokensDisplay => Summary.HasDataToday ? Fmt.Tokens(Summary.Today.TotalTokens) : "—";
    public string CostDisplay => Summary.HasDataToday
        ? Fmt.Cost(Summary.Today.CostUsd) + (Summary.HasUnpricedUsage ? "*" : "") + (Summary.HasEstimatedModel ? "~" : "")
        : "";

    /// <summary>Kredyty zapisane przez narzędzie (Amp, Codebuff, Copilot) — dziś, 7 i 30 dni; widoczne, gdy niezerowe.</summary>
    public bool HasCredits => Summary.Last30Days.Credits != 0;
    public string CreditsDisplay => HasCredits
        ? $"Kredyty: dziś {Fmt.Credits(Summary.Today.Credits)} · 7 dni {Fmt.Credits(Summary.Last7Days.Credits)} · 30 dni {Fmt.Credits(Summary.Last30Days.Credits)}"
        : "";
    public string InputDisplay => Fmt.Tokens(Summary.Today.InputTokens);
    public string OutputDisplay => Fmt.Tokens(Summary.Today.OutputTokens);
    public string CacheWriteDisplay => Fmt.Tokens(Summary.Today.CacheCreationTokens);
    public string CacheReadDisplay => Fmt.Tokens(Summary.Today.CacheReadTokens);

    public double CacheShare => Summary.Today.TotalTokens > 0
        ? (double)Summary.Today.CacheReadTokens / Summary.Today.TotalTokens
        : 0;

    public string CacheShareDisplay => Summary.HasDataToday ? $"{CacheShare:P0} z cache" : "";

    public string ModelsDisplay => Summary.TopModels.Count > 0
        ? string.Join(" · ", Summary.TopModels.Select(Fmt.Model))
        : "brak aktywności dziś";

    public int ActiveSessions => Summary.ActiveSessions;
    public int CompletedSessions => Summary.CompletedSessions;
    public string ActiveWindowDisplay => $"aktywny < {AppState.Config.ActiveSessionMinutes} min";

    public string Last7Display => $"{Fmt.Tokens(Summary.Last7Days.TotalTokens)} · {Fmt.Cost(Summary.Last7Days.CostUsd)}";
    public string Last30Display => $"{Fmt.Tokens(Summary.Last30Days.TotalTokens)} · {Fmt.Cost(Summary.Last30Days.CostUsd)}";

    public bool HasLimits => Summary.Limits is { IsAvailable: true };
    public bool ShowLimitsBox => Summary.Limits is not null;
    public double FiveHourProgress => Summary.Limits?.FiveHourUtilization ?? 0;
    public double SevenDayProgress => Summary.Limits?.SevenDayUtilization ?? 0;
    public string FiveHourDisplay => HasLimits ? $"{FiveHourProgress:P0}" : "n/d";
    public string SevenDayDisplay => HasLimits ? $"{SevenDayProgress:P0}" : "n/d";
    public string FiveHourReset => Summary.Limits?.FiveHourReset is { } reset ? "reset " + Fmt.LocalTime(reset, "HH:mm, dd.MM") : "brak danych";
    public string SevenDayReset => Summary.Limits?.SevenDayReset is { } reset ? "reset " + Fmt.LocalTime(reset, "HH:mm, dd.MM") : "brak danych";
    public string LimitStatus => Summary.Limits?.Status ?? "";

    public string Status => !Summary.IsEnabled
        ? "Wyłączone w ustawieniach"
        : !Summary.IsInstalled
            ? "Nie wykryto"
            : Summary.HasDataToday
                ? (Summary.HasUnpricedUsage ? "Aktywne · * część modeli bez ceny" : Summary.HasEstimatedModel ? "Aktywne · ~ ceny szacowane" : "Aktywne · dane lokalne")
                : "Wykryto · brak użycia dziś";
}

public sealed class MainViewModel : ObservableObject
{
    private const string ProviderOrderKey = "provider_order";
    private bool _isRefreshing;
    private bool _showUndetected;
    private string _lastUpdated = "Jeszcze nie odświeżono";
    private string _todayTokens = "0";
    private string _todayCost = "$0.00";
    private string _activeProviders = "";
    private string _weekSummary = "";
    private string _monthSummary = "";
    private DashboardSnapshot? _snapshot;

    public MainViewModel()
    {
        RefreshCommand = new Command(async () => await RefreshAsync(force: true), () => !IsRefreshing);
        ToggleUndetectedCommand = new Command(() =>
        {
            ShowUndetected = !ShowUndetected;
            ApplySnapshot();
        });
    }

    public ObservableCollection<SourceCardViewModel> Providers { get; } = [];

    public Command RefreshCommand { get; }

    public Command ToggleUndetectedCommand { get; }

    public string TodayTokens { get => _todayTokens; private set => Set(ref _todayTokens, value); }
    public string TodayCost { get => _todayCost; private set => Set(ref _todayCost, value); }
    public string ActiveProviders { get => _activeProviders; private set => Set(ref _activeProviders, value); }
    public string WeekSummary { get => _weekSummary; private set => Set(ref _weekSummary, value); }
    public string MonthSummary { get => _monthSummary; private set => Set(ref _monthSummary, value); }
    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }

    /// <summary>PageHeader subtitle (§6.1): „czwartek, 26 listopada”.</summary>
    public string DateSubtitle => Fmt.LongDate(Fmt.NowLocal);

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

    public string UndetectedToggleText => ShowUndetected ? "Ukryj niewykryte źródła" : $"Pokaż niewykryte źródła ({HiddenCount})";

    public int HiddenCount { get; private set; }

    // Aktywny blok Claude.
    public bool HasActiveBlock => _snapshot?.ActiveBlock is not null;
    public string BlockRemaining => _snapshot?.ActiveBlock is { } block ? Fmt.Duration(block.Remaining(DateTimeOffset.Now)) : "";
    public string BlockEnds => _snapshot?.ActiveBlock is { } block ? "do " + Fmt.LocalTime(block.End, "HH:mm") : "";
    public string BlockCost => _snapshot?.ActiveBlock is { } block ? Fmt.Cost(block.Totals.CostUsd) : "";
    public string BlockTokens => _snapshot?.ActiveBlock is { } block ? Fmt.Tokens(block.Totals.TotalTokens) + " tokenów" : "";
    public string BurnRate => _snapshot?.ActiveBlock?.BurnRate is { } burn
        ? $"{Fmt.Cost(burn.CostPerHour)}/h · {Fmt.Number((long)burn.TokensPerMinute)} tok/min"
        : "za mało danych";
    public string BurnLevel => _snapshot?.ActiveBlock?.BurnRate is { } burn ? Fmt.BurnLevel(burn.Level) : "";
    public Color BurnColor => Design.DesignTokens.Color(_snapshot?.ActiveBlock?.BurnRate is { } burn
        ? Fmt.BurnColorKey(burn.Level)
        : "ColorTextTertiary");
    public string Projection => _snapshot?.ActiveBlock?.Projection is { } projection
        ? $"prognoza: {Fmt.Tokens(projection.TotalTokens)} · {Fmt.Cost(projection.TotalCostUsd)}"
        : "";
    public double BlockLimitProgress => _snapshot is { ActiveBlock: { } block, BlockTokenLimit: > 0 }
        ? Math.Clamp((double)block.Totals.TotalTokens / _snapshot.BlockTokenLimit!.Value, 0, 1)
        : 0;
    public string BlockLimitDisplay => _snapshot is { ActiveBlock: { } block, BlockTokenLimit: { } limit and > 0 }
        ? $"{block.Totals.TotalTokens * 100.0 / limit:0}% limitu {Fmt.Tokens(limit)}{(AppState.Config.ParseTokenLimitSafe() is null ? " (max)" : "")}"
        : "brak limitu";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (Set(ref _isRefreshing, value))
            {
                RefreshCommand.ChangeCanExecute();
            }
        }
    }

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
            ApplySnapshot();
            LastUpdated = AppState.ConfigError is { } error
                ? "Błąd konfiguracji: " + error
                : $"Ostatnia aktualizacja {DateTime.Now:HH:mm}" + ProblemsSuffix();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ConfigException)
        {
            LastUpdated = "Błąd odczytu danych: " + exception.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void SaveProviderOrder() =>
        Preferences.Set(ProviderOrderKey, string.Join('|', Providers.Select(provider => provider.Id)));

    private static string ProblemsSuffix()
    {
        var problems = AppState.Engine.LastSync?.Sources.Sum(source => source.Problems.Count) ?? 0;
        return problems > 0 ? $" · {problems} plików nieczytelnych (Ustawienia → Diagnostyka)" : "";
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

        TodayTokens = Fmt.Tokens(_snapshot.Today.TotalTokens);
        TodayCost = Fmt.Cost(_snapshot.Today.CostUsd) +
                    (_snapshot.Sources.Any(source => source.HasUnpricedUsage) ? "*" : "") +
                    (_snapshot.Sources.Any(source => source.HasEstimatedModel) ? "~" : "");
        ActiveProviders = $"{cards.Count(card => card.Summary.HasDataToday)} z {cards.Count(card => card.Summary.IsInstalled)} aktywnych";
        WeekSummary = $"7 dni: {Fmt.Cost(_snapshot.Last7Days.CostUsd)}";
        MonthSummary = $"30 dni: {Fmt.Cost(_snapshot.Last30Days.CostUsd)}";

        OnPropertyChanged(nameof(DateSubtitle));
        OnPropertyChanged(nameof(UndetectedToggleText));
        OnPropertyChanged(nameof(HiddenCount));
        foreach (var name in new[]
                 {
                     nameof(HasActiveBlock), nameof(BlockRemaining), nameof(BlockEnds), nameof(BlockCost), nameof(BlockTokens), nameof(BurnRate),
                     nameof(BurnLevel), nameof(BurnColor), nameof(Projection), nameof(BlockLimitProgress), nameof(BlockLimitDisplay)
                 })
        {
            OnPropertyChanged(name);
        }
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
