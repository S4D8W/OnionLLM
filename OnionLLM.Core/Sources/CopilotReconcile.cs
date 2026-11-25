using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Uzgadnianie wpisów Copilota z różnych plików — wg ccusage <c>copilot/src/loader.rs:36-142</c> i <c>loader/reconcile.rs</c>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Kopie spanu OTel z kilku plików (ta sama tożsamość lub ponowny eksport spanu chat) liczą się raz — najwcześniejsza kopia.</item>
/// <item>Kopie <c>usage_record</c> z plików kilku sesji liczą się raz — w sesji z <c>sourceSessionId</c>, inaczej o najniższym ID.</item>
/// <item>Każdy <c>usage_record</c> zastępuje najwyżej jeden wiersz OTel tego samego modelu: najpierw po service request id,
/// potem po response id (najbliższy w czasie; ccusage <c>reconcile.rs:6-53</c>, #2108).</item>
/// <item>Przedziały shutdown pary (sesja, model) zostają, a wiersze OTel do ostatniego shutdown są pomijane — chyba że OTel ma
/// więcej wywołań niż shutdowny zgłaszają od pierwszego przedziału z OTel; wtedy od tego przedziału liczy się OTel
/// (proces zakończony bez shutdown, ccusage <c>reconcile.rs:109-141</c>, #1858).</item>
/// <item>Wiersze niewyjaśnionych kredytów są pomijane od pierwszego wiersza OTel sesji, którego nic nie pokrywa.</item>
/// </list>
/// Wiersze bazy <c>session-store.db</c> (poziom 0) nie podlegają uzgadnianiu; sesje z bazy wygrywają później w filtrze poziomów.
/// Źródło deklaruje <see cref="ReconcileNeedsFullHistory"/>, więc decyzja „OTel zamiast shutdown” widzi całą historię
/// (jak w ccusage), a zakres dat raportu jest nakładany dopiero po uzgodnieniu.
/// </remarks>
public sealed partial class CopilotSource
{
    private sealed record OtelRow(int Index, UsageEntry Entry, string? ResponseId, string? ServiceRequestId);

    private sealed record RecordRow(int Index, UsageEntry Entry, string Identity, string? ResponseId, string? ServiceRequestId);

