using OnionLLM.Design;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls.Charts;

/// <summary>
/// Bar chart (DESIGN.md §4.8.2 A) and stacked bar chart (B): one series in time, or layers ordered by total (largest at the
/// bottom, „Pozostałe” on top) with 1 dp gaps; optional 100% mode; current period striped („w toku”); average line;
/// maximum marked with a highlight dot.
/// </summary>
public sealed class BarChart : ChartView
{
    public static readonly BindableProperty IsPercentProperty = BindableProperty.Create(
        nameof(IsPercent), typeof(bool), typeof(BarChart), false, propertyChanged: (b, _, _) => ((BarChart)b).Invalidate());

    public BarChart()
    {
        this.Dyn(HeightRequestProperty, "SizeChartMain");
    }

    /// <summary>„Wartości | Udział %” — every bar scaled to 100%.</summary>
    public bool IsPercent { get => (bool)GetValue(IsPercentProperty); set => SetValue(IsPercentProperty, value); }

    protected override void DrawChart(ICanvas canvas, RectF rect)
    {
        var series = Data.Series.Where(s => s.IsVisible)
            .OrderByDescending(s => s.Id == "other" ? double.MinValue : s.Total)
            .ToList();
        var count = Data.Categories.Count;
        var totals = Enumerable.Range(0, count).Select(i => series.Sum(s => Animated(s, i))).ToList();
        var max = IsPercent ? 100 : Math.Max(totals.DefaultIfEmpty(0).Max(), Data.Thresholds.Select(t => t.Value).DefaultIfEmpty(0).Max());
        var plot = DrawAxes(canvas, rect, max);
        var step = plot.Width / Math.Max(1, count);
        var barWidth = Math.Min(F("SizeChartBarMax"), step * 0.64f);
        var gap = F("StrokeDefault");
        var single = series.Count == 1;

        for (var i = 0; i < count; i++)
        {
            var x = plot.Left + i * step + (step - barWidth) / 2;
            var y = plot.Bottom;
            var dim = HoverIndex >= 0 && HoverIndex != i;
            var current = Data.LastInProgress && i == count - 1;
            for (var s = 0; s < series.Count; s++)
            {
                var value = Animated(series[s], i);
                if (IsPercent)
                {
                    value = totals[i] > 0 ? value / totals[i] * 100 : 0;
                }

                var height = (float)(value / MaxValue) * plot.Height;
                if (height <= 0)
                {
                    continue;
                }

                var top = s == series.Count - 1;
                var bar = new RectF(x, y - height, barWidth, height - (top ? 0 : gap));
                var color = C(series[s].ColorKey);
                canvas.FillColor = color.WithAlpha(dim ? (float)Ui.D("OpacityHover") : single && !current ? (float)Ui.D("OpacityGhost") : 1);
                if (top)
                {
                    canvas.FillRoundedRectangle(bar, F("RadiusXs"), F("RadiusXs"), 0, 0);
                }
                else
                {
                    canvas.FillRectangle(bar);
                }

                // Second encoding for same-hue series (§4.8.3): every other layer gets diagonal stripes.
                if (current || (!single && s % 2 == 1))
                {
                    Stripes(canvas, bar);
                }

                y -= height;
            }
        }

        if (single && !IsPercent)
        {
            var values = Enumerable.Range(0, count).Select(i => Animated(series[0], i)).ToList();
            var best = values.IndexOf(values.Max());
            canvas.FillColor = C("ColorDataHighlight");
            canvas.FillCircle(plot.Left + step * (best + 0.5f), ValueY(values[best]) - F("Space200"), F("SizeDotSm") / 2);
            if (Data.ShowAverage)
            {
                var average = values.Average();
                canvas.StrokeColor = C("ColorTextTertiary");
                canvas.StrokeDashPattern = [F("Space100"), F("Space100")];
                canvas.DrawLine(plot.Left, ValueY(average), plot.Right, ValueY(average));
                canvas.StrokeDashPattern = null;
                canvas.FontColor = C("ColorTextTertiary");
                SafeText.Draw(canvas, "śr. " + Data.Format(average), plot.Left, ValueY(average) - F("Space400"), plot.Width, F("Space400"), HorizontalAlignment.Right, VerticalAlignment.Bottom);
            }
        }

        foreach (var threshold in Data.Thresholds)
        {
            ThresholdLine(canvas, plot, threshold);
        }
    }

