using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Export;
using OnionLLM.Core.Reports;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

/// <summary>Row of the blocks table (DESIGN.md §6.5); gaps are separator rows (<see cref="IsGap"/>).</summary>
public sealed class BlockItemViewModel
{
    public required SessionBlock Block { get; init; }

    public long? TokenLimit { get; init; }

    public bool IsGap => Block.IsGap;

    public bool IsActive => Block.IsActive;

    public string Title => IsGap
        ? $"Przerwa {Fmt.Duration(Block.End - Block.Start)}"
        : $"{Fmt.DateTimeShort(Block.Start)} – {Fmt.Time(Block.End)}";

    public string ModelsText => IsGap ? "" : string.Join(", ", Block.Models.Select(Fmt.Model));

    public string Tokens => IsGap ? "" : Fmt.Tokens(Block.Totals.TotalTokens);

    public string Cost => IsGap ? "" : Fmt.Cost(Block.Totals.CostUsd);

    public double LimitPercent => !IsGap && TokenLimit is > 0 ? Block.Totals.TotalTokens * 100.0 / TokenLimit.Value : double.NaN;

    public string LimitText => double.IsNaN(LimitPercent) ? "" : Fmt.Percent(LimitPercent);

    public double Now => Block.IsActive && Block.End > Block.Start
        ? Math.Clamp((DateTimeOffset.Now - Block.Start) / (Block.End - Block.Start), 0, 1)
        : Block.LastActivity is { } last && Block.End > Block.Start ? Math.Clamp((last - Block.Start) / (Block.End - Block.Start), 0, 1) : 1;

    /// <summary>Start and end values only — per-entry cumulative series in a block: Wymaga danych.</summary>
    public IReadOnlyList<double> Series => IsGap ? [] : [0, Block.Totals.CostUsd];
}

/// <summary>Screen „Bloki” (DESIGN.md §6.5).</summary>
public sealed class BlocksViewModel : ObservableObject
{
    private int _rangeIndex = 1;
    private bool _isBusy;
    private bool _loaded;
    private string? _error;
    private BlocksReport? _report;
    private IReadOnlyList<BlockItemViewModel> _blocks = [];
    private BlockItemViewModel? _selected;

