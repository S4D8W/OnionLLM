using System.Text.Json.Serialization;

namespace OnionLLM.Core.Config;

public enum CostMode
{
    /// <summary>Koszt zapisany przez narzędzie, gdy jest; w przeciwnym razie wyliczony z tokenów.</summary>
    Auto,

    /// <summary>Zawsze wyliczany z tokenów i cennika.</summary>
    Calculate,

    /// <summary>Wyłącznie koszt zapisany przez narzędzie (brak = 0).</summary>
    Display
}

public sealed class SourceSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Własne katalogi danych; puste = zmienne środowiskowe lub domyślne lokalizacje.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Codex: poziom szybkości do wyceny — <c>auto</c> (z logów, potem <c>config.toml</c>), <c>standard</c>, <c>flex</c>, <c>fast</c> (ccusage <c>--speed</c>).</summary>
    public string? Speed { get; set; }

    /// <summary>OpenCode: licz tury dostawcy <c>claude-agent</c>, które Claude Code już liczy (ccusage <c>--include-claude-agent</c>).</summary>
    public bool IncludeClaudeAgent { get; set; }

    /// <summary>pi: katalogi agenta; czytany jest katalog <c>sessions</c> każdego z nich (ccusage <c>--pi-agent-dir</c>).</summary>
    public List<string> AgentDirs { get; set; } = [];

    /// <summary>pi: dodatkowe nazwane magazyny sesji w formacie pi (ccusage <c>pi.stores</c>).</summary>
    public List<SessionStore> Stores { get; set; } = [];
}

/// <summary>Nazwany magazyn sesji (np. fork narzędzia zapisujący w formacie pi).</summary>
public sealed class SessionStore
{
    /// <summary>Nazwa: <c>^[a-z][a-z0-9_-]{0,31}$</c>, unikalna, różna od identyfikatorów źródeł.</summary>
    public string Name { get; set; } = "";

    /// <summary>Katalog sesji lub lista po przecinku; <c>~</c> rozwijane.</summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// Nadpisanie cennika modelu (pola jak <c>pricingOverrides</c> w ccusage, <c>ccusage-config/src/config_schema.rs</c>).
/// Klucz: nazwa modelu, <c>dostawca/model</c>, alias albo <c>[źródło] model</c>. Pola pominięte zostają z cennika.
/// </summary>
public sealed class PricingOverride
{
    public double? InputCostPerToken { get; set; }
    public double? OutputCostPerToken { get; set; }
    public double? CacheCreationInputTokenCost { get; set; }

    /// <summary>Zapis do cache z TTL 1 h. W JSON jako <c>cacheCreationInputTokenCostAbove1hr</c> (nazwa ccusage); stara nazwa <c>…Above1h</c> też jest przyjmowana.</summary>
    [JsonPropertyName("cacheCreationInputTokenCostAbove1hr")]
    public double? CacheCreationInputTokenCostAbove1h { get; set; }

    /// <summary>Dawna nazwa pola (<c>cacheCreationInputTokenCostAbove1h</c>) — tylko do odczytu starych konfiguracji, nie jest zapisywana.</summary>
    [JsonPropertyName("cacheCreationInputTokenCostAbove1h")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public double? LegacyCacheCreationInputTokenCostAbove1h
    {
        get => null;
        set => CacheCreationInputTokenCostAbove1h ??= value;
    }

    public double? CacheReadInputTokenCost { get; set; }

    /// <summary>Stawki progu długiego kontekstu (całe żądanie dla Claude/Gemini/Grok, krańcowo dla pozostałych; próg z cennika lub 200k).</summary>
    public double? InputCostPerTokenAbove200kTokens { get; set; }
    public double? OutputCostPerTokenAbove200kTokens { get; set; }
    public double? CacheCreationInputTokenCostAbove200kTokens { get; set; }
    public double? CacheReadInputTokenCostAbove200kTokens { get; set; }

    public long? MaxInputTokens { get; set; }

    /// <summary>Mnożnik trybu fast (np. 6 dla Opus 4.6).</summary>
    public double? FastMultiplier { get; set; }

    /// <summary>Mnożnik trybu flex (np. 0,5).</summary>
    public double? FlexMultiplier { get; set; }
}

/// <summary>Motyw aplikacji (DESIGN.md §3.4): systemowy, jasny lub ciemny.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>Gęstość interfejsu (DESIGN.md §2.8).</summary>
public enum DensityPreference
{
    Comfortable,
    Compact
}

/// <summary>Tryb animacji (DESIGN.md §2.7.4); <see cref="Full"/> respektuje systemowe ograniczenie ruchu.</summary>
public enum MotionPreference
{
    Full,
    Reduced,
    Off
}

/// <summary>Ustawienia wyglądu aplikacji (klucz <c>appearance</c>); CLI ich nie używa.</summary>
public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public DensityPreference Density { get; set; } = DensityPreference.Comfortable;

