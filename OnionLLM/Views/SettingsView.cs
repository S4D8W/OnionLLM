using OnionLLM.Controls;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

/// <summary>
/// Ustawienia (DESIGN.md §6.9): PageHeader with „Szukaj ustawienia” → SettingsNav (220) | sub-page (FormRows, max 760).
/// Autosave (§5.10) — no „Zapisz” button. „Powiadomienia i budżety” need data/platform work and are not rendered.
/// </summary>
public sealed class SettingsView : ContentView, IScreenView
{
    private static readonly (string Title, string Keywords)[] Pages =
    [
        ("Ogólne", "strefa czasowa początek tygodnia uruchamiaj przy starcie autostart"),
        ("Wygląd", "motyw jasny ciemny gęstość kompaktowa animacje ruch"),
        ("Okno i zasobnik", "zawsze na wierzchu okno"),
        ("Obliczenia", "tryb kosztu długość bloku limit tokenów odświeżanie sesja aktywna"),
        ("Projekty", "aliasy projektów nazwy"),
        ("Cennik", "litellm models.dev offline aliasy modeli nadpisania cen"),
        ("Statusline", "etykiety modeli claude code statusline"),
        ("Zaawansowane", "plik konfiguracji folder danych rozbieżności kosztu przebuduj bazę"),
        ("Diagnostyka", "pliki wpisy błędy synchronizacja raport"),
        ("O aplikacji", "wersja licencje prywatność"),
        ("Źródła danych", "źródła ścieżki narzędzia")
    ];

    private readonly SettingsViewModel _vm = new();
    private readonly SettingsNav _nav = new();
    private readonly Select _navSelect = new() { Placeholder = "Podstrona" };
    private readonly ContentView _page = new();
    private readonly Grid _body = new();
    private readonly ScrollView _pageScroll = new();
    private readonly ScrollView _navScroll = new();
    private List<int> _visible = Enumerable.Range(0, Pages.Length).ToList();
    private int _current;

    public SettingsView()
    {
        BindingContext = _vm;
        var search = new SearchField { Placeholder = "Szukaj ustawienia", MinimumWidthRequest = Ui.D("SizeDrawerMin"), AutomationId = "Settings.Search" };
        search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchField.Query))
            {
                Filter(search.Query);
            }
        };
        var header = new PageHeader { Title = "Ustawienia", Actions = search };
        header.SetBinding(PageHeader.SubtitleProperty, nameof(SettingsViewModel.AutosaveCaption));

        var banner = new Banner { Kind = BannerKind.Danger, Title = "Nie zapisano zmian" };
        banner.SetBinding(Banner.MessageProperty, nameof(SettingsViewModel.SaveError));
        banner.SetBinding(IsVisibleProperty, nameof(SettingsViewModel.HasSaveError));

        _nav.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsNav.SelectedIndex)) { Open(_visible[Math.Clamp(_nav.SelectedIndex, 0, _visible.Count - 1)]); } };
        _navSelect.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Select.SelectedIndex) && _navSelect.SelectedIndex >= 0) { Open(_visible[_navSelect.SelectedIndex]); } };

        _body.ColumnSpacing = Ui.D("LayoutGridGap");
        _body.RowSpacing = Ui.D("Space300");
        _page.MaximumWidthRequest = Ui.D("SizeFormMaxWidth");
        _page.HorizontalOptions = LayoutOptions.Start;
        // Created once and only re-placed: a view must not be added to a new parent while still inside the old one.
        _pageScroll.Content = _page;
        _navScroll.Content = _nav;

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            RowSpacing = Ui.D("Space300")
        }.Dyn(PaddingProperty, "LayoutPagePadding");
        root.Add(header, 0, 0);
        root.Add(banner, 0, 1);
        root.Add(_body, 0, 2);
        Content = root;

        ShellViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Layout))
            {
                Layout();
                Open(_current);
            }
        };
        Filter(string.Empty);
        Layout();
        Open(0);
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await _vm.LoadAsync();
#if DEBUG
        // Developer aid: with ONIONLLM_SCREEN_SMOKE every sub-page is built once and traced.
        if (Environment.GetEnvironmentVariable("ONIONLLM_SCREEN_SMOKE") is { Length: > 0 })
        {
            for (var page = 0; page < Pages.Length - 1; page++)
            {
                Open(page);
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                global::OnionLLM.Diagnostics.Trace("smoke: settings page " + Pages[page].Title);
            }

            Open(0);
        }
