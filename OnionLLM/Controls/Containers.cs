using System.Collections;
using System.Windows.Input;
using OnionLLM.Design;
using OnionLLM.Resources;

namespace OnionLLM.Controls;

public enum CardVariant
{
    Default,
    Interactive,
    Accent,
    Inset
}

/// <summary>
/// Card (DESIGN.md §4.3.1): header (title or overline + actions) · body · footer. <c>Interactive</c> — whole card clickable
/// with hover lift (M8); <c>Accent</c> — 3 dp bar in a source/series colour; <c>Inset</c> — tile inside a card.
/// </summary>
[ContentProperty(nameof(Body))]
public class Card : ContentView
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(Card), string.Empty, propertyChanged: (b, _, _) => ((Card)b).UpdateHeader());

    public static readonly BindableProperty IsOverlineTitleProperty = BindableProperty.Create(
        nameof(IsOverlineTitle), typeof(bool), typeof(Card), false, propertyChanged: (b, _, _) => ((Card)b).UpdateHeader());

    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
        nameof(Subtitle), typeof(string), typeof(Card), string.Empty, propertyChanged: (b, _, _) => ((Card)b).UpdateHeader());

    public static readonly BindableProperty BodyProperty = BindableProperty.Create(
        nameof(Body), typeof(View), typeof(Card), null, propertyChanged: (b, _, v) => ((Card)b)._body.Content = v as View);

    public static readonly BindableProperty ActionsProperty = BindableProperty.Create(
        nameof(Actions), typeof(View), typeof(Card), null, propertyChanged: (b, _, v) =>
        {
            var card = (Card)b;
            card._actions.Content = v as View;
            card.UpdateHeader();
        });

    public static readonly BindableProperty FooterProperty = BindableProperty.Create(
        nameof(Footer), typeof(View), typeof(Card), null, propertyChanged: (b, _, v) =>
        {
            var card = (Card)b;
            card._footer.Content = v as View;
            card._footer.IsVisible = v is not null;
        });

    public static readonly BindableProperty VariantProperty = BindableProperty.Create(
        nameof(Variant), typeof(CardVariant), typeof(Card), CardVariant.Default, propertyChanged: (b, _, _) => ((Card)b).UpdateVariant());

    public static readonly BindableProperty AccentColorKeyProperty = BindableProperty.Create(
        nameof(AccentColorKey), typeof(string), typeof(Card), "ColorActionPrimary", propertyChanged: (b, _, _) => ((Card)b).UpdateVariant());

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command), typeof(ICommand), typeof(Card));

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter), typeof(object), typeof(Card));

    private readonly Border _frame = new();
    private readonly BoxView _accent = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Start };
    private readonly Grid _header = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 0 };
    private readonly Label _title = new();
    private readonly Label _subtitle = Ui.Text("Text.Caption");
    private readonly ContentView _actions = new() { VerticalOptions = LayoutOptions.Center };
    private readonly ContentView _body = new();
    private readonly ContentView _footer = new() { IsVisible = false };
    private bool _hover;

    public Card()
    {
        _accent.WidthRequest = Ui.D("SizeAccentBar");
        _accent.HeightRequest = -1;
        _title.LineBreakMode = LineBreakMode.TailTruncation;
        _header.Add(new VerticalStackLayout { Spacing = Ui.D("Space050"), Children = { _title, _subtitle } });
        _header.Add(_actions, 1);
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space300"), Children = { _header, _body, _footer } };
        _frame.Content = new Grid { Children = { stack, _accent } };
        Content = _frame;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (Variant == CardVariant.Interactive && Command?.CanExecute(CommandParameter) == true)
            {
                Command.Execute(CommandParameter);
            }
        };
        _frame.GestureRecognizers.Add(tap);
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => SetHover(true);
        pointer.PointerExited += (_, _) => SetHover(false);
        _frame.GestureRecognizers.Add(pointer);

        UpdateHeader();
        UpdateVariant();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>Header rendered as an overline („KOSZT · 30 DNI”) instead of a card title.</summary>
    public bool IsOverlineTitle { get => (bool)GetValue(IsOverlineTitleProperty); set => SetValue(IsOverlineTitleProperty, value); }

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public View? Body { get => (View?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public View? Actions { get => (View?)GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public View? Footer { get => (View?)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    public CardVariant Variant { get => (CardVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }

    public string AccentColorKey { get => (string)GetValue(AccentColorKeyProperty); set => SetValue(AccentColorKeyProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    private void UpdateHeader()
    {
        _title.Styled(IsOverlineTitle ? "Text.Overline" : "Text.TitleCard");
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _header.IsVisible = !string.IsNullOrEmpty(Title) || Actions is not null;
    }

    private void UpdateVariant()
    {
        _frame.Styled(Variant == CardVariant.Inset ? "Card.Inset" : "Card.Default");
        _accent.IsVisible = Variant == CardVariant.Accent;
        _accent.Dyn(BoxView.ColorProperty, AccentColorKey);
        if (Variant == CardVariant.Accent)
        {
            // The bar sits inside the padding edge: pull it to the card edge, clipped by the card radius.
            var padding = Ui.D("Space400");
            _accent.Margin = new Thickness(-padding, -padding, 0, -padding);
        }
    }

    /// <summary>M8: hover lifts an interactive card (elevation.1 → 2, stronger stroke).</summary>
    private void SetHover(bool hover)
    {
        if (Variant != CardVariant.Interactive || hover == _hover)
        {
            return;
        }

        _hover = hover;
        _frame.Dyn(Border.ShadowProperty, hover ? "ShadowE2" : "ShadowE1");
        _frame.Dyn(Border.StrokeProperty, hover ? "ColorBorderStrongBrush" : "ColorBorderDefaultBrush");
        if (MotionTokens.AllowsTransforms)
        {
            _ = _frame.TranslateToAsync(0, hover ? -Ui.D("StrokeDefault") : 0, MotionTokens.Fast, OnionEasing.Standard);
        }
    }
}

/// <summary>Page section header (§4.3.5): title · description · actions; optional collapse with a remembered state.</summary>
[ContentProperty(nameof(Body))]
public sealed class Section : VerticalStackLayout
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(Section), string.Empty, propertyChanged: (b, _, v) => ((Section)b)._title.Text = (string)v);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description), typeof(string), typeof(Section), string.Empty, propertyChanged: (b, _, v) =>
        {
            var section = (Section)b;
            section._description.Text = (string)v;
            section._description.IsVisible = !string.IsNullOrEmpty((string)v);
        });

    public static readonly BindableProperty BodyProperty = BindableProperty.Create(
        nameof(Body), typeof(View), typeof(Section), null, propertyChanged: (b, _, v) => ((Section)b)._body.Content = v as View);

    public static readonly BindableProperty ActionsProperty = BindableProperty.Create(
        nameof(Actions), typeof(View), typeof(Section), null, propertyChanged: (b, _, v) => ((Section)b)._actions.Content = v as View);

    public static readonly BindableProperty IsCollapsibleProperty = BindableProperty.Create(
        nameof(IsCollapsible), typeof(bool), typeof(Section), false, propertyChanged: (b, _, _) => ((Section)b).Update());

    public static readonly BindableProperty StateKeyProperty = BindableProperty.Create(
        nameof(StateKey), typeof(string), typeof(Section), string.Empty, propertyChanged: (b, _, _) => ((Section)b).Restore());

    private readonly Label _chevron = Ui.Icon(Icons.ChevronDown, "Text.Icon.Sm");
    private readonly Label _title = Ui.Text("Text.TitleSection");
    private readonly Label _description = Ui.Text("Text.Caption");
    private readonly ContentView _actions = new() { VerticalOptions = LayoutOptions.Center };
    private readonly ContentView _body = new();
    private bool _collapsed;

    public Section()
    {
        Spacing = Ui.D("Space300");
        _description.IsVisible = false;
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        };
        _chevron.VerticalOptions = LayoutOptions.Center;
        header.Add(_chevron);
        header.Add(new VerticalStackLayout { Spacing = Ui.D("Space050"), Children = { _title, _description } }, 1);
        header.Add(_actions, 2);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Toggle();
        _chevron.GestureRecognizers.Add(tap);
        _title.GestureRecognizers.Add(tap);
        Children.Add(header);
        Children.Add(_body);
        Update();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public View? Body { get => (View?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public View? Actions { get => (View?)GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public bool IsCollapsible { get => (bool)GetValue(IsCollapsibleProperty); set => SetValue(IsCollapsibleProperty, value); }

    /// <summary>ViewState key under which the collapsed state is remembered (§5.4).</summary>
    public string StateKey { get => (string)GetValue(StateKeyProperty); set => SetValue(StateKeyProperty, value); }

    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            _collapsed = value;
            Update();
        }
    }

    private void Restore()
    {
        if (!string.IsNullOrEmpty(StateKey))
        {
            _collapsed = ViewModels.ViewState.GetBool("section." + StateKey, _collapsed);
            Update();
        }
    }

    private async void Toggle()
    {
        if (!IsCollapsible)
        {
            return;
        }

        _collapsed = !_collapsed;
        if (!string.IsNullOrEmpty(StateKey))
        {
            ViewModels.ViewState.Set("section." + StateKey, _collapsed);
        }

        // M12: chevron turns, content fades in.
        _ = _chevron.RotateToAsync(_collapsed ? -90 : 0, MotionTokens.Base, OnionEasing.Standard);
        Update();
        if (!_collapsed && MotionTokens.Base > 0)
        {
            _body.Opacity = 0;
            await _body.FadeToAsync(1, MotionTokens.Base, OnionEasing.Standard);
        }
    }

    private void Update()
    {
        _chevron.IsVisible = IsCollapsible;
        _chevron.Rotation = _collapsed ? -90 : 0;
        _body.IsVisible = !IsCollapsible || !_collapsed;
        SemanticProperties.SetHeadingLevel(_title, SemanticHeadingLevel.Level2);
        SemanticProperties.SetDescription(_title, IsCollapsible ? $"{Title}, {(_collapsed ? "zwinięta" : "rozwinięta")}" : Title);
    }
}

/// <summary>One row of a <see cref="KeyValueList"/>.</summary>
public sealed record KeyValueItem(string Label, string Value, bool IsNumeric = false, bool IsMono = false, bool CanCopy = false);

/// <summary>Label — value list (§4.3.7) with copyable values (§5.12).</summary>
public sealed class KeyValueList : VerticalStackLayout
{
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IEnumerable), typeof(KeyValueList), null, propertyChanged: (b, _, _) => ((KeyValueList)b).Rebuild());

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var item in ItemsSource?.OfType<KeyValueItem>() ?? [])
        {
            var row = new Grid
            {
                ColumnDefinitions = [new(new GridLength(2, GridUnitType.Star)), new(new GridLength(3, GridUnitType.Star)), new(GridLength.Auto)],
                ColumnSpacing = Ui.D("Space200")
            }.Dyn(MinimumHeightRequestProperty, "SizeRowHeightDense");
            var label = Ui.Text("Text.Secondary", item.Label);
            label.VerticalOptions = LayoutOptions.Center;
            var value = Ui.Text(item.IsNumeric ? "Text.TableNum" : item.IsMono ? "Text.Mono" : "Text.BodyStrong", item.Value);
            value.HorizontalTextAlignment = TextAlignment.End;
            value.VerticalOptions = LayoutOptions.Center;
            value.LineBreakMode = item.IsMono ? LineBreakMode.MiddleTruncation : LineBreakMode.TailTruncation;
            ToolTipProperties.SetText(value, item.Value);
            row.Add(label);
            row.Add(value, 1);
            if (item.CanCopy)
            {
                var copy = new IconButton { Glyph = Icons.Copy, AutomationId = "KeyValue.Copy." + item.Label }.Styled("IconButton.Ghost.Sm");
                ToolTipProperties.SetText(copy, "Kopiuj");
                SemanticProperties.SetDescription(copy, "Kopiuj: " + item.Label);
                copy.Clicked += async (_, _) =>
                {
                    await Ui.CopyAsync(item.Value);
                    copy.Glyph = Icons.Check;
                    await Task.Delay(MotionTokens.FeedbackVisible);
                    copy.Glyph = Icons.Copy;
                };
                row.Add(copy, 2);
            }

            Children.Add(row);
            Children.Add(new BoxView().Styled("Divider"));
        }
    }
}

