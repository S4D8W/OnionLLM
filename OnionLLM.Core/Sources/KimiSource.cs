using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Kimi CLI oraz Kimi Code (Moonshot AI) — logi <c>wire.jsonl</c>.
/// <list type="bullet">
/// <item>Kimi CLI: <c>&lt;root&gt;/sessions/&lt;grupa&gt;/&lt;sesja&gt;/wire.jsonl</c>, zdarzenia <c>StatusUpdate</c> z <c>token_usage</c>.</item>
/// <item>Kimi Code: <c>&lt;root&gt;/sessions/&lt;workspace&gt;/&lt;sesja&gt;/agents/&lt;agent&gt;/wire.jsonl</c>, linie <c>usage.record</c>
/// (liczone tylko <c>usageScope: turn</c>; <c>session</c> to sumy narastające).</item>
/// </list>
/// Katalogi: <c>KIMI_DATA_DIR</c> (lista) lub <c>~/.kimi</c> i <c>~/.kimi-code</c>.
/// </summary>
/// <remarks>
/// Liczniki Kimi są rozłączne (<c>input_other</c> nie zawiera cache). Nadwyżka <c>token_usage.total</c> ponad sumę
/// składników nie jest wyceniana w ccusage — tutaj jest pomijana (używana tylko w kluczu deduplikacji).
/// Model dla starego formatu pochodzi z <c>&lt;root&gt;/config.json</c> (<c>model</c>), domyślnie <c>kimi-for-coding</c>.
/// </remarks>
public sealed class KimiSource : UsageSourceBase
{
    public const string SourceId = "kimi";
    public const string DefaultModel = "kimi-for-coding";

    private const string WireFileName = "wire.jsonl";

    public override string Id => SourceId;
    public override string DisplayName => "Kimi";
    public override string Vendor => "Moonshot AI";
    public override string AccentColor => "#1783FF";

    /// <summary>2: surowa nazwa modelu z logu Kimi Code w kluczu deduplikacji (kandydat cennika).</summary>
    public override int ParserVersion => 2;

    /// <summary>Oddziela w <see cref="UsageEntry.DedupeKey"/> surową nazwę modelu z logu (np. <c>kimi-code/x</c>).</summary>
    private const char LoggedModelSeparator = '\u001f';

    /// <summary>
    /// Najpierw dokładna nazwa z logu (<c>kimi-code/x</c>), potem <c>moonshot/x</c>, <c>kimi/x</c> i <c>x</c>
    /// (ccusage <c>kimi/src/parser.rs</c> <c>model_candidates</c>, #2235). Plan <c>kimi-for-coding</c> kieruje na
    /// model wg daty <see cref="Pricing.ModelAliases"/>.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        List<string> candidates = [];
        var separator = entry.DedupeKey?.LastIndexOf(LoggedModelSeparator) ?? -1;
        if (separator >= 0)
        {
            candidates.Add(entry.DedupeKey![(separator + 1)..]);
        }

        candidates.AddRange([$"moonshot/{entry.Model}", $"kimi/{entry.Model}", entry.Model]);
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    protected override string[] EnvironmentVariables => ["KIMI_DATA_DIR"];

    protected override string[] Executables => ["kimi"];

