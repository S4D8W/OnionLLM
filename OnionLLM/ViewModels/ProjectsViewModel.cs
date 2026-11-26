using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;
using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

/// <summary>Row of the „Projekty” table (DESIGN.md §6.6).</summary>
public sealed class ProjectRowViewModel
{
    public const string NoProject = "(bez projektu)";

    public required string Name { get; init; }

    public string Path { get; init; } = string.Empty;

    public required TokenCounts Totals { get; init; }

    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public int SessionCount { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Daily cost in the range (oldest first) — the trend sparkline.</summary>
    public IReadOnlyList<double> Trend { get; init; } = [];

    public IReadOnlyList<ModelBreakdown> Models { get; init; } = [];

    public IReadOnlyList<SourceBreakdown> Sources { get; init; } = [];

    public IReadOnlyList<SessionRow> RecentSessions { get; init; } = [];

    public string Marker { get; init; } = string.Empty;

    public bool IsNoProject => Name == NoProject;

    public string Sessions => Fmt.Number(SessionCount);

    public string LastActivityText => LastActivity is { } last ? Fmt.Relative(last) : "—";

    public string Tokens => Fmt.Tokens(Totals.TotalTokens);

    public string Cost => Fmt.Cost(Totals.CostUsd);

    public double CostShare { get; set; }

    public string ShareText { get; set; } = string.Empty;
}

/// <summary>Screen „Projekty” (DESIGN.md §6.6).</summary>
public sealed class ProjectsViewModel : ObservableObject
{
    private const string StateKey = "projects.";
    private static readonly RangePreset[] PresetOrder = Enum.GetValues<RangePreset>();
    private int _rangeIndex = (int)RangePreset.Last30Days;
    private int _sourceIndex;
    private string _search = string.Empty;
    private string _sortKey = nameof(ProjectRowViewModel.Cost);
    private bool _sortDescending = true;
    private DateTime _since = DateTime.Today.AddDays(-29);
    private DateTime _until = DateTime.Today;
    private bool _isBusy;
    private bool _loaded;
    private List<ProjectRowViewModel> _all = [];
    private IReadOnlyList<ProjectRowViewModel> _rows = [];
    private ProjectRowViewModel? _selected;
    private IReadOnlyList<DateOnly> _days = [];

    public ProjectsViewModel()
    {
        SourceChoices = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];
        SourceChoiceNames = SourceChoices.Select(choice => choice.Name).ToArray();
        _rangeIndex = ViewState.GetInt(StateKey + "preset", (int)RangePreset.Last30Days, PresetOrder.Length - 1);
        OpenDetailsCommand = new Command<ProjectRowViewModel>(OpenDetails);
        ResetFiltersCommand = new Command(() => { RangeIndex = (int)RangePreset.Last30Days; SourceIndex = 0; Search = string.Empty; OnPropertyChanged(nameof(Search)); });
        ExportCsvCommand = new Command(async () => await ExportService.CopyTableAsync(TableRows()));
    }

    public string[] Presets { get; } = PresetOrder.Select(DateRanges.Label).ToArray();

    public int CustomPresetIndex => (int)RangePreset.Custom;

    public ChoiceItem[] SourceChoices { get; }

    public string[] SourceChoiceNames { get; }

    public Command<ProjectRowViewModel> OpenDetailsCommand { get; }

    public Command ResetFiltersCommand { get; }

    public Command ExportCsvCommand { get; }

    public int RangeIndex { get => _rangeIndex; set { if (Set(ref _rangeIndex, value)) { ViewState.Set(StateKey + "preset", value); _ = RefreshAsync(); } } }

    public int SourceIndex { get => _sourceIndex; set { if (Set(ref _sourceIndex, value)) { _ = RefreshAsync(); } } }

    public DateTime Since { get => _since; set { if (Set(ref _since, value)) { _ = RefreshAsync(); } } }

    public DateTime Until { get => _until; set { if (Set(ref _until, value)) { _ = RefreshAsync(); } } }

    public string Search { get => _search; set { if (Set(ref _search, value)) { Apply(); } } }

    public string SortKey { get => _sortKey; set { if (Set(ref _sortKey, value)) { Apply(); } } }

