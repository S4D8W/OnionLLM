using OnionLLM.Design;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls.Charts;

public enum ChartUnit
{
    Cost,
    Tokens,
    Percent,
    Count
}

public enum ChartState
{
    Ready,
    Loading,
    Empty,
    Error
}

/// <summary>One series (DESIGN.md §4.8.1 „Model danych”). Colour is a token key, never a hex value.</summary>
public sealed class ChartSeries
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string ColorKey { get; init; }

    /// <summary>One value per category (NaN = no value).</summary>
    public required IReadOnlyList<double> Points { get; init; }

    public bool IsVisible { get; set; } = true;

    public double Total => Points.Where(point => !double.IsNaN(point)).Sum();
}

/// <summary>A threshold / budget line (§4.8.2 C).</summary>
public sealed record ChartThreshold(double Value, string Label, string ColorKey);

/// <summary>Chart input: categories (x axis), series, unit and decorations. View models expose this instead of drawables.</summary>
public sealed class ChartData
{
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Long category names for tooltips („wt 24 lis”); defaults to <see cref="Categories"/>.</summary>
    public IReadOnlyList<string>? CategoryDetails { get; init; }

    public IReadOnlyList<ChartSeries> Series { get; init; } = [];

    public ChartUnit Unit { get; init; } = ChartUnit.Cost;

    /// <summary>The last category is the current, unfinished period („w toku”).</summary>
    public bool LastInProgress { get; init; }

    public bool ShowAverage { get; init; }

    public IReadOnlyList<ChartThreshold> Thresholds { get; init; } = [];

    /// <summary>Line charts: index from which values are a projection (dashed).</summary>
    public int? ProjectionFrom { get; init; }

    /// <summary>Spoken summary prefix, e.g. „Wykres słupkowy kosztu dziennego, 1–26 listopada.”</summary>
    public string Description { get; init; } = string.Empty;

    public static ChartData Empty { get; } = new();

    public bool IsEmpty => Categories.Count == 0 || Series.All(series => series.Points.All(point => double.IsNaN(point) || point == 0));

    public string Format(double value) => Unit switch
    {
        ChartUnit.Tokens => Fmt.Tokens((long)Math.Round(value)),
        ChartUnit.Percent => Fmt.Percent(value),
        ChartUnit.Count => Fmt.Number((long)Math.Round(value)),
        _ => Fmt.Cost(value)
    };

    public string Detail(int index) => CategoryDetails is { } details && index < details.Count ? details[index] : Categories[index];
}

/// <summary>
/// Base of all charts (§4.8.1): <c>GraphicsView</c> + drawable with theme-aware tokens, nice Y ticks, thinned X labels,
/// hover crosshair with an in-canvas tooltip, entry (M4) and morph (M5) animation, Loading/Empty/Error states and a
/// generated <c>SemanticProperties.Description</c>.
/// </summary>
public abstract class ChartView : GraphicsView, IDrawable, OnionLLM.Navigation.IKeyboardTarget
{
    public static readonly BindableProperty DataProperty = BindableProperty.Create(
        nameof(Data), typeof(ChartData), typeof(ChartView), ChartData.Empty, propertyChanged: (b, o, n) => ((ChartView)b).OnDataChanged((ChartData)o, (ChartData)n));

    public static readonly BindableProperty StateProperty = BindableProperty.Create(
        nameof(State), typeof(ChartState), typeof(ChartView), ChartState.Ready, propertyChanged: (b, _, _) => ((ChartView)b).Invalidate());

    private ChartData? _previous;
    private bool _entered;

    protected ChartView()
    {
        Drawable = this;
        StartHoverInteraction += (_, e) => Hover(e.Touches.FirstOrDefault());
        MoveHoverInteraction += (_, e) => Hover(e.Touches.FirstOrDefault());
        EndHoverInteraction += (_, _) => { HoverIndex = -1; Invalidate(); };
        EndInteraction += (_, e) =>
        {
            OnionLLM.Navigation.KeyboardRouter.Target = this;
            var index = IndexAt(e.Touches.FirstOrDefault());
            if (index >= 0)
            {
                CategoryActivated?.Invoke(this, index);
            }
        };

        // §4.8.1 keyboard: Tab focuses the chart, ←/→ move the crosshair (Home/End jump), Enter = click.
        Focused += (_, _) =>
        {
            OnionLLM.Navigation.KeyboardRouter.Target = this;
            if (HoverIndex < 0 && Data.Categories.Count > 0)
            {
                MoveCrosshair(Data.Categories.Count - 1);
            }
        };
        Unfocused += (_, _) =>
        {
            HoverIndex = -1;
            Invalidate();
        };
        ThemeService.ThemeChanged += (_, _) => Invalidate();
        DensityService.DensityChanged += (_, _) => Invalidate();
    }

