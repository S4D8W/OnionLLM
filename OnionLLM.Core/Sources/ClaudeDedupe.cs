using OnionLLM.Core.Models;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Opis wpisu Claude Code. Tożsamość w <see cref="UsageEntry.DedupeKey"/>: ID wiadomości (advisor: <c>msg:advisor:N</c>),
/// <c>requestId</c>, sesja efektywna (<c>sessionId</c> z linii albo ze ścieżki), timestamp i klucz odpowiedzi (łańcuch bez
/// <c>requestId</c>). Flagi sidechain i podziału cache 5 min/1 h są w <see cref="UsageEntry.SourceData"/>.
/// Identyczne opisy łączy <see cref="Storage.Deduplicator"/> (regułą <see cref="ClaudeDedupe.Comparer"/>);
/// pozostałe dopasowania (kopie między sesjami, bloki jednej odpowiedzi, replaye sidechain) robi
/// <see cref="ClaudeDedupe"/> w <see cref="ClaudeSource.Reconcile"/>.
/// </summary>
internal sealed record ClaudeDedupeKey(
    string MessageId,
    string? RequestId,
    string Session,
    long TimestampMs,
    string? Response,
    bool IsSidechain,
    bool HasCacheSplit)
{
    private const char Separator = '';

    /// <summary>Wartość <see cref="UsageEntry.DedupeKey"/>.</summary>
    public string Format() => string.Join(Separator,
        MessageId, RequestId ?? "", Session, TimestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture), Response ?? "");

    /// <summary>Wartość <see cref="UsageEntry.SourceData"/>: <c>s</c>/<c>m</c> (sidechain / wątek główny) i <c>c</c>/<c>-</c> (podział cache).</summary>
    public string Data => (IsSidechain ? "s" : "m") + (HasCacheSplit ? "c" : "-");

    /// <summary>Początek <see cref="UsageEntry.DedupeKey"/> wspólny dla wszystkich kopii wiadomości: ID wiadomości z separatorem.</summary>
    public static string? MessagePrefix(UsageEntry entry)
    {
        var separator = entry.DedupeKey?.IndexOf(Separator) ?? -1;
        return separator > 0 ? entry.DedupeKey![..(separator + 1)] : null;
    }

    public static ClaudeDedupeKey? Parse(UsageEntry entry)
    {
        if (entry.DedupeKey is not { } text)
        {
            return null;
        }

        var parts = text.Split(Separator);
        if (parts.Length != 5 || parts[0].Length == 0 ||
            !long.TryParse(parts[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timestamp))
        {
            return null;
        }

        var flags = entry.SourceData is { Length: 2 } data ? data : "m-";
        return new ClaudeDedupeKey(parts[0], parts[1].Length == 0 ? null : parts[1], parts[2], timestamp,
            parts[4].Length == 0 ? null : parts[4], flags[0] == 's', flags[1] == 'c');
    }
}

/// <summary>
/// Deduplikacja wpisów Claude Code — przeniesienie <c>claude/src/dedupe.rs:push_deduped_entry</c>:
/// <list type="bullet">
/// <item>z <c>requestId</c>: ta sama para (wiadomość, żądanie) także między sesjami (#1765);</item>
/// <item>bez <c>requestId</c>: (wiadomość, sesja, timestamp) albo (wiadomość, sesja, odpowiedź) z łańcucha (#1799, #2255);
/// rekord pasujący do dwóch slotów łączy je;</item>
/// <item>replay sidechain (<c>/btw</c>): ta sama wiadomość w tej samej sesji pasuje mimo innego <c>requestId</c>,
/// gdy jedna ze stron jest sidechain; kopia spoza sidechain wygrywa (#913).</item>
/// </list>
/// Kolejność wyboru: kopia spoza sidechain, więcej tokenów, szybkość (fast &gt; standard &gt; brak), podział cache,
/// zapisany <c>costUSD</c>; przy remisie zostaje wcześniejszy wpis (<c>dedupe.rs:462-475</c>).
/// </summary>
internal sealed class ClaudeDedupe
{
    private readonly record struct Bucket(char Kind, string Message, string? Request, string? Session, long Timestamp, string? Response);

    private readonly record struct Slot(int Index, string? Alias);

