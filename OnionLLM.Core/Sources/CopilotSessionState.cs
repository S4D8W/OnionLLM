using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Plik <c>session-state/&lt;id&gt;/events.jsonl</c> — wg ccusage <c>copilot/src/parser/session_state.rs</c>,
/// <c>loader/reconcile.rs</c> (przedziały) i <c>loader/credits.rs</c> (kredyty).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>session.usage_record</c> — jedno wywołanie we własnym czasie; tożsamość <c>(sourceSessionId, sequence)</c> wspólna dla kopii
/// w plikach różnych sesji.</item>
/// <item><c>session.shutdown</c> — sumy narastające per model; każda kolejna migawka to przedział (różnica względem poprzedniej),
/// datowany własnym czasem. Shutdowny po pierwszym usage_record (lub ze znacznikami <c>usageAccountingWatermarks</c>) są pomijane,
/// bo liczą te same wywołania.</item>
/// <item><c>totalNanoAiu</c> całej sesji (shutdown i <c>session.usage_checkpoint</c>) — reszta niewyjaśniona przez przedziały
/// i potwierdzone rekordy trafia do wiersza <c>unknown</c> bez tokenów.</item>
/// </list>
/// Sesja = jeden plik, więc przedziały i kredyty liczone są przy parsowaniu na pełnej historii sesji (niezależnie od zakresu dat);
/// uzgodnienie z OTel odbywa się w <see cref="Reconcile"/>.
/// </remarks>
public sealed partial class CopilotSource
{
    private const string ShutdownEvent = "session.shutdown";
    private const string CheckpointEvent = "session.usage_checkpoint";
    private const string UsageRecordEvent = "session.usage_record";
    private const string UnattributedModel = "unknown";

    /// <summary>Liczniki z <c>usage</c> (surowe — <c>Input</c> zawiera cache).</summary>
    private readonly record struct SessionUsage(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning)
    {
        public bool IsEmpty => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0 && Reasoning == 0;

        public long UncachedInput => Math.Max(0, Input - CacheRead - CacheWrite);

        public static SessionUsage Read(JsonElement usage) => new(
            usage.Long("inputTokens"), usage.Long("outputTokens"), usage.Long("cacheReadTokens"),
            usage.Long("cacheWriteTokens"), usage.Long("reasoningTokens"));
    }

    private sealed record ShutdownSnapshot(long Timestamp, string Model, string? Logged, SessionUsage Usage, long Requests, long? NanoAiu, string Key);

    private sealed record CreditSnapshot(long Timestamp, bool IsShutdown, long? TotalNanoAiu, long? ReceiptWatermark, string Key);

    private sealed record UsageRecordCall(
        long Timestamp, string? Source, long? Sequence, string? EventId, string? ResponseId, string? ServiceRequestId,
        string Model, string? Logged, SessionUsage Usage, long? NanoAiu)
    {
        public string Key { get; init; } = "";
    }

    /// <summary>Przedział między migawkami shutdown (wartości już po odjęciu poprzedniej migawki).</summary>
    private sealed record ShutdownInterval(
        long Timestamp, string Model, string? Logged, long Input, long Output, long CacheRead, long CacheWrite, long Reasoning,
        long Requests, long? NanoAiu, string Key, bool NoRequestCounts)
    {
        public bool HasUsage => Input > 0 || Output > 0 || CacheRead > 0 || CacheWrite > 0 || Reasoning > 0 || Requests > 0 || NanoAiu > 0;
    }

