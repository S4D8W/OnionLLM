using OnionLLM.Design;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls;

public enum MetricSize
{
    Display,
    Lg,
    Md,
    Sm,
    Table
}

/// <summary>
/// Number label (MoneyLabel / TokensLabel, DESIGN.md §4.5.6): shows a value already formatted by <c>Fmt</c>, renders the
/// unit at 60% in a secondary colour for sizes ≥ metric.md, appends the pricing marker and counts up on change (M3).
/// </summary>
public sealed class MetricLabel : Label
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(double), typeof(MetricLabel), double.NaN, propertyChanged: (b, o, n) => ((MetricLabel)b).OnValueChanged((double)o, (double)n));

    public static readonly BindableProperty FormatProperty = BindableProperty.Create(
        nameof(Format), typeof(MetricFormat), typeof(MetricLabel), MetricFormat.Cost, propertyChanged: (b, _, _) => ((MetricLabel)b).Render(((MetricLabel)b).Value));

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(MetricSize), typeof(MetricLabel), MetricSize.Md, propertyChanged: (b, _, _) => ((MetricLabel)b).ApplySize());

    public static readonly BindableProperty MarkerProperty = BindableProperty.Create(
        nameof(Marker), typeof(string), typeof(MetricLabel), string.Empty, propertyChanged: (b, _, _) => ((MetricLabel)b).Render(((MetricLabel)b).Value));

    public static readonly BindableProperty OnHeroProperty = BindableProperty.Create(
        nameof(OnHero), typeof(bool), typeof(MetricLabel), false, propertyChanged: (b, _, _) => ((MetricLabel)b).ApplySize());

    private bool _rendered;
    private double _shown = double.NaN;

    public MetricLabel()
    {
        ApplySize();
        DensityService.DensityChanged += (_, _) => ApplySize();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public MetricFormat Format { get => (MetricFormat)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    public MetricSize Size { get => (MetricSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public string Marker { get => (string)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public bool OnHero { get => (bool)GetValue(OnHeroProperty); set => SetValue(OnHeroProperty, value); }

    private void ApplySize()
    {
        this.Styled(Size switch
        {
            MetricSize.Display => "Text.Display",
            MetricSize.Lg => "Text.MetricLg",
            MetricSize.Sm => "Text.MetricSm",
            MetricSize.Table => "Text.TableNum",
            _ => "Text.MetricMd"
        });
        if (OnHero)
        {
            this.Dyn(TextColorProperty, "ColorHeroText");
        }
        else if (Format == MetricFormat.Cost && Size == MetricSize.Table)
        {
            this.Dyn(TextColorProperty, "ColorDataCost");
        }

        Render(_shown);
    }

    private async void OnValueChanged(double old, double value)
    {
        // M3: count-up only for refreshes, never for the first render (§2.7.3).
        if (!_rendered || double.IsNaN(old) || double.IsNaN(value) || MotionTokens.Counter == 0 || Window is null || old == value)
        {
            Render(value);
            _rendered = !double.IsNaN(value);
            return;
        }

        var tcs = new TaskCompletionSource();
        this.AbortAnimation("count");
        new Animation(t => Render(old + (value - old) * t), 0, 1, OnionEasing.Standard)
            .Commit(this, "count", length: MotionTokens.Counter, finished: (_, _) => { Render(value); tcs.TrySetResult(); });
        await tcs.Task;
    }

    private void Render(double value)
    {
        _shown = value;
        if (double.IsNaN(value))
        {
            Text = "—";
            return;
        }

        var formatted = Format switch
        {
            MetricFormat.Tokens => Fmt.Tokens((long)Math.Round(value)),
            MetricFormat.Number => Fmt.Number((long)Math.Round(value)),
            MetricFormat.Percent => Fmt.Percent(value),
            _ => Fmt.Cost(value)
        };
        var withUnit = Size is MetricSize.Display or MetricSize.Lg or MetricSize.Md;
        FormattedText = withUnit
            ? Ui.MetricText(formatted, FontSize, OnHero ? "ColorHeroTextSecondary" : "ColorTextSecondary", Marker)
            : null;
        if (!withUnit)
        {
            Text = formatted + Marker;
        }

        SemanticProperties.SetDescription(this, Format switch
        {
            MetricFormat.Cost => Fmt.CostSpoken(value),
            MetricFormat.Tokens => Fmt.TokensSpoken((long)value),
            MetricFormat.Percent => Fmt.PercentSpoken(value),
            _ => formatted
        });
    }
}

public enum MetricFormat
{
    Cost,
    Tokens,
    Number,
    Percent
}

/// <summary>KPI tile (§4.3.2): overline · metric.lg value (smaller unit) · context line. Optional command = Interactive.</summary>
public sealed class StatTile : Card
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(string), typeof(StatTile), string.Empty, propertyChanged: (b, _, _) => ((StatTile)b).Update());

    public static readonly BindableProperty MarkerProperty = BindableProperty.Create(
        nameof(Marker), typeof(string), typeof(StatTile), string.Empty, propertyChanged: (b, _, _) => ((StatTile)b).Update());

    public static readonly BindableProperty CaptionProperty = BindableProperty.Create(
        nameof(Caption), typeof(string), typeof(StatTile), string.Empty, propertyChanged: (b, _, v) =>
        {
            var tile = (StatTile)b;
            tile._caption.Text = (string)v;
            tile._caption.IsVisible = !string.IsNullOrEmpty((string)v);
        });

    public static readonly BindableProperty IsLoadingProperty = BindableProperty.Create(
        nameof(IsLoading), typeof(bool), typeof(StatTile), false, propertyChanged: (b, _, _) => ((StatTile)b).Update());

    /// <summary>§4.3.2 context row: TrendDelta of <see cref="TrendCurrent"/> against <see cref="TrendBaseline"/>.</summary>
    public static readonly BindableProperty TrendBaselineProperty = BindableProperty.Create(
        nameof(TrendBaseline), typeof(double), typeof(StatTile), double.NaN, propertyChanged: (b, _, v) => ((StatTile)b)._trend.Baseline = (double)v);

    public static readonly BindableProperty TrendCurrentProperty = BindableProperty.Create(
        nameof(TrendCurrent), typeof(double), typeof(StatTile), 0d, propertyChanged: (b, _, v) => ((StatTile)b)._trend.Current = (double)v);

    public static readonly BindableProperty TrendLabelProperty = BindableProperty.Create(
        nameof(TrendLabel), typeof(string), typeof(StatTile), string.Empty, propertyChanged: (b, _, v) => ((StatTile)b)._trend.Comparison = (string)v);

    /// <summary>Optional sparkline at the bottom of the tile (§4.3.2).</summary>
    public static readonly BindableProperty SparklineProperty = BindableProperty.Create(
        nameof(Sparkline), typeof(IReadOnlyList<double>), typeof(StatTile), null, propertyChanged: (b, _, v) =>
        {
            var tile = (StatTile)b;
            tile._spark.Points = v as IReadOnlyList<double> ?? [];
            tile._spark.IsVisible = v is IReadOnlyList<double> { Count: > 1 };
        });

    private readonly TrendDelta _trend = new();
    private readonly Charts.Sparkline _spark = new() { ColorKey = "ColorDataCost", IsVisible = false };
    private readonly Label _value = Ui.Text("Text.MetricLg");
    private readonly Label _caption = Ui.Text("Text.Caption");
    private readonly VerticalStackLayout _extra = [];
    private readonly View _skeleton = Skeleton.Lines(2);

    public StatTile()
    {
        IsOverlineTitle = true;
        this.Dyn(MinimumHeightRequestProperty, "SizeStatTileMin");
        _caption.IsVisible = false;
        Body = new VerticalStackLayout
        {
            Spacing = Ui.D("Space100"),
            Children = { _value, _skeleton, _trend, _caption, _extra, _spark }
        };
        Update();
    }

    /// <summary>Value formatted by <c>Fmt</c>; empty = no data („—”).</summary>
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Marker { get => (string)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }

    public double TrendBaseline { get => (double)GetValue(TrendBaselineProperty); set => SetValue(TrendBaselineProperty, value); }

    public double TrendCurrent { get => (double)GetValue(TrendCurrentProperty); set => SetValue(TrendCurrentProperty, value); }

    public string TrendLabel { get => (string)GetValue(TrendLabelProperty); set => SetValue(TrendLabelProperty, value); }

    public IReadOnlyList<double>? Sparkline { get => (IReadOnlyList<double>?)GetValue(SparklineProperty); set => SetValue(SparklineProperty, value); }

    /// <summary>Optional extra content below the caption (e.g. a small TokenBreakdown or a link).</summary>
    public View? Extra
    {
        get => _extra.Children.FirstOrDefault() as View;
        set
        {
            _extra.Children.Clear();
            if (value is not null)
            {
                _extra.Children.Add(value);
            }
        }
    }

    private void Update()
    {
        _skeleton.IsVisible = IsLoading;
        _value.IsVisible = !IsLoading;
        if (string.IsNullOrEmpty(Value))
        {
            _value.FormattedText = null;
            _value.Text = "—";
            _value.Dyn(Label.TextColorProperty, "ColorTextTertiary");
        }
        else
        {
            _value.Dyn(Label.TextColorProperty, "ColorTextPrimary");
            _value.FormattedText = Ui.MetricText(Value, Ui.D("TypeMetricLgSize"), marker: Marker);
        }

        SemanticProperties.SetDescription(this, $"{Title}: {(string.IsNullOrEmpty(Value) ? "brak danych" : Value)}. {Caption}");
    }
}

/// <summary>Meaning of a change for <see cref="TrendDelta"/> colours (§4.3.4: colour follows meaning, not direction).</summary>
public enum TrendMeaning
{
    /// <summary>Cost, tokens: growth = warning, drop = success.</summary>
    HigherIsWorse,

    /// <summary>Cache share: growth = success.</summary>
    HigherIsBetter
}

/// <summary>
/// Trend (§4.3.4): arrow + percent change against a baseline, coloured by meaning; under 2% „bez zmian” (tertiary);
/// no baseline (missing or 0) → not rendered. <c>OnHero</c>: hero text in a pill, meaning colour only on the icon.
/// </summary>
public sealed class TrendDelta : HorizontalStackLayout
{
    public static readonly BindableProperty CurrentProperty = Prop(nameof(Current), typeof(double), 0d);
    public static readonly BindableProperty BaselineProperty = Prop(nameof(Baseline), typeof(double), double.NaN);
    public static readonly BindableProperty ComparisonProperty = Prop(nameof(Comparison), typeof(string), string.Empty);
    public static readonly BindableProperty MeaningProperty = Prop(nameof(Meaning), typeof(TrendMeaning), TrendMeaning.HigherIsWorse);
    public static readonly BindableProperty OnHeroProperty = Prop(nameof(OnHero), typeof(bool), false);

    private const double NeutralBand = 2;

    private readonly Label _icon = Ui.Icon(string.Empty, "Text.Icon.Xs");
    private readonly Label _value = new Label { FontFamily = "NumMedium" }.Styled("Text.Label");
    private readonly Label _comparison = Ui.Text("Text.Caption");
    private readonly Border _pill = new() { StrokeThickness = 0 };

    public TrendDelta()
    {
        Spacing = Ui.D("Space150");
        _icon.VerticalOptions = _value.VerticalOptions = _comparison.VerticalOptions = LayoutOptions.Center;
        _pill.StrokeShape = Ui.Corner("RadiusFull");
        _pill.Content = new HorizontalStackLayout { Spacing = Ui.D("Space100"), Children = { _icon, _value } };
        Children.Add(_pill);
        Children.Add(_comparison);
        Update();
    }

    public double Current { get => (double)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    /// <summary>Value compared against; NaN or ≤ 0 hides the trend (no „∞%”).</summary>
    public double Baseline { get => (double)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }

    /// <summary>„vs wczoraj”, „vs średnia 7 dni”, „vs poprzednie 30 dni”.</summary>
    public string Comparison { get => (string)GetValue(ComparisonProperty); set => SetValue(ComparisonProperty, value); }

    public TrendMeaning Meaning { get => (TrendMeaning)GetValue(MeaningProperty); set => SetValue(MeaningProperty, value); }

    public bool OnHero { get => (bool)GetValue(OnHeroProperty); set => SetValue(OnHeroProperty, value); }

    /// <summary>True when there is a baseline to compare with (the control is shown).</summary>
    public bool HasBaseline => !double.IsNaN(Baseline) && Baseline > 0;

    private static BindableProperty Prop(string name, Type type, object fallback) =>
        BindableProperty.Create(name, type, typeof(TrendDelta), fallback, propertyChanged: (b, _, _) => ((TrendDelta)b).Update());

    private void Update()
    {
        IsVisible = HasBaseline;
        if (!HasBaseline)
        {
            return;
        }

        var change = (Current - Baseline) / Baseline * 100;
        var neutral = Math.Abs(change) < NeutralBand;
        var worse = Meaning == TrendMeaning.HigherIsWorse ? change > 0 : change < 0;
        _icon.Text = neutral ? Icons.Minus : change > 0 ? Icons.TrendingUp : Icons.TrendingDown;
        _value.Text = neutral ? "bez zmian" : (change > 0 ? "+" : "−") + Fmt.Percent(Math.Abs(change));
        _comparison.Text = Comparison;
        _comparison.IsVisible = Comparison.Length > 0;

        if (OnHero)
        {
            _pill.Padding = Ui.Thick("PaddingBadge");
            _pill.Dyn(BackgroundColorProperty, "ColorHeroPillBg");
            _value.Dyn(Label.TextColorProperty, "ColorHeroText");
            _comparison.Dyn(Label.TextColorProperty, "ColorHeroTextSecondary");
            _icon.Dyn(Label.TextColorProperty, neutral ? "ColorHeroTextSecondary" : worse ? "ColorHeroTrendWorse" : "ColorHeroTrendBetter");
        }
        else
        {
            var key = neutral ? "ColorTextTertiary" : worse ? "ColorStatusWarning" : "ColorStatusSuccess";
            _pill.Padding = Ui.Thick("ThicknessZero");
            _pill.Dyn(BackgroundColorProperty, "ColorTransparent");
            _icon.Dyn(Label.TextColorProperty, key);
            _value.Dyn(Label.TextColorProperty, key);
            _comparison.Dyn(Label.TextColorProperty, "ColorTextTertiary");
        }

        var amount = Fmt.Percent(Math.Abs(change)).Replace("%", " procent", StringComparison.Ordinal);
        var spoken = neutral ? "bez zmian" : $"{(change > 0 ? "wzrost" : "spadek")} o {amount}";
        SemanticProperties.SetDescription(this, $"{spoken} {Comparison.Replace("vs ", "względem ", StringComparison.Ordinal)}".Trim());
    }
}

/// <summary>Hero number of „Dziś” (§4.3.3) on the gradient card: overline with freshness dot · display · caption.</summary>
public sealed class HeroMetric : VerticalStackLayout
{
    public static readonly BindableProperty OverlineProperty = BindableProperty.Create(
        nameof(Overline), typeof(string), typeof(HeroMetric), "Dziś", propertyChanged: (b, _, v) => ((HeroMetric)b)._overline.Text = (string)v);

    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(double), typeof(HeroMetric), double.NaN, propertyChanged: (b, _, v) =>
        {
            var hero = (HeroMetric)b;
            hero._number.Value = (double)v;
            hero._yesterday.Current = hero._week.Current = (double)v;
        });

    /// <summary>Cost yesterday — TrendDelta „vs wczoraj” (§6.1); NaN or 0 hides it.</summary>
    public static readonly BindableProperty YesterdayProperty = BindableProperty.Create(
        nameof(Yesterday), typeof(double), typeof(HeroMetric), double.NaN, propertyChanged: (b, _, v) => ((HeroMetric)b)._yesterday.Baseline = (double)v);

    /// <summary>Average daily cost of the 7 days before today — TrendDelta „vs średnia 7 dni”.</summary>
    public static readonly BindableProperty WeekAverageProperty = BindableProperty.Create(
        nameof(WeekAverage), typeof(double), typeof(HeroMetric), double.NaN, propertyChanged: (b, _, v) => ((HeroMetric)b)._week.Baseline = (double)v);

    public static readonly BindableProperty MarkerProperty = BindableProperty.Create(
        nameof(Marker), typeof(string), typeof(HeroMetric), string.Empty, propertyChanged: (b, _, v) => ((HeroMetric)b)._number.Marker = (string)v);

    public static readonly BindableProperty CaptionProperty = BindableProperty.Create(
        nameof(Caption), typeof(string), typeof(HeroMetric), string.Empty, propertyChanged: (b, _, v) => ((HeroMetric)b)._caption.Text = (string)v);

    public static readonly BindableProperty IsStaleProperty = BindableProperty.Create(
        nameof(IsStale), typeof(bool), typeof(HeroMetric), false, propertyChanged: (b, _, v) =>
            ((HeroMetric)b)._dot.Dyn(BackgroundColorProperty, (bool)v ? "ColorStatusWarning" : "ColorStatusSuccess"));

    private readonly Label _overline = Ui.Text("Text.Hero.Overline", "Dziś");
    private readonly Border _dot = Ui.Dot("ColorStatusSuccess", "SizeHeroDot");
    private readonly MetricLabel _number = new() { Size = MetricSize.Display, OnHero = true, AutomationId = "Today.HeroCost" };
    private readonly Label _caption = Ui.Text("Text.Hero.Body");
    private readonly TrendDelta _yesterday = new() { OnHero = true, Comparison = "vs wczoraj" };
    private readonly TrendDelta _week = new() { OnHero = true, Comparison = "vs średnia 7 dni" };

    public HeroMetric()
    {
        Spacing = Ui.D("Space100");
        Children.Add(new HorizontalStackLayout { Spacing = Ui.D("Space150"), Children = { _dot, _overline } });
        Children.Add(_number);
        Children.Add(_caption);
        _yesterday.Margin = _week.Margin = new Thickness(0, 0, Ui.D("Space300"), Ui.D("Space100"));
        Children.Add(new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Children = { _yesterday, _week } });
    }

    public double Yesterday { get => (double)GetValue(YesterdayProperty); set => SetValue(YesterdayProperty, value); }

    public double WeekAverage { get => (double)GetValue(WeekAverageProperty); set => SetValue(WeekAverageProperty, value); }

    public string Overline { get => (string)GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Marker { get => (string)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <summary>Sync older than 2× the refresh interval → warning dot (S-STALE).</summary>
    public bool IsStale { get => (bool)GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }
}

/// <summary>Token types breakdown (§4.5.7): stacked bar + legend with numbers and shares + cache share line.</summary>
public sealed class TokenBreakdown : VerticalStackLayout
{
    public static readonly BindableProperty InputProperty = Create(nameof(Input));
    public static readonly BindableProperty OutputProperty = Create(nameof(Output));
    public static readonly BindableProperty CacheWriteProperty = Create(nameof(CacheWrite));
    public static readonly BindableProperty CacheReadProperty = Create(nameof(CacheRead));
    public static readonly BindableProperty ReasoningProperty = Create(nameof(Reasoning));
    public static readonly BindableProperty CacheWrite1hProperty = Create(nameof(CacheWrite1h));

    public static readonly BindableProperty IsCompactProperty = BindableProperty.Create(
        nameof(IsCompact), typeof(bool), typeof(TokenBreakdown), false, propertyChanged: (b, _, _) => ((TokenBreakdown)b).Update());

    private readonly Grid _bar = new() { ColumnSpacing = 0 };
    private readonly FlexLayout _legend = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
    private readonly Label _cacheShare = Ui.Text("Text.Caption");

    public TokenBreakdown()
    {
        Spacing = Ui.D("Space200");
        var track = Ui.Box(_bar, "ColorDataTrack", "RadiusFull");
        track.HeightRequest = Ui.D("SizeTokenBreakdownBar");
        Children.Add(track);
        Children.Add(_cacheShare);
        Children.Add(_legend);
        Update();
    }

    public long Input { get => (long)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public long Output { get => (long)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }

    public long CacheWrite { get => (long)GetValue(CacheWriteProperty); set => SetValue(CacheWriteProperty, value); }

    public long CacheRead { get => (long)GetValue(CacheReadProperty); set => SetValue(CacheReadProperty, value); }

    /// <summary>Reasoning tokens — part of output, shown as „w tym rozumowanie …”.</summary>
    public long Reasoning { get => (long)GetValue(ReasoningProperty); set => SetValue(ReasoningProperty, value); }

    /// <summary>1 h cache writes — part of cache writes, shown as „w tym 1 h: …”.</summary>
    public long CacheWrite1h { get => (long)GetValue(CacheWrite1hProperty); set => SetValue(CacheWrite1hProperty, value); }

    /// <summary>Compact: bar + one-line summary only (cards, KPI tiles).</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    private static BindableProperty Create(string name) => BindableProperty.Create(
        name, typeof(long), typeof(TokenBreakdown), 0L, propertyChanged: (b, _, _) => ((TokenBreakdown)b).Update());

    private void Update()
    {
        var parts = new (string Name, long Value, string ColorKey, string? Detail)[]
        {
            ("Wejście", Input, "ColorDataInput", null),
            ("Wyjście", Output, "ColorDataOutput", Reasoning > 0 ? $"w tym rozumowanie {Fmt.Tokens(Reasoning)}" : null),
            ("Zapis cache", CacheWrite, "ColorDataCacheWrite", CacheWrite1h > 0 ? $"w tym 1 h: {Fmt.Tokens(CacheWrite1h)}" : null),
            ("Odczyt cache", CacheRead, "ColorDataCacheRead", null)
        };
        var total = parts.Sum(part => part.Value);

        _bar.Children.Clear();
        _bar.ColumnDefinitions.Clear();
        foreach (var part in parts.Where(part => part.Value > 0))
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(part.Value, GridUnitType.Star)));
            _bar.Add(new BoxView { HeightRequest = Ui.D("SizeTokenBreakdownBar") }.Dyn(BoxView.ColorProperty, part.ColorKey),
                _bar.ColumnDefinitions.Count - 1);
        }

        var cacheShare = total > 0 ? (CacheWrite + CacheRead) * 100.0 / total : 0;
        _cacheShare.Text = total > 0 ? $"{Fmt.Percent(cacheShare)} z cache" : "Brak tokenów";

        _legend.Children.Clear();
        _legend.IsVisible = !IsCompact;
        foreach (var part in parts)
        {
            var share = total > 0 ? Fmt.Percent(part.Value * 100.0 / total) : "—";
            var number = Ui.Text("Text.TableNum", Fmt.Tokens(part.Value));
            number.HorizontalTextAlignment = TextAlignment.Start;
            var line = new HorizontalStackLayout
            {
                Spacing = Ui.D("Space150"),
                Children =
                {
                    Ui.Dot(part.ColorKey),
                    Ui.Text("Text.Secondary", part.Name),
                    number,
                    Ui.Text("Text.Caption", share)
                }
            };
            var cell = new VerticalStackLayout { Children = { line } };
            if (part.Detail is not null)
            {
                cell.Children.Add(Ui.Text("Text.Caption", part.Detail));
            }

            cell.Margin = new Thickness(0, 0, Ui.D("Space400"), Ui.D("Space100"));
            _legend.Children.Add(cell);
        }

        SemanticProperties.SetDescription(this,
            $"Tokeny: wejście {Fmt.TokensSpoken(Input)}, wyjście {Fmt.TokensSpoken(Output)}, zapis cache {Fmt.TokensSpoken(CacheWrite)}, " +
            $"odczyt cache {Fmt.TokensSpoken(CacheRead)}; {Fmt.Percent(cacheShare)} z cache.");
    }
}

