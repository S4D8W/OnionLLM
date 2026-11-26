#if DEBUG
using OnionLLM.Navigation;

namespace OnionLLM;

/// <summary>
/// DEBUG-only runtime checks of global surfaces (Faza 12.4), traced to <c>ONIONLLM_DIAG_LOG</c>:
/// <c>ONIONLLM_PALETTE_SELFTEST=1</c> → expect „palette selftest done”.
/// </summary>
internal static class DebugSelfTest
{
    /// <summary><c>ONIONLLM_UI_SELFTEST=1</c>: Faza 12.5 UI gaps → expect „ui selftest done: 0 failures”.</summary>
    public static async Task UiAsync(ShellViewModel shell)
    {
        var failures = 0;
        void Check(bool condition, string what)
        {
            Diagnostics.Trace($"ui: {(condition ? "ok" : "FAIL")} {what}");
            failures += condition ? 0 : 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(4));
        foreach (var entry in shell.OverviewItems.Concat(shell.SystemItems))
        {
            Diagnostics.Trace($"ui: indicator {entry.Info.Screen} badge „{entry.BadgeText}” dot „{entry.DotColorKey}” hint „{entry.SpokenHint}”");
        }

        // Drawer width: clamped 360–640.
        var width = shell.DrawerWidth;
        shell.ResizeDrawer(500, save: false);
        Check(Math.Abs(shell.DrawerWidth - 500) < 1, "drawer resizes to 500");
        shell.ResizeDrawer(5000, save: false);
        Check(shell.DrawerWidth <= Design.DesignTokens.Double("SizeDrawerMax", 640), "drawer width clamped to max");
        shell.ResizeDrawer(width, save: false);

        // Alt+↑/↓ on Dziś.
        await shell.NavigateAsync(Screen.Today);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var today = (Views.TodayView)shell.Navigation.CurrentView!;
        var cards = ElementTree.Find<Controls.SourceCard>(today).ToList();
        if (cards.Count >= 2)
        {
            var first = (ViewModels.SourceCardViewModel)cards[0].BindingContext;
            cards[0].FocusMenu();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Check(KeyboardRouter.Route(KeyboardAction.MoveDown), "Alt+↓ handled by Dziś");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var moved = ElementTree.Find<Controls.SourceCard>(today).Select(card => ((ViewModels.SourceCardViewModel)card.BindingContext).Id).ToList();
            Check(moved.IndexOf(first.Id) == 1, $"card {first.Id} moved to position 2");
            KeyboardRouter.Route(KeyboardAction.MoveUp);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            moved = ElementTree.Find<Controls.SourceCard>(today).Select(card => ((ViewModels.SourceCardViewModel)card.BindingContext).Id).ToList();
            Check(moved.IndexOf(first.Id) == 0, "Alt+↑ restores the order");
        }
        else
        {
            Diagnostics.Trace($"ui: only {cards.Count} source cards — Alt+↑/↓ not checked");
        }

        // Reports: scroll border, chart keyboard, two-month calendar, narrow filter collapse.
        await shell.NavigateAsync(Screen.Reports);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var reports = shell.Navigation.CurrentView!;
        var header = ElementTree.Find<Controls.PageHeader>(reports).First();
        var scroll = ElementTree.Find<ScrollView>(reports).First(view => view.Orientation != ScrollOrientation.Horizontal);
        await scroll.ScrollToAsync(0, 300, false);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Check(header.IsScrolled, "PageHeader shows the scroll border");
        await scroll.ScrollToAsync(0, 0, false);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Check(!header.IsScrolled, "border hidden at the top");

        var chart = ElementTree.Find<Controls.Charts.BarChart>(reports).First();
        KeyboardRouter.Target = chart;
        Check(KeyboardRouter.Route(KeyboardAction.Collapse) && KeyboardRouter.Route(KeyboardAction.Home), "chart handles ←/Home");

        var picker = ElementTree.Find<Controls.DateRangePicker>(reports).First();
        picker.SendClicked();
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(Controls.PopoverService.IsOpen, "date range popover opens");
        Controls.PopoverService.Close();

        var bar = ElementTree.Find<Controls.FilterBar>(reports).First();
        shell.UpdateLayout(700);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(bar.IsCollapsed, "narrow: chips collapse into „Filtry”");
        shell.UpdateLayout(1400);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(!bar.IsCollapsed, "wide: chips back in the bar");

        // Blocks separators and Projects top chart.
        await shell.NavigateAsync(Screen.Blocks);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var blocks = ElementTree.Find<Controls.DataTable>(shell.Navigation.CurrentView).First();
        var items = blocks.ItemsSource?.Cast<object>().ToList() ?? [];
        Diagnostics.Trace($"ui: blocks rows {items.Count}, separators {items.Count(item => item is Controls.ISeparatorRow { IsSeparator: true })}");
        KeyboardRouter.Target = blocks;
        for (var i = 0; i < items.Count; i++)
        {
            KeyboardRouter.Route(KeyboardAction.Down);
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Diagnostics.Trace($"ui: down {i} → {(blocks.SelectedItem as ViewModels.BlockItemViewModel)?.Title}");
        }

        Check(blocks.SelectedItem is not Controls.ISeparatorRow { IsSeparator: true }, "keyboard skips separators");

        await shell.NavigateAsync(Screen.Projects);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var top = ElementTree.Find<Controls.Charts.HorizontalBarChart>(shell.Navigation.CurrentView).FirstOrDefault();
        Check(top is not null && top.Data.Categories.Count > 0, $"Top projekty chart has {top?.Data.Categories.Count ?? 0} rows");

        // Data layer (Faza 12.5 pkt 2): trends, hourly heatmap, project shares, block and session series.
        await shell.NavigateAsync(Screen.Reports);
        await Task.Delay(TimeSpan.FromSeconds(4));
        var reportsVm = (ViewModels.ReportsViewModel)shell.Navigation.CurrentView!.BindingContext;
        Check(reportsVm.Hourly is not null, "Raporty: hourly heatmap data");
        Diagnostics.Trace($"ui: previous range cost {reportsVm.PreviousCost} ({reportsVm.PreviousLabel}), total {reportsVm.TotalCostValue}");
        reportsVm.ShareIndex = 2;
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Check(reportsVm.Share.Series.Count > 0, $"Raporty: project share has {reportsVm.Share.Series.Count} series");
        reportsVm.ShareIndex = 0;
        reportsVm.ActivityModeIndex = 1;
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Check(ElementTree.FirstVisible<Controls.Charts.HourHeatmap>(shell.Navigation.CurrentView) is not null, "Raporty: days × hours view visible");
        reportsVm.ActivityModeIndex = 0;

        await shell.NavigateAsync(Screen.Blocks);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var blockRows = ((ViewModels.BlocksViewModel)shell.Navigation.CurrentView!.BindingContext).Blocks.Where(block => !block.IsGap).ToList();
        Check(blockRows.Count == 0 || blockRows[0].Series.Count > 2, $"Bloki: timeline samples {blockRows.FirstOrDefault()?.Series.Count ?? 0}, sessions {blockRows.FirstOrDefault()?.Details?.Sessions.Count ?? 0}");

        await shell.NavigateAsync(Screen.Today);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var todayVm = (ViewModels.MainViewModel)shell.Navigation.CurrentView!.BindingContext;
        Diagnostics.Trace($"ui: today yesterday {todayVm.YesterdayCost:0.00}, avg7 {todayVm.WeekAverageCost:0.00}, week series {todayVm.WeekSeries.Count}, block samples {todayVm.BlockSeries.Count}");
        Check(todayVm.MonthSeries.Count == 30, "Dziś: 30-day series");

        await shell.NavigateAsync(Screen.Sessions);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var sessionsVm = (ViewModels.SessionsViewModel)shell.Navigation.CurrentView!.BindingContext;
        if (sessionsVm.Sessions.FirstOrDefault() is { } firstSession)
        {
            sessionsVm.OpenDetails(firstSession);
            await Task.Delay(TimeSpan.FromSeconds(2));
            var timeline = DrawerService.Current?.Content is { } drawerContent ? ElementTree.Find<Controls.Charts.LineChart>(drawerContent).FirstOrDefault() : null;
            Check(timeline?.State == Controls.Charts.ChartState.Ready && timeline.Data.Series[0].Points.Count > 1, $"Sesje: timeline {timeline?.State}");
            Diagnostics.Trace($"ui: active sessions {sessionsVm.Sessions.Count(item => item.IsActive)}");
            DrawerService.Close();
        }

        // Budgets, currency, statusline preview, limit history (Faza 12.5 pkt 2c). The config is swapped in memory only.
        var original = AppState.Config;
        var trial = original.Clone();
        trial.Budgets.DailyUsd = 1;
        trial.Budgets.MonthlyUsd = 10;
        trial.Budgets.PerSource["claude"] = new Core.Config.SourceBudget { DailyUsd = 1 };
        AppState.Engine.UpdateConfig(trial);
        try
        {
            await AppState.RefreshDataAsync(force: true);
            Check(AppState.Budgets?.HasAny == true, $"budgets evaluated: peak {AppState.Budgets?.PeakPercent:0}%");
            await shell.NavigateAsync(Screen.Limits);
            await Task.Delay(TimeSpan.FromSeconds(3));
            var limitsVm = (ViewModels.LimitsViewModel)shell.Navigation.CurrentView!.BindingContext;
            await limitsVm.RefreshAsync();
            Check(limitsVm.HasBudgets && limitsVm.SourceBudgets.Count == 1, $"Limity: budgets section ({limitsVm.MonthlyForecastText})");
            Diagnostics.Trace($"ui: limit history {limitsVm.HasHistory}, forecast „{limitsVm.FiveHourForecast}”");
        }
        finally
        {
            AppState.Engine.UpdateConfig(original);
            await AppState.RefreshDataAsync();
        }

        var currencyTrial = original.Clone();
        currencyTrial.Currency.Display = "PLN";
        var nbp = await Core.Pricing.ExchangeRates.FetchNbpAsync(currencyTrial);
        Diagnostics.Trace($"ui: NBP rate {(nbp is null ? "unavailable (offline?)" : $"{nbp.Rate} from {nbp.Date}")}");
        ViewModels.Fmt.Currency = new ViewModels.DisplayCurrency("PLN", nbp?.Rate ?? 4, nbp?.Date);
        Check(ViewModels.Fmt.Cost(10).Contains("zł", StringComparison.Ordinal), $"currency: {ViewModels.Fmt.Cost(10)}");
        ViewModels.Fmt.Currency = ViewModels.DisplayCurrency.Usd;

        var statusline = await ViewModels.StatuslinePreview.RenderAsync(string.Empty);
        Check(statusline.Length > 0, "statusline preview: " + statusline);

        Diagnostics.Trace($"ui selftest done: {failures} failures");
    }