    /// <summary>Click on a category (e.g. opens the period drawer).</summary>
    public event EventHandler<int>? CategoryActivated;

    public ChartData Data { get => (ChartData)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public ChartState State { get => (ChartState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>Animation progress 0–1 (M4 entry / M5 morph).</summary>
    protected double Progress { get; private set; } = 1;

    protected int HoverIndex { get; private set; } = -1;

    /// <summary>Font used for axis labels and tooltips (ICanvas has no getters for font state).</summary>
    protected Microsoft.Maui.Graphics.Font AxisFont { get; private set; }

    protected float AxisSize { get; private set; }

    protected float TextWidth(ICanvas canvas, string text) => canvas.GetStringSize(text, AxisFont, AxisSize).Width;

    protected RectF Plot { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        canvas.Antialias = true;
        AxisFont = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("NumMedium"));
        AxisSize = (float)Ui.D("TypeCaptionSize");
        canvas.FontSize = AxisSize;
        canvas.Font = AxisFont;

        // Layout passes can draw at zero size; Win2D rejects text boxes with non-positive dimensions.
        if (rect.Width < F("Space1200") * 2 || rect.Height < F("Space1200"))
        {
            return;
        }
        switch (State)
        {
            case ChartState.Loading:
                DrawSkeleton(canvas, rect);
                return;
            case ChartState.Error:
                DrawMessage(canvas, rect, "Nie udało się wczytać danych wykresu");
                return;
        }

        if (Data.IsEmpty)
        {
            DrawMessage(canvas, rect, "Brak danych w wybranym okresie");
            return;
        }

        DrawChart(canvas, rect);
        if (HoverIndex >= 0)
        {
            DrawTooltip(canvas, rect, HoverIndex);
        }
    }

    protected abstract void DrawChart(ICanvas canvas, RectF rect);

    /// <summary>Category index under a point; -1 when outside.</summary>
    protected virtual int IndexAt(PointF point)
    {
        var count = Data.Categories.Count;
        if (count == 0 || Plot.Width <= 0 || point.X < Plot.Left || point.X > Plot.Right)
        {
            return -1;
        }

        return Math.Clamp((int)((point.X - Plot.Left) / (Plot.Width / count)), 0, count - 1);
    }

    /// <summary>Value of a series point during the morph animation (M5).</summary>
    protected double Animated(ChartSeries series, int index)
    {
        var target = index < series.Points.Count ? series.Points[index] : 0;
        target = double.IsNaN(target) ? 0 : target;
        if (Progress >= 1)
        {
            return target;
        }

        var before = _previous?.Series.FirstOrDefault(previous => previous.Id == series.Id) is { } old && index < old.Points.Count && !double.IsNaN(old.Points[index])
            ? old.Points[index]
            : 0;
        return before + (target - before) * Progress;
    }

    protected static Color C(string key) => DesignTokens.Color(key);

    protected static float F(string key) => (float)Ui.D(key);

    /// <summary>Axis + grid; returns the plot rectangle (left margin fits the widest Y label).</summary>
    protected RectF DrawAxes(ICanvas canvas, RectF rect, double max, bool showY = true)
    {
        var ticks = NiceTicks(max);
        var labelWidth = showY ? ticks.Max(tick => TextWidth(canvas, Data.Format(tick))) + F("Space200") : 0;
        var bottom = F("Space600") - F("Space100");
        var plot = new RectF(rect.Left + labelWidth, rect.Top + F("Space200"), rect.Width - labelWidth - F("Space200"), rect.Height - bottom - F("Space200"));
        var top = ticks[^1];

        canvas.StrokeSize = F("StrokeDefault");
        canvas.FontColor = C("ColorDataAxis");
        foreach (var tick in ticks)
        {
            var y = plot.Bottom - (float)(tick / top) * plot.Height;
            canvas.StrokeColor = tick == 0 ? C("ColorBorderDefault") : C("ColorDataGrid");
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            if (showY)
            {
                SafeText.Draw(canvas, Data.Format(tick), rect.Left, y - F("Space200"), labelWidth - F("Space200"), F("Space400"), HorizontalAlignment.Right, VerticalAlignment.Center);
            }
        }

        // X labels thinned so they never overlap (min gap 8).
        var count = Data.Categories.Count;
        var step = plot.Width / Math.Max(1, count);
        var widest = Data.Categories.Max(label => TextWidth(canvas, label)) + F("Space200");
        var every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, step)));
        for (var i = 0; i < count; i += every)
        {
            SafeText.Draw(canvas, Data.Categories[i], plot.Left + i * step, plot.Bottom + F("Space100"), step * every, F("Space400"), HorizontalAlignment.Left, VerticalAlignment.Top);
        }

