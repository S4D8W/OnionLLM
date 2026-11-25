using System.Text;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Factory Droid — pliki ustawień sesji <c>**/*.settings.json</c> z polem <c>tokenUsage</c>.
/// Katalogi: <c>DROID_SESSIONS_DIR</c> (lista rozdzielona przecinkami) lub <c>~/.factory/sessions</c>.
/// </summary>
/// <remarks>
/// Każdy plik to zbiorczy licznik całej sesji (jeden wpis na sesję, <see cref="UsageEntry.IsSessionSummary"/>).
/// Pola: <c>inputTokens</c>, <c>outputTokens</c>, <c>cacheCreationTokens</c>, <c>cacheReadTokens</c>,
/// <c>thinkingTokens</c>, <c>totalTokens</c>. Tokeny „thinking” są osobne od wyjścia, a ccusage wycenia je
/// jak wyjście — dlatego dodajemy je do <see cref="UsageEntry.OutputTokens"/> i zapisujemy też w
/// <see cref="UsageEntry.ReasoningTokens"/>. Znacznik czasu: <c>providerLockTimestamp</c>, a gdy go brak —
/// czas modyfikacji pliku. Model: <c>model</c> lub linia „Model: …” z pliku <c>&lt;id&gt;.jsonl</c> obok,
/// znormalizowany jak w ccusage. Kopie tej samej sesji (np. archiwum) mają wspólny klucz deduplikacji — zostaje
/// najnowszy snapshot (<see cref="DuplicateComparer"/>).
/// Dostawca: <c>providerLock</c>, a gdy go brak — wywnioskowany z nazwy modelu (<see cref="UsageEntry.Provider"/>).
/// <see cref="UsageEntry.Model"/> zachowuje kropki wersji z logu (<c>gemini-2.5-pro</c>), bo cennik potrzebuje nazwy
/// surowej; nazwa w pełni znormalizowana (<c>gemini-2-5-pro</c>) jest kandydatem zapasowym (<see cref="PricingCandidates"/>).
/// </remarks>
public sealed class DroidSource : UsageSourceBase
{
    public const string SourceId = "droid";

    private const string SettingsSuffix = ".settings.json";

    public override string Id => SourceId;

    /// <summary>2: dostawca (<see cref="UsageEntry.Provider"/>) i nazwa modelu z kropkami wersji.</summary>
    public override int ParserVersion => 2;

    public override string DisplayName => "Factory Droid";
    public override string Vendor => "Factory";
    public override string AccentColor => "#F2A93B";

    protected override string[] EnvironmentVariables => ["DROID_SESSIONS_DIR"];

    protected override string[] Executables => ["droid"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".factory", "sessions")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            foreach (var file in FileUtil.SafeEnumerateFiles(root, "*" + SettingsSuffix))
            {
                if (file.EndsWith(SettingsSuffix, StringComparison.OrdinalIgnoreCase) && seen.Add(FileUtil.NormalizePath(file)))
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = FileUtil.ReadAllText(file.Path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (!JsonUtil.TryParse(text, out var document))
        {
            diagnostics.Skip("niepoprawny JSON ustawień Droid");
            return [];
        }

        using (document)
        {
            var entry = ParseSettings(document.RootElement, file.Path, file.Root, diagnostics);
            return entry is null ? [] : [entry];
        }
    }

    internal static UsageEntry? ParseSettings(JsonElement settings, string path, string root, ParseDiagnostics diagnostics)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            settings.Prop("tokenUsage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return null;
        }

        var input = usage.Long("inputTokens");
        var output = usage.Long("outputTokens");
        var cacheWrite = usage.Long("cacheCreationTokens");
        var cacheRead = usage.Long("cacheReadTokens");
        var thinking = usage.Long("thinkingTokens");
        var total = usage.Long("totalTokens");

        long counted;
        try
        {
            counted = checked(input + output + cacheWrite + cacheRead + thinking);
        }
        catch (OverflowException)
        {
            diagnostics.Skip("liczniki tokenów Droid poza zakresem");
            return null;
        }

        // Sama nadwyżka totalTokens (bez rozbicia) też czyni rekord „niepustym” w ccusage.
        if (counted == 0 && total == 0)
        {
            return null;
        }

        var provider = NormalizeProvider(settings.Str("providerLock"));
        var loggedModel = NonBlank(settings.Str("model")) ?? ModelFromSidecar(path);
        var model = loggedModel is null ? "" : CleanModelName(loggedModel);
        var isFallback = NormalizeModelName(model).Length == 0;
        if (isFallback)
        {
            model = DefaultModel(provider);
        }

        // Bez providerLock dostawca wynika z nazwy modelu (ccusage droid/src/parser.rs: infer_droid_provider_from_model).
        if (provider == UnknownProvider)
        {
            provider = InferProvider(NormalizeModelName(model));
        }

        long timestamp;
        if (settings.Prop("providerLockTimestamp").TimestampMs() is { } lockTimestamp)
        {
            timestamp = lockTimestamp;
        }
        else
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                diagnostics.Skip("ustawienia Droid bez znacznika czasu");
                return null;
            }

            timestamp = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        }

        var fileName = Path.GetFileName(path);
        var sessionId = fileName.EndsWith(SettingsSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^SettingsSuffix.Length]
            : "unknown";

        // Droid grupuje sesje w podkatalogach projektu — nazwa podkatalogu (jeśli jest) służy za projekt.
        var directory = Path.GetDirectoryName(path);
        string? project = directory is not null &&
                          !string.Equals(FileUtil.NormalizePath(directory), FileUtil.NormalizePath(root), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(directory)
            : null;

        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = timestamp,
            SessionId = sessionId,
            Project = project,
            Model = model,
            IsFallbackModel = isFallback,
            InputTokens = input,
            OutputTokens = output + thinking,
            CacheCreationTokens = cacheWrite,
            CacheReadTokens = cacheRead,
            ReasoningTokens = thinking,
            IsSessionSummary = true,
            Provider = provider == UnknownProvider ? null : provider,
            DedupeKey = "droid:" + sessionId
        };
    }

    /// <summary>
    /// Kopie tej samej sesji (np. archiwum i bieżący plik): zostaje snapshot o najpóźniejszym znaczniku czasu
    /// (ccusage <c>droid/src/loader.rs</c>: sortowanie po czasie i <c>dedupe_last_wins</c>).
    /// </summary>
    public override IComparer<UsageEntry>? DuplicateComparer { get; } =
        Comparer<UsageEntry>.Create((left, right) => left.TimestampMs.CompareTo(right.TimestampMs));

    /// <summary>
    /// Kandydaci cennika jak w ccusage (<c>droid/src/parser.rs</c> <c>price_droid</c>): najpierw nazwa z logu z prefiksem
    /// dostawcy (<c>anthropic/</c>, <c>openai/</c>, <c>google/</c> + <c>vertex_ai/</c>, <c>xai/</c>…), potem nazwa z logu,
    /// nazwa znormalizowana z prefiksem i bez, a na końcu odsprzedawca (<c>openrouter/…</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        var logged = model.Trim();
        var normalized = NormalizeModelName(logged);
        var (direct, reseller) = ProviderPrefixes(provider ?? UnknownProvider);
        var candidates = new List<string>();
        candidates.AddRange(direct.Select(prefix => prefix + logged));
        candidates.Add(logged);
        if (normalized.Length > 0)
        {
            candidates.AddRange(direct.Select(prefix => prefix + normalized));
            candidates.Add(normalized);
            if (reseller is not null)
            {
                candidates.Add(reseller + normalized);
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (string[] Direct, string? Reseller) ProviderPrefixes(string provider) => provider switch
    {
        "anthropic" => (["anthropic/"], "openrouter/anthropic/"),
        "openai" => (["openai/"], "openrouter/openai/"),
        "google" => (["google/", "vertex_ai/"], "openrouter/google/"),
        "vertex_ai" => (["vertex_ai/"], "openrouter/google/"),
        "xai" => (["xai/"], "openrouter/x-ai/"),
        UnknownProvider => ([], null),
        _ => ([provider + "/"], $"openrouter/{provider}/")
    };

    /// <summary>
    /// Nazwa modelu do wpisu: bez prefiksu <c>custom:</c> i nawiasów <c>[…]</c>, małe litery, spacje → myślniki,
    /// ale z kropkami wersji (<c>gemini-2.5-pro</c>), potrzebnymi do dopasowania cennika (świadome odstępstwo:
    /// ccusage wyświetla <c>gemini-2-5-pro</c>). <see cref="NormalizeModelName"/> tej nazwy daje to samo co dla nazwy z logu.
    /// </summary>
    internal static string CleanModelName(string model) => Collapse(StripDecorations(model), keepDots: true);

    /// <summary>
    /// Normalizacja nazwy modelu jak w ccusage: bez prefiksu <c>custom:</c> i nawiasów <c>[…]</c>,
    /// małe litery, kropki/spacje → myślniki, bez powtórzonych myślników.
    /// </summary>
    internal static string NormalizeModelName(string model) => Collapse(StripDecorations(model), keepDots: false);

    private static string StripDecorations(string model)
    {
        var raw = model.StartsWith("custom:", StringComparison.Ordinal) ? model["custom:".Length..] : model;
        var withoutBrackets = new StringBuilder();
        var depth = 0;
        foreach (var ch in raw)
        {
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                withoutBrackets.Append(ch);
            }
        }

        return withoutBrackets.ToString();
    }

    private static string Collapse(string value, bool keepDots)
    {
        var lower = value.Trim().TrimEnd('-').ToLowerInvariant();
        var normalized = new StringBuilder();
        var previousDash = false;
        foreach (var ch in lower)
        {
            var next = (ch == '.' && !keepDots) || char.IsWhiteSpace(ch) || ch == '-' ? '-' : ch;
            if (next == '-')
            {
                if (!previousDash)
                {
                    normalized.Append('-');
                    previousDash = true;
                }
            }
            else
            {
                normalized.Append(next);
                previousDash = false;
            }
        }

        return normalized.ToString().Trim('-');
    }

    private const string UnknownProvider = "unknown";

    /// <summary><c>providerLock</c> → identyfikator dostawcy (ccusage <c>normalize_droid_provider</c>); hosty Vertex → <c>vertex_ai</c>.</summary>
    internal static string NormalizeProvider(string? value)
    {
        if (Pricing.ProviderNames.HostProvider(value) is { } host)
        {
            return host;
        }

        var normalized = value?.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            null or "" => UnknownProvider,
            "claude" or "anthropic" => "anthropic",
            "google" or "google_ai" or "gemini" => "google",
            "xai" or "x_ai" or "grok" => "xai",
            _ => normalized
        };
    }

    /// <summary>Dostawca wywnioskowany z (znormalizowanej) nazwy modelu (ccusage <c>infer_droid_provider_from_model</c>).</summary>
    internal static string InferProvider(string model)
    {
        if (model.Contains("claude") || model.Contains("opus") || model.Contains("sonnet") || model.Contains("haiku"))
        {
            return "anthropic";
        }

        if (model.StartsWith("gpt-", StringComparison.Ordinal) || model.Contains("-gpt-") || model.Contains("chatgpt") ||
            (model.Length > 1 && model[0] == 'o' && char.IsAsciiDigit(model[1])))
        {
            return "openai";
        }

        if (model.Contains("gemini"))
        {
            return "google";
        }

        return model.Contains("grok") ? "xai" : UnknownProvider;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DefaultModel(string provider) => provider switch
    {
        "anthropic" => "claude-unknown",
        "openai" => "gpt-unknown",
        "google" or "vertex_ai" => "gemini-unknown",
        "xai" => "grok-unknown",
        _ => "unknown"
    };

    /// <summary>Szuka „Model: …” w pierwszych 500 liniach pliku <c>&lt;id&gt;.jsonl</c> obok ustawień.</summary>
    private static string? ModelFromSidecar(string settingsPath)
    {
        var fileName = Path.GetFileName(settingsPath);
        if (!fileName.EndsWith(SettingsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sidecar = Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", fileName[..^SettingsSuffix.Length] + ".jsonl");
        if (!File.Exists(sidecar))
        {
            return null;
        }

        try
        {
            var index = 0;
            foreach (var line in FileUtil.ReadLines(sidecar))
            {
                if (index++ >= 500)
                {
                    break;
                }

                var marker = line.IndexOf("Model:", StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }

                var tail = line[(marker + "Model:".Length)..];
                var end = tail.IndexOfAny(['"', '\\', '[']);
                var raw = (end >= 0 ? tail[..end] : tail).Trim();
                if (raw.Length > 0)
                {
                    return raw;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
