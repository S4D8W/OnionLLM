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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Load();
    }
}
