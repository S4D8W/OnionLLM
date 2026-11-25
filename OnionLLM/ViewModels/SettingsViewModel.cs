using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using OnionLLM.Core.Config;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Util;

namespace OnionLLM.ViewModels;

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

    public SourceSettingViewModel(string id, string name, SourceSettings settings, string state)
    {
        Id = id;
        Name = name;
        _enabled = settings.Enabled;
        _paths = string.Join("; ", settings.Paths);
        _speedIndex = Math.Max(0, Array.FindIndex(SpeedValues, value => value.Equals(settings.Speed ?? "auto", StringComparison.OrdinalIgnoreCase)));
        _includeClaudeAgent = settings.IncludeClaudeAgent;
        _agentDirs = string.Join("; ", settings.AgentDirs);
        _stores = string.Join("; ", settings.Stores.Select(store => $"{store.Name}={store.Path}"));
        State = state;
    }

    public string Id { get; }
    public string Name { get; }
    public string State { get; }
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

/// <summary>Wiersz edytora nadpisań cennika; logika parsowania w <see cref="PricingOverrideForm"/> (Core).</summary>
public sealed class PricingOverrideViewModel : ObservableObject
{
    private bool _showAdvanced;

    public PricingOverrideViewModel(PricingOverrideForm form, Action<PricingOverrideViewModel> remove)
    {
        Form = form;
        _showAdvanced = form.HasAdvanced;
        RemoveCommand = new Command(() => remove(this));
    }

    public PricingOverrideForm Form { get; }
    public Command RemoveCommand { get; }
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