    internal static void Stripes(ICanvas canvas, RectF area)
    {
        canvas.SaveState();
        canvas.ClipRectangle(area);
        canvas.StrokeColor = DesignTokens.Color("ColorStripe");
        canvas.StrokeSize = F("StrokeFocus");
        var spacing = F("Space150");
        for (var x = area.Left - area.Height; x < area.Right; x += spacing)
        {
            canvas.DrawLine(x, area.Bottom, x + area.Height, area.Top);
        }

        canvas.RestoreState();
    }

    internal void ThresholdLine(ICanvas canvas, RectF plot, ChartThreshold threshold)
    {
        var y = ValueY(threshold.Value);
        canvas.StrokeColor = C(threshold.ColorKey);
        canvas.StrokeSize = F("StrokeDefault");
        canvas.StrokeDashPattern = [F("Space100"), F("Space100")];
        canvas.DrawLine(plot.Left, y, plot.Right, y);
        canvas.StrokeDashPattern = null;
        canvas.FontColor = C(threshold.ColorKey);
        SafeText.Draw(canvas, threshold.Label, plot.Left, y - F("Space400"), plot.Width, F("Space400"), HorizontalAlignment.Right, VerticalAlignment.Bottom);
    }
}

/// <summary>
/// Horizontal bar chart (§4.8.2 „BarChart poziomy”, §6.6 „Top projekty”): one row per category — name, bar scaled to
/// the largest value, value and share. Hover/keyboard highlight a row (others dimmed); click/Enter = CategoryActivated.
/// </summary>
public sealed class HorizontalBarChart : ChartView
{
    public HorizontalBarChart()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Data))
            {
                UpdateHeight();
            }
        };
        DensityService.DensityChanged += (_, _) => UpdateHeight();
        UpdateHeight();
    }

    private float RowHeight => F("SizeRowHeightDense");

    private void UpdateHeight() => HeightRequest = Math.Max(1, Data.Categories.Count) * Ui.D("SizeRowHeightDense");

    protected override void DrawChart(ICanvas canvas, RectF rect)
    {
        var series = Data.Series.FirstOrDefault(s => s.IsVisible);
        if (series is null)
        {
            return;
        }

        var count = Data.Categories.Count;
        var values = Enumerable.Range(0, count).Select(i => Math.Max(0, Animated(series, i))).ToList();
        var max = Math.Max(values.DefaultIfEmpty(0).Max(), double.Epsilon);
        var total = Enumerable.Range(0, count).Sum(i => i < series.Points.Count && !double.IsNaN(series.Points[i]) ? series.Points[i] : 0);
        var gap = F("Space300");
        var labelFont = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("InterRegular"));
        var labels = Data.Categories.Select(text => canvas.GetStringSize(text, labelFont, AxisSize).Width).DefaultIfEmpty(0).Max();
        var labelWidth = Math.Min(rect.Width * 0.35f, labels + gap);
        var texts = Enumerable.Range(0, count).Select(i => ValueText(series, i, total)).ToList();
        var valueWidth = texts.Select(text => TextWidth(canvas, text)).DefaultIfEmpty(0).Max() + gap;
        Plot = new RectF(rect.Left + labelWidth, rect.Top, Math.Max(0, rect.Width - labelWidth - valueWidth), rect.Height);
        var barHeight = Math.Min(F("SizeChartBarMax"), RowHeight * 0.5f);

        for (var i = 0; i < count; i++)
        {
            var top = rect.Top + i * RowHeight;
            var dim = HoverIndex >= 0 && HoverIndex != i;
            if (HoverIndex == i)
            {
                canvas.FillColor = C("ColorStateHover");
                canvas.FillRoundedRectangle(new RectF(rect.Left, top, rect.Width, RowHeight), F("RadiusSm"));
            }

            canvas.Font = labelFont;
            canvas.FontColor = C("ColorTextPrimary");
            SafeText.Draw(canvas, Data.Categories[i], rect.Left, top, labelWidth - gap, RowHeight, HorizontalAlignment.Left, VerticalAlignment.Center);
            canvas.Font = AxisFont;

            var width = (float)(values[i] / max) * Plot.Width;
            if (width > 0)
            {
                canvas.FillColor = C(series.ColorKey).WithAlpha(dim ? (float)Ui.D("OpacityHover") : 1);
                canvas.FillRoundedRectangle(new RectF(Plot.Left, top + (RowHeight - barHeight) / 2, width, barHeight), 0, F("RadiusXs"), 0, F("RadiusXs"));
            }

            canvas.FontColor = C(dim ? "ColorTextTertiary" : "ColorTextSecondary");
            SafeText.Draw(canvas, texts[i], Plot.Right + gap, top, valueWidth - gap, RowHeight, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }

    private string ValueText(ChartSeries series, int index, double total)
    {
        var value = index < series.Points.Count ? series.Points[index] : 0;
        return total > 0 ? $"{Data.Format(value)} · {Fmt.Percent(value * 100 / total)}" : Data.Format(value);
    }

    protected override int IndexAt(PointF point)
    {
        var count = Data.Categories.Count;
        return count == 0 || point.Y < 0 ? -1 : Math.Min(count - 1, (int)(point.Y / RowHeight));
    }

    /// <summary>Values are printed in every row — the highlight replaces the tooltip.</summary>
    protected override void DrawTooltip(ICanvas canvas, RectF rect, int index)
    {
    }

    protected override string Describe(ChartData data)
    {
        var series = data.Series.FirstOrDefault();
        if (series is null || data.Categories.Count == 0)
        {
            return "Brak danych";
        }

        return "Wykres poziomy: " + string.Join("; ", data.Categories.Select((name, i) =>
            $"{name} {(i < series.Points.Count ? data.Format(series.Points[i]) : "")}"));
    }
}

