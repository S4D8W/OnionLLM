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
        ("Powiadomienia i budżety", "powiadomienia progi ciche godziny tempo budżet dzienny miesięczny koszt wycisz"),
        ("Obliczenia", "tryb kosztu długość bloku limit tokenów odświeżanie sesja aktywna"),
        ("Projekty", "aliasy projektów nazwy"),
        ("Cennik", "litellm models.dev offline aliasy modeli nadpisania cen"),
        ("Statusline", "etykiety modeli claude code statusline"),
        ("Zaawansowane", "plik konfiguracji folder danych rozbieżności kosztu przebuduj bazę"),
        ("Diagnostyka", "pliki wpisy błędy synchronizacja raport"),
        ("O aplikacji", "wersja licencje prywatność"),
        ("Źródła danych", "źródła ścieżki narzędzia")
    ];

    /// <summary>Sub-pages for the command palette („Ustawienia → Cennik”); „Źródła danych” is a link to Źródła.</summary>
    public static IEnumerable<(string Title, string Keywords)> PageIndex => Pages.Where(page => page.Title != "Źródła danych");

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
        // Drill-down to a sub-page by title, e.g. „Projekty” from the project drawer or „Cennik” from Modele.
        if (parameter is string title && Array.FindIndex(Pages, entry => entry.Title == title) is var target and >= 0)
        {
            Open(target);
        }

        // Command palette actions live on their sub-pages (the palette is a shortcut, not the only way, §4.6.7).
        if (parameter is ScreenAction action)
        {
            var (page, command) = action.Name switch
            {
                ScreenActions.RefreshPricing => ("Cennik", _vm.RefreshPricingCommand),
                ScreenActions.CheckMismatches => ("Zaawansowane", _vm.CheckMismatchesCommand),
                ScreenActions.Rebuild => ("Zaawansowane", _vm.RebuildCommand),
                _ => ((string?)null, (Command?)null)
            };
            if (page is not null && command is not null)
            {
                Open(Array.FindIndex(Pages, entry => entry.Title == page));
                command.Execute(null);
            }
        }

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
        if (Pages[page].Title == "Źródła danych")
        {
            Sync();
            _ = ShellViewModel.Instance.NavigateAsync(Screen.Sources);
            return;
        }

        _current = page;
        Sync();
        var content = Pages[page].Title switch
        {
            "Wygląd" => Appearance(),
            "Okno i zasobnik" => WindowPage(),
            "Powiadomienia i budżety" => BudgetsPage(),
            "Obliczenia" => Calculations(),
            "Projekty" => Projects(),
            "Cennik" => Pricing(),
            "Statusline" => Statusline(),
            "Zaawansowane" => Advanced(),
            "Diagnostyka" => Diagnostics(),
            "O aplikacji" => About(),
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
        // §6.9.1 „Waluta wyświetlania”: USD · PLN · EUR + rate (manual or NBP); export and CLI stay in USD (§5.1).
        var rate = Number(nameof(SettingsViewModel.Rate), string.Empty);
        rate.SetBinding(TextField.SuffixProperty, nameof(SettingsViewModel.RateSuffix));
        rate.SetBinding(IsVisibleProperty, nameof(SettingsViewModel.IsManualRate));
        var automatic = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
        automatic.Children.Add(Toggle(nameof(SettingsViewModel.RateAutomatic)));
        automatic.Children.Add(new Label { Text = "Automatycznie (NBP, dziennie)", VerticalOptions = LayoutOptions.Center }.Styled("Text.Body"));
        var info = Ui.Text("Text.Caption");
        info.SetBinding(Label.TextProperty, nameof(SettingsViewModel.RateInfo));
        var rateBox = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children = { Ui.Text("Text.Label", "1 USD ="), rate, automatic, info }
        };
        rateBox.SetBinding(IsVisibleProperty, nameof(SettingsViewModel.IsConverted));
        var currency = new VerticalStackLayout
        {
            Spacing = Ui.D("Space300"),
            Children = { Segmented(nameof(SettingsViewModel.Currencies), nameof(SettingsViewModel.CurrencyIndex), "Waluta wyświetlania"), rateBox }
        };
        var stale = new Banner { Kind = BannerKind.Info, Title = "Kurs waluty jest nieaktualny", Message = "Sprawdź połączenie z internetem albo wpisz kurs ręcznie." };
        stale.SetBinding(IsVisibleProperty, nameof(SettingsViewModel.IsRateStale));
        var stack = Stack(
            stale,
            Row("Strefa czasowa", timezone, "timezone", "Wszystkie daty i godziny w aplikacji są w tej strefie."),
            Row("Początek tygodnia", week, "startOfWeek"),
            Row("Waluta wyświetlania", currency, "currency", "Koszty pokazujemy z „≈”; eksport i CLI zawsze w USD.", nameof(SettingsViewModel.RateError)));
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

    /// <summary>§6.9.3 Okno i zasobnik: always on top, tray icon, close to tray, mini view modules (macOS menu bar: Mac only).</summary>
    private View WindowPage()
    {
        var stack = Stack(Row("Zawsze na wierzchu", Toggle(nameof(SettingsViewModel.AlwaysOnTop)), "alwaysOnTop", "Okno główne pozostaje nad innymi oknami."));
        if (_vm.IsTrayAvailable)
        {
            stack.Children.Add(Row("Pokazuj ikonę w zasobniku", Toggle(nameof(SettingsViewModel.TrayEnabled)), "tray", "Klik — mini-widok, dwuklik — okno, prawy klik — menu."));
            var close = Row("Zamknięcie okna minimalizuje do zasobnika", Toggle(nameof(SettingsViewModel.CloseToTray)), "closeToTray", "Bez ikony w zasobniku zamknięcie okna kończy aplikację.");
            close.SetBinding(IsEnabledProperty, nameof(SettingsViewModel.CanCloseToTray));
            stack.Children.Add(close);
        }

        var modules = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        void Build()
        {
            modules.Children.Clear();
            foreach (var row in _vm.MiniModules)
            {
                var check = new CheckBox { IsChecked = row.Shown, VerticalOptions = LayoutOptions.Center };
                check.CheckedChanged += (_, e) => { if (e.Value != row.Shown) { _vm.ToggleMiniModule(row); } };
                SemanticProperties.SetDescription(check, "Pokaż moduł " + row.Label);
                var up = new IconButton { Glyph = Icons.ArrowUp }.Styled("IconButton.Ghost.Sm");
                up.Clicked += (_, _) => _vm.MoveMiniModule(row, -1);
                ToolTipProperties.SetText(up, "Przesuń w górę");
                SemanticProperties.SetDescription(up, "Przesuń w górę: " + row.Label);
                var down = new IconButton { Glyph = Icons.ArrowDown }.Styled("IconButton.Ghost.Sm");
                down.Clicked += (_, _) => _vm.MoveMiniModule(row, 1);
                ToolTipProperties.SetText(down, "Przesuń w dół");
                SemanticProperties.SetDescription(down, "Przesuń w dół: " + row.Label);
                var line = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = Ui.D("Space200") };
                var label = Ui.Text("Text.Body", row.Label);
                label.VerticalOptions = LayoutOptions.Center;
                line.Add(check);
                line.Add(label, 1);
                line.Add(up, 2);
                line.Add(down, 3);
                modules.Children.Add(line);
            }
        }

        _vm.MiniModules.CollectionChanged += (_, _) => Build();
        Build();
        stack.Children.Add(Row("Mini-widok: moduły", modules, "miniModules", "Kolejność i widoczność modułów (także prawym przyciskiem w mini-widoku)."));
        stack.Children.Add(Row("Mini-widok: zawsze na wierzchu", Toggle(nameof(SettingsViewModel.MiniAlwaysOnTop)), "miniAlwaysOnTop"));
        return stack;
    }

    /// <summary>
    /// §6.9.4 „Powiadomienia i budżety” — the budget part (system notifications and quiet hours need platform code,
    /// so the page is called „Budżety”).
    /// </summary>
    private View BudgetsPage()
    {
        var list = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        void Build()
        {
            list.Children.Clear();
            foreach (var row in _vm.SourceBudgets)
            {
                var actions = new HorizontalStackLayout { Spacing = Ui.D("Space100") };
                var edit = new IconButton { Glyph = Icons.Pencil }.Styled("IconButton.Ghost.Sm");
                edit.Clicked += (_, _) => _vm.EditSourceBudgetCommand.Execute(row);
                ToolTipProperties.SetText(edit, "Edytuj");
                SemanticProperties.SetDescription(edit, "Edytuj budżet " + row.Name);
                var remove = new IconButton { Glyph = Icons.Trash2 }.Styled("IconButton.Ghost.Sm");
                remove.Clicked += (_, _) => _vm.RemoveSourceBudgetCommand.Execute(row);
                ToolTipProperties.SetText(remove, "Usuń");
                SemanticProperties.SetDescription(remove, "Usuń budżet " + row.Name);
                actions.Children.Add(edit);
                actions.Children.Add(remove);
                list.Children.Add(new ListItem
                {
                    Title = row.Name,
                    Subtitle = $"Dziennie {row.DailyText} · miesięcznie {row.MonthlyText}",
                    Leading = new SourceBadge { SourceId = row.Id, Initial = row.Name[..1].ToUpperInvariant(), Size = BadgeSize.Sm },
                    Trailing = actions
                });
            }

            if (_vm.SourceBudgets.Count == 0)
            {
                list.Children.Add(Ui.Text("Text.Tertiary", "Brak budżetów per źródło."));
            }
        }

        _vm.SourceBudgets.CollectionChanged += (_, _) => Build();
        Build();
        var add = new Button { Text = "Dodaj budżet źródła", HorizontalOptions = LayoutOptions.Start, AutomationId = "Settings.AddSourceBudget" }.Styled("Button.Secondary");
        add.Command = _vm.AddSourceBudgetCommand;
        // §6.9.4 notifications: master toggle, thresholds table, burn rate, quiet hours, test.
        CancellationTokenSource? pending = null;
        Grid ThresholdGrid() => new()
        {
            ColumnDefinitions = [new(GridLength.Star), new(new GridLength(Ui.D("SizeSelectMinWidth"))), new(new GridLength(Ui.D("SizeSelectMinWidth"))), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        };
        var status = Ui.Text("Text.Caption");
        status.Text = _vm.NotificationStatus;
        var systemLink = new Button { Text = "Ustawienia powiadomień systemu", HorizontalOptions = LayoutOptions.Start }.Styled("Button.Link");
        systemLink.Command = _vm.OpenSystemNotificationSettingsCommand;
        var thresholds = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        var head = ThresholdGrid();
        head.Add(Ui.Text("Text.Overline", "LIMIT"));
        head.Add(Ui.Text("Text.Overline", "OSTRZEŻENIE"), 1);
        head.Add(Ui.Text("Text.Overline", "PRZEKROCZENIE"), 2);
        head.Add(Ui.Text("Text.Overline", "POWIADAMIAJ"), 3);
        thresholds.Children.Add(head);
        foreach (var row in _vm.Thresholds)
        {
            var line = ThresholdGrid();
            var name = Ui.Text("Text.Body", row.Label);
            name.VerticalOptions = LayoutOptions.Center;
            var warn = new TextField { Kind = TextFieldKind.Number, Suffix = "%", Text = row.Warn };
            var exceed = new TextField { Kind = TextFieldKind.Number, Suffix = "%", Text = row.Exceed };
            var enabled = new Switch { IsToggled = row.Enabled, VerticalOptions = LayoutOptions.Center };
            SemanticProperties.SetDescription(enabled, "Powiadamiaj: " + row.Label);
            warn.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TextField.Text)) { row.Warn = warn.Text; DebouncedThresholds(); } };
            exceed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TextField.Text)) { row.Exceed = exceed.Text; DebouncedThresholds(); } };
            enabled.Toggled += (_, e) => { row.Enabled = e.Value; _vm.SaveThresholds(); };
            line.Add(name);
            line.Add(warn, 1);
            line.Add(exceed, 2);
            line.Add(enabled, 3);
            thresholds.Children.Add(line);
        }

        var thresholdError = Ui.Text("Text.Caption").Dyn(Label.TextColorProperty, "ColorStatusDanger");
        thresholdError.SetBinding(Label.TextProperty, nameof(SettingsViewModel.ThresholdError));
        thresholds.Children.Add(thresholdError);
        var quiet = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
        var from = new TextField { Placeholder = "22:00", MaximumWidthRequest = Ui.D("SizeSelectMinWidth") };
        from.SetBinding(TextField.TextProperty, nameof(SettingsViewModel.QuietFrom), BindingMode.TwoWay);
        var to = new TextField { Placeholder = "08:00", MaximumWidthRequest = Ui.D("SizeSelectMinWidth") };
        to.SetBinding(TextField.TextProperty, nameof(SettingsViewModel.QuietTo), BindingMode.TwoWay);
        quiet.Children.Add(Toggle(nameof(SettingsViewModel.QuietEnabled)));
        quiet.Children.Add(new Label { Text = "od", VerticalOptions = LayoutOptions.Center }.Styled("Text.Body"));
        quiet.Children.Add(from);
        quiet.Children.Add(new Label { Text = "do", VerticalOptions = LayoutOptions.Center }.Styled("Text.Body"));
        quiet.Children.Add(to);
        var test = new Button { Text = "Wyślij powiadomienie testowe", HorizontalOptions = LayoutOptions.Start, AutomationId = "Settings.TestNotification" }.Styled("Button.Secondary");
        test.Command = _vm.SendTestNotificationCommand;
        var notifications = new VerticalStackLayout
        {
            Spacing = Ui.D("Space600"),
            Children =
            {
                Row("Powiadomienia systemowe", new VerticalStackLayout { Spacing = Ui.D("Space100"), Children = { Toggle(nameof(SettingsViewModel.NotificationsEnabled)), status, systemLink } }, "notifications"),
                Row("Progi", thresholds, "thresholds", "Raz na okno limitu: 5 h, 7 dni, blok, dzień, miesiąc."),
                Row("Wysokie tempo spalania", Toggle(nameof(SettingsViewModel.BurnHigh)), "burnHigh"),
                Row("Ciche godziny", quiet, "quietHours", "W tych godzinach powiadomienia czekają do końca ciszy.", nameof(SettingsViewModel.QuietError)),
                test
            }
        };

        async void DebouncedThresholds()
        {
            pending?.Cancel();
            var token = (pending = new CancellationTokenSource()).Token;
            try
            {
                await Task.Delay(Design.MotionTokens.AutosaveDelay, token);
                _vm.SaveThresholds();
            }
            catch (TaskCanceledException)
            {
            }
        }

        return Stack(
            new Section { Title = "Powiadomienia", Body = notifications },
            Row("Budżet dzienny", Number(nameof(SettingsViewModel.DailyBudget), "$"), "dailyBudget", "Puste = bez budżetu. Kwoty w USD.", nameof(SettingsViewModel.DailyBudgetError)),
            Row("Budżet miesięczny", Number(nameof(SettingsViewModel.MonthlyBudget), "$"), "monthlyBudget", "Prognoza końca miesiąca pojawia się od 3. dnia.", nameof(SettingsViewModel.MonthlyBudgetError)),
            new Section { Title = "Budżety per źródło", Body = new VerticalStackLayout { Spacing = Ui.D("Space300"), Children = { list, add } } });
    }

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
            Row("Limit tokenów bloku", limit, "tokenLimit", "Bez limitu — bloki bez procentu i ostrzeżeń. Maksimum z historii = największy zakończony blok.", nameof(SettingsViewModel.TokenLimitError)),
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

    /// <summary>§6.9.8 Statusline: label aliases with a live preview (the CLI renderer from Core) + installation steps.</summary>
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
        var preview = Ui.Text("Text.Mono", "Wczytywanie podglądu…").Dyn(Label.TextColorProperty, "ColorTextOnInverse");
        preview.LineBreakMode = LineBreakMode.WordWrap;
        preview.AutomationId = "Settings.StatuslinePreview";
        var caption = Ui.Text("Text.Caption", "Przykład z ostatniej sesji Claude Code; w Claude Code model i kontekst podaje samo narzędzie.");
        CancellationTokenSource? pending = null;
        async void Refresh()
        {
            pending?.Cancel();
            var token = (pending = new CancellationTokenSource()).Token;
            try
            {
                await Task.Delay(Design.MotionTokens.SearchDebounce, token);
                var text = await StatuslinePreview.RenderAsync(_vm.ModelLabelAliases);
                if (!token.IsCancellationRequested)
                {
                    preview.Text = text;
                    SemanticProperties.SetDescription(preview, "Podgląd linii statusu: " + text);
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        labels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KeyValueEditor.Text))
            {
                Refresh();
            }
        };
        Refresh();
        var previewBox = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            Children = { Ui.Box(preview, "ColorSurfaceInverse", "RadiusMd", "LayoutCardPadding"), caption }
        };
        return Stack(
            Row("Etykiety modeli", labels, "modelLabelAliases", errorPath: nameof(SettingsViewModel.ModelLabelAliasesError)),
            new Section { Title = "Podgląd", Body = previewBox },
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