    private void Update(string current, string? value, Action<string> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (current != (value ?? ""))
        {
            assign(value ?? "");
            OnPropertyChanged(propertyName);
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private string _timezone = "";
    private int _costModeIndex;
    private bool _offline;
    private int _startOfWeekIndex;
    private string _sessionLength = "5";
    private string _tokenLimit = "max";
    private string _refreshInterval = "30";
    private string _activeMinutes = "15";
    private string _projectAliases = "";
    private string _modelAliases = "";
    private string _modelLabelAliases = "";
    private string _liteLlmStatus = "";
    private string _modelsDevStatus = "";
    private string _status = "";
    private string _diagnostics = "";
    private string _pricingMismatches = "";
    private int _mismatchRangeIndex = 1;
    private bool _isBusy;
    private bool _runAtStartup;
    private string _startupStatus = "";

    public SettingsViewModel()
    {
        SaveCommand = new Command(async () => await SaveAsync());
        AddOverrideCommand = new Command(() => PricingOverrides.Add(new PricingOverrideViewModel(new PricingOverrideForm(), RemoveOverride)));
        RefreshPricingCommand = new Command(async () => await RefreshPricingAsync());
        RebuildCommand = new Command(async () => await RebuildAsync());
        CheckMismatchesCommand = new Command(async () => await CheckMismatchesAsync());
        OpenFolderCommand = new Command(async () => await Launcher.Default.OpenAsync(new OpenFileRequest("Folder", new ReadOnlyFile(FileUtil.DataDirectory))));
    }

    public string[] CostModes { get; } = ["auto — koszt z narzędzia, inaczej z cennika", "calculate — zawsze z cennika", "display — tylko zapisany koszt"];
    public string[] Days { get; } = ["poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota", "niedziela"];
    public ObservableCollection<SourceSettingViewModel> Sources { get; } = [];
#if WINDOWS
    public bool IsStartupSettingAvailable => true;
#else
    public bool IsStartupSettingAvailable => false;
#endif
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }
    public string StartupStatus { get => _startupStatus; private set => Set(ref _startupStatus, value); }

    /// <summary>Nadpisania cennika (<c>pricingOverrides</c>); stawki w UI w USD za 1M tokenów.</summary>
    public ObservableCollection<PricingOverrideViewModel> PricingOverrides { get; } = [];
    public Command AddOverrideCommand { get; }

    public Command SaveCommand { get; }
    public Command RefreshPricingCommand { get; }
    public Command RebuildCommand { get; }
    public Command CheckMismatchesCommand { get; }

    public string[] MismatchRanges { get; } = ["7 dni", "30 dni", "90 dni", "Cała historia"];
    public int MismatchRangeIndex { get => _mismatchRangeIndex; set => Set(ref _mismatchRangeIndex, value); }

    /// <summary>Rozbieżności kosztu zapisanego przez narzędzie i wyliczonego z cennika (ccusage <c>--debug</c>).</summary>
    public string PricingMismatches { get => _pricingMismatches; private set { if (Set(ref _pricingMismatches, value)) { OnPropertyChanged(nameof(HasPricingMismatches)); } } }
    public bool HasPricingMismatches => PricingMismatches.Length > 0;
    public Command OpenFolderCommand { get; }

    public string Timezone { get => _timezone; set => Set(ref _timezone, value); }
    public int CostModeIndex { get => _costModeIndex; set => Set(ref _costModeIndex, value); }
    public bool Offline { get => _offline; set => Set(ref _offline, value); }
    public int StartOfWeekIndex { get => _startOfWeekIndex; set => Set(ref _startOfWeekIndex, value); }
    public string SessionLength { get => _sessionLength; set => Set(ref _sessionLength, value); }
    public string TokenLimit { get => _tokenLimit; set => Set(ref _tokenLimit, value); }
    public string RefreshInterval { get => _refreshInterval; set => Set(ref _refreshInterval, value); }
    public string ActiveMinutes { get => _activeMinutes; set => Set(ref _activeMinutes, value); }
    public string ProjectAliases { get => _projectAliases; set => Set(ref _projectAliases, value); }

    /// <summary>Aliasy modeli do wyceny (<c>modelAliases</c>): wiersze <c>nazwa z logu=model w cenniku</c>.</summary>
    public string ModelAliases { get => _modelAliases; set => Set(ref _modelAliases, value); }

    /// <summary>Etykiety modeli w statusline (<c>modelLabelAliases</c>): wiersze <c>nazwa modelu=etykieta</c>.</summary>
    public string ModelLabelAliases { get => _modelLabelAliases; set => Set(ref _modelLabelAliases, value); }

    /// <summary>Stan cennika LiteLLM: pochodzenie, data aktualizacji, liczba modeli.</summary>
    public string LiteLlmStatus { get => _liteLlmStatus; private set => Set(ref _liteLlmStatus, value); }

    /// <summary>Stan cennika models.dev (drugie źródło).</summary>
    public string ModelsDevStatus { get => _modelsDevStatus; private set => Set(ref _modelsDevStatus, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string ConfigPath => ConfigLoader.UserConfigPath;

    public async Task LoadAsync()
    {
        var config = AppState.Config;
        Timezone = config.Timezone ?? "";
        CostModeIndex = (int)config.CostMode;
        Offline = config.Offline;
        StartOfWeekIndex = Array.IndexOf(WeekDays, config.StartOfWeek);
        SessionLength = config.SessionLengthHours.ToString(CultureInfo.InvariantCulture);
        TokenLimit = config.TokenLimit;
        RefreshInterval = config.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        ActiveMinutes = config.ActiveSessionMinutes.ToString(CultureInfo.InvariantCulture);
        ProjectAliases = config.ProjectAliases ?? "";
        ModelAliases = ConfigTextFormats.FormatMap(config.ModelAliases);
        ModelLabelAliases = ConfigTextFormats.FormatMap(config.ModelLabelAliases);
        PricingOverrides.Clear();
        foreach (var form in ConfigTextFormats.ToForms(config.PricingOverrides))
        {
            PricingOverrides.Add(new PricingOverrideViewModel(form, RemoveOverride));
        }

        Sources.Clear();
        foreach (var source in AppState.Engine.Sources)
        {
            var settings = config.GetSource(source.Id);
            var installed = source.IsInstalled(config);
            Sources.Add(new SourceSettingViewModel(source.Id, source.DisplayName, settings,
                installed ? "wykryte: " + string.Join("; ", source.ResolveRoots(config).Where(Directory.Exists)) : "nie wykryto"));
        }

        Status = AppState.ConfigError is { } error ? "Błąd pliku konfiguracji: " + error : "";
        UpdateDiagnostics();
#if WINDOWS
        try
        {
            RunAtStartup = await WindowsStartupManager.IsEnabledAsync();
            StartupStatus = "";
        }
        catch (Exception exception) when (IsStartupError(exception))
        {
            StartupStatus = "Nie można odczytać ustawienia autostartu: " + exception.Message;
        }
#endif
    }

    private void RemoveOverride(PricingOverrideViewModel item) => PricingOverrides.Remove(item);

    private async Task SaveAsync()
    {
        try
        {
            var config = AppState.Config.Clone();
            config.Timezone = string.IsNullOrWhiteSpace(Timezone) ? null : Timezone.Trim();
            config.CostMode = (CostMode)Math.Max(0, CostModeIndex);
            config.Offline = Offline;
            config.StartOfWeek = WeekDays[Math.Max(0, StartOfWeekIndex)];
            config.SessionLengthHours = ParseDouble(SessionLength, "Długość bloku");
            config.TokenLimit = string.IsNullOrWhiteSpace(TokenLimit) ? "max" : TokenLimit.Trim();
            config.RefreshIntervalSeconds = (int)ParseDouble(RefreshInterval, "Interwał odświeżania");
            config.ActiveSessionMinutes = Math.Max(1, (int)ParseDouble(ActiveMinutes, "Okno aktywnej sesji"));
            config.ProjectAliases = string.IsNullOrWhiteSpace(ProjectAliases) ? null : ProjectAliases.Trim();
            var sourceIds = Sources.Select(source => source.Id).ToArray();
            config.ModelAliases = ConfigTextFormats.ParseMap(ModelAliases, "Aliasy modeli");
            config.ModelLabelAliases = ConfigTextFormats.ParseMap(ModelLabelAliases, "Etykiety modeli (statusline)");
            config.PricingOverrides = ConfigTextFormats.BuildPricingOverrides(PricingOverrides.Select(item => item.Form), sourceIds);
            foreach (var source in Sources)
            {
                if (!config.Sources.TryGetValue(source.Id, out var settings))
                {
                    config.Sources[source.Id] = settings = new SourceSettings();
                }

                source.ApplyTo(settings, sourceIds);
            }

            // pi: nazwy i nakładanie się katalogów magazynów (ccusage pi_stores.rs) — błąd blokuje zapis.
            var storeErrors = new PiSource().ValidateStores(config);
            if (storeErrors.Count > 0)
            {
                Status = "Nie zapisano: " + string.Join("; ", storeErrors);
                return;
            }

            AppState.SaveConfig(config);
            Status = "Zapisano: " + ConfigLoader.UserConfigPath;
#if WINDOWS
            try
            {
                var startupResult = await WindowsStartupManager.SetEnabledAsync(RunAtStartup);
                RunAtStartup = startupResult.Enabled;
                StartupStatus = startupResult.Status;
            }
            catch (Exception exception) when (IsStartupError(exception))
            {
                StartupStatus = "Nie udało się zmienić ustawienia autostartu: " + exception.Message;
            }
#endif

            // Nowe nadpisania i aliasy działają po ponownym wczytaniu cennika (z cache, bez wymuszania sieci).
            IsBusy = true;
            try
            {
                var catalog = await AppState.Engine.LoadPricingAsync();
                var unpriced = config.ModelAliases.Values.Distinct(StringComparer.Ordinal).Where(model => catalog.Resolve(model) is null).ToArray();
                if (unpriced.Length > 0)
                {
                    Status += " · uwaga, aliasy wskazują modele bez ceny: " + string.Join(", ", unpriced);
                }

                UpdateDiagnostics();
                AppState.NotifyChanged();
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (Exception exception) when (exception is ConfigException or FormatException or IOException or UnauthorizedAccessException)
        {
            Status = "Nie zapisano: " + exception.Message;
        }
    }

#if WINDOWS
    private static bool IsStartupError(Exception exception) =>
        exception is InvalidOperationException or UnauthorizedAccessException or IOException or
            System.Security.SecurityException or System.Runtime.InteropServices.COMException or NotSupportedException;
#endif

    private async Task RefreshPricingAsync()
    {
        IsBusy = true;
        try
        {
            var catalog = await AppState.Engine.LoadPricingAsync(forceRefresh: true);
            Status = (catalog.Origin, catalog.ModelsDevOrigin) switch
            {
                (PricingOrigin.Online, PricingOrigin.Online) =>
                    $"Pobrano aktualny cennik: LiteLLM {catalog.Count} modeli, models.dev {catalog.ModelsDevCount} modeli.",
                _ when AppState.Config.Offline =>
                    "Tryb offline — cennik nie jest pobierany z sieci (używany cache lub snapshot w aplikacji).",
                _ => "Nie udało się pobrać całego cennika (brak sieci?) — " +
                     $"LiteLLM: {PricingStatus.OriginName(catalog.Origin)}, models.dev: {PricingStatus.OriginName(catalog.ModelsDevOrigin)}."
            };
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
        IsBusy = true;
        try
        {
            await Task.Run(() => AppState.Engine.Ledger.Reset());
            await AppState.Engine.SyncAsync(force: true);
            Status = "Baza przebudowana z logów.";
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
        try
        {
            await AppState.RefreshDataAsync();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var query = new ReportQuery
            {
                Since = MismatchRangeIndex switch { 0 => today.AddDays(-6), 1 => today.AddDays(-29), 2 => today.AddDays(-89), _ => null }
            };
            var report = await Task.Run(() => PricingMismatchAnalyzer.Analyze(AppState.Engine, query, samples: 10));
            PricingMismatches = report.Format(AppState.Engine.Reports.TimeZone);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            PricingMismatches = "Nie udało się sprawdzić rozbieżności: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void UpdateDiagnostics()
    {
        var engine = AppState.Engine;
        var builder = new StringBuilder();
        var (files, entries) = engine.Ledger.Statistics();
        builder.AppendLine($"Baza: {engine.Ledger.DatabasePath}");
        builder.AppendLine($"Plików: {files} · wpisów: {entries} · ostatnia synchronizacja: {engine.Ledger.LastSync()?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        var pricing = engine.Pricing;
        LiteLlmStatus = PricingStatus.DescribeLiteLlm(pricing);
        ModelsDevStatus = PricingStatus.DescribeModelsDev(pricing);
        builder.AppendLine($"Cennik {PricingStatus.LiteLlmName}: {LiteLlmStatus}");
        builder.AppendLine($"Cennik {PricingStatus.ModelsDevName}: {ModelsDevStatus}");
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

        Diagnostics = builder.ToString().TrimEnd();
    }

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"{name}: niepoprawna liczba '{value}'.");
}