    public static async Task PaletteAsync(ShellViewModel shell)
    {
        var failures = 0;
        void Check(bool condition, string what)
        {
            Diagnostics.Trace($"palette: {(condition ? "ok" : "FAIL")} {what}");
            failures += condition ? 0 : 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Palette));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(CommandPaletteService.IsOpen, "opens with Ctrl+K");

        var empty = CommandPaletteService.Search(string.Empty);
        Check(empty.Any(section => section.Group == PaletteGroup.Navigate) && empty.Any(section => section.Group == PaletteGroup.Actions),
            "empty query: Przejdź do + Akcje");
        Check(CommandPaletteService.Search("zrodla").FirstOrDefault()?.Results.FirstOrDefault()?.Item.Title == "Źródła", "„zrodla” → Źródła first");
        Check(CommandPaletteService.Search(">mot").All(section => section.Group == PaletteGroup.Actions), "„>” limits to actions");
        Check(CommandPaletteService.Search("cennik").SelectMany(section => section.Results).Any(result => result.Item.Title == "Ustawienia → Cennik"),
            "Settings sub-pages are searchable");

        await Task.Delay(TimeSpan.FromSeconds(3)); // entity index
        var sessions = CommandPaletteService.Search("#").FirstOrDefault(section => section.Group == PaletteGroup.Sessions);
        Diagnostics.Trace($"palette: {sessions?.Results.Count ?? 0} session results for „#”");
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Escape));
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Palette));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        CommandPaletteService.Close();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Check(!CommandPaletteService.IsOpen, "closes");

        var reports = CommandPaletteService.Search("raporty").SelectMany(section => section.Results).First(result => result.Item.Id == "nav:Reports");
        await CommandPaletteService.ExecuteAsync(reports.Item);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Check(shell.Navigation.Current == Screen.Reports, "navigates to Raporty");
        Check(CommandPaletteService.Search(string.Empty).FirstOrDefault()?.Group == PaletteGroup.Recent, "„Ostatnie” remembered");

        if (sessions?.Results.FirstOrDefault(result => result.Item.Id.StartsWith("session:", StringComparison.Ordinal)) is { } session)
        {
            await CommandPaletteService.ExecuteAsync(session.Item);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Check(shell.Navigation.Current == Screen.Sessions && DrawerService.IsOpen, "session opens Sesje with drawer");
            DrawerService.Close();
        }

        foreach (var prefix in new[] { "project:", "model:", "source:" })
        {
            var entity = CommandPaletteService.Search(prefix == "project:" ? "@" : "a").SelectMany(section => section.Results)
                .FirstOrDefault(result => result.Item.Id.StartsWith(prefix, StringComparison.Ordinal));
            if (entity is null)
            {
                Diagnostics.Trace($"palette: no {prefix} entity to open");
                continue;
            }

            await CommandPaletteService.ExecuteAsync(entity.Item);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Check(DrawerService.IsOpen, $"{prefix} opens drawer on {shell.Navigation.Current}");
            DrawerService.Close();
        }

        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Shortcuts));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(DialogService.Current?.Title == "Skróty klawiszowe", "„?” opens the shortcut list");
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Escape));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Check(DialogService.Current is null, "Esc closes the dialog");

        await shell.NavigateAsync(Screen.Sessions);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Search));
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.Export));
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Check(Controls.PopoverService.IsOpen, "Ctrl+E opens the export menu");
        Controls.PopoverService.Close();

        // M18: sidebar collapse animates the column; M17: theme crossfade leaves no snapshot behind.
        var before = shell.SidebarWidth;
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.ToggleSidebar));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Check(shell.SidebarWidth != before, $"Ctrl+B changes the sidebar ({shell.Layout}: {before} → {shell.SidebarWidth})");
        ShortcutService.Raise(new ShortcutAction(ShortcutCommand.ToggleSidebar));
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        var page = Application.Current?.Windows.FirstOrDefault()?.Page as MainShellPage;
        var children = page?.RootChildCount ?? -1;
        var target = Design.ThemeService.EffectiveTheme == AppTheme.Dark ? Core.Config.ThemePreference.Light : Core.Config.ThemePreference.Dark;
        Design.ThemeService.Apply(target);
        var started = DateTime.UtcNow;
        while (page?.RootChildCount == children && DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        var appeared = DateTime.UtcNow - started;
        while (page?.RootChildCount != children && DateTime.UtcNow - started < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Diagnostics.Trace($"palette: snapshot after {appeared.TotalMilliseconds:0} ms, crossfade done after {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        Check(Design.ThemeService.EffectiveTheme.ToString() == target.ToString(), "theme switched under the crossfade");
        Check(page?.RootChildCount == children, $"crossfade snapshot removed ({children} → {page?.RootChildCount}: {page?.RootChildren})");
        Design.ThemeService.Apply(AppState.Config.Appearance.Theme);
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        Diagnostics.Trace($"palette selftest done: {failures} failures");
    }
}
#endif
