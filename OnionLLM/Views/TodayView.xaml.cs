using System.Collections.Specialized;
using OnionLLM.Controls;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

public partial class TodayView : ContentView, IScreenView, IKeyboardTarget
{
    private readonly MainViewModel _viewModel = new();

    public TodayView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        var breakdown = new TokenBreakdown { IsCompact = true };
        breakdown.SetBinding(TokenBreakdown.InputProperty, nameof(MainViewModel.TodayInput));
        breakdown.SetBinding(TokenBreakdown.OutputProperty, nameof(MainViewModel.TodayOutput));
        breakdown.SetBinding(TokenBreakdown.CacheWriteProperty, nameof(MainViewModel.TodayCacheWrite));
        breakdown.SetBinding(TokenBreakdown.CacheReadProperty, nameof(MainViewModel.TodayCacheRead));
        TokensTile.Extra = breakdown;
        WeekTile.Command = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Reports));
        MonthTile.Command = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Reports));
        SessionsTile.Command = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Sessions));
        BlockCard.Command = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Blocks));

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HasActiveBlock) or nameof(MainViewModel.HasLimitRings))
            {
                ApplyLayout();
            }
        };
        ShellViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Layout))
            {
                ApplyLayout();
            }
        };
        AppState.Changed += async (_, _) => await RefreshAsync(force: false);
        ApplyLayout();
    }

    public Task OnNavigatedToAsync(object? parameter) => RefreshAsync(force: false);

    public void OnNavigatedFrom()
    {
    }

    public async Task RefreshAsync(bool force)
    {
        try
        {
            await _viewModel.RefreshAsync(force);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// §6.1 „Responsywność”: wide — hero with rings (8/12) + active block (4/12), KPI 4 × 3/12; medium — hero full width,
    /// „Limity” + block side by side, KPI 2×2; narrow — one column, KPI 2×2.
    /// </summary>
    private void ApplyLayout()
    {
        var layout = ShellViewModel.Instance.Layout;
        var wide = layout == LayoutState.Wide;
        HeroRings.IsVisible = wide && _viewModel.HasLimitRings;
        LimitsCard.IsVisible = !wide && _viewModel.HasLimitRings;

        TopGrid.ColumnDefinitions.Clear();
        TopGrid.RowDefinitions.Clear();
        if (wide)
        {
            var block = _viewModel.HasActiveBlock;
            TopGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(block ? 2 : 1, GridUnitType.Star)));
            TopGrid.ColumnDefinitions.Add(new ColumnDefinition(block ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));
            Place(HeroCard, 0, 0);
            Place(BlockCard, 0, 1);
            Place(LimitsCard, 1, 0);
        }
        else if (layout == LayoutState.Medium)
        {
            TopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Place(HeroCard, 0, 0, span: 2);
            Place(LimitsCard, 1, 0, span: _viewModel.HasActiveBlock ? 1 : 2);
            Place(BlockCard, 1, _viewModel.HasLimitRings ? 1 : 0, span: _viewModel.HasLimitRings ? 1 : 2);
        }
        else
        {
            TopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Place(HeroCard, 0, 0);
            Place(LimitsCard, 1, 0);
            Place(BlockCard, 2, 0);
        }

        var tiles = new View[] { WeekTile, MonthTile, TokensTile, SessionsTile };
        var columns = wide ? 4 : 2;
        KpiGrid.ColumnDefinitions.Clear();
        KpiGrid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            KpiGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var i = 0; i < (tiles.Length + columns - 1) / columns; i++)
        {
            KpiGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var i = 0; i < tiles.Length; i++)
        {
            Grid.SetRow(tiles[i], i / columns);
            Grid.SetColumn(tiles[i], i % columns);
        }

        void Place(View view, int row, int column, int span = 1)
        {
            while (TopGrid.RowDefinitions.Count <= row)
            {
                TopGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            Grid.SetRow(view, row);
            Grid.SetColumn(view, column);
            Grid.SetColumnSpan(view, span);
        }
    }

    private string? _keyboardCard;

    /// <summary>Alt+↑ / Alt+↓ move the last activated source card (§6.1); focus follows the card.</summary>
    public bool HandleKey(KeyboardAction action)
    {
        if (action is not (KeyboardAction.MoveUp or KeyboardAction.MoveDown) ||
            _viewModel.Providers.FirstOrDefault(provider => provider.Id == _keyboardCard) is not { } card)
        {
            return false;
        }

        var index = _viewModel.Providers.IndexOf(card);
        _viewModel.Move(card, index + (action == KeyboardAction.MoveUp ? -1 : 1));
        SourcesFlex.Children.OfType<SourceCard>().FirstOrDefault(view => (view.BindingContext as SourceCardViewModel)?.Id == card.Id)?.FocusMenu();
        return true;
    }

    /// <summary>Source cards: auto-fill grid with a 320 dp minimum (§2.9.2) in the user's order (§5.11).</summary>
    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SourcesFlex.Children.Clear();
        var gap = Ui.D("LayoutGridGap");
        foreach (var provider in _viewModel.Providers)
        {
            var card = new SourceCard
            {
                BindingContext = provider,
                Margin = new Thickness(0, 0, gap, gap),
                IndexOf = item => _viewModel.Providers.IndexOf(item),
                Count = _viewModel.Providers.Count
            };
            FlexLayout.SetBasis(card, new Microsoft.Maui.Layouts.FlexBasis((float)Ui.D("SizeSourceCardMin")));
            FlexLayout.SetGrow(card, 1);
            card.MoveRequested += (_, move) => _viewModel.Move(move.Card, move.Index);
            card.Activated += (_, item) =>
            {
                _keyboardCard = item.Id;
                KeyboardRouter.Target = this;
            };
            card.ConfigureRequested += (_, _) => _ = ShellViewModel.Instance.NavigateAsync(
                ShellViewModel.Instance.Navigation.IsAvailable(Screen.Sources) ? Screen.Sources : Screen.Settings);
            SourcesFlex.Children.Add(card);
        }
    }

    private void OnSourcesMenu(object? sender, EventArgs e) =>
        Controls.Menu.Show(SourcesMenu, [new MenuEntry("Przywróć domyślną kolejność", () => _viewModel.ResetOrderCommand.Execute(null))], Ui.D("SizeDrawerMin"));

    private void OnHeroTapped(object? sender, TappedEventArgs e) => _ = ShellViewModel.Instance.NavigateAsync(Screen.Reports);

    private void OnRingsTapped(object? sender, TappedEventArgs e) =>
        _ = ShellViewModel.Instance.NavigateAsync(ShellViewModel.Instance.Navigation.IsAvailable(Screen.Limits) ? Screen.Limits : Screen.Today);
}
