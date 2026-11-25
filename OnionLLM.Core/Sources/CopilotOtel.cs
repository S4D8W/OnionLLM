using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Eksport OpenTelemetry Copilota (<c>${COPILOT_HOME}/otel/**/*.jsonl</c>) — wg ccusage <c>copilot/src/parser/otel.rs</c>.
/// </summary>
/// <remarks>
/// Rodzaje rekordów (od najlepszego): span <c>chat</c> &gt; log inferencji (<c>gen_ai.client.inference.operation.details</c>)
/// &gt; log tury agenta (<c>copilot_chat.agent.turn</c>) &gt; span <c>invoke_agent</c>.
/// <c>gen_ai.usage.input_tokens</c> zawiera cache — odejmujemy odczyt i zapis. Model bez sufiksów <c>-1m</c>/<c>-1m-internal</c>
/// (<see cref="ModelNames"/>); <c>gen_ai.response.id</c> i <c>github.copilot.service_request_id</c> służą do dopasowania wywołań
/// do <c>session.usage_record</c>.
/// <para>
/// <see cref="ParseOtel"/> zwraca każde wywołanie pliku osobno (jak ccusage <c>read_otel_calls</c>, <c>otel.rs:183-211</c>), z modelem
/// i sesją rozwiązanymi kontekstem trace tego pliku. Rozstrzyganie między rekordami — jak ccusage <c>reconcile_otel_calls</c>
/// (<c>otel.rs:213-311</c>) na wywołaniach ze wszystkich plików — robi <see cref="ResolveOtelRows"/> w <see cref="Reconcile"/>:
/// kontekst trace (model, sesja) łączony między plikami, kopie tej samej tożsamości, ranking rodzajów per trace / response id
/// i powtórzone spany chat. Dlatego wpis niesie w <see cref="UsageEntry.SourceData"/> trace id, kontekst trace swojego pliku
/// i resztę z <c>total_tokens</c> (<see cref="CopilotKey"/>).
/// </para>
/// <para>
/// Reszta z <c>total_tokens</c> ponad wejście, wyjście i cache (ccusage <c>AdditionalTokens::from_total</c> / <c>replay_counts</c>,
/// <c>otel.rs:406-408</c>): w rekordzie bez <c>output_tokens</c> jest wyjściem „zastępczym”, które staje się wyjściem tylko wtedy,
/// gdy kopia tego samego wywołania z takimi samymi licznikami je potwierdza (#2492, <c>otel.rs:298-303</c>); inaczej jest resztą
/// niesklasyfikowaną. Z reszty rozliczane są tylko tokeny reasoning (ccusage <c>loader.rs:173</c> wycenia je po stawce wyjścia),
/// więc <see cref="UsageEntry.OutputTokens"/> = wyjście + min(reasoning, reszta), a <see cref="UsageEntry.ReasoningTokens"/> są w nim
/// zawarte. Pozostała reszta nie trafia do żadnego licznika (w ccusage jest tylko w sumie tokenów raportu) — <see cref="UsageEntry"/>
/// nie ma na nią pola.
/// </para>
/// </remarks>
public sealed partial class CopilotSource
{
    private enum OtelKind
    {
        ChatSpan = 0,
        InferenceLog = 1,
        AgentTurnLog = 2,
        AgentSummarySpan = 3
    }

    /// <summary>
    /// Wywołanie odczytane z rekordu (ccusage <c>OtelCall</c>, <c>otel.rs:85-101</c>). <c>Input</c> bez cache; <c>Output</c> to wyjście
    /// do porównywania kopii (przy braku <c>output_tokens</c> — reszta z total), <c>Fallback</c> — część <c>Output</c> wzięta z reszty,
    /// <c>Extra</c> — reszta rekordu, który podaje <c>output_tokens</c>.
    /// </summary>
    private sealed record OtelCall(
        OtelKind Kind,
        string? TraceId,
        string? SpanId,
        string? ResponseId,
        string? ServiceRequestId,
        string? Model,
        string? Session,
        long Timestamp,
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite,
        long Reasoning,
        long Extra,
        long Fallback,
        long? Turn,
        int Index);

    /// <summary>Kontekst trace: pierwszy niepusty model i sesja o najwyższym priorytecie atrybutu (ccusage <c>otel.rs:58-77</c>).</summary>
    private sealed class TraceContext
    {
        public string? Model { get; private set; }

        public string? Session { get; private set; }

