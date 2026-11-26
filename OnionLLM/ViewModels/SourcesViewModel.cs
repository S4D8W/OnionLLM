using OnionLLM.Controls;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Storage;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

public enum SourceStatus
{
    Active,
    Detected,
    Problems,
    NotDetected,
    Disabled
}

/// <summary>Row of the „Źródła” table (DESIGN.md §6.8).</summary>
public sealed class SourceRowViewModel : ObservableObject
{
    private readonly Action<SourceRowViewModel> _save;
    private string _configError = string.Empty;

    public SourceRowViewModel(SourceSummary summary, SourceSyncReport? sync, IReadOnlyList<string> roots, SourceSettings settings, Action<SourceRowViewModel> save)
    {
        Summary = summary;
        Sync = sync;
        Roots = roots;
        _save = save;
        Settings = new SourceSettingViewModel(summary.Id, summary.DisplayName, settings);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourceSettingViewModel.Enabled))
            {
                OnPropertyChanged(nameof(Enabled));
            }
        };
    }

    public SourceSummary Summary { get; }

    public SourceSyncReport? Sync { get; }

    public IReadOnlyList<string> Roots { get; }

    public SourceSettingViewModel Settings { get; }

    public string Id => Summary.Id;

    public string Name => Summary.DisplayName;

    public string Vendor => Summary.Vendor;

    public string Initial => Name[..1].ToUpperInvariant();

    public SourceStatus Status => !Summary.IsEnabled ? SourceStatus.Disabled
        : !Summary.IsInstalled ? SourceStatus.NotDetected
        : Sync is { Problems.Count: > 0 } ? SourceStatus.Problems
        : Summary.HasDataToday || Summary.Last7Days.TotalTokens > 0 ? SourceStatus.Active
        : SourceStatus.Detected;

    public string StateText => Status switch
    {
        SourceStatus.Active => "Aktywne",
        SourceStatus.Detected => "Wykryte",
        SourceStatus.Problems => "Błędy",
        SourceStatus.NotDetected => "Nie wykryto",
        _ => "Wyłączone"
    };

    public StatusKind StateKind => Status switch
    {
        SourceStatus.Active => StatusKind.Success,
        SourceStatus.Problems => StatusKind.Warning,
        _ => StatusKind.Neutral
    };

    public SourceState BadgeState => Status switch
    {
        SourceStatus.Disabled => SourceState.Disabled,
        SourceStatus.NotDetected => SourceState.NotDetected,
        _ => SourceState.Normal
    };

    public string Files => Sync is null ? "—" : Fmt.Number(Sync.Files);

    public string Skipped => Sync is null ? "—" : Fmt.Number(Sync.SkippedLines + Sync.Problems.Count);

    public bool HasSkipped => Sync is { } sync && sync.SkippedLines + sync.Problems.Count > 0;

    public string Last7 => $"{Fmt.Cost(Summary.Last7Days.CostUsd)} · {Fmt.TokensWithUnit(Summary.Last7Days.TotalTokens)}";

    public string Last30 => $"{Fmt.Cost(Summary.Last30Days.CostUsd)} · {Fmt.TokensWithUnit(Summary.Last30Days.TotalTokens)}";

    /// <summary>Toggle in the row — immediate change with autosave (§6.8).</summary>
    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled != value)
            {
                Settings.Enabled = value;
                OnPropertyChanged();
                _save(this);
            }
        }
    }

    public string ConfigError { get => _configError; set => Set(ref _configError, value); }

    public void Save() => _save(this);
}

/// <summary>Screen „Źródła” (DESIGN.md §6.8) — the only place where sources are configured.</summary>
public sealed class SourcesViewModel : ObservableObject
{
    private static readonly SourceStatus[] Order = [SourceStatus.Active, SourceStatus.Detected, SourceStatus.Problems, SourceStatus.NotDetected, SourceStatus.Disabled];
    private int _filterIndex;
    private string _search = string.Empty;
    private bool _isBusy;
    private bool _loaded;
    private List<SourceRowViewModel> _all = [];
    private IReadOnlyList<SourceRowViewModel> _rows = [];
    private SourceRowViewModel? _selected;

