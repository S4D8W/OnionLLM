#if DEBUG
using OnionLLM.Controls;
using OnionLLM.Core.Config;
using OnionLLM.Core.Reports;
using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

/// <summary>
/// DEBUG-only component gallery (Faza 12.2): every registry component in its main states, plus switches for theme and
/// density — the place to check both themes, both densities and the breakpoints before using a component on a screen.
/// </summary>
public sealed class GalleryView : ContentView, IScreenView
{
    private static readonly string[] Themes = ["Systemowy", "Jasny", "Ciemny"];
    private static readonly string[] Densities = ["Komfortowa", "Kompaktowa"];

    public GalleryView()
    {
        var themes = new SegmentedControl { ItemsSource = Themes, Label = "Motyw", SelectedIndex = (int)AppState.Config.Appearance.Theme };
        themes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SegmentedControl.SelectedIndex))
            {
                ThemeService.Apply((ThemePreference)themes.SelectedIndex);
            }
        };
        var density = new SegmentedControl { ItemsSource = Densities, Label = "Gęstość", SelectedIndex = (int)AppState.Config.Appearance.Density };
        density.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SegmentedControl.SelectedIndex))
            {
                DensityService.Apply((DensityPreference)density.SelectedIndex);
            }
        };

        var stack = new VerticalStackLayout
        {
            Spacing = Ui.D("LayoutSectionGap"),
            Children =
            {
                new PageHeader { Title = "Galeria komponentów", Subtitle = "Tylko build DEBUG · DESIGN.md §4", Actions = new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { themes, density } } },
                Group("Przyciski", Wrap(
                    new Button { Text = "Eksportuj CSV" }.Styled("Button.Primary"),
                    new Button { Text = "Odśwież cennik" }.Styled("Button.Secondary"),
                    new Button { Text = "Pokaż wszystkie" }.Styled("Button.Ghost"),
                    new Button { Text = "Przebuduj bazę…" }.Styled("Button.Danger"),
                    new Button { Text = "Usuń" }.Styled("Button.DangerGhost"),
                    new Button { Text = "Pokaż w Diagnostyce" }.Styled("Button.Link"),
                    Tip(new IconButton { Glyph = Icons.RefreshCw }.Styled("IconButton.Ghost"), "Odśwież (F5)"),
                    Tip(new IconButton { Glyph = Icons.Ellipsis }.Styled("IconButton.Secondary"), "Więcej"),
                    Export())),
                Group("Formularze i filtry", new VerticalStackLayout
                {
                    Spacing = Ui.D("Space400"),
                    Children =
                    {
                        Wrap(
                            new SegmentedControl { ItemsSource = new[] { "Dzień", "Tydzień", "Miesiąc" }, Label = "Okres" },
                            new SegmentedControl { ItemsSource = new[] { "Koszt", "Tokeny", "Cache %" }, IsSmall = true, Label = "Metryka" },
                            new DateRangePicker { Presets = new[] { "Dziś", "Ostatnie 7 dni", "Ostatnie 30 dni", "Własny zakres" }, PresetIndex = 2, CustomIndex = 3, DisplayText = "27 paź – 26 lis 2026", Since = DateTime.Today.AddDays(-29), Until = DateTime.Today },
                            new FilterChip { Variant = ChipVariant.Menu, Label = "Źródło", ItemsSource = SourceNames(), SelectedIndex = 0 },
                            new FilterChip { Label = "Modele" },
                            new FilterChip { Label = "Projekty", IsChecked = true },
                            new FilterChip { Variant = ChipVariant.Removable, Label = "Źródło: Claude Code" }),
                        Columns(
                            new FormRow { Label = "Odświeżanie danych", Description = "Od 5 do 3600 sekund.", Field = new TextField { Text = "30", Suffix = "s", Kind = TextFieldKind.Number } },
                            new FormRow { Label = "Strefa czasowa", Field = new Select { ItemsSource = new[] { "Systemowa (Europe/Warsaw)", "UTC", "America/New_York" }, SelectedIndex = 0, Placeholder = "Strefa czasowa" } },
                            new FormRow { Label = "Szukaj", Field = new SearchField { Placeholder = "Szukaj: ID sesji, projekt, model, źródło" } }),
                        Columns(
                            new FormRow { Label = "Limit tokenów bloku", Error = "Podaj liczbę większą od zera.", Field = new TextField { Text = "-5", Kind = TextFieldKind.Number, Error = "x" } },
                            new FormRow { Label = "Ścieżka", Field = new TextField { Text = @"C:\Users\dev\.claude\projects", Kind = TextFieldKind.Mono, Glyph = Icons.FolderOpen } },
                            new FormRow { Label = "Uruchamiaj przy starcie systemu", Field = new Switch { IsToggled = true }, IsHorizontal = false }),
                        new Tabs { ItemsSource = new[] { "Przegląd", "Konfiguracja", "Diagnostyka" } }
                    }
                }),
                Group("Metryki", new VerticalStackLayout
                {
                    Spacing = Ui.D("LayoutGridGap"),
                    Children =
                    {
                        Hero(),
                        Columns(
                            new StatTile { Title = "7 dni", Value = Fmt.Cost(84.2), Caption = Fmt.TokensWithUnit(14_200_000) },
                            new StatTile { Title = "30 dni", Value = Fmt.Cost(1234.5), Marker = "*", Caption = "Koszt niepełny" },
                            new StatTile { Title = "Tokeny dziś", Value = Fmt.Tokens(1_243_512), Caption = "62% z cache" },
                            new StatTile { Title = "Sesje", Value = "", Caption = "Brak danych w okresie" }),
                        new Card { Title = "TokenBreakdown", Body = new TokenBreakdown { Input = 120_000, Output = 48_000, CacheWrite = 300_000, CacheWrite1h = 40_000, CacheRead = 2_400_000, Reasoning = 12_000 } }
                    }
                }),
                Group("Limity i statusy", Columns(
                    new Card
                    {
                        Title = "LimitGauge",
                        Body = new VerticalStackLayout
                        {
                            Spacing = Ui.D("Space400"),
                            Children =
                            {
                                new LimitGauge { Title = "Limit 5-godzinny", Percent = 62, ResetText = "Reset za 2\u00A0h 14\u00A0min · 16:30", LiveTooltip = "Dane na żywo z API Anthropic" },
                                new LimitGauge { Title = "Limit 7-dniowy", Percent = 86, ResetText = "Reset w piątek o 09:00", State = LimitState.Estimated },
                                new LimitGauge { Title = "Aktywny blok", Percent = 112, ProjectionText = "Przy obecnym tempie: 140% przed końcem bloku" },
                                new LimitGauge { Title = "Limit 5-godzinny", State = LimitState.Unavailable }
                            }
                        }
                    },
                    new Card
                    {
                        Title = "Plakietki",
                        Body = new VerticalStackLayout
                        {
                            Spacing = Ui.D("Space300"),
                            Children =
                            {
                                Wrap(new StatusBadge { Text = "Aktywne", Kind = StatusKind.Live }, new StatusBadge { Text = "Wykryte", Kind = StatusKind.Neutral },
                                    new StatusBadge { Text = "Błędy", Kind = StatusKind.Warning }, new StatusBadge { Text = "Przekroczono", Kind = StatusKind.Danger },
                                    new StatusBadge { Text = "nadpisana", Kind = StatusKind.Info }),
                                Wrap(new BurnRateBadge { CostPerHour = Fmt.CostPerHour(4.2), TokensPerMinute = Fmt.TokensPerMinute(1800), Level = BurnRateLevel.Normal },
                                    new BurnRateBadge { CostPerHour = Fmt.CostPerHour(9.8), TokensPerMinute = Fmt.TokensPerMinute(4100), Level = BurnRateLevel.High }),
                                Wrap(Sources()),
                                Wrap(new SourceBadge { SourceId = "codex", Initial = "Cx", Size = BadgeSize.Md, State = SourceState.Disabled },
                                    new SourceBadge { SourceId = "goose", Initial = "Go", Size = BadgeSize.Md, State = SourceState.NotDetected },
                                    new ModelTag { Model = "claude-opus-4-6-20260101" }, new ModelTag { Model = "gpt-5-codex-fast" }, new ModelTag { Model = "foo-1", Marker = "*" },
                                    new Kbd { Text = "Ctrl" }, new Kbd { Text = "K" })
                            }
                        }
                    })),
                Group("Kontenery", Columns(
                    new Card
                    {
                        Title = "KeyValueList",
                        Body = new KeyValueList
                        {
                            ItemsSource = new[]
                            {
                                new KeyValueItem("ID sesji", "4f1c2a9e-7b3d-4c11-9a0e-2b5f8c3d1e77", IsMono: true, CanCopy: true),
                                new KeyValueItem("Koszt zapisany", Fmt.CostFull(3.4), IsNumeric: true),
                                new KeyValueItem("Koszt wyliczony", Fmt.CostFull(3.38), IsNumeric: true),
                                new KeyValueItem("Źródło", "Claude Code")
                            }
                        }
                    },
                    new Card
                    {
                        Title = "Section · ListItem · Card Accent",
                        Body = new Section
                        {
                            Title = "Źródła dziś",
                            Description = "Kolejność zmienisz przeciąganiem",
                            IsCollapsible = true,
                            StateKey = "gallery.sources",
                            Body = new VerticalStackLayout
                            {
                                Children =
                                {
                                    new ListItem { Title = "Claude Code", Subtitle = "Anthropic", Leading = new SourceBadge { SourceId = "claude", Initial = "C" }, Trailing = Ui.Text("Text.TableNum.Cost", Fmt.Cost(12.34)) },
                                    new Card { Variant = CardVariant.Accent, AccentColorKey = DesignTokens.SourceKey("codex"), Title = "Codex CLI", Body = Ui.Text("Text.Secondary", "Karta z paskiem akcentu źródła") },
                                    new Card { Variant = CardVariant.Interactive, Title = "Karta klikalna", Body = Ui.Text("Text.Secondary", "Hover: uniesienie (M8)"), Command = new Command(() => ToastService.Show("Kliknięto kartę", ToastKind.Info)) }
                                }
                            }
                        }
                    })),
                Group("Komunikaty i stany", new VerticalStackLayout
                {
                    Spacing = Ui.D("Space400"),
                    Children =
                    {
                        new Banner { Kind = BannerKind.Warning, Title = "Dane częściowe", Message = "Pominięto 3 pliki, których nie udało się odczytać. Koszt może być zaniżony.", ActionText = "Pokaż pliki", CanClose = true },
                        new Banner { Kind = BannerKind.Info, Title = "Limity Claude są niedostępne", Message = "Zaloguj się ponownie w Claude Code (claude /login)." },
                        new PricingNote { Text = "Koszt niepełny: brak ceny dla 2 modeli (gpt-x, foo-1)", AddPricesCommand = new Command(() => { }) },
                        Wrap(
                            new Button { Text = "Toast" }.Styled("Button.Secondary").With(b => b.Clicked += (_, _) => ToastService.Show("Wyeksportowano 26 wierszy", ToastKind.Success, "Otwórz folder", () => { })),
                            new Button { Text = "Toast błędu" }.Styled("Button.Secondary").With(b => b.Clicked += (_, _) => ToastService.Show("Nie udało się zapisać pliku.", ToastKind.Error)),
                            new Button { Text = "Dialog" }.Styled("Button.Secondary").With(b => b.Clicked += async (_, _) => await DialogService.ConfirmAsync("Przebudować bazę?", "Baza zostanie odtworzona z logów na dysku.", "Przebuduj", danger: true)),
                            new Button { Text = "Menu" }.Styled("Button.Secondary").With(b => b.Clicked += (_, _) => Menu.Show(b, [new MenuEntry("Kopiuj ID", Glyph: Icons.Copy, Shortcut: "Ctrl+C"), new MenuEntry("Otwórz folder", Glyph: Icons.FolderOpen), MenuEntry.Separator, new MenuEntry("Usuń", IsDanger: true, Glyph: Icons.Trash2)]))),
                        Columns(
                            new Card { Body = new EmptyState { Title = "Brak danych w tym zakresie", Description = "Zmień zakres lub źródło.", Glyph = Icons.ChartColumn, ActionText = "Pokaż ostatnie 30 dni" } },
                            new Card { Body = new EmptyState { Title = "Nie udało się wczytać sesji", Description = "Baza jest zablokowana przez inny proces.", IsError = true, ActionText = "Spróbuj ponownie", ErrorDetails = "SqliteException: database is locked" } },
                            new Card { Body = new VerticalStackLayout { Spacing = Ui.D("Space200"), Children = { Skeleton.Line("TypeTitleCardSize", 0.5), Skeleton.Line(), Skeleton.Line("TypeBodySize", 0.7) } } })
                    }
                })
            }
        };
        Content = new ScrollView { Content = new ContentView { Padding = Ui.Thick("ThicknessZero"), Content = stack }.Dyn(PaddingProperty, "LayoutPagePadding") };
    }

    public Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    public void OnNavigatedFrom()
    {
    }

    public Task RefreshAsync(bool force) => AppState.RefreshDataAsync(force);

    private static View Group(string title, View body) => new Section { Title = title, Body = body };

    private static View Wrap(params View[] views)
    {
        var flex = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
        foreach (var view in views)
        {
            view.Margin = new Thickness(0, 0, Ui.D("Space200"), Ui.D("Space200"));
            flex.Children.Add(view);
        }

        return flex;
    }

    private static View Columns(params View[] views)
    {
        var grid = new Grid { ColumnSpacing = Ui.D("LayoutGridGap") };
        for (var i = 0; i < views.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(views[i], i);
        }

        return grid;
    }

    private static T Tip<T>(T view, string text) where T : View
    {
        ToolTipProperties.SetText(view, text);
        SemanticProperties.SetDescription(view, text);
        return view;
    }

    private static View Export() => new SplitButton
    {
        Text = "Eksportuj",
        Options = [new MenuEntry("CSV"), new MenuEntry("JSON (format ccusage)"), new MenuEntry("Kopiuj jako tabelę")]
    };

    private static View Hero() => new Border
    {
        Content = new HeroMetric { Value = 12.34, Marker = "*", Caption = "1,24 M tokenów · 12 sesji" }
    }.Styled("Card.Hero");

    private static string[] SourceNames() =>
        ["Wszystkie źródła", .. AppState.Engine.Sources.Select(source => source.DisplayName)];

    private static View[] Sources() =>
        AppState.Engine.Sources.Select(source => (View)Tip(new SourceBadge
        {
            SourceId = source.Id,
            Initial = source.DisplayName[..1].ToUpperInvariant(),
            Size = BadgeSize.Md
        }, source.DisplayName)).ToArray();
}

internal static class GalleryExtensions
{
    public static T With<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }
}
#endif
