using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Views;

/// <summary>
/// Mini view (DESIGN.md §7.2): frameless 360-wide window — hero header (drag, refresh, expand, close) and the modules
/// chosen in Ustawienia → Okno i zasobnik: cost and tokens of the day, limits 5 h / 7 dni, active block, top 3 sources.
/// Right click toggles modules; a click on a module opens the main window on its screen.
/// </summary>
public sealed class MiniViewPage : ContentPage
{
    private readonly VerticalStackLayout _modules = new();
    private readonly Label _updated = Ui.Text("Text.Caption").Dyn(Label.TextColorProperty, "ColorHeroTextSecondary");
    private readonly Grid _header = new();
    private readonly IconButton _refresh;

    public MiniViewPage()
    {
        this.Dyn(BackgroundColorProperty, "ColorSurface1");
        Title = "OnionLLM — mini-widok";

        var logo = new Image { Source = "onion_logo_dark.png", WidthRequest = Ui.D("IconSm"), HeightRequest = Ui.D("IconSm") }; // on the hero gradient: dark-background variant (§7.7)
        var title = Ui.Text("Text.BodyStrong", "Dziś").Dyn(Label.TextColorProperty, "ColorHeroText");
        _refresh = Button(Icons.RefreshCw, "Odśwież (F5)", "Mini.Refresh", async () => await ShellViewModel.Instance.RefreshAsync(force: true));
        var expand = Button(Icons.Maximize2, "Rozwiń (Ctrl+Shift+M)", "Mini.Expand", () => WindowService.CloseMini(showMain: true));
        var close = Button(Icons.X, "Zamknij mini-widok", "Mini.Close", () => WindowService.CloseMini(showMain: true));
        _header.ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)];
        _header.ColumnSpacing = Ui.D("Space200");
        _header.Padding = Ui.Thick("PaddingRow");
        _header.HeightRequest = Ui.D("SizeMiniHeader");
        _header.Dyn(BackgroundProperty, "BrushHero");
        logo.VerticalOptions = title.VerticalOptions = _updated.VerticalOptions = LayoutOptions.Center;
        _header.Add(logo);
        _header.Add(title, 1);
        _header.Add(_updated, 2);
        _header.Add(new HorizontalStackLayout { Spacing = Ui.D("Space050"), VerticalOptions = LayoutOptions.Center, Children = { _refresh, expand, close } }, 3);
        var expandTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        expandTap.Tapped += (_, _) => WindowService.CloseMini(showMain: true);
        _header.GestureRecognizers.Add(expandTap);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);

        Content = new VerticalStackLayout { Children = { _header, _modules } };
        LiveState.Updated += OnLiveUpdated;
        AppState.Changed += OnConfigChanged;
        ShellViewModel.Instance.PropertyChanged += OnShellChanged;
        Build();
        _ = LiveState.UpdateAsync(force: true);
    }

    /// <summary>The window is gone: stop listening to app-wide events (a closed window must not be animated).</summary>
    public void Detach()
    {
        LiveState.Updated -= OnLiveUpdated;
        AppState.Changed -= OnConfigChanged;
        ShellViewModel.Instance.PropertyChanged -= OnShellChanged;
    }

    private void OnLiveUpdated(object? sender, EventArgs e) => Build();

    private void OnConfigChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
    {
#if WINDOWS
        if (Window is not null)
        {
            Platforms.Windows.WindowPlatform.SetAlwaysOnTop(Window, AppState.Config.MiniView.AlwaysOnTop);
        }
#endif
        Build();
    });

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsRefreshing) && Handler is not null)
        {
            _refresh.IsSpinning = ShellViewModel.Instance.IsRefreshing;
        }
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Window is null)
        {
            return;
        }

        // Keyboard shortcuts (Ctrl+Shift+M — back to the main window) work in the mini view too.
        ShortcutService.Attach(Window);
#if WINDOWS
        Platforms.Windows.WindowPlatform.ConfigureMini(Window, AppState.Config.MiniView.AlwaysOnTop);
        Platforms.Windows.WindowPlatform.MakeDragHandle(_header, Window);
