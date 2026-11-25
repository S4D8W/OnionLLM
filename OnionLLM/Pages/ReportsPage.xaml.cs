using OnionLLM.ViewModels;

namespace OnionLLM.Pages;

public partial class ReportsPage : ContentPage
{
    private readonly ReportsViewModel _viewModel = new();

    public ReportsPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        ChartView.Drawable = _viewModel.Chart;
        _viewModel.ChartChanged += (_, _) => ChartView.Invalidate();
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    private void OnMenuClicked(object? sender, EventArgs e) => Shell.Current.FlyoutIsPresented = true;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }
}
