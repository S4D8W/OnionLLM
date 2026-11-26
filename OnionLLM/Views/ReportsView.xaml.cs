using OnionLLM.Controls;
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
        ExportButton.Options =
        [
            new MenuEntry("CSV", () => _viewModel.ExportCsvCommand.Execute(null)),
            new MenuEntry("JSON (format ccusage)", () => _viewModel.ExportJsonCommand.Execute(null)),
            new MenuEntry("Kopiuj jako tabelę", () => _viewModel.CopyTableCommand.Execute(null))
        ];

        // §6.3 table: Okres (+ projekt) · Modele · Wejście / Wyjście / Cache zapis / Cache odczyt · Tokeny · Koszt.
        Table.Columns =
        [
            new DataColumn { Key = nameof(ReportRowViewModel.Period), Header = "Okres", SubtitleKey = nameof(ReportRowViewModel.Project), Star = 2.2, Sortable = true, Format = CellFormat.Strong },
            new DataColumn { Key = nameof(ReportRowViewModel.ModelsText), Header = "Modele", Star = 2, Priority = 3, Format = CellFormat.Mono },
            new DataColumn { Key = nameof(ReportRowViewModel.Input), Header = "Wejście", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ReportRowViewModel.Output), Header = "Wyjście", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ReportRowViewModel.CacheWrite), Header = "Zapis cache", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ReportRowViewModel.CacheRead), Header = "Odczyt cache", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ReportRowViewModel.Tokens), Header = "Tokeny", Align = ColumnAlign.End, Priority = 2, Sortable = true, Format = CellFormat.Number, BarKey = nameof(ReportRowViewModel.TokensShare), BarColorKey = "ColorDataTokens" },
            new DataColumn { Key = nameof(ReportRowViewModel.Cost), Header = "Koszt", Align = ColumnAlign.End, Sortable = true, Format = CellFormat.Cost, MarkerKey = nameof(ReportRowViewModel.Marker), BarKey = nameof(ReportRowViewModel.CostShare) }
        ];
        TimeChart.CategoryActivated += (_, index) => _viewModel.OpenCategory(index);

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

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await ScreenLoad.RefreshIdleAsync(_viewModel.RefreshAsync, () => _viewModel.IsBusy);
        // Command palette: „Eksportuj raport CSV / JSON” runs the export of the current report view.
        if (parameter is ScreenAction { Name: ScreenActions.ExportCsv })
        {
            _viewModel.ExportCsvCommand.Execute(null);
        }
        else if (parameter is ScreenAction { Name: ScreenActions.ExportJson })
        {
            _viewModel.ExportJsonCommand.Execute(null);
        }
    }

    public void OnNavigatedFrom()
    {
    }

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();

    /// <summary>KPI: 4 columns on wide, 2×2 otherwise; analysis row side by side on wide only (§6.3 pkt 5).</summary>
    private void ApplyLayout()
    {
        var wide = ShellViewModel.Instance.Layout == LayoutState.Wide;
        var columns = wide ? 4 : 2;
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

        AnalysisGrid.ColumnDefinitions.Clear();
        AnalysisGrid.RowDefinitions.Clear();
        AnalysisGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (wide)
        {
            AnalysisGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        AnalysisGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AnalysisGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(ActivityCard, wide ? 1 : 0);
        Grid.SetRow(ActivityCard, wide ? 0 : 1);
    }
}
