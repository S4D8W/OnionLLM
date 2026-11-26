using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Export;
using OnionLLM.Core.Reports;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

/// <summary>Row of the sessions table (DESIGN.md §6.4).</summary>
public sealed class SessionItemViewModel(SessionRow row)
{
    public SessionRow Row { get; } = row;

    public string Title => string.IsNullOrWhiteSpace(Row.Project) ? Fmt.SessionId(Row.SessionId, 40) : Row.Project!;

    public string SessionShort => Fmt.SessionId(Row.SessionId);

    /// <summary>Last activity within <c>activeSessionMinutes</c> — StatusBadge „Aktywna” (§6.4).</summary>
    public bool IsActive => Row.IsActive;

    public string SourceId => Row.Source;

    public string Source => AppState.Engine.FindSource(Row.Source)?.DisplayName ?? Row.Source;

    public string Initial => Source[..1].ToUpperInvariant();

    public string Start => Fmt.DateTimeShort(Row.FirstActivity);

    public string End => Fmt.DateTimeShort(Row.LastActivity);

    public string Duration => Fmt.Duration(Row.Duration);

    public string ModelsText => string.Join(", ", Row.Models.Take(3).Select(Fmt.Model)) + (Row.Models.Count > 3 ? $" +{Row.Models.Count - 3}" : "");

    public string Requests => Fmt.Number(Row.Totals.Requests);

    public string Tokens => Fmt.Tokens(Row.Totals.TotalTokens);

    public string Cost => Fmt.Cost(Row.Totals.CostUsd);

    public string Marker => Fmt.Marker(Row.UnpricedModels, Row.EstimatedModels);

    public string SessionId => Row.SessionId;

    public double CostShare { get; set; }
}

/// <summary>Screen „Sesje” (DESIGN.md §6.4).</summary>
public sealed class SessionsViewModel : ObservableObject
{
    private const string StateKey = "sessions.";
    private const int PageSize = 300;
    private static readonly RangePreset[] PresetOrder = [RangePreset.Today, RangePreset.Last7Days, RangePreset.Last30Days, RangePreset.AllTime, RangePreset.Custom];

    private bool _onlyActive;
    private int _rangeIndex = 1;
    private int _sourceIndex;
    private string _search = string.Empty;
    private string _sortKey = nameof(SessionItemViewModel.Start);
    private bool _sortDescending = true;
    private DateTime _since = DateTime.Today.AddDays(-6);
    private DateTime _until = DateTime.Today;
    private string? _project;
    private int _shown = PageSize;
    private bool _isBusy;
    private bool _loaded;
    private string? _error;
    private SessionReport? _report;
    private List<SessionRow> _filtered = [];
    private IReadOnlyList<SessionItemViewModel> _sessions = [];
    private SessionItemViewModel? _selected;

