using OnionLLM.Core.Reports;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Navigation;

/// <summary>Drill-down parameter: open the drawer of one entity (session ID, project name, model, source ID).</summary>
public sealed record EntityTarget(string Id);

/// <summary>Drill-down parameter: put a query into the screen's SearchField.</summary>
public sealed record SearchRequest(string Query);

/// <summary>Drill-down parameter: run a screen action (export, „Przebuduj bazę…”), see <see cref="ScreenActions"/>.</summary>
public sealed record ScreenAction(string Name);

public static class ScreenActions
{
    public const string ExportCsv = "export-csv";
    public const string ExportJson = "export-json";
    public const string RefreshPricing = "refresh-pricing";
    public const string CheckMismatches = "check-mismatches";
    public const string Rebuild = "rebuild";
}

/// <summary>Result groups of the palette in display order (DESIGN.md §4.6.7).</summary>
public enum PaletteGroup
{
    Recent,
    Navigate,
    Actions,
    Sessions,
    Projects,
    Models,
    Sources
}

/// <summary>One command or entity in the palette.</summary>
public sealed class PaletteItem
{
    /// <summary>Stable id for „Ostatnie” (e.g. <c>nav:Reports</c>, <c>session:abc</c>).</summary>
    public required string Id { get; init; }

    public required PaletteGroup Group { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Extra text matched but not shown (session ID, models, keywords).</summary>
    public string Keywords { get; init; } = string.Empty;

    public string Glyph { get; init; } = Icons.ChevronRight;

    /// <summary>Source colour of an entity row (SourceBadge instead of the icon).</summary>
    public string? SourceId { get; init; }

    public string? Shortcut { get; init; }

    public required Func<Task> Run { get; init; }

    /// <summary>Not remembered in „Ostatnie” (e.g. „Pokaż wszystkie w Sesjach”).</summary>
    public bool Transient { get; init; }
}

/// <summary>A matched item with the title characters to render in bold.</summary>
public sealed record PaletteResult(PaletteItem Item, int Score, IReadOnlyList<int> Positions);

public sealed record PaletteSection(PaletteGroup Group, string Header, IReadOnlyList<PaletteResult> Results);

/// <summary>
/// Command palette (§4.6.7, Ctrl+K): navigation, actions and entity search. Every palette command also has its own
/// place in the UI — the palette is a shortcut, never the only way.
/// </summary>
public static class CommandPaletteService
{
    private const string RecentKey = "palette.recent";
    private const int RecentLimit = 5;
    private const int SessionLimit = 5;
    private const int EntityLimit = 3;

    private static IReadOnlyList<PaletteItem> _entities = [];
    private static bool _indexStale = true;
    private static Task? _indexing;

    static CommandPaletteService()
    {
        AppState.SyncStateChanged += (_, _) =>
        {
            if (!AppState.IsSyncing)
            {
                _indexStale = true;
            }
        };
        AppState.Changed += (_, _) => _indexStale = true;
    }

    public static event EventHandler? Changed;

    /// <summary>Raised when the entity index finished building (the open palette re-runs its query).</summary>
    public static event EventHandler? IndexUpdated;

    public static bool IsOpen { get; private set; }

    public static void Open()
    {
        if (IsOpen || DialogService.Current is not null)
        {
            return;
        }

        Controls.PopoverService.Close();
        IsOpen = true;
        Changed?.Invoke(null, EventArgs.Empty);
        if (_indexStale && _indexing is null)
        {
            _indexing = BuildIndexAsync();
        }
    }

    public static void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Closes the palette, remembers the command and runs it.</summary>
    public static async Task ExecuteAsync(PaletteItem item)
    {
        Close();
        if (!item.Transient)
        {
            Remember(item.Id);
        }

        await item.Run();
    }

