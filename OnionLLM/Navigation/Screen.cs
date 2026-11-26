using OnionLLM.Resources;

namespace OnionLLM.Navigation;

/// <summary>Screens of the main window (DESIGN.md §6). Order = Ctrl+1…8 (§9.6).</summary>
public enum Screen
{
    Today,
    Limits,
    Reports,
    Sessions,
    Blocks,
    Projects,
    Models,
    Sources,
    Settings,

    /// <summary>DEBUG-only component gallery (Faza 12.2).</summary>
    Gallery
}

/// <summary>Sidebar group (§4.7.1).</summary>
public enum NavGroup
{
    Overview,
    Analysis,
    System,
    Footer
}

/// <summary>Static description of a screen: label (§8.6), Lucide icon (§11.2), shortcut (§9.6), sidebar group.</summary>
public sealed record ScreenInfo(Screen Screen, string Label, string Glyph, string Shortcut, NavGroup Group)
{
    public static readonly IReadOnlyList<ScreenInfo> All =
    [
        new(Screen.Today, "Dziś", Icons.LayoutDashboard, "Ctrl+1", NavGroup.Overview),
        new(Screen.Limits, "Limity", Icons.Gauge, "Ctrl+2", NavGroup.Overview),
        new(Screen.Reports, "Raporty", Icons.ChartColumn, "Ctrl+3", NavGroup.Analysis),
        new(Screen.Sessions, "Sesje", Icons.SquareTerminal, "Ctrl+4", NavGroup.Analysis),
        new(Screen.Blocks, "Bloki", Icons.Timer, "Ctrl+5", NavGroup.Analysis),
        new(Screen.Projects, "Projekty", Icons.FolderKanban, "Ctrl+6", NavGroup.Analysis),
        new(Screen.Models, "Modele", Icons.Cpu, "Ctrl+7", NavGroup.Analysis),
        new(Screen.Sources, "Źródła", Icons.Plug, "Ctrl+8", NavGroup.System),
        new(Screen.Settings, "Ustawienia", Icons.Settings, ShortcutService.SettingsShortcutLabel, NavGroup.Footer),
        new(Screen.Gallery, "Galeria komponentów", Icons.LayoutGrid, "", NavGroup.Footer)
    ];

    public static ScreenInfo Of(Screen screen) => All.First(info => info.Screen == screen);
}

/// <summary>Implemented by screen views hosted in the main window.</summary>
public interface IScreenView
{
    /// <summary>The screen became visible; <paramref name="parameter"/> carries drill-down context (§5.5).</summary>
    Task OnNavigatedToAsync(object? parameter);

    void OnNavigatedFrom();

    /// <summary>Reload data (auto refresh, F5). <paramref name="force"/> = user asked explicitly.</summary>
    Task RefreshAsync(bool force);
}
