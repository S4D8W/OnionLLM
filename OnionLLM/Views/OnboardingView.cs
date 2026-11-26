using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Config;
using OnionLLM.Core.Sources;
using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.Resources.Styles;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

/// <summary>
/// First run (DESIGN.md §7.5, <c>S-FIRST</c>): full-window wizard without the sidebar — 1. „Wykryte narzędzia”,
/// 2. „Wygląd” (currency needs data: not rendered), 3. „Praca w tle” (Windows: autostart only; tray, notifications and
/// budget need platform/data work) — then the history import. „Pomiń” keeps the defaults; „Wstecz” is always possible.
/// </summary>
public sealed class OnboardingView : ContentView
{
    private const string DoneKey = "onboarding.done";

    private readonly TaskCompletionSource<Screen?> _done = new();
    private readonly Border _card = new Border().Styled("Surface.Dialog");
    private readonly Label _progress = Ui.Text("Text.Caption");
    private readonly HorizontalStackLayout _dots = new() { Spacing = Ui.D("Space150"), VerticalOptions = LayoutOptions.Center };
    private readonly Label _title = Ui.Text("Text.TitlePage");
    private readonly Label _description = Ui.Text("Text.Secondary");
    private readonly ContentView _body = new();
    private readonly Button _skip = new Button { Text = "Pomiń", AutomationId = "Onboarding.Skip" }.Styled("Button.Ghost");
    private readonly Button _back = new Button { Text = "Wstecz", AutomationId = "Onboarding.Back" }.Styled("Button.Secondary");
    private readonly Button _next = new Button { AutomationId = "Onboarding.Next" }.Styled("Button.Primary");
    private readonly HorizontalStackLayout _footer = new() { Spacing = Ui.D("Space200"), HorizontalOptions = LayoutOptions.End };
    private readonly List<(IUsageSource Source, string? Folder, Switch Toggle)> _detected = [];
    private readonly List<IUsageSource> _missing = [];
    private readonly Switch _startup = new() { HorizontalOptions = LayoutOptions.Start, AutomationId = "Onboarding.Startup" };
    private readonly Switch _tray = new() { HorizontalOptions = LayoutOptions.Start, IsToggled = AppState.Config.Tray.Enabled, AutomationId = "Onboarding.Tray" };
    private readonly Switch _notify = new() { HorizontalOptions = LayoutOptions.Start, IsToggled = AppState.Config.Notifications.Enabled, AutomationId = "Onboarding.Notifications" };
    private readonly TextField _budget = new() { Kind = TextFieldKind.Number, Suffix = "$", Placeholder = "brak", AutomationId = "Onboarding.Budget" };
    private ThemePreference _theme;
    private DensityPreference _density;
    private string _currency = AppState.Config.Currency.Display;
    private int _step;
    private bool _detectionDone;

    public OnboardingView()
    {
        AutomationId = "Onboarding";
        this.Dyn(BackgroundColorProperty, "ColorBgCanvas");
        _theme = AppState.Config.Appearance.Theme;
        _density = AppState.Config.Appearance.Density;

        _skip.Clicked += async (_, _) => await FinishAsync(skip: true);
        _back.Clicked += (_, _) => Show(_step - 1);
        _next.Clicked += async (_, _) =>
        {
            if (_step < StepCount - 1)
            {
                Show(_step + 1);
            }
            else
            {
                await FinishAsync(skip: false);
            }
        };

        var logo = new Image { WidthRequest = Ui.D("SizeLogoSidebar"), HeightRequest = Ui.D("SizeLogoSidebar") }.Dyn(Image.SourceProperty, "ImageLogo");
        SemanticProperties.SetDescription(logo, "OnionLLM");
        var brand = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
        brand.Children.Add(logo);
        brand.Children.Add(new Label { FormattedText = Logotype(), VerticalOptions = LayoutOptions.Center }.Styled("Text.TitleCard"));

        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = Ui.D("Space300") };
        header.Add(brand);
        header.Add(_progress, 1);
        header.Add(_dots, 2);
        _progress.VerticalOptions = LayoutOptions.Center;