        public int Priority { get; private set; }

        public void Add(string? model, string? session, int priority)
        {
            Model ??= model;
            if (session is not null && priority > Priority)
            {
                Session = session;
                Priority = priority;
            }
        }
    }

    /// <summary>Dane wpisu OTel z <see cref="UsageEntry.SourceData"/> potrzebne przy uzgadnianiu między plikami.</summary>
    private sealed record OtelInfo(
        OtelKind Kind,
        string? TraceId,
        string? ResponseId,
        string? ServiceRequestId,
        bool NoOwnModel,
        bool NoOwnSession,
        string? TraceModel,
        string? TraceSession,
        int TraceSessionPriority,
        long Extra,
        long Fallback)
    {
        public static OtelInfo From(UsageEntry entry) => new(
            (OtelKind)(CopilotKey.LongField(entry, CopilotKey.OtelKind) ?? 0),
            CopilotKey.Field(entry, CopilotKey.TraceId),
            CopilotKey.Field(entry, CopilotKey.ResponseId),
            CopilotKey.Field(entry, CopilotKey.ServiceRequestId),
            CopilotKey.Field(entry, CopilotKey.NoOwnModel) is not null,
            CopilotKey.Field(entry, CopilotKey.NoOwnSession) is not null,
            CopilotKey.Field(entry, CopilotKey.TraceModel),
            CopilotKey.Field(entry, CopilotKey.TraceSession),
            (int)(CopilotKey.LongField(entry, CopilotKey.TraceSessionPriority) ?? 0),
            CopilotKey.LongField(entry, CopilotKey.ExtraTotal) ?? 0,
            CopilotKey.LongField(entry, CopilotKey.FallbackOutput) ?? 0);

        public string? ToData(string? rawModel) => CopilotKey.Data(
            (CopilotKey.ResponseId, ResponseId),
            (CopilotKey.ServiceRequestId, ServiceRequestId),
            (CopilotKey.RawModel, rawModel),
            (CopilotKey.OtelKind, ((int)Kind).ToString(CultureInfo.InvariantCulture)),
            (CopilotKey.TraceId, TraceId),
            (CopilotKey.NoOwnModel, NoOwnModel ? "1" : null),
            (CopilotKey.NoOwnSession, NoOwnSession ? "1" : null),
            (CopilotKey.TraceModel, TraceModel),
            (CopilotKey.TraceSession, TraceSession),
            (CopilotKey.TraceSessionPriority, TraceSession is null ? null : TraceSessionPriority.ToString(CultureInfo.InvariantCulture)),
            (CopilotKey.ExtraTotal, Extra > 0 ? Extra.ToString(CultureInfo.InvariantCulture) : null),
            (CopilotKey.FallbackOutput, Fallback > 0 ? Fallback.ToString(CultureInfo.InvariantCulture) : null));
    }

    /// <summary>
    /// Wywołanie w trakcie uzgadniania (ccusage <c>CopilotUsageCandidate</c>): model i sesja po kontekście trace ze wszystkich plików,
    /// identyfikatory żądania i wyjście zastępcze uzupełniane z kopii.
    /// </summary>
    private sealed class OtelCandidate
    {
        public required int Index { get; init; }

        public required UsageEntry Entry { get; init; }

        public required OtelInfo Info { get; init; }

        public required string Model { get; init; }

        public required string? RawModel { get; init; }

        public required string Session { get; init; }

        /// <summary>Wyjście do porównywania kopii.</summary>
        public required long Matching { get; init; }

        public string? ResponseId { get; set; }

        public string? ServiceRequestId { get; set; }

        public long Fallback { get; set; }

        /// <summary>Model i liczniki zgłoszone przez rekord — kopie jednego wywołania mają je równe (ccusage <c>reported_counts</c>, <c>otel.rs:171-181</c>).</summary>
        public string Counts =>
            $"{Model}|{Entry.InputTokens}|{Matching}|{Entry.CacheCreationTokens}|{Entry.CacheReadTokens}|{Entry.ReasoningTokens}|{Info.Extra}";

        public UsageEntry Build() => Entry with
        {
            Model = Model,
            IsFallbackModel = Model == "unknown",
            SessionId = Session,
            OutputTokens = OtelOutput(Matching, Info.Extra, Fallback, Entry.ReasoningTokens),
            SourceData = (Info with { ResponseId = ResponseId, ServiceRequestId = ServiceRequestId, Fallback = Fallback }).ToData(RawModel)
        };
    }

