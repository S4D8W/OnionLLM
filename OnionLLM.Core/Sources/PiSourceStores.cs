using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>Magazyn sesji pi: domyślny (<see cref="PiSource.SourceId"/>) albo nazwany z <see cref="SourceSettings.Stores"/>.</summary>
/// <param name="Roots">Znormalizowane katalogi magazynu.</param>
public sealed record PiStore(string Name, IReadOnlyList<string> Roots)
{
    public bool IsDefault => Name == PiSource.SourceId;

    /// <summary>Model wyświetlany: <c>[nazwa] model</c> dla magazynu nazwanego (ccusage <c>AgentModel</c>), sam model dla domyślnego.</summary>
    public string? Label(string? model) => model is null || IsDefault ? model : $"[{Name}] {model}";
}

/// <summary>Magazyny po walidacji; <see cref="Errors"/> — czytelne błędy konfiguracji (magazyn z błędem jest pomijany).</summary>
public sealed record PiStoreResolution(IReadOnlyList<PiStore> Stores, IReadOnlyList<string> Errors);

/// <summary>
/// Magazyny sesji pi (ccusage <c>pi/src/paths.rs</c>, <c>ccusage-config/src/config/pi_stores.rs</c>,
/// <c>ccusage-adapter-all/src/loader/pi_stores.rs</c>).
/// </summary>
public sealed partial class PiSource
{
    private const string ConfigErrorPrefix = "pi.stores:";

    /// <summary>Katalog → magazyn, ustalane przy <see cref="EnumerateFiles"/> (z tej samej konfiguracji czyta <see cref="Parse"/>).</summary>
    private volatile IReadOnlyDictionary<string, PiStore> _storeByRoot = new Dictionary<string, PiStore>();

    /// <summary>Nagłówki sesji (rodzic forka) według ścieżki, rozmiaru i czasu modyfikacji — do odcisku łańcucha forków.</summary>
    private readonly ConcurrentDictionary<string, (long Size, long Modified, string? Parent)> _headers = new(PathComparer);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Katalogi wszystkich magazynów (domyślnego i poprawnych nazwanych).</summary>
    public override IReadOnlyList<string> ResolveRoots(OnionConfig config) =>
        ResolveStores(config).Stores.SelectMany(store => store.Roots).Distinct(PathComparer).ToArray();

    /// <summary>
    /// Magazyn domyślny: <see cref="SourceSettings.Paths"/> oraz <c>&lt;katalog agenta&gt;/sessions</c> dla każdego z
    /// <see cref="SourceSettings.AgentDirs"/>; gdy oba puste — <c>PI_AGENT_DIR</c>, a potem <c>~/.pi/agent/sessions</c>
    /// (ccusage <c>pi/src/paths.rs</c> <c>paths</c>, <c>--pi-agent-dir</c>).
    /// </summary>
    public IReadOnlyList<string> DefaultStoreRoots(OnionConfig config)
    {
        var settings = config.GetSource(Id);
        var configured = settings.Paths
            .SelectMany(FileUtil.SplitPaths)
            .Concat(settings.AgentDirs.SelectMany(FileUtil.SplitPaths).Select(agent => Path.Combine(agent, "sessions")))
            .ToArray();
        IEnumerable<string> roots = configured.Length > 0
            ? configured
            : FileUtil.EnvPaths("PI_AGENT_DIR") is { Count: > 0 } environment ? environment : DefaultRoots();
        return roots.Select(FileUtil.NormalizePath).Distinct(PathComparer).ToArray();
    }

    /// <summary>Błędy konfiguracji magazynów nazwanych (do pokazania w Ustawieniach przed zapisem).</summary>
    public IReadOnlyList<string> ValidateStores(OnionConfig config) => ResolveStores(config).Errors;

