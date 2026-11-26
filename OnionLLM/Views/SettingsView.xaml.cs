using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class SettingsView : ContentView, IScreenView
{
    private readonly SettingsViewModel _viewModel = new();

    public SettingsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    public Task OnNavigatedToAsync(object? parameter) => _viewModel.LoadAsync();

    public void OnNavigatedFrom()
    {
    }

    /// <summary>Settings has no data view of its own — refresh only syncs (status bar shows the result).</summary>
    public Task RefreshAsync(bool force) => AppState.RefreshDataAsync(force);
}