/// <summary>
/// Progress bar (§4.4.1) drawn on a canvas: track + fill, threshold mode (80% warning, 100% danger with diagonal stripes),
/// optional 80/100% ticks, <c>OnHero</c> colours. Always paired with a number next to it.
/// </summary>
public sealed class ProgressBar : GraphicsView, IDrawable
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(double), typeof(ProgressBar), 0d, propertyChanged: (b, _, _) => ((ProgressBar)b).Invalidate());

    public static readonly BindableProperty IsThresholdProperty = BindableProperty.Create(
        nameof(IsThreshold), typeof(bool), typeof(ProgressBar), false, propertyChanged: (b, _, _) => ((ProgressBar)b).Invalidate());

    public static readonly BindableProperty ColorKeyProperty = BindableProperty.Create(
        nameof(ColorKey), typeof(string), typeof(ProgressBar), "ColorActionPrimary", propertyChanged: (b, _, _) => ((ProgressBar)b).Invalidate());

    public static readonly BindableProperty OnHeroProperty = BindableProperty.Create(
        nameof(OnHero), typeof(bool), typeof(ProgressBar), false, propertyChanged: (b, _, _) => ((ProgressBar)b).Invalidate());

    public static readonly BindableProperty ShowTicksProperty = BindableProperty.Create(
        nameof(ShowTicks), typeof(bool), typeof(ProgressBar), false, propertyChanged: (b, _, _) => ((ProgressBar)b).Invalidate());

    public ProgressBar()
    {
        Drawable = this;
        this.Dyn(HeightRequestProperty, "SizeProgressBar");
        ThemeService.ThemeChanged += (_, _) => Invalidate();
    }

    /// <summary>0–1 (values above 1 render full).</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool IsThreshold { get => (bool)GetValue(IsThresholdProperty); set => SetValue(IsThresholdProperty, value); }

    public string ColorKey { get => (string)GetValue(ColorKeyProperty); set => SetValue(ColorKeyProperty, value); }

    public bool OnHero { get => (bool)GetValue(OnHeroProperty); set => SetValue(OnHeroProperty, value); }

    public bool ShowTicks { get => (bool)GetValue(ShowTicksProperty); set => SetValue(ShowTicksProperty, value); }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var radius = rect.Height / 2;
        canvas.FillColor = DesignTokens.Color(OnHero ? "ColorHeroRingTrack" : "ColorDataTrack");
        canvas.FillRoundedRectangle(rect, radius);

        var value = double.IsNaN(Value) ? 0 : Math.Max(0, Value);
        var fillKey = OnHero ? "ColorHeroText" : ColorKey;
        if (IsThreshold && value >= 1)
        {
            fillKey = OnHero ? "ColorHeroProgressDanger" : "ColorStatusDanger";
        }
        else if (IsThreshold && value >= 0.8)
        {
            fillKey = OnHero ? "ColorHeroProgressWarning" : "ColorStatusWarning";
        }

        var width = (float)(rect.Width * Math.Min(1, value));
        if (width > 0)
        {
            var fill = new RectF(rect.X, rect.Y, Math.Max(width, rect.Height), rect.Height);
            canvas.FillColor = DesignTokens.Color(fillKey);
            canvas.FillRoundedRectangle(fill, radius);
            if (IsThreshold && value >= 1)
            {
                // Diagonal stripes: exceeded is distinguishable without colour.
                canvas.SaveState();
                canvas.ClipRectangle(fill);
                canvas.StrokeColor = DesignTokens.Color("ColorStripe");
                canvas.StrokeSize = (float)Ui.D("StrokeFocus");
                for (var x = fill.Left - fill.Height; x < fill.Right; x += fill.Height * 2)
                {
                    canvas.DrawLine(x, fill.Bottom, x + fill.Height, fill.Top);
                }

                canvas.RestoreState();
            }
        }

        if (ShowTicks)
        {
            canvas.StrokeColor = DesignTokens.Color("ColorTextTertiary");
            canvas.StrokeSize = (float)Ui.D("StrokeDefault");
            foreach (var at in new[] { 0.8f, 1f })
            {
                var x = rect.X + rect.Width * at - (at >= 1 ? 1 : 0);
                canvas.DrawLine(x, rect.Top, x, rect.Bottom);
            }
        }
    }
}