/// <summary>Simple list row (§4.5.2): leading · title/subtitle · trailing; hover; optional command.</summary>
public sealed class ListItem : ContentView
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(ListItem), string.Empty, propertyChanged: (b, _, v) => ((ListItem)b)._title.Text = (string)v);

    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
        nameof(Subtitle), typeof(string), typeof(ListItem), string.Empty, propertyChanged: (b, _, v) =>
        {
            var item = (ListItem)b;
            item._subtitle.Text = (string)v;
            item._subtitle.IsVisible = !string.IsNullOrEmpty((string)v);
        });

    public static readonly BindableProperty LeadingProperty = BindableProperty.Create(
        nameof(Leading), typeof(View), typeof(ListItem), null, propertyChanged: (b, _, v) => ((ListItem)b)._leading.Content = v as View);

    public static readonly BindableProperty TrailingProperty = BindableProperty.Create(
        nameof(Trailing), typeof(View), typeof(ListItem), null, propertyChanged: (b, _, v) => ((ListItem)b)._trailing.Content = v as View);

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ListItem));

    private readonly Label _title = Ui.Text("Text.BodyStrong");
    private readonly Label _subtitle = Ui.Text("Text.Caption");
    private readonly ContentView _leading = new() { VerticalOptions = LayoutOptions.Center };
    private readonly ContentView _trailing = new() { VerticalOptions = LayoutOptions.Center };

    public ListItem()
    {
        _subtitle.IsVisible = false;
        _title.LineBreakMode = LineBreakMode.TailTruncation;
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space300"),
            Padding = Ui.Thick("PaddingRow")
        }.Dyn(MinimumHeightRequestProperty, "SizeRowHeight");
        grid.Add(_leading);
        grid.Add(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { _title, _subtitle } }, 1);
        grid.Add(_trailing, 2);
        var box = Ui.Box(grid, "ColorTransparent", "RadiusMd");
        Hover.Attach(box, "ColorTransparent");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (Command?.CanExecute(null) == true) { Command.Execute(null); } };
        box.GestureRecognizers.Add(tap);
        Content = box;
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public View? Leading { get => (View?)GetValue(LeadingProperty); set => SetValue(LeadingProperty, value); }

    public View? Trailing { get => (View?)GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
}

