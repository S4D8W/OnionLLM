using OnionLLM.Core.Reports;
using OnionLLM.Design;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls;

public enum StatusKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
    Live
}

/// <summary>Status pill (DESIGN.md §4.4.5): dot 6 + caption SemiBold, always with a word; <c>Live</c> pulses (M10).</summary>
public sealed class StatusBadge : Border
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(StatusBadge), string.Empty, propertyChanged: (b, _, _) => ((StatusBadge)b).Update());

    public static readonly BindableProperty KindProperty = BindableProperty.Create(
        nameof(Kind), typeof(StatusKind), typeof(StatusBadge), StatusKind.Neutral, propertyChanged: (b, _, _) => ((StatusBadge)b).Update());

    private readonly Label _label = Ui.Text("Text.Caption");
    private readonly Border _dot = Ui.Dot("ColorTextSecondary");
    private readonly Border _pulse = Ui.Dot("ColorStatusSuccess");
    private bool _pulsing;

    public StatusBadge()
    {
        StrokeThickness = 0;
        StrokeShape = Ui.Corner("RadiusFull");
        Padding = Ui.Thick("PaddingBadge");
        HeightRequest = Ui.D("SizeBadge");
        HorizontalOptions = LayoutOptions.Start;
        VerticalOptions = LayoutOptions.Center;
        _label.FontFamily = "InterSemiBold";
        _label.VerticalOptions = LayoutOptions.Center;
        _pulse.Opacity = 0;
        var dot = new Grid { VerticalOptions = LayoutOptions.Center, Children = { _pulse, _dot } };
        Content = new HorizontalStackLayout { Spacing = Ui.D("Space150"), Children = { dot, _label } };
        Loaded += (_, _) => StartPulse();
        Update();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public static (string Foreground, string Background) Keys(StatusKind kind) => kind switch
    {
        StatusKind.Success or StatusKind.Live => ("ColorStatusSuccess", "ColorStatusSuccessBg"),
        StatusKind.Warning => ("ColorStatusWarning", "ColorStatusWarningBg"),
        StatusKind.Danger => ("ColorStatusDanger", "ColorStatusDangerBg"),
        StatusKind.Info => ("ColorStatusInfo", "ColorStatusInfoBg"),
        _ => ("ColorTextSecondary", "ColorSurface2")
    };

    private void Update()
    {
        var (foreground, background) = Keys(Kind);
        this.Dyn(BackgroundColorProperty, background);
        _label.Dyn(Label.TextColorProperty, foreground);
        _dot.Dyn(BackgroundColorProperty, foreground);
        _pulse.Dyn(BackgroundColorProperty, foreground);
        _label.Text = Text;
        SemanticProperties.SetDescription(this, Text);
        StartPulse();
    }

    /// <summary>M10: ring around the dot scales 1 → 2.2 and fades 0.5 → 0 in a loop (off in reduced motion).</summary>
    private async void StartPulse()
    {
        if (_pulsing || Kind != StatusKind.Live || !MotionTokens.AllowsLoops || Window is null)
        {
            return;
        }

        _pulsing = true;
        try
        {
            while (Kind == StatusKind.Live && MotionTokens.AllowsLoops && Window is not null)
            {
                _pulse.Scale = 1;
                _pulse.Opacity = Ui.D("OpacityPulse");
                await Task.WhenAll(
                    _pulse.ScaleToAsync(Ui.D("ScalePulse"), MotionTokens.Ambient, OnionEasing.Standard),
                    _pulse.FadeToAsync(0, MotionTokens.Ambient, OnionEasing.Standard));
            }
        }
        finally
        {
            _pulse.Opacity = 0;
            _pulsing = false;
        }
    }
}

public enum BadgeSize
{
    Xs,
    Sm,
    Md,
    Lg
}

public enum SourceState
{
    Normal,
    Disabled,
    NotDetected
}

/// <summary>Source identity (§4.5.3): square with the theme-tuned source avatar colour and initial.</summary>
public sealed class SourceBadge : Border
{
    public static readonly BindableProperty SourceIdProperty = BindableProperty.Create(
        nameof(SourceId), typeof(string), typeof(SourceBadge), string.Empty, propertyChanged: (b, _, _) => ((SourceBadge)b).Update());

