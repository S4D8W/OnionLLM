using OnionLLM.Controls;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class ModelsView : ContentView, IScreenView
{
    private readonly ModelsViewModel _viewModel = new();

    public ModelsView()
    {
        InitializeComponent();
        BindingContext = _viewModel;

        // §6.7 table: Model · Źródła · Tokeny · Udział cache · Cena wejście / wyjście · Koszt.
        Table.Columns =
        [
            new DataColumn { Key = nameof(ModelRowViewModel.Name), Header = "Model", SubtitleKey = nameof(ModelRowViewModel.Provider), Star = 2.2, Sortable = true, Format = CellFormat.Mono },
            new DataColumn { Key = nameof(ModelRowViewModel.Sources), Header = "Źródła", Star = 1.6, Priority = 3 },
            new DataColumn { Key = nameof(ModelRowViewModel.Tokens), Header = "Tokeny", Align = ColumnAlign.End, Priority = 2, Sortable = true, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ModelRowViewModel.CacheShare), Header = "Udział cache", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(ModelRowViewModel.InputPrice), Header = "Cena wejście", Align = ColumnAlign.End, Star = 1.3, Priority = 2, Sortable = true, CellFactory = PriceCell(nameof(ModelRowViewModel.InputPrice)) },
            new DataColumn { Key = nameof(ModelRowViewModel.OutputPrice), Header = "Cena wyjście", Align = ColumnAlign.End, Star = 1.3, Priority = 3, CellFactory = PriceCell(nameof(ModelRowViewModel.OutputPrice)) },
            new DataColumn { Key = nameof(ModelRowViewModel.Cost), Header = "Koszt", Align = ColumnAlign.End, Sortable = true, Format = CellFormat.Cost, MarkerKey = nameof(ModelRowViewModel.Marker), BarKey = nameof(ModelRowViewModel.CostShare) },
            new DataColumn { Key = nameof(ModelRowViewModel.Model), Header = "Pełna nazwa", Priority = 4, IsId = true }
        ];
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

    /// <summary>Price per 1 M (Mono); „brak ceny” in <c>status.warning</c> with „*”; „nadpisana” badge (§6.7).</summary>
    private static Func<View> PriceCell(string property) => () =>
    {
        var host = new HorizontalStackLayout { Spacing = Ui.D("Space150"), HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
        host.BindingContextChanged += (_, _) =>
        {
            host.Children.Clear();
            if (host.BindingContext is not ModelRowViewModel row)
            {
                return;
            }

            var text = property == nameof(ModelRowViewModel.InputPrice) ? row.InputPrice : row.OutputPrice;
            if (row.Pricing is null)
            {
                host.Children.Add(Ui.Text("Text.Caption", "brak ceny").Dyn(Label.TextColorProperty, "ColorStatusWarning"));
                host.Children.Add(new PricingMarker { Marker = "*", Models = row.Name });
                return;
            }

            if (row.IsOverridden && property == nameof(ModelRowViewModel.InputPrice))
            {
                host.Children.Add(new StatusBadge { Text = "nadpisana", Kind = StatusKind.Info });
            }

            host.Children.Add(Ui.Text("Text.MonoSm", text));
        };
        return host;
    };
}
