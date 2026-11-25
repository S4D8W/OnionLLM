using System.Collections.ObjectModel;
using OnionLLM.Core.Export;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

public sealed class SessionItemViewModel(SessionRow row)
{
    public SessionRow Row { get; } = row;
    public string Title => string.IsNullOrWhiteSpace(Row.Project) ? Fmt.SessionId(Row.SessionId, 40) : Row.Project!;
    public string SessionShort => Fmt.SessionId(Row.SessionId);
    public string Source => Row.Source;
    public string When => $"{Fmt.LocalTime(Row.FirstActivity)} → {Fmt.LocalTime(Row.LastActivity, "HH:mm")} ({Fmt.Duration(Row.Duration)})";
    public string Models => string.Join(", ", Row.Models.Take(3).Select(model =>
                                Fmt.Model(model) + (Row.UnpricedModels.Contains(model) ? "*" : "") + (Row.EstimatedModels.Contains(model) ? "~" : ""))) +
                            (Row.Models.Count > 3 ? $" +{Row.Models.Count - 3}" : "");
    public string Tokens => Fmt.Tokens(Row.Totals.TotalTokens);
    public string Cost => Fmt.Cost(Row.Totals.CostUsd) + Fmt.Marker(Row.UnpricedModels, Row.EstimatedModels);
    public string Details => Fmt.Summary(Row.Totals) + $" · {Row.Totals.Requests} wywołań";
}

public sealed class SessionsViewModel : ObservableObject
{
    private const string StateKey = "sessions.";
    private int _rangeIndex = 1;
    private int _sortIndex;
    private ChoiceItem? _selectedSource;
    private string _search = "";
    private string _status = "";
    private string _total = "";
    private string _pricingNote = "";
    private bool _isBusy;
    private SessionReport? _report;

    public SessionsViewModel()
    {
        Sources = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];