        SemanticProperties.SetHeadingLevel(_title, SemanticHeadingLevel.Level1);
        var footer = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        footer.Add(_skip);
        footer.Add(_footer, 1);

        _card.WidthRequest = Ui.D("SizeOnboardingCard");
        _card.MinimumHeightRequest = Ui.D("SizeOnboardingMinHeight");
        _card.HorizontalOptions = LayoutOptions.Center;
        _card.VerticalOptions = LayoutOptions.Center;
        _card.Content = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            RowSpacing = Ui.D("Space400"),
            Children = { header }
        };
        var grid = (Grid)_card.Content;
        grid.Add(_title, 0, 1);
        grid.Add(_description, 0, 2);
        grid.Add(_body, 0, 3);
        grid.Add(footer, 0, 4);

        Content = new ScrollView
        {
            Content = new Grid { Padding = Ui.Thick("LayoutPagePadding"), Children = { _card } }
        };
        SizeChanged += (_, _) => _card.WidthRequest = Math.Min(Ui.D("SizeOnboardingCard"), Math.Max(0, Width - 2 * Ui.D("Space600")));
        _ = DetectAsync();
        Show(0, announce: false);
#if DEBUG
        if (Environment.GetEnvironmentVariable("ONIONLLM_ONBOARDING_SELFTEST") is { Length: > 0 })
        {
            _ = SelfTestAsync();
        }
#endif
    }

#if DEBUG
    /// <summary>DEBUG: walks the steps, previews the dark theme and skips (defaults restored); expect „onboarding selftest done”.</summary>
    private async Task SelfTestAsync()
    {
        while (!_detectionDone)
        {
            await Task.Delay(MotionTokens.PollInterval);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        global::OnionLLM.Diagnostics.Trace($"onboarding: step {_step + 1}/{StepCount} „{_title.Text}”, detected {_detected.Count}, missing {_missing.Count}");
        for (var step = 1; step < StepCount; step++)
        {
            _next.SendClicked();
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            global::OnionLLM.Diagnostics.Trace($"onboarding: step {_step + 1}/{StepCount} „{_title.Text}”, next „{_next.Text}”");
            if (_step == 1)
            {
                _theme = ThemePreference.Dark;
                ThemeService.Apply(_theme);
                Show(1);
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                DensityService.Apply(DensityPreference.Compact);
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                global::OnionLLM.Diagnostics.Trace($"onboarding: preview {ThemeService.EffectiveTheme}/{DensityService.Current}");
            }
        }

        _back.SendClicked();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        global::OnionLLM.Diagnostics.Trace($"onboarding: back to step {_step + 1}");
        _skip.SendClicked();
        await Task.Delay(TimeSpan.FromSeconds(1));
        global::OnionLLM.Diagnostics.Trace($"onboarding: import screen „{_title.Text}”, restored {DensityService.Current}");
        await _done.Task;
        global::OnionLLM.Diagnostics.Trace("onboarding selftest done");
    }
#endif

    /// <summary>
    /// <c>S-FIRST</c>: no completed onboarding. Existing installations (the ledger already synced) are marked as done
    /// without showing the wizard. DEBUG: <c>ONIONLLM_ONBOARDING=1</c> forces it.
    /// </summary>
    public static bool IsRequired
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("ONIONLLM_ONBOARDING") is { Length: > 0 })
            {
                return true;
            }
#endif
            if (ViewModels.ViewState.GetBool(DoneKey, false))
            {
                return false;
            }

            if (AppState.Engine.Ledger.LastSync() is not null)
            {
                ViewModels.ViewState.Set(DoneKey, true);
                return false;
            }

            return true;
        }
    }

    /// <summary>Completes with the screen to open afterwards (Dziś, or Źródła for „Dodaj ścieżkę ręcznie”).</summary>
    public Task<Screen?> Completion => _done.Task;

    /// <summary>Step 3 exists only where autostart is implemented (Windows).</summary>
    private static int StepCount =>
#if WINDOWS
        3;
#else
        2;