public enum BannerKind
{
    Info,
    Warning,
    Danger,
    Success
}

/// <summary>Persistent message under the PageHeader (§4.6.4): status background, icon, title, message, actions, close.</summary>
public sealed class Banner : ContentView
{
    public static readonly BindableProperty KindProperty = Prop(nameof(Kind), typeof(BannerKind), BannerKind.Info);
    public static readonly BindableProperty TitleProperty = Prop(nameof(Title), typeof(string), string.Empty);
    public static readonly BindableProperty MessageProperty = Prop(nameof(Message), typeof(string), string.Empty);
    public static readonly BindableProperty ActionTextProperty = Prop(nameof(ActionText), typeof(string), string.Empty);
    public static readonly BindableProperty ActionCommandProperty = Prop(nameof(ActionCommand), typeof(ICommand), null);
    public static readonly BindableProperty CanCloseProperty = Prop(nameof(CanClose), typeof(bool), false);
    public static readonly BindableProperty CloseCommandProperty = Prop(nameof(CloseCommand), typeof(ICommand), null);

    private readonly Border _frame = new() { StrokeThickness = Ui.D("StrokeDefault"), StrokeShape = Ui.Corner("RadiusMd") };
    private readonly Label _icon = Ui.Icon(Icons.Info, "Text.Icon");
    private readonly Label _title = Ui.Text("Text.BodyStrong");
    private readonly Label _message = Ui.Text("Text.Body");
    private readonly Button _action = new Button().Styled("Button.Secondary.Sm");
    private readonly IconButton _close = new IconButton { Glyph = Icons.X }.Styled("IconButton.Ghost.Sm");