    protected override IEnumerable<string> DefaultRoots() =>
    [
        Path.Combine(FileUtil.Home, ".kimi"),
        Path.Combine(FileUtil.Home, ".kimi-code")
    ];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var sessions = Path.Combine(root, "sessions");
            foreach (var file in FileUtil.SafeEnumerateFiles(sessions, WireFileName))
            {
                // sessions/<grupa>/<sesja>/wire.jsonl (3 segmenty) lub .../agents/<agent>/wire.jsonl (5 segmentów).
                var depth = Path.GetRelativePath(sessions, file)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                    .Length;
                if (depth is 3 or 5)
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var sessionId = SessionIdFromPath(file.Path);
        string? configModel = null;

        foreach (var line in FileUtil.ReadLines(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 ||
                !(line.Contains("\"token_usage\"", StringComparison.Ordinal) ||
                  line.Contains("\"usage.record\"", StringComparison.Ordinal)))
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
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                UsageEntry? entry;
                if (root.Str("type") == "usage.record")
                {
                    entry = ParseUsageRecord(root, sessionId, diagnostics);
                }
                else
                {
                    entry = ParseStatusUpdate(root, sessionId, diagnostics,
                        () => configModel ??= ReadConfigModel(file.Root));
                }

                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Format Kimi Code: <c>{"type":"usage.record","model":...,"usage":{...},"usageScope":"turn","time":ms}</c>.</summary>
    private static UsageEntry? ParseUsageRecord(JsonElement root, string sessionId, ParseDiagnostics diagnostics)
    {
        // Rekordy "session" to sumy narastające — liczenie ich zdublowałoby tury.
        if (root.Str("usageScope") != "turn")
        {
            return null;
        }

        if (root.Prop("usage") is not { } usage)
        {
            return null;
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Skip("niepoprawne pole usage");
            return null;
        }

        var input = usage.Long("inputOther");
        var output = usage.Long("output");
        var cacheWrite = usage.Long("inputCacheCreation");
        var cacheRead = usage.Long("inputCacheRead");
        if (input + output + cacheWrite + cacheRead == 0)
        {
            return null;
        }

        // "time" to milisekundy.
        var time = root.Prop("time");
        long? timestamp = time.IsNumber() && time!.Value.TryGetInt64(out var millis) && millis > 0 ? millis : null;
        if (timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
            return null;
        }

        var logged = root.Str("model") ?? DefaultModel;
        var model = logged.StartsWith("kimi-code/", StringComparison.Ordinal) ? logged["kimi-code/".Length..] : logged;

        return Build(sessionId, timestamp.Value, model, null, input, output, cacheWrite, cacheRead, 0,
            logged == model ? null : logged);
    }

    /// <summary>Format Kimi CLI: <c>{"timestamp":sekundy,"message":{"type":"StatusUpdate","payload":{"token_usage":{...}}}}</c>.</summary>
    private static UsageEntry? ParseStatusUpdate(JsonElement root, string sessionId, ParseDiagnostics diagnostics, Func<string> model)
    {
        if (root.Str("type") == "metadata")
        {
            return null;
        }

        var message = root.Prop("message");
        if (message is not { ValueKind: JsonValueKind.Object } messageElement || messageElement.Str("type") != "StatusUpdate")
        {
            return null;
        }

        var payload = messageElement.Prop("payload");
        if (payload?.Prop("token_usage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return null;
        }

        var input = usage.Long("input_other");
        var output = usage.Long("output");
        var cacheWrite = usage.Long("input_cache_creation");
        var cacheRead = usage.Long("input_cache_read");
        var sum = input + output + cacheWrite + cacheRead;
        var remainder = Math.Max(0, usage.Long("total") - sum);
        if (sum == 0)
        {
            // Sama nadwyżka total nie jest wyceniana — taki wpis nie wnosi rozłącznych tokenów.
            return null;
        }

        // "timestamp" to sekundy (z ułamkiem).
        var seconds = root.Prop("timestamp").Double();
        if (seconds is not { } value || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            diagnostics.Skip("brak lub zły timestamp");
            return null;
        }

        return Build(sessionId, (long)Math.Truncate(value * 1000), model(), payload.Value.Str("message_id"),
            input, output, cacheWrite, cacheRead, remainder);
    }

    private static UsageEntry Build(string sessionId, long timestamp, string model, string? messageId,
        long input, long output, long cacheWrite, long cacheRead, long remainder, string? loggedModel = null)
    {
        // Jak replay_counts w ccusage: kopia bez output (tylko total) i kopia z output dają ten sam klucz;
        // Deduplicator wybierze pełniejszą (więcej tokenów).
        var (replayOutput, replayRemainder) = output == 0 ? (remainder, 0L) : (output, remainder);
        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = timestamp,
            SessionId = sessionId,
            Project = "kimi",
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = cacheWrite,
            CacheReadTokens = cacheRead,
            DedupeKey = $"{sessionId}:{messageId}:{timestamp}:{model}:{input}:{replayOutput}:{cacheWrite}:{cacheRead}:{replayRemainder}" +
                        (loggedModel is null ? "" : LoggedModelSeparator + loggedModel)
        };
    }

    /// <summary>Identyfikator sesji: katalog sesji (dla Kimi Code — dwa poziomy nad <c>agents/&lt;agent&gt;</c>).</summary>
    internal static string SessionIdFromPath(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var grandParent = parent is null ? null : Path.GetDirectoryName(parent);
        var session = grandParent is not null && Path.GetFileName(grandParent) == "agents"
            ? Path.GetDirectoryName(grandParent)
            : parent;
        var name = session is null ? null : Path.GetFileName(session);
        return string.IsNullOrEmpty(name) ? "unknown" : name;
    }

    private static string ReadConfigModel(string root)
    {
        var path = Path.Combine(root, "config.json");
        try
        {
            if (!File.Exists(path) || !JsonUtil.TryParse(FileUtil.ReadAllText(path), out var document))
            {
                return DefaultModel;
            }

            using (document)
            {
                return document.RootElement.Str("model") ?? DefaultModel;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DefaultModel;
        }
    }
}
