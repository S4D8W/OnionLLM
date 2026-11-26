using Microsoft.Maui.Controls.Shapes;
using OnionLLM.Design;

namespace OnionLLM.Controls;

/// <summary>
/// Helpers for code-built components: every visual value comes from a token or a style key (DESIGN.md §3.1, §10.1).
/// Static tokens are read once; theme- and density-dependent ones are attached with <c>SetDynamicResource</c>.
/// </summary>
internal static class Ui
{
    public static T Res<T>(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed ? typed : default!;

    public static double D(string key) => DesignTokens.Double(key);

    public static T Styled<T>(this T view, string styleKey) where T : VisualElement
    {
        view.Style = Res<Style>(styleKey);
        return view;
    }

    public static T Dyn<T>(this T element, BindableProperty property, string key) where T : Element
    {
        element.SetDynamicResource(property, key);
        return element;
    }

    public static Label Text(string styleKey, string? text = null) =>
        new Label { Text = text }.Styled(styleKey);

    public static Label Icon(string glyph, string styleKey = "Text.Icon.Sm", string? colorKey = null)
    {
        var label = new Label { Text = glyph }.Styled(styleKey);
        if (colorKey is not null)
        {
            label.Dyn(Label.TextColorProperty, colorKey);
        }

        return label;
    }

    public static RoundRectangle Corner(string radiusKey) => new() { CornerRadius = Res<CornerRadius>(radiusKey + "Corner") };

    /// <summary>Rounded container without stroke.</summary>
    public static Border Box(View? content, string backgroundKey, string radiusKey, string? paddingKey = null)
    {
        var border = new Border
        {
            Content = content,
            StrokeThickness = 0,
            StrokeShape = Corner(radiusKey)
        };
        border.Dyn(VisualElement.BackgroundColorProperty, backgroundKey);
        if (paddingKey is not null)
        {
            border.Dyn(Border.PaddingProperty, paddingKey);
        }

        return border;
    }

    /// <summary>Status dot (kropka statusu) — always next to a word, never alone (§1.5).</summary>
    public static Border Dot(string colorKey, string sizeKey = "SizeDotSm")
    {
        var size = D(sizeKey);
        return new Border
        {
            WidthRequest = size,
            HeightRequest = size,
            StrokeThickness = 0,
            StrokeShape = Corner("RadiusFull"),
            VerticalOptions = LayoutOptions.Center
        }.Dyn(VisualElement.BackgroundColorProperty, colorKey);
    }

    public static Thickness Thick(string key) => Res<Thickness>(key);

    /// <summary>
    /// Splits a formatted value („12,34 $”, „1,24 M”) at the last non-breaking space into number and unit so the unit can
    /// be rendered at 60% size in <c>text.secondary</c> (§2.2.3).
    /// </summary>
    public static (string Number, string Unit) SplitUnit(string formatted)
    {
        var index = formatted.LastIndexOf(' ');
        if (index <= 0 || index == formatted.Length - 1)
        {
            return (formatted, string.Empty);
        }

        var unit = formatted[(index + 1)..];
        // Only currency/suffix units are split; „5 min” or „2 h 14 min” stay whole.
        return unit is "$" or "zł" or "€" or "k" or "M" or "B" or "%" ? (formatted[..index], unit) : (formatted, string.Empty);
    }

    /// <summary>Number with a smaller unit span; <paramref name="secondaryKey"/> colours the unit.</summary>
    public static FormattedString MetricText(string formatted, double fontSize, string secondaryKey = "ColorTextSecondary", string? marker = null)
    {
        var (number, unit) = SplitUnit(formatted);
        var text = new FormattedString();
        text.Spans.Add(new Span { Text = number });
        if (unit.Length > 0)
        {
            var span = new Span { Text = " " + unit, FontSize = fontSize * D("TypeUnitScale") };
            span.SetDynamicResource(Span.TextColorProperty, secondaryKey);
            text.Spans.Add(span);
        }

        if (!string.IsNullOrEmpty(marker))
        {
            var span = new Span { Text = marker, FontSize = fontSize * D("TypeUnitScale"), FontFamily = "InterSemiBold" };
            span.SetDynamicResource(Span.TextColorProperty, marker.Contains('*') ? "ColorStatusWarning" : "ColorStatusInfo");
            text.Spans.Add(span);
        }

        return text;
    }

    /// <summary>Copies text and announces it (§5.12).</summary>
    public static async Task CopyAsync(string text)
    {
        await Clipboard.Default.SetTextAsync(text);
        SemanticScreenReader.Announce("Skopiowano");
    }
}
