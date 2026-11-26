using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class ProjectsView : ContentView, IScreenView
{
    private readonly ProjectsViewModel _viewModel = new();

    public ProjectsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;

        // §6.6 table: Projekt · Źródła · Sesje · Ostatnia aktywność · Trend · Tokeny · Koszt.
        Table.Columns =
        [
            new DataColumn { Key = nameof(ProjectRowViewModel.Name), Header = "Projekt", SubtitleKey = nameof(ProjectRowViewModel.Path), Star = 2.4, Sortable = true, Format = CellFormat.Strong, IsId = true },
            new DataColumn { Key = "Sources", Header = "Źródła", Star = 1.2, Priority = 3, CellFactory = SourcesCell },
            new DataColumn { Key = nameof(ProjectRowViewModel.Sessions), Header = "Sesje", Align = ColumnAlign.End, Priority = 2, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ProjectRowViewModel.LastActivityText), Header = "Ostatnia aktywność", Star = 1.3, Priority = 3, Sortable = true },
            new DataColumn { Key = "Trend", Header = "Trend", Star = 1.2, Priority = 2, CellFactory = TrendCell },
            new DataColumn { Key = nameof(ProjectRowViewModel.Tokens), Header = "Tokeny", Align = ColumnAlign.End, Priority = 2, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ProjectRowViewModel.Cost), Header = "Koszt", Align = ColumnAlign.End, Sortable = true, Format = CellFormat.Cost, BarKey = nameof(ProjectRowViewModel.CostShare), MarkerKey = nameof(ProjectRowViewModel.Marker) }
        ];
        // „Top projekty” (§6.6): click / Enter on a bar opens the project drawer.
        TopChart.CategoryActivated += (_, index) =>
        {
            if (index >= 0 && index < _viewModel.Top.Count)
            {
                _viewModel.OpenDetails(_viewModel.Top[index]);
            }
        };
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is EntityTarget target)
        {
            return _viewModel.OpenEntityAsync(target.Id);
        }

        if (parameter is SearchRequest search)
        {
            _viewModel.Search = search.Query;
        }

        return _viewModel.RefreshAsync();
    }

    public void OnNavigatedFrom() => _viewModel.Search = string.Empty;

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();

    /// <summary>Stack of source badges (max 4 + „+N”).</summary>
    private static View SourcesCell()
    {
        var stack = new HorizontalStackLayout { Spacing = Ui.D("Space050"), VerticalOptions = LayoutOptions.Center };
        stack.BindingContextChanged += (_, _) =>
        {
            stack.Children.Clear();
            if (stack.BindingContext is not ProjectRowViewModel project)
            {
                return;
            }

            foreach (var id in project.SourceIds.Take(4))
            {
                var name = AppState.Engine.FindSource(id)?.DisplayName ?? id;
                var badge = new SourceBadge { SourceId = id, Initial = name[..1].ToUpperInvariant(), Size = BadgeSize.Xs };
                ToolTipProperties.SetText(badge, name);
                stack.Children.Add(badge);
            }

            if (project.SourceIds.Count > 4)
            {
                stack.Children.Add(Ui.Text("Text.Caption", $"+{project.SourceIds.Count - 4}"));
            }
        };
        return stack;
    }

    private static View TrendCell()
    {
        var sparkline = new Sparkline { ColorKey = "ColorDataCost" };
        sparkline.SetBinding(Sparkline.PointsProperty, nameof(ProjectRowViewModel.Trend));
        return sparkline;
    }
}
