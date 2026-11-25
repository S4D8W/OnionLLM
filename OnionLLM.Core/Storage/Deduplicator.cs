using OnionLLM.Core.Models;

namespace OnionLLM.Core.Storage;

/// <summary>
/// Usuwa duplikaty wpisów:
/// <list type="number">
/// <item>Wpisy z tym samym <c>(Source, DedupeKey)</c> — zostaje rekord „pełniejszy”
/// (fast &gt; standard &gt; nieznany, potem z cache_creation, potem z zapisanym kosztem; jak w ccusage #886).</item>
/// <item>Uzgodnienie wpisów źródła z różnych plików (<see cref="Sources.IUsageSource.Reconcile"/>).</item>
/// <item>Wpisy mniej szczegółowe (<see cref="UsageEntry.SessionTier"/>), gdy ta sama sesja ma wpisy dokładniejsze.</item>
/// </list>
/// </summary>
public static class Deduplicator
{
    /// <param name="sources">Źródła z regułami (<see cref="Sources.IUsageSource.DuplicateComparer"/>, <see cref="Sources.IUsageSource.Reconcile"/>);
    /// domyślnie <see cref="Sources.SourceRegistry.Find"/>. Źródło bez własnej reguły używa <see cref="DefaultComparer"/>.</param>
    public static List<UsageEntry> Apply(IEnumerable<UsageEntry> entries, Func<string, Sources.IUsageSource?>? sources = null)
    {
        sources ??= Sources.SourceRegistry.Find;
        var input = entries as IReadOnlyList<UsageEntry> ?? entries.ToList();

        // Sesje, które „zgłaszają” wpisy-lustra: tylko wpisy głównego wątku (Claude: Preference 1 = poza sidechainem),
        // także kopie odrzucone niżej jako duplikaty — jak main_thread_sessions w ccusage (adapter-all/src/loader.rs).
        var claimedSessions = input
            .Where(entry => entry.MirrorOf is null && IsMainThread(entry))
            .Select(entry => entry.Source + ":" + entry.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, IComparer<UsageEntry>>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UsageEntry>();
        var byKey = new Dictionary<(string Source, string Key), int>();

        foreach (var entry in input)
        {
            if (entry.DedupeKey is null)
            {
                result.Add(entry);
                continue;
            }

            var key = (entry.Source, entry.DedupeKey);
            if (byKey.TryGetValue(key, out var index))
            {
                if (!cache.TryGetValue(entry.Source, out var comparer))
                {
                    cache[entry.Source] = comparer = sources(entry.Source)?.DuplicateComparer ?? DefaultComparer;
                }

                if (comparer.Compare(entry, result[index]) > 0)
                {
                    result[index] = entry;
                }

                continue;
            }

            byKey[key] = result.Count;
            result.Add(entry);
        }

        // Uzgadnianie między plikami w obrębie źródła (np. Copilot OTel vs shutdown); kolejność źródeł zachowana.
        var reconciled = new List<UsageEntry>(result.Count);
        foreach (var group in result.GroupBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase))
        {
            var groupEntries = group.ToList();
            reconciled.AddRange(sources(group.Key)?.Reconcile(groupEntries) ?? groupEntries);
        }

        result = reconciled;

        // Dla każdej sesji zostaje najdokładniejszy dostępny poziom (0 per wywołanie < 1 OTel < 2 zbiorczy).
        if (result.Any(entry => entry.SessionTier > 0))
        {
            var bestTier = new Dictionary<(string Source, string Session), int>();
            foreach (var entry in result)
            {
                var key = (entry.Source, entry.SessionId);
                bestTier[key] = bestTier.TryGetValue(key, out var tier) ? Math.Min(tier, entry.SessionTier) : entry.SessionTier;
            }

            result.RemoveAll(entry => entry.SessionTier > bestTier[(entry.Source, entry.SessionId)]);
        }

        // Wpisy-lustra: pomijane, gdy źródło właściwe liczy wskazaną sesję.
        if (result.Any(entry => entry.MirrorOf is not null))
        {
            result.RemoveAll(entry => entry.MirrorOf is not null && claimedSessions.Contains(entry.MirrorOf));
        }

        return result;
    }

    private static bool IsMainThread(UsageEntry entry) =>
        !entry.Source.Equals(Sources.ClaudeSource.SourceId, StringComparison.OrdinalIgnoreCase) || entry.Preference >= 1;

    /// <summary>Reguła domyślna: <see cref="UsageEntry.Preference"/>, potem fast &gt; standard &gt; nieznany, cache_creation, zapisany koszt, liczba tokenów.</summary>
    public static IComparer<UsageEntry> DefaultComparer { get; } = Comparer<UsageEntry>.Create((left, right) =>
    {
        var preference = left.Preference.CompareTo(right.Preference);
        return preference != 0 ? preference : Compare(Rank(left), Rank(right));
    });

    private static (int Speed, bool Cache, bool Cost, long Tokens) Rank(UsageEntry entry) => (
        entry.Speed switch
        {
            SpeedTier.Fast or SpeedTier.Ultrafast => 2,
            SpeedTier.Standard or SpeedTier.Flex => 1,
            _ => 0
        },
        entry.CacheCreationTokens > 0,
        entry.RecordedCostUsd is not null,
        entry.TotalTokens);

    private static int Compare((int Speed, bool Cache, bool Cost, long Tokens) left, (int Speed, bool Cache, bool Cost, long Tokens) right) =>
        Comparer<(int, bool, bool, long)>.Default.Compare(left, right);
}
