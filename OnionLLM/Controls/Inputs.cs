using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Input;
using OnionLLM.Design;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls;

/// <summary>2–5 mutually exclusive options visible at once (DESIGN.md §4.2.5).</summary>
public sealed class SegmentedControl : ContentView
{
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IList), typeof(SegmentedControl), null, propertyChanged: (b, _, _) => ((SegmentedControl)b).Rebuild());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex), typeof(int), typeof(SegmentedControl), 0, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((SegmentedControl)b).UpdateSelection());

    public static readonly BindableProperty IsSmallProperty = BindableProperty.Create(
        nameof(IsSmall), typeof(bool), typeof(SegmentedControl), false, propertyChanged: (b, _, _) => ((SegmentedControl)b).Rebuild());

    /// <summary>Accessible name of the group, e.g. „Okres”.</summary>
    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(SegmentedControl), string.Empty, propertyChanged: (b, _, _) => ((SegmentedControl)b).UpdateSelection());

    private readonly HorizontalStackLayout _row = new() { Spacing = 0 };

    public SegmentedControl()
    {
        HorizontalOptions = LayoutOptions.Start;
        Content = Ui.Box(_row, "ColorSurface2", "RadiusMd", null);
        ((Border)Content).Padding = Ui.Thick("PaddingSegmented");
        ThemeService.ThemeChanged += (_, _) => UpdateSelection();
    }

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    public bool IsSmall { get => (bool)GetValue(IsSmallProperty); set => SetValue(IsSmallProperty, value); }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private void Rebuild()
    {
        _row.Children.Clear();
        var index = 0;
        foreach (var item in ItemsSource ?? Array.Empty<object>())
        {
            var position = index++;
            var button = new Button { Text = item?.ToString() }.Styled("Button.Ghost");
            button.Dyn(HeightRequestProperty, IsSmall ? "SizeControlHeightSm" : "SizeControlHeight");
            button.Padding = Ui.Thick(IsSmall ? "PaddingButtonSm" : "PaddingButton");
            button.CornerRadius = Ui.Res<int>(IsSmall ? "RadiusSmInt" : "RadiusMdInt");
            button.Clicked += (_, _) => SelectedIndex = position;
            _row.Children.Add(button);
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = _row.Children.Count;
        for (var i = 0; i < count; i++)
        {
            var button = (Button)_row.Children[i];
            var active = i == SelectedIndex;
            button.Dyn(BackgroundColorProperty, active ? "ColorSegmentActive" : "ColorTransparent");
            button.Dyn(Button.TextColorProperty, active ? "ColorTextPrimary" : "ColorTextSecondary");
            button.Shadow = active && ThemeService.EffectiveTheme == AppTheme.Light ? Ui.Res<Shadow>("ShadowE1") : null!;
            SemanticProperties.SetDescription(button,
                $"{Label}{(string.IsNullOrEmpty(Label) ? "" : ": ")}{button.Text}{(active ? ", wybrane" : "")}, {i + 1} z {count}");
        }
    }
}

public enum ChipVariant
{
    Toggle,
    Removable,
    Menu
}

/// <summary>Filter chip (§4.2.8): Toggle (check icon), Removable („Źródło: Claude Code ✕”), Menu (opens a list).</summary>
public sealed class FilterChip : GlyphButton
{
    public static readonly BindableProperty VariantProperty = BindableProperty.Create(
        nameof(Variant), typeof(ChipVariant), typeof(FilterChip), ChipVariant.Toggle, propertyChanged: (b, _, _) => ((FilterChip)b).Update());

    public static readonly BindableProperty IsCheckedProperty = BindableProperty.Create(
        nameof(IsChecked), typeof(bool), typeof(FilterChip), false, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((FilterChip)b).Update());

    public static readonly BindableProperty RemoveCommandProperty = BindableProperty.Create(nameof(RemoveCommand), typeof(ICommand), typeof(FilterChip));

