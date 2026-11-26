using OnionLLM.Design;

namespace OnionLLM.Navigation;

/// <summary>
/// Screen navigation inside the main window (DESIGN.md §2.9.3, §5.5): one cached view per screen (state survives
/// switching), back/forward history of max 20 entries for Alt+← / Alt+→ and the M2 page transition.
/// </summary>
public sealed class NavigationService
{
    private const int HistoryLimit = 20;

    private readonly Dictionary<Screen, Func<View>> _factories = [];
    private readonly Dictionary<Screen, View> _views = [];
    private readonly List<Screen> _back = [];
    private readonly List<Screen> _forward = [];
    private ContentView? _presenter;
    private int _version;

    public event EventHandler? Navigated;

    public Screen? Current { get; private set; }

    public View? CurrentView => Current is { } screen && _views.TryGetValue(screen, out var view) ? view : null;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public void Register(Screen screen, Func<View> factory) => _factories[screen] = factory;

    public bool IsAvailable(Screen screen) => _factories.ContainsKey(screen);

    public void Attach(ContentView presenter) => _presenter = presenter;

    public Task NavigateAsync(Screen screen, object? parameter = null) => NavigateCoreAsync(screen, parameter, record: true);

    public Task GoBackAsync()
    {
        if (_back.Count == 0 || Current is not { } current)
        {
            return Task.CompletedTask;
        }

        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(current);
        return NavigateCoreAsync(target, null, record: false);
    }

    public Task GoForwardAsync()
    {
        if (_forward.Count == 0 || Current is not { } current)
        {
            return Task.CompletedTask;
        }

        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(current);
        return NavigateCoreAsync(target, null, record: false);
    }

    private async Task NavigateCoreAsync(Screen screen, object? parameter, bool record)
    {
        if (_presenter is null || !_factories.TryGetValue(screen, out var factory))
        {
            return;
        }

        if (Current == screen)
        {
            if (parameter is not null && CurrentView is IScreenView same)
            {
                await same.OnNavigatedToAsync(parameter);
            }

            return;
        }

        if (record && Current is { } previous)
        {
            _back.Add(previous);
            if (_back.Count > HistoryLimit)
            {
                _back.RemoveAt(0);
            }

            _forward.Clear();
        }

        var version = ++_version;
        (CurrentView as IScreenView)?.OnNavigatedFrom();

        // M2: old content fades out (fast, no movement), new content enters like M1 (fade + Y 8 → 0, slow, enter).
        if (_presenter.Content is { } old && MotionTokens.Fast > 0)
        {
            await old.FadeToAsync(0, MotionTokens.Fast, OnionEasing.Exit);
        }

        if (version != _version)
        {
            return;
        }

        if (!_views.TryGetValue(screen, out var view))
        {
            view = factory();

            _views[screen] = view;
        }

        Current = screen;
        view.Opacity = MotionTokens.Slow > 0 ? 0 : 1;
        view.TranslationY = MotionTokens.AllowsTransforms ? DesignTokens.Double("Space200", 8) : 0;
        _presenter.Content = view;
        Navigated?.Invoke(this, EventArgs.Empty);

        // M1: cards and tiles additionally enter one after another in reading order (stagger, max 8).
        var entrance = MotionTokens.Slow > 0
            ? Task.WhenAll(view.FadeToAsync(1, MotionTokens.Slow, OnionEasing.Enter),
                view.TranslateToAsync(0, 0, MotionTokens.Slow, OnionEasing.Enter),
                Motion.StaggerInAsync(ElementTree.Find<Controls.Card>(view).Where(card => ElementTree.IsShown(card, view))))
            : Task.CompletedTask;

        if (view is IScreenView screenView)
        {
            await Task.WhenAll(entrance, screenView.OnNavigatedToAsync(parameter));
        }
        else
        {
            await entrance;
        }
    }
}
