using System.Globalization;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Dane pomocnicze wpisu <c>token_count</c> Codexa w <see cref="UsageEntry.SourceData"/> (ccusage <c>CodexEventProvenance</c>,
/// <c>CodexAttribution</c>, <c>CodexLineage</c>): plik, liczba identycznych zdarzeń w pliku, numer kopii bez rekordu,
/// <c>response_id</c> nazwanego żądania, surowe <c>total_tokens</c>, wątek i jego przodkowie. Tożsamość jest w
/// <see cref="UsageEntry.DedupeKey"/>; plik w danych sprawia, że kopie z różnych plików łączy dopiero <see cref="CodexDedupe"/>.
/// </summary>
internal static class CodexData
{
    private const char Separator = '\u001f';
    private const char ListSeparator = '\u001e';

    /// <summary>Skrót ścieżki pliku (FNV-1a 64) — tożsamość kopii.</summary>
    public const string File = "f";

    /// <summary>Liczba zdarzeń o tej sygnaturze w pliku do tego zdarzenia włącznie (ccusage <c>rollout_count</c>).</summary>
    public const string RolloutCount = "n";

    /// <summary>Numer wśród identycznych zdarzeń bez rekordu w pliku (ccusage <c>CodexUsageKey::occurrence</c>).</summary>
    public const string Occurrence = "o";

    /// <summary><c>response_id</c> nazwanego żądania (ccusage <c>record_response_id</c>).</summary>
    public const string RecordId = "r";

    /// <summary>Surowe <c>total_tokens</c> wywołania — część sygnatury.</summary>
    public const string Total = "t";

    /// <summary>Id wątku z <c>session_meta</c>.</summary>
    public const string Thread = "th";

    /// <summary>Przodkowie wątku od rodzica w górę (<c>forked_from_id</c> / <c>thread_spawn</c>, także przy <c>history_base</c>).</summary>
    public const string Ancestors = "a";

    public static string Data(params (string Name, string? Value)[] fields)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (name, value) in fields)
        {
            if (value is not null)
            {
                builder.Append(Separator).Append(name).Append('=').Append(value);
            }
        }

        return builder.ToString();
    }

    public static string? Field(string? data, string name)
    {
        if (data is null)
        {
            return null;
        }

        var prefix = Separator + name + "=";
        var start = data.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = data.IndexOf(Separator, start);
        return end < 0 ? data[start..] : data[start..end];
    }

    public static long LongField(string? data, string name) =>
        long.TryParse(Field(data, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static string? List(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(ListSeparator, values);

    public static string[] ListField(string? data, string name) =>
        Field(data, name)?.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>Stabilny skrót ścieżki (niezależny od procesu, bez rozróżniania wielkości liter).</summary>
    public static string FileIdentity(string path)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in path.ToUpperInvariant())
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Deduplikacja <c>token_count</c> Codexa między plikami — port ccusage <c>aggregate/dedupe.rs</c> (#2208, #2366):
/// <list type="bullet">
/// <item>Nazwane żądania (<c>rec:</c>) łączą się po <c>response_id</c> w obrębie sygnatury; rekordy kompakcji (<c>resp:</c>) —
/// po <c>response_id</c>.</item>
/// <item>Kopie bez rekordu (<c>tc:</c>) — po sygnaturze i numerze; kopie tej samej sesji scalają się od razu, innych sesji
/// czekają na rozliczenie (<c>record_legacy</c>).</item>
/// <item>Parowanie: kopia bez rekordu może być kopią nazwanego żądania tylko na tej samej głębokości forka; na każdej
/// głębokości odpada tyle kopii, ile nazwy obejmują: liczba kopii minus nadwyżka największego pliku (<c>rollout_count</c>
/// z każdej kopii) ponad liczbę nazw (<c>into_usage</c>, <c>dedupe.rs:228-315</c>).</item>
/// <item>Rozliczenie: dopasowania z kilku sesji grupowane według głębokości forka mierzonej względem tych samych
/// dopasowań co przy parowaniu; każda głębokość to jedno żądanie przypisane sesji pierwszego pliku (<c>collapse_copies</c>).</item>
/// <item>Głębokość = liczba przodków sesji wśród wątków dopasowanych sesji (<c>CodexLineage::fork_depths</c>).</item>
/// <item>Scalanie kopii (<c>CodexAttribution::merge</c>): sesja i pola wpisu z pierwszego pliku, poziom zapisany w logu
/// łączony jak <c>merge_codex_service_tiers</c>, poziomy z <c>config.toml</c> sumowane; bez zapisanego — jedyny
/// skonfigurowany albo ten z najwyższym mnożnikiem dla modelu (<c>pricing_tiers</c>, <c>report/cost.rs</c>).</item>
/// </list>
/// Kolejność wejścia (ledger: czas, ścieżka pliku) wyznacza „pierwszy plik” — kopie jednej sygnatury mają ten sam czas.
/// </summary>
internal static class CodexDedupe
{
    public const string RecordedKeyPrefix = "rec:";
    public const string RecordlessKeyPrefix = "tc:";

    /// <summary>Prefiks klucza rekordu kompakcji z własnym <c>response_id</c> (ccusage <c>CodexResponseUsage</c>).</summary>
    public const string ResponseKeyPrefix = "resp:";

    private readonly record struct Signature(long Timestamp, string? Model, long Input, long CacheRead, long CacheCreation,
        long Output, long Reasoning, long Total);

    private readonly record struct UsageKey(Signature Signature, long Occurrence);

    /// <summary>Przypisanie żądania (ccusage <c>CodexAttribution</c>).</summary>
    private sealed class Attribution(int index, UsageEntry entry)
    {
        public int Index { get; private set; } = index;
        public string Session { get; private set; } = entry.SessionId;
        public long RolloutCount { get; private set; } = CodexData.LongField(entry.SourceData, CodexData.RolloutCount);
        public SpeedTier? Recorded { get; private set; } = entry.Preference > 0 ? entry.Speed : null;
        public int Configured { get; private set; } = entry.Preference == 0 && entry.Speed != SpeedTier.Unknown ? 1 << (int)entry.Speed : 0;

        public void Merge(Attribution incoming)
        {
            Recorded = MergeRecorded(Recorded, incoming.Recorded);
            Configured |= incoming.Configured;
            RolloutCount = Math.Max(RolloutCount, incoming.RolloutCount);
            if (incoming.Index < Index)
            {
                Index = incoming.Index;
                Session = incoming.Session;
            }
        }
    }

    public static IReadOnlyList<UsageEntry> Apply(IReadOnlyList<UsageEntry> entries)
    {
        var usage = new Dictionary<UsageKey, Attribution>();
        var otherSessions = new Dictionary<UsageKey, List<Attribution>>();
        var recorded = new Dictionary<Signature, List<(string Id, Attribution Attribution)>>();
        var responses = new Dictionary<string, Attribution>(StringComparer.Ordinal);
        var threads = new Dictionary<string, string>(StringComparer.Ordinal);
        var ancestors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var tracked = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!IsTracked(entry))
            {
                continue;
            }

            tracked++;
            var data = entry.SourceData;
            if (entry.DedupeKey!.StartsWith(ResponseKeyPrefix, StringComparison.Ordinal))
            {
                // Rekord kompakcji: kopie tego samego response_id (dedupe.rs:405-419).
                if (responses.TryGetValue(entry.DedupeKey, out var response))
                {
                    response.Merge(new Attribution(index, entry));
                }
                else
                {
                    responses[entry.DedupeKey] = new Attribution(index, entry);
                }

                continue;
            }

            if (CodexData.Field(data, CodexData.Thread) is { } thread)
            {
                threads[entry.SessionId] = thread;
                ancestors.TryAdd(entry.SessionId, CodexData.ListField(data, CodexData.Ancestors));
            }

            var signature = new Signature(entry.TimestampMs, entry.Model, entry.InputTokens, entry.CacheReadTokens,
                entry.CacheCreationTokens, entry.OutputTokens, entry.ReasoningTokens, CodexData.LongField(data, CodexData.Total));
            var attribution = new Attribution(index, entry);
            if (CodexData.Field(data, CodexData.RecordId) is { } id)
            {
                var requests = recorded.TryGetValue(signature, out var list) ? list : recorded[signature] = [];
                var match = requests.FindIndex(request => request.Id == id);
                if (match >= 0)
                {
                    requests[match].Attribution.Merge(attribution);
                }
                else
                {
                    requests.Add((id, attribution));
                }

                continue;
            }

            // ccusage record_legacy (dedupe.rs:208-226).
            var key = new UsageKey(signature, CodexData.LongField(data, CodexData.Occurrence));
            if (!usage.TryGetValue(key, out var existing))
            {
                usage[key] = attribution;
            }
            else if (existing.Session == attribution.Session)
            {
                existing.Merge(attribution);
            }
            else
            {
                var sessions = otherSessions.TryGetValue(key, out var others) ? others : otherSessions[key] = [];
                if (sessions.Find(session => session.Session == attribution.Session) is { } same)
                {
                    same.Merge(attribution);
                }
                else
                {
                    sessions.Add(attribution);
                }
            }
        }

        if (tracked == 0)
        {
            return entries;
        }

        // Głębokość forka każdej sesji względem wątków dopasowanych sesji (ccusage fork_depths, dedupe.rs:159-175).
        List<int> ForkDepths(IReadOnlyList<string> sessions, IReadOnlyList<string> related)
        {
            var matched = sessions.Concat(related)
                .Select(session => threads.GetValueOrDefault(session))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            return sessions.Select(session => ancestors.GetValueOrDefault(session, []).Count(matched.Contains)).ToList();
        }

        // Parowanie kopii bez rekordu z nazwanymi żądaniami tej samej głębokości (#2366, dedupe.rs:239-315).
        var pairingSessions = new Dictionary<Signature, List<string>>();
        foreach (var (signature, requests) in recorded)
        {
            var recordless = new List<(long Occurrence, string Session, long RolloutCount)>();
            for (var occurrence = 0L; usage.TryGetValue(new UsageKey(signature, occurrence), out var item); occurrence++)
            {
                foreach (var attribution in otherSessions.GetValueOrDefault(new UsageKey(signature, occurrence), []).Prepend(item))
                {
                    recordless.Add((occurrence, attribution.Session, attribution.RolloutCount));
                }
            }

            if (recordless.Count == 0)
            {
                continue;
            }

            var sessions = requests.Select(request => request.Attribution.Session).Concat(recordless.Select(item => item.Session)).ToList();
            var depths = ForkDepths(sessions, []);
            var namedDepths = depths.Take(requests.Count).ToList();
            var recordlessDepths = depths.Skip(requests.Count).ToList();
            var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var position = 0; position < recordless.Count; position++)
            {
                depthOf[recordless[position].Session] = recordlessDepths[position];
            }

            foreach (var depth in recordlessDepths.Distinct().Order())
            {
                var named = namedDepths.Count(level => level == depth);
                if (named == 0)
                {
                    continue;
                }

                var atDepth = recordless.Where((_, position) => recordlessDepths[position] == depth).ToList();
                var recordlessCount = atDepth.Max(item => item.Occurrence + 1);
                var largestRollout = requests.Where((_, position) => namedDepths[position] == depth)
                    .Select(request => request.Attribution.RolloutCount)
                    .Concat(atDepth.Select(item => item.RolloutCount))
                    .Max();
                var unmatched = Math.Max(0, largestRollout - named);
                var paired = Math.Max(0, recordlessCount - unmatched);
                bool Copy(Attribution attribution) => depthOf.TryGetValue(attribution.Session, out var level) && level == depth;
                for (var occurrence = 0L; occurrence < paired; occurrence++)
                {
                    var key = new UsageKey(signature, occurrence);
                    if (usage.TryGetValue(key, out var standIn) && Copy(standIn))
                    {
                        usage.Remove(key);
                    }

                    if (otherSessions.TryGetValue(key, out var others))
                    {
                        others.RemoveAll(Copy);
                        if (others.Count == 0)
                        {
                            otherSessions.Remove(key);
                        }
                    }
                }
            }

            pairingSessions[signature] = sessions;
        }

        // Rozliczenie dopasowań z kilku sesji: jedna kopia na głębokość (#2208, dedupe.rs:316-332, 177-196).
        var survivors = new List<Attribution>();
        foreach (var (key, sessions) in otherSessions)
        {
            var matches = new List<Attribution>();
            if (usage.Remove(key, out var first))
            {
                matches.Add(first);
            }

            matches.AddRange(sessions);
            var depths = ForkDepths(matches.Select(match => match.Session).ToList(), pairingSessions.GetValueOrDefault(key.Signature, []));
            var byDepth = new List<(int Depth, Attribution Request)>();
            for (var position = 0; position < matches.Count; position++)
            {
                var existing = byDepth.FindIndex(request => request.Depth == depths[position]);
                if (existing >= 0)
                {
                    byDepth[existing].Request.Merge(matches[position]);
                }
                else
                {
                    byDepth.Add((depths[position], matches[position]));
                }
            }

            survivors.AddRange(byDepth.Select(request => request.Request));
        }

        survivors.AddRange(usage.Values);
        survivors.AddRange(recorded.Values.SelectMany(requests => requests.Select(request => request.Attribution)));
        survivors.AddRange(responses.Values);

        var kept = survivors.ToDictionary(attribution => attribution.Index);
        var result = new List<UsageEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!IsTracked(entry))
            {
                result.Add(entry);
            }
            else if (kept.TryGetValue(index, out var attribution))
            {
                var tier = PricingTier(attribution, entry.Model);
                result.Add(tier == entry.Speed ? entry : entry with { Speed = tier });
            }
        }

        return result;
    }

    /// <summary>Wpis <c>token_count</c> lub rekord kompakcji z danymi pliku — wpisy <c>exec:</c> łączy sam klucz.</summary>
    private static bool IsTracked(UsageEntry entry) =>
        entry.DedupeKey is { } key &&
        (key.StartsWith(RecordedKeyPrefix, StringComparison.Ordinal) || key.StartsWith(RecordlessKeyPrefix, StringComparison.Ordinal) ||
         key.StartsWith(ResponseKeyPrefix, StringComparison.Ordinal)) &&
        CodexData.Field(entry.SourceData, CodexData.File) is not null;

    /// <summary>ccusage <c>merge_codex_service_tiers</c> (<c>types.rs:70-89</c>): Standard &gt; Ultrafast &gt; Fast &gt; Flex.</summary>
    internal static SpeedTier? MergeRecorded(SpeedTier? current, SpeedTier? incoming) =>
        current is { } left && incoming is { } right ? (TierRank(left) >= TierRank(right) ? left : right) : current ?? incoming;

    internal static int TierRank(SpeedTier tier) => tier switch
    {
        SpeedTier.Standard => 4,
        SpeedTier.Ultrafast => 3,
        SpeedTier.Fast => 2,
        SpeedTier.Flex => 1,
        _ => 0
    };

    /// <summary>
    /// Poziom do wyceny (ccusage <c>CodexAttribution::pricing_tiers</c>, <c>report/cost.rs:24-40</c>): zapisany w logu, inaczej
    /// jedyny z <c>config.toml</c>, a przy kilku — ten z najwyższym mnożnikiem dla modelu (remis: Standard &gt; Ultrafast &gt;
    /// Fast &gt; Flex, jak <c>max_by</c> po kolejności Flex, Fast, Ultrafast, Standard).
    /// </summary>
    private static SpeedTier PricingTier(Attribution attribution, string? model)
    {
        if (attribution.Recorded is { } recorded)
        {
            return recorded;
        }

        var configured = Enum.GetValues<SpeedTier>()
            .Where(tier => tier != SpeedTier.Unknown && (attribution.Configured & (1 << (int)tier)) != 0)
            .ToList();
        return configured.Count == 0
            ? SpeedTier.Unknown
            : configured.OrderByDescending(tier => Multiplier(tier, model ?? CodexSource.FallbackModel)).ThenByDescending(TierRank).First();
    }

    /// <summary>Mnożnik poziomu dla modelu; poziom bez stawki modelu płaci 1×.</summary>
    internal static double Multiplier(SpeedTier tier, string model) => tier switch
    {
        SpeedTier.Fast => SpeedMultipliers.Fast(model) ?? 1.0,
        SpeedTier.Flex => SpeedMultipliers.Flex(model) ?? 1.0,
        SpeedTier.Ultrafast => SpeedMultipliers.Ultrafast(model) ?? 1.0,
        _ => 1.0
    };
}