    private static IEnumerable<UsageEntry> ParseEvents(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileName(Path.GetDirectoryName(path))?.Trim();
        if (string.IsNullOrEmpty(sessionId))
        {
            return [];
        }

        string? cwd = null;
        var shutdowns = new List<ShutdownSnapshot>();
        var credits = new List<CreditSnapshot>();
        var records = new List<UsageRecordCall>();
        // ccusage session_state.rs:204-262: od pierwszego rekordu (lub znacznika watermark) shutdowny liczą wywołania z rekordów.
        var coveredByRecords = false;
        var unprovenReceiptCoverage = false;

        foreach (var line in FileUtil.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCandidate = line.Contains(ShutdownEvent, StringComparison.Ordinal) ||
                              line.Contains(CheckpointEvent, StringComparison.Ordinal) ||
                              line.Contains(UsageRecordEvent, StringComparison.Ordinal);
            var isStart = cwd is null && line.Contains("\"session.start\"", StringComparison.Ordinal);
            if (!isCandidate && !isStart)
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
                var type = root.Str("type");
                if (type == "session.start")
                {
                    cwd ??= root.Path("data", "context", "cwd").Str() ?? root.Path("data", "cwd").Str();
                    continue;
                }

                if (type is not (ShutdownEvent or CheckpointEvent or UsageRecordEvent))
                {
                    continue;
                }

                var timestamp = root.Prop("timestamp").TimestampMs();
                var data = root.Prop("data") is { ValueKind: JsonValueKind.Object } dataObject ? dataObject : (JsonElement?)null;
                var eventId = root.Str("id");

                if (type == UsageRecordEvent)
                {
                    if (timestamp is null)
                    {
                        if (data?.Prop("usage") is { ValueKind: JsonValueKind.Object })
                        {
                            diagnostics.Skip("brak lub zły timestamp");
                        }

                        continue;
                    }

                    if (ReadUsageRecord(data, eventId, timestamp.Value) is { } record)
                    {
                        coveredByRecords = true;
                        unprovenReceiptCoverage |= record.Source != sessionId;
                        records.Add(record);
                    }

                    continue;
                }

                var isShutdown = type == ShutdownEvent;
                var metrics = data?.Prop("modelMetrics") is { ValueKind: JsonValueKind.Object } metricsObject ? metricsObject : (JsonElement?)null;
                var sessionNano = NanoAiu(data?.Prop("totalNanoAiu"));
                var carriesUsage = data?.Prop("totalNanoAiu") is { ValueKind: not JsonValueKind.Null } ||
                                   (metrics?.EnumerateObject().Any(model => model.Value.Prop("usage") is not null || model.Value.Prop("requests") is not null) ?? false);

                // NOTE ccusage #2108: watermarki mogą wskazywać rekordy innej sesji — wtedy migawka też pokrywa się z rekordami.
                var watermarks = data?.Prop("usageAccountingWatermarks") is { ValueKind: JsonValueKind.Object } watermarkObject
                    ? watermarkObject.EnumerateObject().Select(property => (property.Name, Value: UnsignedInteger(property.Value))).ToList()
                    : [];
                long? receiptWatermark = null;
                if (watermarks.FirstOrDefault(watermark => watermark.Name == sessionId) is { Value: > 0 } local &&
                    watermarks.All(watermark => watermark.Name == sessionId || watermark.Value == 0))
                {
                    receiptWatermark = local.Value;
                }

                coveredByRecords |= watermarks.Any(watermark => watermark.Value > 0);
                if (coveredByRecords && (receiptWatermark is null || timestamp is null))
                {
                    unprovenReceiptCoverage = true;
                    continue;
                }

                if (timestamp is null)
                {
                    if (carriesUsage)
                    {
                        diagnostics.Skip("brak lub zły timestamp");
                    }

                    continue;
                }

                string SnapshotKey(long? total) => eventId is null
                    ? $"{CopilotKey.CreditsPrefix}{sessionId}:{(isShutdown ? "true" : "false")}:{timestamp.Value}:{total?.ToString(CultureInfo.InvariantCulture) ?? "None"}"
                    : $"{CopilotKey.CreditsPrefix}{sessionId}:{eventId}";

                if (!isShutdown || receiptWatermark is not null)
                {
                    if (isShutdown || sessionNano is not null)
                    {
                        credits.Add(new CreditSnapshot(timestamp.Value, isShutdown, sessionNano, receiptWatermark, SnapshotKey(sessionNano)));
                    }

                    continue;
                }

                var modelsReportCredits = true;
                foreach (var model in metrics?.EnumerateObject().ToList() ?? [])
                {
                    var (name, logged) = ModelNames(model.Name);
                    var requests = model.Value.Path("requests", "count").Long();
                    if (model.Value.Prop("usage") is not { ValueKind: JsonValueKind.Object } usageElement)
                    {
                        continue;
                    }

                    var usage = SessionUsage.Read(usageElement);
                    // ccusage session_state.rs:302 — wiersz z samą liczbą żądań (bez tokenów) też jest zużyciem.
                    if (name.Length == 0 || (usage.IsEmpty && requests == 0))
                    {
                        continue;
                    }

                    var modelNano = NanoAiu(model.Value.Prop("totalNanoAiu"));
                    modelsReportCredits &= modelNano is not null;
                    var key = eventId is null
                        ? $"{CopilotKey.ShutdownPrefix}{sessionId}:{DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value):yyyy-MM-ddTHH:mm:ss.fffZ}:{name}:" +
                          $"{usage.Input}:{usage.Output}:{usage.CacheRead}:{usage.CacheWrite}:{usage.Reasoning}:{requests}"
                        : $"{CopilotKey.ShutdownPrefix}{sessionId}:{eventId}:{name}";
                    shutdowns.Add(new ShutdownSnapshot(timestamp.Value, name, logged, usage, requests, modelNano, key));
                }

                var total = modelsReportCredits ? sessionNano : null;
                credits.Add(new CreditSnapshot(timestamp.Value, true, total, null, SnapshotKey(total)));
            }
        }

