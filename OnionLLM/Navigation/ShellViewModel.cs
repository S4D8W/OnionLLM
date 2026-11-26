using System.Collections.ObjectModel;
using OnionLLM.Core.Pricing;
using OnionLLM.Design;
using OnionLLM.ViewModels;

namespace OnionLLM.Navigation;

/// <summary>Window width class (DESIGN.md §2.9.2).</summary>
public enum LayoutState
{
    Wide,
    Medium,
    Narrow
}

/// <summary>One sidebar entry.</summary>
public sealed class NavEntry(ScreenInfo info, Command command) : ObservableObject
{
    private bool _isActive;

    public ScreenInfo Info { get; } = info;

    public string Label => Info.Label;

    public string Glyph => Info.Glyph;

    public string Shortcut => Info.Shortcut;

    public Command Command { get; } = command;

    public string AutomationId => "Sidebar." + Info.Screen;

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}

/// <summary>
/// State of the main window (§2.9): layout breakpoint, sidebar (240 / rail 64 / narrow 56, Ctrl+B), navigation,
/// the status bar (§4.7.5) and the refresh loop (§5.7). Single instance per process.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const string SidebarCollapsedKey = "shell.sidebarCollapsed";

    private LayoutState _layout = LayoutState.Wide;
    private bool _collapsedByUser;
    private bool _isOverlayOpen;
    private bool _isRefreshing;
    private bool _hadSyncError;
    private IDispatcherTimer? _timer;

    private ShellViewModel()
    {
        _collapsedByUser = ViewModels.ViewState.GetBool(SidebarCollapsedKey, false);
        Navigation = new NavigationService();
        Navigation.Navigated += (_, _) => UpdateActive();

        foreach (var info in ScreenInfo.All)
        {
            var entry = new NavEntry(info, new Command(async () => await NavigateAsync(info.Screen)));
            (info.Group switch
            {
                NavGroup.Overview => OverviewItems,
                NavGroup.Analysis => AnalysisItems,
                NavGroup.System => SystemItems,
                _ => FooterItems
            }).Add(entry);
        }

        RefreshCommand = new Command(async () => await RefreshAsync(force: true));
        ToggleSidebarCommand = new Command(ToggleSidebar);
        OpenSidebarCommand = new Command(() => IsOverlayOpen = true);
        CloseOverlayCommand = new Command(() => IsOverlayOpen = false);
        OpenDiagnosticsCommand = new Command(async () => await NavigateAsync(Screen.Settings));

        AppState.SyncStateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnSyncStateChanged);
        AppState.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            RestartTimer();
            UpdateStatus();
        });
        ThemeService.ThemeChanged += (_, _) => UpdateStatus();
        DensityService.DensityChanged += (_, _) => OnLayoutChanged();
        ShortcutService.Invoked += (_, action) => MainThread.BeginInvokeOnMainThread(async () => await OnShortcutAsync(action));
    }

    public static ShellViewModel Instance { get; } = new();

    public NavigationService Navigation { get; }

    public ObservableCollection<NavEntry> OverviewItems { get; } = [];

    public ObservableCollection<NavEntry> AnalysisItems { get; } = [];

    public ObservableCollection<NavEntry> SystemItems { get; } = [];

    public ObservableCollection<NavEntry> FooterItems { get; } = [];

    public Command RefreshCommand { get; }

    public Command ToggleSidebarCommand { get; }

    public Command OpenSidebarCommand { get; }

    public Command CloseOverlayCommand { get; }

    public Command OpenDiagnosticsCommand { get; }

    // ===== Layout and sidebar (§2.9.2, §4.7.1, M18) =====

    public LayoutState Layout
    {
        get => _layout;
        private set
        {
            if (Set(ref _layout, value))
            {
                _isOverlayOpen = false;
                OnLayoutChanged();
            }
        }
    }

    public bool IsNarrow => Layout == LayoutState.Narrow;

    /// <summary>Sidebar shows labels (expanded in place or as an overlay).</summary>
    public bool IsSidebarExpanded => IsOverlayOpen || (Layout == LayoutState.Wide && !_collapsedByUser);

    public bool IsSidebarCollapsed => !IsSidebarExpanded;

    /// <summary>Expanded sidebar drawn over the content (medium/narrow, Ctrl+B or the header button).</summary>
    public bool IsOverlayOpen
    {
        get => _isOverlayOpen;
        set
        {
            if (Set(ref _isOverlayOpen, value))
            {
                OnLayoutChanged();
            }
        }
    }

    /// <summary>Width reserved in the grid for the sidebar.</summary>
    public GridLength SidebarColumn => new(Layout switch
    {
        LayoutState.Narrow => Token("SizeSidebarNarrow", 56),
        LayoutState.Medium => Token("SizeSidebarRail", 64),
        _ => _collapsedByUser ? Token("SizeSidebarRail", 64) : Token("SizeSidebarWide", 240)
    });

    /// <summary>Actual sidebar width (wider than its column while the overlay is open).</summary>
    public double SidebarWidth => IsSidebarExpanded ? Token("SizeSidebarWide", 240) : SidebarColumn.Value;

    public int SidebarColumnSpan => IsOverlayOpen ? 2 : 1;

    public string ToggleSidebarLabel => IsSidebarExpanded ? "Zwiń panel" : "Rozwiń panel";

    public string ToggleSidebarGlyph => IsSidebarExpanded ? Resources.Icons.PanelLeftClose : Resources.Icons.PanelLeftOpen;

    public string ToggleSidebarTooltip => $"{ToggleSidebarLabel} ({ShortcutService.Label("B")})";

    public string ToggleSidebarShortcut => ShortcutService.Label("B");

    public void UpdateLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        Layout = width >= Token("BreakpointWide", 1280) ? LayoutState.Wide
            : width >= Token("BreakpointMedium", 900) ? LayoutState.Medium
            : LayoutState.Narrow;
    }

    private void ToggleSidebar()
    {
        if (Layout == LayoutState.Wide)
        {
            _collapsedByUser = !_collapsedByUser;
            ViewModels.ViewState.Set(SidebarCollapsedKey, _collapsedByUser);
            OnLayoutChanged();
        }
        else
        {
            IsOverlayOpen = !IsOverlayOpen;
        }
    }

    private void OnLayoutChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsNarrow), nameof(IsSidebarExpanded), nameof(IsSidebarCollapsed), nameof(IsOverlayOpen),
                     nameof(SidebarColumn), nameof(SidebarWidth), nameof(SidebarColumnSpan), nameof(ToggleSidebarLabel),
                     nameof(ToggleSidebarGlyph), nameof(ToggleSidebarTooltip)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ===== Navigation =====

    public async Task NavigateAsync(Screen screen, object? parameter = null)
    {
        IsOverlayOpen = false;
        if (!Navigation.IsAvailable(screen))
        {
            return;
        }

        await Navigation.NavigateAsync(screen, parameter);
    }

    private void UpdateActive()
    {
        foreach (var entry in OverviewItems.Concat(AnalysisItems).Concat(SystemItems).Concat(FooterItems))
        {
            entry.IsActive = entry.Info.Screen == Navigation.Current;
        }
    }

    /// <summary>Hides sidebar entries of screens that are not built yet (DESIGN.md §10.1 pkt 7 — no fake screens).</summary>
    public void HideUnavailable()
    {
        foreach (var list in new[] { OverviewItems, AnalysisItems, SystemItems, FooterItems })
        {
            foreach (var entry in list.Where(entry => !Navigation.IsAvailable(entry.Info.Screen)).ToList())
            {
                list.Remove(entry);
            }
        }

        OnPropertyChanged(nameof(HasSystemItems));
    }

    public bool HasSystemItems => SystemItems.Count > 0;

    private async Task OnShortcutAsync(ShortcutAction action)
    {
        if (DialogService.Current is { } dialog)
        {
            if (action.Command == ShortcutCommand.Escape)
            {
                dialog.Complete(false);
            }

            return;
        }

        switch (action.Command)
        {
            case ShortcutCommand.Navigate when action.Screen is { } screen:
                await NavigateAsync(screen);
                break;
            case ShortcutCommand.Settings:
                await NavigateAsync(Screen.Settings);
                break;
            case ShortcutCommand.Refresh:
                await RefreshAsync(force: true);
                break;
            case ShortcutCommand.ToggleSidebar:
                ToggleSidebar();
                break;
            case ShortcutCommand.Back:
                await Navigation.GoBackAsync();
                break;
            case ShortcutCommand.Forward:
                await Navigation.GoForwardAsync();
                break;
            case ShortcutCommand.Escape:
                IsOverlayOpen = false;
                break;
        }
    }

    // ===== Refresh loop (§5.7) =====

    /// <summary>Refresh icon spins (M15) while a sync or a screen reload runs.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing || AppState.IsSyncing;
        private set
        {
            if (Set(ref _isRefreshing, value))
            {
                OnPropertyChanged();
            }
        }
    }

    public void StartRefreshLoop(IDispatcher dispatcher)
    {
        _timer ??= dispatcher.CreateTimer();
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        RestartTimer();
        dispatcher.StartTimer(TimeSpan.FromSeconds(30), () =>
        {
            UpdateStatus(); // keeps „5 min temu” and the stale state current
            return true;
        });
    }

    private void RestartTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, AppState.Config.RefreshIntervalSeconds));
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e) => await RefreshAsync(force: false);

    public async Task RefreshAsync(bool force)
    {
        if (_isRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            if (Navigation.CurrentView is IScreenView screen)
            {
                await screen.RefreshAsync(force);
            }
            else
            {
                await AppState.RefreshDataAsync(force);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Reported through AppState.LastSyncError → StatusBar + Toast.
        }
        finally
        {
            IsRefreshing = false;
            UpdateStatus();
        }
    }

    private void OnSyncStateChanged()
    {
        OnPropertyChanged(nameof(IsRefreshing));
        var hasError = AppState.LastSyncError is not null;
        if (hasError && !_hadSyncError)
        {
            // §5.7: one toast per series of errors.
            ToastService.Show("Nie udało się odświeżyć danych. Poprzednie dane są nadal widoczne.", ToastKind.Error);
        }

        _hadSyncError = hasError;
        UpdateStatus();
    }

    // ===== Status bar (§4.7.5) =====

    public string SyncText { get; private set; } = "Aktualizowanie…";

    /// <summary>Colour token key of the sync item: tertiary, warning (stale) or danger (error).</summary>
    public string SyncColorKey { get; private set; } = "ColorTextTertiary";

    public string SyncTooltip { get; private set; } = "";

    public string SourcesText { get; private set; } = "";

    public string SkippedText { get; private set; } = "";

    public bool HasSkipped { get; private set; }

    public string PricingText { get; private set; } = "";

    public string PricingColorKey { get; private set; } = "ColorTextTertiary";

    public bool IsAlwaysOnTop => AppState.Config.Window.AlwaysOnTop;

    public void UpdateStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromSeconds(Math.Max(5, AppState.Config.RefreshIntervalSeconds));
        if (AppState.ConfigError is { } configError)
        {
            SyncText = "Błąd konfiguracji — Szczegóły";
            SyncColorKey = "ColorStatusDanger";
            SyncTooltip = configError;
        }
        else if (AppState.IsSyncing)
        {
            SyncText = "Aktualizowanie…";
            SyncColorKey = "ColorTextTertiary";
            SyncTooltip = "";
        }
        else if (AppState.LastSyncError is { } syncError)
        {
            SyncText = "Błąd aktualizacji — Szczegóły";
            SyncColorKey = "ColorStatusDanger";
            SyncTooltip = syncError;
        }
        else if (AppState.LastSyncSucceededAt is { } last)
        {
            var stale = now - last > interval * 2;
            SyncText = stale
                ? $"Zaktualizowano {Fmt.Relative(last)}"
                : $"Zaktualizowano {Fmt.Time(last)}";
            SyncColorKey = stale ? "ColorStatusWarning" : "ColorTextTertiary";
            SyncTooltip = "";
        }

        var sync = AppState.Engine.LastSync;
        var enabled = AppState.Engine.Sources.Where(source => AppState.Config.IsSourceEnabled(source.Id)).ToList();
        var detected = sync?.Sources.Count(report => report.Files > 0 && enabled.Any(source => source.Id == report.Source)) ?? 0;
        var total = AppState.Engine.Sources.Count;
        SourcesText = $"{detected} z {total} {Fmt.Plural(total, "aktywnego", "aktywnych", "aktywnych")}";

        var skipped = sync?.Sources.Sum(report => report.Problems.Count) ?? 0;
        HasSkipped = skipped > 0;
        SkippedText = $"{Fmt.Number(skipped)} {Fmt.Plural(skipped, "plik pominięty", "pliki pominięte", "plików pominiętych")}";

        var pricing = AppState.Engine.Pricing;
        if (AppState.Config.Offline)
        {
            PricingText = "Cennik offline";
            PricingColorKey = "ColorStatusWarning";
        }
        else if (pricing.UpdatedAt is { } updated && now - updated > TimeSpan.FromDays(7))
        {
            var days = (int)(now - updated).TotalDays;
            PricingText = $"Cennik nieaktualny ({days} {Fmt.Plural(days, "dzień", "dni", "dni")})";
            PricingColorKey = "ColorStatusWarning";
        }
        else
        {
            var date = pricing.UpdatedAt is { } at ? ", " + Fmt.ToLocal(at).ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture) : "";
            PricingText = pricing.Origin == PricingOrigin.Embedded ? "Cennik: wbudowany" + date : "Cennik: LiteLLM" + date;
            PricingColorKey = "ColorTextTertiary";
        }

        foreach (var name in new[]
                 {
                     nameof(SyncText), nameof(SyncColorKey), nameof(SyncTooltip), nameof(SourcesText), nameof(SkippedText),
                     nameof(HasSkipped), nameof(PricingText), nameof(PricingColorKey), nameof(IsAlwaysOnTop), nameof(IsRefreshing)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private static double Token(string key, double fallback) => DesignTokens.Double(key, fallback);
}