/// <summary>Line / area chart (§4.8.2 C): 2 dp lines, area gradient 20% → 0%, dashed projection, threshold lines.</summary>
public sealed class LineChart : ChartView
{
    public static readonly BindableProperty IsAreaProperty = BindableProperty.Create(
        nameof(IsArea), typeof(bool), typeof(LineChart), true, propertyChanged: (b, _, _) => ((LineChart)b).Invalidate());

    public LineChart()
    {
        this.Dyn(HeightRequestProperty, "SizeChartMain");
    }

    public bool IsArea { get => (bool)GetValue(IsAreaProperty); set => SetValue(IsAreaProperty, value); }

    protected override void DrawChart(ICanvas canvas, RectF rect)
    {
        var series = Data.Series.Where(s => s.IsVisible).ToList();
        var count = Data.Categories.Count;
        var max = Math.Max(series.SelectMany(s => s.Points).Where(p => !double.IsNaN(p)).DefaultIfEmpty(0).Max(),
            Data.Thresholds.Select(t => t.Value).DefaultIfEmpty(0).Max());
        var plot = DrawAxes(canvas, rect, max);
        var step = plot.Width / Math.Max(1, count);
        foreach (var s in series)
        {
            var points = Enumerable.Range(0, count).Select(i => new PointF(plot.Left + step * (i + 0.5f), ValueY(Animated(s, i)))).ToList();
            var color = C(s.ColorKey);
            var split = Data.ProjectionFrom is { } from ? Math.Clamp(from, 0, count - 1) : count - 1;
            if (IsArea && points.Count > 1)
            {
                var area = new PathF(points[0].X, plot.Bottom);
                foreach (var point in points.Take(split + 1))
                {
                    area.LineTo(point);
                }

                area.LineTo(points[split].X, plot.Bottom);
                area.Close();
                canvas.SetFillPaint(new LinearGradientPaint(
                    [new PaintGradientStop(0, color.WithAlpha(0.2f)), new PaintGradientStop(1, color.WithAlpha(0))],
                    new Point(0, 0), new Point(0, 1)), new RectF(plot.Left, plot.Top, plot.Width, plot.Height));
                canvas.FillPath(area);
            }

            canvas.StrokeColor = color;
            canvas.StrokeSize = F("StrokeFocus");
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.StrokeLineCap = LineCap.Round;
            Polyline(canvas, points.Take(split + 1).ToList());
            if (split < count - 1)
            {
                canvas.StrokeDashPattern = [F("Space100"), F("Space100")];
                Polyline(canvas, points.Skip(split).ToList());
                canvas.StrokeDashPattern = null;
                canvas.FillColor = color.WithAlpha(0.08f);
                var cone = new PathF(points[split]);
                cone.LineTo(points[^1].X, points[^1].Y - plot.Height * 0.1f);
                cone.LineTo(points[^1].X, Math.Min(plot.Bottom, points[^1].Y + plot.Height * 0.1f));
                cone.Close();
                canvas.FillPath(cone);
            }

            if (HoverIndex >= 0 && HoverIndex < points.Count)
            {
                canvas.FillColor = C("ColorSurface1");
                canvas.FillCircle(points[HoverIndex], F("SizeDotSm") / 2 + F("StrokeFocus"));
                canvas.FillColor = color;
                canvas.FillCircle(points[HoverIndex], F("SizeDotSm") / 2);
            }
        }

        foreach (var threshold in Data.Thresholds)
        {
            var y = ValueY(threshold.Value);
            canvas.StrokeColor = C(threshold.ColorKey);
            canvas.StrokeSize = F("StrokeDefault");
            canvas.StrokeDashPattern = [F("Space100"), F("Space100")];
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.StrokeDashPattern = null;
            canvas.FontColor = C(threshold.ColorKey);
            SafeText.Draw(canvas, threshold.Label, plot.Left, y - F("Space400"), plot.Width, F("Space400"), HorizontalAlignment.Right, VerticalAlignment.Bottom);
        }
    }