    public bool SortDescending { get => _sortDescending; set { if (Set(ref _sortDescending, value)) { Apply(); } } }

    public bool IsFiltered => RangeIndex != (int)RangePreset.Last30Days || SourceIndex != 0 || Search.Length > 0;

    public IReadOnlyList<ProjectRowViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }

    public ProjectRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    /// <summary>Top 10 by cost for the „Top projekty” card.</summary>
    public IReadOnlyList<ProjectRowViewModel> Top { get; private set; } = [];

    public string Subtitle { get; private set; } = string.Empty;

    public string RangeText { get; private set; } = string.Empty;

    public string PricingNote { get; private set; } = string.Empty;

    public TableState TableState => !_loaded ? TableState.Loading : Rows.Count > 0 ? TableState.Ready : _all.Count > 0 ? TableState.NoResults : TableState.Empty;

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string? SourceId => _sourceIndex > 0 && _sourceIndex < SourceChoices.Length ? SourceChoices[_sourceIndex].Id : null;

    private RangePreset Preset => PresetOrder[Math.Clamp(RangeIndex, 0, PresetOrder.Length - 1)];

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await AppState.RefreshDataAsync();
            var (since, until) = DateRanges.Resolve(Preset, Since, Until);
            var query = new ReportQuery
            {
                Since = since,
                Until = until,
                Sources = SourceId is null ? Array.Empty<string>() : [SourceId],
                Breakdown = true,
                BySource = true,
                GroupByProject = true
            };
            var daily = await Task.Run(() => AppState.Engine.BuildReport(ReportPeriod.Daily, query));
            var sessions = await Task.Run(() => AppState.Engine.BuildSessions(query with { GroupByProject = false, BySource = false }));
            var first = since ?? daily.Rows.Select(row => (DateOnly?)row.PeriodStart).Min() ?? until;
            _days = Enumerable.Range(0, until.DayNumber - first.DayNumber + 1).Select(first.AddDays).ToList();
            RangeText = DateRanges.Describe(Preset, since, until);

            var sessionsByProject = sessions.Sessions.GroupBy(session => session.Project ?? ProjectRowViewModel.NoProject)
                .ToDictionary(group => group.Key, group => group.ToList());
            _all = daily.Rows.GroupBy(row => row.Project ?? ProjectRowViewModel.NoProject).Select(group =>
            {
                var rows = group.ToList();
                var projectSessions = sessionsByProject.GetValueOrDefault(group.Key) ?? [];
                var byDay = rows.GroupBy(row => row.PeriodStart).ToDictionary(g => g.Key, g => g.Sum(row => row.Totals.CostUsd));
                return new ProjectRowViewModel
                {
                    Name = group.Key,
                    Path = projectSessions.Select(session => session.ProjectPath).FirstOrDefault(path => !string.IsNullOrEmpty(path)) ?? string.Empty,
                    Totals = Sum(rows.Select(row => row.Totals)),
                    SourceIds = rows.SelectMany(row => row.Sources).Distinct().ToList(),
                    SessionCount = projectSessions.Count,
                    LastActivity = projectSessions.Count == 0 ? null : projectSessions.Max(session => session.LastActivity),
                    Trend = _days.Select(day => byDay.GetValueOrDefault(day)).ToList(),
                    Models = rows.SelectMany(row => row.ModelBreakdowns).GroupBy(m => m.Model)
                        .Select(g => new ModelBreakdown(g.Key, Sum(g.Select(m => m.Totals)), g.Any(m => m.IsUnpriced), g.Any(m => m.IsEstimated)))
                        .OrderByDescending(m => m.Totals.CostUsd).ToList(),
                    Sources = rows.SelectMany(row => row.SourceBreakdowns).GroupBy(s => s.Source)
                        .Select(g => new SourceBreakdown(g.Key, Sum(g.Select(s => s.Totals)))).OrderByDescending(s => s.Totals.CostUsd).ToList(),
                    RecentSessions = projectSessions.OrderByDescending(session => session.LastActivity).Take(5).ToList(),
                    Marker = Fmt.Marker(rows.SelectMany(row => row.UnpricedModels).Distinct().ToArray(), rows.SelectMany(row => row.EstimatedModels).Distinct().ToArray())
                };
            }).ToList();
            var total = _all.Sum(project => project.Totals.CostUsd);
            var max = _all.Select(project => project.Totals.CostUsd).DefaultIfEmpty(0).Max();
            foreach (var project in _all)
            {
                project.CostShare = max > 0 ? project.Totals.CostUsd / max : 0;
                project.ShareText = total > 0 ? Fmt.Percent(project.Totals.CostUsd * 100 / total) : "";
            }

            Top = _all.Where(project => !project.IsNoProject).OrderByDescending(project => project.Totals.CostUsd).Take(10).ToList();
            var named = _all.Count(project => !project.IsNoProject);
            Subtitle = $"{Fmt.Count(named, "projekt", "projekty", "projektów")} · {RangeText}";
            PricingNote = Fmt.PricingNote(daily.UnpricedModels, daily.EstimatedModels);
            _loaded = true;
            Apply();
        }
        finally
        {
            IsBusy = false;
            foreach (var name in new[] { nameof(Top), nameof(Subtitle), nameof(RangeText), nameof(PricingNote), nameof(TableState), nameof(IsFiltered) })
            {
                OnPropertyChanged(name);
            }
        }
    }

    private void Apply()
    {
        IEnumerable<ProjectRowViewModel> rows = _all;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Select.Normalize(Search.Trim());
            rows = rows.Where(row => Select.Normalize(row.Name).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     row.Path.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<ProjectRowViewModel> sorted = SortKey switch
        {
            nameof(ProjectRowViewModel.Name) => rows.OrderBy(row => row.Name, StringComparer.CurrentCulture),
            nameof(ProjectRowViewModel.Sessions) => rows.OrderBy(row => row.SessionCount),
            nameof(ProjectRowViewModel.LastActivityText) => rows.OrderBy(row => row.LastActivity),
            nameof(ProjectRowViewModel.Tokens) => rows.OrderBy(row => row.Totals.TotalTokens),
            _ => rows.OrderBy(row => row.Totals.CostUsd)
        };
        var list = (SortDescending ? sorted.Reverse() : sorted).ToList();
        // „(bez projektu)” always at the end of the list (§6.6 „Stany”).
        Rows = list.Where(row => !row.IsNoProject).Concat(list.Where(row => row.IsNoProject)).ToList();
        OnPropertyChanged(nameof(TableState));
        OnPropertyChanged(nameof(IsFiltered));
    }

    /// <summary>Project drawer (§6.6): KPI, cost in time, sources donut, models, recent sessions, path and alias.</summary>
    public void OpenDetails(ProjectRowViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        SelectedRow = project;
        var index = Rows.ToList().IndexOf(project);
        var content = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        var tiles = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        tiles.Add(new StatTile { Title = "Koszt", Value = project.Cost, Marker = project.Marker });
        tiles.Add(new StatTile { Title = "Tokeny", Value = project.Tokens }, 1);
        tiles.Add(new StatTile { Title = "Sesje", Value = project.Sessions }, 2);
        content.Children.Add(tiles);
        content.Children.Add(new Section
        {
            Title = "Koszt w zakresie",
            Body = new LineChart
            {
                HeightRequest = Ui.D("SizeChartAux"),
                Data = new ChartData
                {
                    Categories = _days.Select(day => Fmt.DayTick(day.ToDateTime(TimeOnly.MinValue))).ToList(),
                    CategoryDetails = _days.Select(day => Fmt.DayDate(day.ToDateTime(TimeOnly.MinValue))).ToList(),
                    Series = [new ChartSeries { Id = "cost", Label = "Koszt", ColorKey = "ColorDataCost", Points = project.Trend }],
                    Description = $"Koszt dzienny projektu {project.Name}, {RangeText}."
                }
            }
        });
        if (project.Sources.Count > 0)
        {
            content.Children.Add(new Section
            {
                Title = "Źródła",
                Body = new DonutChart
                {
                    CenterLabel = "Koszt",
                    Data = new ChartData
                    {
                        Categories = ["Udział"],
                        Series = project.Sources.Take(6).Select(source => new ChartSeries
                        {
                            Id = source.Source,
                            Label = AppState.Engine.FindSource(source.Source)?.DisplayName ?? source.Source,
                            ColorKey = DesignTokens.SourceKey(source.Source),
                            Points = [source.Totals.CostUsd]
                        }).ToList()
                    }
                }
            });
        }

        content.Children.Add(new Section
        {
            Title = "Modele",
            Body = new KeyValueList
            {
                ItemsSource = project.Models.Select(m => new KeyValueItem(Fmt.Model(m.Model) + Fmt.Marker(m), $"{Fmt.Cost(m.Totals.CostUsd)} · {Fmt.Tokens(m.Totals.TotalTokens)}", IsNumeric: true)).ToList()
            }
        });
        if (project.RecentSessions.Count > 0)
        {
            var all = new Button { Text = "Wszystkie sesje projektu →" }.Styled("Button.Link");
            var (since, until) = DateRanges.Resolve(Preset, Since, Until);
            all.Clicked += (_, _) => _ = ShellViewModel.Instance.NavigateAsync(Screen.Sessions,
                new RangeFilter(since ?? _days.FirstOrDefault(), until, SourceId, project.IsNoProject ? null : project.Name));
            content.Children.Add(new Section
            {
                Title = "Ostatnie sesje",
                Actions = all,
                Body = new KeyValueList
                {
                    ItemsSource = project.RecentSessions.Select(session => new KeyValueItem(
                        $"{AppState.Engine.FindSource(session.Source)?.DisplayName ?? session.Source} · {Fmt.DateTimeShort(session.FirstActivity)}",
                        Fmt.Cost(session.Totals.CostUsd), IsNumeric: true)).ToList()
                }
            });
        }

        var details = new List<KeyValueItem>();
        if (project.Path.Length > 0)
        {
            details.Add(new KeyValueItem("Ścieżka", project.Path, IsMono: true, CanCopy: true));
        }

        details.Add(new KeyValueItem("Ostatnia aktywność", project.LastActivityText));
        content.Children.Add(new Section { Title = "Szczegóły", Body = new KeyValueList { ItemsSource = details } });

        var footer = new HorizontalStackLayout { Spacing = Ui.D("Space200") };
        if (project.Path.Length > 0 && Directory.Exists(project.Path))
        {
            var open = new Button { Text = "Otwórz folder" }.Styled("Button.Secondary");
            open.Clicked += async (_, _) => await Launcher.Default.OpenAsync(new Uri(project.Path));
            footer.Children.Add(open);
        }

        var alias = new Button { Text = "Zmień alias" }.Styled("Button.Ghost");
        alias.Clicked += (_, _) => _ = ShellViewModel.Instance.NavigateAsync(Screen.Settings, "Projekty");
        footer.Children.Add(alias);

        DrawerService.Open(new DrawerRequest
        {
            Overline = "Projekt",
            Title = project.Name,
            Subtitle = project.Path.Length > 0 ? project.Path : RangeText,
            Content = content,
            Footer = footer,
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(Rows[index - 1])) : null,
            NextCommand = index >= 0 && index < Rows.Count - 1 ? new Command(() => OpenDetails(Rows[index + 1])) : null,
            MoreEntries = project.Path.Length > 0 ? [new MenuEntry("Kopiuj ścieżkę", () => _ = Ui.CopyAsync(project.Path), Icons.Copy)] : [],
            Closed = () => SelectedRow = null
        });
    }

    private IEnumerable<IEnumerable<string>> TableRows()
    {
        yield return ["Projekt", "Ścieżka", "Sesje", "Tokeny", "Koszt"];
        foreach (var row in Rows)
        {
            yield return [row.Name, row.Path, row.Sessions, Fmt.Number(row.Totals.TotalTokens), Fmt.CostFull(row.Totals.CostUsd)];
        }
    }

    private static TokenCounts Sum(IEnumerable<TokenCounts> counts)
    {
        var total = new TokenCounts();
        foreach (var count in counts)
        {
            total.Add(count);
        }

        return total;
    }
}
