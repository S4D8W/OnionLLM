using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Input;
using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.Controls;

public enum ColumnAlign
{
    Start,
    End
}

/// <summary>Cell rendering of a column (DESIGN.md §4.5.1 „Format”). Values are already formatted by <c>Fmt</c> in the VM.</summary>
public enum CellFormat
{
    Text,
    Strong,
    Number,
    Cost,
    Mono
}

/// <summary>Column definition provided by the screen view model (§4.5.1).</summary>
public sealed class DataColumn
{
    /// <summary>Row property shown in the cell (and used for sorting by the VM).</summary>
    public required string Key { get; init; }

    public required string Header { get; init; }

    public ColumnAlign Align { get; init; }

    /// <summary>Fixed width in dp; 0 = proportional (<see cref="Star"/>).</summary>
    public double Width { get; init; }

    public double Star { get; init; } = 1;

    /// <summary>1 = always, 2 = hidden on narrow, 3 = hidden on medium and narrow.</summary>
    public int Priority { get; init; } = 1;

    public bool Sortable { get; init; }

    public CellFormat Format { get; init; }

    /// <summary>Optional second line (identity column): row property with the subtitle (caption, tertiary).</summary>
    public string? SubtitleKey { get; init; }

    /// <summary>Optional inline share bar: row property with a 0–1 value; colour token of the bar.</summary>
    public string? BarKey { get; init; }

    public string BarColorKey { get; init; } = "ColorDataCost";

    /// <summary>Optional pricing marker property („*”, „~”) appended to the value.</summary>
    public string? MarkerKey { get; init; }

    /// <summary>Custom cell (e.g. SourceBadge + name, ModelTags); the view gets the row as BindingContext.</summary>
    public Func<View>? CellFactory { get; init; }

    /// <summary>Copied by „Kopiuj ID”.</summary>
    public bool IsId { get; init; }
}

public enum TableState
{
    Ready,
    Loading,
    Empty,
    NoResults,
    Error
}

/// <summary>Row with breakdown sub-rows (§4.5.1 „Rozwijany wiersz”).</summary>
public interface IExpandableRow
{
    bool IsExpanded { get; set; }

    IReadOnlyList<object> Children { get; }
}

/// <summary>
/// Data table (§4.5.1): sticky header with sorting, rows with hover/selection, numeric columns right-aligned, inline bars,
/// expandable rows, „Razem” row, paging footer, column priorities per breakpoint, card mode below 760 dp of width,
/// keyboard (↑/↓, Enter, →/←, Ctrl+C) and a spoken description per row.
/// </summary>
public sealed class DataTable : ContentView, IKeyboardTarget
{
    public static readonly BindableProperty ColumnsProperty = Prop(nameof(Columns), typeof(IList<DataColumn>), null, rebuild: true);
    public static readonly BindableProperty ItemsSourceProperty = Prop(nameof(ItemsSource), typeof(IEnumerable), null);
    public static readonly BindableProperty SortKeyProperty = Prop(nameof(SortKey), typeof(string), string.Empty, BindingMode.TwoWay, rebuild: true);
    public static readonly BindableProperty SortDescendingProperty = Prop(nameof(SortDescending), typeof(bool), true, BindingMode.TwoWay, rebuild: true);
    public static readonly BindableProperty SelectedItemProperty = Prop(nameof(SelectedItem), typeof(object), null, BindingMode.TwoWay);
    public static readonly BindableProperty RowActivatedCommandProperty = Prop(nameof(RowActivatedCommand), typeof(ICommand), null);
    public static readonly BindableProperty StateProperty = Prop(nameof(State), typeof(TableState), TableState.Ready);
    public static readonly BindableProperty EmptyViewProperty = Prop(nameof(EmptyView), typeof(View), null);
    public static readonly BindableProperty TotalRowProperty = Prop(nameof(TotalRow), typeof(object), null, rebuild: true);
    public static readonly BindableProperty FooterTextProperty = Prop(nameof(FooterText), typeof(string), string.Empty);
    public static readonly BindableProperty ShowMoreCommandProperty = Prop(nameof(ShowMoreCommand), typeof(ICommand), null);
    public static readonly BindableProperty CanShowMoreProperty = Prop(nameof(CanShowMore), typeof(bool), false);
    public static readonly BindableProperty AutomationPrefixProperty = Prop(nameof(AutomationPrefix), typeof(string), "Table");

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();