    public SourcesViewModel()
    {
        OpenDetailsCommand = new Command<SourceRowViewModel>(row => OpenDetails(row, 0));
        RedetectCommand = new Command(async () => await RefreshAsync(force: true));
        RebuildCommand = new Command(async () => await RebuildAsync());
        ResetFiltersCommand = new Command(() => { FilterIndex = 0; Search = string.Empty; OnPropertyChanged(nameof(Search)); });
    }

    public string[] Filters { get; } = ["Wszystkie", "Aktywne", "Wykryte", "Niewykryte", "Z problemami"];

    public Command<SourceRowViewModel> OpenDetailsCommand { get; }

    public Command RedetectCommand { get; }

    public Command RebuildCommand { get; }

    public Command ResetFiltersCommand { get; }

    public int FilterIndex { get => _filterIndex; set { if (Set(ref _filterIndex, value)) { Apply(); } } }

    public string Search { get => _search; set { if (Set(ref _search, value)) { Apply(); } } }

    public IReadOnlyList<SourceRowViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }

    public SourceRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    public string Subtitle { get; private set; } = string.Empty;

    public TableState TableState => !_loaded ? TableState.Loading : Rows.Count > 0 ? TableState.Ready : TableState.NoResults;

    public int SkippedFiles => _all.Sum(row => row.Sync?.Problems.Count ?? 0);

    public bool HasPartialData => SkippedFiles > 0;

    public string PartialMessage => $"Pominięto {Fmt.Count(SkippedFiles, "plik", "pliki", "plików")}, których nie udało się odczytać. Koszt może być zaniżony.";

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public async Task RefreshAsync(bool force = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await AppState.RefreshDataAsync(force);
            var engine = AppState.Engine;
            var snapshot = await Task.Run(() => engine.BuildDashboard(AppState.Limits));
            var config = AppState.Config;
            _all = snapshot.Sources.Select(summary =>
            {
                var source = engine.FindSource(summary.Id);
                var roots = source?.ResolveRoots(config).Where(Directory.Exists).ToList() ?? [];
                return new SourceRowViewModel(summary, engine.LastSync?.Sources.FirstOrDefault(sync => sync.Source == summary.Id), roots,
                    config.GetSource(summary.Id), SaveSource);
            }).ToList();
            _loaded = true;
            var active = _all.Count(row => row.Status == SourceStatus.Active);
            var detected = _all.Count(row => row.Status is SourceStatus.Detected or SourceStatus.Problems);
            var missing = _all.Count(row => row.Status == SourceStatus.NotDetected);
            Subtitle = $"{Fmt.Count(active, "aktywne", "aktywne", "aktywnych")} · {Fmt.Count(detected, "wykryte", "wykryte", "wykrytych")} · " +
                       $"{Fmt.Count(missing, "niewykryte", "niewykryte", "niewykrytych")}";
            Apply();
            if (force)
            {
                ToastService.Show($"Wykryto {Fmt.Count(active + detected, "źródło", "źródła", "źródeł")}", ToastKind.Info);
            }
        }
        finally
        {
            IsBusy = false;
            foreach (var name in new[] { nameof(Subtitle), nameof(TableState), nameof(SkippedFiles), nameof(HasPartialData), nameof(PartialMessage) })
            {
                OnPropertyChanged(name);
            }
        }
    }

    private void Apply()
    {
        IEnumerable<SourceRowViewModel> rows = _all;
        rows = FilterIndex switch
        {
            1 => rows.Where(row => row.Status == SourceStatus.Active),
            2 => rows.Where(row => row.Status is SourceStatus.Detected or SourceStatus.Problems),
            3 => rows.Where(row => row.Status == SourceStatus.NotDetected),
            4 => rows.Where(row => row.Status == SourceStatus.Problems || row.HasSkipped),
            _ => rows
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Select.Normalize(Search.Trim());
            rows = rows.Where(row => Select.Normalize(row.Name).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     Select.Normalize(row.Vendor).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     row.Id.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        Rows = rows.OrderBy(row => Array.IndexOf(Order, row.Status)).ThenBy(row => row.Name, StringComparer.CurrentCulture).ToList();
        OnPropertyChanged(nameof(TableState));
    }

    /// <summary>Autosave of a source (§5.10); pi store errors block the save and show under the field.</summary>
    private void SaveSource(SourceRowViewModel row)
    {
        try
        {
            var config = AppState.Config.Clone();
            if (!config.Sources.TryGetValue(row.Id, out var settings))
            {
                config.Sources[row.Id] = settings = new SourceSettings();
            }

            row.Settings.ApplyTo(settings, AppState.Engine.Sources.Select(source => source.Id).ToArray());
            var storeErrors = new PiSource().ValidateStores(config);
            if (storeErrors.Count > 0)
            {
                row.ConfigError = string.Join("; ", storeErrors);
                return;
            }

            AppState.SaveConfig(config);
            row.ConfigError = string.Empty;
            SemanticScreenReader.Announce("Zapisano");
        }
        catch (Exception exception) when (exception is FormatException or ConfigException or IOException or UnauthorizedAccessException)
        {
            row.ConfigError = exception.Message;
        }
    }

    /// <summary>Source drawer (§6.8): tabs Przegląd · Konfiguracja · Diagnostyka.</summary>
    /// <summary>Opens the drawer of one source on „Przegląd” (command palette).</summary>
    public async Task OpenEntityAsync(string sourceId)
    {
        await ScreenLoad.RefreshIdleAsync(() => RefreshAsync(), () => IsBusy);
        OpenDetails(_all.FirstOrDefault(row => row.Id == sourceId), 0);
    }

    public void OpenDetails(SourceRowViewModel? row, int tab)
    {
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        var tabs = new Tabs { ItemsSource = new[] { "Przegląd", "Konfiguracja", "Diagnostyka" }, SelectedIndex = tab };
        var body = new ContentView();
        void Show() => body.Content = tabs.SelectedIndex switch { 1 => Configuration(row), 2 => Diagnostics(row), _ => Overview(row) };
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Tabs.SelectedIndex))
            {
                Show();
            }
        };
        Show();
        var index = Rows.ToList().IndexOf(row);
        DrawerService.Open(new DrawerRequest
        {
            Overline = "Źródło",
            Title = row.Name,
            Subtitle = $"{row.Vendor} · {row.StateText}",
            Content = new VerticalStackLayout { Spacing = Ui.D("Space400"), Children = { tabs, body } },
            PreviousCommand = index > 0 ? new Command(() => OpenDetails(Rows[index - 1], tabs.SelectedIndex)) : null,
            NextCommand = index >= 0 && index < Rows.Count - 1 ? new Command(() => OpenDetails(Rows[index + 1], tabs.SelectedIndex)) : null,
            Closed = () => SelectedRow = null
        });
    }

    private static View Overview(SourceRowViewModel row)
    {
        var tiles = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
        tiles.Add(new StatTile { Title = "7 dni", Value = Fmt.Cost(row.Summary.Last7Days.CostUsd), Caption = Fmt.TokensWithUnit(row.Summary.Last7Days.TotalTokens) });
        tiles.Add(new StatTile { Title = "30 dni", Value = Fmt.Cost(row.Summary.Last30Days.CostUsd), Caption = Fmt.TokensWithUnit(row.Summary.Last30Days.TotalTokens) }, 1);
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space600"), Children = { tiles } };
        if (row.Summary.TopModels.Count > 0)
        {
            var models = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var model in row.Summary.TopModels)
            {
                models.Children.Add(new ModelTag { Model = model, Margin = new Thickness(0, 0, Ui.D("Space150"), Ui.D("Space150")) });
            }

            stack.Children.Add(new Section { Title = "Najczęstsze modele", Body = models });
        }

        if (row.Summary.Limits is { } limits)
        {
            var state = limits.IsAvailable ? LimitState.Live : LimitState.Unavailable;
            stack.Children.Add(new Section
            {
                Title = "Limity",
                Body = new VerticalStackLayout
                {
                    Spacing = Ui.D("Space400"),
                    Children =
                    {
                        new LimitGauge { Title = "Limit 5-godzinny", Percent = limits.IsAvailable ? limits.FiveHourUtilization * 100 : double.NaN, ResetText = SourceCardViewModel.ResetText(limits.FiveHourReset), State = state },
                        new LimitGauge { Title = "Limit 7-dniowy", Percent = limits.IsAvailable ? limits.SevenDayUtilization * 100 : double.NaN, ResetText = SourceCardViewModel.ResetText(limits.SevenDayReset), State = state }
                    }
                }
            });
        }

        return stack;
    }

    private static View Configuration(SourceRowViewModel row)
    {
        var settings = row.Settings;
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space400") };
        var enabled = new Switch { BindingContext = row };
        enabled.SetBinding(Switch.IsToggledProperty, nameof(SourceRowViewModel.Enabled), BindingMode.TwoWay);
        stack.Children.Add(new FormRow { Label = "Włączone", Field = enabled });

        var roots = new VerticalStackLayout { Spacing = Ui.D("Space100") };
        foreach (var root in row.Roots)
        {
            var line = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = Ui.D("Space200") };
            var label = Ui.Text("Text.Mono", root);
            label.LineBreakMode = LineBreakMode.MiddleTruncation;
            ToolTipProperties.SetText(label, root);
            var open = new Button { Text = "Otwórz folder" }.Styled("Button.Ghost.Sm");
            open.Clicked += async (_, _) => await Launcher.Default.OpenAsync(new Uri(root));
            line.Add(label);
            line.Add(open, 1);
            roots.Children.Add(line);
        }

        if (row.Roots.Count == 0)
        {
            roots.Children.Add(Ui.Text("Text.Caption", "Nie znaleziono folderów z danymi."));
        }

        stack.Children.Add(new FormRow { Label = "Ścieżki danych", Field = roots });
        stack.Children.Add(Text(settings, nameof(SourceSettingViewModel.Paths), "Własne ścieżki", "Puste = zmienne środowiskowe, potem domyślne foldery. Kilka ścieżek oddziel „;”.", row));
        if (settings.IsCodex)
        {
            var speed = new Select { ItemsSource = settings.Speeds, Placeholder = "Poziom szybkości do wyceny", BindingContext = settings };
            speed.SetBinding(Select.SelectedIndexProperty, nameof(SourceSettingViewModel.SpeedIndex), BindingMode.TwoWay);
            speed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Select.SelectedIndex)) { row.Save(); } };
            stack.Children.Add(new FormRow { Label = "Poziom szybkości do wyceny", Field = speed });
        }

        if (settings.IsOpenCode)
        {
            var include = new Switch { BindingContext = settings };
            include.SetBinding(Switch.IsToggledProperty, nameof(SourceSettingViewModel.IncludeClaudeAgent), BindingMode.TwoWay);
            include.Toggled += (_, _) => row.Save();
            stack.Children.Add(new FormRow { Label = "Licz tury claude-agent", Description = "Zwykle liczy je już Claude Code — włącz tylko bez tego źródła.", Field = include });
        }

        if (settings.IsPi)
        {
            stack.Children.Add(Text(settings, nameof(SourceSettingViewModel.AgentDirs), "Katalogi agentów", "Czytany jest katalog sessions każdego z nich; oddziel „;”.", row));
            stack.Children.Add(Text(settings, nameof(SourceSettingViewModel.Stores), "Magazyny sesji", "nazwa=ścieżka; nazwa2=ścieżka2", row));
        }

        var error = Ui.Text("Text.Caption").Dyn(Label.TextColorProperty, "ColorStatusDanger");
        error.BindingContext = row;
        error.SetBinding(Label.TextProperty, nameof(SourceRowViewModel.ConfigError));
        stack.Children.Add(error);
        stack.Children.Add(Ui.Text("Text.Caption", "Zmiany zapisują się automatycznie."));
        return stack;
    }

    private static View Text(SourceSettingViewModel settings, string property, string label, string description, SourceRowViewModel row)
    {
        var field = new TextField { Kind = TextFieldKind.Mono, BindingContext = settings };
        field.SetBinding(TextField.TextProperty, property, BindingMode.TwoWay);
        CancellationTokenSource? debounce = null;
        field.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(TextField.Text))
            {
                return;
            }

            debounce?.Cancel();
            var token = (debounce = new CancellationTokenSource()).Token;
            try
            {
                await Task.Delay(Design.MotionTokens.AutosaveDelay, token);
                row.Save();
            }
            catch (TaskCanceledException)
            {
            }
        };
        return new FormRow { Label = label, Description = description, Field = field };
    }

    private static View Diagnostics(SourceRowViewModel row)
    {
        var sync = row.Sync;
        var items = new List<KeyValueItem>
        {
            new("Pliki", sync is null ? "—" : Fmt.Number(sync.Files), IsNumeric: true),
            new("Przetworzone pliki", sync is null ? "—" : Fmt.Number(sync.ParsedFiles), IsNumeric: true),
            new("Pominięte linie", sync is null ? "—" : Fmt.Number(sync.SkippedLines), IsNumeric: true),
            new("Ostatnia synchronizacja", AppState.Engine.LastSync is { } last ? Fmt.DateTimeShort(last.FinishedAt) : "—")
        };
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space400"), Children = { new KeyValueList { ItemsSource = items } } };
        if (sync is { SkipReasons.Count: > 0 })
        {
            stack.Children.Add(new Section
            {
                Title = "Powody pominięcia",
                Body = new KeyValueList { ItemsSource = sync.SkipReasons.Select(pair => new KeyValueItem(pair.Key, Fmt.Number(pair.Value), IsNumeric: true)).ToList() }
            });
        }

        if (sync is { Problems.Count: > 0 })
        {
            var text = string.Join(Environment.NewLine, sync.Problems.Select(problem => $"{problem.Path}: {problem.Error}"));
            var copy = new Button { Text = "Kopiuj" }.Styled("Button.Ghost.Sm");
            copy.Clicked += async (_, _) => await Ui.CopyAsync(text);
            stack.Children.Add(new Section
            {
                Title = "Błędy odczytu",
                Actions = copy,
                Body = Ui.Box(Ui.Text("Text.MonoSm", text), "ColorSurface2", "RadiusMd", "LayoutCardPadding")
            });
        }

        return stack;
    }

    private async Task RebuildAsync()
    {
        var confirmed = await DialogService.ConfirmAsync(
            "Przebudować bazę?",
            "Baza zostanie odtworzona z logów na dysku. Wpisy z logów, które narzędzia już usunęły, zostaną utracone. Zwykle trwa to 10–60 s.",
            "Przebuduj",
            danger: true);
        if (!confirmed)
        {
            return;
        }

        await Task.Run(() => AppState.Engine.Ledger.Reset());
        await AppState.Engine.SyncAsync(force: true);
        var (_, entries) = AppState.Engine.Ledger.Statistics();
        ToastService.Show($"Baza przebudowana: {Fmt.Count(entries, "wpis", "wpisy", "wpisów")}");
        AppState.NotifyChanged();
    }
}
