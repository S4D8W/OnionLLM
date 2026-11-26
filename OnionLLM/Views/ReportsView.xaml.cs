using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class ReportsView : ContentView, IScreenView
{
    private readonly ReportsViewModel _viewModel = new();

    public ReportsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        ChartView.Drawable = _viewModel.Chart;
        _viewModel.ChartChanged += (_, _) => ChartView.Invalidate();
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    public Task OnNavigatedToAsync(object? parameter) => _viewModel.RefreshAsync();

    public void OnNavigatedFrom()
    {
    }

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();
}
