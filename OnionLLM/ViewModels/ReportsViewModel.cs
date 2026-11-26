using System.Collections.ObjectModel;
using System.Globalization;
using OnionLLM.Core.Export;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

public sealed record ChoiceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class ReportRowViewModel
{
    public ReportRow? Row { get; init; }
    public required string Period { get; init; }
    public required string Subtitle { get; init; }
    public required string Tokens { get; init; }
    public required string Cost { get; init; }
    public required string Details { get; init; }
    public string Breakdown { get; init; } = "";
    public bool HasBreakdown => Breakdown.Length > 0;
}

public sealed class ReportsViewModel : ObservableObject
{
    private const string StateKey = "reports.";
    private int _periodIndex;
    private int _rangeIndex = 1;
    private ChoiceItem? _selectedSource;
    private bool _breakdown;
    private bool _bySource;
    private bool _groupByProject;
    private DateTime _since = DateTime.Today.AddDays(-29);
    private DateTime _until = DateTime.Today;
    private string _totalTokens = "";
    private string _totalCost = "";
    private string _totalDetails = "";
    private string _dateRangeSummary = "";
    private string _chartDescription = "";
    private string _pricingNote = "";
    private string _status = "";
    private bool _isBusy;
    private UsageReport? _report;

    public ReportsViewModel()
    {
        Sources = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];

