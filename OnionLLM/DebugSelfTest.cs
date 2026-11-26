#if DEBUG
using OnionLLM.Navigation;

namespace OnionLLM;

/// <summary>
/// DEBUG-only runtime checks of global surfaces (Faza 12.4), traced to <c>ONIONLLM_DIAG_LOG</c>:
/// <c>ONIONLLM_PALETTE_SELFTEST=1</c> → expect „palette selftest done”.
/// </summary>
internal static class DebugSelfTest
{
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
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Check(Design.ThemeService.EffectiveTheme.ToString() == target.ToString(), "theme switched under the crossfade");
        Check(page?.RootChildCount == children, "crossfade snapshot removed");
        Design.ThemeService.Apply(AppState.Config.Appearance.Theme);
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        Diagnostics.Trace($"palette selftest done: {failures} failures");
    }
}
#endif