#endif

    private void Show(int step, bool announce = true)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);
        _progress.Text = $"Krok {_step + 1} z {StepCount}";
        _dots.Children.Clear();
        for (var i = 0; i < StepCount; i++)
        {
            _dots.Children.Add(Ui.Dot(i == _step ? "ColorActionPrimary" : "ColorBorderStrong"));
        }

        SemanticProperties.SetDescription(_dots, _progress.Text);
        _footer.Children.Clear();
        if (_step > 0)
        {
            _footer.Children.Add(_back);
        }

        _footer.Children.Add(_next);
        var last = _step == StepCount - 1;
        _next.Text = last ? "Zacznij" : "Dalej";
        _next.Style = Ui.Res<Style>(last ? "Button.Primary.Lg" : "Button.Primary");
        (_title.Text, _description.Text, _body.Content) = _step switch
        {
            0 => ("Wykryte narzędzia", "OnionLLM czyta lokalne logi narzędzi AI. Wybierz, które śledzić — zmienisz to później na ekranie Źródła.", Tools()),
            1 => ("Wygląd i waluta", "Motyw, gęstość i walutę możesz zmienić w każdej chwili w Ustawieniach.", Appearance()),
            _ => ("Praca w tle", "Aplikacja może startować razem z systemem, żeby dane były zawsze aktualne.", BackgroundStep())
        };
        if (announce)
        {
            SemanticScreenReader.Announce($"{_progress.Text}: {_title.Text}");
        }
    }

    // ===== Step 1: detected tools =====

    private async Task DetectAsync()
    {
        var config = AppState.Config;
        var results = await Task.Run(() => AppState.Engine.Sources
            .Select(source => (Source: source, Installed: source.IsInstalled(config), Folder: source.ResolveRoots(config).FirstOrDefault(Directory.Exists)))
            .ToList());
        foreach (var (source, installed, folder) in results)
        {
            if (installed)
            {
                _detected.Add((source, folder, new Switch { IsToggled = true, AutomationId = "Onboarding.Tool." + source.Id }));
            }
            else
            {
                _missing.Add(source);
            }
        }

        _detectionDone = true;
        if (_step == 0)
        {
            Show(0, announce: false);
        }
    }

    private View Tools()
    {
        if (!_detectionDone)
        {
            var loading = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
            loading.Children.Add(new ActivityIndicator { IsRunning = true });
            loading.Children.Add(Ui.Text("Text.Secondary", "Szukam narzędzi na dysku…"));
            return loading;
        }

        if (_detected.Count == 0)
        {
            return new EmptyState
            {
                Variant = EmptyStateVariant.Inline,
                Glyph = Icons.FolderSearch,
                Title = "Nie wykryto żadnego narzędzia",
                Description = "Żadne z 20 obsługiwanych narzędzi nie ma logów w domyślnych folderach. Wskaż folder ręcznie na ekranie Źródła.",
                ActionText = "Dodaj ścieżkę ręcznie",
                ActionCommand = new Command(async () => await FinishAsync(skip: false, then: Screen.Sources, importHistory: false))
            };
        }

        var list = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        foreach (var (source, folder, toggle) in _detected)
        {
            SemanticProperties.SetDescription(toggle, "Śledź " + source.DisplayName);
            list.Children.Add(new ListItem
            {
                Title = source.DisplayName,
                Subtitle = folder ?? "Wykryto w ścieżce systemowej",
                Leading = new SourceBadge { SourceId = source.Id, Initial = source.DisplayName[..1].ToUpperInvariant(), Size = BadgeSize.Sm },
                Trailing = toggle
            });
        }

        if (_missing.Count > 0)
        {
            var names = new VerticalStackLayout { Spacing = Ui.D("Space100") };
            foreach (var source in _missing)
            {
                names.Children.Add(Ui.Text("Text.Secondary", source.DisplayName));
            }

            list.Children.Add(new Section
            {
                Title = $"+{_missing.Count} {Fmt.Plural(_missing.Count, "niewykryte", "niewykryte", "niewykrytych")} — pokaż",
                IsCollapsible = true,
                IsCollapsed = true,
                Body = names
            });
        }

        return list;
    }

    // ===== Step 2: appearance (live preview) =====

    private View Appearance()
    {
        var tiles = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start };
        foreach (var (theme, label) in new[] { (ThemePreference.System, "Systemowy"), (ThemePreference.Light, "Jasny"), (ThemePreference.Dark, "Ciemny") })
        {
            tiles.Children.Add(ThemeTile(theme, label));
        }

        var density = new SegmentedControl
        {
            ItemsSource = new[] { "Komfortowa", "Kompaktowa" },
            SelectedIndex = _density == DensityPreference.Compact ? 1 : 0,
            Label = "Gęstość",
            AutomationId = "Onboarding.Density"
        };
        density.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SegmentedControl.SelectedIndex))
            {
                _density = density.SelectedIndex == 1 ? DensityPreference.Compact : DensityPreference.Comfortable;
                DensityService.Apply(_density);
            }
        };

        string[] currencies = ["USD", "PLN", "EUR"];
        var currency = new SegmentedControl
        {
            ItemsSource = currencies,
            SelectedIndex = Math.Max(0, Array.IndexOf(currencies, _currency)),
            Label = "Waluta wyświetlania",
            AutomationId = "Onboarding.Currency"
        };
        currency.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SegmentedControl.SelectedIndex))
            {
                _currency = currencies[Math.Clamp(currency.SelectedIndex, 0, 2)];
            }
        };

        return new VerticalStackLayout
        {
            Spacing = Ui.D("Space500"),
            Children =
            {
                new FormRow { Label = "Motyw", Field = tiles },
                new FormRow { Label = "Waluta wyświetlania", Description = "PLN i EUR liczymy po kursie średnim NBP (aktualizowany codziennie); eksport zawsze w USD.", Field = currency },
                new FormRow { Label = "Gęstość", Description = "Kompaktowa mieści więcej wierszy na ekranie.", Field = density }
            }
        };
    }

    /// <summary>Theme tile with a miniature window drawn from the theme's own token dictionary.</summary>
    private View ThemeTile(ThemePreference theme, string label)
    {
        var selected = theme == _theme;
        var preview = new Grid
        {
            ColumnDefinitions = theme == ThemePreference.System ? [new(GridLength.Star), new(GridLength.Star)] : [new(GridLength.Star)],
            HeightRequest = Ui.D("SizeThemePreview")
        };
        if (theme == ThemePreference.System)
        {
            preview.Add(Miniature(new TokensLight()));
            preview.Add(Miniature(new TokensDark()), 1);
        }
        else
        {
            preview.Add(Miniature(theme == ThemePreference.Dark ? new TokensDark() : new TokensLight()));
        }

        var tile = new Border
        {
            WidthRequest = Ui.D("SizeThemeTile"),
            StrokeShape = Ui.Corner("RadiusLg"),
            StrokeThickness = Ui.D(selected ? "StrokeFocus" : "StrokeDefault"),
            Padding = Ui.Thick("ThicknessZero"),
            Margin = new Thickness(0, 0, Ui.D("Space300"), Ui.D("Space300")),
            Content = new VerticalStackLayout
            {
                Children =
                {
                    preview,
                    new Label { Text = label, Padding = Ui.Thick("PaddingNavItem") }.Styled(selected ? "Text.BodyStrong" : "Text.Body")
                }
            },
            AutomationId = "Onboarding.Theme." + theme
        }.Dyn(Border.StrokeProperty, selected ? "ColorBorderFocusBrush" : "ColorBorderDefaultBrush");
        SemanticProperties.SetDescription(tile, $"Motyw {label.ToLowerInvariant()}{(selected ? ", wybrany" : "")}");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _theme = theme;
            ThemeService.Apply(theme);
            Show(1);
        };
        tile.GestureRecognizers.Add(tap);
        return tile;
    }

    private static View Miniature(ResourceDictionary tokens)
    {
        Color Of(string key) => tokens.TryGetValue(key, out var value) && value is Color color ? color : Ui.Res<Color>("ColorTransparent");
        var window = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(new GridLength(3, GridUnitType.Star))],
            BackgroundColor = Of("ColorBgCanvas")
        };
        window.Add(new BoxView { Color = Of("ColorBgSidebar") });
        var content = new VerticalStackLayout { Padding = Ui.Thick("PaddingMenu"), Spacing = Ui.D("Space100") };
        content.Children.Add(new BoxView { Color = Of("ColorActionPrimary"), HeightRequest = Ui.D("SizeDotSm"), CornerRadius = Ui.D("RadiusXs") });
        content.Children.Add(new BoxView { Color = Of("ColorSurface1"), HeightRequest = Ui.D("SizeAvatarMd"), CornerRadius = Ui.D("RadiusXs") });
        window.Add(content, 1);
        return window;
    }

    // ===== Step 3: background work (Windows) =====

    private View BackgroundStep()
    {
        _budget.MaximumWidthRequest = Ui.D("SizeSelectMinWidth");
        return new VerticalStackLayout
        {
            Spacing = Ui.D("Space400"),
            Children =
            {
                new FormRow { Label = "Uruchamiaj przy starcie systemu", Description = "OnionLLM startuje po zalogowaniu do Windows.", Field = _startup, IsHorizontal = true },
                new FormRow { Label = "Ikona w zasobniku", Description = "Podgląd kosztu i limitów, mini-widok jednym kliknięciem; zamknięcie okna chowa je do zasobnika.", Field = _tray, IsHorizontal = true },
                new FormRow { Label = "Powiadomienia o limitach", Description = "Przy 80% i 100% limitów oraz budżetów; progi zmienisz w Ustawieniach.", Field = _notify, IsHorizontal = true },
                new FormRow { Label = "Budżet dzienny (opcjonalnie)", Description = "W USD; puste = bez budżetu.", Field = _budget, IsHorizontal = true }
            }
        };
    }

    // ===== Finish → history import =====

    private async Task FinishAsync(bool skip, Screen then = Screen.Today, bool importHistory = true)
    {
        if (!skip)
        {
            try
            {
                var config = AppState.Config.Clone();
                config.Appearance.Theme = _theme;
                config.Appearance.Density = _density;
                config.Tray.Enabled = _tray.IsToggled;
                config.Notifications.Enabled = _notify.IsToggled;
                if (double.TryParse(_budget.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var budget) && budget > 0)
                {
                    config.Budgets.DailyUsd = budget;
                }
                if (config.Currency.Display != _currency)
                {
                    config.Currency = new Core.Config.CurrencySettings { Display = _currency, RateSource = "nbp" };
                }
                foreach (var (source, _, toggle) in _detected)
                {
                    if (!config.Sources.TryGetValue(source.Id, out var settings))
                    {
                        config.Sources[source.Id] = settings = new SourceSettings();
                    }

                    settings.Enabled = toggle.IsToggled;
                }

                AppState.SaveConfig(config);
#if WINDOWS
                if (_startup.IsToggled)
                {
                    await WindowsStartupManager.SetEnabledAsync(true);
                }
#endif
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ToastService.Show("Nie zapisano ustawień: " + exception.Message, ToastKind.Error);
            }
        }
        else
        {
            // „Pomiń” keeps the defaults: undo the live preview.
            AppearanceService.Apply(AppState.Config.Appearance);
        }

        ViewModels.ViewState.Set(DoneKey, true);
        if (importHistory)
        {
            await ImportAsync();
        }

        _done.TrySetResult(then);
    }

    /// <summary>
    /// History import (§7.5): the first full sync. OnionRings show the progress of up to three tools (rings = sources,
    /// percent of files), the list shows „Pliki X z Y · N wpisów” per tool. „Kontynuuj w tle” leaves the import running
    /// (banner <c>S-IMPORT</c> on the screens) and opens Dziś.
    /// </summary>
    private async Task ImportAsync()
    {
        var tools = AppState.Engine.Sources.Where(source => AppState.Config.IsSourceEnabled(source.Id) && _detected.Any(item => item.Source.Id == source.Id)).ToList();
        var background = new TaskCompletionSource();
        var rings = new OnionRings { HeightRequest = Ui.D("SizeRingHero"), WidthRequest = Ui.D("SizeRingHero"), HorizontalOptions = LayoutOptions.Center };
        var list = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        var items = new Dictionary<string, ListItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in tools)
        {
            var item = new ListItem
            {
                Title = source.DisplayName,
                Subtitle = "Czeka w kolejce",
                Leading = new SourceBadge { SourceId = source.Id, Initial = source.DisplayName[..1].ToUpperInvariant(), Size = BadgeSize.Sm }
            };
            items[source.Id] = item;
            list.Children.Add(item);
        }

        void Update()
        {
            var progress = AppState.Progress;
            foreach (var (id, item) in items)
            {
                item.Subtitle = progress.TryGetValue(id, out var current)
                    ? (current.Files == current.TotalFiles ? "Gotowe · " : "") +
                      $"Pliki {Fmt.Number(current.Files)} z {Fmt.Number(current.TotalFiles)} · {Fmt.Count(current.Entries, "wpis", "wpisy", "wpisów")}"
                    : "Czeka w kolejce";
            }

            rings.Rings = tools.Take(3).Select(source => new OnionRing(source.DisplayName,
                progress.TryGetValue(source.Id, out var current) && current.TotalFiles > 0 ? current.Files * 100.0 / current.TotalFiles : 0,
                DesignTokens.SourceKey(source.Id))).ToList();
            SemanticProperties.SetDescription(rings, "Postęp importu: " + string.Join(", ", rings.Rings.Select(ring => $"{ring.Name} {Fmt.Percent(ring.Percent)}")));
        }

        var note = Ui.Text("Text.Secondary", "Wczytywanie historii z dysku. Nic nie jest wysyłane do internetu.");
        note.HorizontalTextAlignment = TextAlignment.Center;
        var continueButton = new Button { Text = "Kontynuuj w tle", AutomationId = "Onboarding.Background" }.Styled("Button.Secondary");
        continueButton.Clicked += (_, _) => background.TrySetResult();

        _progress.Text = string.Empty;
        _dots.Children.Clear();
        _title.Text = "Import historii";
        _description.Text = tools.Count > 0
            ? $"Śledzone narzędzia: {tools.Count}. Duża historia może wczytywać się kilka minut."
            : "Wczytywanie danych.";
        _body.Content = new VerticalStackLayout { Spacing = Ui.D("Space400"), Children = { rings, note, list } };
        _skip.IsVisible = false;
        _footer.Children.Clear();
        _footer.Children.Add(continueButton);
        Update();
        SemanticScreenReader.Announce("Import historii");

        void OnProgress(object? sender, EventArgs e) => Update();
        AppState.ProgressChanged += OnProgress;
        try
        {
            var sync = AppState.RefreshDataAsync(force: true);
            var finished = await Task.WhenAny(sync, background.Task);
            if (finished != sync)
            {
                ShellViewModel.Instance.ShowImportBanner();
                _ = sync.ContinueWith(task =>
                {
                    _ = task.Exception; // observed: the status bar reports sync errors
                    MainThread.BeginInvokeOnMainThread(ToastImported);
                }, TaskScheduler.Default);
                return;
            }

            try
            {
                await sync;
                Update();
                ToastImported();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // StatusBar and its error toast report the failure (§5.7).
            }
        }
        finally
        {
            AppState.ProgressChanged -= OnProgress;
        }
    }

    private static void ToastImported()
    {
        if (AppState.LastSyncError is not null)
        {
            return;
        }

        var (_, entries) = AppState.Engine.Ledger.Statistics();
        var tools = AppState.Engine.LastSync?.Sources.Count(report => report.ParsedFiles > 0 || report.Files > 0) ?? 0;
        ToastService.Show($"Wczytano {Fmt.Count(entries, "wpis", "wpisy", "wpisów")} z {Fmt.Count(tools, "narzędzia", "narzędzi", "narzędzi")}");
    }

    /// <summary>„OnionLLM” two-tone logotype: „LLM” in text.secondary (§7.7).</summary>
    private static FormattedString Logotype()
    {
        var text = new FormattedString();
        text.Spans.Add(new Span { Text = "Onion" });
        text.Spans.Add(new Span { Text = "LLM" }.Dyn(Span.TextColorProperty, "ColorTextSecondary"));
        return text;
    }
}