    public Banner()
    {
        _frame.Padding = Ui.Thick("PaddingBanner");
        _icon.VerticalOptions = LayoutOptions.Start;
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space300")
        };
        grid.Add(_icon);
        grid.Add(new VerticalStackLayout { Spacing = Ui.D("Space050"), Children = { _title, _message } }, 1);
        _action.VerticalOptions = LayoutOptions.Center;
        grid.Add(_action, 2);
        ToolTipProperties.SetText(_close, "Zamknij");
        SemanticProperties.SetDescription(_close, "Zamknij komunikat");
        grid.Add(_close, 3);
        _action.Clicked += (_, _) => { if (ActionCommand?.CanExecute(null) == true) { ActionCommand.Execute(null); } };
        _close.Clicked += (_, _) =>
        {
            IsVisible = false;
            CloseCommand?.Execute(null);
        };
        _frame.Content = grid;
        Content = _frame;
        Update();
    }

    public BannerKind Kind { get => (BannerKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }

    public bool CanClose { get => (bool)GetValue(CanCloseProperty); set => SetValue(CanCloseProperty, value); }

    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    private static BindableProperty Prop(string name, Type type, object? defaultValue) => BindableProperty.Create(
        name, type, typeof(Banner), defaultValue, propertyChanged: (b, _, _) => ((Banner)b).Update());

    private void Update()
    {
        var (key, glyph) = Kind switch
        {
            BannerKind.Warning => ("ColorStatusWarning", Icons.TriangleAlert),
            BannerKind.Danger => ("ColorStatusDanger", Icons.CircleAlert),
            BannerKind.Success => ("ColorStatusSuccess", Icons.CircleCheck),
            _ => ("ColorStatusInfo", Icons.Info)
        };
        _frame.Dyn(BackgroundColorProperty, key + "Bg").Dyn(Border.StrokeProperty, key + "BorderBrush");
        _icon.Text = glyph;
        _icon.Dyn(Label.TextColorProperty, key);
        _title.Text = Title;
        _title.IsVisible = !string.IsNullOrEmpty(Title);
        _message.Text = Message;
        _message.IsVisible = !string.IsNullOrEmpty(Message);
        _action.Text = ActionText;
        _action.IsVisible = !string.IsNullOrEmpty(ActionText);
        _close.IsVisible = CanClose;
        SemanticProperties.SetDescription(this, $"{Title} {Message}");
    }
}

