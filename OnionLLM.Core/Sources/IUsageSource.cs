using OnionLLM.Core.Config;
using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>Plik (lub baza) z danymi źródła.</summary>
public sealed record SourceFile(string Path, string Root)
{
    public long Size { get; init; }

    public long ModifiedMs { get; init; }

    public static SourceFile Create(string path, string root, params string[] companions)
    {
        var info = new FileInfo(path);
        long size = info.Exists ? info.Length : 0;
        long modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() : 0;

        // Pliki towarzyszące (np. SQLite -wal) też zmieniają stan danych.
        foreach (var companion in companions)
        {
            var extra = new FileInfo(path + companion);
            if (extra.Exists)
            {
                size += extra.Length;
                modified = Math.Max(modified, new DateTimeOffset(extra.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }
        }

        return new SourceFile(path, root) { Size = size, ModifiedMs = modified };
    }
}

/// <summary>Zbiera informacje o pominiętych liniach i problemach (tryb debug / diagnostyka).</summary>
public sealed class ParseDiagnostics
{
    private readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);

    public int SkippedLines { get; private set; }

    public IReadOnlyDictionary<string, int> Reasons => _reasons;

    public void Skip(string reason)
    {
        SkippedLines++;
        _reasons[reason] = _reasons.TryGetValue(reason, out var count) ? count + 1 : 1;
    }
}

/// <summary>Źródło danych o zużyciu (narzędzie AI zapisujące lokalne logi).</summary>
public interface IUsageSource
{
    /// <summary>Krótki identyfikator, np. <c>claude</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Vendor { get; }

    string AccentColor { get; }

    /// <summary>Wersja parsera; jej zmiana wymusza ponowne przetworzenie wszystkich plików źródła.</summary>
    int ParserVersion { get; }

    /// <summary>Katalogi danych: ścieżki z konfiguracji, zmienne środowiskowe lub lokalizacje domyślne.</summary>
    IReadOnlyList<string> ResolveRoots(OnionConfig config);

    bool IsInstalled(OnionConfig config);

    IEnumerable<SourceFile> EnumerateFiles(OnionConfig config);

    IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken);

    /// <summary>
    /// Nazwy modeli do wyszukania w cenniku, w kolejności (pierwsza znaleziona wygrywa), np. z prefiksem dostawcy
    /// (<c>xai/grok-4.5</c>), surowa i znormalizowana. Domyślnie sam <see cref="UsageEntry.Model"/>.
    /// Odpowiednik kandydatów <c>price_entry</c> w adapterach ccusage.
    /// </summary>
    IEnumerable<string> PricingCandidates(UsageEntry entry) => entry.Model is null ? [] : [entry.Model];

    /// <summary>Korekta cennika specyficzna dla źródła (np. ZCode: zapis do cache po stawce wejścia). Domyślnie bez zmian.</summary>
    Pricing.ModelPricing AdjustPricing(UsageEntry entry, Pricing.ModelPricing pricing) => pricing;

    /// <summary>
    /// Reguła wyboru między wpisami o tym samym <see cref="UsageEntry.DedupeKey"/>: wynik &gt; 0, gdy pierwszy wpis jest lepszy.
    /// Null = reguła domyślna <see cref="Storage.Deduplicator"/>.
    /// </summary>
    IComparer<UsageEntry>? DuplicateComparer => null;

