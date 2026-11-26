using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls.Charts;

/// <summary>
/// Interactive legend (DESIGN.md §4.8.2 B): one chip per series (dot · name · total); click hides/shows the series and the
/// chart re-scales (M5). The hidden set can be remembered per screen through <see cref="StateKey"/>.
/// </summary>
public sealed class ChartLegend : FlexLayout
{
    public static readonly BindableProperty ChartProperty = BindableProperty.Create(
        nameof(Chart), typeof(ChartView), typeof(ChartLegend), null, propertyChanged: (b, _, v) =>
        {
            var legend = (ChartLegend)b;
            if (v is ChartView chart)
            {
                chart.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ChartView.Data))
                    {
                        legend.Rebuild();
                    }
                };
            }

            legend.Rebuild();
        });

    public static readonly BindableProperty StateKeyProperty = BindableProperty.Create(nameof(StateKey), typeof(string), typeof(ChartLegend), string.Empty);

    public ChartLegend()
    {
        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap;
        AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center;
    }

    public ChartView? Chart { get => (ChartView?)GetValue(ChartProperty); set => SetValue(ChartProperty, value); }

    /// <summary>ViewState key for hidden series (§5.4).</summary>
    public string StateKey { get => (string)GetValue(StateKeyProperty); set => SetValue(StateKeyProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        if (Chart is not { } chart)
        {
            return;
        }

        var hidden = string.IsNullOrEmpty(StateKey)
            ? []
            : ViewModels.ViewState.GetString("legend." + StateKey, string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var series in chart.Data.Series)
        {
            if (hidden.Contains(series.Id))
            {
                series.IsVisible = false;
            }

            var label = Ui.Text("Text.Caption.Secondary", $"{series.Label} {chart.Data.Format(series.Total)}");
            var chip = new HorizontalStackLayout
            {
                Spacing = Ui.D("Space150"),
                Margin = new Thickness(0, 0, Ui.D("Space400"), Ui.D("Space100")),
                Opacity = series.IsVisible ? 1 : Ui.D("OpacityDisabled"),
                Children = { Ui.Dot(series.ColorKey), label }
            };
            SemanticProperties.SetDescription(chip, $"{series.Label}, {(series.IsVisible ? "widoczna" : "ukryta")}");
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                series.IsVisible = !series.IsVisible;
                if (!string.IsNullOrEmpty(StateKey))
                {
                    ViewModels.ViewState.Set("legend." + StateKey, string.Join('|', chart.Data.Series.Where(s => !s.IsVisible).Select(s => s.Id)));
                }

                chip.Opacity = series.IsVisible ? 1 : Ui.D("OpacityDisabled");
                chart.Invalidate();
            };
            chip.GestureRecognizers.Add(tap);
            Children.Add(chip);
        }

        chart.Invalidate();
    }
}

/// <summary>Legend of <see cref="OnionRings"/> (§4.4.3, mandatory): dot · name · percent · reset.</summary>
public sealed class RingLegend : VerticalStackLayout
{
    public static readonly BindableProperty RingsProperty = BindableProperty.Create(
        nameof(Rings), typeof(IReadOnlyList<OnionRing>), typeof(RingLegend), Array.Empty<OnionRing>(), propertyChanged: (b, _, _) => ((RingLegend)b).Rebuild());

    public static readonly BindableProperty OnHeroProperty = BindableProperty.Create(
        nameof(OnHero), typeof(bool), typeof(RingLegend), false, propertyChanged: (b, _, _) => ((RingLegend)b).Rebuild());

    public RingLegend()
    {
        Spacing = Ui.D("Space200");
        VerticalOptions = LayoutOptions.Center;
    }

    public IReadOnlyList<OnionRing> Rings { get => (IReadOnlyList<OnionRing>)GetValue(RingsProperty); set => SetValue(RingsProperty, value); }

    public bool OnHero { get => (bool)GetValue(OnHeroProperty); set => SetValue(OnHeroProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var ring in Rings)
        {
            var name = Ui.Text(OnHero ? "Text.Hero.Body" : "Text.Secondary", ring.Name);
            var value = Ui.Text("Text.MetricSm", double.IsNaN(ring.Percent) ? "—" : Fmt.Percent(ring.Percent));
            if (OnHero)
            {
                value.Dyn(Label.TextColorProperty, "ColorHeroText");
            }

            var line = new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { Ui.Dot(OnHero ? "ColorHeroText" : ring.ColorKey), name, value } };
            var block = new VerticalStackLayout { Children = { line } };
            if (!string.IsNullOrEmpty(ring.ResetText))
            {
                block.Children.Add(Ui.Text(OnHero ? "Text.Hero.Body" : "Text.Caption", ring.ResetText));
            }

            Children.Add(block);
        }
    }
}

