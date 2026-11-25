namespace OnionLLM.Core.Sources;

/// <summary>Linia rolloutu wskazująca właściciela zdarzeń (ccusage <c>CodexOwnershipMarker</c>).</summary>
internal enum CodexOwnershipMarker
{
    /// <summary><c>token_usage_record</c>.</summary>
    UsageRecord,

    /// <summary><c>event_msg/thread_settings_applied</c>.</summary>
    ThreadSettings
}

/// <summary>
/// Dane forka do odfiltrowania historii skopiowanej z rodzica (ccusage <c>CodexForkReplay</c>).
/// </summary>
/// <param name="ParentUsage">Zużycie rodzica (zdarzenia bez <c>response_id</c>) do chwili forka.</param>
/// <param name="MissingParent">Id rodzica, gdy jego rolloutu nie znaleziono (wtedy <paramref name="ParentUsage"/> jest puste).</param>
/// <param name="ChildId">Id wątku dziecka z <c>session_meta.id</c>.</param>
/// <param name="EndsAtUsageRecord">Subagent (<c>thread_spawn</c>): pierwszy rekord bez <c>thread_id</c> kończy okno replay.
/// Codex VS Code 0.164 pomija w subagentach odziedziczone <c>token_usage_record</c> (#2051).</param>
internal sealed record CodexForkReplay(
    IReadOnlyList<CodexSource.RawUsage> ParentUsage,
    string? MissingParent,
    string? ChildId,
    bool EndsAtUsageRecord);

/// <summary>
/// Okno replay forka (ccusage <c>parser/replay_filter.rs</c>). Zdarzenia są wstrzymywane do rozstrzygnięcia:
/// pierwszego <c>token_usage_record</c> własnego wątku (u subagenta także rekordu bez <c>thread_id</c>) albo końca pliku.
/// <list type="bullet">
/// <item>Okno kończy się na pierwszym znaczniku własnego wątku (<c>thread_id</c> = id dziecka).</item>
/// <item>Początkowe zdarzenia okna zgodne z prefiksem lub sufiksem zużycia rodzica są pomijane.</item>
/// <item>Zdarzenia między pierwszym znacznikiem obcego wątku a pierwszym własnym są kopiami (odrzucane).</item>
/// <item>Zdarzenia z <c>response_id</c> (rekordy kompakcji) przechodzą zawsze.</item>
/// </list>
/// </summary>
internal sealed class CodexReplayFilter<T>
{
    private CodexForkReplay? _fork;
    private readonly Func<T, bool> _replayable;
    private readonly Func<T, CodexSource.RawUsage> _usage;
    private readonly Action<T> _visit;
    private readonly List<T> _pending = [];
    private int? _ownStart;
    private int? _foreignStart;

    public CodexReplayFilter(CodexForkReplay? fork, Func<T, bool> replayable, Func<T, CodexSource.RawUsage> usage, Action<T> visit)
    {
        _fork = fork;
        _replayable = replayable;
        _usage = usage;
        _visit = visit;
    }

    public bool IsDone => _fork is null;

    /// <summary>Znacznik właściciela; zwraca id brakującego rodzica, gdy rozstrzygnięcie nie mogło potwierdzić kopii.</summary>
    public string? ObserveMarker(CodexOwnershipMarker marker, string? threadId)
    {
        if (_fork is not { } fork)
        {
            return null;
        }

        threadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId;
        var isOwn = threadId is not null && threadId == fork.ChildId;
        if (isOwn)
        {
            _ownStart ??= _pending.Count;
            return marker == CodexOwnershipMarker.UsageRecord ? ResolveLeading() : null;
        }

        if (marker == CodexOwnershipMarker.UsageRecord && threadId is null && fork.EndsAtUsageRecord)
        {
            return ResolveLeading();
        }

        if (threadId is not null && fork.ChildId is not null && _ownStart is null)
        {
            _foreignStart ??= _pending.Count;
        }

        return null;
    }

    public string? Finish() => _fork is null ? null : ResolveLeading();

    public void Visit(T item)
    {
        if (_fork is null)
        {
            _visit(item);
            return;
        }

        _pending.Add(item);
    }

    private string? ResolveLeading()
    {
        if (_fork is not { } fork)
        {
            return null;
        }

        var windowEnd = _ownStart ?? _pending.Count;
        var (unprovenEnd, copiedStart, copiedEnd) = _foreignStart is { } foreign && _ownStart is { } own
            ? (foreign, foreign, own)
            : (windowEnd, 0, 0);

        string? missing = null;
        if (fork.MissingParent is { } parentId && _pending.Take(unprovenEnd).Any(_replayable))
        {
            missing = parentId;
        }

        var leading = _pending.Take(windowEnd)
            .Where(_replayable)
            .Select(_usage)
            .Take(fork.ParentUsage.Count)
            .ToList();
        var inherited = CodexSource.ReplayedParentLength(fork.ParentUsage, leading);
        _fork = null;

        var pending = _pending.ToArray();
        _pending.Clear();
        for (var index = 0; index < pending.Length; index++)
        {
            var item = pending[index];
            if (!_replayable(item))
            {
                _visit(item);
            }
            else if (index < windowEnd && inherited > 0)
            {
                inherited--;
            }
            else if (index < copiedStart || index >= copiedEnd)
            {
                _visit(item);
            }
        }

        return missing;
    }
}