    /// <summary>Menu variant: options and the selected index (two-way).</summary>
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IList), typeof(FilterChip), null, propertyChanged: (b, _, _) => ((FilterChip)b).Update());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex), typeof(int), typeof(FilterChip), 0, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((FilterChip)b).Update());

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(FilterChip), string.Empty, propertyChanged: (b, _, _) => ((FilterChip)b).Update());

    public FilterChip()
    {
        this.Dyn(HeightRequestProperty, "SizeControlHeightSm");
        Padding = Ui.Thick("PaddingChip");
        CornerRadius = (int)Ui.D("SizeControlHeightSm");
        FontFamily = "InterMedium";
        this.Dyn(FontSizeProperty, "TypeLabelSize");
        BorderWidth = Ui.D("StrokeDefault");
        GlyphSize = Ui.D("IconTable");
        HorizontalOptions = LayoutOptions.Start;
        Clicked += OnClicked;
        Update();
    }

    public ChipVariant Variant { get => (ChipVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }

    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    public ICommand? RemoveCommand { get => (ICommand?)GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    /// <summary>Toggle/Removable text, or the Menu prefix („Źródło”).</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private void OnClicked(object? sender, EventArgs e)
    {
        switch (Variant)
        {
            case ChipVariant.Toggle:
                IsChecked = !IsChecked;
                break;
            case ChipVariant.Removable:
                RemoveCommand?.Execute(null);
                break;
            case ChipVariant.Menu when ItemsSource is { } items:
                Menu.Show(this, items.Cast<object?>().Select((item, index) =>
                    new MenuEntry(item?.ToString(), () => SelectedIndex = index, IsChecked: index == SelectedIndex)));
                break;
        }
    }

    private void Update()
    {
        var active = Variant == ChipVariant.Removable || (Variant == ChipVariant.Toggle && IsChecked);
        this.Dyn(BackgroundColorProperty, active ? "ColorActionSubtle" : "ColorSurface1");
        this.Dyn(BorderColorProperty, active ? "ColorActionSubtleBorder" : "ColorBorderDefault");
        this.Dyn(TextColorProperty, active ? "ColorActionOnSubtle" : "ColorTextSecondary");
        switch (Variant)
        {
            case ChipVariant.Toggle:
                Glyph = IsChecked ? Icons.Check : string.Empty;
                Text = Label;
                SemanticProperties.SetDescription(this, $"{Label}, {(IsChecked ? "włączone" : "wyłączone")}");
                break;
            case ChipVariant.Removable:
                Glyph = Icons.X;
                ContentLayout = new ButtonContentLayout(ButtonContentLayout.ImagePosition.Right, Ui.D("Space150"));
                Text = Label;
                SemanticProperties.SetDescription(this, $"Filtr {Label}, usuń");
                break;
            case ChipVariant.Menu:
                Glyph = Icons.ChevronDown;
                ContentLayout = new ButtonContentLayout(ButtonContentLayout.ImagePosition.Right, Ui.D("Space150"));
                var value = ItemsSource is { } items && SelectedIndex >= 0 && SelectedIndex < items.Count ? items[SelectedIndex]?.ToString() : "";
                Text = string.IsNullOrEmpty(Label) ? value : $"{Label}: {value}";
                SemanticProperties.SetDescription(this, Text);
                break;
        }
    }
}

/// <summary>Entry without the native frame — the surrounding <see cref="TextField"/> draws the field (§4.2.0).</summary>
public sealed class BareEntry : Entry
{
}

public enum TextFieldKind
{
    Default,
    Mono,
    Number
}

/// <summary>Text field (§4.2.1): leading icon, clear button, unit suffix, error message; focus border 2 dp.</summary>
public class TextField : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(TextField), string.Empty, BindingMode.TwoWay, propertyChanged: (b, _, v) => ((TextField)b).OnTextChanged((string?)v));

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(TextField), string.Empty, propertyChanged: (b, _, v) => ((TextField)b).Entry.Placeholder = (string)v);

    public static readonly BindableProperty SuffixProperty = BindableProperty.Create(
        nameof(Suffix), typeof(string), typeof(TextField), string.Empty, propertyChanged: (b, _, _) => ((TextField)b).UpdateDecor());

    public static readonly BindableProperty GlyphProperty = BindableProperty.Create(
        nameof(Glyph), typeof(string), typeof(TextField), string.Empty, propertyChanged: (b, _, _) => ((TextField)b).UpdateDecor());

    public static readonly BindableProperty ErrorProperty = BindableProperty.Create(
        nameof(Error), typeof(string), typeof(TextField), string.Empty, propertyChanged: (b, _, _) => ((TextField)b).UpdateDecor());

    public static readonly BindableProperty KindProperty = BindableProperty.Create(
        nameof(Kind), typeof(TextFieldKind), typeof(TextField), TextFieldKind.Default, propertyChanged: (b, _, _) => ((TextField)b).UpdateKind());

    protected readonly BareEntry Entry = new();
    private readonly Border _frame = new() { StrokeShape = Ui.Corner("RadiusMd") };
    private readonly Label _leading = Ui.Icon(string.Empty, "Text.Icon.Sm", "ColorTextTertiary");
    private readonly Label _suffix = Ui.Text("Text.Tertiary");
    private readonly IconButton _clear = new IconButton { Glyph = Icons.X }.Styled("IconButton.Ghost.Sm");
    private readonly Label _error = Ui.Text("Text.Caption");
    protected readonly Grid Row = new()
    {
        ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)]
    };

    public TextField()
    {
        Row.ColumnSpacing = Ui.D("Space200");
        Row.Padding = Ui.Thick("PaddingInput");
        _frame.Dyn(BackgroundColorProperty, "ColorSurface2");
        _frame.Dyn(MinimumHeightRequestProperty, "SizeControlHeight");
        Entry.Style = null;
        Entry.FontFamily = "InterRegular";
        Entry.Dyn(Microsoft.Maui.Controls.Entry.FontSizeProperty, "TypeBodySize");
        Entry.Dyn(Microsoft.Maui.Controls.Entry.TextColorProperty, "ColorTextPrimary");
        Entry.Dyn(Microsoft.Maui.Controls.Entry.PlaceholderColorProperty, "ColorTextTertiary");
        Entry.Dyn(BackgroundColorProperty, "ColorTransparent");
        Entry.VerticalOptions = LayoutOptions.Center;
        Entry.TextChanged += (_, e) => Text = e.NewTextValue ?? string.Empty;
        Entry.Focused += (_, _) => UpdateFrame(true);
        Entry.Unfocused += (_, _) => UpdateFrame(false);
        _leading.VerticalOptions = _suffix.VerticalOptions = LayoutOptions.Center;
        ToolTipProperties.SetText(_clear, "Wyczyść");
        SemanticProperties.SetDescription(_clear, "Wyczyść pole");
        _clear.Clicked += (_, _) => { Text = string.Empty; Entry.Focus(); };
        Row.Add(_leading);
        Row.Add(Entry, 1);
        Row.Add(_suffix, 2);
        Row.Add(_clear, 3);
        _frame.Content = Row;
        _error.Dyn(Label.TextColorProperty, "ColorStatusDanger");
        Content = new VerticalStackLayout { Spacing = Ui.D("Space100"), Children = { _frame, _error } };
        UpdateDecor();
        UpdateFrame(false);
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Validation message („Podaj liczbę od 5 do 3600.”); empty = valid.</summary>
    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    public TextFieldKind Kind { get => (TextFieldKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public event EventHandler? Completed
    {
        add => Entry.Completed += value;
        remove => Entry.Completed -= value;
    }

    public new bool Focus() => Entry.Focus();

    protected virtual void OnTextChanged(string? text)
    {
        if (Entry.Text != text)
        {
            Entry.Text = text;
        }

        UpdateDecor();
    }

    protected void UpdateDecor()
    {
        _leading.Text = Glyph;
        _leading.IsVisible = !string.IsNullOrEmpty(Glyph);
        _suffix.Text = Suffix;
        _suffix.IsVisible = !string.IsNullOrEmpty(Suffix);
        _clear.IsVisible = !string.IsNullOrEmpty(Text) && Kind != TextFieldKind.Number;
        _error.Text = Error;
        _error.IsVisible = !string.IsNullOrEmpty(Error);
        UpdateFrame(Entry.IsFocused);
    }

    private void UpdateKind()
    {
        Entry.FontFamily = Kind switch { TextFieldKind.Mono => "MonoRegular", TextFieldKind.Number => "NumMedium", _ => "InterRegular" };
        Entry.Keyboard = Kind == TextFieldKind.Number ? Keyboard.Numeric : Keyboard.Default;
        Entry.HorizontalTextAlignment = Kind == TextFieldKind.Number ? TextAlignment.End : TextAlignment.Start;
        UpdateDecor();
    }

    private void UpdateFrame(bool focused)
    {
        _frame.StrokeThickness = Ui.D(focused ? "StrokeFocus" : "StrokeDefault");
        _frame.Dyn(Border.StrokeProperty, !string.IsNullOrEmpty(Error) ? "ColorStatusDangerBrush"
            : focused ? "ColorBorderFocusBrush" : "ColorBorderStrongBrush");
    }
}

/// <summary>
/// Search field (§4.2.2): search icon, clear, „/” hint when empty, 150 ms debounce into <see cref="Query"/>, Esc clears.
/// </summary>
public sealed class SearchField : TextField
{
    public static readonly BindableProperty QueryProperty = BindableProperty.Create(
        nameof(Query), typeof(string), typeof(SearchField), string.Empty, BindingMode.TwoWay, propertyChanged: (b, _, v) =>
        {
            var field = (SearchField)b;
            if (field.Text != (string?)v)
            {
                field.Text = (string?)v ?? string.Empty;
            }
        });

    private readonly Kbd _hint = new() { Text = "/", VerticalOptions = LayoutOptions.Center };
    private CancellationTokenSource? _debounce;

    public SearchField()
    {
        Glyph = Icons.Search;
        Row.Add(_hint, 4);
        Entry.Focused += (_, _) => _hint.IsVisible = false;
        Entry.Unfocused += (_, _) => _hint.IsVisible = string.IsNullOrEmpty(Text);
    }

    /// <summary>Debounced value used for filtering.</summary>
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }

    protected override async void OnTextChanged(string? text)
    {
        base.OnTextChanged(text);
        _hint.IsVisible = string.IsNullOrEmpty(text) && !Entry.IsFocused;
        _debounce?.Cancel();
        var debounce = _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(MotionTokens.SearchDebounce, debounce.Token);
            Query = text ?? string.Empty;
        }
        catch (TaskCanceledException)
        {
        }
    }
}

