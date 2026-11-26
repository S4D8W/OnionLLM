using System.Collections.ObjectModel;
using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Export;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;
using OnionLLM.Design;
using OnionLLM.Navigation;

namespace OnionLLM.ViewModels;

public sealed record ChoiceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Table row of „Raporty” (period, or a source/model sub-row of its breakdown).</summary>
public sealed class ReportRowViewModel : IExpandableRow
{
    public ReportRow? Row { get; init; }

    public required string Period { get; init; }

    public string Project { get; init; } = string.Empty;

    public required TokenCounts Totals { get; init; }

    public string Marker { get; init; } = string.Empty;

    public IReadOnlyList<string> Models { get; init; } = [];

    public string ModelsText => Models.Count == 0 ? "" : string.Join(", ", Models.Take(3).Select(Fmt.Model)) + (Models.Count > 3 ? $" +{Models.Count - 3}" : "");

    public string Input => Fmt.Tokens(Totals.InputTokens);

    public string Output => Fmt.Tokens(Totals.OutputTokens);

    public string CacheWrite => Fmt.Tokens(Totals.CacheCreationTokens);

    public string CacheRead => Fmt.Tokens(Totals.CacheReadTokens);

    public string Tokens => Fmt.Tokens(Totals.TotalTokens);

    public string Cost => Fmt.Cost(Totals.CostUsd);

    public double TokensShare { get; set; }

    public double CostShare { get; set; }

    public bool IsExpanded { get; set; }

    public IReadOnlyList<object> Children { get; init; } = [];
}

/// <summary>Screen „Raporty” (DESIGN.md §6.3).</summary>
public sealed class ReportsViewModel : ObservableObject
{
    private const string StateKey = "reports.";
    private static readonly RangePreset[] PresetOrder = Enum.GetValues<RangePreset>();

    private int _periodIndex;
    private int _rangeIndex = (int)RangePreset.Last30Days;
    private int _sourceIndex;
    private bool _breakdown;
    private bool _bySource;
    private bool _groupByProject;
    private DateTime _since = DateTime.Today.AddDays(-29);
    private DateTime _until = DateTime.Today;
    private int _metricIndex;
    private int _shareIndex;
    private string _sortKey = string.Empty;
    private bool _sortDescending = true;
    private bool _isBusy;
    private bool _loaded;
    private string? _error;
    private UsageReport? _report;
    private ReportRowViewModel? _selected;
    private IReadOnlyList<ReportRowViewModel> _rows = [];
    private ChartData _chart = ChartData.Empty;
    private ChartData _share = ChartData.Empty;
    private IReadOnlyList<(DateTime, double)> _activity = [];