        // Zapamiętany stan widoku (11.7.6).
        _rangeIndex = ViewState.GetInt(StateKey + "range", 1, Ranges.Length - 1);
        _sortIndex = ViewState.GetInt(StateKey + "sort", 0, Sorts.Length - 1);
        var sourceId = ViewState.GetString(StateKey + "source", "");
        _selectedSource = Sources.FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)) ?? Sources[0];

        RefreshCommand = new Command(async () => await RefreshAsync());
        ExportCommand = new Command(async () => await ExportAsync("csv"));
        ExportJsonCommand = new Command(async () => await ExportAsync("json"));
        ShowDetailsCommand = new Command<SessionItemViewModel>(async item => await ShowDetailsAsync(item));
    }

    public string[] Ranges { get; } = ["Dziś", "7 dni", "30 dni", "Cała historia"];
    public string[] Sorts { get; } = ["Najnowsze", "Najdroższe", "Najwięcej tokenów"];
    public ChoiceItem[] Sources { get; }
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    public Command RefreshCommand { get; }
    public Command ExportCommand { get; }
    public Command ExportJsonCommand { get; }
    public Command<SessionItemViewModel> ShowDetailsCommand { get; }

    public int RangeIndex { get => _rangeIndex; set { if (Set(ref _rangeIndex, value)) { ViewState.Set(StateKey + "range", value); _ = RefreshAsync(); } } }
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) { ViewState.Set(StateKey + "sort", value); Apply(); } } }

    public ChoiceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (Set(ref _selectedSource, value))
            {
                ViewState.Set(StateKey + "source", value?.Id ?? "");
                _ = RefreshAsync();
            }
        }
    }

    public string Search { get => _search; set { if (Set(ref _search, value)) { Apply(); } } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Total { get => _total; private set => Set(ref _total, value); }
    public string PricingNote { get => _pricingNote; private set { if (Set(ref _pricingNote, value)) { OnPropertyChanged(nameof(HasPricingNote)); } } }
    public bool HasPricingNote => PricingNote.Length > 0;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string? SourceId => string.IsNullOrEmpty(SelectedSource?.Id) ? null : SelectedSource.Id;

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
            var today = DateOnly.FromDateTime(DateTime.Today);
            var query = new ReportQuery
            {
                Since = RangeIndex switch { 0 => today, 1 => today.AddDays(-6), 2 => today.AddDays(-29), _ => null },
                Sources = SourceId is null ? Array.Empty<string>() : new[] { SourceId },
                Order = SortOrder.Desc,
                Breakdown = true
            };
            _report = await Task.Run(() => AppState.Engine.BuildSessions(query));
            Apply();
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
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            rows = rows.Where(row =>
                row.SessionId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (row.Project?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                row.Source.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Models.Any(model => model.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        rows = SortIndex switch
        {
            1 => rows.OrderByDescending(row => row.Totals.CostUsd),
            2 => rows.OrderByDescending(row => row.Totals.TotalTokens),
            _ => rows.OrderByDescending(row => row.LastActivity)
        };

        var list = rows.ToList();
        Sessions.Clear();
        foreach (var row in list.Take(300))
        {
            Sessions.Add(new SessionItemViewModel(row));
        }

        var credits = list.Sum(row => row.Totals.Credits);
        var unpriced = list.SelectMany(row => row.UnpricedModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var estimated = list.SelectMany(row => row.EstimatedModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Total = $"{list.Count} sesji · {Fmt.Tokens(list.Sum(row => row.Totals.TotalTokens))} tokenów · " +
                $"{Fmt.Cost(list.Sum(row => row.Totals.CostUsd))}{Fmt.Marker(unpriced, estimated)}" +
                (credits != 0 ? $" · kredyty {Fmt.Credits(credits)}" : "");
        PricingNote = Fmt.PricingNote(unpriced, estimated);
        Status = list.Count > 300 ? "Pokazano 300 pierwszych — zawęź wyszukiwanie." : "Dotknij sesji, aby zobaczyć szczegóły.";
    }

    private static async Task ShowDetailsAsync(SessionItemViewModel? item)
    {
        if (item is null || Application.Current?.Windows.FirstOrDefault()?.Page is not { } page)
        {
            return;
        }

        var row = item.Row;
        var lines = new List<string>
        {
            $"ID: {row.SessionId}",
            $"Źródło: {AppState.Engine.FindSource(row.Source)?.DisplayName ?? row.Source}",
            $"Projekt: {row.Project} {(row.ProjectPath is null ? "" : "(" + row.ProjectPath + ")")}",
            $"Czas: {item.When}"
        };
        lines.AddRange(Fmt.DetailLines(row.Totals));
        lines.Add("");
        lines.AddRange(row.ModelBreakdowns.Select(model =>
            $"• {Fmt.Model(model.Model)}{Fmt.Marker(model)}{(model.IsUnpriced ? " (brak ceny)" : model.IsEstimated ? " (cena szacowana)" : "")}: " +
            $"{Fmt.Number(model.Totals.TotalTokens)} tok · {Fmt.Cost(model.Totals.CostUsd)}" +
            (model.Totals.CacheCreation1hTokens > 0 ? $" · cache 1 h {Fmt.Tokens(model.Totals.CacheCreation1hTokens)}" : "") +
            (model.Totals.HasStoredCost ? $" · zapisany {Fmt.Cost(model.Totals.StoredCostUsd)}" : "") +
            (model.Totals.Credits != 0 ? $" · kredyty {Fmt.Credits(model.Totals.Credits)}" : "")));

        var copy = await page.DisplayAlertAsync("Sesja", string.Join("\n", lines), "Kopiuj ID", "Zamknij");
        if (copy)
        {
            await Clipboard.Default.SetTextAsync(row.SessionId);
        }
    }

    private async Task ExportAsync(string format)
    {
        if (_report is null)
        {
            return;
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OnionLLM");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"onionllm-sessions-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");
        var content = format == "json"
            ? ReportExporter.ToJson(_report, new JsonExportOptions { Agent = SourceId })
            : ReportExporter.ToCsv(_report);
        await File.WriteAllTextAsync(path, content);
        await Clipboard.Default.SetTextAsync(path);
        Status = "Zapisano: " + path;
    }
}