        // Zapamiętany stan widoku (11.7.6) — pola ustawiane bezpośrednio, bez odświeżania.
        _periodIndex = ViewState.GetInt(StateKey + "period", 0, Periods.Length - 1);
        _rangeIndex = ViewState.GetInt(StateKey + "range", 1, Ranges.Length - 1);
        var sourceId = ViewState.GetString(StateKey + "source", "");
        _selectedSource = Sources.FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)) ?? Sources[0];
        _breakdown = ViewState.GetBool(StateKey + "breakdown", false);
        _bySource = ViewState.GetBool(StateKey + "bySource", false);
        _groupByProject = ViewState.GetBool(StateKey + "groupByProject", false);
        _since = ViewState.GetDate(StateKey + "since", _since);
        _until = ViewState.GetDate(StateKey + "until", _until);

        RefreshCommand = new Command(async () => await RefreshAsync());
        ExportJsonCommand = new Command(async () => await ExportAsync("json"));
        ExportCsvCommand = new Command(async () => await ExportAsync("csv"));
        ShowDetailsCommand = new Command<ReportRowViewModel>(async item => await ShowDetailsAsync(item));
    }

    public string[] Periods { get; } = ["Dziennie", "Tygodniowo", "Miesięcznie"];
    public string[] Ranges { get; } = ["7 dni", "30 dni", "Ten miesiąc", "90 dni", "Cała historia", "Własny zakres"];
    public ChoiceItem[] Sources { get; }
    public ObservableCollection<ReportRowViewModel> Rows { get; } = [];
    public BarChartDrawable Chart { get; } = new();

    public Command RefreshCommand { get; }
    public Command ExportJsonCommand { get; }
    public Command ExportCsvCommand { get; }
    public Command<ReportRowViewModel> ShowDetailsCommand { get; }

    public event EventHandler? ChartChanged;

    public int PeriodIndex
    {
        get => _periodIndex;
        set
        {
            if (Set(ref _periodIndex, value))
            {
                ViewState.Set(StateKey + "period", value);
                _ = RefreshAsync();
            }
        }
    }

    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
            {
                ViewState.Set(StateKey + "range", value);
                OnPropertyChanged(nameof(IsCustomRange));
                _ = RefreshAsync();
            }
        }
    }

    public bool IsCustomRange => RangeIndex == 5;

    public ChoiceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (Set(ref _selectedSource, value))
            {
                ViewState.Set(StateKey + "source", value?.Id ?? "");
                OnPropertyChanged(nameof(CanSplitBySource));
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Podział na źródła ma sens tylko przy „Wszystkie źródła”.</summary>
    public bool CanSplitBySource => string.IsNullOrEmpty(SelectedSource?.Id);

    public bool Breakdown { get => _breakdown; set { if (Set(ref _breakdown, value)) { ViewState.Set(StateKey + "breakdown", value); _ = RefreshAsync(); } } }

    /// <summary>Podział każdego okresu na źródła (ccusage <c>--by-agent</c>).</summary>
    public bool BySource { get => _bySource; set { if (Set(ref _bySource, value)) { ViewState.Set(StateKey + "bySource", value); _ = RefreshAsync(); } } }

    public bool GroupByProject { get => _groupByProject; set { if (Set(ref _groupByProject, value)) { ViewState.Set(StateKey + "groupByProject", value); _ = RefreshAsync(); } } }

    public DateTime Since
    {
        get => _since;
        set
        {
            if (Set(ref _since, value))
            {
                ViewState.Set(StateKey + "since", value);
                if (IsCustomRange)
                {
                    _ = RefreshAsync();
                }
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
                if (IsCustomRange)
                {
                    _ = RefreshAsync();
                }
            }
        }
    }

    public string TotalTokens { get => _totalTokens; private set => Set(ref _totalTokens, value); }
    public string TotalCost { get => _totalCost; private set => Set(ref _totalCost, value); }
    public string TotalDetails { get => _totalDetails; private set => Set(ref _totalDetails, value); }
    public string DateRangeSummary { get => _dateRangeSummary; private set => Set(ref _dateRangeSummary, value); }
    public string ChartDescription { get => _chartDescription; private set => Set(ref _chartDescription, value); }

    /// <summary>Stopka: modele bez ceny (*) i z ceną szacowaną (~).</summary>
    public string PricingNote { get => _pricingNote; private set { if (Set(ref _pricingNote, value)) { OnPropertyChanged(nameof(HasPricingNote)); } } }
    public bool HasPricingNote => PricingNote.Length > 0;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private ReportPeriod Period => (ReportPeriod)PeriodIndex;

    private string? SourceId => string.IsNullOrEmpty(SelectedSource?.Id) ? null : SelectedSource.Id;

    private ReportQuery BuildQuery()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? since = RangeIndex switch
        {
            0 => today.AddDays(-6),
            1 => today.AddDays(-29),
            2 => new DateOnly(today.Year, today.Month, 1),
            3 => today.AddDays(-89),
            4 => null,
            _ => DateOnly.FromDateTime(Since)
        };
        DateOnly? until = IsCustomRange ? DateOnly.FromDateTime(Until) : null;
        if (since is not null && until is not null && since > until)
        {
            (since, until) = (until, since);
        }

        return new ReportQuery
        {
            Since = since,
            Until = until,
            Sources = SourceId is null ? Array.Empty<string>() : new[] { SourceId },
            Order = SortOrder.Desc,
            Breakdown = Breakdown,
            BySource = BySource && SourceId is null,
            GroupByProject = GroupByProject
        };
    }

    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

    private string FormatPeriod(ReportRow row)
    {
        var start = row.PeriodStart;
        var label = Period switch
        {
            ReportPeriod.Monthly => start.ToString("MMMM yyyy", PolishCulture),
            ReportPeriod.Weekly => FormatWeek(start),
            _ => start.ToString("ddd, d MMM yyyy", PolishCulture)
        };
        return label;

        string FormatWeek(DateOnly weekStart)
        {
            var weekEnd = weekStart.AddDays(6);
            if (weekStart.Year != weekEnd.Year)
            {
                return $"{weekStart.ToString("d MMM yyyy", PolishCulture)} – {weekEnd.ToString("d MMM yyyy", PolishCulture)}";
            }

            return weekStart.Month == weekEnd.Month
                ? $"{weekStart.Day}–{weekEnd.ToString("d MMM yyyy", PolishCulture)}"
                : $"{weekStart.ToString("d MMM", PolishCulture)} – {weekEnd.ToString("d MMM yyyy", PolishCulture)}";
        }
    }

    private string FormatChartDate(DateOnly date, bool includeYear) => Period == ReportPeriod.Monthly
        ? date.ToString("MMM yy", PolishCulture)
        : date.ToString(includeYear ? "d MMM yy" : "d MMM", PolishCulture);

    private string DescribeRange(ReportQuery query, UsageReport report)
    {
        var name = RangeIndex switch
        {
            0 => "Ostatnie 7 dni",
            1 => "Ostatnie 30 dni",
            2 => "Ten miesiąc",
            3 => "Ostatnie 90 dni",
            4 => "Cała historia",
            _ => "Własny zakres"
        };
        var from = query.Since ?? (report.Rows.Count == 0 ? null : report.Rows.Min(row => row.PeriodStart));
        if (from is null)
        {
            return RangeIndex == 4 ? "Cała historia · brak danych" : $"{name} · brak danych";
        }

        var until = query.Until ?? DateOnly.FromDateTime(DateTime.Today);
        var dateRange = FormatDateRange(from.Value, until);
        return RangeIndex == 4 ? $"Cała historia · {dateRange}" : $"{name} · {dateRange}";
    }

    private static string FormatDateRange(DateOnly from, DateOnly until)
    {
        if (from.Year != until.Year)
        {
            return $"{from.ToString("d MMM yyyy", PolishCulture)} – {until.ToString("d MMM yyyy", PolishCulture)}";
        }

        return from.Month == until.Month
            ? $"{from.Day}–{until.ToString("d MMM yyyy", PolishCulture)}"
            : $"{from.ToString("d MMM", PolishCulture)} – {until.ToString("d MMM yyyy", PolishCulture)}";
    }

    private static string SourceName(string id) => AppState.Engine.FindSource(id)?.DisplayName ?? id;

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
            var query = BuildQuery();
            _report = await Task.Run(() => AppState.Engine.BuildReport(Period, query));
            Rows.Clear();
            foreach (var row in _report.Rows)
            {
                var lines = new List<string>();
                if (query.BySource && row.SourceBreakdowns.Count > 0)
                {
                    lines.AddRange(row.SourceBreakdowns.Select(source =>
                    {
                        var unpriced = source.ModelBreakdowns.Where(model => model.IsUnpriced).Select(model => model.Model).ToArray();
                        var estimated = source.ModelBreakdowns.Where(model => model.IsEstimated).Select(model => model.Model).ToArray();
                        return $"▸ {SourceName(source.Source)}: {Fmt.Tokens(source.Totals.TotalTokens)} · {Fmt.Cost(source.Totals.CostUsd)}{Fmt.Marker(unpriced, estimated)}" +
                               (source.Totals.Credits != 0 ? $" · kredyty {Fmt.Credits(source.Totals.Credits)}" : "");
                    }));
                }

                if (query.Breakdown)
                {
                    lines.AddRange(row.ModelBreakdowns.Select(model =>
                        $"└ {Fmt.Model(model.Model)}{Fmt.Marker(model)}: {Fmt.Tokens(model.Totals.TotalTokens)} · {Fmt.Cost(model.Totals.CostUsd)}" +
                        (model.Totals.Credits != 0 ? $" · kredyty {Fmt.Credits(model.Totals.Credits)}" : "")));
                }

                Rows.Add(new ReportRowViewModel
                {
                    Row = row,
                    Period = FormatPeriod(row) + (row.Project is null ? "" : "  ·  " + row.Project),
                    Subtitle = string.Join(", ", row.Models.Take(4).Select(model =>
                                   Fmt.Model(model) + (row.UnpricedModels.Contains(model) ? "*" : "") + (row.EstimatedModels.Contains(model) ? "~" : ""))) +
                               (row.Models.Count > 4 ? $" +{row.Models.Count - 4}" : ""),
                    Tokens = Fmt.Tokens(row.Totals.TotalTokens),
                    Cost = Fmt.Cost(row.Totals.CostUsd) + Fmt.Marker(row.UnpricedModels, row.EstimatedModels),
                    Details = Fmt.Summary(row.Totals) +
                              (!query.BySource && row.Sources.Count > 1
                                  ? "  ·  " + string.Join(", ", row.SourceBreakdowns.Select(source => $"{source.Source} {Fmt.Cost(source.Totals.CostUsd)}"))
                                  : ""),
                    Breakdown = string.Join("\n", lines)
                });
            }

            TotalTokens = Fmt.Tokens(_report.Totals.TotalTokens);
            TotalCost = Fmt.Cost(_report.Totals.CostUsd) + Fmt.Marker(_report.UnpricedModels, _report.EstimatedModels);
            TotalDetails = Fmt.Summary(_report.Totals) +
                           (_report.Totals.HasStoredCost ? $"  ·  zapisany {Fmt.Cost(_report.Totals.StoredCostUsd)} / wyliczony {Fmt.Cost(_report.Totals.CalculatedCostUsd)}" : "");
            DateRangeSummary = DescribeRange(query, _report);
            PricingNote = Fmt.PricingNote(_report.UnpricedModels, _report.EstimatedModels);
            Status = _report.Rows.Count == 0
                ? "Brak danych w wybranym zakresie."
                : $"{_report.Rows.Count} wierszy · cennik: {AppState.Engine.Pricing.Origin} · dotknij wiersza, aby zobaczyć szczegóły";

            // Wykres chronologicznie (po okresie), sumując ewentualne wiersze projektów.
            var chartGroups = _report.Rows.GroupBy(row => row.PeriodStart).OrderBy(group => group.Key).ToArray();
            var includeYear = Period == ReportPeriod.Monthly ||
                              (chartGroups.Length > 1 && chartGroups[0].Key.Year != chartGroups[^1].Key.Year);
            Chart.Bars = chartGroups
                .Select(group => new BarChartDrawable.Bar(
                    FormatChartDate(group.Key, includeYear),
                    group.Sum(row => row.Totals.CostUsd),
                    group.Sum(row => row.Totals.TotalTokens)))
                .ToArray();
            var highestBar = Chart.Bars.OrderByDescending(bar => bar.Cost).FirstOrDefault();
            ChartDescription = highestBar is null
                ? "Brak danych do wyświetlenia na wykresie."
                : $"Koszt w {Chart.Bars.Count} okresach. Najwyższy: {Fmt.Cost(highestBar.Cost)} ({highestBar.Label}).";
            ChartChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Szczegóły wiersza: tokeny (z cache 1 h osobno), koszt zapisany i wyliczony, kredyty, źródła i modele ze znacznikami.</summary>
    private static async Task ShowDetailsAsync(ReportRowViewModel? item)
    {
        if (item?.Row is not { } row || Application.Current?.Windows.FirstOrDefault()?.Page is not { } page)
        {
            return;
        }

        var lines = new List<string> { item.Period, "" };
        lines.AddRange(Fmt.DetailLines(row.Totals));
        if (row.SourceBreakdowns.Count > 0)
        {
            lines.Add("");
            lines.Add("Źródła:");
            lines.AddRange(row.SourceBreakdowns.Select(source =>
                $"• {SourceName(source.Source)}: {Fmt.Number(source.Totals.TotalTokens)} tok · {Fmt.Cost(source.Totals.CostUsd)}" +
                (source.Totals.HasStoredCost ? $" (zapisany {Fmt.Cost(source.Totals.StoredCostUsd)})" : "") +
                (source.Totals.Credits != 0 ? $" · kredyty {Fmt.Credits(source.Totals.Credits)}" : "")));
        }

        lines.Add("");
        lines.Add("Modele:");
        lines.AddRange(row.ModelBreakdowns.Select(model =>
            $"• {Fmt.Model(model.Model)}{Fmt.Marker(model)}: {Fmt.Number(model.Totals.TotalTokens)} tok · {Fmt.Cost(model.Totals.CostUsd)}" +
            (model.Totals.CacheCreation1hTokens > 0 ? $" · cache 1 h {Fmt.Tokens(model.Totals.CacheCreation1hTokens)}" : "") +
            (model.Totals.HasStoredCost ? $" · zapisany {Fmt.Cost(model.Totals.StoredCostUsd)}" : "") +
            (model.Totals.Credits != 0 ? $" · kredyty {Fmt.Credits(model.Totals.Credits)}" : "")));
        var note = Fmt.PricingNote(row.UnpricedModels, row.EstimatedModels);
        if (note.Length > 0)
        {
            lines.Add("");
            lines.Add(note);
        }

        await page.DisplayAlertAsync("Szczegóły okresu", string.Join("\n", lines), "Zamknij");
    }

    private async Task ExportAsync(string format)
    {
        if (_report is null)
        {
            return;
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OnionLLM");
        Directory.CreateDirectory(directory);
        var name = $"onionllm-{Period.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.{format}";
        var path = Path.Combine(directory, name);
        // JSON zgodny z ccusage: raport zbiorczy (period/agent/metadata) albo jednego źródła (date/week/month), z agents przy podziale na źródła.
        var content = format == "json"
            ? ReportExporter.ToJson(_report, new JsonExportOptions { Agent = SourceId, ByAgent = BySource && SourceId is null })
            : ReportExporter.ToCsv(_report);
        await File.WriteAllTextAsync(path, content);
        Status = "Zapisano: " + path;
        await Clipboard.Default.SetTextAsync(path);
    }
}

