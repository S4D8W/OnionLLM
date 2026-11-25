using OnionLLM.ViewModels;

namespace OnionLLM;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel = new();
    private IDispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _lifetimeCancellation;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        AppState.Changed += OnAppStateChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _lifetimeCancellation = new CancellationTokenSource();
        await RefreshSafelyAsync(_lifetimeCancellation.Token);

        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, AppState.Config.RefreshIntervalSeconds));
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    protected override void OnDisappearing()
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
        }

        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;
        base.OnDisappearing();
    }

    private void OnProvidersReordered(object? sender, EventArgs e)
    {
        _viewModel.SaveProviderOrder();
        SemanticScreenReader.Announce("Zapisano nową kolejność źródeł.");
    }

    private async void OnAppStateChanged(object? sender, EventArgs e)
    {
        if (_lifetimeCancellation is not null)
        {
            if (_refreshTimer is not null)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, AppState.Config.RefreshIntervalSeconds));
            }

            await RefreshSafelyAsync(_lifetimeCancellation.Token);
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_lifetimeCancellation is not null)
        {
            await RefreshSafelyAsync(_lifetimeCancellation.Token);
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _viewModel.RefreshAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