#endif
    }

    public void OnNavigatedFrom()
    {
    }

    /// <summary>Settings has no data view of its own — refresh only syncs (status bar shows the result).</summary>
    public Task RefreshAsync(bool force) => AppState.RefreshDataAsync(force);

    private bool Wide => ShellViewModel.Instance.Layout == LayoutState.Wide;

    private void Layout()
    {
        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        _body.RowDefinitions.Clear();
        var narrow = ShellViewModel.Instance.Layout == LayoutState.Narrow;
        var scroll = _pageScroll;
        if (narrow)
        {
            // §4.7.7: on narrow SettingsNav becomes a Select above the content.
            _body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _body.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _body.Add(_navSelect, 0, 0);
            _body.Add(scroll, 0, 1);
        }
        else
        {
            _body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Ui.D("SizeSettingsNav"))));
            _body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _body.Add(_navScroll, 0, 0);
            _body.Add(scroll, 1, 0);
        }
    }

    private void Filter(string query)
    {
        var term = Select.Normalize(query.Trim());
        _visible = Enumerable.Range(0, Pages.Length)
            .Where(i => term.Length == 0 || Select.Normalize(Pages[i].Title + " " + Pages[i].Keywords).Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var titles = _visible.Select(i => Pages[i].Title).ToList();
        _nav.ItemsSource = titles;
        _navSelect.ItemsSource = titles;
        if (_visible.Count > 0 && !_visible.Contains(_current))
        {
            Open(_visible[0]);
        }
        else
        {
            Sync();
        }
    }

    private void Sync()
    {
        var index = _visible.IndexOf(_current);
        _nav.SelectedIndex = index;
        _navSelect.SelectedIndex = index;
    }

    private void Open(int page)
    {
        if (page == 10)
        {
            Sync();
            _ = ShellViewModel.Instance.NavigateAsync(Screen.Sources);
            return;
        }

        _current = page;
        Sync();
        var content = page switch
        {
            1 => Appearance(),
            2 => WindowPage(),
            3 => Calculations(),
            4 => Projects(),
            5 => Pricing(),
            6 => Statusline(),
            7 => Advanced(),
            8 => Diagnostics(),
            9 => About(),
            _ => General()
        };
        var stack = new VerticalStackLayout { Spacing = Ui.D("LayoutSectionGap") };
        stack.Children.Add(new Label { Text = Pages[page].Title }.Styled("Text.TitleSection"));
        stack.Children.Add(content);
        _page.Content = stack;
    }

    // ===== Helpers =====

    private FormRow Row(string label, View field, string? savedKey = null, string? description = null, string? errorPath = null)
    {
        var row = new FormRow { Label = label, Field = field, Description = description ?? string.Empty, IsHorizontal = Wide };
        if (savedKey is not null)
        {
            row.SetBinding(FormRow.SavedProperty, new Binding(nameof(SettingsViewModel.LastSaved), converter: new Converters.EqualsConverter(), converterParameter: savedKey));
        }

        if (errorPath is not null)
        {
            row.SetBinding(FormRow.ErrorProperty, errorPath);
        }

        return row;
    }

    private static SegmentedControl Segmented(string items, string selected, string label)
    {
        var control = new SegmentedControl { Label = label };
        control.SetBinding(SegmentedControl.ItemsSourceProperty, items);
        control.SetBinding(SegmentedControl.SelectedIndexProperty, selected, BindingMode.TwoWay);
        return control;
    }

    private static Switch Toggle(string path)
    {
        var toggle = new Switch { HorizontalOptions = LayoutOptions.Start };
        toggle.SetBinding(Switch.IsToggledProperty, path, BindingMode.TwoWay);
        return toggle;
    }

    private static TextField Number(string path, string suffix)
    {
        var field = new TextField { Kind = TextFieldKind.Number, Suffix = suffix, MaximumWidthRequest = Ui.D("SizeSelectMinWidth") };
        field.SetBinding(TextField.TextProperty, path, BindingMode.TwoWay);
        return field;
    }

    private static Button Action(string text, string command, string style = "Button.Secondary")
    {
        var button = new Button { Text = text, HorizontalOptions = LayoutOptions.Start }.Styled(style);
        button.SetBinding(Button.CommandProperty, command);
        return button;
    }

    private static VerticalStackLayout Stack(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    // ===== Sub-pages =====

    /// <summary>§6.9.1 Ogólne.</summary>
    private View General()
    {
        var timezone = new Select { Placeholder = "Strefa czasowa", ItemsSource = _vm.Timezones };
        timezone.SetBinding(Select.SelectedIndexProperty, nameof(SettingsViewModel.TimezoneIndex), BindingMode.TwoWay);
        var week = new Select { Placeholder = "Początek tygodnia", ItemsSource = _vm.Days };
        week.SetBinding(Select.SelectedIndexProperty, nameof(SettingsViewModel.StartOfWeekIndex), BindingMode.TwoWay);
        var stack = Stack(
            Row("Strefa czasowa", timezone, "timezone", "Wszystkie daty i godziny w aplikacji są w tej strefie."),
            Row("Początek tygodnia", week, "startOfWeek"));
        if (_vm.IsStartupSettingAvailable)
        {
            var row = Row("Uruchamiaj przy starcie systemu", Toggle(nameof(SettingsViewModel.RunAtStartup)), "startup");
            row.SetBinding(FormRow.DescriptionProperty, nameof(SettingsViewModel.StartupStatus));
            stack.Children.Add(row);
        }

        return stack;
    }

    /// <summary>§6.9.2 Wygląd with a live preview.</summary>
    private View Appearance()
    {
        var preview = new Card
        {
            Title = "Podgląd",
            Body = new VerticalStackLayout
            {
                Spacing = Ui.D("Space300"),
                Children =
                {
                    new StatTile { Title = "30 dni", Value = Fmt.Cost(212.4), Caption = Fmt.TokensWithUnit(14_200_000) },
                    new ListItem { Title = "Claude Code", Subtitle = "Anthropic", Leading = new SourceBadge { SourceId = "claude", Initial = "C" }, Trailing = Ui.Text("Text.TableNum.Cost", Fmt.Cost(12.34)) }
                }
            }
        };
        return Stack(
            Row("Motyw", Segmented(nameof(SettingsViewModel.Themes), nameof(SettingsViewModel.ThemeIndex), "Motyw"), "theme"),
            Row("Gęstość", Segmented(nameof(SettingsViewModel.Densities), nameof(SettingsViewModel.DensityIndex), "Gęstość"), "density"),
            Row("Animacje", Segmented(nameof(SettingsViewModel.Motions), nameof(SettingsViewModel.MotionIndex), "Animacje"), "motion",
                "„Pełne” respektuje systemowe ograniczenie ruchu."),
            preview);
    }

    /// <summary>§6.9.3 Okno i zasobnik — only „Zawsze na wierzchu” (tray and mini view: Wymaga platformy).</summary>
    private View WindowPage() => Stack(
        Row("Zawsze na wierzchu", Toggle(nameof(SettingsViewModel.AlwaysOnTop)), "alwaysOnTop", "Okno główne pozostaje nad innymi oknami."));

    /// <summary>§6.9.5 Obliczenia.</summary>
    private View Calculations()
    {
        var costMode = Row("Tryb kosztu", Segmented(nameof(SettingsViewModel.CostModes), nameof(SettingsViewModel.CostModeIndex), "Tryb kosztu"), "costMode");
        costMode.SetBinding(FormRow.DescriptionProperty, nameof(SettingsViewModel.CostModeDescription));
        var limitValue = Number(nameof(SettingsViewModel.TokenLimitValue), "tokenów");
        limitValue.SetBinding(IsVisibleProperty, nameof(SettingsViewModel.IsCustomTokenLimit));
        var limit = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children = { Segmented(nameof(SettingsViewModel.TokenLimitModes), nameof(SettingsViewModel.TokenLimitModeIndex), "Limit tokenów bloku"), limitValue }
        };
        return Stack(
            costMode,
            Row("Długość bloku", Number(nameof(SettingsViewModel.SessionLength), "h"), "sessionLength", "Od 1 do 24 godzin.", nameof(SettingsViewModel.SessionLengthError)),
            Row("Limit tokenów bloku", limit, "tokenLimit", "Maksimum z historii = największy zakończony blok.", nameof(SettingsViewModel.TokenLimitError)),
            Row("Odświeżanie danych", Number(nameof(SettingsViewModel.RefreshInterval), "s"), "refreshInterval", "Od 5 do 3600 sekund.", nameof(SettingsViewModel.RefreshIntervalError)),
            Row("Sesja aktywna, jeśli ostatnie zdarzenie młodsze niż", Number(nameof(SettingsViewModel.ActiveMinutes), "min"), "activeMinutes", "Od 1 do 240 minut.", nameof(SettingsViewModel.ActiveMinutesError)));
    }

    /// <summary>§6.9.6 Projekty.</summary>
    private View Projects()
    {
        var editor = new KeyValueEditor { Separator = ",", KeyPlaceholder = "katalog projektu", ValuePlaceholder = "nazwa wyświetlana", Help = "Tryb tekstowy: klucz=Nazwa,klucz2=Nazwa2" };
        editor.SetBinding(KeyValueEditor.TextProperty, nameof(SettingsViewModel.ProjectAliases), BindingMode.TwoWay);
        return Stack(Row("Aliasy projektów", editor, "projectAliases", "Nazwa katalogu projektu → nazwa wyświetlana w raportach i sesjach."));
    }

    /// <summary>§6.9.7 Cennik.</summary>
    private View Pricing()
    {
        StatusBadge Badge(string text, string kind)
        {
            var badge = new StatusBadge();
            badge.SetBinding(StatusBadge.TextProperty, text);
            badge.SetBinding(StatusBadge.KindProperty, kind);
            return badge;
        }

        Label Caption(string path)
        {
            var label = Ui.Text("Text.Caption.Secondary");
            label.SetBinding(Label.TextProperty, path);
            return label;
        }

        var refresh = Action("Odśwież cennik", nameof(SettingsViewModel.RefreshPricingCommand));
        refresh.AutomationId = "Settings.RefreshPricing";
        var status = new Card
        {
            Title = "Źródła cen",
            Actions = refresh,
            Body = new VerticalStackLayout
            {
                Spacing = Ui.D("Space300"),
                Children =
                {
                    new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { Ui.Text("Text.BodyStrong", "LiteLLM"), Badge(nameof(SettingsViewModel.LiteLlmBadge), nameof(SettingsViewModel.LiteLlmKind)) } },
                    Caption(nameof(SettingsViewModel.LiteLlmStatus)),
                    new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { Ui.Text("Text.BodyStrong", "models.dev"), Badge(nameof(SettingsViewModel.ModelsDevBadge), nameof(SettingsViewModel.ModelsDevKind)) } },
                    Caption(nameof(SettingsViewModel.ModelsDevStatus))
                }
            }
        };

        var aliases = new KeyValueEditor { Separator = "\n", KeyPlaceholder = "nazwa z logu", ValuePlaceholder = "model w cenniku", Help = "Jeden wpis w wierszu: nazwa z logu=model w cenniku (np. private-alpha=gpt-5.5)." };
        aliases.SetBinding(KeyValueEditor.TextProperty, nameof(SettingsViewModel.ModelAliases), BindingMode.TwoWay);

        var table = new DataTable
        {
            AutomationPrefix = "Settings.Overrides",
            HeightRequest = Ui.D("SizePopoverMaxHeight"),
            Columns =
            [
                new DataColumn { Key = nameof(PricingOverrideViewModel.Key), Header = "Model", Star = 2.2, Format = CellFormat.Mono, IsId = true },
                new DataColumn { Key = nameof(PricingOverrideViewModel.Input), Header = "Wejście", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(PricingOverrideViewModel.Output), Header = "Wyjście", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(PricingOverrideViewModel.CacheWrite), Header = "Cache 5 min", Align = ColumnAlign.End, Priority = 2, Format = CellFormat.Number },
                new DataColumn { Key = nameof(PricingOverrideViewModel.CacheWrite1h), Header = "Cache 1 h", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
                new DataColumn { Key = nameof(PricingOverrideViewModel.CacheRead), Header = "Odczyt", Align = ColumnAlign.End, Priority = 2, Format = CellFormat.Number },
                new DataColumn { Key = "Remove", Header = "", Width = Ui.D("SizeControlHeight") + Ui.D("Space200"), CellFactory = RemoveCell }
            ],
            ItemsSource = _vm.PricingOverrides,
            RowActivatedCommand = _vm.EditOverrideCommand,
            EmptyView = new EmptyState
            {
                Variant = EmptyStateVariant.Inline,
                Title = "Brak nadpisań cen",
                Description = "Nadpisanie zastępuje cenę z LiteLLM lub models.dev dla jednego modelu.",
                ActionText = "Dodaj nadpisanie",
                ActionCommand = _vm.AddOverrideCommand
            }
        };
        table.SetBinding(DataTable.StateProperty, nameof(SettingsViewModel.OverridesState));
        var add = Action("Dodaj nadpisanie", nameof(SettingsViewModel.AddOverrideCommand));
        add.AutomationId = "Settings.AddOverride";
        var advancedLink = new Button { Text = "Sprawdź rozbieżności kosztu" }.Styled("Button.Link");
        advancedLink.Clicked += (_, _) => Open(7);

        return Stack(
            status,
            Row("Cennik offline (bez pobierania)", Toggle(nameof(SettingsViewModel.Offline)), "offline",
                "Ceny tylko z pamięci podręcznej lub ze snapshotu w aplikacji; nowe modele mogą nie mieć ceny."),
            Row("Aliasy modeli do wyceny", aliases, "modelAliases", errorPath: nameof(SettingsViewModel.ModelAliasesError)),
            new Section { Title = "Nadpisania cen", Description = "USD za 1 M tokenów; puste pole = stawka z cennika.", Actions = add, Body = table },
            advancedLink);
    }

    private View RemoveCell()
    {
        var remove = new IconButton { Glyph = Icons.Trash2 }.Styled("IconButton.Ghost.Sm");
        remove.Dyn(Button.TextColorProperty, "ColorStatusDanger");
        ToolTipProperties.SetText(remove, "Usuń");
        remove.Clicked += (_, _) => _vm.RemoveOverrideCommand.Execute(remove.BindingContext as PricingOverrideViewModel);
        remove.BindingContextChanged += (_, _) =>
            SemanticProperties.SetDescription(remove, "Usuń nadpisanie " + (remove.BindingContext as PricingOverrideViewModel)?.Key);
        return remove;
    }

    /// <summary>§6.9.8 Statusline: label aliases + installation steps (the live preview needs the CLI renderer — not shown).</summary>
    private View Statusline()
    {
        var labels = new KeyValueEditor { Separator = "\n", KeyPlaceholder = "nazwa modelu", ValuePlaceholder = "etykieta", Help = "Dokładna nazwa modelu z Claude Code=krótka etykieta (np. ARN profilu Bedrock=claude-opus-4-6)." };
        labels.SetBinding(KeyValueEditor.TextProperty, nameof(SettingsViewModel.ModelLabelAliases), BindingMode.TwoWay);
        const string command = "onionllm statusline";
        var copy = new Button { Text = "Kopiuj polecenie" }.Styled("Button.Secondary.Sm");
        copy.Clicked += async (_, _) => await Ui.CopyAsync(command);
        var steps = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children =
            {
                Ui.Text("Text.Body", "1. Zainstaluj CLI OnionLLM i upewnij się, że polecenie onionllm jest w PATH."),
                Ui.Text("Text.Body", "2. W pliku ~/.claude/settings.json ustaw statusLine na polecenie:"),
                Ui.Box(Ui.Text("Text.Mono", command).Dyn(Label.TextColorProperty, "ColorTextOnInverse"), "ColorSurfaceInverse", "RadiusMd", "LayoutCardPadding"),
                copy,
                Ui.Text("Text.Body", "3. Uruchom ponownie Claude Code.")
            }
        };
        return Stack(
            Row("Etykiety modeli", labels, "modelLabelAliases", errorPath: nameof(SettingsViewModel.ModelLabelAliasesError)),
            new Section { Title = "Instalacja w Claude Code", Body = steps });
    }

    /// <summary>§6.9.9 Zaawansowane.</summary>
    private View Advanced()
    {
        var path = Ui.Text("Text.Mono", _vm.ConfigPath);
        path.LineBreakMode = LineBreakMode.MiddleTruncation;
        ToolTipProperties.SetText(path, _vm.ConfigPath);
        var config = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children =
            {
                path,
                new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { Action("Otwórz plik", nameof(SettingsViewModel.OpenConfigFileCommand), "Button.Secondary.Sm"), Action("Otwórz folder", nameof(SettingsViewModel.OpenConfigFolderCommand), "Button.Ghost.Sm") } }
            }
        };
        var summary = Ui.Text("Text.Secondary");
        summary.SetBinding(Label.TextProperty, nameof(SettingsViewModel.MismatchSummary));
        var table = new DataTable
        {
            AutomationPrefix = "Settings.Mismatches",
            HeightRequest = Ui.D("SizePopoverMaxHeight"),
            Columns =
            [
                new DataColumn { Key = nameof(MismatchRow.Source), Header = "Źródło", Priority = 2 },
                new DataColumn { Key = nameof(MismatchRow.Model), Header = "Model", Star = 1.6, Format = CellFormat.Mono },
                new DataColumn { Key = nameof(MismatchRow.Compared), Header = "Porównano", Align = ColumnAlign.End, Priority = 3, Format = CellFormat.Number },
                new DataColumn { Key = nameof(MismatchRow.Mismatches), Header = "Rozbieżne", Align = ColumnAlign.End, Priority = 2, Format = CellFormat.Number },
                new DataColumn { Key = nameof(MismatchRow.Stored), Header = "Zapisany", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(MismatchRow.Calculated), Header = "Wyliczony", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(MismatchRow.Difference), Header = "Różnica", Align = ColumnAlign.End, Format = CellFormat.Number }
            ],
            EmptyView = new EmptyState { Variant = EmptyStateVariant.Inline, Title = "Brak wyniku", Description = "Wybierz zakres i kliknij „Sprawdź”." }
        };
        table.SetBinding(DataTable.ItemsSourceProperty, nameof(SettingsViewModel.Mismatches));
        table.SetBinding(DataTable.StateProperty, nameof(SettingsViewModel.MismatchState));
        var check = new HorizontalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children = { Segmented(nameof(SettingsViewModel.MismatchRanges), nameof(SettingsViewModel.MismatchRangeIndex), "Zakres"), Action("Sprawdź", nameof(SettingsViewModel.CheckMismatchesCommand)) }
        };
        var rebuild = Action("Przebuduj bazę…", nameof(SettingsViewModel.RebuildCommand), "Button.Danger");
        rebuild.AutomationId = "Settings.Rebuild";
        return Stack(
            Row("Plik konfiguracji", config),
            Row("Folder danych", Action("Otwórz folder danych", nameof(SettingsViewModel.OpenDataFolderCommand))),
            new Section
            {
                Title = "Rozbieżności kosztu",
                Description = "Koszt zapisany przez narzędzie (np. costUSD Claude Code) a wyliczony z cennika.",
                Body = new VerticalStackLayout { Spacing = Ui.D("Space300"), Children = { check, summary, table } }
            },
            new Section
            {
                Title = "Przebudowa bazy",
                Description = "Odtwarza bazę z logów na dysku. Wpisy z logów, które narzędzia już usunęły, zostaną utracone.",
                Body = rebuild
            });
    }

    /// <summary>§6.9.10 Diagnostyka.</summary>
    private View Diagnostics()
    {
        _vm.UpdateDiagnostics();
        var list = new KeyValueList();
        list.SetBinding(KeyValueList.ItemsSourceProperty, nameof(SettingsViewModel.DiagnosticsItems));
        var table = new DataTable
        {
            AutomationPrefix = "Settings.Diagnostics",
            HeightRequest = Ui.D("SizeTableHeight"),
            Columns =
            [
                new DataColumn { Key = nameof(DiagnosticsRow.Source), Header = "Źródło", Star = 2, Format = CellFormat.Strong },
                new DataColumn { Key = nameof(DiagnosticsRow.Files), Header = "Pliki", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(DiagnosticsRow.Parsed), Header = "Przetworzone", Align = ColumnAlign.End, Priority = 2, Format = CellFormat.Number },
                new DataColumn { Key = nameof(DiagnosticsRow.Skipped), Header = "Pominięte linie", Align = ColumnAlign.End, Format = CellFormat.Number },
                new DataColumn { Key = nameof(DiagnosticsRow.Errors), Header = "Błędy", Align = ColumnAlign.End, Format = CellFormat.Number }
            ],
            RowActivatedCommand = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Sources))
        };
        table.SetBinding(DataTable.ItemsSourceProperty, nameof(SettingsViewModel.DiagnosticsRows));
        return Stack(list, table, Action("Kopiuj raport diagnostyczny", nameof(SettingsViewModel.CopyDiagnosticsCommand)));
    }

    /// <summary>§6.9.11 O aplikacji.</summary>
    private View About()
    {
        var logo = new Image { WidthRequest = Ui.D("SizeAvatarLg"), HeightRequest = Ui.D("SizeAvatarLg"), HorizontalOptions = LayoutOptions.Start };
        logo.SetDynamicResource(Image.SourceProperty, "ImageLogo");
        return Stack(
            new HorizontalStackLayout
            {
                Spacing = Ui.D("Space400"),
                Children = { logo, new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { Ui.Text("Text.TitleSection", "OnionLLM"), Ui.Text("Text.Caption", "Wersja " + _vm.Version) } } }
            },
            new Section
            {
                Title = "Prywatność",
                Body = Ui.Text("Text.Body", "Dane nie opuszczają komputera. Połączenia sieciowe: cennik (LiteLLM, models.dev), limity Claude (api.anthropic.com).")
            },
            new Section
            {
                Title = "Licencje",
                Body = Ui.Text("Text.Secondary", "Inter i JetBrains Mono — SIL Open Font License 1.1. Ikony Lucide — licencja ISC. Pliki licencji są dołączone do aplikacji.")
            });
    }
}