    /// <summary>
    /// Groups in §4.6.7 order. Empty query: Ostatnie · Przejdź do · Akcje. Prefixes: <c>&gt;</c> actions only,
    /// <c>#</c> sessions, <c>@</c> projects.
    /// </summary>
    public static IReadOnlyList<PaletteSection> Search(string query)
    {
        var text = query.Trim();
        PaletteGroup? only = null;
        if (text.Length > 0 && text[0] is '>' or '#' or '@')
        {
            only = text[0] switch { '>' => PaletteGroup.Actions, '#' => PaletteGroup.Sessions, _ => PaletteGroup.Projects };
            text = text[1..].Trim();
        }

        var commands = Commands();
        var sections = new List<PaletteSection>();
        if (text.Length == 0 && only is null)
        {
            var all = commands.Concat(_entities).ToList();
            var recent = RecentIds()
                .Select(id => all.FirstOrDefault(item => item.Id == id))
                .OfType<PaletteItem>()
                .Select(item => new PaletteResult(item, 0, []))
                .ToList();
            Add(sections, PaletteGroup.Recent, recent);
            Add(sections, PaletteGroup.Navigate, commands.Where(item => item.Group == PaletteGroup.Navigate).Select(item => new PaletteResult(item, 0, [])).ToList());
            Add(sections, PaletteGroup.Actions, commands.Where(item => item.Group == PaletteGroup.Actions).Select(item => new PaletteResult(item, 0, [])).ToList());
            return sections;
        }

        var candidates = commands.Concat(text.Length > 0 ? _entities : only is PaletteGroup.Sessions or PaletteGroup.Projects ? _entities : []);
        var matched = candidates
            .Where(item => only is null || item.Group == only)
            .Select(item => Score(text, item))
            .OfType<PaletteResult>()
            .GroupBy(result => result.Item.Group)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(result => result.Score).ToList());

        foreach (var group in new[] { PaletteGroup.Navigate, PaletteGroup.Actions, PaletteGroup.Sessions, PaletteGroup.Projects, PaletteGroup.Models, PaletteGroup.Sources })
        {
            if (!matched.TryGetValue(group, out var results))
            {
                continue;
            }

            var limit = group switch
            {
                PaletteGroup.Sessions => SessionLimit,
                PaletteGroup.Projects or PaletteGroup.Models or PaletteGroup.Sources when only is null => EntityLimit,
                _ => int.MaxValue
            };
            var shown = results.Take(limit).ToList();
            if (group == PaletteGroup.Sessions && results.Count > limit)
            {
                var search = text;
                shown.Add(new PaletteResult(new PaletteItem
                {
                    Id = "sessions:all",
                    Group = PaletteGroup.Sessions,
                    Title = "Pokaż wszystkie w Sesjach →",
                    Subtitle = Fmt.Count(results.Count, "dopasowanie", "dopasowania", "dopasowań"),
                    Glyph = Icons.SquareTerminal,
                    Transient = true,
                    Run = () => Shell.NavigateAsync(Screen.Sessions, new SearchRequest(search))
                }, 0, []));
            }

            Add(sections, group, shown);
        }

        return sections;
    }

    private static ShellViewModel Shell => ShellViewModel.Instance;

    private static PaletteResult? Score(string query, PaletteItem item)
    {
        if (FuzzyMatch.Match(query, item.Title) is { } title)
        {
            return new PaletteResult(item, title.Score, title.Positions);
        }

        // Subtitle / hidden keywords match too, below any title match (no bold characters to show).
        var extra = item.Subtitle + " " + item.Keywords;
        return FuzzyMatch.Match(query, extra) is { } other ? new PaletteResult(item, other.Score - 400, []) : null;
    }

    private static void Add(List<PaletteSection> sections, PaletteGroup group, IReadOnlyList<PaletteResult> results)
    {
        if (results.Count > 0)
        {
            sections.Add(new PaletteSection(group, Header(group), results));
        }
    }