/// <summary>Footer of lists and reports (§4.7.6): missing/estimated prices; rendered only when not empty.</summary>
public sealed class PricingNote : HorizontalStackLayout
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(PricingNote), string.Empty, propertyChanged: (b, _, _) => ((PricingNote)b).Update());

    public static readonly BindableProperty AddPricesCommandProperty = BindableProperty.Create(
        nameof(AddPricesCommand), typeof(ICommand), typeof(PricingNote), propertyChanged: (b, _, _) => ((PricingNote)b).Update());

    private readonly Label _icon = Ui.Icon(Icons.Info, "Text.Icon.Xs");
    private readonly Label _text = Ui.Text("Text.Caption");
    private readonly Button _link = new Button { Text = "Dodaj ceny" }.Styled("Button.Link");

    public PricingNote()
    {
        Spacing = Ui.D("Space150");
        _text.VerticalOptions = LayoutOptions.Center;
        _text.LineBreakMode = LineBreakMode.WordWrap;
        Children.Add(_icon);
        Children.Add(_text);
        Children.Add(_link);
        _link.Clicked += (_, _) => AddPricesCommand?.Execute(null);
        Update();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public ICommand? AddPricesCommand { get => (ICommand?)GetValue(AddPricesCommandProperty); set => SetValue(AddPricesCommandProperty, value); }

    private void Update()
    {
        IsVisible = !string.IsNullOrEmpty(Text);
        var warning = Text.StartsWith("Koszt niepełny", StringComparison.Ordinal);
        _text.Text = Text;
        _text.Dyn(Label.TextColorProperty, warning ? "ColorStatusWarning" : "ColorTextTertiary");
        _icon.Dyn(Label.TextColorProperty, warning ? "ColorStatusWarning" : "ColorTextTertiary");
        _link.IsVisible = warning && AddPricesCommand is not null;
    }
}