    public static readonly BindableProperty InitialProperty = BindableProperty.Create(
        nameof(Initial), typeof(string), typeof(SourceBadge), string.Empty, propertyChanged: (b, _, _) => ((SourceBadge)b).Update());

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(BadgeSize), typeof(SourceBadge), BadgeSize.Sm, propertyChanged: (b, _, _) => ((SourceBadge)b).Update());

    public static readonly BindableProperty StateProperty = BindableProperty.Create(
        nameof(State), typeof(SourceState), typeof(SourceBadge), SourceState.Normal, propertyChanged: (b, _, _) => ((SourceBadge)b).Update());

    private readonly Label _initial = new()
    {
        FontFamily = "InterSemiBold",
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center
    };

    public SourceBadge()
    {
        VerticalOptions = LayoutOptions.Center;
        HorizontalOptions = LayoutOptions.Start;
        Content = _initial;
        DensityService.DensityChanged += (_, _) => Update();
        Update();
    }

    public string SourceId { get => (string)GetValue(SourceIdProperty); set => SetValue(SourceIdProperty, value); }

    public string Initial { get => (string)GetValue(InitialProperty); set => SetValue(InitialProperty, value); }

    public BadgeSize Size { get => (BadgeSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public SourceState State { get => (SourceState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    private void Update()
    {
        var side = Size switch
        {
            BadgeSize.Xs => Ui.D("SizeAvatarXs"),
            BadgeSize.Md => Ui.D("SizeAvatarMd"),
            BadgeSize.Lg => Ui.D("SizeAvatarLg"),
            _ => Ui.D("SizeAvatarSm")
        };
        WidthRequest = HeightRequest = side;
        StrokeShape = Ui.Corner(Size is BadgeSize.Xs or BadgeSize.Sm ? "RadiusSm" : "RadiusMd");
        _initial.FontSize = Math.Max(Ui.D("TypeOverlineSize"), side * Ui.D("TypeInitialScale"));
        _initial.Text = Size == BadgeSize.Xs ? Initial[..Math.Min(1, Initial.Length)] : Initial;

        var key = DesignTokens.SourceKey(SourceId);
        switch (State)
        {
            case SourceState.Disabled:
                StrokeThickness = 0;
                this.Dyn(BackgroundColorProperty, "ColorSurface3");
                _initial.Dyn(Label.TextColorProperty, "ColorTextTertiary");
                break;
            case SourceState.NotDetected:
                StrokeThickness = Ui.D("StrokeDefault");
                StrokeDashArray = [Ui.D("StrokeDash"), Ui.D("StrokeDash")];
                this.Dyn(StrokeProperty, "ColorBorderStrongBrush");
                this.Dyn(BackgroundColorProperty, "ColorTransparent");
                _initial.Dyn(Label.TextColorProperty, "ColorTextTertiary");
                break;
            default:
                StrokeThickness = 0;
                this.Dyn(BackgroundColorProperty, key + "Avatar");
                _initial.Dyn(Label.TextColorProperty, key + "OnAvatar");
                break;
        }
    }
}

/// <summary>Model pill (§4.5.4): mono, shortened by <c>Fmt.Model</c>, full name in the tooltip, optional speed tier and marker.</summary>
public sealed class ModelTag : Border
{
    public static readonly BindableProperty ModelProperty = BindableProperty.Create(
        nameof(Model), typeof(string), typeof(ModelTag), string.Empty, propertyChanged: (b, _, _) => ((ModelTag)b).Update());

    public static readonly BindableProperty MarkerProperty = BindableProperty.Create(
        nameof(Marker), typeof(string), typeof(ModelTag), string.Empty, propertyChanged: (b, _, _) => ((ModelTag)b).Update());

    private readonly Label _label = Ui.Text("Text.MonoSm");

    public ModelTag()
    {
        StrokeShape = Ui.Corner("RadiusXs");
        StrokeThickness = Ui.D("StrokeDefault");
        HeightRequest = Ui.D("SizeModelTag");
        Padding = Ui.Thick("PaddingModelTag");
        HorizontalOptions = LayoutOptions.Start;
        VerticalOptions = LayoutOptions.Center;
        this.Dyn(BackgroundColorProperty, "ColorSurface2").Dyn(StrokeProperty, "ColorBorderSubtleBrush");
        _label.VerticalOptions = LayoutOptions.Center;
        _label.LineBreakMode = LineBreakMode.TailTruncation;
        Content = _label;
    }

    /// <summary>Full model name; a trailing „-fast”/„-flex” is shown as a tertiary suffix.</summary>
    public string Model { get => (string)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    public string Marker { get => (string)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    private void Update()
    {
        var name = Fmt.Model(Model);
        var suffix = string.Empty;
        foreach (var tier in new[] { "-fast", "-flex" })
        {
            if (name.EndsWith(tier, StringComparison.OrdinalIgnoreCase))
            {
                suffix = " " + tier[1..];
                name = name[..^tier.Length];
            }
        }

        var text = new FormattedString();
        text.Spans.Add(new Span { Text = name });
        if (suffix.Length > 0)
        {
            text.Spans.Add(new Span { Text = suffix }.Dyn(Span.TextColorProperty, "ColorTextTertiary"));
        }

        if (!string.IsNullOrEmpty(Marker))
        {
            text.Spans.Add(new Span { Text = Marker, FontFamily = "InterSemiBold" }
                .Dyn(Span.TextColorProperty, Marker.Contains('*') ? "ColorStatusWarning" : "ColorStatusInfo"));
        }

        _label.FormattedText = text;
        ToolTipProperties.SetText(this, Model);
        SemanticProperties.SetDescription(this, "Model " + Model);
    }
}

/// <summary>Keyboard key (§4.7.8).</summary>
public sealed class Kbd : Border
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(Kbd), string.Empty, propertyChanged: (b, _, v) => ((Kbd)b)._label.Text = (string)v);

    private readonly Label _label = Ui.Text("Text.Kbd");

    public Kbd()
    {
        StrokeShape = Ui.Corner("RadiusXs");
        StrokeThickness = Ui.D("StrokeDefault");
        Padding = Ui.Thick("PaddingKbd");
        this.Dyn(BackgroundColorProperty, "ColorSurface2").Dyn(StrokeProperty, "ColorBorderDefaultBrush");
        Content = _label;
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Burn rate pill (§4.4.4): flame · cost/h · tokens/min · level word, coloured by level.</summary>
public sealed class BurnRateBadge : Border
{
    public static readonly BindableProperty CostPerHourProperty = BindableProperty.Create(
        nameof(CostPerHour), typeof(string), typeof(BurnRateBadge), string.Empty, propertyChanged: (b, _, _) => ((BurnRateBadge)b).Update());

    public static readonly BindableProperty TokensPerMinuteProperty = BindableProperty.Create(
        nameof(TokensPerMinute), typeof(string), typeof(BurnRateBadge), string.Empty, propertyChanged: (b, _, _) => ((BurnRateBadge)b).Update());

    public static readonly BindableProperty LevelProperty = BindableProperty.Create(
        nameof(Level), typeof(BurnRateLevel), typeof(BurnRateBadge), BurnRateLevel.Normal, propertyChanged: (b, _, _) => ((BurnRateBadge)b).Update());

    private readonly Label _icon = Ui.Icon(Icons.Flame, "Text.Icon.Xs");
    private readonly Label _text = Ui.Text("Text.Caption");

    public BurnRateBadge()
    {
        StrokeThickness = 0;
        StrokeShape = Ui.Corner("RadiusFull");
        Padding = Ui.Thick("PaddingBadge");
        HeightRequest = Ui.D("SizeBadge");
        HorizontalOptions = LayoutOptions.Start;
        _text.FontFamily = "NumMedium";
        _text.VerticalOptions = LayoutOptions.Center;
        Content = new HorizontalStackLayout { Spacing = Ui.D("Space100"), Children = { _icon, _text } };
        ToolTipProperties.SetText(this, "Tempo spalania (burn rate): średni koszt na godzinę w aktywnym bloku.");
        Update();
    }

    public string CostPerHour { get => (string)GetValue(CostPerHourProperty); set => SetValue(CostPerHourProperty, value); }

    public string TokensPerMinute { get => (string)GetValue(TokensPerMinuteProperty); set => SetValue(TokensPerMinuteProperty, value); }

    public BurnRateLevel Level { get => (BurnRateLevel)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private void Update()
    {
        var (background, foreground) = Level switch
        {
            BurnRateLevel.High => ("ColorStatusDangerBg", "ColorBurnHigh"),
            BurnRateLevel.Moderate => ("ColorStatusWarningBg", "ColorBurnModerate"),
            _ => ("ColorStatusSuccessBg", "ColorBurnNormal")
        };
        this.Dyn(BackgroundColorProperty, background);
        _icon.Dyn(Label.TextColorProperty, foreground);
        _text.Dyn(Label.TextColorProperty, foreground);
        _text.Text = $"{CostPerHour} · {TokensPerMinute} · {Fmt.BurnLevel(Level)}";
        SemanticProperties.SetDescription(this, $"Tempo spalania {Fmt.BurnLevel(Level)}: {CostPerHour}, {TokensPerMinute}");
    }
}

/// <summary>Cost honesty marker (§4.5.5): „*” (no price, warning) or „~” (estimated, info) with an explanatory tooltip.</summary>
public sealed class PricingMarker : Label
{
    public static readonly BindableProperty MarkerProperty = BindableProperty.Create(
        nameof(Marker), typeof(string), typeof(PricingMarker), string.Empty, propertyChanged: (b, _, _) => ((PricingMarker)b).Update());

    public static readonly BindableProperty ModelsProperty = BindableProperty.Create(
        nameof(Models), typeof(string), typeof(PricingMarker), string.Empty, propertyChanged: (b, _, _) => ((PricingMarker)b).Update());

    public PricingMarker()
    {
        this.Styled("Text.Caption");
        FontFamily = "InterSemiBold";
        VerticalOptions = LayoutOptions.Start;
        Update();
    }

    public string Marker { get => (string)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    /// <summary>Comma-separated model names for the tooltip.</summary>
    public string Models { get => (string)GetValue(ModelsProperty); set => SetValue(ModelsProperty, value); }

    private void Update()
    {
        Text = Marker;
        IsVisible = !string.IsNullOrEmpty(Marker);
        this.Dyn(TextColorProperty, Marker.Contains('*') ? "ColorStatusWarning" : "ColorStatusInfo");
        var models = string.IsNullOrEmpty(Models) ? "" : $" modeli: {Models}";
        var tip = Marker.Contains('*')
            ? $"Koszt niepełny: brak ceny dla{models}. Dodaj cenę w Ustawienia → Cennik."
            : $"Koszt szacowany: ceny{models} pochodzą z przybliżenia.";
        ToolTipProperties.SetText(this, tip);
        SemanticProperties.SetDescription(this, tip);
    }
}
