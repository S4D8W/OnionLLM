using OnionLLM.Design;
using OnionLLM.Resources;

namespace OnionLLM.Controls;

/// <summary>
/// Light-dismiss popover layer (menus, Select, DateRangePicker — DESIGN.md §4.6.6, §4.2.3, §4.2.4). One popover at a time;
/// a click outside or Esc closes it and returns focus to the anchor.
/// </summary>
public static class PopoverService
{
    private static PopoverHost? _host;
    private static VisualElement? _anchor;

    public static bool IsOpen => _host?.IsVisible == true;

    internal static void Attach(PopoverHost host) => _host = host;

    /// <summary>Shows <paramref name="content"/> below <paramref name="anchor"/> (above it when there is no room).</summary>
    public static void Show(VisualElement anchor, View content, double? width = null)
    {
        if (_host is null)
        {
            return;
        }

        _anchor = anchor;
        _host.Open(anchor, content, width ?? Math.Max(anchor.Width, Ui.D("SizeSelectMinWidth")));
    }

    public static void Close()
    {
        if (_host?.IsVisible != true)
        {
            return;
        }

        _host.CloseNow();
        _anchor?.Focus();
        _anchor = null;
    }

    /// <summary>Position of <paramref name="view"/> in page coordinates (scroll offsets of ScrollView parents included).</summary>
    internal static Point PagePosition(VisualElement view)
    {
        double x = 0, y = 0;
        Element? element = view;
        while (element is VisualElement visual and not Page)
        {
            x += visual.X + visual.TranslationX;
            y += visual.Y + visual.TranslationY;
            if (visual.Parent is ScrollView scroll)
            {
                x -= scroll.ScrollX;
                y -= scroll.ScrollY;
            }

            element = visual.Parent;
        }

        return new Point(x, y);
    }
}

/// <summary>Overlay layer hosting the open popover (placed once in the window host).</summary>
public sealed class PopoverHost : Grid
{
    private readonly AbsoluteLayout _layer = new();
    private readonly Border _card;

    public PopoverHost()
    {
        IsVisible = false;
        this.Dyn(BackgroundColorProperty, "ColorTransparent");
        var dismiss = new TapGestureRecognizer();
        dismiss.Tapped += (_, _) => PopoverService.Close();
        GestureRecognizers.Add(dismiss);

        _card = new Border { Padding = Ui.Thick("PaddingMenu") }.Styled("Surface.Overlay");
        // Taps inside the card must not dismiss.
        _card.GestureRecognizers.Add(new TapGestureRecognizer());
        _layer.Children.Add(_card);
        Children.Add(_layer);
        PopoverService.Attach(this);
    }

    internal void Open(VisualElement anchor, View content, double width)
    {
        _card.Content = new ScrollView { Content = content, MaximumHeightRequest = Ui.D("SizePopoverMaxHeight") };
        var origin = PopoverService.PagePosition(anchor);
        var gap = Ui.D("Space100");
        var x = Math.Max(Ui.D("Space200"), Math.Min(origin.X, Width - width - Ui.D("Space200")));
        var below = origin.Y + anchor.Height + gap;
        var room = Height - below;
        var y = room < Ui.D("SizePopoverMaxHeight") && origin.Y > room
            ? Math.Max(Ui.D("Space200"), origin.Y - gap - Math.Min(Ui.D("SizePopoverMaxHeight"), origin.Y))
            : below;
        AbsoluteLayout.SetLayoutBounds(_card, new Rect(x, y, width, AbsoluteLayout.AutoSize));
        IsVisible = true;
        _card.Opacity = 0;
        _ = _card.FadeToAsync(1, MotionTokens.Fast, OnionEasing.Enter);
    }

    internal void CloseNow()
    {
        IsVisible = false;
        _card.Content = null;
    }
}

/// <summary>One entry of a <see cref="Menu"/>; <c>null</c> text = separator.</summary>
public sealed record MenuEntry(
    string? Text,
    Action? Invoke = null,
    string? Glyph = null,
    string? Shortcut = null,
    bool IsChecked = false,
    bool IsDanger = false)
{
    public static MenuEntry Separator { get; } = new((string?)null);
}

/// <summary>Menu list (§4.6.6): rows <c>size.row.heightDense</c>, icon 16 + label + shortcut, danger in <c>status.danger</c>.</summary>
public sealed class Menu : VerticalStackLayout
{
    public Menu(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            Children.Add(entry.Text is null ? new BoxView().Styled("Divider") : Row(entry));
        }
    }

    public static void Show(VisualElement anchor, IEnumerable<MenuEntry> entries, double? width = null) =>
        PopoverService.Show(anchor, new Menu(entries), width);

    private static View Row(MenuEntry entry)
    {
        var colorKey = entry.IsDanger ? "ColorStatusDanger" : "ColorTextPrimary";
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200"),
            Padding = Ui.Thick("PaddingNavItem"),
            MinimumHeightRequest = Ui.D("SizeMinTarget")
        }.Dyn(HeightRequestProperty, "SizeRowHeightDense");
        grid.Add(Ui.Icon(entry.IsChecked ? Icons.Check : entry.Glyph ?? string.Empty, "Text.Icon.Sm", colorKey));
        var label = Ui.Text("Text.Body", entry.Text).Dyn(Label.TextColorProperty, colorKey);
        label.VerticalOptions = LayoutOptions.Center;
        grid.Add(label, 1);
        if (entry.Shortcut is not null)
        {
            grid.Add(new Kbd { Text = entry.Shortcut, VerticalOptions = LayoutOptions.Center }, 2);
        }

        var row = Ui.Box(grid, "ColorTransparent", "RadiusSm");
        SemanticProperties.SetDescription(row, entry.Text);
        Hover.Attach(row, "ColorTransparent");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            PopoverService.Close();
            entry.Invoke?.Invoke();
        };
        row.GestureRecognizers.Add(tap);
        return row;
    }
}

/// <summary>Pointer hover background (<c>state.hover</c>) for non-button rows and cards (§4.0).</summary>
internal static class Hover
{
    public static void Attach(View element, string normalKey, string hoverKey = "ColorStateHover")
    {
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => element.SetDynamicResource(VisualElement.BackgroundColorProperty, hoverKey);
        pointer.PointerExited += (_, _) => element.SetDynamicResource(VisualElement.BackgroundColorProperty, normalKey);
        element.GestureRecognizers.Add(pointer);
    }
}
