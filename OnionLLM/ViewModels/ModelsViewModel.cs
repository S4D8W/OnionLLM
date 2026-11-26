using OnionLLM.Controls;
using OnionLLM.Controls.Charts;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

/// <summary>Row of the „Modele” table (DESIGN.md §6.7).</summary>
public sealed class ModelRowViewModel
{
    public required string Model { get; init; }

    public required TokenCounts Totals { get; init; }

    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public ModelPricing? Pricing { get; init; }

    public bool IsUnpriced { get; init; }

    public bool IsEstimated { get; init; }

    public bool IsOverridden { get; init; }

    /// <summary>Daily cost series in the range (usage over time in the drawer).</summary>
    public IReadOnlyList<double> Daily { get; init; } = [];

    public string Name => Fmt.Model(Model);

    public string Provider => Pricing is null ? "brak ceny"
        : IsOverridden ? "nadpisana"
        : Pricing.Source switch { PricingSource.ModelsDev => "models.dev", PricingSource.Builtin => "wbudowany", PricingSource.Override => "nadpisana", _ => "LiteLLM" }
          + (Pricing.Provider is { Length: > 0 } provider ? " · " + provider : "");

    public string Sources => string.Join(", ", SourceIds.Select(id => AppState.Engine.FindSource(id)?.DisplayName ?? id));

    public string Tokens => Fmt.Tokens(Totals.TotalTokens);

    public string CacheShare => Totals.TotalTokens > 0 ? Fmt.Percent((Totals.CacheCreationTokens + Totals.CacheReadTokens) * 100.0 / Totals.TotalTokens) : "—";

    public string InputPrice => Price(Pricing?.Base.Input);

    public string OutputPrice => Price(Pricing?.Base.Output);

    public string Cost => Fmt.Cost(Totals.CostUsd);

    public string Marker => (IsUnpriced ? "*" : "") + (IsEstimated ? "~" : "");

    public double CostShare { get; set; }

    internal static string Price(double? perToken) => perToken is { } value ? Fmt.PricePerMillion(value) : "brak ceny";
}

/// <summary>Screen „Modele” (DESIGN.md §6.7): usage per model and its price per 1 M tokens.</summary>
public sealed class ModelsViewModel : ObservableObject
{
    private static readonly RangePreset[] PresetOrder = Enum.GetValues<RangePreset>();
    private int _rangeIndex = (int)RangePreset.Last30Days;
    private int _sourceIndex;
    private string _search = string.Empty;
    private bool _onlyUnpriced;
    private string _sortKey = nameof(ModelRowViewModel.Cost);
    private bool _sortDescending = true;
    private DateTime _since = DateTime.Today.AddDays(-29);
    private DateTime _until = DateTime.Today;
    private bool _isBusy;
    private bool _loaded;
    private List<ModelRowViewModel> _all = [];
    private IReadOnlyList<ModelRowViewModel> _rows = [];
    private ModelRowViewModel? _selected;
    private IReadOnlyList<DateOnly> _days = [];