    private sealed record Item(UsageEntry Entry, ClaudeDedupeKey? Key)
    {
        public string? Message => Key?.MessageId;
        public string? Request => Key?.RequestId;
        public string Session => Key?.Session ?? Entry.SessionId;
        public bool IsSidechain => IsSidechainEntry(Entry);
        public bool HasCacheSplit => Key?.HasCacheSplit ?? false;
    }

    private readonly List<Item> _deduped = [];
    private readonly Dictionary<Bucket, List<Slot>> _buckets = [];
    private readonly Dictionary<int, int> _merged = [];

    /// <summary>Reguła wyboru między duplikatami (wynik &gt; 0, gdy pierwszy wpis jest lepszy).</summary>
    public static IComparer<UsageEntry> Comparer { get; } = Comparer<UsageEntry>.Create((left, right) =>
    {
        var leftSplit = ClaudeDedupeKey.Parse(left)?.HasCacheSplit ?? false;
        var rightSplit = ClaudeDedupeKey.Parse(right)?.HasCacheSplit ?? false;
        return Replaces(left, leftSplit, right, rightSplit) ? 1 : Replaces(right, rightSplit, left, leftSplit) ? -1 : 0;
    });

    /// <summary>Sidechain wyrażony przez <see cref="UsageEntry.Preference"/> (0 = sidechain, 1 = wątek główny).</summary>
    public static bool IsSidechainEntry(UsageEntry entry) => entry.Preference < 1;

    public static List<UsageEntry> Apply(IEnumerable<UsageEntry> entries)
    {
        var dedupe = new ClaudeDedupe();
        foreach (var entry in entries)
        {
            dedupe.Push(entry);
        }

        return dedupe.Survivors();
    }

    public void Push(UsageEntry entry)
    {
        var item = new Item(entry, ClaudeDedupeKey.Parse(entry));
        if (item.Key is not { } key)
        {
            _deduped.Add(item);
            return;
        }

        var message = key.MessageId;
        var exact = Exact(item);
        var response = ResponseBucket(item);

        int? existing;
        if (key.RequestId is null)
        {
            var byExact = MatchMessage(exact, message);
            var byResponse = response is { } responseBucket ? MatchMessage(responseBucket, message) : null;
            existing = byExact is { } left && byResponse is { } right && left != right
                ? MergeSlots(left, right)
                : byExact ?? byResponse;
        }
        else
        {
            existing = MatchMessage(exact, message);
        }

        if (existing is { } found)
        {
            // #2255: rekord spoza sidechain wchłania też slot replayu ze swojej sesji.
            if (!item.IsSidechain && Replayed(item) is { } replay && replay != found)
            {
                existing = MergeSlots(found, replay);
            }
        }
        else
        {
            existing = Replayed(item);
        }

        if (existing is { } index)
        {
            var current = _deduped[index];
            if (!string.Equals(current.Session, item.Session, StringComparison.Ordinal))
            {
                foreach (var session in new[] { item.Session, current.Session })
                {
                    PushAlias(Replay(message, session), index, session);
                    PushAlias(ReplayEntry(message, session), index, session);
                }
            }

            if (key.RequestId is null)
            {
                PushIndex(exact, index);
                if (response is { } responseBucket)
                {
                    PushIndex(responseBucket, index);
                }
            }

            if (Replaces(item, current))
            {
                _deduped[index] = item;
                if (Exact(current) != exact)
                {
                    PushIndex(exact, index);
                }

                var routeChanged = !string.Equals(current.Session, item.Session, StringComparison.Ordinal) ||
                                   current.IsSidechain != item.IsSidechain ||
                                   !string.Equals(current.Message, item.Message, StringComparison.Ordinal);
                if (routeChanged)
                {
                    PushIndex(Replay(message, item.Session), index);
                    if (item.IsSidechain)
                    {
                        PushIndex(ReplayEntry(message, item.Session), index);
                    }
                }
            }

            return;
        }

        var newIndex = _deduped.Count;
        _deduped.Add(item);
        PushNew(exact, newIndex);
        if (response is { } newResponse)
        {
            PushNew(newResponse, newIndex);
        }

        PushNew(Replay(message, item.Session), newIndex);
        if (item.IsSidechain)
        {
            PushNew(ReplayEntry(message, item.Session), newIndex);
        }
    }

    public List<UsageEntry> Survivors()
    {
        var result = new List<UsageEntry>(_deduped.Count - _merged.Count);
        for (var index = 0; index < _deduped.Count; index++)
        {
            if (!_merged.ContainsKey(index))
            {
                result.Add(_deduped[index].Entry);
            }
        }

        return result;
    }

