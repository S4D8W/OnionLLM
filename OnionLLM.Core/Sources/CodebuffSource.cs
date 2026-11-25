using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Codebuff (dawniej Manicode) — historie czatów <c>projects/&lt;projekt&gt;/chats/&lt;czat&gt;/chat-messages.json</c>.
/// Katalogi: <c>CODEBUFF_DATA_DIR</c> (lista kanałów) lub <c>~/.config/manicode</c>, <c>manicode-dev</c>,
/// <c>manicode-staging</c>. Katalog może wskazywać kanał albo bezpośrednio jego <c>projects</c>.
/// </summary>
/// <remarks>
/// Zużycie z wiadomości asystenta (<c>variant</c>/<c>role</c> = <c>ai</c>, <c>agent</c>, <c>assistant</c>):
/// kolejno <c>metadata.usage</c>, <c>metadata.codebuff.usage</c> i historia <c>runState</c> (od końca) —
/// pierwsza dodatnia wartość każdego licznika wygrywa (jak w ccusage). Znacznik czasu: <c>timestamp</c>,
/// <c>createdAt</c>, <c>metadata.timestamp</c>, a w ostateczności czas zakodowany w nazwie katalogu czatu.
/// Codebuff zapisuje kredyty, nie USD. Klucz deduplikacji: id wiadomości (lub skład pól) w obrębie czatu.
/// Odstępstwo od ccusage: przy liczeniu w stylu OpenAI (<c>prompt_tokens</c> + <c>prompt_tokens_details.cached_tokens</c>)
/// odejmujemy tokeny cache od wejścia, aby liczniki były rozłączne.
/// Codebuff rozlicza każde żądanie przez własną usługę, więc dostawca wpisu to zawsze <see cref="BillingProvider"/> —
/// trasa obca, bez harmonogramów dostawców (ccusage <c>codebuff/src/parser.rs</c> <c>price_codebuff</c>, #2332).
/// </remarks>
public sealed class CodebuffSource : UsageSourceBase
{
    public const string SourceId = "codebuff";

    /// <summary>Dostawca rozliczający wszystkie żądania Codebuff (trasa obca w cenniku).</summary>
    public const string BillingProvider = "codebuff";

    /// <summary>2: dostawca rozliczenia (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;
    public const string FallbackModel = "codebuff-unknown";

    private static readonly string[] Channels = ["manicode", "manicode-dev", "manicode-staging"];

    public override string Id => SourceId;
    public override string DisplayName => "Codebuff";
    public override string Vendor => "Codebuff";
    public override string AccentColor => "#84CC16";

    protected override string[] EnvironmentVariables => ["CODEBUFF_DATA_DIR"];

    protected override string[] Executables => ["codebuff"];

    protected override IEnumerable<string> DefaultRoots() =>
        Channels.Select(channel => Path.Combine(FileUtil.Home, ".config", channel));

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            var projects = string.Equals(Path.GetFileName(root), "projects", StringComparison.OrdinalIgnoreCase)
                ? root
                : Path.Combine(root, "projects");
            foreach (var file in FileUtil.SafeEnumerateFiles(projects, "chat-messages.json"))
            {
                if (seen.Add(FileUtil.NormalizePath(file)))
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
            diagnostics.Skip("niepoprawny JSON czatu Codebuff");
            return [];
        }

