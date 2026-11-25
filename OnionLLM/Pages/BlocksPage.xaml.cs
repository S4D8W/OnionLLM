using OnionLLM.ViewModels;

namespace OnionLLM.Pages;

public partial class BlocksPage : ContentPage
{
    private readonly BlocksViewModel _viewModel = new();

    public BlocksPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    private void OnMenuClicked(object? sender, EventArgs e) => Shell.Current.FlyoutIsPresented = true;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }
}
