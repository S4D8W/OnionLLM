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
    }
}