public enum LimitState
{
    Live,
    Estimated,
    Unavailable
}

/// <summary>Limit gauge (§4.4.2): name · badge · percent / threshold bar / reset caption / projection.</summary>
public sealed class LimitGauge : VerticalStackLayout
{
    public static readonly BindableProperty TitleProperty = Prop(nameof(Title), typeof(string), string.Empty);
    public static readonly BindableProperty PercentProperty = Prop(nameof(Percent), typeof(double), double.NaN);
    public static readonly BindableProperty ResetTextProperty = Prop(nameof(ResetText), typeof(string), string.Empty);
    public static readonly BindableProperty ProjectionTextProperty = Prop(nameof(ProjectionText), typeof(string), string.Empty);
    public static readonly BindableProperty StateProperty = Prop(nameof(State), typeof(LimitState), LimitState.Live);
    public static readonly BindableProperty LiveTooltipProperty = Prop(nameof(LiveTooltip), typeof(string), string.Empty);
    public static readonly BindableProperty UnavailableTextProperty = Prop(nameof(UnavailableText), typeof(string),
        "Zaloguj się ponownie w Claude Code, aby zobaczyć limity");

    private readonly Label _title = Ui.Text("Text.Label");
    private readonly StatusBadge _badge = new();
    private readonly Label _live = Ui.Icon(Icons.Radio, "Text.Icon.Xs", "ColorTextTertiary");
    private readonly Label _percent = Ui.Text("Text.MetricSm");
    private readonly ProgressBar _bar = new() { IsThreshold = true, ShowTicks = true };
    private readonly Label _reset = Ui.Text("Text.Caption");
    private readonly Label _projection = Ui.Text("Text.Caption");
    private readonly Label _unavailable = Ui.Text("Text.Caption.Secondary");

