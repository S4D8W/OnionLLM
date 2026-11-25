using OnionLLM.ViewModels;

namespace OnionLLM.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel = new();

    public SettingsPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
    }

    private void OnMenuClicked(object? sender, EventArgs e) => Shell.Current.FlyoutIsPresented = true;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Load();
    }
}