    public ReportsViewModel()
    {
        SourceChoices = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];
        _periodIndex = ViewState.GetInt(StateKey + "period", 0, Periods.Length - 1);
        _rangeIndex = ViewState.GetInt(StateKey + "preset", (int)RangePreset.Last30Days, PresetOrder.Length - 1);
        var sourceId = ViewState.GetString(StateKey + "source", "");
        _sourceIndex = Math.Max(0, Array.FindIndex(SourceChoices, choice => choice.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)));
        _breakdown = ViewState.GetBool(StateKey + "breakdown", false);
        _bySource = ViewState.GetBool(StateKey + "bySource", false);
        _groupByProject = ViewState.GetBool(StateKey + "groupByProject", false);
        _since = ViewState.GetDate(StateKey + "since", _since);
        _until = ViewState.GetDate(StateKey + "until", _until);
        _metricIndex = ViewState.GetInt(StateKey + "metric", 0, Metrics.Length - 1);
        _sortKey = ViewState.GetString(StateKey + "sort", string.Empty);
        _sortDescending = ViewState.GetBool(StateKey + "sortDesc", true);

        ExportCsvCommand = new Command(async () => await ExportAsync("csv"));
        ExportJsonCommand = new Command(async () => await ExportAsync("json"));
        CopyTableCommand = new Command(async () => await ExportService.CopyTableAsync(TableRows()));
        OpenDetailsCommand = new Command<ReportRowViewModel>(OpenDetails);
        ResetFiltersCommand = new Command(ResetFilters);
        ShowLast30Command = new Command(() => RangeIndex = (int)RangePreset.Last30Days);
        ShowAllCommand = new Command(() => RangeIndex = (int)RangePreset.AllTime);
        SourceChoiceNames = SourceChoices.Select(choice => choice.Name).ToArray();
    }

    public string[] Periods { get; } = ["Dzień", "Tydzień", "Miesiąc"];

    public string[] Presets { get; } = PresetOrder.Select(DateRanges.Label).ToArray();

    public int CustomPresetIndex => (int)RangePreset.Custom;

    public ChoiceItem[] SourceChoices { get; }

    public string[] SourceChoiceNames { get; }

    public string[] Metrics { get; } = ["Koszt", "Tokeny", "Cache %"];

    public string[] ShareModes { get; } = ["Źródła", "Modele"];

    public Command ExportCsvCommand { get; }

    public Command ExportJsonCommand { get; }

    public Command CopyTableCommand { get; }

    public Command<ReportRowViewModel> OpenDetailsCommand { get; }

    public Command ResetFiltersCommand { get; }

    public Command ShowLast30Command { get; }

    public Command ShowAllCommand { get; }

    // ===== Filters (§6.3 table) =====

    public int PeriodIndex { get => _periodIndex; set => SetFilter(ref _periodIndex, value, "period"); }

    public int RangeIndex { get => _rangeIndex; set => SetFilter(ref _rangeIndex, value, "preset"); }

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (Set(ref _sourceIndex, value))
            {
                ViewState.Set(StateKey + "source", SourceId ?? "");
                OnPropertyChanged(nameof(CanSplitBySource));
                Changed();
            }
        }
    }

    public bool CanSplitBySource => SourceId is null;

    public bool Breakdown { get => _breakdown; set => SetFilter(ref _breakdown, value, "breakdown"); }

    public bool BySource { get => _bySource; set => SetFilter(ref _bySource, value, "bySource"); }

    public bool GroupByProject { get => _groupByProject; set => SetFilter(ref _groupByProject, value, "groupByProject"); }

    public DateTime Since
    {
        get => _since;
        set
        {
            if (Set(ref _since, value))
            {
                ViewState.Set(StateKey + "since", value);
                Changed();
            }
        }
    }

    public DateTime Until
    {
        get => _until;
        set
        {
            if (Set(ref _until, value))
            {
                ViewState.Set(StateKey + "until", value);
                Changed();
            }
        }
    }

    public bool IsFiltered => PeriodIndex != 0 || RangeIndex != (int)RangePreset.Last30Days || SourceIndex != 0 || Breakdown || BySource || GroupByProject;

    public int MetricIndex
    {
        get => _metricIndex;
        set
        {
            if (Set(ref _metricIndex, value))
            {
                ViewState.Set(StateKey + "metric", value);
                BuildCharts();
            }
        }
    }

    public int ShareIndex
    {
        get => _shareIndex;
        set
        {
            if (Set(ref _shareIndex, value))
            {
                BuildCharts();
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
                ViewState.Set(StateKey + "sort", value);
                ApplySort();
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
                ApplySort();
            }
        }
    }

    // ===== Results =====

    public string RangeText { get; private set; } = string.Empty;

    public IReadOnlyList<ReportRowViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }

    public ReportRowViewModel? TotalRow { get; private set; }

    public ReportRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    public TableState TableState => _error is not null ? TableState.Error : !_loaded ? TableState.Loading : Rows.Count == 0 ? TableState.Empty : TableState.Ready;

    public string? Error => _error;

    public string TotalCost => _report is null ? "" : Fmt.Cost(_report.Totals.CostUsd);

    public string TotalMarker => _report is null ? "" : Fmt.Marker(_report.UnpricedModels, _report.EstimatedModels);

    public string TotalTokens => _report is null ? "" : Fmt.Tokens(_report.Totals.TotalTokens);

    public string TotalTokensCaption => _report is null ? "" : $"we {Fmt.Tokens(_report.Totals.InputTokens)} · wy {Fmt.Tokens(_report.Totals.OutputTokens)}";

    public string AverageTitle => "Średnio / " + Periods[PeriodIndex].ToLowerInvariant();

    public string AverageCost { get; private set; } = string.Empty;

    public string CacheShare => _report is { Totals.TotalTokens: > 0 } report
        ? Fmt.Percent((report.Totals.CacheCreationTokens + report.Totals.CacheReadTokens) * 100.0 / report.Totals.TotalTokens)
        : "";

    public string StoredCaption => _report is { Totals.HasStoredCost: true } report
        ? $"Zapisany {Fmt.Cost(report.Totals.StoredCostUsd)} · wyliczony {Fmt.Cost(report.Totals.CalculatedCostUsd)}"
        : "";

    public ChartData Chart { get => _chart; private set => Set(ref _chart, value); }

    public ChartData Share { get => _share; private set => Set(ref _share, value); }

    public string ShareCenterLabel => "Koszt · " + RangeText;

    public IReadOnlyList<(DateTime Date, double Value)> Activity { get => _activity; private set => Set(ref _activity, value); }

    public string PricingNote => _report is null ? "" : Fmt.PricingNote(_report.UnpricedModels, _report.EstimatedModels);

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string? SourceId => _sourceIndex > 0 && _sourceIndex < SourceChoices.Length ? SourceChoices[_sourceIndex].Id : null;

    private ReportPeriod Period => (ReportPeriod)PeriodIndex;

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
                Order = SortOrder.Desc,
                Breakdown = true,
                BySource = SourceId is null,
                GroupByProject = GroupByProject
            };
            var period = Period;
            _report = await Task.Run(() => AppState.Engine.BuildReport(period, query));
            var today = DateOnly.FromDateTime(Fmt.NowLocal);
            var activityQuery = query with { Since = today.AddDays(-364), Until = today, GroupByProject = false, Breakdown = false, BySource = false };
            var daily = await Task.Run(() => AppState.Engine.BuildReport(ReportPeriod.Daily, activityQuery));
            Activity = daily.Rows.OrderBy(row => row.PeriodStart)
                .Select(row => (row.PeriodStart.ToDateTime(TimeOnly.MinValue), row.Totals.CostUsd)).ToList();

            var firstDay = since ?? _report.Rows.Select(row => (DateOnly?)row.PeriodStart).Min() ?? until;
            RangeText = DateRanges.Describe(Preset, since ?? firstDay, until);
            var days = until.DayNumber - firstDay.DayNumber + 1;
            var periods = period switch
            {
                ReportPeriod.Weekly => Math.Max(1, (int)Math.Ceiling(days / 7.0)),
                ReportPeriod.Monthly => Math.Max(1, (until.Year - firstDay.Year) * 12 + until.Month - firstDay.Month + 1),
                _ => Math.Max(1, days)
            };
            AverageCost = Fmt.Cost(_report.Totals.CostUsd / periods);
            _error = null;
            _loaded = true;
            BuildRows();
            BuildCharts();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error = exception.Message;
        }
        finally
        {
            IsBusy = false;
            foreach (var name in new[]
                     {
                         nameof(RangeText), nameof(TotalRow), nameof(TableState), nameof(Error), nameof(TotalCost), nameof(TotalMarker),
                         nameof(TotalTokens), nameof(TotalTokensCaption), nameof(AverageTitle), nameof(AverageCost), nameof(CacheShare),
                         nameof(StoredCaption), nameof(ShareCenterLabel), nameof(PricingNote), nameof(IsFiltered)
                     })
            {
                OnPropertyChanged(name);
            }
        }
    }

    private void BuildRows()
    {
        if (_report is null)
        {
            return;
        }

        var rows = _report.Rows.Select(row =>
        {
            var children = new List<object>();
            if (BySource && SourceId is null)
            {
                children.AddRange(row.SourceBreakdowns.Select(source => new ReportRowViewModel
                {
                    Period = SourceName(source.Source),
                    Totals = source.Totals,
                    Marker = Fmt.Marker(source.ModelBreakdowns.Where(m => m.IsUnpriced).Select(m => m.Model).ToArray(),
                        source.ModelBreakdowns.Where(m => m.IsEstimated).Select(m => m.Model).ToArray()),
                    Models = source.Models
                }));
            }

            if (Breakdown)
            {
                children.AddRange(row.ModelBreakdowns.Select(model => new ReportRowViewModel
                {
                    Period = Fmt.Model(model.Model),
                    Totals = model.Totals,
                    Marker = Fmt.Marker(model)
                }));
            }

            return new ReportRowViewModel
            {
                Row = row,
                Period = PeriodLabel(row.PeriodStart) + (InProgress(row.PeriodStart) ? " · w toku" : ""),
                Project = row.Project ?? string.Empty,
                Totals = row.Totals,
                Marker = Fmt.Marker(row.UnpricedModels, row.EstimatedModels),
                Models = row.Models,
                Children = children
            };
        }).ToList();
        var maxCost = rows.Select(row => row.Totals.CostUsd).DefaultIfEmpty(0).Max();
        var maxTokens = rows.Select(row => (double)row.Totals.TotalTokens).DefaultIfEmpty(0).Max();
        foreach (var row in rows)
        {
            row.CostShare = maxCost > 0 ? row.Totals.CostUsd / maxCost : 0;
            row.TokensShare = maxTokens > 0 ? row.Totals.TotalTokens / maxTokens : 0;
        }

        _allRows = rows;
        TotalRow = new ReportRowViewModel
        {
            Period = "Razem",
            Totals = _report.Totals,
            Marker = Fmt.Marker(_report.UnpricedModels, _report.EstimatedModels)
        };
        ApplySort();
    }

    private List<ReportRowViewModel> _allRows = [];

    private void ApplySort()
    {
        IEnumerable<ReportRowViewModel> sorted = SortKey switch
        {
            nameof(ReportRowViewModel.Cost) => _allRows.OrderBy(row => row.Totals.CostUsd),
            nameof(ReportRowViewModel.Tokens) => _allRows.OrderBy(row => row.Totals.TotalTokens),
            nameof(ReportRowViewModel.Period) => _allRows.OrderBy(row => row.Row?.PeriodStart).ThenBy(row => row.Project),
            _ => _allRows
        };
        Rows = (SortKey.Length > 0 && SortDescending ? sorted.Reverse() : sorted).ToList();
        OnPropertyChanged(nameof(TableState));
    }

    /// <summary>„W czasie” (§6.3 pkt 4): series = sources for all sources, otherwise models; metric Koszt/Tokeny/Cache %.</summary>
    private void BuildCharts()
    {
        if (_report is null)
        {
            return;
        }

        var groups = _report.Rows.GroupBy(row => row.PeriodStart).OrderBy(group => group.Key).ToList();
        var categories = groups.Select(group => Tick(group.Key)).ToList();
        var details = groups.Select(group => PeriodLabel(group.Key)).ToList();
        var lastInProgress = groups.Count > 0 && InProgress(groups[^1].Key);
        var unit = MetricIndex switch { 1 => ChartUnit.Tokens, 2 => ChartUnit.Percent, _ => ChartUnit.Cost };

        List<ChartSeries> series;
        if (MetricIndex == 2)
        {
            series =
            [
                new ChartSeries
                {
                    Id = "cache",
                    Label = "Udział cache",
                    ColorKey = "ColorDataCacheRead",
                    Points = groups.Select(group =>
                    {
                        var total = group.Sum(row => row.Totals.TotalTokens);
                        return total > 0 ? group.Sum(row => row.Totals.CacheCreationTokens + row.Totals.CacheReadTokens) * 100.0 / total : 0;
                    }).ToList()
                }
            ];
        }
        else
        {
            double Value(TokenCounts totals) => MetricIndex == 1 ? totals.TotalTokens : totals.CostUsd;
            var bySource = SourceId is null;
            var keys = bySource
                ? _report.Rows.SelectMany(row => row.SourceBreakdowns).GroupBy(s => s.Source).Select(g => (Key: g.Key, Total: g.Sum(s => Value(s.Totals))))
                : _report.Rows.SelectMany(row => row.ModelBreakdowns).GroupBy(m => m.Model).Select(g => (Key: g.Key, Total: g.Sum(m => Value(m.Totals))));
            var top = keys.OrderByDescending(k => k.Total).Take(6).Select(k => k.Key).ToList();
            double Point(IGrouping<DateOnly, ReportRow> group, string key) => bySource
                ? group.SelectMany(row => row.SourceBreakdowns).Where(s => s.Source == key).Sum(s => Value(s.Totals))
                : group.SelectMany(row => row.ModelBreakdowns).Where(m => m.Model == key).Sum(m => Value(m.Totals));
            series = top.Select((key, index) => new ChartSeries
            {
                Id = key,
                Label = bySource ? SourceName(key) : Fmt.Model(key),
                ColorKey = bySource ? DesignTokens.SourceKey(key) : $"ColorChartCat{index % 8 + 1}",
                Points = groups.Select(group => Point(group, key)).ToList()
            }).ToList();
            var other = groups.Select(group => group.Sum(row => Value(row.Totals)) - top.Sum(key => Point(group, key))).ToList();
            if (other.Any(value => value > 0.0001))
            {
                series.Add(new ChartSeries { Id = "other", Label = "Pozostałe", ColorKey = "ColorChartOther", Points = other });
            }
        }

        Chart = new ChartData
        {
            Categories = categories,
            CategoryDetails = details,
            Series = series,
            Unit = unit,
            LastInProgress = lastInProgress,
            Description = $"Wykres {(series.Count > 1 ? "skumulowany " : "")}{Metrics[MetricIndex].ToLowerInvariant()} w okresach: {Periods[PeriodIndex].ToLowerInvariant()}, {RangeText}."
        };

        var shareBySource = ShareIndex == 0 && SourceId is null;
        var shares = (shareBySource
                ? _report.Rows.SelectMany(row => row.SourceBreakdowns).GroupBy(s => s.Source).Select(g => (Key: g.Key, Label: SourceName(g.Key), Cost: g.Sum(s => s.Totals.CostUsd)))
                : _report.Rows.SelectMany(row => row.ModelBreakdowns).GroupBy(m => m.Model).Select(g => (Key: g.Key, Label: Fmt.Model(g.Key), Cost: g.Sum(m => m.Totals.CostUsd))))
            .OrderByDescending(item => item.Cost).ToList();
        var shareSeries = shares.Take(6).Select((item, index) => new ChartSeries
        {
            Id = item.Key,
            Label = item.Label,
            ColorKey = shareBySource ? DesignTokens.SourceKey(item.Key) : $"ColorChartCat{index % 8 + 1}",
            Points = [item.Cost]
        }).ToList();
        if (shares.Count > 6)
        {
            shareSeries.Add(new ChartSeries { Id = "other", Label = "Pozostałe", ColorKey = "ColorChartOther", Points = [shares.Skip(6).Sum(item => item.Cost)] });
        }

        Share = new ChartData { Categories = ["Udział"], Series = shareSeries, Description = $"Udział {(shareBySource ? "źródeł" : "modeli")} w koszcie, {RangeText}." };
        OnPropertyChanged(nameof(ShareCenterLabel));
    }

    /// <summary>Click on a chart category opens the drawer of that period (§6.3 pkt 4).</summary>
    public void OpenCategory(int index)
    {
        var periods = _allRows.Where(row => row.Row is not null).Select(row => row.Row!.PeriodStart).Distinct().OrderBy(date => date).ToList();
        if (index >= 0 && index < periods.Count)
        {
            OpenDetails(Rows.FirstOrDefault(row => row.Row?.PeriodStart == periods[index]));
        }
    }

    /// <summary>Period drawer (§6.3 pkt 6): KPI, TokenBreakdown, models, sources, stored vs calculated cost, drill-down.</summary>
    public void OpenDetails(ReportRowViewModel? item)
    {
        if (item?.Row is not { } row)
        {
            return;
        }

        SelectedRow = item;
        var index = Rows.ToList().IndexOf(item);
        var content = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        var tiles = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        tiles.Add(new StatTile { Title = "Koszt", Value = item.Cost, Marker = item.Marker, Caption = row.Totals.HasStoredCost ? "zapisany " + Fmt.Cost(row.Totals.StoredCostUsd) : "" });
        tiles.Add(new StatTile { Title = "Tokeny", Value = item.Tokens, Caption = Fmt.Count(row.Totals.Requests, "wywołanie", "wywołania", "wywołań") }, 1);
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
        if (row.SourceBreakdowns.Count > 0)
        {
            content.Children.Add(new Section
            {
                Title = "Źródła",
                Body = new KeyValueList
                {
                    ItemsSource = row.SourceBreakdowns.Select(s => new KeyValueItem(SourceName(s.Source), $"{Fmt.Cost(s.Totals.CostUsd)} · {Fmt.Tokens(s.Totals.TotalTokens)}", IsNumeric: true)).ToList()
                }
            });
        }

        var detailsList = new List<KeyValueItem>
        {
            new("Koszt wyliczony z cennika", Fmt.CostFull(row.Totals.CalculatedCostUsd), IsNumeric: true),
            new("Tryb kosztu", AppState.Config.CostMode.ToString().ToLowerInvariant())
        };
        if (row.Totals.HasStoredCost)
        {
            detailsList.Insert(0, new KeyValueItem("Koszt zapisany przez narzędzie", Fmt.CostFull(row.Totals.StoredCostUsd), IsNumeric: true));
        }

        if (row.Totals.Credits != 0)
        {
            detailsList.Add(new KeyValueItem("Kredyty", Fmt.CreditsValue(row.Totals.Credits), IsNumeric: true));
        }

        content.Children.Add(new Section { Title = "Szczegóły", Body = new KeyValueList { ItemsSource = detailsList } });
        var note = Fmt.PricingNote(row.UnpricedModels, row.EstimatedModels);
        if (note.Length > 0)
        {
            content.Children.Add(new PricingNote { Text = note });
        }

        var (start, end) = PeriodBounds(row.PeriodStart);
        var sessions = new Button { Text = "Pokaż sesje z tego okresu" }.Styled("Button.Secondary");
        sessions.Clicked += (_, _) => _ = ShellViewModel.Instance.NavigateAsync(Screen.Sessions, new RangeFilter(start, end, SourceId, row.Project));
        DrawerService.Open(new DrawerRequest
        {
            Overline = "Okres",
            Title = item.Period + (item.Project.Length > 0 ? " · " + item.Project : ""),
            Subtitle = Fmt.Range(start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue)),
            Content = content,
            Footer = sessions,
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(Rows[index - 1])) : null,
            NextCommand = index >= 0 && index < Rows.Count - 1 ? new Command(() => OpenDetails(Rows[index + 1])) : null,
            MoreEntries =
            [
                new MenuEntry("Kopiuj jako JSON", () => _ = Ui.CopyAsync(System.Text.Json.JsonSerializer.Serialize(row, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })), Resources.Icons.Copy)
            ],
            Closed = () => SelectedRow = null
        });
    }

    private void ResetFilters()
    {
        _periodIndex = 0;
        _rangeIndex = (int)RangePreset.Last30Days;
        _sourceIndex = 0;
        _breakdown = _bySource = _groupByProject = false;
        foreach (var key in new[] { "period", "preset", "source", "breakdown", "bySource", "groupByProject" })
        {
            ViewState.Set(StateKey + key, key is "period" ? 0 : key is "preset" ? (int)RangePreset.Last30Days : 0);
        }

        ViewState.Set(StateKey + "source", "");
        foreach (var name in new[] { nameof(PeriodIndex), nameof(RangeIndex), nameof(SourceIndex), nameof(Breakdown), nameof(BySource), nameof(GroupByProject), nameof(CanSplitBySource) })
        {
            OnPropertyChanged(name);
        }

        Changed();
    }

    private void SetFilter<T>(ref T field, T value, string key)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        switch (value)
        {
            case int number:
                ViewState.Set(StateKey + key, number);
                break;
            case bool flag:
                ViewState.Set(StateKey + key, flag);
                break;
        }

        OnPropertyChanged(key switch
        {
            "period" => nameof(PeriodIndex),
            "preset" => nameof(RangeIndex),
            "breakdown" => nameof(Breakdown),
            "bySource" => nameof(BySource),
            _ => nameof(GroupByProject)
        });
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsFiltered));
        _ = RefreshAsync();
    }

    private async Task ExportAsync(string format)
    {
        if (_report is null)
        {
            return;
        }

        var content = format == "json"
            ? ReportExporter.ToJson(_report, new JsonExportOptions { Agent = SourceId, ByAgent = BySource && SourceId is null })
            : ReportExporter.ToCsv(_report);
        await ExportService.SaveAsync("raporty", Period.ToString().ToLowerInvariant(), format, content, _report.Rows.Count);
    }

    private IEnumerable<IEnumerable<string>> TableRows()
    {
        yield return ["Okres", "Projekt", "Wejście", "Wyjście", "Zapis cache", "Odczyt cache", "Tokeny", "Koszt"];
        foreach (var row in Rows)
        {
            yield return [row.Period, row.Project, Fmt.Number(row.Totals.InputTokens), Fmt.Number(row.Totals.OutputTokens),
                Fmt.Number(row.Totals.CacheCreationTokens), Fmt.Number(row.Totals.CacheReadTokens), Fmt.Number(row.Totals.TotalTokens), Fmt.CostFull(row.Totals.CostUsd)];
        }
    }

    private string PeriodLabel(DateOnly start) => Period switch
    {
        ReportPeriod.Monthly => Fmt.Month(start.ToDateTime(TimeOnly.MinValue)),
        ReportPeriod.Weekly => Fmt.Week(start.ToDateTime(TimeOnly.MinValue), start.AddDays(6).ToDateTime(TimeOnly.MinValue)),
        _ => Fmt.DayDate(start.ToDateTime(TimeOnly.MinValue))
    };

    private string Tick(DateOnly start) => Period switch
    {
        ReportPeriod.Monthly => Fmt.MonthShortName(start.Month),
        ReportPeriod.Weekly => "Tydz. " + System.Globalization.ISOWeek.GetWeekOfYear(start.ToDateTime(TimeOnly.MinValue)),
        _ => Fmt.DayTick(start.ToDateTime(TimeOnly.MinValue))
    };

    private (DateOnly Start, DateOnly End) PeriodBounds(DateOnly start) => Period switch
    {
        ReportPeriod.Monthly => (start, start.AddMonths(1).AddDays(-1)),
        ReportPeriod.Weekly => (start, start.AddDays(6)),
        _ => (start, start)
    };

    private bool InProgress(DateOnly start) => PeriodBounds(start).End >= DateOnly.FromDateTime(Fmt.NowLocal);

    private static string SourceName(string id) => AppState.Engine.FindSource(id)?.DisplayName ?? id;
}
