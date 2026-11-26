using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using OnionLLM.Controls;
using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Util;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.ViewModels;

/// <summary>Source configuration (edited in the „Źródła” drawer, DESIGN.md §6.8).</summary>
public sealed class SourceSettingViewModel : ObservableObject
{
    /// <summary>Codex: poziom szybkości do wyceny (indeks 0 = auto → <c>null</c> w configu).</summary>
    public static readonly string[] SpeedValues = ["auto", "standard", "flex", "fast"];
    private static readonly System.Text.RegularExpressions.Regex StoreName = new("^[a-z][a-z0-9_-]{0,31}$");
    private bool _enabled;
    private string _paths;
    private int _speedIndex;
    private bool _includeClaudeAgent;
    private string _agentDirs;
    private string _stores;

    public SourceSettingViewModel(string id, string name, SourceSettings settings)
    {
        Id = id;
        Name = name;
        _enabled = settings.Enabled;
        _paths = string.Join("; ", settings.Paths);
        _speedIndex = Math.Max(0, Array.FindIndex(SpeedValues, value => value.Equals(settings.Speed ?? "auto", StringComparison.OrdinalIgnoreCase)));
        _includeClaudeAgent = settings.IncludeClaudeAgent;
        _agentDirs = string.Join("; ", settings.AgentDirs);
        _stores = string.Join("; ", settings.Stores.Select(store => $"{store.Name}={store.Path}"));
    }

    public string Id { get; }

    public string Name { get; }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public string Paths { get => _paths; set => Set(ref _paths, value); }

    public bool IsCodex => Id == "codex";

    public bool IsOpenCode => Id == "opencode";

    public bool IsPi => Id == "pi";

    public string[] Speeds { get; } = ["auto — z logów, potem config.toml", "standard", "flex", "fast (priority)"];

    public int SpeedIndex { get => _speedIndex; set => Set(ref _speedIndex, value); }

    public bool IncludeClaudeAgent { get => _includeClaudeAgent; set => Set(ref _includeClaudeAgent, value); }

    public string AgentDirs { get => _agentDirs; set => Set(ref _agentDirs, value); }

    public string Stores { get => _stores; set => Set(ref _stores, value); }

    /// <summary>Zapisuje pola widoku do ustawień źródła; pozostałe pola zostają bez zmian.</summary>
    public void ApplyTo(SourceSettings settings, IReadOnlyCollection<string> sourceIds)
    {
        settings.Enabled = Enabled;
        settings.Paths = FileUtil.SplitPaths(Paths).ToList();
        if (IsCodex)
        {
            settings.Speed = SpeedIndex <= 0 || SpeedIndex >= SpeedValues.Length ? null : SpeedValues[SpeedIndex];
        }

        if (IsOpenCode)
        {
            settings.IncludeClaudeAgent = IncludeClaudeAgent;
        }

        if (IsPi)
        {
            settings.AgentDirs = FileUtil.SplitPaths(AgentDirs).ToList();
            settings.Stores = ParseStores(Stores, sourceIds);
        }
    }

    /// <summary>
    /// Magazyny pi w formacie <c>nazwa=ścieżka; nazwa2=ścieżka2</c> (ścieżka może być listą po przecinku).
    /// Nazwa jak w ccusage <c>pi.stores</c>: <c>^[a-z][a-z0-9_-]{0,31}$</c>, unikalna, różna od identyfikatorów źródeł.
    /// </summary>
    public static List<SessionStore> ParseStores(string text, IReadOnlyCollection<string> sourceIds)
    {
        var result = new List<SessionStore>();
        foreach (var item in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator == item.Length - 1)
            {
                throw new FormatException($"pi — magazyn '{item}': oczekiwano nazwa=ścieżka.");
            }

            var name = item[..separator].Trim();
            var path = item[(separator + 1)..].Trim();
            if (!StoreName.IsMatch(name))
            {
                throw new FormatException($"pi — niepoprawna nazwa magazynu '{name}' (małe litery, cyfry, - i _, do 32 znaków, zaczyna się literą).");
            }

            if (sourceIds.Contains(name, StringComparer.OrdinalIgnoreCase) || result.Any(store => store.Name == name))
            {
                throw new FormatException($"pi — nazwa magazynu '{name}' jest zajęta (powtórzona lub równa identyfikatorowi źródła).");
            }

            result.Add(new SessionStore { Name = name, Path = path });
        }

        return result;
    }
}

/// <summary>Row of the pricing overrides table; parsing lives in <see cref="PricingOverrideForm"/> (Core).</summary>
public sealed class PricingOverrideViewModel : ObservableObject
{
    private bool _showAdvanced;

    public PricingOverrideViewModel(PricingOverrideForm form)
    {
        Form = form;
        _showAdvanced = form.HasAdvanced;
    }