    internal static void Polyline(ICanvas canvas, IReadOnlyList<PointF> points)
    {
        if (points.Count < 2)
        {
            return;
        }

        var path = new PathF(points[0]);
        foreach (var point in points.Skip(1))
        {
            path.LineTo(point);
        }

        canvas.DrawPath(path);
    }
}

/// <summary>Sparkline (§4.8.2 D): small trend without axes; 1.5 dp line, last point dot, optional 12% area.</summary>
public sealed class Sparkline : GraphicsView, IDrawable
{
    public static readonly BindableProperty PointsProperty = BindableProperty.Create(
        nameof(Points), typeof(IReadOnlyList<double>), typeof(Sparkline), Array.Empty<double>(), propertyChanged: (b, _, _) => ((Sparkline)b).Invalidate());

    public static readonly BindableProperty ColorKeyProperty = BindableProperty.Create(
        nameof(ColorKey), typeof(string), typeof(Sparkline), "ColorDataCost", propertyChanged: (b, _, _) => ((Sparkline)b).Invalidate());

    public Sparkline()
    {
        Drawable = this;
        HeightRequest = Ui.D("SizeSparkline");
        ThemeService.ThemeChanged += (_, _) => Invalidate();
    }

    public IReadOnlyList<double> Points { get => (IReadOnlyList<double>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public string ColorKey { get => (string)GetValue(ColorKeyProperty); set => SetValue(ColorKeyProperty, value); }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var values = Points.Select(point => double.IsNaN(point) ? 0 : point).ToList();
        if (values.Count < 2)
        {
            return;
        }

        var max = Math.Max(values.Max(), double.Epsilon);
        var inset = (float)Ui.D("SizeDotSm") / 2;
        var area = new RectF(rect.Left + inset, rect.Top + inset, rect.Width - inset * 2, rect.Height - inset * 2);
        var points = values.Select((value, i) => new PointF(area.Left + area.Width * i / (values.Count - 1), area.Bottom - (float)(value / max) * area.Height)).ToList();
        var color = DesignTokens.Color(ColorKey);
        var fill = new PathF(points[0].X, area.Bottom);
        points.ForEach(point => fill.LineTo(point));
        fill.LineTo(points[^1].X, area.Bottom);
        fill.Close();
        canvas.FillColor = color.WithAlpha(0.12f);
        canvas.FillPath(fill);
        canvas.StrokeColor = color;
        canvas.StrokeSize = (float)Ui.D("StrokeDefault") * 1.5f;
        LineChart.Polyline(canvas, points);
        canvas.FillColor = color;
        canvas.FillCircle(points[^1], inset * 0.7f);
    }
}

/// <summary>Donut (§4.8.2 E): thickness 18% of the radius, 2° gaps, max 6 segments + „Pozostałe”, total in the centre.</summary>
public sealed class DonutChart : ChartView
{
    public static readonly BindableProperty CenterLabelProperty = BindableProperty.Create(
        nameof(CenterLabel), typeof(string), typeof(DonutChart), string.Empty, propertyChanged: (b, _, _) => ((DonutChart)b).Invalidate());

    public DonutChart()
    {
        HeightRequest = Ui.D("SizeChartAux");
    }

    /// <summary>„Koszt · 30 dni”.</summary>
    public string CenterLabel { get => (string)GetValue(CenterLabelProperty); set => SetValue(CenterLabelProperty, value); }