/// <summary>Field wrapper (§4.2.0): label above (or left on wide), field, description or error below.</summary>
[ContentProperty(nameof(Field))]
public sealed class FormRow : ContentView
{
    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(FormRow), string.Empty, propertyChanged: (b, _, v) => ((FormRow)b)._label.Text = (string)v);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description), typeof(string), typeof(FormRow), string.Empty, propertyChanged: (b, _, _) => ((FormRow)b).Update());

    public static readonly BindableProperty ErrorProperty = BindableProperty.Create(
        nameof(Error), typeof(string), typeof(FormRow), string.Empty, propertyChanged: (b, _, _) => ((FormRow)b).Update());

    public static readonly BindableProperty FieldProperty = BindableProperty.Create(
        nameof(Field), typeof(View), typeof(FormRow), null, propertyChanged: (b, _, v) => ((FormRow)b)._field.Content = v as View);

    public static readonly BindableProperty IsHorizontalProperty = BindableProperty.Create(
        nameof(IsHorizontal), typeof(bool), typeof(FormRow), false, propertyChanged: (b, _, _) => ((FormRow)b).Layout());

    public static readonly BindableProperty SavedProperty = BindableProperty.Create(
        nameof(Saved), typeof(bool), typeof(FormRow), false, propertyChanged: (b, _, v) => ((FormRow)b).ShowSaved((bool)v));

    private readonly Label _label = Ui.Text("Text.Label");
    private readonly Label _description = Ui.Text("Text.Caption");
    private readonly ContentView _field = new();
    private readonly HorizontalStackLayout _saved = new()
    {
        IsVisible = false,
        Spacing = Ui.D("Space100"),
        Children = { Ui.Icon(Icons.Check, "Text.Icon.Xs", "ColorStatusSuccess"), Ui.Text("Text.Caption", "Zapisano").Dyn(Microsoft.Maui.Controls.Label.TextColorProperty, "ColorStatusSuccess") }
    };

    public FormRow()
    {
        Layout();
        Update();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    public View? Field { get => (View?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }

    /// <summary>Settings on wide windows: label + description in a 280 column, field (max 400) on the right.</summary>
    public bool IsHorizontal { get => (bool)GetValue(IsHorizontalProperty); set => SetValue(IsHorizontalProperty, value); }

    /// <summary>Autosave confirmation (§5.10): set true after a save — „Zapisano” shows for 1.5 s.</summary>
    public bool Saved { get => (bool)GetValue(SavedProperty); set => SetValue(SavedProperty, value); }

    private void Layout()
    {
        var labelBlock = new VerticalStackLayout { Spacing = Ui.D("Space100"), Children = { new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { _label, _saved } } } };
        if (IsHorizontal)
        {
            labelBlock.Children.Add(_description);
            _field.MaximumWidthRequest = Ui.D("SizeFormField");
            var grid = new Grid
            {
                ColumnDefinitions = [new(new GridLength(Ui.D("SizeFormLabel"))), new(GridLength.Star)],
                ColumnSpacing = Ui.D("Space400")
            };
            grid.Add(labelBlock);
            grid.Add(_field, 1);
            Content = grid;
        }
        else
        {
            _field.MaximumWidthRequest = double.PositiveInfinity;
            Content = new VerticalStackLayout { Spacing = Ui.D("Space100"), Children = { labelBlock, _field, _description } };
        }
    }

    private void Update()
    {
        var hasError = !string.IsNullOrEmpty(Error);
        _description.Text = hasError ? Error : Description;
        _description.Dyn(Microsoft.Maui.Controls.Label.TextColorProperty, hasError ? "ColorStatusDanger" : "ColorTextTertiary");
        _description.IsVisible = !string.IsNullOrEmpty(_description.Text);
    }

    private async void ShowSaved(bool saved)
    {
        if (!saved)
        {
            return;
        }

        _saved.IsVisible = true;
        await Task.Delay(MotionTokens.FeedbackVisible);
        _saved.IsVisible = false;
        Saved = false;
    }
}

