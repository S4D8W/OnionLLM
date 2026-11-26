using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls;

/// <summary>
/// Command palette (DESIGN.md §4.6.7): centred in the top fifth of the window, 640 wide (max 90%), scrim 50%,
/// search field without outline, grouped results with bold matched characters, key hints in the footer.
/// Keys (↑↓, PgUp/PgDn, Enter, Esc, Tab trap) arrive from <see cref="ShortcutService.PaletteKeyPressed"/>.
/// </summary>
public sealed class CommandPalette : Grid
{
    private const int PageStep = 5;

    private readonly BoxView _scrim = new();
    private readonly Border _card = new Border().Styled("Surface.Dialog");
    private readonly BareEntry _entry = new();
    private readonly VerticalStackLayout _list = new();
    private readonly ScrollView _scroll = new();
    private readonly List<(PaletteResult Result, Border Row)> _rows = [];
    private int _selected;

    public CommandPalette()
    {
        IsVisible = false;
        _scrim.Dyn(BoxView.ColorProperty, "ColorScrim");
        _scrim.Opacity = Ui.D("OpacityScrimPalette");
        var close = new TapGestureRecognizer();
        close.Tapped += (_, _) => CommandPaletteService.Close();
        _scrim.GestureRecognizers.Add(close);

        _entry.Style = null;
        _entry.FontFamily = "InterRegular";
        _entry.FontSize = Ui.D("TypePaletteInputSize");
        _entry.Placeholder = "Wpisz polecenie lub szukaj…";
        _entry.Dyn(Entry.TextColorProperty, "ColorTextPrimary");
        _entry.Dyn(Entry.PlaceholderColorProperty, "ColorTextTertiary");
        _entry.Dyn(BackgroundColorProperty, "ColorTransparent");
        _entry.VerticalOptions = LayoutOptions.Center;
        _entry.AutomationId = "Palette.Search";
        SemanticProperties.SetDescription(_entry, "Paleta poleceń: wpisz polecenie lub szukaj");
        _entry.TextChanged += (_, _) => Rebuild();

        var search = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = Ui.D("Space300"),
            Padding = new Thickness(Ui.D("Space400"), Ui.D("Space300"))
        };
        search.Add(Ui.Icon(Icons.Search, "Text.Icon", "ColorTextTertiary"));
        search.Add(_entry, 1);

        _list.Padding = Ui.Thick("PaddingMenu");
        _scroll.Content = _list;
        _scroll.MaximumHeightRequest = Ui.D("SizePaletteListMax");

        var footer = new HorizontalStackLayout
        {
            Spacing = Ui.D("Space400"),
            Padding = new Thickness(Ui.D("Space400"), Ui.D("Space200"))
        };
        footer.Children.Add(Hint("↑↓", "wybierz"));
        footer.Children.Add(Hint("↵", "otwórz"));
        footer.Children.Add(Hint("Esc", "zamknij"));

        _card.Padding = Ui.Thick("ThicknessZero");
        _card.HorizontalOptions = LayoutOptions.Center;
        _card.VerticalOptions = LayoutOptions.Start;
        _card.WidthRequest = Ui.D("SizePaletteWidth");
        _card.Content = new VerticalStackLayout
        {
            Children = { search, new BoxView().Styled("Divider"), _scroll, new BoxView().Styled("Divider"), footer }
        };
        SemanticProperties.SetDescription(_card, "Paleta poleceń");

        Children.Add(_scrim);
        Children.Add(_card);
        SizeChanged += (_, _) => Place();