public enum EmptyStateVariant
{
    Page,
    Inline
}

/// <summary>
/// Empty state (§4.7.9): onion-ring illustration with a Lucide icon · title · description (≤ 2 sentences) · one action.
/// ErrorState (§4.7.11) is the same component with <see cref="IsError"/> and expandable details.
/// </summary>
public sealed class EmptyState : VerticalStackLayout
{
    public static readonly BindableProperty TitleProperty = Prop(nameof(Title), typeof(string), string.Empty);
    public static readonly BindableProperty DescriptionProperty = Prop(nameof(Description), typeof(string), string.Empty);
    public static readonly BindableProperty GlyphProperty = Prop(nameof(Glyph), typeof(string), Icons.Inbox);
    public static readonly BindableProperty ActionTextProperty = Prop(nameof(ActionText), typeof(string), string.Empty);
    public static readonly BindableProperty ActionCommandProperty = Prop(nameof(ActionCommand), typeof(ICommand), null);
    public static readonly BindableProperty IsPrimaryActionProperty = Prop(nameof(IsPrimaryAction), typeof(bool), false);
    public static readonly BindableProperty VariantProperty = Prop(nameof(Variant), typeof(EmptyStateVariant), EmptyStateVariant.Page);
    public static readonly BindableProperty IsErrorProperty = Prop(nameof(IsError), typeof(bool), false);
    public static readonly BindableProperty ErrorDetailsProperty = Prop(nameof(ErrorDetails), typeof(string), string.Empty);

    private readonly GraphicsView _rings = new();
    private readonly Label _icon = Ui.Icon(Icons.Inbox, "Text.Icon");
    private readonly Grid _illustration = [];
    private readonly Label _title = Ui.Text("Text.TitleCard");
    private readonly Label _description = Ui.Text("Text.Secondary");
    private readonly Button _action = new();
    private readonly Button _detailsLink = new Button { Text = "Szczegóły błędu" }.Styled("Button.Link");
    private readonly VerticalStackLayout _details = new() { IsVisible = false };

    public EmptyState()
    {
        HorizontalOptions = LayoutOptions.Center;
        _rings.Drawable = new RingsDrawable(this);
        _illustration.Children.Add(_rings);
        _illustration.Children.Add(_icon);
        _illustration.HorizontalOptions = LayoutOptions.Center;
        _title.HorizontalTextAlignment = TextAlignment.Center;
        _description.HorizontalTextAlignment = TextAlignment.Center;
        _description.MaximumWidthRequest = Ui.D("SizeTextMaxWidth");
        _action.HorizontalOptions = LayoutOptions.Center;
        _action.Clicked += (_, _) => { if (ActionCommand?.CanExecute(null) == true) { ActionCommand.Execute(null); } };
        _detailsLink.HorizontalOptions = LayoutOptions.Center;
        _detailsLink.Clicked += (_, _) => _details.IsVisible = !_details.IsVisible;
        Children.Add(_illustration);
        Children.Add(_title);
        Children.Add(_description);
        Children.Add(_action);
        Children.Add(_detailsLink);
        Children.Add(_details);
        ThemeService.ThemeChanged += (_, _) => _rings.Invalidate();
        Update();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }

    public bool IsPrimaryAction { get => (bool)GetValue(IsPrimaryActionProperty); set => SetValue(IsPrimaryActionProperty, value); }