/// <summary>
/// All screen filters in one row (§4.7.4); wraps on narrow windows; „Wyczyść filtry” appears only when filters differ
/// from the defaults.
/// </summary>
[ContentProperty(nameof(Filters))]
public sealed class FilterBar : ContentView
{
    public static readonly BindableProperty ResetCommandProperty = BindableProperty.Create(nameof(ResetCommand), typeof(ICommand), typeof(FilterBar));

    public static readonly BindableProperty IsDirtyProperty = BindableProperty.Create(
        nameof(IsDirty), typeof(bool), typeof(FilterBar), false, propertyChanged: (b, _, v) => ((FilterBar)b)._reset.IsVisible = (bool)v);

    private readonly FlexLayout _flex = new()
    {
        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
        AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center
    };

    private readonly Button _reset = new Button { Text = "Wyczyść filtry", IsVisible = false, AutomationId = "FilterBar.Reset" }.Styled("Button.Link");

    public FilterBar()
    {
        Filters.CollectionChanged += (_, _) => Rebuild();
        _reset.Clicked += (_, _) => ResetCommand?.Execute(null);
        Content = _flex;
    }

    public ObservableCollection<View> Filters { get; } = [];

    public ICommand? ResetCommand { get => (ICommand?)GetValue(ResetCommandProperty); set => SetValue(ResetCommandProperty, value); }

