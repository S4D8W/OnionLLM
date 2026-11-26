using OnionLLM.Controls;
using OnionLLM.Core;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class LimitsView : ContentView, IScreenView
{
    private readonly LimitsViewModel _viewModel = new();

    public LimitsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        BindableLayout.SetItemTemplate(OthersList, new DataTemplate(() =>
        {
            var badge = new SourceBadge { Size = BadgeSize.Sm };
            var item = new ListItem { Leading = badge, Subtitle = "Brak danych o limitach dla tego narzędzia" };
            item.BindingContextChanged += (_, _) =>
            {
                if (item.BindingContext is SourceSummary source)
                {
                    badge.SourceId = source.Id;
                    badge.Initial = source.DisplayName[..1].ToUpperInvariant();
                    item.Title = source.DisplayName;
                }
            };
            return item;
        }));
        ShellViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Layout))
            {
                ApplyLayout();
            }
        };
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
        ApplyLayout();
    }

    public Task OnNavigatedToAsync(object? parameter) => _viewModel.RefreshAsync();

    public void OnNavigatedFrom()
    {
    }

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync(force);

    /// <summary>Rings left of the gauges; stacked on narrow windows. Hero-size rings (168) where there is room.</summary>
    private void ApplyLayout()
    {
        var narrow = ShellViewModel.Instance.Layout == LayoutState.Narrow;
        ClaudeGrid.ColumnDefinitions.Clear();
        ClaudeGrid.RowDefinitions.Clear();
        ClaudeGrid.ColumnDefinitions.Add(new ColumnDefinition(narrow ? GridLength.Star : GridLength.Auto));
        if (!narrow)
        {
            ClaudeGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        ClaudeGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        ClaudeGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(Gauges, narrow ? 0 : 1);
        Grid.SetRow(Gauges, narrow ? 1 : 0);
        var size = narrow ? Ui.D("SizeRingCard") : Ui.D("SizeRingHero");
        Rings.WidthRequest = Rings.HeightRequest = size;
        Rings.HorizontalOptions = narrow ? LayoutOptions.Center : LayoutOptions.Start;
    }
}
