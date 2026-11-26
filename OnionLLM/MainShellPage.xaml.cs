using OnionLLM.Navigation;
using OnionLLM.Views;

namespace OnionLLM;

/// <summary>Main window host (DESIGN.md §2.9.3): sidebar, the current screen, status bar and overlay layers.</summary>
public partial class MainShellPage : ContentPage
{
    private readonly ShellViewModel _shell = ShellViewModel.Instance;

#if DEBUG
    internal int RootChildCount => Root.Children.Count;

    internal string RootChildren => string.Join(", ", Root.Children.Select(child => child.GetType().Name));
#endif
    private bool _started;

    public MainShellPage()
    {
        InitializeComponent();
        BindingContext = _shell;

        var navigation = _shell.Navigation;
        navigation.Register(Screen.Today, () => new TodayView());
        navigation.Register(Screen.Limits, () => new LimitsView());
        navigation.Register(Screen.Reports, () => new ReportsView());
        navigation.Register(Screen.Sessions, () => new SessionsView());
        navigation.Register(Screen.Blocks, () => new BlocksView());
        navigation.Register(Screen.Projects, () => new ProjectsView());
        navigation.Register(Screen.Models, () => new ModelsView());
        navigation.Register(Screen.Sources, () => new SourcesView());
        navigation.Register(Screen.Settings, () => new SettingsView());
#if DEBUG
        navigation.Register(Screen.Gallery, () => new GalleryView());
#endif
        navigation.Attach(Presenter);
        _shell.HideUnavailable();
        ShortcutService.BuildMenuBar(this, navigation.IsAvailable);

        SizeChanged += (_, _) => _shell.UpdateLayout(Width);
        SidebarColumn.Width = _shell.SidebarColumn;
        SidebarView.WidthRequest = _shell.SidebarWidth;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.SidebarColumn) or nameof(ShellViewModel.SidebarWidth))
            {
                AnimateSidebar();
            }
        };
        Design.ThemeService.Transition = apply => Design.Motion.CrossfadeAsync(Root, apply);
    }

    /// <summary>
    /// M18: sidebar width 240 ↔ 64 (base, standard); the content fades back in during the last 40% while labels
    /// appear or disappear. Reduced motion: the width changes at once.
    /// </summary>
    private void AnimateSidebar()
    {
        const string name = "M18";
        this.AbortAnimation(name);
        var fromColumn = SidebarColumn.Width.Value;
        var toColumn = _shell.SidebarColumn.Value;
        var fromWidth = SidebarView.WidthRequest > 0 ? SidebarView.WidthRequest : _shell.SidebarWidth;
        var toWidth = _shell.SidebarWidth;
        if (!Design.MotionTokens.AllowsTransforms || (fromColumn == toColumn && fromWidth == toWidth))
        {
            SidebarColumn.Width = _shell.SidebarColumn;
            SidebarView.WidthRequest = toWidth;
            SidebarView.Opacity = 1;
            return;
        }

        var muted = Design.DesignTokens.Double("OpacityMuted", 0.6);
        new Animation(t =>
        {
            SidebarColumn.Width = new GridLength(fromColumn + (toColumn - fromColumn) * t);
            SidebarView.WidthRequest = fromWidth + (toWidth - fromWidth) * t;
            SidebarView.Opacity = t < 0.6 ? muted : muted + (1 - muted) * (t - 0.6) / 0.4;
        }).Commit(this, name, length: Design.MotionTokens.Base, easing: Design.OnionEasing.Standard, finished: (_, _) =>
        {
            SidebarColumn.Width = _shell.SidebarColumn;
            SidebarView.WidthRequest = _shell.SidebarWidth;
            SidebarView.Opacity = 1;
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
        {
            return;
        }

        _started = true;
        _shell.UpdateLayout(Width);
        var start = Screen.Today;

        // S-FIRST (§7.5): the wizard covers the whole window (no sidebar); the refresh loop starts after it, so the
        // first sync already uses the chosen tools.
        if (OnboardingView.IsRequired)
        {
            var onboarding = new OnboardingView { ZIndex = 100 };
            Root.Children.Add(onboarding);
            start = await onboarding.Completion ?? Screen.Today;
            Root.Children.Remove(onboarding);
        }

        _shell.StartRefreshLoop(Dispatcher);
        _shell.UpdateStatus();
#if DEBUG
        // Developer aid: ONIONLLM_START_SCREEN=<Screen> opens a screen at start-up (e.g. Gallery).
        if (Enum.TryParse<Screen>(Environment.GetEnvironmentVariable("ONIONLLM_START_SCREEN"), out var requested))
        {
            start = requested;
        }
#endif
        await _shell.NavigateAsync(start);
#if DEBUG
        if (Environment.GetEnvironmentVariable("ONIONLLM_PALETTE_SELFTEST") is { Length: > 0 })
        {
            await DebugSelfTest.PaletteAsync(_shell);
        }

        if (Environment.GetEnvironmentVariable("ONIONLLM_UI_SELFTEST") is { Length: > 0 })
        {
            await DebugSelfTest.UiAsync(_shell);
        }

        // Developer aid: ONIONLLM_SCREEN_SMOKE=1 cycles breakpoints, theme and density on the start screen and logs it.
        if (Environment.GetEnvironmentVariable("ONIONLLM_SCREEN_SMOKE") is { Length: > 0 })
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            foreach (var width in new[] { 1000d, 700d, 1400d })
            {
                _shell.UpdateLayout(width);
                await Task.Delay(TimeSpan.FromMilliseconds(800));
                Diagnostics.Trace($"smoke: layout {_shell.Layout}");
            }

            Design.ThemeService.Apply(Core.Config.ThemePreference.Dark);
            Design.DensityService.Apply(Core.Config.DensityPreference.Compact);
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            Design.ThemeService.Apply(AppState.Config.Appearance.Theme);
            Design.DensityService.Apply(AppState.Config.Appearance.Density);
            await _shell.RefreshAsync(force: false);
            foreach (var table in ElementTree.Find<Controls.DataTable>(_shell.Navigation.CurrentView))
            {
                if (table.ItemsSource?.Cast<object>().FirstOrDefault() is { } first && table.RowActivatedCommand is { } open)
                {
                    open.Execute(first);
                    await Task.Delay(TimeSpan.FromMilliseconds(900));
                    Diagnostics.Trace($"smoke: drawer {(DrawerService.IsOpen ? "open" : "NOT OPEN")} for {table.AutomationPrefix}");
                    KeyboardRouter.Target = table;
                    KeyboardRouter.Route(KeyboardAction.Down);
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    DrawerService.Close();
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }
            }

            Diagnostics.Trace("smoke done: " + _shell.Navigation.Current);
        }
#endif
    }
}