    /// <summary>Donut slices use the first point of each series.</summary>
    protected override void DrawChart(ICanvas canvas, RectF rect)
    {
        var slices = Data.Series.Where(s => s.IsVisible).Select(s => (s, value: Animated(s, 0))).Where(x => x.value > 0).ToList();
        var total = slices.Sum(x => x.value);
        var radius = Math.Min(rect.Width, rect.Height) / 2 - F("Space100");
        var center = rect.Center;
        var thickness = radius * 0.18f;
        Plot = rect;
        var start = 90f;
        foreach (var (series, value) in slices)
        {
            var sweep = (float)(value / total * 360 * Progress);
            canvas.StrokeColor = C(series.ColorKey);
            canvas.StrokeSize = thickness;
            canvas.StrokeLineCap = LineCap.Butt;
            var gap = slices.Count > 1 ? 2f : 0;
            canvas.DrawArc(center.X - radius + thickness / 2, center.Y - radius + thickness / 2, (radius - thickness / 2) * 2, (radius - thickness / 2) * 2,
                start - gap / 2, start - sweep + gap / 2, true, false);
            start -= sweep;
        }

        canvas.FontColor = C("ColorTextPrimary");
        canvas.FontSize = F("TypeMetricMdSize");
        canvas.Font = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("NumSemiBold"));
        SafeText.Draw(canvas, Data.Format(total), center.X - radius, center.Y - F("TypeMetricMdSize"), radius * 2, F("TypeMetricMdSize") * 1.3f, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.FontSize = F("TypeCaptionSize");
        canvas.Font = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("InterRegular"));
        canvas.FontColor = C("ColorTextTertiary");
        SafeText.Draw(canvas, CenterLabel, center.X - radius, center.Y + F("Space100"), radius * 2, F("TypeCaptionSize") * 1.4f, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    protected override int IndexAt(PointF point) => -1;
}

/// <summary>
/// Calendar heatmap (§4.8.2 F, „Kalendarz”): weeks × weekdays, square cells with a 5-step quantile scale
/// (<c>ColorHeat0…4</c>); the hour heatmap is not rendered until hourly data exists in Core.
/// </summary>
public sealed class Heatmap : GraphicsView, IDrawable
{
    public static readonly BindableProperty DaysProperty = BindableProperty.Create(
        nameof(Days), typeof(IReadOnlyList<(DateTime Date, double Value)>), typeof(Heatmap), Array.Empty<(DateTime, double)>(),
        propertyChanged: (b, _, _) => ((Heatmap)b).Update());

    public static readonly BindableProperty UnitProperty = BindableProperty.Create(
        nameof(Unit), typeof(ChartUnit), typeof(Heatmap), ChartUnit.Cost, propertyChanged: (b, _, _) => ((Heatmap)b).Update());

    private int _hover = -1;

    public Heatmap()
    {
        Drawable = this;
        HeightRequest = Ui.D("SizeChartAux");
        ThemeService.ThemeChanged += (_, _) => Invalidate();
        StartHoverInteraction += (_, e) => HoverAt(e.Touches.FirstOrDefault());
        MoveHoverInteraction += (_, e) => HoverAt(e.Touches.FirstOrDefault());
        EndHoverInteraction += (_, _) => { _hover = -1; Invalidate(); };
    }