/// <summary>Wykres słupkowy kosztu z siatką, skalą wartości i etykietami okresów.</summary>
public sealed class BarChartDrawable : IDrawable
{
    public sealed record Bar(string Label, double Cost, long Tokens);

    public IReadOnlyList<Bar> Bars { get; set; } = [];

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (Bars.Count == 0)
        {
            return;
        }

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var textColor = Color.FromArgb(dark ? "#A4A9B6" : "#727986");
        var gridColor = Color.FromArgb(dark ? "#343846" : "#E7E9F0");
        const float labelWidth = 48;
        const float labelHeight = 17;
        const float topPadding = 5;
        var plotLeft = dirtyRect.Left + labelWidth;
        var plotRight = dirtyRect.Right - 4;
        var plotTop = dirtyRect.Top + topPadding;
        var plotBottom = dirtyRect.Bottom - labelHeight;
        var plotHeight = Math.Max(1, plotBottom - plotTop);
        var plotWidth = Math.Max(1, plotRight - plotLeft);
        var max = Math.Max(Bars.Max(bar => bar.Cost), 0.0001);
        var slot = plotWidth / Bars.Count;
        var barWidth = Math.Max(2, Math.Min(26, slot * 0.68f));
        var labelEvery = Math.Max(1, (int)Math.Ceiling(Bars.Count / Math.Max(1, plotWidth / 44)));