    public EmptyStateVariant Variant { get => (EmptyStateVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }

    public bool IsError { get => (bool)GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }

    public string ErrorDetails { get => (string)GetValue(ErrorDetailsProperty); set => SetValue(ErrorDetailsProperty, value); }

    private static BindableProperty Prop(string name, Type type, object? defaultValue) => BindableProperty.Create(
        name, type, typeof(EmptyState), defaultValue, propertyChanged: (b, _, _) => ((EmptyState)b).Update());

    private void Update()
    {
        var page = Variant == EmptyStateVariant.Page;
        Spacing = Ui.D(page ? "Space300" : "Space200");
        Margin = page ? new Thickness(Ui.D("Space1000")) : new Thickness(Ui.D("Space400"));
        var size = Ui.D(page ? "SizeIllustration" : "SizeIllustrationInline");
        _illustration.WidthRequest = _illustration.HeightRequest = size;
        _rings.IsVisible = page;
        _icon.Text = IsError ? Icons.TriangleAlert : Glyph;
        _icon.Styled(page ? "Text.Icon" : "Text.Icon.Sm");
        _icon.Dyn(Label.TextColorProperty, IsError ? "ColorStatusDanger" : "ColorTextTertiary");
        _title.Text = Title;
        _description.Text = Description;
        _description.IsVisible = !string.IsNullOrEmpty(Description);
        _action.Text = ActionText;
        _action.Styled(IsPrimaryAction || IsError ? "Button.Primary" : "Button.Secondary");
        _action.IsVisible = !string.IsNullOrEmpty(ActionText);
        _detailsLink.IsVisible = IsError && !string.IsNullOrEmpty(ErrorDetails);
        _details.Children.Clear();
        if (IsError && !string.IsNullOrEmpty(ErrorDetails))
        {
            var text = Ui.Text("Text.MonoSm", ErrorDetails);
            var copy = new Button { Text = "Kopiuj" }.Styled("Button.Ghost.Sm");
            copy.Clicked += async (_, _) => await Ui.CopyAsync(ErrorDetails);
            _details.Children.Add(Ui.Box(text, "ColorSurface2", "RadiusMd", "LayoutCardPadding"));
            _details.Children.Add(copy);
        }

        _rings.Invalidate();
    }

    /// <summary>Concentric onion rings behind the icon (brand motif, §1.3).</summary>
    private sealed class RingsDrawable(EmptyState owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF rect)
        {
            var center = rect.Center;
            var radius = Math.Min(rect.Width, rect.Height) / 2;
            canvas.FillColor = DesignTokens.Color("ColorSurface3");
            canvas.FillCircle(center, radius);
            canvas.StrokeColor = DesignTokens.Color(owner.IsError ? "ColorStatusDangerBorder" : "ColorBorderDefault");
            canvas.StrokeSize = (float)Ui.D("StrokeDefault");
            for (var ring = 1; ring <= 3; ring++)
            {
                canvas.DrawCircle(center, radius * ring / 4f);
            }
        }
    }
}

/// <summary>Skeleton placeholders (§4.7.10) with the M16 shimmer; static in reduced motion.</summary>
public sealed class Skeleton : Border
{
    private bool _running;

    public Skeleton()
    {
        StrokeThickness = 0;
        StrokeShape = Ui.Corner("RadiusXs");
        this.Dyn(BackgroundColorProperty, "ColorSurface2");
        Loaded += (_, _) => Shimmer();
    }

    /// <summary>A text-line block: height of the given type size, width as a fraction of the container.</summary>
    public static View Line(string sizeKey = "TypeBodySize", double widthFraction = 1)
    {
        var line = new Skeleton { HeightRequest = Ui.D(sizeKey), HorizontalOptions = LayoutOptions.Fill };
        return widthFraction >= 1
            ? line
            : new Grid
            {
                ColumnDefinitions = [new(new GridLength(widthFraction, GridUnitType.Star)), new(new GridLength(1 - widthFraction, GridUnitType.Star))],
                Children = { line }
            };
    }

    public static View Lines(int count) => new VerticalStackLayout
    {
        Spacing = Ui.D("Space200"),
        IsVisible = false,
        Children = { Line("TypeMetricLgSize", 0.6), Line("TypeCaptionSize", 0.8) }
    };

    private async void Shimmer()
    {
        if (_running || !MotionTokens.AllowsLoops)
        {
            return;
        }

        _running = true;
        while (Window is not null && MotionTokens.AllowsLoops && IsVisible)
        {
            this.Dyn(BackgroundColorProperty, "ColorSurface3");
            await this.FadeToAsync(Ui.D("OpacityGhost"), MotionTokens.Shimmer / 2, Easing.Linear);
            this.Dyn(BackgroundColorProperty, "ColorSurface2");
            await this.FadeToAsync(1, MotionTokens.Shimmer / 2, Easing.Linear);
        }

        _running = false;
    }
}