    private static readonly (string Key, int Priority)[] SessionAttributes =
    [
        ("gen_ai.conversation.id", 3), ("copilot_chat.session_id", 3), ("copilot_chat.chat_session_id", 3), ("session.id", 3),
        ("github.copilot.interaction_id", 2), ("gen_ai.response.id", 1)
    ];

    /// <summary>Atrybuty zużycia — rekord z którymkolwiek z nich wnosi kontekst trace (ccusage <c>is_otel_usage_envelope</c>, <c>otel.rs:337-353</c>).</summary>
    private static readonly string[] UsageAttributes =
    [
        "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens",
        "gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens",
        "gen_ai.usage.total_tokens", "gen_ai.usage.total.token_count"
    ];

    private static IEnumerable<UsageEntry> ParseOtel(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var calls = new List<OtelCall>();
        var contexts = new Dictionary<string, TraceContext>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in FileUtil.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Contains("\"attributes\"", StringComparison.Ordinal) || !line.Contains("gen_ai.usage", StringComparison.Ordinal))
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
                if (record.Prop("attributes") is not { ValueKind: JsonValueKind.Object } attributes ||
                    !UsageAttributes.Any(key => attributes.Prop(key) is not null))
                {
                    continue;
                }

                // Kontekst trace z każdego rekordu zużycia, także takiego, który sam nie jest wywołaniem (ccusage otel.rs:355-370).
                if (TraceIdOf(record) is { } trace)
                {
                    if (!contexts.TryGetValue(trace, out var context))
                    {
                        contexts[trace] = context = new TraceContext();
                    }

                    var (session, priority) = BestSession(attributes);
                    context.Add(ModelOf(attributes), session, priority);
                }