    public bool IsDirty { get => (bool)GetValue(IsDirtyProperty); set => SetValue(IsDirtyProperty, value); }

    private void Rebuild()
    {
        _flex.Children.Clear();
        var gap = Ui.D("Space200");
        foreach (var view in Filters.Append(_reset))
        {
            view.Margin = new Thickness(0, 0, gap, gap);
            _flex.Children.Add(view);
        }
    }
}

/// <summary>Select (§4.2.3): field with value and chevron, opens a menu; lists longer than 8 get a search box.</summary>
public sealed class Select : Button
{
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IList), typeof(Select), null, propertyChanged: (b, _, _) => ((Select)b).Update());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex), typeof(int), typeof(Select), -1, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((Select)b).Update());

    public static readonly BindableProperty SelectedItemProperty = BindableProperty.Create(
        nameof(SelectedItem), typeof(object), typeof(Select), null, BindingMode.TwoWay, propertyChanged: (b, _, v) =>
        {
            var select = (Select)b;
            if (select.ItemsSource is { } items)
            {
                var index = items.IndexOf(v);
                if (index != select.SelectedIndex)
                {
                    select.SelectedIndex = index;
                }
            }
        });

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(Select), string.Empty, propertyChanged: (b, _, _) => ((Select)b).Update());

    public Select()
    {
        this.Styled("Button.Secondary");
        this.Dyn(BackgroundColorProperty, "ColorSurface2").Dyn(BorderColorProperty, "ColorBorderStrong");
        FontFamily = "InterRegular";
        this.Dyn(FontSizeProperty, "TypeBodySize");
        Padding = Ui.Thick("PaddingInput");
        MinimumWidthRequest = Ui.D("SizeSelectMinWidth");
        ContentLayout = new ButtonContentLayout(ButtonContentLayout.ImagePosition.Right, Ui.D("Space200"));
        ImageSource = new FontImageSource { FontFamily = "Lucide", Glyph = Icons.ChevronDown, Size = Ui.D("IconSm") }
            .Dyn(FontImageSource.ColorProperty, "ColorTextSecondary");
        Clicked += (_, _) => Open();
        Update();
    }

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private void Open()
    {
        if (ItemsSource is not { Count: > 0 } items)
        {
            return;
        }

        var list = new VerticalStackLayout();
        void Fill(string filter)
        {
            list.Children.Clear();
            var entries = items.Cast<object?>()
                .Select((item, index) => (Text: item?.ToString() ?? "", Index: index))
                .Where(entry => filter.Length == 0 || Normalize(entry.Text).Contains(Normalize(filter), StringComparison.OrdinalIgnoreCase))
                .Select(entry => new MenuEntry(entry.Text, () => Choose(entry.Index), IsChecked: entry.Index == SelectedIndex));
            list.Children.Add(new Menu(entries));
        }

        Fill(string.Empty);
        if (items.Count > 8)
        {
            var search = new SearchField { Placeholder = "Szukaj", Margin = Ui.Thick("PaddingMenu") };
            search.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchField.Query))
                {
                    Fill(search.Query);
                }
            };
            PopoverService.Show(this, new VerticalStackLayout { Children = { search, list } }, Math.Max(Width, Ui.D("SizeSelectMinWidth")));
            search.Focus();
        }
        else
        {
            PopoverService.Show(this, list);
        }
    }

    private void Choose(int index)
    {
        SelectedIndex = index;
        SelectedItem = ItemsSource is { } items && index >= 0 && index < items.Count ? items[index] : null;
    }

    private void Update()
    {
        var hasValue = ItemsSource is { } items && SelectedIndex >= 0 && SelectedIndex < items.Count;
        Text = hasValue ? ItemsSource![SelectedIndex]?.ToString() : Placeholder;
        this.Dyn(TextColorProperty, hasValue ? "ColorTextPrimary" : "ColorTextTertiary");
        SemanticProperties.SetDescription(this, $"{Placeholder}: {Text}");
    }

    /// <summary>Case- and diacritics-insensitive matching („zrodla” → „Źródła”).</summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.Replace('ł', 'l').Replace('Ł', 'L').Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }
}

