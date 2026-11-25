using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Gemini CLI — logi czatów JSON / JSONL w <c>~/.gemini/tmp/**</c> (zwykle <c>&lt;projekt&gt;/chats/*.json(l)</c>).
/// Katalogi: <c>GEMINI_DATA_DIR</c> (lista) lub <c>~/.gemini/tmp</c>.
/// </summary>
/// <remarks>
/// Odwzorowanie adaptera <c>gemini</c> z ccusage:
/// <list type="bullet">
/// <item>Dokument JSON z tablicą <c>messages</c> — wpis per wiadomość <c>type == "gemini"</c> z <c>tokens</c>;
/// czas z wiadomości, potem <c>startTime</c>/<c>lastUpdated</c> sesji, potem mtime pliku.</item>
/// <item>JSONL — rekordy <c>type == "gemini"</c> (ten sam <c>id</c> w pliku = aktualizacja, wygrywa ostatni)
/// oraz rekordy ze <c>stats</c> / <c>result.stats</c> (per model z <c>stats.models</c> albo zbiorczo).</item>
/// </list>
/// Tokeny: <c>input</c> bywa łącznie z <c>cached</c> — odejmujemy cache (dla wiadomości tylko gdy <c>total</c>
/// potwierdza, że input zawiera cache; dla <c>stats</c> zawsze). <c>tool</c> doliczamy do wejścia.
/// <c>thoughts</c> są poza <c>output</c>, a ccusage wycenia je stawką wyjścia — dlatego
/// <see cref="UsageEntry.OutputTokens"/> = output + thoughts, a <see cref="UsageEntry.ReasoningTokens"/> = thoughts.
/// </remarks>
public sealed class GeminiSource : UsageSourceBase
{
    public const string SourceId = "gemini";
    public const string FallbackModel = "unknown";

    private static readonly string[] InputKeys = ["input", "prompt", "input_tokens", "prompt_tokens"];
    private static readonly string[] OutputKeys = ["output", "candidates", "output_tokens", "candidates_tokens"];
    private static readonly string[] CachedKeys = ["cached", "cached_tokens"];
    private static readonly string[] ThoughtsKeys = ["thoughts", "reasoning", "thoughts_tokens", "reasoning_tokens"];
    private static readonly string[] ToolKeys = ["tool", "tool_tokens"];
    private static readonly string[] TotalKeys = ["total", "total_tokens"];

    public override string Id => SourceId;
    public override string DisplayName => "Gemini CLI";
    public override string Vendor => "Google";
    public override string AccentColor => "#C2489B";

    protected override string[] EnvironmentVariables => ["GEMINI_DATA_DIR"];

    protected override string[] Executables => ["gemini"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".gemini", "tmp")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var files = FileUtil.SafeEnumerateFiles(root, "*.json")
                .Concat(FileUtil.SafeEnumerateFiles(root, "*.jsonl"))
                .Where(IsLogFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
            {
                yield return SourceFile.Create(file, root);
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var project = ProjectFromPath(file.Path, file.Root);
        return Path.GetExtension(file.Path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? ParseJsonl(file.Path, project, diagnostics, cancellationToken)
            : ParseJson(file.Path, project, diagnostics);
    }

    private static bool IsLogFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cały dokument JSON (sesja z <c>messages</c>, pojedynczy rekord albo <c>stats</c>).</summary>
    private static List<UsageEntry> ParseJson(string path, string? project, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        string text;
        try
        {
            text = FileUtil.ReadAllText(path);
        }
        catch (IOException)
        {
            diagnostics.Skip("nie można odczytać pliku");
            return entries;
        }

        if (!JsonUtil.TryParse(text, out var document))
        {
            diagnostics.Skip("niepoprawny JSON");
            return entries;
        }

        using (document)
        {
            var root = document.RootElement;
            // logs.json (tablica promptów) i inne nie-obiekty nie zawierają zużycia.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            long? fileTime = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() : null;
            var sessionId = SessionIdOf(root) ?? Path.GetFileNameWithoutExtension(path);
            project ??= root.Str("projectHash");

            if (root.Prop("messages") is { ValueKind: JsonValueKind.Array } messages)
            {
                var sessionTime = root.Prop("startTime").TimestampMs() ?? root.Prop("lastUpdated").TimestampMs() ?? fileTime;
                foreach (var message in messages.EnumerateArray())
                {
                    if (IsGeminiRecord(message) &&
                        DirectEntry(message, null, sessionId, project, sessionTime, diagnostics) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }

                return entries;
            }

            if (IsGeminiRecord(root))
            {
                if (DirectEntry(root, null, sessionId, project, fileTime, diagnostics) is { } entry)
                {
                    entries.Add(entry);
                }

                return entries;
            }

            entries.AddRange(StatsEntries(root, root.Str("model"), sessionId, project, fileTime, diagnostics));
        }

        return entries;
    }

    private static List<UsageEntry> ParseJsonl(string path, string? project, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        string? currentModel = null;
        var entries = new List<UsageEntry>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in FileUtil.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
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
                var record = document.RootElement;
                if (record.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                sessionId = SessionIdOf(record) ?? sessionId;
                project ??= record.Str("projectHash");
                currentModel = record.Str("model") ?? currentModel;

                if (IsGeminiRecord(record))
                {
                    if (DirectEntry(record, currentModel, sessionId, project, null, diagnostics) is not { } entry)
                    {
                        continue;
                    }

                    // Gemini dopisuje kolejne wersje tej samej wiadomości — zostaje ostatnia.
                    if (record.Str("id") is { } id)
                    {
                        if (indexById.TryGetValue(id, out var index))
                        {
                            entries[index] = entry;
                        }
                        else
                        {
                            indexById[id] = entries.Count;
                            entries.Add(entry);
                        }
                    }
                    else
                    {
                        entries.Add(entry);
                    }

                    continue;
                }

                entries.AddRange(StatsEntries(record, currentModel, sessionId, project, null, diagnostics));
            }
        }

        return entries;
    }

    private static string? SessionIdOf(JsonElement record) => record.Str("sessionId") ?? record.Str("session_id");

    /// <summary>Dyskryminator <c>type</c> porównywany dokładnie (bez przycinania), jak w ccusage.</summary>
    private static bool IsGeminiRecord(JsonElement record) =>
        record.Prop("type") is { ValueKind: JsonValueKind.String } type && type.GetString() == "gemini";

    private static long? ResolveTime(JsonElement record, long? fallback, ParseDiagnostics diagnostics)
    {
        var timestamp = record.Prop("timestamp").TimestampMs() ?? record.Prop("created_at").TimestampMs() ?? fallback;
        if (timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
        }

        return timestamp;
    }

    private static UsageEntry? DirectEntry(JsonElement record, string? modelHint, string sessionId, string? project,
        long? fallbackTime, ParseDiagnostics diagnostics)
    {
        if (ReadTokens(record.Prop("tokens"), diagnostics) is not { } tokens)
        {
            return null;
        }

        var model = record.Str("model") ?? modelHint;
        if (Build(model, tokens, NormalizeSessionInput) is not { } usage)
        {
            return null;
        }

        if (ResolveTime(record, fallbackTime, diagnostics) is not { } timestamp)
        {
            return null;
        }

        var messageId = record.Str("id");
        return ToEntry(usage, model!, sessionId, project, timestamp, messageId is null ? null : $"{sessionId}:{messageId}");
    }

    private static IEnumerable<UsageEntry> StatsEntries(JsonElement record, string? modelHint, string sessionId, string? project,
        long? fallbackTime, ParseDiagnostics diagnostics)
    {
        var stats = record.Prop("stats") ?? record.Path("result", "stats");
        if (stats is not { ValueKind: JsonValueKind.Object } statsElement)
        {
            return [];
        }

        var usages = new List<(string Model, Usage Usage)>();
        if (statsElement.Prop("models") is { ValueKind: JsonValueKind.Object } models)
        {
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object &&
                    ReadTokens(model.Value.Prop("tokens"), diagnostics) is { } modelTokens &&
                    Build(model.Name, modelTokens, SubtractCachedOverlap) is { } modelUsage)
                {
                    usages.Add((model.Name, modelUsage));
                }
            }
        }

        if (usages.Count == 0)
        {
            var fallbackModel = modelHint ?? FallbackModel;
            if (ReadTokens(statsElement, diagnostics) is { } tokens && Build(fallbackModel, tokens, SubtractCachedOverlap) is { } usage)
            {
                usages.Add((fallbackModel, usage));
            }
        }

        if (usages.Count == 0 || ResolveTime(record, fallbackTime, diagnostics) is not { } timestamp)
        {
            return [];
        }

        return usages.Select(item => ToEntry(item.Usage, item.Model, sessionId, project, timestamp, null) with
        {
            IsFallbackModel = item.Model == FallbackModel && modelHint is null
        }).ToList();
    }

    private static UsageEntry ToEntry(Usage usage, string model, string sessionId, string? project, long timestamp, string? dedupeKey) => new()
    {
        Source = SourceId,
        TimestampMs = timestamp,
        SessionId = sessionId,
        Project = project,
        Model = model,
        InputTokens = usage.Input,
        CacheReadTokens = usage.CacheRead,
        OutputTokens = usage.Output + usage.Thoughts,
        ReasoningTokens = usage.Thoughts,
        DedupeKey = dedupeKey
    };

    internal readonly record struct Tokens(long Input, long Output, long Cached, long Thoughts, long Tool, long? Total);

    private readonly record struct Usage(long Input, long Output, long CacheRead, long Thoughts);

    private static Tokens? ReadTokens(JsonElement? value, ParseDiagnostics diagnostics)
    {
        if (value is not { ValueKind: JsonValueKind.Object } tokens)
        {
            return null;
        }

        var input = Number(tokens, InputKeys);
        var output = Number(tokens, OutputKeys);
        var cached = Number(tokens, CachedKeys);
        var thoughts = Number(tokens, ThoughtsKeys);
        var tool = Number(tokens, ToolKeys);
        var total = TotalKeys.Select(key => tokens.Prop(key)).FirstOrDefault(element => element is not null) is { } totalElement
            ? NumberValue(totalElement)
            : null;

        // Liczniki poza zakresem (np. 2^64-1) — rekord odrzucany jak w ccusage.
        if (input is null || output is null || cached is null || thoughts is null || tool is null ||
            !TryAdd(input.Value, output.Value, cached.Value, thoughts.Value, tool.Value))
        {
            diagnostics.Skip("liczniki tokenów poza zakresem");
            return null;
        }

        return new Tokens(input.Value, output.Value, cached.Value, thoughts.Value, tool.Value, total);
    }

    private static bool TryAdd(params long[] values)
    {
        try
        {
            long sum = 0;
            foreach (var value in values)
            {
                sum = checked(sum + value);
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Pierwszy alias z wartością liczbową; brak = 0, liczba poza zakresem = null.</summary>
    private static long? Number(JsonElement tokens, string[] keys)
    {
        foreach (var key in keys)
        {
            if (tokens.Prop(key) is { ValueKind: JsonValueKind.Number } element)
            {
                return NumberValue(element);
            }
        }

        return 0;
    }

    private static long? NumberValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (element.TryGetInt64(out var integer))
        {
            return Math.Max(0, integer);
        }

        if (element.TryGetDouble(out var real) && double.IsFinite(real))
        {
            if (real <= 0)
            {
                return 0;
            }

            return real < long.MaxValue ? (long)Math.Truncate(real) : null;
        }

        return null;
    }

    private static Usage? Build(string? model, Tokens tokens, Func<Tokens, (long Input, long CacheRead)> normalize)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var (inputWithoutCache, cacheRead) = normalize(tokens);
        var input = inputWithoutCache + tokens.Tool;
        // Rekordy z samym "total" nie mają kubełka w OnionLLM (ccusage liczy je tylko do sumy) — pomijamy.
        if (input == 0 && tokens.Output == 0 && cacheRead == 0 && tokens.Thoughts == 0)
        {
            return null;
        }

        return new Usage(input, tokens.Output, cacheRead, tokens.Thoughts);
    }

    /// <summary><c>stats</c>: <c>input</c> zawsze zawiera część z cache.</summary>
    private static (long, long) SubtractCachedOverlap(Tokens tokens)
    {
        var overlap = Math.Min(tokens.Input, tokens.Cached);
        return (tokens.Input - overlap, tokens.Cached);
    }

    /// <summary>
    /// Wiadomości: cache odejmujemy tylko, gdy <c>total</c> = input + output + thoughts + tool
    /// (czyli input zawiera cache), a nie równa się sumie z cache.
    /// </summary>
    private static (long, long) NormalizeSessionInput(Tokens tokens)
    {
        var inclusive = tokens.Input + tokens.Output + tokens.Thoughts + tokens.Tool;
        var exclusive = inclusive + tokens.Cached;
        if (tokens.Cached > 0 && tokens.Total == inclusive && tokens.Total != exclusive)
        {
            return SubtractCachedOverlap(tokens);
        }

        return (tokens.Input, tokens.Cached);
    }

    /// <summary><c>&lt;root&gt;/&lt;projekt&gt;/chats/plik</c> → <c>projekt</c>; plik w podkatalogu → nazwa podkatalogu.</summary>
    private static string? ProjectFromPath(string path, string root)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return null;
        }

        if (string.Equals(Path.GetFileName(directory), "chats", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory);
        }

        if (directory is null ||
            string.Equals(FileUtil.NormalizePath(directory), FileUtil.NormalizePath(root), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
