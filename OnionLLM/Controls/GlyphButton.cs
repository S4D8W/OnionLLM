using OnionLLM.Design;

namespace OnionLLM.Controls;

/// <summary>
/// Base for buttons that show a Lucide glyph (DESIGN.md §2.6): the glyph is a <see cref="FontImageSource"/> whose colour
/// always follows <see cref="Button.TextColor"/>, so visual states that change the text colour recolour the icon too.
/// </summary>
public abstract class GlyphButton : Button
{
    public static readonly BindableProperty GlyphProperty = BindableProperty.Create(
        nameof(Glyph), typeof(string), typeof(GlyphButton), string.Empty,
        propertyChanged: (bindable, _, _) => ((GlyphButton)bindable).UpdateGlyph());

    public static readonly BindableProperty GlyphSizeProperty = BindableProperty.Create(
        nameof(GlyphSize), typeof(double), typeof(GlyphButton), 0d,
        propertyChanged: (bindable, _, _) => ((GlyphButton)bindable).UpdateGlyph());

    protected GlyphButton()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TextColor))
            {
                UpdateGlyph();
            }
        };
    }

    /// <summary>Glyph from <c>OnionLLM.Resources.Icons</c>.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Glyph size; 0 = <c>IconMd</c> (20).</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    protected void UpdateGlyph()
    {

        if (string.IsNullOrEmpty(Glyph))
        {
            ImageSource = null;
            return;
        }

        ImageSource = new FontImageSource
        {
            FontFamily = "Lucide",
            Glyph = Glyph,
            Size = GlyphSize > 0 ? GlyphSize : DesignTokens.Double("IconMd", 20),
            Color = TextColor
        };
    }
}

/// <summary>Icon-only button (DESIGN.md §4.1.2). Always set a tooltip and <c>SemanticProperties.Description</c>.</summary>
public sealed class IconButton : GlyphButton
{
    public static readonly BindableProperty IsSpinningProperty = BindableProperty.Create(
        nameof(IsSpinning), typeof(bool), typeof(IconButton), false,
        propertyChanged: (bindable, _, value) => ((IconButton)bindable).OnSpinningChanged((bool)value));

    private bool _spinning;

    /// <summary>„Spinning” state (M15): the icon rotates while a sync runs and stops after a full turn.</summary>
    public bool IsSpinning
    {
        get => (bool)GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    private async void OnSpinningChanged(bool spinning)
    {
        // Only while attached to a window: animations on detached views complete synchronously and would spin the UI thread.
        if (!spinning || _spinning || MotionTokens.Spin == 0 || Window is null)
        {
            return;
        }

        _spinning = true;
        try
        {
            do
            {
                Rotation = 0;
                await this.RotateToAsync(360, MotionTokens.Spin, Easing.Linear);
            }
            while (IsSpinning && MotionTokens.Spin > 0 && Window is not null);
        }
        finally
        {
            Rotation = 0;
            _spinning = false;
        }
    }
}

/// <summary>
/// Sidebar navigation item (DESIGN.md §4.7.2): icon + label; inactive — <c>text.secondary</c>, hover — <c>state.hover</c>,
/// active — <c>action.subtle</c> background with <c>action.onSubtle</c> SemiBold text; collapsed — icon only (label in
/// the tooltip). States are applied in code so hover never overrides the active background.
/// </summary>
public sealed class NavItem : GlyphButton
{
    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(
        nameof(IsActive), typeof(bool), typeof(NavItem), false,
        propertyChanged: (bindable, _, _) => ((NavItem)bindable).UpdateState());

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(NavItem), string.Empty,
        propertyChanged: (bindable, _, _) => ((NavItem)bindable).UpdateText());

    public static readonly BindableProperty ShortcutProperty = BindableProperty.Create(
        nameof(Shortcut), typeof(string), typeof(NavItem), string.Empty,
        propertyChanged: (bindable, _, _) => ((NavItem)bindable).UpdateText());

    public static readonly BindableProperty IsCollapsedProperty = BindableProperty.Create(
        nameof(IsCollapsed), typeof(bool), typeof(NavItem), false,
        propertyChanged: (bindable, _, _) => ((NavItem)bindable).UpdateText());

    private bool _hover;

    public NavItem()
    {
        ContentLayout = new ButtonContentLayout(ButtonContentLayout.ImagePosition.Left, DesignTokens.Double("Space300", 12));
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => { _hover = true; UpdateState(); };
        pointer.PointerExited += (_, _) => { _hover = false; UpdateState(); };
        GestureRecognizers.Add(pointer);
        Focused += (_, _) => SetDynamicResource(BorderColorProperty, "ColorBorderFocus");
        Unfocused += (_, _) => SetDynamicResource(BorderColorProperty, "ColorTransparent");
        UpdateState();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Shortcut shown in the tooltip, e.g. „Ctrl+3”.</summary>
    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>Collapsed sidebar: icon only, the label moves to the tooltip.</summary>
    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    private void UpdateText()
    {
        Text = IsCollapsed ? string.Empty : Label;
        SemanticProperties.SetDescription(this, Label);
        ToolTipProperties.SetText(this, string.IsNullOrEmpty(Shortcut) ? Label : $"{Label} · {Shortcut}");
    }

    private void UpdateState()
    {
        SetDynamicResource(BackgroundColorProperty,
            IsActive ? "ColorActionSubtle" : _hover ? "ColorStateHover" : "ColorTransparent");
        SetDynamicResource(TextColorProperty,
            IsActive ? "ColorActionOnSubtle" : _hover ? "ColorTextPrimary" : "ColorTextSecondary");
        FontFamily = IsActive ? "InterSemiBold" : "InterMedium";
    }
}