    private readonly Grid _header = new();
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.None };
    private readonly VerticalStackLayout _skeleton = new();
    private readonly ContentView _empty = new();
    private readonly ContentView _total = new();
    private readonly Grid _footer = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
    private readonly Label _footerText = Ui.Text("Text.Caption");
    private readonly Button _showMore = new Button { Text = "Pokaż kolejne 300" }.Styled("Button.Ghost.Sm");
    private readonly Select _cardSort = new() { Placeholder = "Sortuj:" };
    private readonly List<WeakReference<TableRow>> _rows = [];
    private bool _cardMode;
    private IReadOnlyList<DataColumn> _visible = [];

    public DataTable()
    {
        _list.VerticalScrollBarVisibility = ScrollBarVisibility.Default;
        _footer.Add(_footerText);
        _footer.Add(_showMore, 1);
        _footer.Padding = Ui.Thick("PaddingRow");
        _footerText.VerticalOptions = LayoutOptions.Center;
        _showMore.Clicked += (_, _) => ShowMoreCommand?.Execute(null);
        _cardSort.HorizontalOptions = LayoutOptions.Start;
        _cardSort.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Select.SelectedIndex) && _cardSort.SelectedIndex >= 0)
            {
                var sortable = Columns?.Where(column => column.Sortable).ToList() ?? [];
                if (_cardSort.SelectedIndex < sortable.Count)
                {
                    SortKey = sortable[_cardSort.SelectedIndex].Key;
                    SortDescending = true;
                }
            }
        };
        for (var i = 0; i < 5; i++)
        {
            _skeleton.Children.Add(new ContentView { Padding = Ui.Thick("PaddingRow"), Content = Skeleton.Line("TypeTableNumSize", 1) }
                .Dyn(MinimumHeightRequestProperty, "SizeRowHeight"));
        }

        var frame = new Border
        {
            Content = new Grid
            {
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
                Children = { }
            }
        }.Styled("Card.Flat");
        frame.Padding = Ui.Thick("ThicknessZero");
        var grid = (Grid)frame.Content!;
        grid.Add(_cardSort, 0, 0);
        grid.Add(_header, 0, 1);
        grid.Add(_list, 0, 2);
        grid.Add(_skeleton, 0, 2);
        grid.Add(_empty, 0, 2);
        grid.Add(_total, 0, 3);
        grid.Add(_footer, 0, 4);
        Content = frame;

        SizeChanged += (_, _) => UpdateMode();
        ShellViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Layout))
            {
                Rebuild();
            }
        };
        DensityService.DensityChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public IList<DataColumn>? Columns { get => (IList<DataColumn>?)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>Sorted column key; empty = default order. The VM sorts (and remembers it per screen, §5.4).</summary>
    public string SortKey { get => (string)GetValue(SortKeyProperty); set => SetValue(SortKeyProperty, value); }

    public bool SortDescending { get => (bool)GetValue(SortDescendingProperty); set => SetValue(SortDescendingProperty, value); }

    /// <summary>Row open in the drawer (selected: <c>state.selected</c> + 2 dp bar).</summary>
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>Click / Enter on a row — opens the drawer.</summary>
    public ICommand? RowActivatedCommand { get => (ICommand?)GetValue(RowActivatedCommandProperty); set => SetValue(RowActivatedCommandProperty, value); }

    public TableState State { get => (TableState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>EmptyState / ErrorState shown for Empty, NoResults and Error.</summary>
    public View? EmptyView { get => (View?)GetValue(EmptyViewProperty); set => SetValue(EmptyViewProperty, value); }

    /// <summary>„Razem” row rendered with the same columns.</summary>
    public object? TotalRow { get => GetValue(TotalRowProperty); set => SetValue(TotalRowProperty, value); }

    /// <summary>„Pokazano 300 z 1 284”.</summary>
    public string FooterText { get => (string)GetValue(FooterTextProperty); set => SetValue(FooterTextProperty, value); }

    public ICommand? ShowMoreCommand { get => (ICommand?)GetValue(ShowMoreCommandProperty); set => SetValue(ShowMoreCommandProperty, value); }

    public bool CanShowMore { get => (bool)GetValue(CanShowMoreProperty); set => SetValue(CanShowMoreProperty, value); }

    /// <summary>AutomationId prefix: „Sessions” → „Sessions.Row”, „Sessions.Header.Cost”.</summary>
    public string AutomationPrefix { get => (string)GetValue(AutomationPrefixProperty); set => SetValue(AutomationPrefixProperty, value); }

    private static BindableProperty Prop(string name, Type type, object? defaultValue, BindingMode mode = BindingMode.OneWay, bool rebuild = false) =>
        BindableProperty.Create(name, type, typeof(DataTable), defaultValue, mode, propertyChanged: (b, _, _) =>
        {
            var table = (DataTable)b;
            if (rebuild)
            {
                table.Rebuild();
            }
            else
            {
                table.UpdateState();
            }
        });

    // ===== Keyboard (§4.5.1 table) =====

    public bool HandleKey(KeyboardAction action)
    {
        var items = ItemsSource?.Cast<object>().ToList() ?? [];
        if (items.Count == 0)
        {
            return false;
        }

        var index = SelectedItem is null ? -1 : items.IndexOf(SelectedItem);
        switch (action)
        {
            case KeyboardAction.Up or KeyboardAction.Previous:
                SelectRow(items[Math.Max(0, index - 1)], items);
                return true;
            case KeyboardAction.Down or KeyboardAction.Next:
                SelectRow(items[Math.Min(items.Count - 1, index + 1)], items);
                return true;
            case KeyboardAction.Home:
                SelectRow(items[0], items);
                return true;
            case KeyboardAction.End:
                SelectRow(items[^1], items);
                return true;
            case KeyboardAction.Activate when SelectedItem is not null:
                Activate(SelectedItem);
                return true;
            case KeyboardAction.Expand when SelectedItem is IExpandableRow expandable:
                SetExpanded(expandable, true);
                return true;
            case KeyboardAction.Collapse when SelectedItem is IExpandableRow expandable:
                SetExpanded(expandable, false);
                return true;
            case KeyboardAction.Copy when SelectedItem is not null:
                _ = Ui.CopyAsync(RowTsv(SelectedItem));
                return true;
            default:
                return false;
        }
    }

    private void SelectRow(object item, List<object> items)
    {
        SelectedItem = item;
        _list.ScrollTo(items.IndexOf(item), position: ScrollToPosition.MakeVisible, animate: false);
        // With the drawer open, moving the selection updates the drawer (§4.5.1).
        if (DrawerService.IsOpen)
        {
            Activate(item);
        }
    }

    private void Activate(object item)
    {
        SelectedItem = item;
        KeyboardRouter.Target = this;
        if (RowActivatedCommand?.CanExecute(item) == true)
        {
            RowActivatedCommand.Execute(item);
        }
    }

    // ===== Layout =====

    private void UpdateMode()
    {
        var cards = Width > 0 && Width < Ui.D("BreakpointCards");
        if (cards != _cardMode)
        {
            _cardMode = cards;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var layout = ShellViewModel.Instance.Layout;
        var maxPriority = layout switch { LayoutState.Narrow => 1, LayoutState.Medium => 2, _ => 3 };
        _visible = (Columns ?? []).Where(column => column.Priority <= maxPriority).ToList();

        BuildHeader();
        _list.ItemTemplate = new DataTemplate(() => new TableRow(this, _cardMode));
        _total.Content = TotalRow is null ? null : new TableRow(this, _cardMode, isTotal: true) { BindingContext = TotalRow };

        var sortable = Columns?.Where(column => column.Sortable).ToList() ?? [];
        _cardSort.ItemsSource = sortable.Select(column => column.Header).ToList();
        _cardSort.SelectedIndex = sortable.FindIndex(column => column.Key == SortKey);
        _cardSort.IsVisible = _cardMode && sortable.Count > 0;
        UpdateState();
    }

    private void UpdateState()
    {
        _list.ItemsSource = ItemsSource;
        var showList = State == TableState.Ready;
        _list.IsVisible = showList;
        _skeleton.IsVisible = State == TableState.Loading;
        _empty.Content = EmptyView;
        _empty.IsVisible = State is TableState.Empty or TableState.NoResults or TableState.Error;
        _total.IsVisible = showList && TotalRow is not null;
        _footerText.Text = FooterText;
        _showMore.IsVisible = CanShowMore;
        _footer.IsVisible = showList && (!string.IsNullOrEmpty(FooterText) || CanShowMore);
        foreach (var row in LiveRows())
        {
            row.UpdateSelection();
        }
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _header.IsVisible = !_cardMode;
        _header.Padding = Ui.Thick("PaddingRow");
        _header.ColumnSpacing = Ui.D("Space300");
        _header.Dyn(BackgroundColorProperty, "ColorSurface2").Dyn(MinimumHeightRequestProperty, "SizeRowHeightDense");
        foreach (var column in _visible)
        {
            _header.ColumnDefinitions.Add(Definition(column));
            var label = Ui.Text("Text.Label", column.Header);
            label.VerticalOptions = LayoutOptions.Center;
            label.HorizontalTextAlignment = column.Align == ColumnAlign.End ? TextAlignment.End : TextAlignment.Start;
            View cell = label;
            if (column.Sortable)
            {
                var active = column.Key == SortKey;
                var icon = Ui.Icon(active ? (SortDescending ? Icons.ArrowDown : Icons.ArrowUp) : Icons.ArrowUpDown, "Text.Icon.Xs",
                    active ? "ColorTextPrimary" : "ColorTextTertiary");
                if (active)
                {
                    label.Dyn(Label.TextColorProperty, "ColorTextPrimary");
                }

                var stack = new HorizontalStackLayout
                {
                    Spacing = Ui.D("Space100"),
                    HorizontalOptions = column.Align == ColumnAlign.End ? LayoutOptions.End : LayoutOptions.Start,
                    Children = { label, icon }
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => CycleSort(column);
                stack.GestureRecognizers.Add(tap);
                SemanticProperties.SetDescription(stack, $"{column.Header}, " + (active
                    ? SortDescending ? "sortowanie malejąco" : "sortowanie rosnąco"
                    : "sortowanie wyłączone"));
                AutomationProperties.SetIsInAccessibleTree(stack, true);
                stack.AutomationId = $"{AutomationPrefix}.Header.{column.Key}";
                cell = stack;
            }

            _header.Add(cell, _header.ColumnDefinitions.Count - 1);
        }
    }

    /// <summary>Header click: ascending → descending → default (§4.5.1).</summary>
    private void CycleSort(DataColumn column)
    {
        if (SortKey != column.Key)
        {
            SortDescending = false;
            SortKey = column.Key;
        }
        else if (!SortDescending)
        {
            SortDescending = true;
        }
        else
        {
            SortKey = string.Empty;
            SortDescending = true;
        }
    }

    internal static ColumnDefinition Definition(DataColumn column) =>
        new(column.Width > 0 ? new GridLength(column.Width) : new GridLength(column.Star, GridUnitType.Star));

    internal IReadOnlyList<DataColumn> VisibleColumns => _visible;

    internal IReadOnlyList<DataColumn> AllColumns => Columns?.ToList() ?? [];

    internal bool IsSelected(object? item) => item is not null && Equals(item, SelectedItem);

    internal void OnRowTapped(object? item)
    {
        if (item is not null)
        {
            Activate(item);
        }
    }

    internal void Register(TableRow row) => _rows.Add(new WeakReference<TableRow>(row));

    internal void SetExpanded(IExpandableRow row, bool expanded)
    {
        row.IsExpanded = expanded;
        foreach (var live in LiveRows().Where(live => ReferenceEquals(live.BindingContext, row)))
        {
            live.UpdateChildren();
        }
    }

    internal static string Value(object? row, string? key)
    {
        if (row is null || string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var property = PropertyCache.GetOrAdd((row.GetType(), key), k => k.Item1.GetProperty(k.Item2));
        return property?.GetValue(row)?.ToString() ?? string.Empty;
    }

    internal static double Number(object? row, string? key)
    {
        if (row is null || string.IsNullOrEmpty(key))
        {
            return 0;
        }

        var property = PropertyCache.GetOrAdd((row.GetType(), key), k => k.Item1.GetProperty(k.Item2));
        return property?.GetValue(row) is { } value ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Spoken row: priority 1–2 columns, „Sesja onion-app, Claude Code, 1,2 M tokenów, koszt 3,40 $”.</summary>
    internal string Describe(object? row) => string.Join(", ", AllColumns
        .Where(column => column.Priority <= 2)
        .Select(column => $"{column.Header} {Value(row, column.Key)}{Value(row, column.MarkerKey)}")
        .Where(text => text.Trim().Length > 0));

    internal string RowTsv(object row) => string.Join('\t', AllColumns.Select(column => Value(row, column.Key)));

    private IEnumerable<TableRow> LiveRows()
    {
        _rows.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _rows)
        {
            if (reference.TryGetTarget(out var row))
            {
                yield return row;
            }
        }
    }
}

/// <summary>One table row (or card in card mode) — hover, selection bar, cells, expandable children.</summary>
internal sealed class TableRow : VerticalStackLayout
{
    private readonly DataTable _table;
    private readonly bool _cardMode;
    private readonly bool _isTotal;
    private readonly Grid _main = new();
    private readonly BoxView _selectionBar = new() { HorizontalOptions = LayoutOptions.Start };
    private readonly VerticalStackLayout _children = new();
    private Label _chevron = Ui.Icon(Icons.ChevronRight, "Text.Icon.Xs");
    private bool _hover;

    public TableRow(DataTable table, bool cardMode, bool isTotal = false)
    {
        _table = table;
        _cardMode = cardMode;
        _isTotal = isTotal;
        _selectionBar.WidthRequest = Ui.D("StrokeSelected");
        _selectionBar.Dyn(BoxView.ColorProperty, "ColorActionPrimary");
        _selectionBar.HeightRequest = -1;
        _selectionBar.VerticalOptions = LayoutOptions.Fill;
        _main.Padding = Ui.Thick("PaddingRow");
        _main.ColumnSpacing = Ui.D("Space300");
        _main.Dyn(MinimumHeightRequestProperty, "SizeRowHeight");
        Children.Add(new Grid { Children = { _main, _selectionBar } });
        Children.Add(_children);
        Children.Add(new BoxView().Styled("Divider"));

        if (!isTotal)
        {
            var pointer = new PointerGestureRecognizer();
            pointer.PointerEntered += (_, _) => { _hover = true; UpdateSelection(); };
            pointer.PointerExited += (_, _) => { _hover = false; UpdateSelection(); };
            GestureRecognizers.Add(pointer);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _table.OnRowTapped(BindingContext);
            _main.GestureRecognizers.Add(tap);
            FlyoutBase.SetContextFlyout(this, BuildMenu());
            AutomationId = _table.AutomationPrefix + ".Row";
            table.Register(this);
        }

        BindingContextChanged += (_, _) => Build();
    }

    public void UpdateSelection()
    {
        if (_isTotal)
        {
            this.Dyn(BackgroundColorProperty, "ColorSurface2");
            _selectionBar.IsVisible = false;
            return;
        }

        var selected = _table.IsSelected(BindingContext);
        _selectionBar.IsVisible = selected;
        this.Dyn(BackgroundColorProperty, selected ? "ColorStateSelected" : _hover ? "ColorStateHover" : "ColorTransparent");
    }

    public void UpdateChildren()
    {
        _children.Children.Clear();
        var expandable = BindingContext as IExpandableRow;
        _chevron.Rotation = expandable?.IsExpanded == true ? 90 : 0;
        if (expandable is not { IsExpanded: true })
        {
            return;
        }

        foreach (var child in expandable.Children)
        {
            var grid = Cells(child, sub: true);
            grid.Padding = new Thickness(Ui.D("SizeIndent") + Ui.D("Space400"), 0, Ui.D("Space400"), 0);
            grid.Dyn(MinimumHeightRequestProperty, "SizeRowHeightDense");
            _children.Children.Add(Ui.Box(grid, "ColorSurface2", "RadiusXs"));
        }
    }

    private void Build()
    {
        _main.Children.Clear();
        _main.ColumnDefinitions.Clear();
        var row = BindingContext;
        if (row is null)
        {
            return;
        }

        if (_cardMode)
        {
            BuildCard(row);
        }
        else
        {
            var cells = Cells(row, sub: false);
            foreach (var definition in cells.ColumnDefinitions.ToList())
            {
                _main.ColumnDefinitions.Add(new ColumnDefinition(definition.Width));
            }

            foreach (var child in cells.Children.ToList())
            {
                var column = cells.GetColumn(child);
                cells.Children.Remove(child);
                _main.Add((View)child, column);
            }
        }

        SemanticProperties.SetDescription(this, _isTotal ? "Razem: " + _table.Describe(row) : _table.Describe(row));
        UpdateSelection();
        UpdateChildren();
    }

    private Grid Cells(object row, bool sub)
    {
        var grid = new Grid { ColumnSpacing = Ui.D("Space300") };
        var first = true;
        foreach (var column in _table.VisibleColumns)
        {
            grid.ColumnDefinitions.Add(DataTable.Definition(column));
            var cell = Cell(column, row, sub);
            if (first && !sub && !_isTotal && row is IExpandableRow expandable && expandable.Children.Count > 0)
            {
                // A fresh icon per build: a view must never be added to a new parent while still inside the old one.
                _chevron = Ui.Icon(Icons.ChevronRight, "Text.Icon.Xs");
                _chevron.Rotation = expandable.IsExpanded ? 90 : 0;
                var toggle = new TapGestureRecognizer();
                toggle.Tapped += (_, _) => _table.SetExpanded(expandable, !expandable.IsExpanded);
                _chevron.GestureRecognizers.Add(toggle);
                _chevron.VerticalOptions = LayoutOptions.Center;
                ToolTipProperties.SetText(_chevron, "Rozwiń / zwiń");
                cell = new HorizontalStackLayout { Spacing = Ui.D("Space150"), Children = { _chevron, cell } };
            }

            first = false;
            grid.Add(cell, grid.ColumnDefinitions.Count - 1);
        }

        return grid;
    }

    private View Cell(DataColumn column, object row, bool sub)
    {
        if (column.CellFactory is not null && !sub && !_isTotal)
        {
            var custom = column.CellFactory();
            custom.BindingContext = row;
            custom.VerticalOptions = LayoutOptions.Center;
            return custom;
        }

        var style = sub ? (column.Format is CellFormat.Number or CellFormat.Cost ? "Text.TableNum" : "Text.Secondary")
            : _isTotal ? (column.Format is CellFormat.Number or CellFormat.Cost ? "Text.TableNum" : "Text.BodyStrong")
            : column.Format switch
            {
                CellFormat.Number => "Text.TableNum",
                CellFormat.Cost => "Text.TableNum.Cost",
                CellFormat.Mono => "Text.Mono",
                CellFormat.Strong => "Text.BodyStrong",
                _ => "Text.Body"
            };
        var text = DataTable.Value(row, column.Key);
        var label = Ui.Text(style, text);
        if (_isTotal && column.Format is CellFormat.Number or CellFormat.Cost)
        {
            label.FontFamily = "NumSemiBold";
        }

        label.HorizontalTextAlignment = column.Align == ColumnAlign.End ? TextAlignment.End : TextAlignment.Start;
        label.LineBreakMode = column.Format == CellFormat.Mono ? LineBreakMode.MiddleTruncation : LineBreakMode.TailTruncation;
        label.MaxLines = 1;
        ToolTipProperties.SetText(label, text);

        var marker = DataTable.Value(row, column.MarkerKey);
        View value = label;
        if (marker.Length > 0)
        {
            value = new HorizontalStackLayout
            {
                HorizontalOptions = column.Align == ColumnAlign.End ? LayoutOptions.End : LayoutOptions.Start,
                Children = { label, new PricingMarker { Marker = marker } }
            };
        }

        var stack = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { value } };
        if (!string.IsNullOrEmpty(column.SubtitleKey) && !sub)
        {
            var subtitle = Ui.Text("Text.Caption", DataTable.Value(row, column.SubtitleKey));
            subtitle.LineBreakMode = LineBreakMode.MiddleTruncation;
            subtitle.MaxLines = 1;
            stack.Children.Add(subtitle);
        }

        if (!string.IsNullOrEmpty(column.BarKey) && !sub && !_isTotal)
        {
            stack.Children.Add(new InlineBar
            {
                Fraction = DataTable.Number(row, column.BarKey),
                ColorKey = column.BarColorKey,
                HorizontalOptions = column.Align == ColumnAlign.End ? LayoutOptions.End : LayoutOptions.Start
            });
        }

        return stack;
    }

    /// <summary>Card mode (§4.5.1): line 1 identity + cost, line 2 priority 1–2 columns as „label value · label value”.</summary>
    private void BuildCard(object row)
    {
        var columns = _table.AllColumns;
        if (columns.Count == 0)
        {
            return;
        }

        var identity = columns[0];
        var amount = columns.LastOrDefault(column => column.Format == CellFormat.Cost) ?? columns[^1];
        _main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _main.RowDefinitions.Clear();
        _main.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _main.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _main.Add(Cell(identity, row, sub: false), 0, 0);
        _main.Add(Cell(amount, row, sub: false), 1, 0);
        var details = columns
            .Where(column => column.Priority <= 2 && column != identity && column != amount && column.CellFactory is null)
            .Select(column => $"{column.Header} {DataTable.Value(row, column.Key)}");
        var line = Ui.Text("Text.Caption.Secondary", string.Join(" · ", details));
        _main.Add(line, 0, 1);
        Grid.SetColumnSpan(line, 2);
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        var copyRow = new MenuFlyoutItem { Text = "Kopiuj wiersz" };
        copyRow.Clicked += async (_, _) => { if (BindingContext is { } row) { await Ui.CopyAsync(_table.RowTsv(row)); } };
        menu.Add(copyRow);
        var id = _table.AllColumns.FirstOrDefault(column => column.IsId);
        if (id is not null)
        {
            var copyId = new MenuFlyoutItem { Text = "Kopiuj ID" };
            copyId.Clicked += async (_, _) => await Ui.CopyAsync(DataTable.Value(BindingContext, id.Key));
            menu.Add(copyId);
        }

        return menu;
    }
}

/// <summary>Inline share bar under a number (§4.5.1): 4 dp, metric colour at 35%, length proportional to the table max.</summary>
public sealed class InlineBar : ContentView
{
    public static readonly BindableProperty FractionProperty = BindableProperty.Create(
        nameof(Fraction), typeof(double), typeof(InlineBar), 0d, propertyChanged: (b, _, _) => ((InlineBar)b).Update());

    public static readonly BindableProperty ColorKeyProperty = BindableProperty.Create(
        nameof(ColorKey), typeof(string), typeof(InlineBar), "ColorDataCost", propertyChanged: (b, _, _) => ((InlineBar)b).Update());

    private readonly BoxView _bar = new();
    private readonly Grid _grid = new();

    public InlineBar()
    {
        HeightRequest = Ui.D("SizeInlineBar");
        _bar.HeightRequest = Ui.D("SizeInlineBar");
        _bar.Opacity = Ui.D("OpacityInlineBar");
        _bar.CornerRadius = Ui.D("SizeInlineBar") / 2;
        _grid.Add(_bar);
        Content = _grid;
        WidthRequest = Ui.D("SizeSelectMinWidth") / 2;
        Update();
    }

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    public string ColorKey { get => (string)GetValue(ColorKeyProperty); set => SetValue(ColorKeyProperty, value); }

    private void Update()
    {
        var fraction = Math.Clamp(double.IsNaN(Fraction) ? 0 : Fraction, 0, 1);
        _grid.ColumnDefinitions.Clear();
        _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(fraction, GridUnitType.Star)));
        _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - fraction, GridUnitType.Star)));
        _bar.Dyn(BoxView.ColorProperty, ColorKey);
        _bar.IsVisible = fraction > 0;
    }
}