/// <summary>Split button (§4.1.3): main action + chevron opening the remaining options.</summary>
public sealed class SplitButton : HorizontalStackLayout
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(SplitButton), string.Empty, propertyChanged: (b, _, v) => ((SplitButton)b)._main.Text = (string)v);

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command), typeof(ICommand), typeof(SplitButton), propertyChanged: (b, _, v) => ((SplitButton)b)._main.Command = v as ICommand);

    private readonly Button _main = new Button().Styled("Button.Secondary");
    private readonly IconButton _more = new IconButton { Glyph = Icons.ChevronDown }.Styled("IconButton.Secondary");

    public SplitButton()
    {
        Spacing = 0;
        _main.ImageSource = new FontImageSource { FontFamily = "Lucide", Glyph = Icons.Download, Size = Ui.D("IconSm") }
            .Dyn(FontImageSource.ColorProperty, "ColorTextPrimary");
        ToolTipProperties.SetText(_more, "Więcej opcji");
        SemanticProperties.SetDescription(_more, "Więcej opcji eksportu");
        _more.Clicked += (_, _) => Menu.Show(_more, Options, Ui.D("SizeSelectMinWidth"));
        Children.Add(_main);
        Children.Add(_more);
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>Menu entries behind the chevron.</summary>
    public IList<MenuEntry> Options { get; set; } = [];

    /// <summary>Opens the option menu (Ctrl+E).</summary>
    public void OpenMenu() => Menu.Show(_more, Options, Ui.D("SizeSelectMinWidth"));
}

/// <summary>
/// Date range picker (§4.2.4): field with the range text → popover with presets (applied at once) and a month calendar
/// with „Od”/„Do” fields and „Zastosuj” for a custom range.
/// </summary>
public sealed class DateRangePicker : Button
{
    public static readonly BindableProperty PresetsProperty = BindableProperty.Create(nameof(Presets), typeof(IList), typeof(DateRangePicker));

    public static readonly BindableProperty PresetIndexProperty = BindableProperty.Create(
        nameof(PresetIndex), typeof(int), typeof(DateRangePicker), 0, BindingMode.TwoWay);

    /// <summary>Index in <see cref="Presets"/> meaning „Własny zakres”.</summary>
    public static readonly BindableProperty CustomIndexProperty = BindableProperty.Create(nameof(CustomIndex), typeof(int), typeof(DateRangePicker), -1);

    public static readonly BindableProperty SinceProperty = BindableProperty.Create(
        nameof(Since), typeof(DateTime), typeof(DateRangePicker), DateTime.Today, BindingMode.TwoWay);

    public static readonly BindableProperty UntilProperty = BindableProperty.Create(
        nameof(Until), typeof(DateTime), typeof(DateRangePicker), DateTime.Today, BindingMode.TwoWay);

    public static readonly BindableProperty DisplayTextProperty = BindableProperty.Create(
        nameof(DisplayText), typeof(string), typeof(DateRangePicker), string.Empty, propertyChanged: (b, _, v) => ((DateRangePicker)b).Text = (string)v);

    public DateRangePicker()
    {
        this.Styled("Button.Secondary");
        ContentLayout = new ButtonContentLayout(ButtonContentLayout.ImagePosition.Left, Ui.D("Space200"));
        ImageSource = new FontImageSource { FontFamily = "Lucide", Glyph = Icons.Calendar, Size = Ui.D("IconSm") }
            .Dyn(FontImageSource.ColorProperty, "ColorTextSecondary");
        SemanticProperties.SetDescription(this, "Zakres dat");
        Clicked += (_, _) => Open();
    }