#endif
        FitHeight();
    }

    private static IconButton Button(string glyph, string tooltip, string automationId, Action action)
    {
        var button = new IconButton { Glyph = glyph, AutomationId = automationId }.Styled("IconButton.OnHero");
        ToolTipProperties.SetText(button, tooltip);
        SemanticProperties.SetDescription(button, tooltip);
        button.Clicked += (_, _) => action();
        return button;
    }

    private void Build()
    {
        _modules.Children.Clear();
        var snapshot = LiveState.Snapshot;
        _updated.Text = snapshot is null ? "" : Fmt.Time(LiveState.UpdatedAt);
        var modules = AppState.Config.MiniView.Modules;
        if (modules.Count == 0)
        {
            _modules.Children.Add(Module(Ui.Text("Text.Tertiary", "Wybierz moduły (prawy przycisk)"), null));
        }

        foreach (var module in modules)
        {
            var view = snapshot is null ? Skeleton.Lines(2) : module switch
            {
                "cost" => Cost(snapshot),
                "limits" => Limits(snapshot),
                "block" => snapshot.ActiveBlock is null ? null : Block(snapshot),
                "sources" => Sources(snapshot),
                _ => null
            };
            if (view is not null)
            {
                _modules.Children.Add(Module(view, module switch
                {
                    "limits" => Screen.Limits,
                    "block" => Screen.Blocks,
                    _ => Screen.Today
                }));
            }
        }

        BuildModuleMenu();
        FitHeight();
    }

    /// <summary>Module frame: padding 12, divider above, click → main window on <paramref name="screen"/>.</summary>
    private static View Module(View content, Screen? screen)
    {
        var frame = new VerticalStackLayout { Children = { new BoxView().Styled("Divider"), new ContentView { Content = content, Padding = Ui.D("Space300") } } };
        if (screen is { } target)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                WindowService.CloseMini(showMain: false);
                await WindowService.ShowMainAsync(target);
            };
            frame.GestureRecognizers.Add(tap);
        }

        return frame;
    }

    private static View Cost(DashboardSnapshot snapshot)
    {
        if (snapshot.Today.TotalTokens == 0)
        {
            return Ui.Text("Text.Tertiary", "Brak aktywności dziś");
        }

        var marker = (snapshot.Sources.Any(source => source.HasUnpricedUsage) ? "*" : "") + (snapshot.Sources.Any(source => source.HasEstimatedModel) ? "~" : "");
        var cost = new MetricLabel { Value = snapshot.Today.CostUsd, Marker = marker, Size = MetricSize.Lg, Format = MetricFormat.Cost };
        var trend = new TrendDelta { Current = snapshot.Today.CostUsd, Baseline = snapshot.YesterdayCost ?? double.NaN, Comparison = "vs wczoraj" };
        return new VerticalStackLayout
        {
            Spacing = Ui.D("Space100"),
            Children = { cost, Ui.Text("Text.Caption", Fmt.TokensWithUnit(snapshot.Today.TotalTokens)), trend }
        };
    }

    private static View Limits(DashboardSnapshot snapshot)
    {
        var claude = snapshot.Sources.FirstOrDefault(source => source.Id == "claude")?.Limits;
        if (claude is not { IsAvailable: true })
        {
            return Ui.Text("Text.Tertiary", "Limity niedostępne");
        }

        var five = claude.FiveHourUtilization * 100;
        var seven = claude.SevenDayUtilization * 100;
        var rings = new OnionRings
        {
            WidthRequest = Ui.D("SizeMiniRings"),
            HeightRequest = Ui.D("SizeMiniRings"),
            Rings = [new OnionRing("7 dni", seven, "ColorChartCat1"), new OnionRing("5 h", five, "ColorChartCat4")],
            VerticalOptions = LayoutOptions.Center
        };
        var lines = new VerticalStackLayout
        {
            Spacing = Ui.D("Space200"),
            VerticalOptions = LayoutOptions.Center,
            Children = { GaugeLine("5 h", five, claude.FiveHourReset), GaugeLine("7 dni", seven, claude.SevenDayReset) }
        };
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        grid.Add(rings);
        grid.Add(lines, 1);
        return grid;
    }

    /// <summary>One-line gauge: „5 h ▬▬▬▭ 62% · za 2 h 14 min”.</summary>
    private static View GaugeLine(string name, double percent, DateTimeOffset? reset)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        };
        grid.Add(Ui.Text("Text.Caption", name));
        grid.Add(new Controls.ProgressBar { Value = percent / 100, IsThreshold = true, VerticalOptions = LayoutOptions.Center }, 1);
        var text = Fmt.Percent(percent) + (reset is { } at && at > DateTimeOffset.Now ? " · " + Fmt.Countdown(at - DateTimeOffset.Now) : "");
        grid.Add(Ui.Text("Text.Caption", text), 2);
        SemanticProperties.SetDescription(grid, $"Limit {name}: {Fmt.PercentSpoken(percent)}");
        return grid;
    }

    private static View Block(DashboardSnapshot snapshot)
    {
        var block = snapshot.ActiveBlock!;
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space200") };
        stack.Children.Add(Ui.Text("Text.Body", $"Pozostało {Fmt.Duration(block.Remaining(DateTimeOffset.Now))} · {Fmt.Cost(block.Totals.CostUsd)} w bloku"));
        if (block.BurnRate is { } burn)
        {
            stack.Children.Add(new BurnRateBadge
            {
                CostPerHour = Fmt.CostPerHour(burn.CostPerHour),
                TokensPerMinute = Fmt.TokensPerMinute(burn.TokensPerMinute),
                Level = burn.Level,
                HorizontalOptions = LayoutOptions.Start
            });
        }

        if (block.Projection is { } projection && snapshot.BlockTokenLimit is { } limit and > 0)
        {
            stack.Children.Add(Ui.Text("Text.Caption", $"Prognoza: {Fmt.Percent(projection.TotalTokens * 100.0 / limit)} limitu do końca bloku."));
        }

        return stack;
    }

    private static View Sources(DashboardSnapshot snapshot)
    {
        var top = snapshot.Sources.Where(source => source.Today.CostUsd > 0).OrderByDescending(source => source.Today.CostUsd).Take(3).ToList();
        if (top.Count == 0)
        {
            return Ui.Text("Text.Tertiary", "Brak aktywności dziś");
        }

        var max = top[0].Today.CostUsd;
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space200") };
        foreach (var source in top)
        {
            var grid = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = Ui.D("Space200")
            };
            grid.Add(new SourceBadge { SourceId = source.Id, Initial = source.DisplayName[..1].ToUpperInvariant(), Size = BadgeSize.Xs, VerticalOptions = LayoutOptions.Center });
            grid.Add(new VerticalStackLayout
            {
                Spacing = Ui.D("Space050"),
                Children =
                {
                    Ui.Text("Text.Body", source.DisplayName),
                    new Controls.ProgressBar { Value = max > 0 ? source.Today.CostUsd / max : 0, ColorKey = DesignTokens.SourceKey(source.Id) }
                }
            }, 1);
            grid.Add(Ui.Text("Text.TableNum.Cost", Fmt.Cost(source.Today.CostUsd)), 2);
            SemanticProperties.SetDescription(grid, $"{source.DisplayName}: {Fmt.CostSpoken(source.Today.CostUsd)}");
            stack.Children.Add(grid);
        }

        return stack;
    }

    /// <summary>Right click (§7.2): module toggles and „Zawsze na wierzchu” — the same settings as Okno i zasobnik.</summary>
    private void BuildModuleMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var (id, label) in new[] { ("cost", "Koszt i tokeny"), ("limits", "Limity"), ("block", "Blok"), ("sources", "Top źródła") })
        {
            var shown = AppState.Config.MiniView.Modules.Contains(id);
            flyout.Add(Item($"Pokaż: {label}", shown, () => Save(config =>
            {
                var modules = config.MiniView.Modules;
                if (!modules.Remove(id))
                {
                    modules.Add(id);
                    config.MiniView.Modules = MiniViewSettings.AllModules.Where(modules.Contains).ToList();
                }
            })));
        }

        flyout.Add(new MenuFlyoutSeparator());
        var top = AppState.Config.MiniView.AlwaysOnTop;
        flyout.Add(Item("Zawsze na wierzchu", top, () =>
        {
            Save(config => config.MiniView.AlwaysOnTop = !top);
#if WINDOWS
            if (Window is not null)
            {
                Platforms.Windows.WindowPlatform.SetAlwaysOnTop(Window, !top);
            }
#endif
        }));
        if (Content is View root)
        {
            FlyoutBase.SetContextFlyout(root, flyout);
        }
    }

    private static MenuFlyoutItem Item(string text, bool isChecked, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IconImageSource = isChecked ? new FontImageSource { FontFamily = "Lucide", Glyph = Icons.Check, Size = Ui.D("IconSm") }.Dyn(FontImageSource.ColorProperty, "ColorTextPrimary") : null
        };
        item.Clicked += (_, _) => action();
        return item;
    }

    private static void Save(Action<OnionConfig> change)
    {
        try
        {
            var config = AppState.Config.Clone();
            change(config);
            AppState.SaveConfig(config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigException)
        {
            ToastService.Show("Nie zapisano ustawienia: " + exception.Message, ToastKind.Error);
        }
    }

    /// <summary>Height follows the modules: min 140, max 520 (§7.2).</summary>
    private void FitHeight()
    {
        if (Window is not { } window || Content is not View content)
        {
            return;
        }

        var size = content.Measure(Ui.D("SizeMiniWidth"), double.PositiveInfinity);
        var height = Math.Clamp(size.Height, Ui.D("SizeMiniMinHeight"), Ui.D("SizeMiniMaxHeight"));
#if WINDOWS
        Platforms.Windows.WindowPlatform.Resize(window, Ui.D("SizeMiniWidth"), height);
#else
        window.Height = height;
#endif
    }
}