    /// <summary>
    /// Magazyn domyślny i nazwane. Nazwa: <c>^[a-z][a-z0-9_-]{0,31}$</c>, unikalna, różna od <c>all</c> i identyfikatorów
    /// źródeł; ścieżka niepusta; istniejące katalogi magazynu nie mogą się pokrywać (ani zawierać) z katalogami innego
    /// magazynu. ccusage przerywa wtedy działanie — tutaj magazyn z błędem jest pomijany, a błąd trafia do
    /// <see cref="PiStoreResolution.Errors"/> i diagnostyki synchronizacji.
    /// </summary>
    public PiStoreResolution ResolveStores(OnionConfig config)
    {
        var defaultStore = new PiStore(SourceId, DefaultStoreRoots(config));
        var stores = new List<PiStore> { defaultStore };
        var errors = new List<string>();
        var owners = new List<(string Path, string Owner)>();
        foreach (var root in defaultStore.Roots.Where(Directory.Exists))
        {
            owners.Add((root, "domyślny magazyn pi"));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var reserved = SourceRegistry.Default.Select(source => source.Id).Append("all").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configured = config.GetSource(Id).Stores;
        for (var index = 0; index < configured.Count; index++)
        {
            var name = configured[index].Name?.Trim() ?? "";
            var path = configured[index].Path ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add($"pi.stores[{index}] ('{name}'): ścieżka nie może być pusta.");
                continue;
            }

            if (!StoreNameRegex().IsMatch(name))
            {
                errors.Add($"pi.stores[{index}].name musi pasować do ^[a-z][a-z0-9_-]{{0,31}}$ (jest '{name}').");
                continue;
            }

            if (reserved.Contains(name))
            {
                errors.Add($"Nazwa pi.stores '{name}' koliduje z wbudowanym źródłem.");
                continue;
            }

            if (!names.Add(name))
            {
                errors.Add($"Zduplikowana nazwa pi.stores '{name}'.");
                continue;
            }

            var roots = FileUtil.SplitPaths(path).Select(FileUtil.NormalizePath).Distinct(PathComparer).ToArray();
            var collisions = new SortedSet<string>(StringComparer.Ordinal);
            var claimed = new List<(string Path, string Owner)>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var (owned, owner) in owners.Concat(claimed))
                {
                    if (Overlaps(root, owned))
                    {
                        collisions.Add(owner);
                    }
                }

                claimed.Add((root, $"pi.stores '{name}'"));
            }

            if (collisions.Count > 0)
            {
                errors.Add($"Ścieżki pi.stores '{name}' nakładają się na: {string.Join(", ", collisions)}.");
                continue;
            }