    public IList? Presets { get => (IList?)GetValue(PresetsProperty); set => SetValue(PresetsProperty, value); }

    public int PresetIndex { get => (int)GetValue(PresetIndexProperty); set => SetValue(PresetIndexProperty, value); }

    public int CustomIndex { get => (int)GetValue(CustomIndexProperty); set => SetValue(CustomIndexProperty, value); }

    public DateTime Since { get => (DateTime)GetValue(SinceProperty); set => SetValue(SinceProperty, value); }

    public DateTime Until { get => (DateTime)GetValue(UntilProperty); set => SetValue(UntilProperty, value); }

    /// <summary>Text shown in the field, e.g. „1–26 lis 2026”.</summary>
    public string DisplayText { get => (string)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }

    private void Open()
    {
        var presets = new VerticalStackLayout();
        var index = 0;
        foreach (var preset in Presets ?? Array.Empty<object>())
        {
            var position = index++;
            if (position == CustomIndex)
            {
                continue;
            }

            presets.Children.Add(new Menu([new MenuEntry(preset?.ToString(), () => PresetIndex = position, IsChecked: position == PresetIndex)]));
        }

        var calendar = new RangeCalendar(Since, Until);
        var from = new TextField { Placeholder = "dd.mm.rrrr", Text = Fmt.DateField(Since), Kind = TextFieldKind.Number };
        var to = new TextField { Placeholder = "dd.mm.rrrr", Text = Fmt.DateField(Until), Kind = TextFieldKind.Number };
        calendar.RangeChanged += (_, _) =>
        {
            from.Text = Fmt.DateField(calendar.Start);
            to.Text = Fmt.DateField(calendar.End);
        };
        var apply = new Button { Text = "Zastosuj" }.Styled("Button.Primary");
        apply.Clicked += (_, _) =>
        {
            if (!TryParse(from.Text, out var start))
            {
                from.Error = "Podaj datę w formacie dd.mm.rrrr.";
                return;
            }

            if (!TryParse(to.Text, out var end) || end < start)
            {
                to.Error = "Podaj datę nie wcześniejszą niż „Od”.";
                return;
            }

            Since = start;
            Until = end;
            if (CustomIndex >= 0)
            {
                PresetIndex = CustomIndex;
            }

            PopoverService.Close();
        };

        var footer = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        };
        footer.Add(new FormRow { Label = "Od", Field = from });
        footer.Add(new FormRow { Label = "Do", Field = to }, 1);
        apply.VerticalOptions = LayoutOptions.End;
        footer.Add(apply, 2);

        var body = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = Ui.D("Space400") };
        body.Add(presets);
        body.Add(new VerticalStackLayout { Spacing = Ui.D("Space300"), Children = { calendar, footer } }, 1);
        PopoverService.Show(this, body, Ui.D("SizeDialogMd"));
    }

    private static bool TryParse(string text, out DateTime date) =>
        DateTime.TryParseExact(text?.Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
}

/// <summary>One-month calendar with range selection (first click = start, second = end).</summary>
internal sealed class RangeCalendar : VerticalStackLayout
{
    private static readonly string[] Days = ["pn", "wt", "śr", "cz", "pt", "sb", "nd"];
    private readonly Grid _grid = new();
    private readonly Label _title = Ui.Text("Text.TitleCard");
    private DateTime _month;
    private bool _pickingEnd;