        if (coveredByRecords && (unprovenReceiptCoverage || !credits.Any(snapshot => snapshot.ReceiptWatermark is not null)))
        {
            credits.Clear();
        }

        var project = ProjectName(cwd);
        var recordCalls = UniqueRecords(KeyRecords(records, sessionId));
        var (intervals, attributed) = ShutdownIntervals(shutdowns);
        var gaps = CreditGaps(sessionId, credits, attributed, recordCalls);

        var entries = new List<UsageEntry>();
        entries.AddRange(intervals.Select(interval => new UsageEntry
        {
            Source = SourceId,
            TimestampMs = interval.Timestamp,
            SessionId = sessionId,
            Model = interval.Model,
            Project = project,
            ProjectPath = cwd,
            InputTokens = interval.Input,
            OutputTokens = interval.Output,
            CacheReadTokens = interval.CacheRead,
            CacheCreationTokens = interval.CacheWrite,
            ReasoningTokens = interval.Reasoning,
            RecordedCostUsd = interval.NanoAiu * NanoAiuToUsd,
            Credits = interval.NanoAiu / NanoAiuPerCredit,
            // ccusage loader.rs:184-189 (#2365): tylko przedział z dokładnie jednym żądaniem może wejść w próg długiego kontekstu.
            IsAggregate = interval.Requests != 1,
            SessionTier = 1,
            DedupeKey = interval.Key,
            SourceData = CopilotKey.Data(
                (CopilotKey.Requests, interval.Requests.ToString(CultureInfo.InvariantCulture)),
                (CopilotKey.NoRequestCounts, interval.NoRequestCounts ? "1" : null),
                (CopilotKey.RawModel, interval.Logged))
        }));
        entries.AddRange(recordCalls.Select(record => new UsageEntry
        {
            Source = SourceId,
            TimestampMs = record.Timestamp,
            SessionId = sessionId,
            Model = record.Model,
            Project = project,
            ProjectPath = cwd,
            InputTokens = record.Usage.UncachedInput,
            OutputTokens = record.Usage.Output,
            CacheReadTokens = record.Usage.CacheRead,
            CacheCreationTokens = record.Usage.CacheWrite,
            ReasoningTokens = record.Usage.Reasoning,
            RecordedCostUsd = record.NanoAiu * NanoAiuToUsd,
            Credits = record.NanoAiu / NanoAiuPerCredit,
            SessionTier = 1,
            DedupeKey = record.Key,
            SourceData = CopilotKey.Data(
                (CopilotKey.SourceSession, record.Source),
                (CopilotKey.ResponseId, record.ResponseId),
                (CopilotKey.ServiceRequestId, record.ServiceRequestId),
                (CopilotKey.RawModel, record.Logged))
        }));
        entries.AddRange(gaps.Select(gap => new UsageEntry
        {
            Source = SourceId,
            TimestampMs = gap.Snapshot.Timestamp,
            SessionId = sessionId,
            Model = UnattributedModel,
            IsFallbackModel = true,
            Project = project,
            ProjectPath = cwd,
            RecordedCostUsd = gap.NanoAiu * NanoAiuToUsd,
            Credits = gap.NanoAiu / NanoAiuPerCredit,
            SessionTier = 1,
            DedupeKey = gap.Snapshot.Key
        }));
        return entries.OrderBy(entry => entry.TimestampMs).ThenBy(entry => entry.DedupeKey, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourceData, StringComparer.Ordinal).ToList();
    }

    private static UsageRecordCall? ReadUsageRecord(JsonElement? data, string? eventId, long timestamp)
    {
        if (data?.Prop("usage") is not { ValueKind: JsonValueKind.Object } usage || usage.Str("model") is not { } logged)
        {
            return null;
        }

        var (model, raw) = ModelNames(logged);
        if (model.Length == 0)
        {
            return null;
        }

        var source = usage.Path("accounting", "sourceSessionId").Str();
        var sequence = usage.Path("accounting", "sequence").Long();
        var hasAccounting = source is not null && sequence > 0;
        return new UsageRecordCall(
            timestamp,
            hasAccounting ? source : null,
            hasAccounting ? sequence : null,
            eventId,
            usage.Str("apiCallId"),
            usage.Str("serviceRequestId"),
            model,
            raw,
            SessionUsage.Read(usage),
            NanoAiu(usage.Path("copilotUsage", "totalNanoAiu")));
    }

    /// <summary>Klucze rekordów (ccusage <c>session_state.rs:405-443</c>): <c>(sourceSessionId, sequence)</c>, inaczej id zdarzenia lub pozycja w pliku.</summary>
    private static List<UsageRecordCall> KeyRecords(List<UsageRecordCall> records, string sessionId) =>
        records.Select((record, index) => record with
        {
            Key = record.Source is not null && record.Sequence is not null
                ? $"{CopilotKey.UsageRecordPrefix}{record.Source}:{record.Sequence}"
                : record.EventId is not null
                    ? $"{CopilotKey.UsageRecordPrefix}{sessionId}:event:{record.EventId}"
                    : $"{CopilotKey.UsageRecordPrefix}{sessionId}:line:{index}"
        }).ToList();

    /// <summary>Rekord dostarczony dwa razy w tym samym pliku liczy się raz (pierwsza kopia).</summary>
    private static List<UsageRecordCall> UniqueRecords(List<UsageRecordCall> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return records.Where(record => seen.Add(record.Key)).ToList();
    }

    /// <summary>
    /// Migawki shutdown → przedziały per model (ccusage <c>reconcile.rs:143-216</c>): pierwsza bez zmian, każda kolejna jako różnica.
    /// Zwraca też kredyty przypisane przez przedziały w chwili każdego shutdown (do bilansu kredytów sesji).
    /// </summary>
    private static (List<ShutdownInterval> Intervals, Dictionary<long, long> Attributed) ShutdownIntervals(List<ShutdownSnapshot> snapshots)
    {
        // Powtórzona migawka (ten sam klucz) — zostaje najpóźniejsza.
        var chosen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (!chosen.TryGetValue(snapshots[index].Key, out var previous) || snapshots[previous].Timestamp <= snapshots[index].Timestamp)
            {
                chosen[snapshots[index].Key] = index;
            }
        }

        var unique = chosen.Values.Order().Select(index => snapshots[index]).ToList();
        var intervals = new List<ShutdownInterval>();
        var attributed = new Dictionary<long, long>();
        foreach (var group in unique.GroupBy(snapshot => snapshot.Model, StringComparer.Ordinal))
        {
            var sorted = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var noRequestCounts = sorted.Any(snapshot => snapshot.Requests == 0);
            ShutdownSnapshot? previous = null;
            foreach (var current in sorted)
            {
                var interval = previous is null
                    ? new ShutdownInterval(current.Timestamp, current.Model, current.Logged, current.Usage.UncachedInput, current.Usage.Output,
                        current.Usage.CacheRead, current.Usage.CacheWrite, current.Usage.Reasoning, current.Requests, current.NanoAiu, current.Key,
                        noRequestCounts)
                    : new ShutdownInterval(current.Timestamp, current.Model, current.Logged,
                        Math.Max(0, current.Usage.UncachedInput - previous.Usage.UncachedInput),
                        Math.Max(0, current.Usage.Output - previous.Usage.Output),
                        Math.Max(0, current.Usage.CacheRead - previous.Usage.CacheRead),
                        Math.Max(0, current.Usage.CacheWrite - previous.Usage.CacheWrite),
                        Math.Max(0, current.Usage.Reasoning - previous.Usage.Reasoning),
                        Math.Max(0, current.Requests - previous.Requests),
                        current.NanoAiu is { } now && previous.NanoAiu is { } before ? Math.Max(0, now - before) : null,
                        current.Key,
                        noRequestCounts);
                previous = current;
                attributed[current.Timestamp] = attributed.GetValueOrDefault(current.Timestamp) + (interval.NanoAiu ?? 0);
                if (interval.HasUsage)
                {
                    intervals.Add(interval);
                }
            }
        }

        return (intervals, attributed);
    }

    private sealed record ReceiptCredit(long Timestamp, long Sequence, long? NanoAiu);

    /// <summary>
    /// Kredyty sesji niewyjaśnione przez przedziały shutdown i potwierdzone rekordy (ccusage <c>loader/credits.rs:86-174</c>, #1824).
    /// Wymaga checkpointu i pełnych sum na każdym shutdown; reszta trwała w kolejnych migawkach trafia do pierwszej z nich.
    /// </summary>
    private static List<(CreditSnapshot Snapshot, long NanoAiu)> CreditGaps(
        string sessionId, List<CreditSnapshot> credits, Dictionary<long, long> attributed, List<UsageRecordCall> records)
    {
        var gaps = new List<(CreditSnapshot, long)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = credits.Where(snapshot => seen.Add(snapshot.Key)).ToList();
        if (snapshots.All(snapshot => snapshot.IsShutdown) ||
            snapshots.Any(snapshot => snapshot.IsShutdown && snapshot.TotalNanoAiu is null))
        {
            return gaps;
        }

        snapshots = snapshots.OrderBy(snapshot => snapshot.Timestamp).ThenBy(snapshot => snapshot.IsShutdown).ToList();
        IReadOnlyList<long> receiptCredits = [];
        if (snapshots.Any(snapshot => snapshot.ReceiptWatermark is not null))
        {
            var ledger = ReceiptLedger(sessionId, records);
            if (ledger.Count == 0 || CoveredReceiptCredits(snapshots, ledger) is not { } amounts)
            {
                return gaps;
            }

            receiptCredits = amounts;
        }

        var lastShutdown = snapshots.FindLastIndex(snapshot => snapshot.IsShutdown);
        long sessionTotal = 0;
        long attributedTotal = 0;
        var points = new List<(CreditSnapshot Snapshot, long Unexplained)>();
        for (var position = 0; position < snapshots.Count; position++)
        {
            var snapshot = snapshots[position];
            if (!snapshot.IsShutdown && position < lastShutdown)
            {
                continue;
            }

            sessionTotal = Math.Max(sessionTotal, snapshot.TotalNanoAiu ?? 0);
            if (snapshot.IsShutdown && snapshot.ReceiptWatermark is null)
            {
                attributedTotal += attributed.GetValueOrDefault(snapshot.Timestamp);
            }

            var receipted = position < receiptCredits.Count ? receiptCredits[position] : 0;
            points.Add((snapshot, Math.Max(0, Math.Max(0, sessionTotal - attributedTotal) - receipted)));
        }

        // Reszta liczy się tylko, jeśli trwa we wszystkich późniejszych migawkach.
        var lasting = long.MaxValue;
        for (var index = points.Count - 1; index >= 0; index--)
        {
            lasting = Math.Min(lasting, points[index].Unexplained);
            points[index] = (points[index].Snapshot, lasting);
        }

        long reported = 0;
        foreach (var (snapshot, value) in points)
        {
            var gap = Math.Max(0, value - reported);
            reported = Math.Max(reported, value);
            if (gap > 0)
            {
                gaps.Add((snapshot, gap));
            }
        }

        return gaps;
    }

    /// <summary>Narastające kredyty rekordów własnej sesji po numerze sekwencji; luka w numeracji lub brak kwoty → brak sumy.</summary>
    private static List<ReceiptCredit> ReceiptLedger(string sessionId, List<UsageRecordCall> records)
    {
        var calls = records
            .Where(record => record.Source == sessionId && record.Sequence is not null)
            .Select(record => new ReceiptCredit(record.Timestamp, record.Sequence!.Value, record.NanoAiu))
            .OrderBy(call => call.Sequence)
            .ToList();
        long? total = 0;
        for (var index = 0; index < calls.Count; index++)
        {
            total = total is { } sum && calls[index].NanoAiu is { } amount && calls[index].Sequence == index + 1 ? sum + amount : null;
            calls[index] = calls[index] with { NanoAiu = total };
        }

        return calls;
    }

    /// <summary>Kredyty potwierdzone rekordami do znacznika każdej migawki (ccusage <c>credits.rs:44-81</c>); null = nie da się zweryfikować.</summary>
    private static List<long>? CoveredReceiptCredits(List<CreditSnapshot> snapshots, List<ReceiptCredit> calls)
    {
        for (var index = 1; index < calls.Count; index++)
        {
            if (calls[index].Timestamp < calls[index - 1].Timestamp)
            {
                return null;
            }
        }

        long previousWatermark = 0;
        long previousTotal = 0;
        var amounts = new List<long>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.ReceiptWatermark is not { } watermark)
            {
                if (previousWatermark != 0 || snapshot.Timestamp >= calls[0].Timestamp)
                {
                    return null;
                }

                amounts.Add(0);
                continue;
            }

            if (snapshot.TotalNanoAiu is not { } total || watermark - 1 >= calls.Count)
            {
                return null;
            }

            var callIndex = (int)(watermark - 1);
            var call = calls[callIndex];
            if (watermark < previousWatermark || total < previousTotal || call.Timestamp > snapshot.Timestamp ||
                (callIndex + 1 < calls.Count && calls[callIndex + 1].Timestamp <= snapshot.Timestamp) ||
                call.NanoAiu is not { } amount || amount > total)
            {
                return null;
            }

            previousWatermark = watermark;
            previousTotal = total;
            amounts.Add(amount);
        }

        return amounts;
    }

    /// <summary>Nano-AIU: skończona, nieujemna liczba, zaokrąglona (ccusage <c>session_state.rs:445-449</c>).</summary>
    private static long? NanoAiu(JsonElement? element) =>
        element.Double() is { } value && double.IsFinite(value) && value >= 0 ? (long)Math.Round(value) : null;

    /// <summary>Liczba całkowita bez znaku (jak <c>Value::as_u64</c>); inne wartości → null.</summary>
    private static long? UnsignedInteger(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) && value >= 0 ? value : null;
}