    public PricingOverrideForm Form { get; }

    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }

    public string Key { get => Form.Key; set => Update(Form.Key, value, v => Form.Key = v); }

    public string Input { get => Form.Input; set => Update(Form.Input, value, v => Form.Input = v); }

    public string Output { get => Form.Output; set => Update(Form.Output, value, v => Form.Output = v); }

    public string CacheWrite { get => Form.CacheWrite; set => Update(Form.CacheWrite, value, v => Form.CacheWrite = v); }

    public string CacheWrite1h { get => Form.CacheWrite1h; set => Update(Form.CacheWrite1h, value, v => Form.CacheWrite1h = v); }

    public string CacheRead { get => Form.CacheRead; set => Update(Form.CacheRead, value, v => Form.CacheRead = v); }

    public string InputAbove200k { get => Form.InputAbove200k; set => Update(Form.InputAbove200k, value, v => Form.InputAbove200k = v); }

    public string OutputAbove200k { get => Form.OutputAbove200k; set => Update(Form.OutputAbove200k, value, v => Form.OutputAbove200k = v); }

    public string CacheWriteAbove200k { get => Form.CacheWriteAbove200k; set => Update(Form.CacheWriteAbove200k, value, v => Form.CacheWriteAbove200k = v); }

    public string CacheReadAbove200k { get => Form.CacheReadAbove200k; set => Update(Form.CacheReadAbove200k, value, v => Form.CacheReadAbove200k = v); }

    public string MaxInputTokens { get => Form.MaxInputTokens; set => Update(Form.MaxInputTokens, value, v => Form.MaxInputTokens = v); }

    public string FastMultiplier { get => Form.FastMultiplier; set => Update(Form.FastMultiplier, value, v => Form.FastMultiplier = v); }

    public string FlexMultiplier { get => Form.FlexMultiplier; set => Update(Form.FlexMultiplier, value, v => Form.FlexMultiplier = v); }

    public PricingOverrideViewModel Copy() => new(new PricingOverrideForm
    {
        Key = Key, Input = Input, Output = Output, CacheWrite = CacheWrite, CacheWrite1h = CacheWrite1h, CacheRead = CacheRead,
        InputAbove200k = InputAbove200k, OutputAbove200k = OutputAbove200k, CacheWriteAbove200k = CacheWriteAbove200k,
        CacheReadAbove200k = CacheReadAbove200k, MaxInputTokens = MaxInputTokens, FastMultiplier = FastMultiplier, FlexMultiplier = FlexMultiplier
    });

    private void Update(string current, string? value, Action<string> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (current != (value ?? ""))
        {
            assign(value ?? "");
            OnPropertyChanged(propertyName);
        }
    }
}

/// <summary>Row of „Rozbieżności kosztu” (§6.9.9).</summary>
public sealed record MismatchRow(string Source, string Model, string Compared, string Mismatches, string Stored, string Calculated, string Difference);

/// <summary>Row of „Diagnostyka” per source (§6.9.10).</summary>
public sealed record DiagnosticsRow(string Source, string Files, string Parsed, string Skipped, string Errors);

