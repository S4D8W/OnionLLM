using OnionLLM.Controls;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class SessionsView : ContentView, IScreenView
{
    private readonly SessionsViewModel _viewModel = new();

    public SessionsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        ExportButton.Options =
        [
            new MenuEntry("CSV", () => _viewModel.ExportCsvCommand.Execute(null)),
            new MenuEntry("JSON (format ccusage)", () => _viewModel.ExportJsonCommand.Execute(null)),
            new MenuEntry("Kopiuj jako tabelę", () => _viewModel.CopyTableCommand.Execute(null))
        ];

        // §6.4 table: Sesja · Źródło · Start · Czas trwania · Modele · Wywołania · Tokeny · Koszt.
        Table.Columns =
        [
            new DataColumn { Key = nameof(SessionItemViewModel.Title), Header = "Sesja", SubtitleKey = nameof(SessionItemViewModel.SessionShort), Star = 2.4, Format = CellFormat.Strong, IsId = false },
            new DataColumn { Key = nameof(SessionItemViewModel.Source), Header = "Źródło", Star = 1.6, Priority = 2, CellFactory = SourceCell },
            new DataColumn { Key = nameof(SessionItemViewModel.Start), Header = "Start", Star = 1.3, Priority = 2, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SessionItemViewModel.Duration), Header = "Czas trwania", Align = ColumnAlign.End, Priority = 3, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SessionItemViewModel.ModelsText), Header = "Modele", Star = 1.6, Priority = 3, Format = CellFormat.Mono },
            new DataColumn { Key = nameof(SessionItemViewModel.Requests), Header = "Wywołania", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SessionItemViewModel.Tokens), Header = "Tokeny", Align = ColumnAlign.End, Priority = 2, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SessionItemViewModel.Cost), Header = "Koszt", Align = ColumnAlign.End, Sortable = true, Format = CellFormat.Cost, MarkerKey = nameof(SessionItemViewModel.Marker), BarKey = nameof(SessionItemViewModel.CostShare) },
            new DataColumn { Key = nameof(SessionItemViewModel.SessionId), Header = "ID", Priority = 4, IsId = true }
        ];
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        switch (parameter)
        {
            case RangeFilter filter:
                _viewModel.ApplyFilter(filter);
                return;
            case EntityTarget target:
                await _viewModel.OpenEntityAsync(target.Id);
                return;
            case SearchRequest search:
                _viewModel.Search = search.Query;
                break;
        }

        await _viewModel.RefreshAsync();
    }

    /// <summary>Search is not remembered when leaving the screen (§5.4).</summary>
    public void OnNavigatedFrom() => _viewModel.Search = string.Empty;

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync();

    internal static View SourceCell()
    {
        var badge = new SourceBadge { Size = BadgeSize.Sm };
        badge.SetBinding(SourceBadge.SourceIdProperty, nameof(SessionItemViewModel.SourceId));
        badge.SetBinding(SourceBadge.InitialProperty, nameof(SessionItemViewModel.Initial));
        var name = Ui.Text("Text.Body");
        name.SetBinding(Label.TextProperty, nameof(SessionItemViewModel.Source));
        name.VerticalOptions = LayoutOptions.Center;
        name.LineBreakMode = LineBreakMode.TailTruncation;
        return new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { badge, name } };
    }
}
