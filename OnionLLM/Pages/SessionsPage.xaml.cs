using OnionLLM.ViewModels;

namespace OnionLLM.Pages;

public partial class SessionsPage : ContentPage
{
    private readonly SessionsViewModel _viewModel = new();

    public SessionsPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }
}