    /// <summary>Daily values (oldest first).</summary>
    public IReadOnlyList<(DateTime Date, double Value)> Days { get => (IReadOnlyList<(DateTime, double)>)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public ChartUnit Unit { get => (ChartUnit)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private float Cell => (float)Ui.D("SizeCalendarCell") / 2;

    private float Gap => (float)Ui.D("Space050");

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Days.Count == 0)
        {
            return;
        }

        var levels = Quantiles();
        var first = Days[0].Date;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        canvas.FontSize = (float)Ui.D("TypeCaptionSize");
        for (var i = 0; i < Days.Count; i++)
        {
            var (x, y) = Position(i, offset);
            var level = Days[i].Value <= 0 ? 0 : 1 + levels.Count(q => Days[i].Value > q);
            canvas.FillColor = DesignTokens.Color("ColorHeat" + Math.Min(4, level));
            canvas.FillRoundedRectangle(rect.Left + x, rect.Top + y, Cell, Cell, (float)Ui.D("RadiusXs") / 2);
            if (i == _hover)
            {
                canvas.StrokeColor = DesignTokens.Color("ColorBorderFocus");
                canvas.StrokeSize = (float)Ui.D("StrokeDefault");
                canvas.DrawRoundedRectangle(rect.Left + x, rect.Top + y, Cell, Cell, (float)Ui.D("RadiusXs") / 2);
            }
        }

        // Legend „Mniej ▢▢▢▢▢ Więcej”.
        var legendY = rect.Top + 7 * (Cell + Gap) + Gap * 4;
        canvas.FontColor = DesignTokens.Color("ColorTextTertiary");
        SafeText.Draw(canvas, "Mniej", rect.Left, legendY, (float)Ui.D("Space1200"), Cell, HorizontalAlignment.Left, VerticalAlignment.Center);
        var legendX = rect.Left + (float)Ui.D("Space1200");
        for (var level = 0; level <= 4; level++)
        {
            canvas.FillColor = DesignTokens.Color("ColorHeat" + level);
            canvas.FillRoundedRectangle(legendX + level * (Cell + Gap), legendY, Cell, Cell, (float)Ui.D("RadiusXs") / 2);
        }

        SafeText.Draw(canvas, "Więcej", legendX + 5 * (Cell + Gap) + Gap, legendY, (float)Ui.D("Space1200"), Cell, HorizontalAlignment.Left, VerticalAlignment.Center);

        if (_hover >= 0)
        {
            var (date, value) = Days[_hover];
            var text = $"{Fmt.DayDate(date)} · {Format(value)}";
            canvas.FontColor = DesignTokens.Color("ColorTextPrimary");
            SafeText.Draw(canvas, text, rect.Left, legendY, rect.Width, Cell, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }

    private (float X, float Y) Position(int index, int offset)
    {
        var slot = index + offset;
        return (slot / 7 * (Cell + Gap), slot % 7 * (Cell + Gap));
    }

    private double[] Quantiles()
    {
        var values = Days.Select(day => day.Value).Where(value => value > 0).OrderBy(value => value).ToList();
        return values.Count == 0 ? [] : [values[values.Count / 4], values[values.Count / 2], values[values.Count * 3 / 4]];
    }

    private string Format(double value) => new ChartData { Unit = Unit }.Format(value);

    private void Update()
    {
        Invalidate();
        var total = Days.Sum(day => day.Value);
        SemanticProperties.SetDescription(this, $"Kalendarz aktywności: {Days.Count} dni, łącznie {Format(total)}.");
    }

    private void HoverAt(PointF point)
    {
        if (Days.Count == 0)
        {
            return;
        }

        var offset = ((int)Days[0].Date.DayOfWeek + 6) % 7;
        var column = (int)(point.X / (Cell + Gap));
        var row = (int)(point.Y / (Cell + Gap));
        var index = row < 7 ? column * 7 + row - offset : -1;
        _hover = index >= 0 && index < Days.Count ? index : -1;
        Invalidate();
    }
}

/// <summary>
/// 5-hour block timeline (§4.8.2 H): track, cumulative cost area in the Claude colour, „teraz” line, dashed projection
/// to the end of the block, optional limit line. Mini variant (24 dp) for table cells.
/// </summary>
public sealed class BlockTimeline : GraphicsView, IDrawable
{
    public static readonly BindableProperty CumulativeProperty = BindableProperty.Create(
        nameof(Cumulative), typeof(IReadOnlyList<double>), typeof(BlockTimeline), Array.Empty<double>(), propertyChanged: (b, _, _) => ((BlockTimeline)b).Invalidate());

    /// <summary>Elapsed share of the block 0–1 (position of „teraz”); 1 = finished block.</summary>
    public static readonly BindableProperty NowProperty = BindableProperty.Create(
        nameof(Now), typeof(double), typeof(BlockTimeline), 1d, propertyChanged: (b, _, _) => ((BlockTimeline)b).Invalidate());

    /// <summary>Projected total at the end of the block (NaN = none).</summary>
    public static readonly BindableProperty ProjectionProperty = BindableProperty.Create(
        nameof(Projection), typeof(double), typeof(BlockTimeline), double.NaN, propertyChanged: (b, _, _) => ((BlockTimeline)b).Invalidate());

    public static readonly BindableProperty LimitProperty = BindableProperty.Create(
        nameof(Limit), typeof(double), typeof(BlockTimeline), double.NaN, propertyChanged: (b, _, _) => ((BlockTimeline)b).Invalidate());

    public static readonly BindableProperty IsMiniProperty = BindableProperty.Create(
        nameof(IsMini), typeof(bool), typeof(BlockTimeline), false, propertyChanged: (b, _, v) => ((BlockTimeline)b).HeightRequest = Ui.D((bool)v ? "IconLg" : "SizeChartAux") * ((bool)v ? 1 : 0.75));

    public BlockTimeline()
    {
        Drawable = this;
        HeightRequest = Ui.D("SizeChartAux") * 0.75;
        ThemeService.ThemeChanged += (_, _) => Invalidate();
    }

    /// <summary>Cumulative value samples across the elapsed part of the block.</summary>
    public IReadOnlyList<double> Cumulative { get => (IReadOnlyList<double>)GetValue(CumulativeProperty); set => SetValue(CumulativeProperty, value); }

    public double Now { get => (double)GetValue(NowProperty); set => SetValue(NowProperty, value); }

    public double Projection { get => (double)GetValue(ProjectionProperty); set => SetValue(ProjectionProperty, value); }

    public double Limit { get => (double)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

    public bool IsMini { get => (bool)GetValue(IsMiniProperty); set => SetValue(IsMiniProperty, value); }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var radius = (float)Ui.D(IsMini ? "RadiusXs" : "RadiusSm");
        canvas.FillColor = DesignTokens.Color("ColorDataTrack");
        canvas.FillRoundedRectangle(rect, radius);
        var values = Cumulative.Select(value => double.IsNaN(value) ? 0 : value).ToList();
        var max = new[] { values.DefaultIfEmpty(0).Max(), double.IsNaN(Projection) ? 0 : Projection, double.IsNaN(Limit) ? 0 : Limit }.Max();
        if (max <= 0)
        {
            return;
        }

        float X(double share) => rect.Left + (float)share * rect.Width;
        float Y(double value) => rect.Bottom - (float)(value / max) * rect.Height * 0.9f;
        var now = Math.Clamp(Now, 0, 1);
        var color = DesignTokens.SourceColor("claude");
        if (values.Count > 1)
        {
            var area = new PathF(X(0), rect.Bottom);
            for (var i = 0; i < values.Count; i++)
            {
                area.LineTo(X(now * i / (values.Count - 1)), Y(values[i]));
            }

            area.LineTo(X(now), rect.Bottom);
            area.Close();
            canvas.FillColor = color.WithAlpha(0.35f);
            canvas.FillPath(area);
            canvas.StrokeColor = color;
            canvas.StrokeSize = (float)Ui.D("StrokeFocus");
            LineChart.Polyline(canvas, values.Select((v, i) => new PointF(X(now * i / (values.Count - 1)), Y(v))).ToList());
        }

        if (!double.IsNaN(Projection) && now < 1 && values.Count > 0)
        {
            canvas.StrokeColor = color;
            canvas.StrokeDashPattern = [(float)Ui.D("Space100"), (float)Ui.D("Space100")];
            canvas.DrawLine(X(now), Y(values[^1]), X(1), Y(Projection));
            canvas.StrokeDashPattern = null;
        }

        if (!double.IsNaN(Limit) && Limit > 0)
        {
            canvas.StrokeColor = DesignTokens.Color("ColorStatusWarning");
            canvas.StrokeSize = (float)Ui.D("StrokeDefault");
            canvas.StrokeDashPattern = [(float)Ui.D("Space100"), (float)Ui.D("Space100")];
            canvas.DrawLine(rect.Left, Y(Limit), rect.Right, Y(Limit));
            canvas.StrokeDashPattern = null;
        }

        if (now < 1)
        {
            canvas.StrokeColor = DesignTokens.Color("ColorTextPrimary");
            canvas.StrokeSize = (float)Ui.D("StrokeDefault");
            canvas.DrawLine(X(now), rect.Top, X(now), rect.Bottom);
            if (!IsMini)
            {
                canvas.FontSize = (float)Ui.D("TypeCaptionSize");
                canvas.FontColor = DesignTokens.Color("ColorTextPrimary");
                SafeText.Draw(canvas, "teraz", X(now) + (float)Ui.D("Space100"), rect.Top, (float)Ui.D("Space1200"), (float)Ui.D("Space400"), HorizontalAlignment.Left, VerticalAlignment.Top);
            }
        }
    }
}

/// <summary>One ring of <see cref="OnionRings"/> (outermost first).</summary>
public sealed record OnionRing(string Name, double Percent, string ColorKey, string ResetText = "");

/// <summary>
/// Onion rings — brand signature (§4.4.3): up to 3 concentric limit rings from 12 o'clock, threshold colours (80% warning,
/// 100% danger, dashed when exceeded), the most critical percentage in the centre; <c>OnHero</c> variant; M11.
/// </summary>
public sealed class OnionRings : GraphicsView, IDrawable
{
    public static readonly BindableProperty RingsProperty = BindableProperty.Create(
        nameof(Rings), typeof(IReadOnlyList<OnionRing>), typeof(OnionRings), Array.Empty<OnionRing>(), propertyChanged: (b, _, _) => ((OnionRings)b).Animate());

    public static readonly BindableProperty OnHeroProperty = BindableProperty.Create(
        nameof(OnHero), typeof(bool), typeof(OnionRings), false, propertyChanged: (b, _, _) => ((OnionRings)b).Invalidate());

    public static readonly BindableProperty ShowCenterProperty = BindableProperty.Create(
        nameof(ShowCenter), typeof(bool), typeof(OnionRings), true, propertyChanged: (b, _, _) => ((OnionRings)b).Invalidate());

    private double _progress = 1;

    public OnionRings()
    {
        Drawable = this;
        ThemeService.ThemeChanged += (_, _) => Invalidate();
        DensityService.DensityChanged += (_, _) => Invalidate();
    }

    public IReadOnlyList<OnionRing> Rings { get => (IReadOnlyList<OnionRing>)GetValue(RingsProperty); set => SetValue(RingsProperty, value); }

    public bool OnHero { get => (bool)GetValue(OnHeroProperty); set => SetValue(OnHeroProperty, value); }

    /// <summary>Centre text (off for the 40 dp mini variant).</summary>
    public bool ShowCenter { get => (bool)GetValue(ShowCenterProperty); set => SetValue(ShowCenterProperty, value); }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var size = Math.Min(rect.Width, rect.Height);
        var center = rect.Center;
        var thickness = (float)Ui.D("SizeRingThickness");
        var gap = (float)Ui.D("Space100");
        var radius = size / 2 - thickness / 2;
        var heroAlphas = new[] { 1f, 0.75f, 0.55f };
        for (var i = 0; i < Math.Min(3, Rings.Count); i++)
        {
            var ring = Rings[i];
            var r = radius - i * (thickness + gap);
            if (r <= thickness)
            {
                break;
            }

            canvas.StrokeSize = thickness;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeColor = DesignTokens.Color(OnHero ? "ColorHeroRingTrack" : "ColorDataTrack");
            canvas.DrawCircle(center, r);
            if (double.IsNaN(ring.Percent))
            {
                continue;
            }

            var key = ring.Percent >= 100 ? (OnHero ? "ColorHeroProgressDanger" : "ColorStatusDanger")
                : ring.Percent >= 80 ? (OnHero ? "ColorHeroProgressWarning" : "ColorStatusWarning")
                : OnHero ? "ColorHeroText" : ring.ColorKey;
            canvas.StrokeColor = DesignTokens.Color(key).WithAlpha(OnHero && ring.Percent < 80 ? heroAlphas[i] : 1);
            if (ring.Percent >= 100)
            {
                canvas.StrokeDashPattern = [thickness, thickness / 2];
            }

            var sweep = (float)(Math.Min(100, ring.Percent) / 100 * 360 * _progress);
            if (sweep > 0)
            {
                canvas.DrawArc(center.X - r, center.Y - r, r * 2, r * 2, 90, 90 - sweep, true, false);
            }

            canvas.StrokeDashPattern = null;
        }

        if (ShowCenter && Rings.Count > 0)
        {
            var critical = Rings.Where(ring => !double.IsNaN(ring.Percent)).OrderByDescending(ring => ring.Percent).FirstOrDefault();
            var text = critical is null ? "—" : Fmt.Percent(critical.Percent);
            canvas.Font = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("NumSemiBold"));
            canvas.FontSize = (float)Ui.D("TypeMetricMdSize");
            canvas.FontColor = DesignTokens.Color(OnHero ? "ColorHeroText" : "ColorTextPrimary");
            SafeText.Draw(canvas, text, rect.Left, center.Y - (float)Ui.D("TypeMetricMdSize"), rect.Width, (float)Ui.D("TypeMetricMdSize") * 1.3f, HorizontalAlignment.Center, VerticalAlignment.Center);
            canvas.Font = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("InterRegular"));
            canvas.FontSize = (float)Ui.D("TypeCaptionSize");
            canvas.FontColor = DesignTokens.Color(OnHero ? "ColorHeroTextSecondary" : "ColorTextTertiary");
            SafeText.Draw(canvas, critical?.Name ?? "", rect.Left, center.Y + (float)Ui.D("Space100"), rect.Width, (float)Ui.D("TypeCaptionSize") * 1.4f, HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    /// <summary>M11: arcs grow from 12 o'clock with a spring.</summary>
    private void Animate()
    {
        SemanticProperties.SetDescription(this, "Limity: " + string.Join("; ", Rings.Select(ring =>
            double.IsNaN(ring.Percent) ? $"{ring.Name} niedostępny" : $"{ring.Name} {Fmt.PercentSpoken(ring.Percent)}{(string.IsNullOrEmpty(ring.ResetText) ? "" : ", " + ring.ResetText)}")) + ".");
        this.AbortAnimation("rings");
        if (Window is null || MotionTokens.Slower == 0)
        {
            _progress = 1;
            Invalidate();
            return;
        }

        new Animation(t => { _progress = t; Invalidate(); }, 0, 1, MotionTokens.AllowsTransforms ? OnionEasing.Spring : OnionEasing.Standard)
            .Commit(this, "rings", length: MotionTokens.Slower);
    }
}
