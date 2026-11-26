using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class TodayView : ContentView, IScreenView
{
    private readonly MainViewModel _viewModel = new();

    public TodayView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        AppState.Changed += async (_, _) => await RefreshAsync(force: false);
    }

    public Task OnNavigatedToAsync(object? parameter) => RefreshAsync(force: false);

    public void OnNavigatedFrom()
    {
    }

    public async Task RefreshAsync(bool force)
    {
        try
        {
            await _viewModel.RefreshAsync(force);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnProvidersReordered(object? sender, EventArgs e)
    {
        _viewModel.SaveProviderOrder();
        SemanticScreenReader.Announce("Zapisano nową kolejność źródeł.");
    }
}
