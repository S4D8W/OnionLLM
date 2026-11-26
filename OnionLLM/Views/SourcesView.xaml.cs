using OnionLLM.Controls;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class SourcesView : ContentView, IScreenView
{
    private readonly SourcesViewModel _viewModel = new();

    public SourcesView()
    {
        InitializeComponent();
        BindingContext = _viewModel;

        // §6.8 table: Źródło · Stan · Pliki · Pominięte · Włączone. „Ostatnia aktywność” and „Wpisy” need per-source ledger
        // statistics (Wymaga danych) and are not rendered.
        Table.Columns =
        [
            new DataColumn { Key = nameof(SourceRowViewModel.Name), Header = "Źródło", Star = 2.4, CellFactory = SourceCell },
            new DataColumn { Key = nameof(SourceRowViewModel.StateText), Header = "Stan", Star = 1.2, CellFactory = StateCell },
            new DataColumn { Key = nameof(SourceRowViewModel.LastActivityText), Header = "Ostatnia aktywność", Star = 1.3, Priority = 2 },
            new DataColumn { Key = nameof(SourceRowViewModel.Entries), Header = "Wpisy", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SourceRowViewModel.Files), Header = "Pliki", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
            new DataColumn { Key = nameof(SourceRowViewModel.Skipped), Header = "Pominięte", Align = ColumnAlign.End, Priority = 2, CellFactory = SkippedCell },
            new DataColumn { Key = nameof(SourceRowViewModel.Enabled), Header = "Włączone", Star = 0.8, CellFactory = ToggleCell }
        ];
        AppState.Changed += async (_, _) => await _viewModel.RefreshAsync();
    }

    public Task OnNavigatedToAsync(object? parameter) =>
        parameter is EntityTarget target ? _viewModel.OpenEntityAsync(target.Id) : _viewModel.RefreshAsync();

    public void OnNavigatedFrom() => _viewModel.Search = string.Empty;

    public Task RefreshAsync(bool force) => _viewModel.RefreshAsync(force);

    private static View SourceCell()
    {
        var badge = new SourceBadge { Size = BadgeSize.Sm };
        badge.SetBinding(SourceBadge.SourceIdProperty, nameof(SourceRowViewModel.Id));
        badge.SetBinding(SourceBadge.InitialProperty, nameof(SourceRowViewModel.Initial));
        badge.SetBinding(SourceBadge.StateProperty, nameof(SourceRowViewModel.BadgeState));
        var name = Ui.Text("Text.BodyStrong");
        name.SetBinding(Label.TextProperty, nameof(SourceRowViewModel.Name));
        var vendor = Ui.Text("Text.Caption");
        vendor.SetBinding(Label.TextProperty, nameof(SourceRowViewModel.Vendor));
        return new HorizontalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children = { badge, new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { name, vendor } } }
        };
    }

    private static View StateCell()
    {
        var badge = new StatusBadge();
        badge.SetBinding(StatusBadge.TextProperty, nameof(SourceRowViewModel.StateText));
        badge.SetBinding(StatusBadge.KindProperty, nameof(SourceRowViewModel.StateKind));
        return badge;
    }

    private static View SkippedCell()
    {
        var label = Ui.Text("Text.TableNum");
        label.SetBinding(Label.TextProperty, nameof(SourceRowViewModel.Skipped));
        label.BindingContextChanged += (_, _) =>
            label.Dyn(Label.TextColorProperty, label.BindingContext is SourceRowViewModel { HasSkipped: true } ? "ColorStatusWarning" : "ColorTextPrimary");
        return label;
    }

    private static View ToggleCell()
    {
        var toggle = new Switch { HorizontalOptions = LayoutOptions.Start };
        toggle.SetBinding(Switch.IsToggledProperty, nameof(SourceRowViewModel.Enabled), BindingMode.TwoWay);
        toggle.BindingContextChanged += (_, _) =>
            SemanticProperties.SetDescription(toggle, toggle.BindingContext is SourceRowViewModel row ? "Włączone: " + row.Name : "Włączone");
        return toggle;
    }
}