    public LimitGauge()
    {
        Spacing = Ui.D("Space100");
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        };
        _title.VerticalOptions = LayoutOptions.Center;
        header.Add(_title);
        header.Add(_badge, 1);
        header.Add(_live, 2);
        header.Add(_percent, 3);
        Children.Add(header);
        Children.Add(_bar);
        Children.Add(_reset);
        Children.Add(_projection);
        Children.Add(_unavailable);
        Update();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>0–100+; NaN = unknown.</summary>
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    public string ResetText { get => (string)GetValue(ResetTextProperty); set => SetValue(ResetTextProperty, value); }

    public string ProjectionText { get => (string)GetValue(ProjectionTextProperty); set => SetValue(ProjectionTextProperty, value); }

    public LimitState State { get => (LimitState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public string LiveTooltip { get => (string)GetValue(LiveTooltipProperty); set => SetValue(LiveTooltipProperty, value); }

    public string UnavailableText { get => (string)GetValue(UnavailableTextProperty); set => SetValue(UnavailableTextProperty, value); }

    private static BindableProperty Prop(string name, Type type, object defaultValue) => BindableProperty.Create(
        name, type, typeof(LimitGauge), defaultValue, propertyChanged: (b, _, _) => ((LimitGauge)b).Update());

    private void Update()
    {
        _title.Text = Title;
        var unavailable = State == LimitState.Unavailable || double.IsNaN(Percent);
        _bar.Value = unavailable ? 0 : Percent / 100;
        _percent.Text = unavailable ? "—" : (State == LimitState.Estimated ? "~" : "") + Fmt.Percent(Percent);
        _badge.IsVisible = !unavailable && Percent >= 80;
        _badge.Kind = Percent >= 100 ? StatusKind.Danger : StatusKind.Warning;
        _badge.Text = Percent >= 100 ? "Przekroczono" : "Blisko limitu";
        _live.IsVisible = State == LimitState.Live && !unavailable;
        ToolTipProperties.SetText(_live, LiveTooltip);
        ToolTipProperties.SetText(_percent, State == LimitState.Estimated ? "Szacowane z historii; dane na żywo niedostępne" : string.Empty);
        _reset.Text = ResetText;
        _reset.IsVisible = !unavailable && !string.IsNullOrEmpty(ResetText);
        _projection.Text = ProjectionText;
        _projection.IsVisible = !unavailable && !string.IsNullOrEmpty(ProjectionText);
        _unavailable.Text = UnavailableText;
        _unavailable.IsVisible = unavailable;
        SemanticProperties.SetDescription(this, unavailable
            ? $"{Title}: niedostępny. {UnavailableText}"
            : $"{Title}: {Fmt.PercentSpoken(Percent)}. {ResetText}");
    }
}