/// <summary>
/// Chart card (§4.8.4): title + range caption, controls on the right (metric switch etc.), „Pokaż dane w tabeli” toggle,
/// legend, chart area (240/200 main, 160 auxiliary). The table view shows the same data (§4.8.1 A11y).
/// </summary>
public sealed class ChartCard : Card
{
    public static readonly BindableProperty ChartProperty = BindableProperty.Create(
        nameof(Chart), typeof(ChartView), typeof(ChartCard), null, propertyChanged: (b, _, _) => ((ChartCard)b).Build());

    public static readonly BindableProperty ControlsProperty = BindableProperty.Create(
        nameof(Controls), typeof(View), typeof(ChartCard), null, propertyChanged: (b, _, _) => ((ChartCard)b).Build());

    public static readonly BindableProperty ShowLegendProperty = BindableProperty.Create(
        nameof(ShowLegend), typeof(bool), typeof(ChartCard), true, propertyChanged: (b, _, _) => ((ChartCard)b).Build());

    public static readonly BindableProperty LegendStateKeyProperty = BindableProperty.Create(
        nameof(LegendStateKey), typeof(string), typeof(ChartCard), string.Empty, propertyChanged: (b, _, _) => ((ChartCard)b).Build());

    private readonly IconButton _tableToggle = new IconButton { Glyph = Icons.Table2 }.Styled("IconButton.Ghost.Sm");
    private readonly ContentView _area = new();
    private bool _showTable;

    public ChartCard()
    {
        ToolTipProperties.SetText(_tableToggle, "Pokaż dane w tabeli");
        SemanticProperties.SetDescription(_tableToggle, "Pokaż dane w tabeli");
        _tableToggle.Clicked += (_, _) =>
        {
            _showTable = !_showTable;
            _tableToggle.Glyph = _showTable ? Icons.ChartColumn : Icons.Table2;
            ToolTipProperties.SetText(_tableToggle, _showTable ? "Pokaż wykres" : "Pokaż dane w tabeli");
            UpdateArea();
        };
    }

    public ChartView? Chart { get => (ChartView?)GetValue(ChartProperty); set => SetValue(ChartProperty, value); }

    /// <summary>SegmentedControls (metric, values/%) placed next to the table toggle.</summary>
    public View? Controls { get => (View?)GetValue(ControlsProperty); set => SetValue(ControlsProperty, value); }

    public bool ShowLegend { get => (bool)GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }

    public string LegendStateKey { get => (string)GetValue(LegendStateKeyProperty); set => SetValue(LegendStateKeyProperty, value); }

    private void Build()
    {
        var actions = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
        if (Controls is not null)
        {
            actions.Children.Add(Controls);
        }

        actions.Children.Add(_tableToggle);
        Actions = actions;
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space300") };
        if (ShowLegend && Chart is not null && Chart is not DonutChart)
        {
            stack.Children.Add(new ChartLegend { StateKey = LegendStateKey, Chart = Chart });
        }

        stack.Children.Add(_area);
        Body = stack;
        if (Chart is not null)
        {
            Chart.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChartView.Data) && _showTable)
                {
                    UpdateArea();
                }
            };
        }

        UpdateArea();
    }

    private void UpdateArea()
    {
        if (Chart is null)
        {
            _area.Content = null;
            return;
        }

        _area.Content = _showTable ? Table(Chart.Data) : Chart;
    }

    /// <summary>The chart's data as a table: category × visible series (+ total).</summary>
    private static View Table(ChartData data)
    {
        var series = data.Series.Where(s => s.IsVisible).ToList();
        var columns = new List<DataColumn> { new() { Key = "Category", Header = "Okres", Star = 2 } };
        columns.AddRange(series.Select((s, i) => new DataColumn { Key = "V" + i, Header = s.Label, Align = ColumnAlign.End, Format = CellFormat.Number }));
        var rows = Enumerable.Range(0, data.Categories.Count)
            .Select(i => new ChartRow(data.Detail(i), series.Select(s => i < s.Points.Count && !double.IsNaN(s.Points[i]) ? data.Format(s.Points[i]) : "—").ToArray()))
            .ToList();
        return new DataTable { Columns = columns, ItemsSource = rows, HeightRequest = Ui.D("SizeDialogSm"), AutomationPrefix = "ChartTable" };
    }
}

/// <summary>Row of the chart data table (up to 8 series + „Pozostałe”).</summary>
internal sealed class ChartRow(string category, string[] values)
{
    public string Category { get; } = category;
    public string V0 => Value(0);
    public string V1 => Value(1);
    public string V2 => Value(2);
    public string V3 => Value(3);
    public string V4 => Value(4);
    public string V5 => Value(5);
    public string V6 => Value(6);
    public string V7 => Value(7);
    public string V8 => Value(8);

    private string Value(int index) => index < values.Length ? values[index] : string.Empty;
}
