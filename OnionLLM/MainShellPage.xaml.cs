using OnionLLM.Navigation;
using OnionLLM.Views;

namespace OnionLLM;

/// <summary>Main window host (DESIGN.md §2.9.3): sidebar, the current screen, status bar and overlay layers.</summary>
public partial class MainShellPage : ContentPage
{
    private readonly ShellViewModel _shell = ShellViewModel.Instance;
    private bool _started;

    public MainShellPage()
    {
        InitializeComponent();
        BindingContext = _shell;

        var navigation = _shell.Navigation;
        navigation.Register(Screen.Today, () => new TodayView());
        navigation.Register(Screen.Reports, () => new ReportsView());
        navigation.Register(Screen.Sessions, () => new SessionsView());
        navigation.Register(Screen.Blocks, () => new BlocksView());
        navigation.Register(Screen.Settings, () => new SettingsView());
#if DEBUG
        navigation.Register(Screen.Gallery, () => new GalleryView());
#endif
        navigation.Attach(Presenter);
        _shell.HideUnavailable();
        ShortcutService.BuildMenuBar(this, navigation.IsAvailable);

        SizeChanged += (_, _) => _shell.UpdateLayout(Width);
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
        _shell.StartRefreshLoop(Dispatcher);
        _shell.UpdateStatus();
        var start = Screen.Today;
#if DEBUG
        // Developer aid: ONIONLLM_START_SCREEN=<Screen> opens a screen at start-up (e.g. Gallery).
        if (Enum.TryParse<Screen>(Environment.GetEnvironmentVariable("ONIONLLM_START_SCREEN"), out var requested))
        {
            start = requested;
        }
#endif
        await _shell.NavigateAsync(start);
#if DEBUG
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
            foreach (var table in Find<Controls.DataTable>(_shell.Navigation.CurrentView))
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

#if DEBUG
    private static IEnumerable<T> Find<T>(Element? root)
    {
        if (root is null)
        {
            yield break;
        }

        if (root is T match)
        {
            yield return match;
        }

        foreach (var child in ((IElementController)root).LogicalChildren)
        {
            foreach (var found in Find<T>(child))
            {
                yield return found;
            }
        }
    }
#endif
}