    private static string Header(PaletteGroup group) => group switch
    {
        PaletteGroup.Recent => "Ostatnie",
        PaletteGroup.Navigate => "Przejdź do",
        PaletteGroup.Actions => "Akcje",
        PaletteGroup.Sessions => "Sesje",
        PaletteGroup.Projects => "Projekty",
        PaletteGroup.Models => "Modele",
        _ => "Źródła"
    };

    // ===== Commands: screens, Settings sub-pages, actions =====

    private static List<PaletteItem> Commands()
    {
        var items = new List<PaletteItem>();
        foreach (var info in ScreenInfo.All.Where(info => info.Screen != Screen.Gallery && Shell.Navigation.IsAvailable(info.Screen)))
        {
            items.Add(new PaletteItem
            {
                Id = "nav:" + info.Screen,
                Group = PaletteGroup.Navigate,
                Title = info.Label,
                Glyph = info.Glyph,
                Shortcut = info.Shortcut,
                Run = () => Shell.NavigateAsync(info.Screen)
            });
        }

        foreach (var (page, keywords) in Views.SettingsView.PageIndex)
        {
            items.Add(new PaletteItem
            {
                Id = "settings:" + page,
                Group = PaletteGroup.Navigate,
                Title = "Ustawienia → " + page,
                Keywords = keywords,
                Glyph = Icons.Settings,
                Run = () => Shell.NavigateAsync(Screen.Settings, page)
            });
        }

        var config = AppState.Config;
        items.Add(Action("refresh", "Odśwież dane", Icons.RefreshCw, "F5", () => Shell.RefreshAsync(force: true)));
        items.Add(Action("export-csv", "Eksportuj raport CSV", Icons.Download, null, () => Shell.NavigateAsync(Screen.Reports, new ScreenAction(ScreenActions.ExportCsv))));
        items.Add(Action("export-json", "Eksportuj raport JSON", Icons.Download, null, () => Shell.NavigateAsync(Screen.Reports, new ScreenAction(ScreenActions.ExportJson))));
        items.Add(Action("theme", "Przełącz motyw", Icons.SunMoon, ShortcutService.Label("Shift+L"), Sync(WindowActions.CycleTheme),
            "Teraz: " + WindowActions.ThemeLabel(config.Appearance.Theme)));
        items.Add(Action("density", "Przełącz gęstość", Icons.Rows3, null, Sync(WindowActions.ToggleDensity),
            "Teraz: " + (config.Appearance.Density == Core.Config.DensityPreference.Compact ? "kompaktowa" : "komfortowa")));
        items.Add(Action("top", "Zawsze na wierzchu", Icons.Pin, ShortcutService.Label("Shift+T"), Sync(WindowActions.ToggleAlwaysOnTop),
            config.Window.AlwaysOnTop ? "Włączone" : "Wyłączone"));
        items.Add(Action("pricing", "Odśwież cennik", Icons.Tag, null, () => Shell.NavigateAsync(Screen.Settings, new ScreenAction(ScreenActions.RefreshPricing))));
        items.Add(Action("mismatches", "Sprawdź rozbieżności kosztu", Icons.Scale, null, () => Shell.NavigateAsync(Screen.Settings, new ScreenAction(ScreenActions.CheckMismatches))));
        items.Add(Action("data-folder", "Otwórz folder danych", Icons.FolderOpen, null, WindowActions.OpenDataFolderAsync));
        items.Add(Action("rebuild", "Przebuduj bazę…", Icons.DatabaseZap, null, () => Shell.NavigateAsync(Screen.Settings, new ScreenAction(ScreenActions.Rebuild))));
        items.Add(Action("shortcuts", "Skróty klawiszowe", Icons.Keyboard, "?", ShortcutHelp.ShowAsync));
        return items;

        static PaletteItem Action(string id, string title, string glyph, string? shortcut, Func<Task> run, string subtitle = "") => new()
        {
            Id = "action:" + id,
            Group = PaletteGroup.Actions,
            Title = title,
            Subtitle = subtitle,
            Glyph = glyph,
            Shortcut = shortcut,
            Run = run
        };

        static Func<Task> Sync(Action action) => () =>
        {
            action();
            return Task.CompletedTask;
        };
    }

