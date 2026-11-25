using System.Globalization;
using System.Text;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Grok Build CLI (xAI) — zakończone tury w <c>sessions/&lt;cwd-url-encoded&gt;/&lt;sesja&gt;/updates.jsonl</c>
/// (opcjonalnie <c>summary.json</c> z id sesji, cwd i domyślnym modelem).
/// Katalog: <c>GROK_HOME</c> (jedna ścieżka, jak w ccusage <c>RootsEnv::Single</c>) lub <c>~/.grok</c>.
/// </summary>
/// <remarks>
/// Liczone są tylko wiersze <c>sessionUpdate = turn_completed</c>, osobno dla każdego klucza <c>modelUsage</c>.
/// <c>inputTokens</c> zawiera odczyt i zapis cache — odejmujemy je. <c>reasoningTokens</c> są już w <c>outputTokens</c>.
/// <c>costUsdTicks</c> to koszt faktury w jednostkach 1e-10 USD. Tury przerwane (bez <c>turn_completed</c>) nie są widoczne.
/// Cennik: nazwa z logu, z prefiksami <c>xai/</c> i <c>x-ai/</c>, potem to samo bez sufiksu <c>-build</c> (<see cref="PricingCandidates"/>).
/// </remarks>
public sealed class GrokSource : UsageSourceBase
{
    public const string SourceId = "grok";
    public const string UnknownModel = "unknown";

    private const double TicksPerUsd = 1e10;

    public override string Id => SourceId;

    /// <summary>2: wpisy oznaczone jako <see cref="UsageEntry.IsAggregate"/>.</summary>
    public override int ParserVersion => 2;

    public override string DisplayName => "Grok Build";
    public override string Vendor => "xAI";
    public override string AccentColor => "#5B5B66";

    private const string HomeVariable = "GROK_HOME";