        Plot = plot;
        MaxValue = top;
        return plot;
    }

    protected double MaxValue { get; private set; } = 1;

    protected float ValueY(double value) => Plot.Bottom - (float)(value / MaxValue) * Plot.Height;

    /// <summary>4–5 „nice” ticks: 1 / 2 / 2.5 / 5 × 10ⁿ (§4.8.1).</summary>
    public static double[] NiceTicks(double max)
    {
        if (max <= 0 || double.IsNaN(max))
        {
            return [0, 1];
        }

        var raw = max / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(factor => factor * magnitude).First(candidate => candidate >= raw);
        var ticks = new List<double>();
        for (var value = 0.0; value < max + step * 0.999; value += step)
        {
            ticks.Add(value);
        }

        return ticks.ToArray();
    }

    public bool HandleKey(OnionLLM.Navigation.KeyboardAction action)
    {
        var count = Data.Categories.Count;
        if (count == 0 || State != ChartState.Ready)
        {
            return false;
        }

        var current = HoverIndex < 0 ? count - 1 : HoverIndex;
        switch (action)
        {
            case OnionLLM.Navigation.KeyboardAction.Expand:
                MoveCrosshair(Math.Min(count - 1, HoverIndex < 0 ? current : current + 1));
                return true;
            case OnionLLM.Navigation.KeyboardAction.Collapse:
                MoveCrosshair(Math.Max(0, HoverIndex < 0 ? current : current - 1));
                return true;
            case OnionLLM.Navigation.KeyboardAction.Home:
                MoveCrosshair(0);
                return true;
            case OnionLLM.Navigation.KeyboardAction.End:
                MoveCrosshair(count - 1);
                return true;
            case OnionLLM.Navigation.KeyboardAction.Activate when HoverIndex >= 0:
                CategoryActivated?.Invoke(this, HoverIndex);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Keyboard crosshair: highlight + tooltip, and the category read out (series values, §9.3).</summary>
    private void MoveCrosshair(int index)
    {
        HoverIndex = index;
        Invalidate();
        var data = Data;
        var values = data.Series.Where(series => series.IsVisible && index < series.Points.Count && !double.IsNaN(series.Points[index]))
            .Select(series => $"{series.Label}: {data.Format(series.Points[index])}");
        SemanticScreenReader.Announce($"{data.Detail(index)}. {string.Join(", ", values)}");
    }

    private void Hover(PointF point)
    {
        var index = IndexAt(point);
        if (index != HoverIndex)
        {
            HoverIndex = index;
            Invalidate();
        }
    }

    /// <summary>Tooltip (§4.8.1): header with the category, series sorted by value with dots, total.</summary>
    protected virtual void DrawTooltip(ICanvas canvas, RectF rect, int index)
    {
        var step = Plot.Width / Math.Max(1, Data.Categories.Count);
        var x = Plot.Left + step * (index + 0.5f);
        canvas.StrokeColor = C("ColorBorderStrong");
        canvas.StrokeSize = F("StrokeDefault");
        canvas.DrawLine(x, Plot.Top, x, Plot.Bottom);

        var lines = Data.Series.Where(series => series.IsVisible)
            .Select(series => (series, value: index < series.Points.Count ? series.Points[index] : double.NaN))
            .Where(item => !double.IsNaN(item.value))
            .OrderByDescending(item => item.value)
            .ToList();
        var header = Data.Detail(index) + (Data.LastInProgress && index == Data.Categories.Count - 1 ? " · w toku" : "");
        var rows = lines.Select(item => (Text: $"{item.series.Label}  {Data.Format(item.value)}", item.series.ColorKey)).ToList();
        if (lines.Count > 1)
        {
            rows.Add(($"Razem  {Data.Format(lines.Sum(item => item.value))}", "ColorTextPrimary"));
        }

        var lineHeight = F("TypeCaptionLineHeight") * F("TypeCaptionSize");
        var padding = F("Space200");
        var width = Math.Min(F("SizeToastWidth") * 0.75f, new[] { header }.Concat(rows.Select(row => row.Text))
            .Max(text => TextWidth(canvas, text)) + padding * 3 + F("SizeDotSm"));
        var height = lineHeight * (rows.Count + 1) + padding * 2;
        var left = x + padding + width > rect.Right ? x - padding - width : x + padding;
        var box = new RectF(left, Plot.Top, width, height);

        canvas.FillColor = C("ColorSurfaceOverlay");
        canvas.FillRoundedRectangle(box, F("RadiusSm"));
        canvas.StrokeColor = C("ColorBorderDefault");
        canvas.DrawRoundedRectangle(box, F("RadiusSm"));
        canvas.FontColor = C("ColorTextSecondary");
        SafeText.Draw(canvas, header, box.Left + padding, box.Top + padding, box.Width - padding * 2, lineHeight, HorizontalAlignment.Left, VerticalAlignment.Center);
        for (var i = 0; i < rows.Count; i++)
        {
            var y = box.Top + padding + lineHeight * (i + 1);
            canvas.FillColor = C(rows[i].ColorKey);
            canvas.FillCircle(box.Left + padding + F("SizeDotSm") / 2, y + lineHeight / 2, F("SizeDotSm") / 2);
            canvas.FontColor = C("ColorTextPrimary");
            SafeText.Draw(canvas, rows[i].Text, box.Left + padding * 2 + F("SizeDotSm"), y, box.Width - padding * 3, lineHeight, HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    private void DrawSkeleton(ICanvas canvas, RectF rect)
    {
        canvas.FillColor = C("ColorSurface2");
        var bars = 6;
        var step = rect.Width / bars;
        for (var i = 0; i < bars; i++)
        {
            var height = rect.Height * (0.3f + 0.1f * ((i * 7) % 5));
            canvas.FillRoundedRectangle(rect.Left + i * step + step * 0.2f, rect.Bottom - height, step * 0.6f, height, F("RadiusXs"));
        }
    }

    private void DrawMessage(ICanvas canvas, RectF rect, string text)
    {
        canvas.StrokeColor = C("ColorBorderDefault");
        canvas.StrokeSize = F("StrokeDefault");
        canvas.DrawLine(rect.Left, rect.Bottom - F("StrokeDefault"), rect.Right, rect.Bottom - F("StrokeDefault"));
        canvas.Font = new Microsoft.Maui.Graphics.Font(DesignTokens.Font("InterRegular"));
        canvas.FontColor = C("ColorTextTertiary");
        SafeText.Draw(canvas, text, rect, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private void OnDataChanged(ChartData old, ChartData data)
    {
        _previous = _entered ? old : null;
        SemanticProperties.SetDescription(this, Describe(data));
        if (Window is null || data.IsEmpty)
        {
            Progress = 1;
            Invalidate();
            return;
        }

        // M4 on the first data, M5 morph afterwards; reduced motion → short fade-like progress, off → instant.
        var length = _entered ? MotionTokens.Base : MotionTokens.Slower;
        _entered = true;
        this.AbortAnimation("chart");
        if (length == 0)
        {
            Progress = 1;
            Invalidate();
            return;
        }

        new Animation(t => { Progress = t; Invalidate(); }, 0, 1, _previous is null ? OnionEasing.Enter : OnionEasing.Standard)
            .Commit(this, "chart", length: length);
    }

    /// <summary>„… Maksimum 24 lis: 18,20 $. Suma: 212,40 $.”</summary>
    protected virtual string Describe(ChartData data)
    {
        if (data.IsEmpty)
        {
            return data.Description + " Brak danych w wybranym okresie.";
        }

        var totals = Enumerable.Range(0, data.Categories.Count)
            .Select(i => data.Series.Where(series => series.IsVisible).Sum(series => i < series.Points.Count && !double.IsNaN(series.Points[i]) ? series.Points[i] : 0))
            .ToList();
        var max = totals.IndexOf(totals.Max());
        return $"{data.Description} Maksimum {data.Detail(max)}: {data.Format(totals[max])}. Suma: {data.Format(totals.Sum())}.".Trim();
    }
}

/// <summary>Text drawing that skips empty or degenerate boxes (Win2D throws on non-positive sizes).</summary>
internal static class SafeText
{
    public static void Draw(ICanvas canvas, string text, float x, float y, float width, float height, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        if (string.IsNullOrEmpty(text) || width <= 0 || height <= 0 || float.IsNaN(x) || float.IsNaN(y))
        {
            return;
        }

        canvas.DrawString(text, x, y, width, height, horizontal, vertical);
    }

    public static void Draw(ICanvas canvas, string text, RectF rect, HorizontalAlignment horizontal, VerticalAlignment vertical) =>
        Draw(canvas, text, rect.X, rect.Y, rect.Width, rect.Height, horizontal, vertical);
}
