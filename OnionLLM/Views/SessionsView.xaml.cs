using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class SessionsView : ContentView, IScreenView
{
    private readonly SessionsViewModel _viewModel = new();

    public SessionsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    public Task OnNavigatedToAsync(object? parameter) => _viewModel.RefreshAsync();

    public void OnNavigatedFrom()
    {
    }

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();
}