    public MotionPreference Motion { get; set; } = MotionPreference.Full;
}

/// <summary>Zachowanie okna aplikacji (klucz <c>window</c>).</summary>
/// <summary>Waluta wyświetlania kosztów w GUI (DESIGN.md §5.1); eksport i CLI zawsze w USD.</summary>
public sealed class CurrencySettings
{
    /// <summary><c>USD</c> (domyślnie), <c>PLN</c> lub <c>EUR</c>.</summary>
    public string Display { get; set; } = "USD";

    /// <summary>1 USD = <c>Rate</c> jednostek waluty wyświetlania.</summary>
    public double? Rate { get; set; }

    /// <summary><c>manual</c> (kurs wpisany) lub <c>nbp</c> (kurs średni NBP, pobierany raz dziennie).</summary>
    public string RateSource { get; set; } = "nbp";

    /// <summary>Data notowania kursu (<c>yyyy-MM-dd</c>).</summary>
    public string? RateDate { get; set; }

    public bool IsConverted => Display != "USD" && Rate is > 0;

    public DateOnly? ParsedRateDate => DateOnly.TryParseExact(RateDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var date) ? date : null;
}

/// <summary>Budżety kosztu w USD (DESIGN.md §5.9); null = brak budżetu.</summary>
public sealed class BudgetSettings
{
    public double? DailyUsd { get; set; }

    public double? MonthlyUsd { get; set; }

    /// <summary>Budżety per źródło (klucz = id źródła).</summary>
    public Dictionary<string, SourceBudget> PerSource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceBudget
{
    public double? DailyUsd { get; set; }

    public double? MonthlyUsd { get; set; }
}

public sealed class WindowSettings
{
    /// <summary>Okno główne zawsze na wierzchu (domyślnie wyłączone, DESIGN.md §7.1).</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Zamknięcie okna chowa je do zasobnika, gdy ikona w zasobniku jest włączona (§7.1).</summary>
    public bool CloseToTray { get; set; } = true;
}

/// <summary>Ikona w zasobniku Windows / pasku menu macOS (§7.3, §7.4).</summary>
public sealed class TraySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>macOS: koszt dnia obok ikony w pasku menu.</summary>
    public bool ShowCost { get; set; } = true;
}

/// <summary>Tryb kompaktowy (mini-widok, §7.2).</summary>
public sealed class MiniViewSettings
{
    /// <summary>Moduły w kolejności: <c>cost</c>, <c>limits</c>, <c>block</c>, <c>sources</c>.</summary>
    public List<string> Modules { get; set; } = [.. AllModules];

    public bool AlwaysOnTop { get; set; } = true;

    public static readonly string[] AllModules = ["cost", "limits", "block", "sources"];
}

/// <summary>Progi powiadomień jednego limitu lub budżetu w procentach (§5.8).</summary>
public sealed class ThresholdSettings
{
    public int Warn { get; set; } = 80;

    public int Exceed { get; set; } = 100;

    public bool Enabled { get; set; } = true;
}

/// <summary>Ciche godziny powiadomień, np. 22:00–08:00 (§5.8).</summary>
public sealed class QuietHoursSettings
{
    public bool Enabled { get; set; }

    public string From { get; set; } = "22:00";

    public string To { get; set; } = "08:00";

    /// <summary>Czy <paramref name="time"/> (czas lokalny) wypada w cichych godzinach (zakres może przechodzić przez północ).</summary>
    public bool Contains(TimeOnly time)
    {
        if (!Enabled || !TimeOnly.TryParse(From, System.Globalization.CultureInfo.InvariantCulture, out var from) ||
            !TimeOnly.TryParse(To, System.Globalization.CultureInfo.InvariantCulture, out var to) || from == to)
        {
            return false;
        }

        return from < to ? time >= from && time < to : time >= from || time < to;
    }
}

/// <summary>Powiadomienia systemowe (§5.8, §7.6).</summary>
public sealed class NotificationSettings
{
    public static readonly string[] Limits = ["fiveHour", "sevenDay", "block", "dailyBudget", "monthlyBudget"];

    public bool Enabled { get; set; } = true;

    /// <summary>Progi per limit (<see cref="Limits"/>); brak wpisu = 80% / 100%, włączone.</summary>
    public Dictionary<string, ThresholdSettings> Thresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Powiadomienie o wysokim tempie spalania aktywnego bloku.</summary>
    public bool BurnHigh { get; set; } = true;