                if (ToCall(record, attributes, index++, diagnostics) is { } call)
                {
                    calls.Add(call);
                }
            }
        }

        var file = FileTag(path);
        foreach (var call in calls)
        {
            yield return ToEntry(call, call.TraceId is null ? null : contexts.GetValueOrDefault(call.TraceId), file);
        }
    }

    /// <summary>Wpis z modelem i sesją z kontekstu trace pliku; <see cref="ResolveOtelRows"/> uzupełnia je kontekstem z innych plików.</summary>
    private static UsageEntry ToEntry(OtelCall call, TraceContext? context, string file)
    {
        var (model, rawModel) = ModelNames(call.Model ?? context?.Model ?? "unknown");
        var info = new OtelInfo(
            call.Kind,
            call.TraceId,
            call.ResponseId,
            call.ServiceRequestId,
            call.Model is null,
            call.Session is null,
            context?.Model,
            context?.Session,
            context?.Priority ?? 0,
            call.Extra,
            call.Fallback);
        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = call.Timestamp,
            SessionId = call.Session ?? context?.Session ?? call.TraceId ?? "unknown-session",
            Model = model,
            IsFallbackModel = model == "unknown",
            InputTokens = call.Input,
            OutputTokens = OtelOutput(call.Output, call.Extra, call.Fallback, call.Reasoning),
            CacheReadTokens = call.CacheRead,
            CacheCreationTokens = call.CacheWrite,
            ReasoningTokens = call.Reasoning,
            SessionTier = 1,
            // Rekord bez tożsamości wywołania liczy się osobno w każdym pliku (ccusage nie łączy go z innymi, otel.rs:266-268).
            DedupeKey = CopilotKey.OtelPrefix + (CallKey(call) ?? $"{call.Kind}:{file}:{call.Index}"),
            SourceData = info.ToData(rawModel)
        };
    }

    /// <summary>
    /// Tożsamość wywołania (ccusage <c>CallKey</c>, <c>parser.rs:32-46</c>, <c>otel.rs:636-653</c>): spany (chat i <c>invoke_agent</c>)
    /// i logi inferencji po trace i span id, tury agenta po trace i numerze tury.
    /// </summary>
    private static string? CallKey(OtelCall call) => call.Kind switch
    {
        _ when call.TraceId is null => null,
        OtelKind.AgentTurnLog => call.Turn is null ? null : $"agent-turn:{call.TraceId}:{call.Turn}",
        _ when call.SpanId is null => null,
        OtelKind.InferenceLog => $"log:{call.TraceId}:{call.SpanId}",
        _ => $"span:{call.TraceId}:{call.SpanId}"
    };

    /// <summary>
    /// Wyjście wpisu: potwierdzone wyjście (<paramref name="matching"/> minus wyjście zastępcze) plus tokeny reasoning mieszczące się
    /// w reszcie z total (ccusage <c>otel.rs:294-303</c>, <c>loader.rs:173</c>).
    /// </summary>
    private static long OtelOutput(long matching, long extra, long fallback, long reasoning) =>
        matching - fallback + Math.Min(reasoning, Math.Max(extra, fallback));

    /// <summary>Krótki, stały identyfikator pliku do kluczy wywołań bez tożsamości.</summary>
    private static string FileTag(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];

    /// <summary>
    /// Wiersze OTel po uzgodnieniu wywołań ze wszystkich plików (ccusage <c>reconcile_otel_calls</c>, <c>otel.rs:213-311</c>),
    /// w kolejności czasu. Wpisy mają model i sesję z kontekstu trace wszystkich plików oraz wyjście po potwierdzeniu przez kopie.
    /// </summary>
    /// <remarks>
    /// Kontekst trace jest łączony jak w ccusage (pierwszy model, sesja o najwyższym priorytecie), ale pierwszeństwo ma kontekst
    /// z pliku samego rekordu, a pozostałe pliki liczą się w kolejności czasu wpisów (ccusage: w kolejności ścieżek plików) — różnica
    /// tylko przy sprzecznych modelach jednego trace w różnych plikach. Kontekst z rekordu, który sam nie jest wywołaniem, przechodzi
    /// do innych plików tylko przez wpisy tego samego trace z jego pliku.
    /// </remarks>
    private static List<OtelRow> ResolveOtelRows(IReadOnlyList<UsageEntry> entries, CopilotEntryKind[] kinds)
    {
        var indices = Enumerable.Range(0, entries.Count)
            .Where(index => kinds[index] == CopilotEntryKind.Otel)
            .OrderBy(index => entries[index].TimestampMs)
            .ThenBy(index => index)
            .ToList();
        var infos = indices.ToDictionary(index => index, index => OtelInfo.From(entries[index]));

        // Kontekst trace ze wszystkich plików (ccusage otel.rs:214-225).
        var contexts = new Dictionary<string, TraceContext>(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            var info = infos[index];
            if (info.TraceId is { } trace)
            {
                if (!contexts.TryGetValue(trace, out var context))
                {
                    contexts[trace] = context = new TraceContext();
                }

                context.Add(info.TraceModel, info.TraceSession, info.TraceSessionPriority);
            }
        }

        var calls = indices.Select(index => Candidate(index, entries[index], infos[index], contexts)).ToList();

        // Kopie tej samej tożsamości: zostaje najwcześniejsza, z identyfikatorami żądania z każdej kopii; wyjście zastępcze
        // zmniejsza kopia o tym samym modelu i licznikach (ccusage otel.rs:231-280).
        var kept = new List<OtelCandidate>();
        foreach (var group in calls.GroupBy(call => call.Entry.DedupeKey!, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var copy in group)
            {
                first.ResponseId ??= copy.ResponseId;
                first.ServiceRequestId ??= copy.ServiceRequestId;
                if (copy.Counts == first.Counts)
                {
                    first.Fallback = Math.Min(first.Fallback, copy.Fallback);
                }
            }

            kept.Add(first);
        }

        // Najlepszy rodzaj rekordu per trace i per response id (ccusage otel.rs:476-511, 562-577).
        var bestByTrace = new Dictionary<string, OtelKind>(StringComparer.Ordinal);
        var bestByResponse = new Dictionary<string, OtelKind>(StringComparer.Ordinal);
        foreach (var call in kept)
        {
            Keep(bestByTrace, call.Info.TraceId, call.Info.Kind);
            Keep(bestByResponse, call.ResponseId, call.Info.Kind);
        }

        var repeated = RepeatedChatSpans(kept);
        return kept
            .Where(call => call.Info.Kind == OtelKind.ChatSpan
                ? !repeated.Contains(call)
                : !Outranked(bestByTrace, call.Info.TraceId, call.Info.Kind) && !Outranked(bestByResponse, call.ResponseId, call.Info.Kind))
            .Select(call => new OtelRow(call.Index, call.Build(), call.ResponseId, call.ServiceRequestId))
            .ToList();
    }

    /// <summary>Model i sesja wywołania: własne atrybuty, potem kontekst trace pliku rekordu, potem innych plików (ccusage <c>otel.rs:103-115</c>).</summary>
    private static OtelCandidate Candidate(int index, UsageEntry entry, OtelInfo info, Dictionary<string, TraceContext> contexts)
    {
        var context = info.TraceId is null ? null : contexts.GetValueOrDefault(info.TraceId);
        var ownModel = info.NoOwnModel ? null : CopilotKey.Field(entry, CopilotKey.RawModel) ?? entry.Model;
        var (model, rawModel) = ModelNames(ownModel ?? info.TraceModel ?? context?.Model ?? "unknown");
        var traceSession = info.TraceSession is not null && info.TraceSessionPriority >= (context?.Priority ?? 0)
            ? info.TraceSession
            : context?.Session;
        return new OtelCandidate
        {
            Index = index,
            Entry = entry,
            Info = info,
            Model = model,
            RawModel = rawModel,
            Session = (info.NoOwnSession ? null : entry.SessionId) ?? traceSession ?? info.TraceId ?? "unknown-session",
            // Wpis z wyjściem zastępczym nie miał output_tokens — do porównań liczy się reszta (replay_counts).
            Matching = info.Fallback > 0 ? info.Fallback : entry.OutputTokens - Math.Min(entry.ReasoningTokens, info.Extra),
            ResponseId = info.ResponseId,
            ServiceRequestId = info.ServiceRequestId,
            Fallback = info.Fallback
        };
    }

    /// <summary>
    /// Spany chat ponownie wyeksportowane z tym samym response id, modelem i licznikami (ccusage #1858) — zostaje najwcześniejszy
    /// i przejmuje najmniejsze wyjście zastępcze kopii. Różne service request id oznaczają osobne żądania, nawet przy tej samej
    /// odpowiedzi i zużyciu (ccusage #2368, <c>otel.rs:513-560</c>).
    /// </summary>
    private static HashSet<OtelCandidate> RepeatedChatSpans(List<OtelCandidate> calls)
    {
        var copies = new Dictionary<string, List<OtelCandidate>>(StringComparer.Ordinal);
        var repeated = new HashSet<OtelCandidate>();
        foreach (var call in calls)
        {
            if (call.Info.Kind != OtelKind.ChatSpan || call.ResponseId is null)
            {
                continue;
            }

            var key = call.ResponseId + "|" + call.Counts;
            if (!copies.TryGetValue(key, out var kept))
            {
                copies[key] = kept = [];
            }

            var same = kept.FirstOrDefault(copy =>
                copy.ServiceRequestId is null || call.ServiceRequestId is null || copy.ServiceRequestId == call.ServiceRequestId);
            if (same is null)
            {
                kept.Add(call);
                continue;
            }

            same.Fallback = Math.Min(same.Fallback, call.Fallback);
            repeated.Add(call);
        }

        return repeated;
    }

    private static void Keep(Dictionary<string, OtelKind> best, string? id, OtelKind kind)
    {
        if (id is not null && (!best.TryGetValue(id, out var current) || kind < current))
        {
            best[id] = kind;
        }
    }

    private static bool Outranked(Dictionary<string, OtelKind> best, string? id, OtelKind kind) =>
        id is not null && best.TryGetValue(id, out var current) && current < kind;

    private static OtelCall? ToCall(JsonElement record, JsonElement attributes, int index, ParseDiagnostics diagnostics)
    {
        var isSpan = IsSpan(record);
        var operation = attributes.Str("gen_ai.operation.name");
        var name = record.Str("name");
        var eventName = attributes.Str("event.name");
        var body = record.Str("body") ?? record.Str("_body");

        OtelKind kind;
        if (isSpan && (operation == "chat" || name?.StartsWith("chat ", StringComparison.Ordinal) == true))
        {
            kind = OtelKind.ChatSpan;
        }
        else if (!isSpan && (eventName == "gen_ai.client.inference.operation.details" || body?.StartsWith("GenAI inference:", StringComparison.Ordinal) == true))
        {
            kind = OtelKind.InferenceLog;
        }
        else if (!isSpan && (eventName == "copilot_chat.agent.turn" || body?.StartsWith("copilot_chat.agent.turn", StringComparison.Ordinal) == true))
        {
            kind = OtelKind.AgentTurnLog;
        }
        else if (isSpan && (operation == "invoke_agent" || name?.StartsWith("invoke_agent ", StringComparison.Ordinal) == true))
        {
            kind = OtelKind.AgentSummarySpan;
        }
        else
        {
            return null;
        }

        var cacheRead = attributes.Long("gen_ai.usage.cache_read.input_tokens");
        var cacheWrite = FirstPositive(attributes, "gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens");
        var input = Math.Max(0, attributes.Long("gen_ai.usage.input_tokens") - cacheRead - cacheWrite);
        var output = attributes.Long("gen_ai.usage.output_tokens");
        var reasoning = FirstPositive(attributes, "gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens");
        var total = FirstPositive(attributes, "gen_ai.usage.total_tokens", "gen_ai.usage.total.token_count");
        var counted = input + output + cacheRead + cacheWrite;
        var remainder = Math.Max(0, total - counted);
        if (counted + remainder == 0)
        {
            return null;
        }

        var timestamp = OtelTimestamp(record);
        if (timestamp is null)
        {
            diagnostics.Skip("rekord OTel bez poprawnego czasu");
            return null;
        }

        // replay_counts: bez output_tokens reszta służy do porównania kopii, a rozliczana jest dopiero po potwierdzeniu.
        var (matching, extra) = output == 0 ? (remainder, 0L) : (output, remainder);
        var (session, _) = BestSession(attributes);
        var turn = attributes.Prop("turn.index") ?? attributes.Prop("copilot_chat.turn.index");

        return new OtelCall(
            kind,
            TraceIdOf(record),
            record.Str("spanId") ?? record.Path("spanContext", "spanId").Str(),
            attributes.Str("gen_ai.response.id"),
            attributes.Str("github.copilot.service_request_id"),
            ModelOf(attributes),
            session,
            timestamp.Value,
            input,
            matching,
            cacheRead,
            cacheWrite,
            reasoning,
            extra,
            matching - output,
            turn is null ? null : turn.Long(),
            index);
    }

    private static string? TraceIdOf(JsonElement record) => record.Str("traceId") ?? record.Path("spanContext", "traceId").Str();

    private static string? ModelOf(JsonElement attributes) => attributes.Str("gen_ai.response.model") ?? attributes.Str("gen_ai.request.model");

    private static (string? Session, int Priority) BestSession(JsonElement attributes) => SessionAttributes
        .Select(attribute => (Session: attributes.Str(attribute.Key), attribute.Priority))
        .Where(candidate => candidate.Session is not null)
        .OrderByDescending(candidate => candidate.Priority)
        .FirstOrDefault();

    private static bool IsSpan(JsonElement record)
    {
        if (record.Str("type") is { } type)
        {
            return type == "span";
        }

        return record.Str("name") is not null &&
               (record.Str("spanId") is not null || record.Str("traceId") is not null || record.Prop("startTime") is not null ||
                record.Prop("endTime") is not null || record.Prop("duration") is not null || record.Prop("kind") is not null);
    }

    private static long FirstPositive(JsonElement attributes, params string[] keys) =>
        keys.Select(key => attributes.Long(key)).FirstOrDefault(value => value > 0);

    /// <summary>Czas: endTime/startTime/hrTime/time jako [sekundy, nanosekundy], potem timestamp/observedTimestamp, timeUnixNano.</summary>
    private static long? OtelTimestamp(JsonElement record)
    {
        foreach (var name in new[] { "endTime", "startTime", "hrTime", "_hrTime", "time" })
        {
            if (record.Prop(name) is { ValueKind: JsonValueKind.Array } parts && parts.GetArrayLength() >= 2)
            {
                var seconds = ((JsonElement?)parts[0]).Long();
                var nanos = ((JsonElement?)parts[1]).Long();
                if (seconds > 0)
                {
                    return seconds * 1000 + nanos / 1_000_000;
                }
            }
        }

        foreach (var name in new[] { "timestamp", "observedTimestamp" })
        {
            var raw = record.Prop(name).Long();
            if (raw > 0)
            {
                return raw switch
                {
                    >= 100_000_000_000_000_000 => raw / 1_000_000,
                    >= 100_000_000_000_000 => raw / 1_000,
                    _ => JsonUtil.FromEpochNumber(raw)
                };
            }

            if (record.Prop(name) is { ValueKind: JsonValueKind.String } text && ((JsonElement?)text).TimestampMs() is { } parsed)
            {
                return parsed;
            }
        }

        var unixNano = record.Prop("timeUnixNano").Long();
        return unixNano > 0 ? unixNano / 1_000_000 : null;
    }
}
