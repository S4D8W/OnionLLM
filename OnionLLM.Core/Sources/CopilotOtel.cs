using System.Text.Json;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Eksport OpenTelemetry Copilota (<c>${COPILOT_HOME}/otel/**/*.jsonl</c>) — wg ccusage <c>copilot/src/parser/otel.rs</c>.
/// </summary>
/// <remarks>
/// Rodzaje rekordów (od najlepszego): span <c>chat</c> &gt; log inferencji (<c>gen_ai.client.inference.operation.details</c>)
/// &gt; log tury agenta (<c>copilot_chat.agent.turn</c>) &gt; span <c>invoke_agent</c>. Dla tego samego trace / response id
/// emitowany jest tylko najlepszy rodzaj; powtórzone spany chat (ta sama odpowiedź i liczniki) liczone są raz.
/// <c>gen_ai.usage.input_tokens</c> zawiera cache — odejmujemy odczyt i zapis. Model bez sufiksów <c>-1m</c>/<c>-1m-internal</c>
/// (<see cref="ModelNames"/>); <c>gen_ai.response.id</c> i <c>github.copilot.service_request_id</c> trafiają do klucza, aby dopasować
/// wywołania do <c>session.usage_record</c> (<see cref="Reconcile"/>). Kopie spanu w różnych plikach są łączone w <see cref="Reconcile"/>.
/// Uproszczenia względem ccusage: kontekst trace (model/sesja) tylko w obrębie pliku; bez rozliczania „reszty” z total_tokens.
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

    private sealed record OtelCall(
        OtelKind Kind,
        string? TraceId,
        string? SpanId,
        string? ResponseId,
        string? ServiceRequestId,
        string? Model,
        string? Session,
        int SessionPriority,
        long Timestamp,
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite,
        long Reasoning,
        long? Turn,
        int Index);

    private static readonly (string Key, int Priority)[] SessionAttributes =
    [
        ("gen_ai.conversation.id", 3), ("copilot_chat.session_id", 3), ("copilot_chat.chat_session_id", 3), ("session.id", 3),
        ("github.copilot.interaction_id", 2), ("gen_ai.response.id", 1)
    ];

    private static IEnumerable<UsageEntry> ParseOtel(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var calls = new List<OtelCall>();
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
                if (ToCall(document.RootElement, index++, diagnostics) is { } call)
                {
                    calls.Add(call);
                }
            }
        }

        // Kontekst trace: model i sesja z dowolnego rekordu tego samego trace.
        var traceModel = new Dictionary<string, string>();
        var traceSession = new Dictionary<string, (string Session, int Priority)>();
        foreach (var call in calls.Where(call => call.TraceId is not null))
        {
            if (call.Model is not null)
            {
                traceModel.TryAdd(call.TraceId!, call.Model);
            }

            if (call.Session is not null &&
                (!traceSession.TryGetValue(call.TraceId!, out var known) || call.SessionPriority > known.Priority))
            {
                traceSession[call.TraceId!] = (call.Session, call.SessionPriority);
            }
        }

        // Najlepszy rodzaj rekordu per trace i per response id.
        var bestByTrace = new Dictionary<string, OtelKind>();
        var bestByResponse = new Dictionary<string, OtelKind>();
        foreach (var call in calls)
        {
            Keep(bestByTrace, call.TraceId, call.Kind);
            Keep(bestByResponse, call.ResponseId, call.Kind);
        }

        static string? CallKey(OtelCall call) => call.Kind switch
        {
            OtelKind.AgentTurnLog when call.TraceId is not null && call.Turn is not null => $"agent-turn:{call.TraceId}:{call.Turn}",
            OtelKind.AgentTurnLog => null,
            _ when call.TraceId is not null && call.SpanId is not null => $"{call.Kind}:{call.TraceId}:{call.SpanId}",
            _ => null
        };

        // Kopie tego samego wywołania (ten sam klucz) przekazują sobie identyfikatory żądania (ccusage otel.rs:232-252).
        var callIds = new Dictionary<string, (string? Response, string? ServiceRequest)>(StringComparer.Ordinal);
        foreach (var call in calls.OrderBy(call => call.Timestamp).ThenBy(call => call.Index))
        {
            if (CallKey(call) is { } key)
            {
                var known = callIds.GetValueOrDefault(key);
                callIds[key] = (known.Response ?? call.ResponseId, known.ServiceRequest ?? call.ServiceRequestId);
            }
        }

        var seenCallKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenChatResponses = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var call in calls.OrderBy(call => call.Timestamp).ThenBy(call => call.Index))
        {
            if (call.Kind != OtelKind.ChatSpan &&
                (Outranked(bestByTrace, call.TraceId, call.Kind) || Outranked(bestByResponse, call.ResponseId, call.Kind)))
            {
                continue;
            }

            var callKey = CallKey(call);
            var (responseId, serviceRequestId) = callKey is not null ? callIds[callKey] : (call.ResponseId, call.ServiceRequestId);
            var logged = call.Model ?? (call.TraceId is not null && traceModel.TryGetValue(call.TraceId, out var traced) ? traced : "unknown");
            var (model, rawModel) = ModelNames(logged);
            if (call.Kind == OtelKind.ChatSpan && responseId is not null &&
                IsRepeatedChatSpan(seenChatResponses, $"{responseId}|{model}|{call.Input}|{call.Output}|{call.CacheRead}|{call.CacheWrite}|{call.Reasoning}", serviceRequestId))
            {
                continue; // powtórzony span tej samej odpowiedzi
            }

            if (callKey is not null && !seenCallKeys.Add(callKey))
            {
                continue;
            }

            var session = call.Session ??
                          (call.TraceId is not null && traceSession.TryGetValue(call.TraceId, out var tracedSession) ? tracedSession.Session : null) ??
                          call.TraceId ?? "unknown-session";

            yield return new UsageEntry
            {
                Source = SourceId,
                TimestampMs = call.Timestamp,
                SessionId = session,
                Model = model,
                IsFallbackModel = model == "unknown",
                InputTokens = Math.Max(0, call.Input - call.CacheRead - call.CacheWrite),
                OutputTokens = call.Output,
                CacheReadTokens = call.CacheRead,
                CacheCreationTokens = call.CacheWrite,
                ReasoningTokens = call.Reasoning,
                SessionTier = 1,
                DedupeKey = CopilotKey.Compose(CopilotKey.OtelPrefix + (callKey ?? $"{call.Kind}:{session}:{call.Timestamp}:{call.Index}"),
                    (CopilotKey.ResponseId, responseId),
                    (CopilotKey.ServiceRequestId, serviceRequestId),
                    (CopilotKey.RawModel, rawModel))
            };
        }
    }

    /// <summary>
    /// Span chat ponownie wyeksportowany z tym samym response id i licznikami (ccusage #1858). Różne service request id
    /// oznaczają osobne żądania, nawet przy tej samej odpowiedzi i zużyciu (ccusage #2368).
    /// </summary>
    internal static bool IsRepeatedChatSpan(Dictionary<string, List<string?>> seen, string call, string? serviceRequestId)
    {
        if (!seen.TryGetValue(call, out var copies))
        {
            seen[call] = [serviceRequestId];
            return false;
        }

        if (copies.Any(kept => kept is null || serviceRequestId is null || kept == serviceRequestId))
        {
            return true;
        }

        copies.Add(serviceRequestId);
        return false;
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

    private static OtelCall? ToCall(JsonElement record, int index, ParseDiagnostics diagnostics)
    {
        if (record.Prop("attributes") is not { ValueKind: JsonValueKind.Object } attributes)
        {
            return null;
        }

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

        var input = attributes.Long("gen_ai.usage.input_tokens");
        var output = attributes.Long("gen_ai.usage.output_tokens");
        var cacheRead = attributes.Long("gen_ai.usage.cache_read.input_tokens");
        var cacheWrite = FirstPositive(attributes, "gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens");
        var reasoning = FirstPositive(attributes, "gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens");
        if (input + output + cacheRead + cacheWrite == 0)
        {
            return null;
        }

        var timestamp = OtelTimestamp(record);
        if (timestamp is null)
        {
            diagnostics.Skip("rekord OTel bez poprawnego czasu");
            return null;
        }

        var traceId = record.Str("traceId") ?? record.Path("spanContext", "traceId").Str();
        var spanId = record.Str("spanId") ?? record.Path("spanContext", "spanId").Str();
        var session = SessionAttributes
            .Select(attribute => (Value: attributes.Str(attribute.Key), attribute.Priority))
            .Where(candidate => candidate.Value is not null)
            .OrderByDescending(candidate => candidate.Priority)
            .FirstOrDefault();
        var turn = attributes.Prop("turn.index") ?? attributes.Prop("copilot_chat.turn.index");

        return new OtelCall(
            kind,
            traceId,
            spanId,
            attributes.Str("gen_ai.response.id"),
            attributes.Str("github.copilot.service_request_id"),
            attributes.Str("gen_ai.response.model") ?? attributes.Str("gen_ai.request.model"),
            session.Value,
            session.Priority,
            timestamp.Value,
            input,
            output,
            cacheRead,
            cacheWrite,
            reasoning,
            turn is null ? null : turn.Long(),
            index);
    }

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