    /// <summary>
    /// Uzgadnia wpisy tego źródła pochodzące z różnych plików (po deduplikacji kluczem, przed wyborem poziomu sesji),
    /// np. Copilot: OTel vs <c>session.shutdown</c>, pi: forki między magazynami, Antigravity: kopie z kilku baz.
    /// Dostaje wyłącznie wpisy tego źródła; domyślnie bez zmian.
    /// </summary>
    IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries) => entries;

    /// <summary>
    /// Ustawienia źródła stosowane przy odczycie (bez ponownego parsowania), np. Codex: wymuszony poziom szybkości,
    /// OpenCode: liczenie tur claude-agent. Wołane dla każdego wpisu przed deduplikacją; domyślnie bez zmian.
    /// </summary>
    UsageEntry ApplySettings(UsageEntry entry, SourceSettings settings) => entry;

    /// <summary>
    /// True, gdy <see cref="Reconcile"/> potrzebuje całej historii źródła (decyzje zależą od wpisów spoza zakresu raportu).
    /// Wtedy wpisy źródła są wczytywane bez filtra dat, a zakres jest nakładany po deduplikacji.
    /// </summary>
    bool ReconcileNeedsFullHistory => false;

    /// <summary>
    /// Prefiksy <see cref="UsageEntry.DedupeKey"/>, pod którymi <see cref="Reconcile"/> może dopasować do podanych wpisów
    /// z zakresu dat wpisy spoza niego (np. Claude: ID wiadomości — kopia we wznowionej sesji, oryginał przed zakresem).
    /// Silnik dociąga takie wpisy z ledgera bez filtra dat, uzgadnia je razem z zakresem i dopiero potem nakłada zakres —
    /// tańsza alternatywa <see cref="ReconcileNeedsFullHistory"/> dla źródeł, których dopasowania nie wychodzą poza wspólny
    /// prefiks klucza. Null (domyślnie) = bez dociągania.
    /// </summary>
    IReadOnlyCollection<string>? ReconcileKeyPrefixes(IReadOnlyList<UsageEntry> entries) => null;

    /// <summary>Błędy konfiguracji katalogów źródła (np. zmienna środowiskowa wskazuje nieistniejący katalog) — do Diagnostyki.</summary>
    IReadOnlyList<string> ConfigurationProblems(OnionConfig config) => [];

    /// <summary>
    /// True dla wpisów używanych wyłącznie w raporcie sesji bez filtra dat (np. sumy sesji OpenCode, ccusage
    /// <c>opencode/src/loader/files.rs</c>); pozostałe widoki pomijają je przed deduplikacją.
    /// </summary>
    bool IsSessionReportOnly(UsageEntry entry) => false;
}

/// <summary>Wspólna logika rozwiązywania katalogów: konfiguracja → zmienna środowiskowa → domyślne.</summary>
public abstract class UsageSourceBase : IUsageSource
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Vendor { get; }
    public abstract string AccentColor { get; }
    public virtual int ParserVersion => 1;

    /// <summary>Zmienne środowiskowe wskazujące katalog(i) danych.</summary>
    protected virtual string[] EnvironmentVariables => [];

    protected abstract IEnumerable<string> DefaultRoots();

    /// <summary>Nazwy plików wykonywalnych używane do wykrycia instalacji.</summary>
    protected virtual string[] Executables => [];

    public virtual IReadOnlyList<string> ResolveRoots(OnionConfig config)
    {
        var configured = config.GetSource(Id).Paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Util.FileUtil.ExpandHome)
            .ToArray();
        if (configured.Length > 0)
        {
            return Distinct(configured);
        }

        foreach (var variable in EnvironmentVariables)
        {
            var paths = Util.FileUtil.EnvPaths(variable);
            if (paths.Count > 0)
            {
                return Distinct(paths);
            }
        }

        return Distinct(DefaultRoots());
    }

    public virtual bool IsInstalled(OnionConfig config) =>
        ResolveRoots(config).Any(Directory.Exists) || Executables.Any(Util.FileUtil.IsToolOnPath);

    public abstract IEnumerable<SourceFile> EnumerateFiles(OnionConfig config);

    public abstract IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken);

    // Hooki jako metody wirtualne: domyślna implementacja interfejsu nie jest zastępowana przez składową klasy pochodnej,
    // jeśli klasa nie deklaruje interfejsu ponownie — źródła nadpisują je przez `override`.
    public virtual IEnumerable<string> PricingCandidates(UsageEntry entry) => entry.Model is null ? [] : [entry.Model];

    public virtual Pricing.ModelPricing AdjustPricing(UsageEntry entry, Pricing.ModelPricing pricing) => pricing;

    public virtual IComparer<UsageEntry>? DuplicateComparer => null;

    public virtual IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries) => entries;

    public virtual UsageEntry ApplySettings(UsageEntry entry, SourceSettings settings) => entry;

    public virtual bool ReconcileNeedsFullHistory => false;

    public virtual IReadOnlyCollection<string>? ReconcileKeyPrefixes(IReadOnlyList<UsageEntry> entries) => null;

    public virtual IReadOnlyList<string> ConfigurationProblems(OnionConfig config) => [];

    public virtual bool IsSessionReportOnly(UsageEntry entry) => false;

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        paths.Select(Util.FileUtil.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