        using (document)
        {
            return ParseChat(document.RootElement, file.Path, diagnostics);
        }
    }

    internal static List<UsageEntry> ParseChat(JsonElement root, string path, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Skip("czat Codebuff nie jest tablicą");
            return entries;
        }

        var (sessionId, project, chatId) = DeriveContext(path);
        var chatTimestamp = ChatIdTimestamp(chatId);
        var ordinal = -1;
        foreach (var message in root.EnumerateArray())
        {
            ordinal++;
            if (message.ValueKind != JsonValueKind.Object || !IsAssistant(message))
            {
                continue;
            }

            var usage = ExtractUsage(message);
            if (!usage.HasSignal)
            {
                continue;
            }

            if ((MessageTimestamp(message) ?? chatTimestamp) is not { } timestamp)
            {
                diagnostics.Skip("rekord Codebuff bez znacznika czasu");
                continue;
            }

            var model = usage.Model ?? FallbackModel;
            var output = usage.Output ?? 0;
            var id = message.Str("id");
            var dedupeKey = id is not null
                ? $"codebuff:{sessionId}:{id}"
                : string.Create(CultureInfo.InvariantCulture,
                    $"codebuff:{sessionId}:{timestamp}:{model}:{ordinal}:{usage.Input}:{output}:{usage.CacheRead}:{usage.CacheWrite}:{usage.Total}");

            entries.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = timestamp,
                SessionId = sessionId,
                Project = project,
                Model = model,
                IsFallbackModel = usage.Model is null,
                InputTokens = usage.Input,
                OutputTokens = output,
                CacheCreationTokens = usage.CacheWrite,
                CacheReadTokens = usage.CacheRead,
                Credits = usage.Credits > 0 ? usage.Credits : null,
                Provider = BillingProvider,
                DedupeKey = dedupeKey
            });
        }

        return entries;
    }

    /// <summary>
    /// Kandydaci cennika (ccusage <c>codebuff_model_candidates</c>): nazwa z logu, potem <c>dostawca/model</c> z dostawcą
    /// wywnioskowanym z nazwy (<c>infer_provider</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model);

    internal static IReadOnlyList<string> ModelCandidates(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        var provider = InferProvider(model);
        return provider is null || model.StartsWith(provider + "/", StringComparison.Ordinal) ? [model] : [model, $"{provider}/{model}"];
    }

    /// <summary>Dostawca z nazwy modelu (ccusage <c>infer_provider</c>); null = nieznany.</summary>
    internal static string? InferProvider(string model)
    {
        var lower = model.ToLowerInvariant();
        bool Starts(params string[] prefixes) => prefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal));
        if (Starts("claude-", "anthropic/", "anthropic."))
        {
            return "anthropic";
        }

        if (Starts("gpt-", "o1", "o3", "o4", "openai/"))
        {
            return "openai";
        }

        if (Starts("gemini", "google/"))
        {
            return "google";
        }

        if (Starts("grok", "xai/"))
        {
            return "xai";
        }

        return Starts("openrouter/") ? "openrouter" : null;
    }

    /// <summary>Sesja = <c>kanał/projekt/czat</c>; kanał to katalog nad <c>projects</c>.</summary>
    private static (string SessionId, string Project, string ChatId) DeriveContext(string path)
    {
        var chatDirectory = Path.GetDirectoryName(path);
        var chatId = NonEmpty(Path.GetFileName(chatDirectory)) ?? "unknown";
        var projectDirectory = Path.GetDirectoryName(Path.GetDirectoryName(chatDirectory));
        var project = NonEmpty(Path.GetFileName(projectDirectory)) ?? "unknown";
        var channel = NonEmpty(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(projectDirectory)))) ?? "manicode";
        return ($"{channel}/{project}/{chatId}", project, chatId);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsAssistant(JsonElement message) =>
        (message.Str("variant") ?? message.Str("role")) is "ai" or "agent" or "assistant";

    internal sealed class Usage
    {
        public string? Model { get; set; }
        public double Credits { get; set; }
        public long Input { get; set; }

        /// <summary>null = licznik wyjścia nie został zapisany (różne od zapisanego zera).</summary>
        public long? Output { get; set; }

        public long CacheWrite { get; set; }
        public long CacheRead { get; set; }
        public long Total { get; set; }

        public bool HasSignal => Input > 0 || Output > 0 || CacheWrite > 0 || CacheRead > 0 || Total > 0 || Credits > 0;

        /// <summary>Uzupełnia brakujące (zerowe) pola wartościami z kolejnego źródła.</summary>
        public void MergeFallback(Usage fallback)
        {
            if (Input == 0)
            {
                Input = fallback.Input;
            }

            Output = PreferRecordedOutput(Output, fallback.Output);
            if (CacheWrite == 0)
            {
                CacheWrite = fallback.CacheWrite;
            }

            if (CacheRead == 0)
            {
                CacheRead = fallback.CacheRead;
            }

            if (Total == 0)
            {
                Total = fallback.Total;
            }

            if (Credits <= 0)
            {
                Credits = fallback.Credits;
            }

            Model ??= fallback.Model;
        }
    }

    private static long? PreferRecordedOutput(long? primary, long? fallback) =>
        primary is > 0 ? primary : fallback ?? primary;

    internal static Usage ExtractUsage(JsonElement message)
    {
        var usage = new Usage();
        if (message.Prop("metadata") is { ValueKind: JsonValueKind.Object } metadata)
        {
            usage.Model = metadata.Str("model");
            usage.MergeFallback(ParseUsageObject(metadata.Prop("usage")));
            usage.MergeFallback(ParseUsageObject(metadata.Path("codebuff", "usage")));
            if (RunStateUsage(metadata) is { } runState)
            {
                usage.MergeFallback(runState);
            }
        }

        var credits = PositiveNumber(message.Prop("credits"));
        if (credits > 0 && usage.Credits <= 0)
        {
            usage.Credits = credits;
        }

        return usage;
    }

    private static Usage? RunStateUsage(JsonElement metadata)
    {
        if (metadata.Path("runState", "sessionState", "mainAgentState", "messageHistory") is not { ValueKind: JsonValueKind.Array } history)
        {
            return null;
        }

        var usage = new Usage();
        var found = false;
        foreach (var item in history.EnumerateArray().Reverse())
        {
            if (item.Str("role") != "assistant" || item.Prop("providerOptions") is not { ValueKind: JsonValueKind.Object } options)
            {
                continue;
            }

            var entry = new Usage();
            entry.MergeFallback(ParseUsageObject(options.Prop("usage")));
            if (options.Prop("codebuff") is { ValueKind: JsonValueKind.Object } codebuff)
            {
                entry.MergeFallback(ParseUsageObject(codebuff.Prop("usage")));
                entry.Model = codebuff.Str("model") ?? entry.Model;
            }

            if (entry.HasSignal || entry.Model is not null)
            {
                found = true;
            }

            usage.MergeFallback(entry);
        }

        return found ? usage : null;
    }

    private static Usage ParseUsageObject(JsonElement? value)
    {
        var usage = new Usage();
        if (value is not { ValueKind: JsonValueKind.Object } record)
        {
            return usage;
        }

        var (input, inputKey) = Pick(record, "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
        usage.Output = new[] { "outputTokens", "output_tokens", "completionTokens", "completion_tokens" }
            .Select(key => UnsignedInteger(record.Prop(key)))
            .Aggregate((long?)null, PreferRecordedOutput);

        var direct = Pick(record, "cacheReadInputTokens", "cache_read_input_tokens").Value;
        var details = Math.Max(
            Pick(record.Prop("promptTokensDetails"), "cachedTokens").Value,
            Pick(record.Prop("prompt_tokens_details"), "cached_tokens").Value);
        usage.CacheRead = Math.Max(direct, details);

        // Wejście w stylu OpenAI (prompt_tokens) zawiera tokeny z prompt_tokens_details.cached_tokens.
        if (inputKey is "promptTokens" or "prompt_tokens" && details > 0 && direct == 0)
        {
            input = Math.Max(0, input - details);
        }

        usage.Input = input;
        usage.CacheWrite = Pick(record, "cacheCreationInputTokens", "cache_creation_input_tokens", "cacheCreationTokens",
            "cache_creation_tokens", "cachedTokensCreated", "cached_tokens_created").Value;
        usage.Total = Pick(record, "totalTokens", "total_tokens", "total").Value;
        usage.Credits = PositiveNumber(record.Prop("credits"));
        usage.Model = record.Str("model");
        return usage;
    }

    /// <summary>Pierwsza dodatnia wartość całkowita spośród kluczy (jak <c>pick_u64</c> w ccusage).</summary>
    private static (long Value, string? Key) Pick(JsonElement? record, params string[] keys)
    {
        if (record is not { ValueKind: JsonValueKind.Object } value)
        {
            return (0, null);
        }

        foreach (var key in keys)
        {
            if (UnsignedInteger(value.Prop(key)) is > 0 and var found)
            {
                return (found, key);
            }
        }

        return (0, null);
    }

    private static long? UnsignedInteger(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var value) && value >= 0 ? value : null;

    private static double PositiveNumber(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var value) && double.IsFinite(value) && value > 0
            ? value
            : 0;

    private static long? MessageTimestamp(JsonElement message) =>
        TimestampValue(message.Prop("timestamp")) ??
        TimestampValue(message.Prop("createdAt")) ??
        TimestampValue(message.Path("metadata", "timestamp"));

    private static long? TimestampValue(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } => element.TimestampMs(),
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var raw) => EpochSecondsOrMillis(raw),
        _ => null
    };

    /// <summary>Jak ccusage: wartości poniżej 1e10 to sekundy, pozostałe — milisekundy.</summary>
    private static long? EpochSecondsOrMillis(long raw) => raw switch
    {
        <= 0 => null,
        < 10_000_000_000 => raw * 1000,
        _ => raw
    };

    /// <summary>Nazwa katalogu czatu, np. <c>2026-01-02T03-04-05.000Z</c> → znacznik czasu.</summary>
    internal static long? ChatIdTimestamp(string chatId)
    {
        var separator = chatId.IndexOf('T');
        if (separator < 0)
        {
            return null;
        }

        var time = chatId[(separator + 1)..].ToCharArray();
        var replaced = 0;
        for (var i = 0; i < time.Length && replaced < 2; i++)
        {
            if (time[i] == '-')
            {
                time[i] = ':';
                replaced++;
            }
        }

        var text = chatId[..separator] + "T" + new string(time);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }
}