            owners.AddRange(claimed);
            stores.Add(new PiStore(name, roots));
        }

        return new PiStoreResolution(stores, errors);
    }

    /// <summary>Ten sam katalog albo jeden zawiera drugi (ccusage <c>paths_overlap</c>).</summary>
    private static bool Overlaps(string left, string right) =>
        string.Equals(left, right, PathComparison) ||
        left.StartsWith(right + Path.DirectorySeparatorChar, PathComparison) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, PathComparison);

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var resolution = ResolveStores(config);
        var byRoot = new Dictionary<string, PiStore>(PathComparer);
        foreach (var store in resolution.Stores)
        {
            foreach (var root in store.Roots)
            {
                byRoot.TryAdd(root, store);
            }
        }

        _storeByRoot = byRoot;

        // Błąd konfiguracji jako „plik” bez wpisów: Parse zapisuje go w diagnostyce przy każdej synchronizacji.
        for (var index = 0; index < resolution.Errors.Count; index++)
        {
            yield return new SourceFile($"{ConfigErrorPrefix}{index}", resolution.Errors[index])
            {
                ModifiedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        foreach (var store in resolution.Stores)
        {
            var files = new List<(string Path, string Root)>();
            foreach (var root in store.Roots)
            {
                files.AddRange(FileUtil.SafeEnumerateFiles(root, "*.jsonl")
                    .Where(file => !IsSubagentArtifact(file))
                    .Select(file => (FileUtil.NormalizePath(file), root)));
            }

            var discovered = files.Select(file => file.Path).ToHashSet(PathComparer);
            foreach (var (path, root) in files)
            {
                yield return Fingerprint(SourceFile.Create(path, root), store, discovered);
            }
        }
    }

    /// <summary>
    /// <see cref="SourceFile.ModifiedMs"/> jako odcisk: dla forka obejmuje rozmiar i czas modyfikacji rodziców (prefiks
    /// powtórzonej historii liczony jest od nowa, gdy rodzic się zmieni lub pojawi), dla magazynu nazwanego — jego nazwę
    /// (zmiana nazwy zmienia etykietę modelu). Ledger porównuje te pola tylko pod kątem zmian.
    /// </summary>
    private SourceFile Fingerprint(SourceFile file, PiStore store, HashSet<string> discovered)
    {
        var salt = new List<string>();
        if (!store.IsDefault)
        {
            salt.Add("store:" + store.Name);
        }

        var visited = new HashSet<string>(PathComparer) { file.Path };
        var current = file.Path;
        for (var depth = 0; depth < MaxLineageDepth; depth++)
        {
            var parent = Header(current).Parent;
            if (parent is null || ResolveDiscovered(parent, current, discovered) is not { } parentPath || !visited.Add(parentPath))
            {
                break;
            }

            var info = new FileInfo(parentPath);
            salt.Add(string.Create(CultureInfo.InvariantCulture,
                $"{parentPath}:{info.Length}:{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()}"));
            current = parentPath;
        }

        if (salt.Count == 0)
        {
            return file;
        }

        salt.Add(file.ModifiedMs.ToString(CultureInfo.InvariantCulture));
        return file with { ModifiedMs = (long)(Fnv1a(string.Join('\n', salt)) & long.MaxValue) };
    }

    /// <summary>Rodzic z nagłówka: ścieżka bezwzględna albo względna wobec katalogu dziecka, wśród plików magazynu.</summary>
    private static string? ResolveDiscovered(string parentSession, string childPath, HashSet<string> discovered)
    {
        try
        {
            var candidate = Path.IsPathRooted(parentSession)
                ? FileUtil.NormalizePath(parentSession)
                : FileUtil.NormalizePath(Path.Combine(Path.GetDirectoryName(childPath) ?? "", parentSession));
            return discovered.Contains(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private (long Size, long Modified, string? Parent) Header(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return (0, 0, null);
        }

        var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        if (_headers.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Modified == modified)
        {
            return cached;
        }

        string? parent;
        try
        {
            parent = ReadSession(path, null, CancellationToken.None, headerOnly: true).ParentSession;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            parent = null;
        }

        var header = (info.Length, modified, parent);
        _headers[path] = header;
        return header;
    }

    private static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in text)
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Magazyn katalogu pliku; nieznany katalog (np. plik podany wprost) = magazyn domyślny z tym katalogiem.</summary>
    private PiStore StoreFor(string root)
    {
        var normalized = FileUtil.NormalizePath(root);
        return _storeByRoot.TryGetValue(normalized, out var store) ? store : new PiStore(SourceId, [normalized]);
    }

    private static bool IsConfigErrorFile(SourceFile file) => file.Path.StartsWith(ConfigErrorPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Model magazynu nazwanego: najpierw <c>[nazwa] model</c> (nadpisania cen w stylu ccusage), potem sama nazwa
    /// (ccusage <c>AgentModel::candidates</c>). Nadpisania <c>[pi] model</c> obsługuje cennik przez identyfikator źródła.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        if (entry.Model.StartsWith('[') && entry.Model.IndexOf("] ", StringComparison.Ordinal) is var end and > 1)
        {
            return [entry.Model, entry.Model[(end + 2)..]];
        }

        return [entry.Model];
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex StoreNameRegex();
}