        CommandPaletteService.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await UpdateAsync());
        CommandPaletteService.IndexUpdated += (_, _) =>
        {
            if (IsVisible && _entry.Text?.Trim().Length > 0)
            {
                Rebuild();
            }
        };
        ShortcutService.PaletteKeyPressed += (_, key) => MainThread.BeginInvokeOnMainThread(async () => await OnKeyAsync(key));
    }

    private void Place()
    {
        if (Width <= 0)
        {
            return;
        }

        _card.WidthRequest = Math.Min(Ui.D("SizePaletteWidth"), Width * Ui.D("RatioPaletteMaxWidth"));
        _card.Margin = new Thickness(0, Height * Ui.D("RatioPaletteTop"), 0, 0);
    }

    private async Task UpdateAsync()
    {
        if (!CommandPaletteService.IsOpen)
        {
            if (IsVisible)
            {
                IsVisible = false;
                ShortcutService.RestoreFocus();
            }

            return;
        }

        ShortcutService.SaveFocus();
        _entry.Text = string.Empty;
        Rebuild();
        IsVisible = true;
        Place();

        // M7: scale 0.96 → 1 + fade (base, enter); scrim fades in. Reduced motion → fade only.
        _card.Opacity = 0;
        _scrim.Opacity = 0;
        _card.Scale = MotionTokens.AllowsTransforms ? Ui.D("ScaleDialogEnter") : 1;
        _entry.Focus();
        await Task.WhenAll(
            _card.FadeToAsync(1, MotionTokens.Base, OnionEasing.Enter),
            _card.ScaleToAsync(1, MotionTokens.Base, OnionEasing.Enter),
            _scrim.FadeToAsync(Ui.D("OpacityScrimPalette"), MotionTokens.Base, OnionEasing.Enter));
        _entry.Focus();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        _selected = 0;
        var query = _entry.Text ?? string.Empty;
        var sections = CommandPaletteService.Search(query);
        if (sections.Count == 0)
        {
            _list.Children.Add(new EmptyState
            {
                Variant = EmptyStateVariant.Inline,
                Glyph = Icons.SearchX,
                Title = $"Brak wyników dla „{query.Trim()}”",
                Description = "Spróbuj innej nazwy. Prefiksy: > akcje, # sesje, @ projekty."
            });
            return;
        }

        foreach (var section in sections)
        {
            var header = Ui.Text("Text.Overline", section.Header.ToUpperInvariant());
            header.Padding = Ui.Thick("PaddingNavItem");
            header.Margin = new Thickness(0, Ui.D("Space200"), 0, Ui.D("Space100"));
            SemanticProperties.SetHeadingLevel(header, SemanticHeadingLevel.Level3);
            _list.Children.Add(header);
            foreach (var result in section.Results)
            {
                var row = Row(result, section.Header, _rows.Count);
                _rows.Add((result, row));
                _list.Children.Add(row);
            }
        }

        Highlight(announce: false);
    }

    private Border Row(PaletteResult result, string group, int index)
    {
        var item = result.Item;
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space300")
        };
        View leading = item.SourceId is { } sourceId
            ? new SourceBadge { SourceId = sourceId, Initial = Initial(item), Size = BadgeSize.Xs, VerticalOptions = LayoutOptions.Center }
            : Ui.Icon(item.Glyph, "Text.Icon.Sm");
        grid.Add(leading);

        var title = new Label { FormattedText = Highlighted(item.Title, result.Positions) }.Styled("Text.Body");
        title.LineBreakMode = LineBreakMode.TailTruncation;
        var texts = new HorizontalStackLayout { Spacing = Ui.D("Space200"), VerticalOptions = LayoutOptions.Center };
        texts.Children.Add(title);
        if (item.Subtitle.Length > 0)
        {
            var subtitle = Ui.Text("Text.Tertiary", item.Subtitle);
            subtitle.LineBreakMode = LineBreakMode.TailTruncation;
            subtitle.VerticalOptions = LayoutOptions.Center;
            texts.Children.Add(subtitle);
        }

        grid.Add(texts, 1);
        if (item.Shortcut is { Length: > 0 } shortcut)
        {
            grid.Add(new Kbd { Text = shortcut, VerticalOptions = LayoutOptions.Center }, 2);
        }

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = Ui.Corner("RadiusSm"),
            Padding = Ui.Thick("PaddingNavItem"),
            Content = grid
        }.Dyn(HeightRequestProperty, "SizeRowHeightDense");
        row.AutomationId = "Palette.Item." + item.Id;
        SemanticProperties.SetDescription(row, string.Join(", ", new[] { item.Title, item.Subtitle, group, item.Shortcut ?? "" }.Where(text => text.Length > 0)));
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CommandPaletteService.ExecuteAsync(item);
        row.GestureRecognizers.Add(tap);
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) =>
        {
            _selected = index;
            Highlight(announce: false);
        };
        row.GestureRecognizers.Add(pointer);
        return row;
    }

    private static string Initial(PaletteItem item)
    {
        var name = item.SourceId is { } id ? AppState.Engine.FindSource(id)?.DisplayName ?? id : item.Title;
        return name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    }

    /// <summary>Matched characters in SemiBold + primary text colour (§4.6.7).</summary>
    private static FormattedString Highlighted(string text, IReadOnlyList<int> positions)
    {
        var formatted = new FormattedString();
        var set = positions.ToHashSet();
        var start = 0;
        while (start < text.Length)
        {
            var bold = set.Contains(start);
            var end = start;
            while (end < text.Length && set.Contains(end) == bold)
            {
                end++;
            }

            var span = new Span { Text = text[start..end] };
            if (bold)
            {
                span.FontFamily = "InterSemiBold";
                span.Dyn(Span.TextColorProperty, "ColorActionOnSubtle");
            }

            formatted.Spans.Add(span);
            start = end;
        }

        return formatted;
    }

    private void Highlight(bool announce)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Row.Dyn(BackgroundColorProperty, i == _selected ? "ColorStateSelected" : "ColorTransparent");
        }

        if (announce && _selected < _rows.Count)
        {
            var item = _rows[_selected].Result.Item;
            SemanticScreenReader.Announce(item.Subtitle.Length > 0 ? $"{item.Title}, {item.Subtitle}" : item.Title);
        }
    }

    private async Task OnKeyAsync(PaletteKey key)
    {
        if (!IsVisible)
        {
            return;
        }

        switch (key)
        {
            case PaletteKey.Escape:
                CommandPaletteService.Close();
                return;
            case PaletteKey.Tab:
                _entry.Focus();
                return;
            case PaletteKey.Enter when _selected < _rows.Count:
                await CommandPaletteService.ExecuteAsync(_rows[_selected].Result.Item);
                return;
            case PaletteKey.Enter:
                return;
        }

        if (_rows.Count == 0)
        {
            return;
        }

        _selected = key switch
        {
            PaletteKey.Up => Math.Max(0, _selected - 1),
            PaletteKey.Down => Math.Min(_rows.Count - 1, _selected + 1),
            PaletteKey.PageUp => Math.Max(0, _selected - PageStep),
            _ => Math.Min(_rows.Count - 1, _selected + PageStep)
        };
        Highlight(announce: true);
        await _scroll.ScrollToAsync(_rows[_selected].Row, ScrollToPosition.MakeVisible, false);
    }

    private static View Hint(string key, string text)
    {
        var hint = new HorizontalStackLayout { Spacing = Ui.D("Space150") };
        hint.Children.Add(new Kbd { Text = key, VerticalOptions = LayoutOptions.Center });
        var label = Ui.Text("Text.Caption", text);
        label.VerticalOptions = LayoutOptions.Center;
        hint.Children.Add(label);
        return hint;
    }
}