    protected override string[] Executables => ["grok"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".grok")];

    /// <summary>
    /// <c>GROK_HOME</c> to jedna ścieżka (przecinek może być częścią nazwy), pusta wartość = <c>~/.grok</c>
    /// (ccusage <c>grok/src/paths.rs</c>: <c>RootsEnv::Single</c>).
    /// </summary>
    public override IReadOnlyList<string> ResolveRoots(OnionConfig config)
    {
        if (config.GetSource(Id).Paths.Any(path => !string.IsNullOrWhiteSpace(path)))
        {
            return base.ResolveRoots(config);
        }

        var value = Environment.GetEnvironmentVariable(HomeVariable)?.Trim();
        return string.IsNullOrEmpty(value)
            ? base.ResolveRoots(config)
            : [FileUtil.NormalizePath(FileUtil.ExpandHome(value))];
    }

    /// <summary>
    /// Kandydaci cennika jak w ccusage (<c>grok/src/parser.rs</c> <c>pricing_candidates</c>): nazwa z logu, <c>xai/</c>…,
    /// <c>x-ai/</c>…, a potem te same bez sufiksu <c>-build</c> (Grok Build zapisuje np. <c>grok-4.5-build</c>,
    /// a katalog zna <c>xai/grok-4.5</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model);

    internal static IReadOnlyList<string> ModelCandidates(string? model)
    {
        var stripped = model?.Trim();
        if (string.IsNullOrEmpty(stripped))
        {
            return [];
        }

        var normalized = stripped.EndsWith("-build", StringComparison.Ordinal) ? stripped[..^"-build".Length] : stripped;
        string[] all =
        [
            stripped, "xai/" + stripped, "x-ai/" + stripped,
            normalized, "xai/" + normalized, "x-ai/" + normalized
        ];
        return all.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            foreach (var file in FileUtil.SafeEnumerateFiles(Path.Combine(root, "sessions"), "updates.jsonl"))
            {
                yield return SourceFile.Create(file, root);
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var meta = LoadSessionMeta(file.Path);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in FileUtil.ReadLines(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 || !line.Contains("\"turn_completed\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!JsonUtil.TryParse(line, out var document))
            {
                diagnostics.Skip("niepoprawny JSON");
                continue;
            }

            using (document)
            {
                foreach (var entry in ParseLine(document.RootElement, meta, seen, diagnostics))
                {
                    yield return entry;
                }
            }
        }
    }

    private sealed record SessionMeta(string SessionId, string ProjectPath, string? DefaultModel);

    private static List<UsageEntry> ParseLine(JsonElement root, SessionMeta meta, HashSet<string> seen, ParseDiagnostics diagnostics)
    {
        var result = new List<UsageEntry>();
        var parameters = root.Prop("params");
        var update = parameters?.Prop("update");
        if (update is not { ValueKind: JsonValueKind.Object } updateElement || updateElement.Str("sessionUpdate") != "turn_completed")
        {
            return result;
        }

        if (updateElement.Prop("usage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return result;
        }

        var rows = ModelRows(usage, meta.DefaultModel)
            .Select(row => (row.Model, row.Usage, Split: SplitInput(row.Usage)))
            .Where(row => row.Split.Input + row.Split.CacheRead + row.Split.CacheWrite + row.Usage.Long("outputTokens") +
                          row.Usage.Long("reasoningTokens") > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return result;
        }

        var metaElement = parameters!.Value.Prop("_meta");
        var eventId = metaElement?.Str("eventId") is { } id ? id : null;
        var timestamp = ResolveTimestamp(root, metaElement);
        if (timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
            return result;
        }

        var sessionId = parameters.Value.Str("sessionId") ?? meta.SessionId;
        foreach (var (model, modelUsage, split) in rows)
        {
            var output = modelUsage.Long("outputTokens");
            var reasoning = modelUsage.Long("reasoningTokens");
            // eventId bywa eksportowany do kilku sesji — wtedy klucz obejmuje tylko zdarzenie i model.
            var key = eventId is not null
                ? $"event:{eventId}:{model}"
                : $"{sessionId}:{timestamp}:{model}:{split.Input}:{output}:{split.CacheRead}:{split.CacheWrite}:{reasoning}";
            if (!seen.Add(key))
            {
                continue;
            }

            var ticks = modelUsage.Long("costUsdTicks");
            result.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = timestamp.Value,
                SessionId = sessionId,
                Project = ProjectName(meta.ProjectPath),
                ProjectPath = meta.ProjectPath,
                Model = model,
                IsFallbackModel = model == UnknownModel,
                InputTokens = split.Input,
                OutputTokens = output,
                CacheReadTokens = split.CacheRead,
                CacheCreationTokens = split.CacheWrite,
                ReasoningTokens = reasoning,
                RecordedCostUsd = ticks > 0 ? ticks / TicksPerUsd : null,
                DedupeKey = key,
                // turn_completed sumuje wszystkie żądania tury (ccusage grok/src/parser.rs: UsageGranularity::Aggregate).
                IsAggregate = true
            });
        }

        return result;
    }

    /// <summary>Wiersze per model z <c>modelUsage</c> (sortowane po nazwie), a gdy brak — sumy najwyższego poziomu.</summary>
    private static List<(string Model, JsonElement Usage)> ModelRows(JsonElement usage, string? defaultModel)
    {
        if (usage.Prop("modelUsage") is { ValueKind: JsonValueKind.Object } map)
        {
            var rows = map.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Object)
                .Select(property => (property.Name, property.Value))
                .OrderBy(row => row.Name, StringComparer.Ordinal)
                .ToList();
            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return [(defaultModel ?? UnknownModel, usage)];
    }

    /// <summary>inputTokens zawiera cache read i cache write; odcinamy je (z ograniczeniem do wartości input).</summary>
    internal static (long Input, long CacheRead, long CacheWrite) SplitInput(JsonElement usage)
    {
        var input = usage.Long("inputTokens");
        var cacheRead = Math.Min(usage.Long("cachedReadTokens"), input);
        var uncached = input - cacheRead;
        var cacheWrite = Math.Min(usage.Long("cacheCreationTokens"), uncached);
        return (uncached - cacheWrite, cacheRead, cacheWrite);
    }

    /// <summary><c>_meta.agentTimestampMs</c> (ms), a w drugiej kolejności <c>timestamp</c> koperty (sekundy).</summary>
    private static long? ResolveTimestamp(JsonElement root, JsonElement? meta)
    {
        if (AsLong(meta?.Prop("agentTimestampMs")) is { } millis && millis > 0)
        {
            return millis;
        }

        return AsLong(root.Prop("timestamp")) is { } seconds && seconds > 0 && seconds < long.MaxValue / 1000 ? seconds * 1000 : null;
    }

    private static long? AsLong(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } value)
        {
            return null;
        }

        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var real) && !double.IsNaN(real) && !double.IsInfinity(real) ? (long)real : null;
    }

    private static SessionMeta LoadSessionMeta(string updatesPath)
    {
        var sessionDirectory = Path.GetDirectoryName(updatesPath);
        var sessionId = sessionDirectory is null ? "unknown" : Path.GetFileName(sessionDirectory);
        var projectDirectory = sessionDirectory is null ? null : Path.GetDirectoryName(sessionDirectory);
        var projectPath = projectDirectory is null ? "unknown" : DecodeSegment(Path.GetFileName(projectDirectory));
        string? defaultModel = null;

        var summaryPath = Path.Combine(sessionDirectory ?? "", "summary.json");
        try
        {
            if (File.Exists(summaryPath) && JsonUtil.TryParse(FileUtil.ReadAllText(summaryPath), out var document))
            {
                using (document)
                {
                    var summary = document.RootElement;
                    var info = summary.Prop("info");
                    sessionId = info?.Str("id") is { } id ? id : sessionId;
                    projectPath = info?.Str("cwd") ?? summary.Str("git_root_dir") ?? projectPath;
                    defaultModel = summary.Str("current_model_id");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Uszkodzone / niedostępne summary.json — zostają dane ze ścieżki.
        }

        return new SessionMeta(string.IsNullOrEmpty(sessionId) ? "unknown" : sessionId, projectPath, defaultModel);
    }

    /// <summary>Nazwa katalogu projektu to zakodowany URL cwd; niepoprawny UTF-8 zostawiamy zakodowany.</summary>
    internal static string DecodeSegment(string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length &&
                byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
            {
                bytes.Add(decoded);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
    }

    private static string? ProjectName(string projectPath)
    {
        var trimmed = projectPath.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        var name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