    public SessionsViewModel()
    {
        SourceChoices = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];
        SourceChoiceNames = SourceChoices.Select(choice => choice.Name).ToArray();
        _rangeIndex = ViewState.GetInt(StateKey + "preset", 1, PresetOrder.Length - 1);
        var sourceId = ViewState.GetString(StateKey + "source", "");
        _sourceIndex = Math.Max(0, Array.FindIndex(SourceChoices, choice => choice.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)));
        _sortKey = ViewState.GetString(StateKey + "sortKey", nameof(SessionItemViewModel.Start));
        _sortDescending = ViewState.GetBool(StateKey + "sortDesc", true);

        ExportCsvCommand = new Command(async () => await ExportAsync("csv"));
        ExportJsonCommand = new Command(async () => await ExportAsync("json"));
        CopyTableCommand = new Command(async () => await ExportService.CopyTableAsync(TableRows()));
        OpenDetailsCommand = new Command<SessionItemViewModel>(OpenDetails);
        ShowMoreCommand = new Command(() => { _shown += PageSize; Apply(); });
        ResetFiltersCommand = new Command(ResetFilters);
        ShowAllCommand = new Command(() => RangeIndex = Array.IndexOf(PresetOrder, RangePreset.AllTime));
        ClearProjectCommand = new Command(() => { _project = null; Changed(); });
    }

    public string[] Presets { get; } = PresetOrder.Select(DateRanges.Label).ToArray();

    public int CustomPresetIndex => PresetOrder.Length - 1;

    public ChoiceItem[] SourceChoices { get; }

    public string[] SourceChoiceNames { get; }

    public Command ExportCsvCommand { get; }

    public Command ExportJsonCommand { get; }

    public Command CopyTableCommand { get; }

    public Command<SessionItemViewModel> OpenDetailsCommand { get; }

    public Command ShowMoreCommand { get; }

    public Command ResetFiltersCommand { get; }

    public Command ShowAllCommand { get; }

    public Command ClearProjectCommand { get; }

    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
            {
                ViewState.Set(StateKey + "preset", value);
                Changed();
            }
        }
    }

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (Set(ref _sourceIndex, value))
            {
                ViewState.Set(StateKey + "source", SourceId ?? "");
                Changed();
            }
        }
    }

    public DateTime Since { get => _since; set { if (Set(ref _since, value)) { Changed(); } } }

    public DateTime Until { get => _until; set { if (Set(ref _until, value)) { Changed(); } } }

    /// <summary>Debounced search: ID, project, model, source (§6.4). Not remembered between visits (§5.4).</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                _shown = PageSize;
                Apply();
            }
        }
    }

    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (Set(ref _sortKey, value))
            {
                ViewState.Set(StateKey + "sortKey", value);
                Apply();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (Set(ref _sortDescending, value))
            {
                ViewState.Set(StateKey + "sortDesc", value);
                Apply();
            }
        }
    }

    /// <summary>„Tylko aktywne” (§6.4): sessions whose last activity is within <c>activeSessionMinutes</c>.</summary>
    public bool OnlyActive
    {
        get => _onlyActive;
        set
        {
            if (Set(ref _onlyActive, value))
            {
                _shown = PageSize;
                OnPropertyChanged(nameof(IsFiltered));
                Apply();
            }
        }
    }

    public bool HasProjectFilter => _project is not null;

    public string ProjectFilterText => "Projekt: " + _project;

    public bool IsFiltered => RangeIndex != 1 || SourceIndex != 0 || Search.Length > 0 || _project is not null || OnlyActive;

    public IReadOnlyList<SessionItemViewModel> Sessions { get => _sessions; private set => Set(ref _sessions, value); }

    public SessionItemViewModel? SelectedSession { get => _selected; set => Set(ref _selected, value); }

    public string Total { get; private set; } = string.Empty;

    public string RangeText { get; private set; } = string.Empty;

    public string FooterText { get; private set; } = string.Empty;

    public bool CanShowMore => _filtered.Count > _shown;

    public string PricingNote { get; private set; } = string.Empty;

    public TableState TableState => _error is not null ? TableState.Error
        : !_loaded ? TableState.Loading
        : _filtered.Count > 0 ? TableState.Ready
        : Search.Length > 0 || _project is not null || OnlyActive ? TableState.NoResults
        : TableState.Empty;

    public string EmptyTitle => TableState == TableState.NoResults
        ? Search.Length > 0 ? $"Brak sesji pasujących do „{Search}”" : "Brak sesji dla wybranych filtrów"
        : "Brak sesji w tym zakresie";

    public string EmptyAction => TableState == TableState.NoResults ? "Wyczyść filtry" : "Pokaż całą historię";

    public System.Windows.Input.ICommand EmptyCommand => TableState == TableState.NoResults ? ResetFiltersCommand : ShowAllCommand;

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string? SourceId => _sourceIndex > 0 && _sourceIndex < SourceChoices.Length ? SourceChoices[_sourceIndex].Id : null;

    private RangePreset Preset => PresetOrder[Math.Clamp(RangeIndex, 0, PresetOrder.Length - 1)];

    /// <summary>Drill-down from Raporty / Projekty (§5.5): custom range, source and project.</summary>
    public void ApplyFilter(RangeFilter filter)
    {
        _since = filter.Since.ToDateTime(TimeOnly.MinValue);
        _until = filter.Until.ToDateTime(TimeOnly.MinValue);
        _rangeIndex = CustomPresetIndex;
        _project = filter.Project;
        if (filter.SourceId is { } source)
        {
            _sourceIndex = Math.Max(0, Array.FindIndex(SourceChoices, choice => choice.Id == source));
        }

        foreach (var name in new[] { nameof(Since), nameof(Until), nameof(RangeIndex), nameof(SourceIndex) })
        {
            OnPropertyChanged(name);
        }

        Changed();
    }

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
            RangeText = DateRanges.Describe(Preset, since, until);
            var query = new ReportQuery
            {
                Since = since,
                Until = until,
                Sources = SourceId is null ? Array.Empty<string>() : [SourceId],
                Order = SortOrder.Desc,
                Breakdown = true
            };
            _report = await Task.Run(() => AppState.Engine.BuildSessions(query));
            _error = null;
            _loaded = true;
            Apply();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error = exception.Message;
            Notify();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply()
    {
        if (_report is null)
        {
            return;
        }

        IEnumerable<SessionRow> rows = _report.Sessions;
        if (_project is { } project)
        {
            rows = rows.Where(row => string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase));
        }

        if (OnlyActive)
        {
            rows = rows.Where(row => row.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Select.Normalize(Search.Trim());
            rows = rows.Where(row =>
                row.SessionId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                Select.Normalize(row.Project ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                Select.Normalize(AppState.Engine.FindSource(row.Source)?.DisplayName ?? row.Source).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Models.Any(model => model.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        IEnumerable<SessionRow> sorted = SortKey switch
        {
            nameof(SessionItemViewModel.Cost) => rows.OrderBy(row => row.Totals.CostUsd),
            nameof(SessionItemViewModel.Tokens) => rows.OrderBy(row => row.Totals.TotalTokens),
            nameof(SessionItemViewModel.Duration) => rows.OrderBy(row => row.Duration),
            nameof(SessionItemViewModel.Start) => rows.OrderBy(row => row.FirstActivity),
            _ => rows.OrderBy(row => row.LastActivity)
        };
        _filtered = (SortKey.Length == 0 || SortDescending ? sorted.Reverse() : sorted).ToList();
        var max = _filtered.Select(row => row.Totals.CostUsd).DefaultIfEmpty(0).Max();
        Sessions = _filtered.Take(_shown).Select(row => new SessionItemViewModel(row) { CostShare = max > 0 ? row.Totals.CostUsd / max : 0 }).ToList();

        var unpriced = _filtered.SelectMany(row => row.UnpricedModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var estimated = _filtered.SelectMany(row => row.EstimatedModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Total = $"{Fmt.Count(_filtered.Count, "sesja", "sesje", "sesji")} · {Fmt.TokensWithUnit(_filtered.Sum(row => row.Totals.TotalTokens))} · " +
                $"{Fmt.Cost(_filtered.Sum(row => row.Totals.CostUsd))}{Fmt.Marker(unpriced, estimated)}";
        PricingNote = Fmt.PricingNote(unpriced, estimated);
        FooterText = _filtered.Count > Sessions.Count ? $"Pokazano {Fmt.Number(Sessions.Count)} z {Fmt.Number(_filtered.Count)}" : string.Empty;
        Notify();
    }

    private void Notify()
    {
        foreach (var name in new[]
                 {
                     nameof(Total), nameof(RangeText), nameof(FooterText), nameof(CanShowMore), nameof(PricingNote), nameof(TableState),
                     nameof(EmptyTitle), nameof(EmptyAction), nameof(EmptyCommand), nameof(HasProjectFilter), nameof(ProjectFilterText), nameof(IsFiltered)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// Opens the drawer of one session (command palette, §4.6.7). Falls back to the whole history when the session is
    /// outside the current range; the fallback is not remembered.
    /// </summary>
    public async Task OpenEntityAsync(string sessionId)
    {
        await ScreenLoad.RefreshIdleAsync(RefreshAsync, () => IsBusy);
        var row = _report?.Sessions.FirstOrDefault(session => session.SessionId == sessionId);
        if (row is null && Preset != RangePreset.AllTime)
        {
            _rangeIndex = Array.IndexOf(PresetOrder, RangePreset.AllTime);
            _project = null;
            OnPropertyChanged(nameof(RangeIndex));
            await ScreenLoad.RefreshIdleAsync(RefreshAsync, () => IsBusy);
            row = _report?.Sessions.FirstOrDefault(session => session.SessionId == sessionId);
        }

        if (row is not null)
        {
            OpenDetails(Sessions.FirstOrDefault(item => item.Row.SessionId == sessionId) ?? new SessionItemViewModel(row));
        }
    }

    /// <summary>Samples of the „Przebieg” chart (§6.4 drawer).</summary>
    private const int TimelineSamples = 36;

    /// <summary>
    /// „Przebieg” (§6.4): cumulative cost over the session (area chart, time on the X axis). Loaded in the background
    /// from the ledger when the drawer opens.
    /// </summary>
    private static View Timeline(SessionRow row)
    {
        var chart = new LineChart { IsArea = true, State = ChartState.Loading, AutomationId = "Sessions.Drawer.Timeline" };
        chart.Dyn(VisualElement.HeightRequestProperty, "SizeChartAux");
        _ = LoadAsync();
        return chart;

        async Task LoadAsync()
        {
            try
            {
                var points = await Task.Run(() => AppState.Engine.BuildSessionTimeline(row.Source, row.SessionId, row.FirstActivity, row.LastActivity));
                var end = row.LastActivity > row.FirstActivity ? row.LastActivity : row.FirstActivity.AddMinutes(1);
                var samples = TimeSeries.Resample(points, row.FirstActivity, end, TimelineSamples);
                var step = (end - row.FirstActivity) / (TimelineSamples - 1);
                var times = Enumerable.Range(0, TimelineSamples).Select(i => Fmt.ToLocal(row.FirstActivity + step * i)).ToList();
                chart.Data = new ChartData
                {
                    Categories = times.Select(time => Fmt.Time(time)).ToList(),
                    CategoryDetails = times.Select(time => Fmt.DateTimeShort(time, Fmt.NowLocal)).ToList(),
                    Series = [new ChartSeries { Id = "cost", Label = "Koszt narastająco", ColorKey = "ColorDataCost", Points = samples }],
                    Unit = ChartUnit.Cost,
                    Description = $"Przebieg sesji: koszt rośnie do {Fmt.CostSpoken(row.Totals.CostUsd)} w {Fmt.Duration(row.Duration)}."
                };
                chart.State = points.Count == 0 ? ChartState.Error : ChartState.Ready;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                chart.State = ChartState.Error;
            }
        }
    }

    /// <summary>Session drawer (§6.4): KPI, tokens, models, details with copyable ID and path.</summary>
    public void OpenDetails(SessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedSession = item;
        var row = item.Row;
        var index = Sessions.ToList().IndexOf(item);
        var content = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        var tiles = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        tiles.Add(new StatTile { Title = "Koszt", Value = item.Cost, Marker = item.Marker });
        tiles.Add(new StatTile { Title = "Tokeny", Value = item.Tokens }, 1);
        tiles.Add(new StatTile { Title = "Wywołania", Value = item.Requests }, 2);
        content.Children.Add(tiles);
        content.Children.Add(new Section
        {
            Title = "Tokeny",
            Body = new TokenBreakdown
            {
                Input = row.Totals.InputTokens,
                Output = row.Totals.OutputTokens,
                CacheWrite = row.Totals.CacheCreationTokens,
                CacheRead = row.Totals.CacheReadTokens,
                Reasoning = row.Totals.ReasoningTokens,
                CacheWrite1h = row.Totals.CacheCreation1hTokens
            }
        });
        content.Children.Add(new Section
        {
            Title = "Modele",
            Body = new KeyValueList
            {
                ItemsSource = row.ModelBreakdowns.OrderByDescending(m => m.Totals.CostUsd)
                    .Select(m => new KeyValueItem(Fmt.Model(m.Model) + Fmt.Marker(m), $"{Fmt.Cost(m.Totals.CostUsd)} · {Fmt.Tokens(m.Totals.TotalTokens)}", IsNumeric: true)).ToList()
            }
        });
        content.Children.Add(new Section { Title = "Przebieg", Body = Timeline(row) });
        var details = new List<KeyValueItem>
        {
            new("ID sesji", row.SessionId, IsMono: true, CanCopy: true),
            new("Źródło", item.Source),
            new("Start", Fmt.DateTimeShort(row.FirstActivity)),
            new("Koniec", Fmt.DateTimeShort(row.LastActivity)),
            new("Koszt wyliczony z cennika", Fmt.CostFull(row.Totals.CalculatedCostUsd), IsNumeric: true)
        };
        if (row.ProjectPath is { } path)
        {
            details.Insert(1, new KeyValueItem("Ścieżka projektu", path, IsMono: true, CanCopy: true));
        }

        if (row.Totals.HasStoredCost)
        {
            details.Add(new KeyValueItem("Koszt zapisany przez narzędzie", Fmt.CostFull(row.Totals.StoredCostUsd), IsNumeric: true));
        }

        content.Children.Add(new Section { Title = "Szczegóły", Body = new KeyValueList { ItemsSource = details } });

        var copy = new Button { Text = "Kopiuj ID" }.Styled("Button.Secondary");
        copy.Clicked += async (_, _) => await Ui.CopyAsync(row.SessionId);
        var footer = new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { copy } };
        if (row.ProjectPath is { } folder && Directory.Exists(folder))
        {
            var open = new Button { Text = "Otwórz folder" }.Styled("Button.Ghost");
            open.Clicked += async (_, _) => await Launcher.Default.OpenAsync(new Uri(folder));
            footer.Children.Add(open);
        }

        DrawerService.Open(new DrawerRequest
        {
            Overline = "Sesja",
            Title = item.Title,
            Subtitle = $"{item.Source} · {Fmt.DateTimeShort(row.FirstActivity)} → {Fmt.Time(row.LastActivity)} ({item.Duration})",
            Content = content,
            Footer = footer,
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(Sessions[index - 1])) : null,
            NextCommand = index >= 0 && index < Sessions.Count - 1 ? new Command(() => OpenDetails(Sessions[index + 1])) : null,
            MoreEntries =
            [
                new MenuEntry("Kopiuj ID", () => _ = Ui.CopyAsync(row.SessionId), Icons.Copy),
                new MenuEntry("Kopiuj jako JSON", () => _ = Ui.CopyAsync(System.Text.Json.JsonSerializer.Serialize(row, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })), Icons.Copy)
            ],
            Closed = () => SelectedSession = null
        });
    }

    private void ResetFilters()
    {
        _rangeIndex = 1;
        _sourceIndex = 0;
        _search = string.Empty;
        _project = null;
        _onlyActive = false;
        OnPropertyChanged(nameof(OnlyActive));
        ViewState.Set(StateKey + "preset", 1);
        ViewState.Set(StateKey + "source", "");
        foreach (var name in new[] { nameof(RangeIndex), nameof(SourceIndex), nameof(Search) })
        {
            OnPropertyChanged(name);
        }

        Changed();
    }

    private void Changed()
    {
        _shown = PageSize;
        OnPropertyChanged(nameof(IsFiltered));
        _ = RefreshAsync();
    }

    private async Task ExportAsync(string format)
    {
        if (_report is null)
        {
            return;
        }

        var visible = _report with { Sessions = _filtered };
        var content = format == "json" ? ReportExporter.ToJson(visible) : ReportExporter.ToCsv(visible);
        await ExportService.SaveAsync("sesje", Preset.ToString().ToLowerInvariant(), format, content, _filtered.Count);
    }

    private IEnumerable<IEnumerable<string>> TableRows()
    {
        yield return ["Sesja", "ID", "Źródło", "Start", "Czas trwania", "Modele", "Wywołania", "Tokeny", "Koszt"];
        foreach (var row in _filtered.Select(row => new SessionItemViewModel(row)))
        {
            yield return [row.Title, row.SessionId, row.Source, row.Start, row.Duration, row.ModelsText, row.Requests, Fmt.Number(row.Row.Totals.TotalTokens), Fmt.CostFull(row.Row.Totals.CostUsd)];
        }
    }
}