        canvas.FontSize = 8;
        canvas.FontColor = textColor;
        canvas.StrokeSize = 1;
        for (var step = 0; step <= 4; step++)
        {
            var fraction = step / 4f;
            var y = plotBottom - fraction * plotHeight;
            canvas.StrokeColor = gridColor;
            canvas.DrawLine(plotLeft, y, plotRight, y);
            if (step > 0)
            {
                canvas.DrawString(Fmt.Cost(max * fraction), dirtyRect.Left, y - 6, labelWidth - 5, 12,
                    HorizontalAlignment.Right, VerticalAlignment.Center);
            }
        }

        for (var index = 0; index < Bars.Count; index++)
        {
            var bar = Bars[index];
            var height = (float)(bar.Cost / max * plotHeight);
            var x = plotLeft + slot * index + (slot - barWidth) / 2;
            var y = plotBottom - height;
            canvas.FillColor = Color.FromArgb(index == Bars.Count - 1
                ? dark ? "#9B8AFF" : "#7257E8"
                : dark ? "#48B8B1" : "#159C99");
            canvas.FillRoundedRectangle(x, y, barWidth, Math.Max(1, height), 3);

            if (index % labelEvery == 0 || index == Bars.Count - 1)
            {
                canvas.DrawString(bar.Label, plotLeft + slot * index - 8, plotBottom + 1, slot + 16, labelHeight - 1,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }
        }
    }
}