    public RangeCalendar(DateTime start, DateTime end)
    {
        Start = start.Date;
        End = end.Date;
        _month = new DateTime(End.Year, End.Month, 1);
        Spacing = Ui.D("Space200");
        var previous = new IconButton { Glyph = Icons.ChevronLeft }.Styled("IconButton.Ghost.Sm");
        var next = new IconButton { Glyph = Icons.ChevronRight }.Styled("IconButton.Ghost.Sm");
        ToolTipProperties.SetText(previous, "Poprzedni miesiąc");
        ToolTipProperties.SetText(next, "Następny miesiąc");
        previous.Clicked += (_, _) => { _month = _month.AddMonths(-1); Render(); };
        next.Clicked += (_, _) => { _month = _month.AddMonths(1); Render(); };
        var header = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        _title.HorizontalTextAlignment = TextAlignment.Center;
        _title.VerticalOptions = LayoutOptions.Center;
        header.Add(previous);
        header.Add(_title, 1);
        header.Add(next, 2);
        Children.Add(header);
        Children.Add(_grid);
        for (var i = 0; i < 7; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Ui.D("SizeCalendarCell"))));
        }

        Render();
    }

    public event EventHandler? RangeChanged;

    public DateTime Start { get; private set; }

    public DateTime End { get; private set; }

    private void Render()
    {
        _title.Text = Fmt.Month(_month);
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        for (var row = 0; row < 7; row++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(Ui.D("SizeCalendarCell"))));
        }

        for (var day = 0; day < 7; day++)
        {
            var label = Ui.Text("Text.Caption", Days[day]);
            label.HorizontalTextAlignment = TextAlignment.Center;
            label.VerticalTextAlignment = TextAlignment.Center;
            _grid.Add(label, day, 0);
        }

        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        var today = DateTime.Today;
        for (var d = 1; d <= days; d++)
        {
            var date = new DateTime(_month.Year, _month.Month, d);
            var cell = offset + d - 1;
            var inRange = date >= Start && date <= End;
            var edge = date == Start || date == End;
            var label = new Label { Text = d.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Styled("Text.TableNum");
            label.HorizontalTextAlignment = TextAlignment.Center;
            label.VerticalTextAlignment = TextAlignment.Center;
            label.Dyn(Label.TextColorProperty, edge ? "ColorTextOnAction" : date > today ? "ColorTextTertiary" : "ColorTextPrimary");
            var box = Ui.Box(label, edge ? "ColorActionPrimary" : inRange ? "ColorActionSubtle" : "ColorTransparent", "RadiusSm");
            if (date == today && !edge)
            {
                box.StrokeThickness = Ui.D("StrokeDefault");
                box.Dyn(Border.StrokeProperty, "ColorBorderFocusBrush");
            }

            SemanticProperties.SetDescription(box, Fmt.LongDate(date) + (inRange ? ", w zakresie" : ""));
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Pick(date);
            box.GestureRecognizers.Add(tap);
            _grid.Add(box, cell % 7, cell / 7 + 1);
        }
    }

    private void Pick(DateTime date)
    {
        if (!_pickingEnd || date < Start)
        {
            Start = End = date;
            _pickingEnd = true;
        }
        else
        {
            End = date;
            _pickingEnd = false;
        }

        Render();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Horizontal tabs (§4.7.7): active label primary with a 2 dp underline; ←/→ handled by focus order.</summary>
public sealed class Tabs : ContentView
{
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IList), typeof(Tabs), null, propertyChanged: (b, _, _) => ((Tabs)b).Rebuild());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex), typeof(int), typeof(Tabs), 0, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((Tabs)b).Rebuild());

    private readonly HorizontalStackLayout _row = new() { Spacing = 0 };

    public Tabs()
    {
        Content = new Grid { Children = { new BoxView { VerticalOptions = LayoutOptions.End }.Styled("Divider"), _row } };
    }

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private void Rebuild()
    {
        _row.Children.Clear();
        var index = 0;
        var count = ItemsSource?.Count ?? 0;
        foreach (var item in ItemsSource ?? Array.Empty<object>())
        {
            var position = index++;
            var active = position == SelectedIndex;
            var button = new Button { Text = item?.ToString() }.Styled("Button.Ghost");
            button.Dyn(Button.TextColorProperty, active ? "ColorTextPrimary" : "ColorTextSecondary");
            button.Clicked += (_, _) => SelectedIndex = position;
            SemanticProperties.SetDescription(button, $"{item}, karta {position + 1} z {count}{(active ? ", wybrana" : "")}");
            var underline = new BoxView { HeightRequest = Ui.D("SizeTabUnderline"), IsVisible = active }.Dyn(BoxView.ColorProperty, "ColorActionPrimary");
            _row.Children.Add(new VerticalStackLayout { Children = { button, underline } });
        }
    }
}

/// <summary>Vertical list of Settings sub-pages (§4.7.7) — NavItem rows without icons.</summary>
public sealed class SettingsNav : VerticalStackLayout
{
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IList), typeof(SettingsNav), null, propertyChanged: (b, _, _) => ((SettingsNav)b).Rebuild());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex), typeof(int), typeof(SettingsNav), 0, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((SettingsNav)b).Rebuild());

    public SettingsNav()
    {
        Spacing = Ui.D("Space050");
        WidthRequest = Ui.D("SizeSettingsNav");
    }

    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        var index = 0;
        foreach (var item in ItemsSource ?? Array.Empty<object>())
        {
            var position = index++;
            var nav = new NavItem { Label = item?.ToString() ?? "", IsActive = position == SelectedIndex }.Styled("NavItem");
            nav.Clicked += (_, _) => SelectedIndex = position;
            Children.Add(nav);
        }
    }
}