/// <summary>Screen „Ustawienia” (DESIGN.md §6.9) with autosave (§5.10).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly Dictionary<string, string> _errors = [];
    private CancellationTokenSource? _debounce;
    private bool _loading;
    private int _timezoneIndex;
    private int _startOfWeekIndex;
    private bool _runAtStartup;
    private int _themeIndex;
    private int _densityIndex;
    private int _motionIndex;
    private bool _alwaysOnTop;
    private int _costModeIndex;
    private string _sessionLength = "5";
    private int _tokenLimitModeIndex;
    private string _tokenLimitValue = "";
    private string _refreshInterval = "30";
    private string _activeMinutes = "15";
    private string _projectAliases = "";
    private bool _offline;
    private string _modelAliases = "";
    private string _modelLabelAliases = "";
    private int _mismatchRangeIndex = 1;
    private string _lastSaved = "";
    private string _startupStatus = "";
    private bool _isBusy;

    public SettingsViewModel()
    {
        Timezones = [new ChoiceItem("", $"Systemowa ({TimeZoneInfo.Local.Id})"), .. TimeZoneInfo.GetSystemTimeZones().Select(zone => new ChoiceItem(zone.Id, zone.DisplayName))];
        AddOverrideCommand = new Command(async () => await EditOverrideAsync(null));
        EditOverrideCommand = new Command<PricingOverrideViewModel>(async item => await EditOverrideAsync(item));
        RemoveOverrideCommand = new Command<PricingOverrideViewModel>(RemoveOverride);
        RefreshPricingCommand = new Command(async () => await RefreshPricingAsync());
        RebuildCommand = new Command(async () => await RebuildAsync());
        CheckMismatchesCommand = new Command(async () => await CheckMismatchesAsync());
        OpenDataFolderCommand = new Command(async () => await WindowActions.OpenDataFolderAsync());
        // Theme / density / always-on-top can change from shortcuts and the palette: keep the form in sync so the next
        // autosave does not write the old value back.
        AppState.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(SyncWindowFields);
        OpenConfigFolderCommand = new Command(async () => await Launcher.Default.OpenAsync(new Uri(Path.GetDirectoryName(ConfigPath)!)));
        OpenConfigFileCommand = new Command(async () =>
        {
            if (File.Exists(ConfigPath))
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest("Plik konfiguracji", new ReadOnlyFile(ConfigPath)));
            }
        });
        CopyDiagnosticsCommand = new Command(async () => await Ui.CopyAsync(DiagnosticsText()));
        OpenSourcesCommand = new Command(() => _ = ShellViewModel.Instance.NavigateAsync(Screen.Sources));
    }

    // ===== Options (labels §6.9) =====

    public ChoiceItem[] Timezones { get; }

    public string[] Days { get; } = ["poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota", "niedziela"];

    public string[] Themes { get; } = ["Systemowy", "Jasny", "Ciemny"];

    public string[] Densities { get; } = ["Komfortowa", "Kompaktowa"];

    public string[] Motions { get; } = ["Pełne", "Ograniczone", "Wyłączone"];

    public string[] CostModes { get; } = ["Automatyczny", "Wyliczany", "Zapisany"];

    /// <summary>§6.9.5: „Bez limitu” (<c>none</c>) · „Maksimum z historii” (<c>max</c>) · „Własny” (liczba).</summary>
    public string[] TokenLimitModes { get; } = ["Bez limitu", "Maksimum z historii", "Własny"];

    public string[] MismatchRanges { get; } = ["7 dni", "30 dni", "90 dni", "Cała historia"];

    public Command AddOverrideCommand { get; }

    public Command<PricingOverrideViewModel> EditOverrideCommand { get; }

    public Command<PricingOverrideViewModel> RemoveOverrideCommand { get; }

    public Command RefreshPricingCommand { get; }

    public Command RebuildCommand { get; }

    public Command CheckMismatchesCommand { get; }

    public Command OpenDataFolderCommand { get; }

    public Command OpenConfigFolderCommand { get; }

    public Command OpenConfigFileCommand { get; }

    public Command CopyDiagnosticsCommand { get; }

    public Command OpenSourcesCommand { get; }

    /// <summary>Field key saved last — FormRow shows „Zapisano” next to it (§5.10).</summary>
    public string LastSaved { get => _lastSaved; private set => Set(ref _lastSaved, value); }

    public string SaveError { get; private set; } = string.Empty;

    public bool HasSaveError => SaveError.Length > 0;

    public string ConfigPath => ConfigLoader.UserConfigPath;

    public string AutosaveCaption => "Zmiany zapisują się automatycznie · Plik: " + ConfigPath;

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    // ===== Ogólne =====

    public int TimezoneIndex { get => _timezoneIndex; set => Field(ref _timezoneIndex, value, "timezone", immediate: true); }

    public int StartOfWeekIndex { get => _startOfWeekIndex; set => Field(ref _startOfWeekIndex, value, "startOfWeek", immediate: true); }

#if WINDOWS
    public bool IsStartupSettingAvailable => true;
#else
    public bool IsStartupSettingAvailable => false;