    private int? MatchMessage(Bucket bucket, string message)
    {
        if (!_buckets.TryGetValue(bucket, out var slots))
        {
            return null;
        }

        foreach (var slot in slots)
        {
            var index = Resolve(slot.Index);
            if (string.Equals(_deduped[index].Message, message, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>Dopasowanie replayu sidechain (#913) w obrębie sesji efektywnej (z aliasami kopii z innych sesji).</summary>
    private int? Replayed(Item item)
    {
        var message = item.Message!;
        var route = item.IsSidechain ? Replay(message, item.Session) : ReplayEntry(message, item.Session);
        if (!_buckets.TryGetValue(route, out var slots))
        {
            return null;
        }

        foreach (var slot in slots)
        {
            var index = Resolve(slot.Index);
            var existing = _deduped[index];
            var indexedSession = slot.Alias ?? existing.Session;
            if (string.Equals(existing.Message, message, StringComparison.Ordinal) &&
                string.Equals(indexedSession, item.Session, StringComparison.Ordinal) &&
                (item.IsSidechain || existing.IsSidechain || (item.Request is not null && existing.Request is null)))
            {
                return index;
            }
        }

        return null;
    }

    private int MergeSlots(int kept, int merged)
    {
        if (Replaces(_deduped[merged], _deduped[kept]))
        {
            (_deduped[kept], _deduped[merged]) = (_deduped[merged], _deduped[kept]);
        }

        _merged[merged] = kept;
        return kept;
    }

    private int Resolve(int index)
    {
        while (_merged.TryGetValue(index, out var survivor))
        {
            index = survivor;
        }

        return index;
    }

    private void PushIndex(Bucket bucket, int index)
    {
        var slots = Slots(bucket);
        if (!slots.Any(slot => slot.Index == index && slot.Alias is null))
        {
            slots.Add(new Slot(index, null));
        }
    }

    private void PushNew(Bucket bucket, int index) => Slots(bucket).Add(new Slot(index, null));

    private void PushAlias(Bucket bucket, int index, string session)
    {
        var slots = Slots(bucket);
        if (!slots.Any(slot => slot.Index == index && slot.Alias == session))
        {
            slots.Add(new Slot(index, session));
        }
    }

    private List<Slot> Slots(Bucket bucket)
    {
        if (!_buckets.TryGetValue(bucket, out var slots))
        {
            _buckets[bucket] = slots = [];
        }

        return slots;
    }

    private static Bucket Exact(Item item) => item.Request is not null
        ? new Bucket('x', item.Message!, item.Request, null, 0, null)
        : new Bucket('x', item.Message!, null, item.Session, item.Entry.TimestampMs, null);

    private static Bucket? ResponseBucket(Item item) => item.Request is null && item.Key?.Response is { } response
        ? new Bucket('r', item.Message!, null, item.Session, 0, response)
        : null;

    private static Bucket Replay(string message, string session) => new('s', message, null, session, 0, null);

    private static Bucket ReplayEntry(string message, string session) => new('e', message, null, session, 0, null);

    private static bool Replaces(Item candidate, Item existing) =>
        Replaces(candidate.Entry, candidate.HasCacheSplit, existing.Entry, existing.HasCacheSplit);

    /// <summary><c>dedupe.rs:should_replace_deduped_entry</c> + <c>reports_more_of_the_request</c> (#886).</summary>
    private static bool Replaces(UsageEntry candidate, bool candidateSplit, UsageEntry existing, bool existingSplit)
    {
        var candidateSidechain = IsSidechainEntry(candidate);
        var existingSidechain = IsSidechainEntry(existing);
        if (candidateSidechain != existingSidechain)
        {
            return existingSidechain;
        }

        if (candidate.TotalTokens != existing.TotalTokens)
        {
            return candidate.TotalTokens > existing.TotalTokens;
        }

        return Detail(candidate, candidateSplit).CompareTo(Detail(existing, existingSplit)) > 0;
    }

    private static (int Speed, bool Split, bool Cost) Detail(UsageEntry entry, bool split) => (
        entry.Speed switch
        {
            SpeedTier.Fast or SpeedTier.Ultrafast => 2,
            SpeedTier.Standard => 1,
            _ => 0
        },
        split,
        entry.RecordedCostUsd is not null);
}