    public QuietHoursSettings QuietHours { get; set; } = new();

    public ThresholdSettings For(string limit) => Thresholds.TryGetValue(limit, out var threshold) ? threshold : new ThresholdSettings();
}

/// <summary>Konfiguracja OnionLLM (plik JSON, camelCase).</summary>
public sealed class OnionConfig
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Identyfikator strefy IANA lub Windows; null = strefa systemowa.</summary>
    public string? Timezone { get; set; }

    public CostMode CostMode { get; set; } = CostMode.Auto;

    /// <summary>Bez pobierania cennika z sieci (używany snapshot/cache).</summary>
    public bool Offline { get; set; }

    public DayOfWeek StartOfWeek { get; set; } = DayOfWeek.Monday;

    public double SessionLengthHours { get; set; } = 5;

    /// <summary>Limit tokenów bloku: liczba albo <c>max</c> (największy zakończony blok).</summary>
    public string TokenLimit { get; set; } = "max";

    /// <summary>Aliasy projektów w formacie <c>klucz=Nazwa,klucz2=Nazwa2</c>.</summary>
    public string? ProjectAliases { get; set; }

    public Dictionary<string, SourceSettings> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PricingOverride> PricingOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aliasy modeli do wyceny: <c>nazwa z logu → model w cenniku</c> (odpowiednik <c>CCUSAGE_MODEL_ALIASES</c>);
    /// <c>x-fast</c> korzysta z aliasu <c>x</c>. Nazwa wyświetlana się nie zmienia.
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Statusline Claude Code: krótka etykieta dla nazwy modelu z hooka, np. ARN profilu Bedrock → <c>claude-opus-4-6</c>
    /// (ccusage <c>modelLabelAliases</c>). Klucz porównywany dokładnie z nazwą wyświetlaną modelu.
    /// </summary>
    public Dictionary<string, string> ModelLabelAliases { get; set; } = new(StringComparer.Ordinal);

    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>Po ilu minutach bez aktywności sesja jest uznawana za zakończoną.</summary>
    public int ActiveSessionMinutes { get; set; } = 15;

    public AppearanceSettings Appearance { get; set; } = new();

    public WindowSettings Window { get; set; } = new();

    /// <summary>Ikona w zasobniku / pasku menu (tylko GUI).</summary>
    public TraySettings Tray { get; set; } = new();

    /// <summary>Mini-widok (tylko GUI).</summary>
    public MiniViewSettings MiniView { get; set; } = new();

    /// <summary>Powiadomienia systemowe (tylko GUI).</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>Waluta wyświetlania i kurs (tylko GUI).</summary>
    public CurrencySettings Currency { get; set; } = new();

    /// <summary>Budżety dzienne i miesięczne (tylko GUI: Dziś, Limity, Raporty).</summary>
    public BudgetSettings Budgets { get; set; } = new();

    public SourceSettings GetSource(string id) =>
        Sources.TryGetValue(id, out var settings) ? settings : new SourceSettings();

    public bool IsSourceEnabled(string id) => GetSource(id).Enabled;

    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(Timezone))
        {
            return TimeZoneInfo.Local;
        }

        if (Timezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(Timezone, out var zone)
            ? zone
            : throw new ConfigException($"Nieznana strefa czasowa: '{Timezone}'.");
    }

    public IReadOnlyDictionary<string, string> ParseProjectAliases()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ProjectAliases))
        {
            return result;
        }

        foreach (var pair in ProjectAliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && separator < pair.Length - 1)
            {
                result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }

        return result;
    }

    /// <summary><c>tokenLimit: "none"</c> — bloki bez limitu (bez procentu i ostrzeżeń, także bez maksimum z historii).</summary>
    public bool IsTokenLimitDisabled => TokenLimit?.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Limit tokenów bloku lub null dla trybów <c>max</c> i <c>none</c> (rozróżnia je <see cref="IsTokenLimitDisabled"/>).</summary>
    public long? ParseTokenLimit()
    {
        if (string.IsNullOrWhiteSpace(TokenLimit) || TokenLimit.Equals("max", StringComparison.OrdinalIgnoreCase) || IsTokenLimitDisabled)
        {
            return null;
        }

        return long.TryParse(TokenLimit, out var limit) && limit > 0
            ? limit
            : throw new ConfigException($"Niepoprawny limit tokenów: '{TokenLimit}' (oczekiwano liczby, 'max' lub 'none').");
    }

    public OnionConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, ConfigLoader.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<OnionConfig>(json, ConfigLoader.JsonOptions)!;
    }
}

public sealed class ConfigException(string message) : Exception(message);