#endif

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (Set(ref _runAtStartup, value) && !_loading)
            {
                _ = ApplyStartupAsync();
            }
        }
    }

    public string StartupStatus { get => _startupStatus; private set => Set(ref _startupStatus, value); }

    // ===== Wygląd / Okno =====

    public int ThemeIndex { get => _themeIndex; set => Field(ref _themeIndex, value, "theme", immediate: true); }

    public int DensityIndex { get => _densityIndex; set => Field(ref _densityIndex, value, "density", immediate: true); }

    public int MotionIndex { get => _motionIndex; set => Field(ref _motionIndex, value, "motion", immediate: true); }

    public bool AlwaysOnTop { get => _alwaysOnTop; set => Field(ref _alwaysOnTop, value, "alwaysOnTop", immediate: true); }

    // ===== Obliczenia (§6.9.5) =====

    public int CostModeIndex
    {
        get => _costModeIndex;
        set
        {
            Field(ref _costModeIndex, value, "costMode", immediate: true);
            OnPropertyChanged(nameof(CostModeDescription));
        }
    }

    public string CostModeDescription => CostModeIndex switch
    {
        1 => "Zawsze z cennika i liczby tokenów.",
        2 => "Tylko koszt zapisany przez narzędzie; bez niego koszt wynosi 0.",
        _ => "Koszt zapisany przez narzędzie, gdy jest; w przeciwnym razie wyliczony z cennika."
    };

    public string SessionLength { get => _sessionLength; set => Field(ref _sessionLength, value, "sessionLength"); }

    public int TokenLimitModeIndex
    {
        get => _tokenLimitModeIndex;
        set
        {
            Field(ref _tokenLimitModeIndex, value, "tokenLimit", immediate: value != 2);
            OnPropertyChanged(nameof(IsCustomTokenLimit));
        }
    }

    public bool IsCustomTokenLimit => TokenLimitModeIndex == 2;

    public string TokenLimitValue { get => _tokenLimitValue; set => Field(ref _tokenLimitValue, value, "tokenLimit"); }

    public string RefreshInterval { get => _refreshInterval; set => Field(ref _refreshInterval, value, "refreshInterval"); }

    public string ActiveMinutes { get => _activeMinutes; set => Field(ref _activeMinutes, value, "activeMinutes"); }

    public string SessionLengthError => Error("sessionLength");

    public string TokenLimitError => Error("tokenLimit");

    public string RefreshIntervalError => Error("refreshInterval");

    public string ActiveMinutesError => Error("activeMinutes");

    // ===== Projekty / Cennik / Statusline =====

    public string ProjectAliases { get => _projectAliases; set => Field(ref _projectAliases, value, "projectAliases"); }

    public bool Offline { get => _offline; set => Field(ref _offline, value, "offline", immediate: true); }

    public string ModelAliases { get => _modelAliases; set => Field(ref _modelAliases, value, "modelAliases"); }

    public string ModelAliasesError => Error("modelAliases");

    public string ModelLabelAliases { get => _modelLabelAliases; set => Field(ref _modelLabelAliases, value, "modelLabelAliases"); }

    public string ModelLabelAliasesError => Error("modelLabelAliases");

    public ObservableCollection<PricingOverrideViewModel> PricingOverrides { get; } = [];

    public TableState OverridesState => PricingOverrides.Count == 0 ? TableState.Empty : TableState.Ready;

    public string LiteLlmStatus { get; private set; } = string.Empty;

    public string ModelsDevStatus { get; private set; } = string.Empty;

    public StatusKind LiteLlmKind { get; private set; }

    public StatusKind ModelsDevKind { get; private set; }

    public string LiteLlmBadge => LiteLlmKind == StatusKind.Success ? "aktualny" : LiteLlmKind == StatusKind.Warning ? "nieaktualny" : "offline";

    public string ModelsDevBadge => ModelsDevKind == StatusKind.Success ? "aktualny" : ModelsDevKind == StatusKind.Warning ? "nieaktualny" : "offline";

    // ===== Zaawansowane / Diagnostyka / O aplikacji =====

    public int MismatchRangeIndex { get => _mismatchRangeIndex; set => Set(ref _mismatchRangeIndex, value); }

    public IReadOnlyList<MismatchRow> Mismatches { get; private set; } = [];

    public string MismatchSummary { get; private set; } = string.Empty;

    public TableState MismatchState { get; private set; } = TableState.Empty;

    public IReadOnlyList<KeyValueItem> DiagnosticsItems { get; private set; } = [];

    public IReadOnlyList<DiagnosticsRow> DiagnosticsRows { get; private set; } = [];

    public string Version => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var config = AppState.Config;
            _timezoneIndex = Math.Max(0, Array.FindIndex(Timezones, zone => zone.Id.Length > 0 && zone.Id.Equals(config.Timezone, StringComparison.OrdinalIgnoreCase)));
            _startOfWeekIndex = Math.Max(0, Array.IndexOf(WeekDays, config.StartOfWeek));
            _themeIndex = (int)config.Appearance.Theme;
            _densityIndex = (int)config.Appearance.Density;
            _motionIndex = (int)config.Appearance.Motion;
            _alwaysOnTop = config.Window.AlwaysOnTop;
            _costModeIndex = (int)config.CostMode;
            _sessionLength = config.SessionLengthHours.ToString(CultureInfo.InvariantCulture);
            _tokenLimitModeIndex = config.IsTokenLimitDisabled ? 0
                : config.TokenLimit.Equals("max", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(config.TokenLimit) ? 1
                : 2;
            _tokenLimitValue = _tokenLimitModeIndex == 2 ? config.TokenLimit : "";
            _refreshInterval = config.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            _activeMinutes = config.ActiveSessionMinutes.ToString(CultureInfo.InvariantCulture);
            _projectAliases = config.ProjectAliases ?? "";
            _offline = config.Offline;
            _modelAliases = ConfigTextFormats.FormatMap(config.ModelAliases);
            _modelLabelAliases = ConfigTextFormats.FormatMap(config.ModelLabelAliases);
            PricingOverrides.Clear();
            foreach (var form in ConfigTextFormats.ToForms(config.PricingOverrides))
            {
                PricingOverrides.Add(new PricingOverrideViewModel(form));
            }

            _errors.Clear();
            SaveError = AppState.ConfigError is { } error ? "Plik konfiguracji jest niepoprawny — użyto wartości domyślnych: " + error : "";
            UpdateDiagnostics();
#if WINDOWS
            try
            {
                _runAtStartup = await WindowsStartupManager.IsEnabledAsync();
                StartupStatus = "";
            }
            catch (Exception exception) when (IsStartupError(exception))
            {
                StartupStatus = "Nie można odczytać ustawienia autostartu: " + exception.Message;
            }
#else
            await Task.CompletedTask;
#endif
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>Field change → validate → save immediately (toggles, selects) or after 600 ms (text, §5.10).</summary>
    private void SyncWindowFields()
    {
        var config = AppState.Config;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            ThemeIndex = (int)config.Appearance.Theme;
            DensityIndex = (int)config.Appearance.Density;
            MotionIndex = (int)config.Appearance.Motion;
            AlwaysOnTop = config.Window.AlwaysOnTop;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void Field<T>(ref T field, T value, string key, bool immediate = false, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName) || _loading)
        {
            return;
        }

        _debounce?.Cancel();
        var debounce = _debounce = new CancellationTokenSource();
        _ = SaveLaterAsync(key, immediate, debounce.Token);
    }

    private async Task SaveLaterAsync(string key, bool immediate, CancellationToken token)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(Design.MotionTokens.AutosaveDelay, token);
            }

            Save(key);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void Save(string key)
    {
        _errors.Clear();
        var config = AppState.Config.Clone();
        config.Timezone = TimezoneIndex > 0 && TimezoneIndex < Timezones.Length ? Timezones[TimezoneIndex].Id : null;
        config.StartOfWeek = WeekDays[Math.Clamp(StartOfWeekIndex, 0, WeekDays.Length - 1)];
        config.Appearance.Theme = (ThemePreference)ThemeIndex;
        config.Appearance.Density = (DensityPreference)DensityIndex;
        config.Appearance.Motion = (MotionPreference)MotionIndex;
        config.Window.AlwaysOnTop = AlwaysOnTop;
        config.CostMode = (CostMode)Math.Clamp(CostModeIndex, 0, 2);
        config.Offline = Offline;
        config.ProjectAliases = string.IsNullOrWhiteSpace(ProjectAliases) ? null : ProjectAliases.Trim();

        if (Number(SessionLength, 1, 24, "sessionLength") is { } hours)
        {
            config.SessionLengthHours = hours;
        }

        if (TokenLimitModeIndex == 0)
        {
            config.TokenLimit = "none";
        }
        else if (TokenLimitModeIndex == 1)
        {
            config.TokenLimit = "max";
        }
        else if (Number(TokenLimitValue, 1, long.MaxValue, "tokenLimit", integer: true) is { } limit)
        {
            config.TokenLimit = ((long)limit).ToString(CultureInfo.InvariantCulture);
        }

        if (Number(RefreshInterval, 5, 3600, "refreshInterval", integer: true) is { } seconds)
        {
            config.RefreshIntervalSeconds = (int)seconds;
        }

        if (Number(ActiveMinutes, 1, 240, "activeMinutes", integer: true) is { } minutes)
        {
            config.ActiveSessionMinutes = (int)minutes;
        }

        try
        {
            config.ModelAliases = ConfigTextFormats.ParseMap(ModelAliases, "Aliasy modeli");
        }
        catch (Exception exception) when (exception is FormatException or ConfigException)
        {
            _errors["modelAliases"] = exception.Message;
        }

        try
        {
            config.ModelLabelAliases = ConfigTextFormats.ParseMap(ModelLabelAliases, "Etykiety modeli");
        }
        catch (Exception exception) when (exception is FormatException or ConfigException)
        {
            _errors["modelLabelAliases"] = exception.Message;
        }

        // Invalid values are not saved; the previous valid value stays in force (§4.2.0 „Walidacja”).
        try
        {
            AppState.SaveConfig(config);
            SaveError = string.Empty;
            if (!_errors.ContainsKey(key))
            {
                LastSaved = string.Empty;
                LastSaved = key;
            }
        }
        catch (Exception exception) when (exception is ConfigException or IOException or UnauthorizedAccessException)
        {
            SaveError = "Nie zapisano: " + exception.Message;
        }

        foreach (var name in new[]
                 {
                     nameof(SessionLengthError), nameof(TokenLimitError), nameof(RefreshIntervalError), nameof(ActiveMinutesError),
                     nameof(ModelAliasesError), nameof(ModelLabelAliasesError), nameof(SaveError), nameof(HasSaveError)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private double? Number(string text, double min, double max, string key, bool integer = false)
    {
        var valid = double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                    value >= min && value <= max && (!integer || value % 1 == 0);
        if (valid)
        {
            return value;
        }

        _errors[key] = max >= long.MaxValue
            ? "Podaj liczbę większą od zera."
            : $"Podaj liczbę od {min.ToString(CultureInfo.CurrentCulture)} do {max.ToString(CultureInfo.CurrentCulture)}.";
        return null;
    }

    private string Error(string key) => _errors.TryGetValue(key, out var error) ? error : string.Empty;

    private async Task ApplyStartupAsync()
    {
#if WINDOWS
        try
        {
            var result = await WindowsStartupManager.SetEnabledAsync(RunAtStartup);
            _runAtStartup = result.Enabled;
            StartupStatus = result.Status;
            OnPropertyChanged(nameof(RunAtStartup));
            LastSaved = string.Empty;
            LastSaved = "startup";
        }
        catch (Exception exception) when (IsStartupError(exception))
        {
            StartupStatus = "Nie udało się zmienić ustawienia autostartu: " + exception.Message;
        }
#else
        await Task.CompletedTask;
#endif
    }

#if WINDOWS
    private static bool IsStartupError(Exception exception) =>
        exception is InvalidOperationException or UnauthorizedAccessException or IOException or
            System.Security.SecurityException or System.Runtime.InteropServices.COMException or NotSupportedException;
#endif

    // ===== Pricing overrides (§6.9.7) =====

    /// <summary>„Nadpisz cenę” from the model drawer (§6.7): edit the existing override of the model or start a new one.</summary>
    public async Task OverrideModelAsync(string model)
    {
        await LoadAsync();
        var existing = PricingOverrides.FirstOrDefault(item => item.Key.Equals(model, StringComparison.OrdinalIgnoreCase));
        await EditOverrideAsync(existing, model);
    }

    /// <summary>„Usuń nadpisanie” from the model drawer.</summary>
    public async Task RemoveModelOverrideAsync(string model)
    {
        await LoadAsync();
        RemoveOverride(PricingOverrides.FirstOrDefault(item => item.Key.Equals(model, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task EditOverrideAsync(PricingOverrideViewModel? existing, string? key = null)
    {
        var draft = existing?.Copy() ?? new PricingOverrideViewModel(new PricingOverrideForm { Key = key ?? string.Empty });
        var error = Ui.Text("Text.Caption").Dyn(Label.TextColorProperty, "ColorStatusDanger");
        View Field(string label, string property, bool mono = false, string suffix = "$ / 1 M")
        {
            var field = new TextField { Kind = mono ? TextFieldKind.Mono : TextFieldKind.Number, Suffix = mono ? "" : suffix, BindingContext = draft };
            field.SetBinding(TextField.TextProperty, property, BindingMode.TwoWay);
            return new FormRow { Label = label, Field = field };
        }

        Grid Pair(View left, View right)
        {
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = Ui.D("Space300") };
            grid.Add(left);
            grid.Add(right, 1);
            return grid;
        }

        var advanced = new VerticalStackLayout
        {
            Spacing = Ui.D("Space300"),
            Children =
            {
                Pair(Field("Wejście > 200 k", nameof(PricingOverrideViewModel.InputAbove200k)), Field("Wyjście > 200 k", nameof(PricingOverrideViewModel.OutputAbove200k))),
                Pair(Field("Zapis cache > 200 k", nameof(PricingOverrideViewModel.CacheWriteAbove200k)), Field("Odczyt cache > 200 k", nameof(PricingOverrideViewModel.CacheReadAbove200k))),
                Pair(Field("Maks. tokenów wejścia", nameof(PricingOverrideViewModel.MaxInputTokens), suffix: ""), Field("Mnożnik fast", nameof(PricingOverrideViewModel.FastMultiplier), suffix: "×")),
                Field("Mnożnik flex", nameof(PricingOverrideViewModel.FlexMultiplier), suffix: "×")
            }
        };
        var body = new VerticalStackLayout
        {
            Spacing = Ui.D("Space300"),
            Children =
            {
                Field("Model", nameof(PricingOverrideViewModel.Key), mono: true),
                Ui.Text("Text.Caption", "Klucz: model, dostawca/model lub [źródło] model. Puste pole = stawka z cennika."),
                Pair(Field("Wejście", nameof(PricingOverrideViewModel.Input)), Field("Wyjście", nameof(PricingOverrideViewModel.Output))),
                Pair(Field("Zapis cache 5 min", nameof(PricingOverrideViewModel.CacheWrite)), Field("Zapis cache 1 h", nameof(PricingOverrideViewModel.CacheWrite1h))),
                Field("Odczyt cache", nameof(PricingOverrideViewModel.CacheRead)),
                new Section { Title = "Zaawansowane", IsCollapsible = true, IsCollapsed = !draft.ShowAdvanced, Body = advanced },
                error
            }
        };

        bool Validate()
        {
            var forms = PricingOverrides.Where(item => item != existing).Select(item => item.Form).Append(draft.Form).ToList();
            try
            {
                ConfigTextFormats.BuildPricingOverrides(forms, AppState.Engine.Sources.Select(source => source.Id).ToArray());
                return !string.IsNullOrWhiteSpace(draft.Key) || SetError("Podaj nazwę modelu.");
            }
            catch (Exception exception) when (exception is FormatException or ConfigException)
            {
                return SetError(exception.Message);
            }

            bool SetError(string text)
            {
                error.Text = text;
                return false;
            }
        }

        if (!await DialogService.FormAsync(existing is null ? "Dodaj nadpisanie" : "Edytuj nadpisanie", body, existing is null ? "Dodaj" : "Zapisz", Validate))
        {
            return;
        }

        if (existing is null)
        {
            PricingOverrides.Add(draft);
        }
        else
        {
            PricingOverrides[PricingOverrides.IndexOf(existing)] = draft;
        }

        SaveOverrides("Zapisano nadpisanie ceny");
    }

    private void RemoveOverride(PricingOverrideViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var index = PricingOverrides.IndexOf(item);
        PricingOverrides.Remove(item);
        SaveOverrides(null);
        // Reversible action: no confirmation, a toast with „Cofnij” (§1.5).
        ToastService.Show($"Usunięto nadpisanie {item.Key}", ToastKind.Info, "Cofnij", () =>
        {
            PricingOverrides.Insert(Math.Min(index, PricingOverrides.Count), item);
            SaveOverrides(null);
        });
    }

    private void SaveOverrides(string? toast)
    {
        try
        {
            var config = AppState.Config.Clone();
            config.PricingOverrides = ConfigTextFormats.BuildPricingOverrides(PricingOverrides.Select(item => item.Form), AppState.Engine.Sources.Select(source => source.Id).ToArray());
            AppState.SaveConfig(config);
            if (toast is not null)
            {
                ToastService.Show(toast);
            }

            _ = AppState.Engine.LoadPricingAsync();
        }
        catch (Exception exception) when (exception is FormatException or ConfigException or IOException)
        {
            ToastService.Show("Nie zapisano nadpisań: " + exception.Message, ToastKind.Error);
        }

        OnPropertyChanged(nameof(OverridesState));
    }

    private async Task RefreshPricingAsync()
    {
        IsBusy = true;
        try
        {
            var catalog = await AppState.Engine.LoadPricingAsync(forceRefresh: true);
            if (catalog.Origin == PricingOrigin.Online)
            {
                ToastService.Show($"Cennik zaktualizowany: {Fmt.Count(catalog.Count, "model", "modele", "modeli")}");
            }
            else
            {
                ToastService.Show(AppState.Config.Offline
                    ? "Tryb offline — cennik nie jest pobierany z sieci."
                    : "Brak połączenia — limity i cennik zostaną odświeżone automatycznie.", ToastKind.Info);
            }

            UpdateDiagnostics();
            AppState.NotifyChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebuildAsync()
    {
        // §8.4: irreversible action → confirmation dialog stating the consequence.
        var confirmed = await DialogService.ConfirmAsync(
            "Przebudować bazę?",
            "Baza zostanie odtworzona z logów na dysku. Wpisy z logów, które narzędzia już usunęły, zostaną utracone. Zwykle trwa to 10–60 s.",
            "Przebuduj",
            danger: true);
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() => AppState.Engine.Ledger.Reset());
            await AppState.Engine.SyncAsync(force: true);
            var (_, entries) = AppState.Engine.Ledger.Statistics();
            ToastService.Show($"Baza przebudowana: {Fmt.Count(entries, "wpis", "wpisy", "wpisów")}");
            UpdateDiagnostics();
            AppState.NotifyChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckMismatchesAsync()
    {
        IsBusy = true;
        MismatchState = TableState.Loading;
        OnPropertyChanged(nameof(MismatchState));
        try
        {
            await AppState.RefreshDataAsync();
            var today = DateOnly.FromDateTime(Fmt.NowLocal);
            var query = new ReportQuery
            {
                Since = MismatchRangeIndex switch { 0 => today.AddDays(-6), 1 => today.AddDays(-29), 2 => today.AddDays(-89), _ => null }
            };
            var report = await Task.Run(() => PricingMismatchAnalyzer.Analyze(AppState.Engine, query, samples: 10));
            Mismatches = report.Models.OrderByDescending(model => Math.Abs(model.CalculatedCostUsd - model.StoredCostUsd))
                .Select(model => new MismatchRow(
                    AppState.Engine.FindSource(model.Source)?.DisplayName ?? model.Source,
                    Fmt.Model(model.Model),
                    Fmt.Number(model.Compared),
                    Fmt.Number(model.Mismatches),
                    Fmt.CostFull(model.StoredCostUsd),
                    Fmt.CostFull(model.CalculatedCostUsd),
                    Fmt.Percent(model.AveragePercentDifference)))
                .ToList();
            MismatchSummary = report.Compared == 0
                ? "Brak wpisów z kosztem zapisanym przez narzędzie w tym zakresie."
                : $"Porównano {Fmt.Count(report.Compared, "wpis", "wpisy", "wpisów")}: zgodnych {Fmt.Percent(report.MatchRate * 100)}, " +
                  $"zapisany {Fmt.CostFull(report.StoredCostUsd)}, wyliczony {Fmt.CostFull(report.CalculatedCostUsd)}.";
            MismatchState = Mismatches.Count == 0 ? TableState.Empty : TableState.Ready;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MismatchSummary = "Nie udało się sprawdzić rozbieżności: " + exception.Message;
            MismatchState = TableState.Error;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Mismatches));
            OnPropertyChanged(nameof(MismatchSummary));
            OnPropertyChanged(nameof(MismatchState));
        }
    }

    public void UpdateDiagnostics()
    {
        var engine = AppState.Engine;
        var (files, entries) = engine.Ledger.Statistics();
        var pricing = engine.Pricing;
        LiteLlmStatus = PricingStatus.DescribeLiteLlm(pricing);
        ModelsDevStatus = PricingStatus.DescribeModelsDev(pricing);
        LiteLlmKind = Kind(pricing.Origin, pricing.UpdatedAt);
        ModelsDevKind = Kind(pricing.ModelsDevOrigin, pricing.ModelsDevUpdatedAt);
        DiagnosticsItems =
        [
            new KeyValueItem("Baza", engine.Ledger.DatabasePath, IsMono: true, CanCopy: true),
            new KeyValueItem("Pliki", Fmt.Number(files), IsNumeric: true),
            new KeyValueItem("Wpisy", Fmt.Number(entries), IsNumeric: true),
            new KeyValueItem("Ostatnia synchronizacja", engine.Ledger.LastSync() is { } last ? Fmt.DateTimeShort(last) : "—"),
            new KeyValueItem("Cennik LiteLLM", LiteLlmStatus),
            new KeyValueItem("Cennik models.dev", ModelsDevStatus)
        ];
        DiagnosticsRows = engine.LastSync?.Sources.Select(source => new DiagnosticsRow(
            engine.FindSource(source.Source)?.DisplayName ?? source.Source,
            Fmt.Number(source.Files),
            Fmt.Number(source.ParsedFiles),
            Fmt.Number(source.SkippedLines),
            Fmt.Number(source.Problems.Count))).ToList() ?? [];
        foreach (var name in new[]
                 {
                     nameof(LiteLlmStatus), nameof(ModelsDevStatus), nameof(LiteLlmKind), nameof(ModelsDevKind), nameof(LiteLlmBadge),
                     nameof(ModelsDevBadge), nameof(DiagnosticsItems), nameof(DiagnosticsRows)
                 })
        {
            OnPropertyChanged(name);
        }

        static StatusKind Kind(PricingOrigin origin, DateTimeOffset? updated) => origin switch
        {
            PricingOrigin.Online => StatusKind.Success,
            _ when AppState.Config.Offline => StatusKind.Neutral,
            _ when updated is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromDays(7) => StatusKind.Warning,
            _ => StatusKind.Neutral
        };
    }

    /// <summary>„Kopiuj raport diagnostyczny” — the whole diagnostics as text.</summary>
    private static string DiagnosticsText()
    {
        var engine = AppState.Engine;
        var builder = new StringBuilder();
        var (files, entries) = engine.Ledger.Statistics();
        builder.AppendLine($"Baza: {engine.Ledger.DatabasePath}");
        builder.AppendLine($"Plików: {files} · wpisów: {entries} · ostatnia synchronizacja: {engine.Ledger.LastSync()?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Cennik {PricingStatus.LiteLlmName}: {PricingStatus.DescribeLiteLlm(engine.Pricing)}");
        builder.AppendLine($"Cennik {PricingStatus.ModelsDevName}: {PricingStatus.DescribeModelsDev(engine.Pricing)}");
        if (engine.LastSync is { } sync)
        {
            builder.AppendLine($"Synchronizacja: {sync.Duration.TotalMilliseconds:0} ms");
            foreach (var source in sync.Sources)
            {
                builder.AppendLine($"[{source.Source}] plików {source.Files}, przetworzonych {source.ParsedFiles}, pominiętych linii {source.SkippedLines}");
                foreach (var (reason, count) in source.SkipReasons)
                {
                    builder.AppendLine($"   {count}× {reason}");
                }

                foreach (var problem in source.Problems.Take(5))
                {
                    builder.AppendLine($"   błąd: {Path.GetFileName(problem.Path)}: {problem.Error}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }
}
