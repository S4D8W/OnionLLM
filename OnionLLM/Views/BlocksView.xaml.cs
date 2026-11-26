using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class BlocksView : ContentView, IScreenView
{
    private readonly BlocksViewModel _viewModel = new();

    public BlocksView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        ExportButton.Options =
        [
            new MenuEntry("JSON (format ccusage)", () => _viewModel.ExportJsonCommand.Execute(null)),
            new MenuEntry("Kopiuj jako tabelę", () => _viewModel.CopyTableCommand.Execute(null))
        ];

        // §6.5 table: Blok · Oś · Modele · % limitu · Tokeny · Koszt; gaps render as separator rows.
        Table.Columns =
        [
            new DataColumn { Key = nameof(BlockItemViewModel.Title), Header = "Blok", Star = 2, CellFactory = BlockCell },
            new DataColumn { Key = "Timeline", Header = "Oś", Star = 1.4, Priority = 2, CellFactory = TimelineCell },
            new DataColumn { Key = nameof(BlockItemViewModel.ModelsText), Header = "Modele", Star = 1.6, Priority = 3, Format = CellFormat.Mono },
            new DataColumn { Key = nameof(BlockItemViewModel.LimitText), Header = "% limitu", Star = 1.2, Priority = 2, CellFactory = LimitCell },
            new DataColumn { Key = nameof(BlockItemViewModel.Tokens), Header = "Tokeny", Align = ColumnAlign.End, Priority = 2, Format = CellFormat.Number },
            new DataColumn { Key = nameof(BlockItemViewModel.Cost), Header = "Koszt", Align = ColumnAlign.End, Format = CellFormat.Cost }
        ];

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

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();

    private void ApplyLayout()
    {
        var columns = ShellViewModel.Instance.Layout == LayoutState.Wide ? 4 : 2;
        KpiGrid.ColumnDefinitions.Clear();
        KpiGrid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            KpiGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        KpiGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        KpiGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < KpiGrid.Children.Count; i++)
        {
            Grid.SetColumn((View)KpiGrid.Children[i], i % columns);
            Grid.SetRow((View)KpiGrid.Children[i], i / columns);
        }
    }

    /// <summary>„Blok” cell: start–end + „Aktywny” (gaps are separator rows of the table, see <see cref="ISeparatorRow"/>).</summary>
    private static View BlockCell()
    {
        var host = new ContentView();
        host.BindingContextChanged += (_, _) =>
        {
            if (host.BindingContext is not BlockItemViewModel item)
            {
                return;
            }

            host.Content = new HorizontalStackLayout
                {
                    Spacing = Ui.D("Space200"),
                    Children =
                    {
                        Ui.Text("Text.BodyStrong", item.Title),
                        new StatusBadge { Text = "Aktywny", Kind = StatusKind.Live, IsVisible = item.IsActive }
                    }
                };
        };
        return host;
    }

    private static View TimelineCell()
    {
        var timeline = new BlockTimeline { IsMini = true };
        timeline.SetBinding(BlockTimeline.CumulativeProperty, nameof(BlockItemViewModel.Series));
        timeline.SetBinding(BlockTimeline.NowProperty, nameof(BlockItemViewModel.Now));
        timeline.SetBinding(IsVisibleProperty, nameof(BlockItemViewModel.IsGap), converter: new Converters.InvertBoolConverter());
        return timeline;
    }

    /// <summary>„% limitu”: threshold ProgressBar + number (never the bar alone, §4.4.1).</summary>
    private static View LimitCell()
    {
        var bar = new Controls.ProgressBar { IsThreshold = true, VerticalOptions = LayoutOptions.Center };
        bar.SetBinding(Controls.ProgressBar.ValueProperty, nameof(BlockItemViewModel.LimitPercent), converter: new PercentToFraction());
        var number = Ui.Text("Text.TableNum");
        number.SetBinding(Label.TextProperty, nameof(BlockItemViewModel.LimitText));
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = Ui.D("Space200") };
        grid.Add(bar);
        grid.Add(number, 1);
        grid.SetBinding(IsVisibleProperty, nameof(BlockItemViewModel.IsGap), converter: new Converters.InvertBoolConverter());
        return grid;
    }

    private sealed class PercentToFraction : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is double percent && !double.IsNaN(percent) ? percent / 100 : 0d;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
