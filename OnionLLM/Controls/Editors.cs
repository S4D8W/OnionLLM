using System.Collections.ObjectModel;
using OnionLLM.Resources;

namespace OnionLLM.Controls;

/// <summary>Editor without the native frame — <see cref="CodeEditor"/> draws the field.</summary>
public sealed class BareEditor : Editor
{
}

/// <summary>
/// Multi-line mono editor (DESIGN.md §4.2.9): field look, min. 6 lines and auto height, format help and line validation
/// below („Linia 3: brak znaku „=””).
/// </summary>
public sealed class CodeEditor : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(CodeEditor), string.Empty, BindingMode.TwoWay, propertyChanged: (b, _, v) =>
        {
            var editor = (CodeEditor)b;
            if (editor._editor.Text != (string?)v)
            {
                editor._editor.Text = (string?)v;
            }
        });

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(CodeEditor), string.Empty, propertyChanged: (b, _, v) => ((CodeEditor)b)._editor.Placeholder = (string)v);

    public static readonly BindableProperty HelpProperty = BindableProperty.Create(
        nameof(Help), typeof(string), typeof(CodeEditor), string.Empty, propertyChanged: (b, _, _) => ((CodeEditor)b).Update());

    public static readonly BindableProperty ErrorProperty = BindableProperty.Create(
        nameof(Error), typeof(string), typeof(CodeEditor), string.Empty, propertyChanged: (b, _, _) => ((CodeEditor)b).Update());

    private readonly BareEditor _editor = new();
    private readonly Border _frame = new() { StrokeShape = Ui.Corner("RadiusMd") };
    private readonly Label _help = Ui.Text("Text.Caption");

    public CodeEditor()
    {
        _editor.Styled("Editor.Code");
        _editor.Dyn(BackgroundColorProperty, "ColorTransparent");
        _editor.MinimumHeightRequest = Ui.D("SizeEditorMinHeight");
        _editor.TextChanged += (_, e) => Text = e.NewTextValue ?? string.Empty;
        _editor.Focused += (_, _) => UpdateFrame(true);
        _editor.Unfocused += (_, _) => UpdateFrame(false);
        _frame.Dyn(BackgroundColorProperty, "ColorSurface2");
        _frame.Padding = Ui.Thick("PaddingInput");
        _frame.Content = _editor;
        Content = new VerticalStackLayout { Spacing = Ui.D("Space100"), Children = { _frame, _help } };
        Update();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    /// <summary>Format description with an example.</summary>
    public string Help { get => (string)GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    private void Update()
    {
        var hasError = !string.IsNullOrEmpty(Error);
        _help.Text = hasError ? Error : Help;
        _help.Dyn(Label.TextColorProperty, hasError ? "ColorStatusDanger" : "ColorTextTertiary");
        _help.IsVisible = !string.IsNullOrEmpty(_help.Text);
        UpdateFrame(_editor.IsFocused);
    }

    private void UpdateFrame(bool focused)
    {
        _frame.StrokeThickness = Ui.D(focused ? "StrokeFocus" : "StrokeDefault");
        _frame.Dyn(Border.StrokeProperty, !string.IsNullOrEmpty(Error) ? "ColorStatusDangerBrush"
            : focused ? "ColorBorderFocusBrush" : "ColorBorderStrongBrush");
    }
}

/// <summary>
/// Editable key → value list (§4.2.10) bound to a text form (<c>klucz=wartość</c> joined by <see cref="Separator"/>);
/// „Edytuj jako tekst” switches to a <see cref="CodeEditor"/> with the same data.
/// </summary>
public sealed class KeyValueEditor : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(KeyValueEditor), string.Empty, BindingMode.TwoWay, propertyChanged: (b, _, _) => ((KeyValueEditor)b).Load());

    public static readonly BindableProperty SeparatorProperty = BindableProperty.Create(
        nameof(Separator), typeof(string), typeof(KeyValueEditor), "\n", propertyChanged: (b, _, _) => ((KeyValueEditor)b).Load());

    public static readonly BindableProperty KeyPlaceholderProperty = BindableProperty.Create(nameof(KeyPlaceholder), typeof(string), typeof(KeyValueEditor), "klucz");

    public static readonly BindableProperty ValuePlaceholderProperty = BindableProperty.Create(nameof(ValuePlaceholder), typeof(string), typeof(KeyValueEditor), "wartość");

    public static readonly BindableProperty AddTextProperty = BindableProperty.Create(
        nameof(AddText), typeof(string), typeof(KeyValueEditor), "Dodaj alias", propertyChanged: (b, _, v) => ((KeyValueEditor)b)._add.Text = (string)v);

    public static readonly BindableProperty HelpProperty = BindableProperty.Create(
        nameof(Help), typeof(string), typeof(KeyValueEditor), string.Empty, propertyChanged: (b, _, v) => ((KeyValueEditor)b)._code.Help = (string)v);

    private readonly ObservableCollection<(string Key, string Value)> _pairs = [];
    private readonly VerticalStackLayout _rows = new();
    private readonly Button _add = new Button { Text = "Dodaj alias" }.Styled("Button.Ghost.Sm");
    private readonly Button _mode = new Button { Text = "Edytuj jako tekst" }.Styled("Button.Link");
    private readonly CodeEditor _code = new();
    private readonly VerticalStackLayout _list = null!;
    private bool _textMode;
    private bool _writing;

    public KeyValueEditor()
    {
        _rows.Spacing = Ui.D("Space200");
        _add.ImageSource = new FontImageSource { FontFamily = "Lucide", Glyph = Icons.Plus, Size = Ui.D("IconSm") }
            .Dyn(FontImageSource.ColorProperty, "ColorTextSecondary");
        _add.HorizontalOptions = LayoutOptions.Start;
        _add.Clicked += (_, _) =>
        {
            _pairs.Add((string.Empty, string.Empty));
            RenderRows();
        };
        _mode.Clicked += (_, _) =>
        {
            _textMode = !_textMode;
            _mode.Text = _textMode ? "Edytuj jako listę" : "Edytuj jako tekst";
            if (_textMode)
            {
                _code.Text = string.Join("\n", _pairs.Where(p => p.Key.Length > 0).Select(p => $"{p.Key}={p.Value}"));
            }
            else
            {
                Write(_code.Text.Replace("\r", string.Empty).Replace("\n", Separator));
                Load();
            }

            _list.IsVisible = !_textMode;
            _code.IsVisible = _textMode;
        };
        _code.IsVisible = false;
        _code.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CodeEditor.Text) && _textMode)
            {
                _code.Error = Validate(_code.Text);
                if (_code.Error.Length == 0)
                {
                    Write(string.Join(Separator, _code.Text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
                }
            }
        };
        _list = new VerticalStackLayout { Spacing = Ui.D("Space200"), Children = { _rows, _add } };
        Content = new VerticalStackLayout { Spacing = Ui.D("Space200"), Children = { _list, _code, _mode } };
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Pair separator in <see cref="Text"/>: „\n” (model aliases) or „,” (project aliases).</summary>
    public string Separator { get => (string)GetValue(SeparatorProperty); set => SetValue(SeparatorProperty, value); }

    public string KeyPlaceholder { get => (string)GetValue(KeyPlaceholderProperty); set => SetValue(KeyPlaceholderProperty, value); }

    public string ValuePlaceholder { get => (string)GetValue(ValuePlaceholderProperty); set => SetValue(ValuePlaceholderProperty, value); }

    public string AddText { get => (string)GetValue(AddTextProperty); set => SetValue(AddTextProperty, value); }

    public string Help { get => (string)GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    /// <summary>„Linia 3: brak znaku „=””; empty when valid.</summary>
    internal static string Validate(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0 && !lines[i].Contains('='))
            {
                return $"Linia {i + 1}: brak znaku „=”.";
            }
        }

        return string.Empty;
    }

    private void Load()
    {
        if (_writing)
        {
            return;
        }

        _pairs.Clear();
        var separator = string.IsNullOrEmpty(Separator) ? "\n" : Separator;
        foreach (var part in (Text ?? string.Empty).Replace("\r", string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            _pairs.Add(index < 0 ? (part.Trim(), string.Empty) : (part[..index].Trim(), part[(index + 1)..].Trim()));
        }

        RenderRows();
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        for (var i = 0; i < _pairs.Count; i++)
        {
            var index = i;
            var key = new TextField { Text = _pairs[i].Key, Placeholder = KeyPlaceholder, Kind = TextFieldKind.Mono };
            var value = new TextField { Text = _pairs[i].Value, Placeholder = ValuePlaceholder };
            key.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TextField.Text)) { Update(index, key.Text, null); } };
            value.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TextField.Text)) { Update(index, null, value.Text); } };
            var remove = new IconButton { Glyph = Icons.Trash2 }.Styled("IconButton.Ghost.Sm");
            remove.Dyn(Button.TextColorProperty, "ColorStatusDanger");
            ToolTipProperties.SetText(remove, "Usuń");
            SemanticProperties.SetDescription(remove, "Usuń wpis " + _pairs[i].Key);
            remove.Clicked += (_, _) =>
            {
                _pairs.RemoveAt(index);
                Commit();
                RenderRows();
            };
            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = Ui.D("Space200")
            };
            row.Add(key);
            row.Add(Ui.Icon(Icons.ArrowRight, "Text.Icon.Sm", "ColorTextTertiary"), 1);
            row.Add(value, 2);
            row.Add(remove, 3);
            _rows.Children.Add(row);
        }
    }

    private void Update(int index, string? key, string? value)
    {
        if (index >= _pairs.Count)
        {
            return;
        }

        var current = _pairs[index];
        _pairs[index] = (key ?? current.Key, value ?? current.Value);
        Commit();
    }

    private void Commit() =>
        Write(string.Join(Separator, _pairs.Where(pair => pair.Key.Trim().Length > 0).Select(pair => $"{pair.Key.Trim()}={pair.Value.Trim()}")));

    private void Write(string text)
    {
        _writing = true;
        Text = text;
        _writing = false;
    }
}