    // ===== Entity index: sessions, projects and models of the last 30 days; all sources =====

    private static async Task BuildIndexAsync()
    {
        try
        {
            var (since, until) = DateRanges.Resolve(RangePreset.Last30Days, DateTime.Today, DateTime.Today);
            var report = await Task.Run(() => AppState.Engine.BuildSessions(new ReportQuery { Since = since, Until = until, Order = SortOrder.Desc }));
            var items = new List<PaletteItem>();
            foreach (var row in report.Sessions.OrderByDescending(row => row.LastActivity))
            {
                var source = AppState.Engine.FindSource(row.Source)?.DisplayName ?? row.Source;
                var id = row.SessionId;
                items.Add(new PaletteItem
                {
                    Id = "session:" + id,
                    Group = PaletteGroup.Sessions,
                    Title = string.IsNullOrWhiteSpace(row.Project) ? Fmt.SessionId(id, 40) : row.Project!,
                    Subtitle = $"{Fmt.SessionId(id)} · {source} · {Fmt.DateTimeShort(row.LastActivity)}",
                    Keywords = id + " " + string.Join(" ", row.Models),
                    Glyph = Icons.SquareTerminal,
                    SourceId = row.Source,
                    Run = () => Shell.NavigateAsync(Screen.Sessions, new EntityTarget(id))
                });
            }

            foreach (var project in report.Sessions.Where(row => !string.IsNullOrWhiteSpace(row.Project))
                         .GroupBy(row => row.Project!, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Sum(row => row.Totals.CostUsd)))
            {
                var name = project.Key;
                items.Add(new PaletteItem
                {
                    Id = "project:" + name,
                    Group = PaletteGroup.Projects,
                    Title = name,
                    Subtitle = project.First().ProjectPath ?? string.Empty,
                    Glyph = Icons.FolderKanban,
                    Run = () => Shell.NavigateAsync(Screen.Projects, new EntityTarget(name))
                });
            }

            foreach (var model in report.Sessions.SelectMany(row => row.Models).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new PaletteItem
                {
                    Id = "model:" + model,
                    Group = PaletteGroup.Models,
                    Title = Fmt.Model(model),
                    Keywords = model,
                    Glyph = Icons.Cpu,
                    Run = () => Shell.NavigateAsync(Screen.Models, new EntityTarget(model))
                });
            }

            foreach (var source in AppState.Engine.Sources)
            {
                var id = source.Id;
                items.Add(new PaletteItem
                {
                    Id = "source:" + id,
                    Group = PaletteGroup.Sources,
                    Title = source.DisplayName,
                    Subtitle = AppState.Config.IsSourceEnabled(id) ? "Włączone" : "Wyłączone",
                    Keywords = id,
                    Glyph = Icons.Plug,
                    SourceId = id,
                    Run = () => Shell.NavigateAsync(Screen.Sources, new EntityTarget(id))
                });
            }

            _entities = items;
            _indexStale = false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Entities are optional: the palette still navigates and runs actions.
            global::OnionLLM.Diagnostics.Trace("palette index failed: " + exception.Message);
        }
        finally
        {
            _indexing = null;
            MainThread.BeginInvokeOnMainThread(() => IndexUpdated?.Invoke(null, EventArgs.Empty));
        }
    }

    // ===== „Ostatnie” =====

    private static List<string> RecentIds() =>
        ViewModels.ViewState.GetString(RecentKey, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void Remember(string id)
    {
        var ids = RecentIds();
        ids.Remove(id);
        ids.Insert(0, id);
        ViewModels.ViewState.Set(RecentKey, string.Join('\n', ids.Take(RecentLimit)));
    }
}