    public BlocksViewModel()
    {
        _rangeIndex = ViewState.GetInt("blocks.range", 1, Ranges.Length - 1);
        OpenDetailsCommand = new Command<BlockItemViewModel>(OpenDetails);
        ExportJsonCommand = new Command(async () => await ExportAsync());
        CopyTableCommand = new Command(async () => await ExportService.CopyTableAsync(TableRows()));
        ConfigureCommand = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(
            ShellViewModel.Instance.Navigation.IsAvailable(Screen.Sources) ? Screen.Sources : Screen.Settings));
    }

    public string[] Ranges { get; } = ["3 dni", "7 dni", "30 dni"];

    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
            {
                ViewState.Set("blocks.range", value);
                _ = RefreshAsync();
            }
        }
    }

    public Command<BlockItemViewModel> OpenDetailsCommand { get; }

    public Command ExportJsonCommand { get; }

    public Command CopyTableCommand { get; }

    public Command ConfigureCommand { get; }

    public IReadOnlyList<BlockItemViewModel> Blocks { get => _blocks; private set => Set(ref _blocks, value); }

    public BlockItemViewModel? SelectedBlock { get => _selected; set => Set(ref _selected, value); }

    /// <summary>Claude Code not detected / disabled → page empty state (§6.5 „Stany”).</summary>
    public bool ClaudeMissing { get; private set; }

    public TableState TableState => _error is not null ? TableState.Error : !_loaded ? TableState.Loading : Blocks.Count == 0 ? TableState.Empty : TableState.Ready;

    // ===== Active block card =====

    public bool HasActive => _report?.Active is not null;

    public bool HasNoActive => _loaded && !HasActive && !ClaudeMissing;

    public string LastBlockText => _report?.Blocks.LastOrDefault(block => !block.IsGap) is { } last
        ? $"Ostatni blok zakończył się o {Fmt.Time(last.LastActivity ?? last.End)} ({Fmt.ShortDate(last.LastActivity ?? last.End)})."
        : "Brak bloków w wybranym zakresie.";

    public BlockItemViewModel? Active => _report?.Active is { } block ? new BlockItemViewModel { Block = block, TokenLimit = _report.TokenLimit } : null;

    public string ActiveRemaining => _report?.Active is { } block
        ? $"Pozostało {Fmt.Duration(block.Remaining(DateTimeOffset.Now))} · do {Fmt.Time(block.End)}"
        : "";

    public double ActiveCost => _report?.Active?.Totals.CostUsd ?? double.NaN;

    public string ActiveTokens => _report?.Active is { } block ? Fmt.TokensWithUnit(block.Totals.TotalTokens) : "";

    public bool HasBurnRate => _report?.Active?.BurnRate is not null;

    public string BurnCostPerHour => _report?.Active?.BurnRate is { } burn ? Fmt.CostPerHour(burn.CostPerHour) : "";

    public string BurnTokensPerMinute => _report?.Active?.BurnRate is { } burn ? Fmt.TokensPerMinute(burn.TokensPerMinute) : "";

    public BurnRateLevel BurnLevel => _report?.Active?.BurnRate?.Level ?? BurnRateLevel.Normal;

    public double ActiveProjection => _report?.Active?.Projection?.TotalCostUsd ?? double.NaN;

    public IReadOnlyList<double> ActiveSeries => Active?.Series ?? [];

    public double ActiveNow => Active?.Now ?? 1;

    public bool HasLimit => _report?.TokenLimit is > 0;

    public double ActiveLimitPercent => Active?.LimitPercent ?? double.NaN;

    public string LimitTitle => _report?.TokenLimit is { } limit and > 0
        ? $"Limit tokenów bloku · {Fmt.Tokens(limit)}{(_report.TokenLimitIsMax ? " (maksimum z historii)" : "")}"
        : "Limit tokenów bloku";

    /// <summary>„Przy obecnym tempie blok przekroczy limit ok. 15:40” (§6.5 pkt 2).</summary>
    public string ProjectionText
    {
        get
        {
            if (_report?.Active is not { Projection: { } projection } block)
            {
                return "";
            }

            var text = $"Prognoza do końca bloku: {Fmt.Cost(projection.TotalCostUsd)} · {Fmt.TokensWithUnit(projection.TotalTokens)}";
            if (_report.TokenLimit is { } limit and > 0 && projection.TotalTokens > limit && block.BurnRate is { TokensPerMinute: > 0 } burn)
            {
                var minutes = Math.Max(0, (limit - block.Totals.TotalTokens) / burn.TokensPerMinute);
                text = $"Przy obecnym tempie blok przekroczy limit ok. {Fmt.Time(DateTimeOffset.Now.AddMinutes(minutes))}. " + text;
            }

            return text;
        }
    }

    public bool ProjectionWarns => _report is { Active.Projection: { } projection, TokenLimit: { } limit and > 0 } && projection.TotalTokens > limit;

    // ===== KPI =====

    public string BlockCount => _report is null ? "" : Fmt.Number(RealBlocks.Count);

    public string AverageCost => RealBlocks.Count == 0 ? "" : Fmt.Cost(RealBlocks.Average(block => block.Totals.CostUsd));

    public string LimitValue => _report is null ? "" : _report.TokenLimit is { } limit and > 0 ? Fmt.Tokens(limit) : "Bez limitu";

    public string LimitCaption => _report is { TokenLimitIsMax: true } ? "maksimum z historii" : "";

    public string MostExpensive => RealBlocks.Count == 0 ? "" : Fmt.Cost(RealBlocks.Max(block => block.Totals.CostUsd));

    public string MostExpensiveCaption => RealBlocks.Count == 0 ? "" : Fmt.DateTimeShort(RealBlocks.MaxBy(block => block.Totals.CostUsd)!.Start);

    public string RangeText => Ranges[RangeIndex];

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private List<SessionBlock> RealBlocks => _report?.Blocks.Where(block => !block.IsGap).ToList() ?? [];

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
            var claude = AppState.Engine.FindSource("claude");
            var detected = AppState.Engine.LastSync?.Sources.FirstOrDefault(source => source.Source == "claude") is { Files: > 0 };
            ClaudeMissing = claude is null || !AppState.Config.IsSourceEnabled("claude") || !detected;
            var since = DateOnly.FromDateTime(Fmt.NowLocal).AddDays(RangeIndex switch { 0 => -2, 1 => -6, _ => -29 });
            DateTimeOffset? resetsAt = AppState.Limits is { } limits && limits.TryGetValue("claude", out var claudeLimits) && claudeLimits.IsAvailable
                ? claudeLimits.FiveHourReset
                : null;
            var now = DateTimeOffset.Now;
            _report = await Task.Run(() => AppState.Engine.BuildBlocks(since, now: now, activeResetsAt: resetsAt));
            Blocks = _report.Blocks.Reverse().Select(block => new BlockItemViewModel { Block = block, TokenLimit = _report.TokenLimit }).ToList();
            _error = null;
            _loaded = true;
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
                         nameof(ClaudeMissing), nameof(TableState), nameof(HasActive), nameof(HasNoActive), nameof(LastBlockText), nameof(Active),
                         nameof(ActiveRemaining), nameof(ActiveSeries), nameof(ActiveNow), nameof(ActiveCost), nameof(ActiveTokens), nameof(HasBurnRate), nameof(BurnCostPerHour),
                         nameof(BurnTokensPerMinute), nameof(BurnLevel), nameof(ActiveProjection), nameof(HasLimit), nameof(ActiveLimitPercent),
                         nameof(LimitTitle), nameof(ProjectionText), nameof(ProjectionWarns), nameof(BlockCount), nameof(AverageCost),
                         nameof(LimitValue), nameof(LimitCaption), nameof(MostExpensive), nameof(MostExpensiveCaption), nameof(RangeText)
                     })
            {
                OnPropertyChanged(name);
            }
        }
    }

    /// <summary>Block drawer (§6.5 pkt 5): timeline, KPI, burn rate, models.</summary>
    public void OpenDetails(BlockItemViewModel? item)
    {
        if (item is null || item.IsGap)
        {
            return;
        }

        SelectedBlock = item;
        var real = Blocks.Where(block => !block.IsGap).ToList();
        var index = real.IndexOf(item);
        var block = item.Block;
        var content = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        content.Children.Add(new BlockTimeline { Cumulative = item.Series, Now = item.Now, Projection = block.Projection?.TotalCostUsd ?? double.NaN });
        var tiles = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        tiles.Add(new StatTile { Title = "Koszt", Value = item.Cost });
        tiles.Add(new StatTile { Title = "Tokeny", Value = item.Tokens, Caption = item.LimitText.Length > 0 ? item.LimitText + " limitu" : "" }, 1);
        content.Children.Add(tiles);
        if (block.BurnRate is { } burn)
        {
            content.Children.Add(new BurnRateBadge { CostPerHour = Fmt.CostPerHour(burn.CostPerHour), TokensPerMinute = Fmt.TokensPerMinute(burn.TokensPerMinute), Level = burn.Level });
        }

        content.Children.Add(new Section
        {
            Title = "Tokeny",
            Body = new TokenBreakdown { Input = block.Totals.InputTokens, Output = block.Totals.OutputTokens, CacheWrite = block.Totals.CacheCreationTokens, CacheRead = block.Totals.CacheReadTokens }
        });
        var models = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var model in block.Models)
        {
            models.Children.Add(new ModelTag { Model = model, Margin = new Thickness(0, 0, Ui.D("Space150"), Ui.D("Space150")) });
        }

        content.Children.Add(new Section { Title = "Modele", Body = models });
        content.Children.Add(new Section
        {
            Title = "Szczegóły",
            Body = new KeyValueList
            {
                ItemsSource = new List<KeyValueItem>
                {
                    new("Start", Fmt.DateTimeShort(block.Start)),
                    new("Koniec okna", Fmt.DateTimeShort(block.End)),
                    new("Ostatnia aktywność", block.LastActivity is { } last ? Fmt.DateTimeShort(last) : "—"),
                    new("Wpisy", Fmt.Number(block.EntryCount), IsNumeric: true)
                }
            }
        });
        DrawerService.Open(new DrawerRequest
        {
            Overline = "Blok",
            Title = item.Title,
            Subtitle = block.IsActive ? ActiveRemaining : $"{Fmt.Duration(block.Elapsed(DateTimeOffset.Now))} aktywności",
            Content = content,
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(real[index - 1])) : null,
            NextCommand = index >= 0 && index < real.Count - 1 ? new Command(() => OpenDetails(real[index + 1])) : null,
            MoreEntries = [new MenuEntry("Kopiuj ID bloku", () => _ = Ui.CopyAsync(block.Id), Icons.Copy)],
            Closed = () => SelectedBlock = null
        });
    }

    private async Task ExportAsync()
    {
        if (_report is null)
        {
            return;
        }

        await ExportService.SaveAsync("bloki", Ranges[RangeIndex].Replace(' ', '-'), "json", ReportExporter.ToJson(_report, DateTimeOffset.Now), RealBlocks.Count);
    }

    private IEnumerable<IEnumerable<string>> TableRows()
    {
        yield return ["Blok", "Modele", "% limitu", "Tokeny", "Koszt"];
        foreach (var row in Blocks.Where(block => !block.IsGap))
        {
            yield return [row.Title, row.ModelsText, row.LimitText, Fmt.Number(row.Block.Totals.TotalTokens), Fmt.CostFull(row.Block.Totals.CostUsd)];
        }
    }
}