    public ModelsViewModel()
    {
        SourceChoices = [new ChoiceItem("", "Wszystkie źródła"), .. AppState.Engine.Sources.Select(source => new ChoiceItem(source.Id, source.DisplayName))];
        SourceChoiceNames = SourceChoices.Select(choice => choice.Name).ToArray();
        OpenDetailsCommand = new Command<ModelRowViewModel>(OpenDetails);
        ResetFiltersCommand = new Command(() => { RangeIndex = (int)RangePreset.Last30Days; SourceIndex = 0; OnlyUnpriced = false; Search = string.Empty; OnPropertyChanged(nameof(Search)); });
        AddPricesCommand = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Settings, "Cennik"));
        ShowAllModelsCommand = new Command(() => OnlyUnpriced = false);
        CopyTableCommand = new Command(async () => await ExportService.CopyTableAsync(TableRows()));
    }

    public string[] Presets { get; } = PresetOrder.Select(DateRanges.Label).ToArray();

    public int CustomPresetIndex => (int)RangePreset.Custom;

    public ChoiceItem[] SourceChoices { get; }

    public string[] SourceChoiceNames { get; }

    public Command<ModelRowViewModel> OpenDetailsCommand { get; }

    public Command ResetFiltersCommand { get; }

    public Command AddPricesCommand { get; }

    public Command ShowAllModelsCommand { get; }

    public Command CopyTableCommand { get; }

    public int RangeIndex { get => _rangeIndex; set { if (Set(ref _rangeIndex, value)) { _ = RefreshAsync(); } } }

    public int SourceIndex { get => _sourceIndex; set { if (Set(ref _sourceIndex, value)) { _ = RefreshAsync(); } } }

    public DateTime Since { get => _since; set { if (Set(ref _since, value)) { _ = RefreshAsync(); } } }

    public DateTime Until { get => _until; set { if (Set(ref _until, value)) { _ = RefreshAsync(); } } }

    public string Search { get => _search; set { if (Set(ref _search, value)) { Apply(); } } }

    /// <summary>FilterChip „Tylko bez ceny”.</summary>
    public bool OnlyUnpriced { get => _onlyUnpriced; set { if (Set(ref _onlyUnpriced, value)) { Apply(); } } }

    public string SortKey { get => _sortKey; set { if (Set(ref _sortKey, value)) { Apply(); } } }

    public bool SortDescending { get => _sortDescending; set { if (Set(ref _sortDescending, value)) { Apply(); } } }

    public bool IsFiltered => RangeIndex != (int)RangePreset.Last30Days || SourceIndex != 0 || OnlyUnpriced || Search.Length > 0;

    public IReadOnlyList<ModelRowViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }

    public ModelRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    public string Subtitle { get; private set; } = string.Empty;

    public string RangeText { get; private set; } = string.Empty;

    public int UnpricedCount => _all.Count(row => row.IsUnpriced);

    /// <summary>One banner (§4.6.4): missing prices (warning) before offline/stale pricing (info).</summary>
    public bool HasBanner => UnpricedCount > 0 || PricingOffline;

    public BannerKind BannerKind => UnpricedCount > 0 ? BannerKind.Warning : BannerKind.Info;

    public string BannerTitle => UnpricedCount > 0 ? $"{Fmt.Count(UnpricedCount, "model", "modele", "modeli")} bez ceny" : "Cennik offline lub nieaktualny";

    public string BannerMessage => UnpricedCount > 0
        ? "Koszt tych modeli jest zaniżony. Dodaj nadpisanie ceny lub alias do modelu z cennika."
        : "Ceny mogą nie uwzględniać najnowszych modeli. Odśwież cennik w Ustawieniach.";

    public string BannerAction => UnpricedCount > 0 ? "Dodaj ceny" : "Przejdź do cennika";

    private static bool PricingOffline => AppState.Config.Offline ||
                                          AppState.Engine.Pricing.UpdatedAt is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromDays(7);

    public TableState TableState => !_loaded ? TableState.Loading
        : Rows.Count > 0 ? TableState.Ready
        : OnlyUnpriced ? TableState.NoResults
        : TableState.Empty;

    public string EmptyTitle => OnlyUnpriced && _all.Count > 0 && UnpricedCount == 0 ? "Wszystkie modele mają ceny" : "Brak modeli w tym zakresie";

    public string EmptyAction => OnlyUnpriced ? "Pokaż wszystkie modele" : "Wyczyść filtry";

    public System.Windows.Input.ICommand EmptyCommand => OnlyUnpriced ? ShowAllModelsCommand : ResetFiltersCommand;

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
                BySource = true
            };
            var daily = await Task.Run(() => AppState.Engine.BuildReport(ReportPeriod.Daily, query));
            var first = since ?? daily.Rows.Select(row => (DateOnly?)row.PeriodStart).Min() ?? until;
            _days = Enumerable.Range(0, until.DayNumber - first.DayNumber + 1).Select(first.AddDays).ToList();
            RangeText = DateRanges.Describe(Preset, since, until);
            var pricing = AppState.Engine.Pricing;
            var overrides = AppState.Config.PricingOverrides.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _all = daily.Rows.SelectMany(row => row.SourceBreakdowns.SelectMany(source => source.ModelBreakdowns.Select(model => (row.PeriodStart, source.Source, model))))
                .GroupBy(item => item.model.Model)
                .Select(group =>
                {
                    var totals = new TokenCounts();
                    foreach (var item in group)
                    {
                        totals.Add(item.model.Totals);
                    }

                    var byDay = group.GroupBy(item => item.PeriodStart).ToDictionary(g => g.Key, g => g.Sum(item => item.model.Totals.CostUsd));
                    return new ModelRowViewModel
                    {
                        Model = group.Key,
                        Totals = totals,
                        SourceIds = group.Select(item => item.Source).Distinct().ToList(),
                        Pricing = pricing.Resolve(group.Key),
                        IsUnpriced = group.Any(item => item.model.IsUnpriced),
                        IsEstimated = group.Any(item => item.model.IsEstimated),
                        IsOverridden = overrides.Contains(group.Key),
                        Daily = _days.Select(day => byDay.GetValueOrDefault(day)).ToList()
                    };
                }).ToList();
            var max = _all.Select(row => row.Totals.CostUsd).DefaultIfEmpty(0).Max();
            foreach (var row in _all)
            {
                row.CostShare = max > 0 ? row.Totals.CostUsd / max : 0;
            }

            Subtitle = $"{Fmt.Count(_all.Count, "model", "modele", "modeli")} · {RangeText}";
            _loaded = true;
            Apply();
        }
        finally
        {
            IsBusy = false;
            foreach (var name in new[]
                     {
                         nameof(Subtitle), nameof(RangeText), nameof(UnpricedCount), nameof(HasBanner), nameof(BannerKind), nameof(BannerTitle),
                         nameof(BannerMessage), nameof(BannerAction), nameof(TableState), nameof(IsFiltered)
                     })
            {
                OnPropertyChanged(name);
            }
        }
    }

    private void Apply()
    {
        IEnumerable<ModelRowViewModel> rows = _all;
        if (OnlyUnpriced)
        {
            rows = rows.Where(row => row.IsUnpriced);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            rows = rows.Where(row => row.Model.Contains(term, StringComparison.OrdinalIgnoreCase) || Select.Normalize(row.Sources).Contains(Select.Normalize(term), StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<ModelRowViewModel> sorted = SortKey switch
        {
            nameof(ModelRowViewModel.Name) => rows.OrderBy(row => row.Name, StringComparer.Ordinal),
            nameof(ModelRowViewModel.Tokens) => rows.OrderBy(row => row.Totals.TotalTokens),
            nameof(ModelRowViewModel.InputPrice) => rows.OrderBy(row => row.Pricing?.Base.Input ?? -1),
            _ => rows.OrderBy(row => row.Totals.CostUsd)
        };
        Rows = (SortDescending ? sorted.Reverse() : sorted).ToList();
        foreach (var name in new[] { nameof(TableState), nameof(EmptyTitle), nameof(EmptyAction), nameof(EmptyCommand), nameof(IsFiltered) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Model drawer (§6.7): pricing card with override actions, usage in time, tokens, sources, aliases.</summary>
    public void OpenDetails(ModelRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        var index = Rows.ToList().IndexOf(row);
        var pricing = row.Pricing;
        var rates = new List<KeyValueItem>
        {
            new("Wejście", ModelRowViewModel.Price(pricing?.Base.Input), IsNumeric: true),
            new("Wyjście", ModelRowViewModel.Price(pricing?.Base.Output), IsNumeric: true),
            new("Zapis cache 5 min", ModelRowViewModel.Price(pricing?.Base.CacheWrite), IsNumeric: true),
            new("Zapis cache 1 h", ModelRowViewModel.Price(pricing?.Base.CacheWrite1h), IsNumeric: true),
            new("Odczyt cache", ModelRowViewModel.Price(pricing?.Base.CacheRead), IsNumeric: true)
        };
        if (pricing?.Tiers.FirstOrDefault() is { } tier)
        {
            rates.Add(new KeyValueItem($"Wejście > {Fmt.Tokens(tier.AboveTokens)}", ModelRowViewModel.Price(tier.Rates.Input), IsNumeric: true));
            rates.Add(new KeyValueItem($"Wyjście > {Fmt.Tokens(tier.AboveTokens)}", ModelRowViewModel.Price(tier.Rates.Output), IsNumeric: true));
        }

        if (pricing?.MaxInputTokens is { } maxInput)
        {
            rates.Add(new KeyValueItem("Maks. tokenów wejścia", Fmt.Number(maxInput), IsNumeric: true));
        }

        if (pricing?.FastMultiplier is { } fast)
        {
            rates.Add(new KeyValueItem("Mnożnik fast", Fmt.Multiplier(fast), IsNumeric: true));
        }

        if (pricing?.FlexMultiplier is { } flex)
        {
            rates.Add(new KeyValueItem("Mnożnik flex", Fmt.Multiplier(flex), IsNumeric: true));
        }

        rates.Add(new KeyValueItem("Źródło ceny", row.Provider));
        var overrideButton = new Button { Text = "Nadpisz cenę" }.Styled("Button.Secondary.Sm");
        overrideButton.Clicked += async (_, _) => { await new SettingsViewModel().OverrideModelAsync(row.Model); await RefreshAsync(); };
        var actions = new HorizontalStackLayout { Spacing = Ui.D("Space200"), Children = { overrideButton } };
        if (row.IsOverridden)
        {
            var remove = new Button { Text = "Usuń nadpisanie" }.Styled("Button.DangerGhost");
            remove.Clicked += async (_, _) => { await new SettingsViewModel().RemoveModelOverrideAsync(row.Model); await RefreshAsync(); };
            actions.Children.Add(remove);
        }

        var content = new VerticalStackLayout { Spacing = Ui.D("Space600") };
        content.Children.Add(new Card { Title = "Cennik", Subtitle = "USD za 1 M tokenów", Actions = actions, Body = new KeyValueList { ItemsSource = rates } });
        content.Children.Add(new Section
        {
            Title = "Użycie w czasie",
            Body = new LineChart
            {
                HeightRequest = Ui.D("SizeChartAux"),
                Data = new ChartData
                {
                    Categories = _days.Select(day => Fmt.DayTick(day.ToDateTime(TimeOnly.MinValue))).ToList(),
                    CategoryDetails = _days.Select(day => Fmt.DayDate(day.ToDateTime(TimeOnly.MinValue))).ToList(),
                    Series = [new ChartSeries { Id = "cost", Label = "Koszt", ColorKey = "ColorDataCost", Points = row.Daily }],
                    Description = $"Koszt dzienny modelu {row.Name}, {RangeText}."
                }
            }
        });
        content.Children.Add(new Section
        {
            Title = "Tokeny",
            Body = new TokenBreakdown { Input = row.Totals.InputTokens, Output = row.Totals.OutputTokens, CacheWrite = row.Totals.CacheCreationTokens, CacheRead = row.Totals.CacheReadTokens, Reasoning = row.Totals.ReasoningTokens }
        });
        var aliases = AppState.Config.ModelAliases.Where(pair => pair.Value.Equals(row.Model, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
        content.Children.Add(new Section
        {
            Title = "Szczegóły",
            Body = new KeyValueList
            {
                ItemsSource = new List<KeyValueItem>
                {
                    new("Model", row.Model, IsMono: true, CanCopy: true),
                    new("Źródła", row.Sources),
                    new("Aliasy wskazujące na model", aliases.Count == 0 ? "—" : string.Join(", ", aliases), IsMono: aliases.Count > 0)
                }
            }
        });
        DrawerService.Open(new DrawerRequest
        {
            Overline = "Model",
            Title = row.Name,
            Subtitle = $"{row.Cost} · {Fmt.TokensWithUnit(row.Totals.TotalTokens)} · {RangeText}",
            Content = content,
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(Rows[index - 1])) : null,
            NextCommand = index >= 0 && index < Rows.Count - 1 ? new Command(() => OpenDetails(Rows[index + 1])) : null,
            MoreEntries = [new MenuEntry("Kopiuj nazwę modelu", () => _ = Ui.CopyAsync(row.Model), Icons.Copy)],
            Closed = () => SelectedRow = null
        });
    }

    private IEnumerable<IEnumerable<string>> TableRows()
    {
        yield return ["Model", "Źródła", "Tokeny", "Udział cache", "Cena wejście", "Cena wyjście", "Koszt"];
        foreach (var row in Rows)
        {
            yield return [row.Model, row.Sources, Fmt.Number(row.Totals.TotalTokens), row.CacheShare, row.InputPrice, row.OutputPrice, Fmt.CostFull(row.Totals.CostUsd)];
        }
    }
}