    public override bool ReconcileNeedsFullHistory => true;

    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries)
    {
        var kinds = entries.Select(CopilotKey.KindOf).ToArray();
        if (kinds.All(kind => kind == CopilotEntryKind.Other))
        {
            return entries;
        }

        var keep = new bool[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            keep[index] = kinds[index] == CopilotEntryKind.Other;
        }

        var otel = UniqueOtelRows(entries, kinds);
        var records = UniqueRecordRows(entries, kinds);
        var matches = RecordedOtelCalls(otel, records);
        foreach (var record in records)
        {
            keep[record.Index] = true;
        }

        // Przedziały shutdown vs OTel per (sesja, model).
        var otelCalls = otel
            .GroupBy(row => (row.Entry.SessionId, Model: row.Entry.Model ?? ""))
            .ToDictionary(group => group.Key, group => group.Select(row => row.Entry.TimestampMs).Order().ToList());
        var cutoffs = new Dictionary<(string Session, string Model), long?>();
        var intervalGroups = Enumerable.Range(0, entries.Count)
            .Where(index => kinds[index] == CopilotEntryKind.ShutdownInterval)
            .GroupBy(index => (entries[index].SessionId, Model: entries[index].Model ?? ""));
        foreach (var group in intervalGroups)
        {
            var sorted = group
                .OrderBy(index => entries[index].TimestampMs)
                .ThenBy(index => entries[index].DedupeKey, StringComparer.Ordinal)
                .ToList();
            var start = otelCalls.TryGetValue(group.Key, out var calls) ? ReplacementStart(entries, sorted, calls) : null;
            cutoffs[group.Key] = start switch
            {
                null => entries[sorted[^1]].TimestampMs,
                0 => null,
                _ => entries[sorted[start.Value - 1]].TimestampMs
            };
            for (var position = 0; position < sorted.Count; position++)
            {
                keep[sorted[position]] = start is null || position < start;
            }
        }

        bool SurvivesShutdown(UsageEntry entry) =>
            !cutoffs.TryGetValue((entry.SessionId, entry.Model ?? ""), out var cutoff) || cutoff is null || entry.TimestampMs > cutoff;

        var firstUncovered = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var position = 0; position < otel.Count; position++)
        {
            var row = otel[position];
            var survives = SurvivesShutdown(row.Entry);
            if (survives && !(matches.TryGetValue(position, out var sameSession) && sameSession))
            {
                firstUncovered[row.Entry.SessionId] = Math.Min(firstUncovered.GetValueOrDefault(row.Entry.SessionId, long.MaxValue), row.Entry.TimestampMs);
            }

            keep[row.Index] = survives && !matches.ContainsKey(position);
        }

        // Kredytów sprzed niepokrytego wiersza OTel nic nie wyjaśnia; od niego mogą być w kosztach OTel (ccusage credits.rs:157-158).
        for (var index = 0; index < entries.Count; index++)
        {
            if (kinds[index] == CopilotEntryKind.CreditGap)
            {
                keep[index] = !(firstUncovered.TryGetValue(entries[index].SessionId, out var uncovered) && uncovered <= entries[index].TimestampMs);
            }
        }

        var result = new List<UsageEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            if (keep[index])
            {
                result.Add(entries[index]);
            }
        }

        return result;
    }

    /// <summary>Jeden wiersz na tożsamość spanu (najwcześniejszy, z identyfikatorami żądania z każdej kopii) i na ponownie wyeksportowany span chat.</summary>
    private static List<OtelRow> UniqueOtelRows(IReadOnlyList<UsageEntry> entries, CopilotEntryKind[] kinds)
    {
        var rows = new List<OtelRow>();
        var copies = Enumerable.Range(0, entries.Count)
            .Where(index => kinds[index] == CopilotEntryKind.Otel)
            .GroupBy(index => CopilotKey.Identity(entries[index].DedupeKey!), StringComparer.Ordinal);
        foreach (var group in copies)
        {
            var ordered = group.OrderBy(index => entries[index].TimestampMs).ThenBy(index => index).ToList();
            var keys = ordered.Select(index => entries[index].DedupeKey).ToList();
            rows.Add(new OtelRow(
                ordered[0],
                entries[ordered[0]],
                keys.Select(key => CopilotKey.Field(key, CopilotKey.ResponseId)).FirstOrDefault(id => id is not null),
                keys.Select(key => CopilotKey.Field(key, CopilotKey.ServiceRequestId)).FirstOrDefault(id => id is not null)));
        }

        rows = rows.OrderBy(row => row.Entry.TimestampMs).ThenBy(row => row.Index).ToList();
        var seenChatResponses = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        return rows.Where(row =>
        {
            if (row.ResponseId is null ||
                !row.Entry.DedupeKey!.StartsWith(CopilotKey.OtelPrefix + nameof(OtelKind.ChatSpan) + ":", StringComparison.Ordinal))
            {
                return true;
            }

            var entry = row.Entry;
            var call = $"{row.ResponseId}|{entry.Model}|{entry.InputTokens}|{entry.OutputTokens}|{entry.CacheReadTokens}|{entry.CacheCreationTokens}|{entry.ReasoningTokens}";
            return !IsRepeatedChatSpan(seenChatResponses, call, row.ServiceRequestId);
        }).ToList();
    }

    /// <summary>Jedna kopia na tożsamość rekordu: najpierw w sesji z <c>sourceSessionId</c>, potem o najniższym ID (ccusage <c>reconcile.rs:55-80</c>).</summary>
    private static List<RecordRow> UniqueRecordRows(IReadOnlyList<UsageEntry> entries, CopilotEntryKind[] kinds)
    {
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (kinds[index] != CopilotEntryKind.UsageRecord)
            {
                continue;
            }

            var identity = CopilotKey.Identity(entries[index].DedupeKey!);
            if (!best.TryGetValue(identity, out var kept))
            {
                best[identity] = index;
                order.Add(identity);
            }
            else if (RecordRank(entries[index]).CompareTo(RecordRank(entries[kept])) < 0)
            {
                best[identity] = index;
            }
        }

        return order.Select(identity =>
        {
            var entry = entries[best[identity]];
            return new RecordRow(best[identity], entry, identity,
                CopilotKey.Field(entry.DedupeKey, CopilotKey.ResponseId), CopilotKey.Field(entry.DedupeKey, CopilotKey.ServiceRequestId));
        }).ToList();
    }

    /// <summary>
    /// Pozycja wiersza OTel → czy zastępujący go rekord jest z tej samej sesji. Service request id nazywa jedno żądanie, a response id
    /// VS Code jest wspólny dla tury — dlatego najpierw service request id (ccusage <c>reconcile.rs:6-53</c>).
    /// </summary>
    private static Dictionary<int, bool> RecordedOtelCalls(List<OtelRow> otel, List<RecordRow> records)
    {
        var replaced = new Dictionary<int, bool>();
        if (otel.Count == 0 || records.Count == 0)
        {
            return replaced;
        }

        var rowsById = new Dictionary<(bool ServiceRequest, string Id), List<int>>();
        for (var position = 0; position < otel.Count; position++)
        {
            foreach (var key in new[] { (false, otel[position].ResponseId), (true, otel[position].ServiceRequestId) })
            {
                if (key.Item2 is { } id)
                {
                    if (!rowsById.TryGetValue((key.Item1, id), out var list))
                    {
                        rowsById[(key.Item1, id)] = list = [];
                    }

                    list.Add(position);
                }
            }
        }

        var sorted = records.OrderBy(record => record.Entry.TimestampMs).ThenBy(record => record.Identity, StringComparer.Ordinal).ToList();
        var matched = new bool[sorted.Count];
        foreach (var serviceRequest in new[] { true, false })
        {
            for (var position = 0; position < sorted.Count; position++)
            {
                var record = sorted[position];
                var id = serviceRequest ? record.ServiceRequestId : record.ResponseId;
                if (matched[position] || id is null || !rowsById.TryGetValue((serviceRequest, id), out var candidates))
                {
                    continue;
                }

                var nearest = candidates
                    .Where(index => otel[index].Entry.Model == record.Entry.Model && !replaced.ContainsKey(index))
                    .OrderBy(index => Math.Abs(otel[index].Entry.TimestampMs - record.Entry.TimestampMs))
                    .ThenBy(index => otel[index].Entry.TimestampMs)
                    .ThenBy(index => index)
                    .Select(index => (int?)index)
                    .FirstOrDefault();
                if (nearest is { } found)
                {
                    replaced[found] = otel[found].Entry.SessionId == record.Entry.SessionId;
                    matched[position] = true;
                }
            }
        }

        return replaced;
    }

    /// <summary>
    /// Pierwszy przedział, od którego OTel zastępuje shutdowny: wywołań OTel od pierwszego przedziału z OTel jest więcej, niż shutdowny
    /// zgłaszają żądań. Gdy któraś migawka nie podaje liczby żądań, zostają shutdowny (ccusage <c>reconcile.rs:109-141</c>).
    /// </summary>
    private static int? ReplacementStart(IReadOnlyList<UsageEntry> entries, List<int> sorted, List<long> calls)
    {
        if (sorted.Any(index => CopilotKey.Field(entries[index].DedupeKey, CopilotKey.NoRequestCounts) is not null))
        {
            return null;
        }

        int? start = null;
        var counted = 0;
        long otelCalls = 0;
        long reportedCalls = 0;
        for (var position = 0; position < sorted.Count; position++)
        {
            var entry = entries[sorted[position]];
            var through = CallsThrough(calls, entry.TimestampMs);
            var intervalCalls = through - counted;
            counted = through;
            if (start is null && intervalCalls > 0)
            {
                start = position;
            }

            if (start is not null)
            {
                otelCalls += intervalCalls;
                reportedCalls += CopilotKey.LongField(entry.DedupeKey, CopilotKey.Requests) ?? 0;
            }
        }

        return otelCalls > reportedCalls ? start : null;
    }

    /// <summary>Liczba wywołań z czasem ≤ <paramref name="timestamp"/> (lista posortowana).</summary>
    private static int CallsThrough(List<long> calls, long timestamp)
    {
        int low = 0, high = calls.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (calls[middle] <= timestamp)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
