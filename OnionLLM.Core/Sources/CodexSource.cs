using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Pricing;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// OpenAI Codex CLI — rollouty JSONL (także skompresowane <c>.jsonl.zst</c>) w <c>sessions/**</c> i <c>archived_sessions/**</c>;
/// katalog bez tych podkatalogów (np. zapisane logi <c>codex exec --json</c>) jest skanowany bezpośrednio (ccusage <c>paths.rs</c>).
/// Katalogi: <c>CODEX_HOME</c> (lista) lub <c>~/.codex</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>event_msg/token_count</c>: <c>last_token_usage</c> (zużycie wywołania), a gdy go brak — różnica kolejnych
/// <c>total_token_usage</c>. Zdarzenia z niezmienionym licznikiem łącznym są powtórzeniami.</item>
/// <item><c>token_usage_record</c> (z <c>response_id</c>) czeka jako „oczekujący”: jeśli następny <c>token_count</c> go potwierdza,
/// liczy się tylko <c>token_count</c>; jeśli przyjdzie <c>compacted</c> z tym <c>compaction_response_id</c>, rekord jest emitowany
/// (wywołania auto-kompakcji nie mają własnego <c>token_count</c>). Jak w ccusage <c>CodexCompactionUsageState</c>.</item>
/// <item>Linie spoza formatu sesji z <c>usage</c>, <c>data.usage</c>, <c>result.usage</c> lub <c>response.usage</c> to logi
/// <c>codex exec --json</c> (ccusage <c>parser/exec.rs</c>).</item>
/// <item>Forki (<c>session_meta.forked_from_id</c> lub subagent <c>thread_spawn.parent_thread_id</c>) kopiują historię rodzica —
/// okno replay filtruje <see cref="CodexReplayFilter{T}"/> (ccusage <c>parser/replay_filter.rs</c>).</item>
/// <item>Poziom usługi: ostatni <c>thread_settings_applied</c>, a gdy nieustalony — <c>config.toml</c> katalogu
/// <c>CODEX_HOME</c>, z którego wczytano plik; ustawienie <see cref="SourceSettings.Speed"/> może go wymusić
/// (<see cref="ApplySettings"/>). Wpis z poziomem z logu ma <see cref="UsageEntry.Preference"/> = 1.</item>
/// <item>Duplikaty między plikami (ccusage <c>aggregate/dedupe.rs</c>, #2208/#2366): <c>token_count</c> potwierdzający
/// <c>token_usage_record</c> ma klucz <c>rec:&lt;response_id&gt;</c>; pozostałe — sygnaturę (czas, model, zużycie) i numer wśród
/// identycznych zdarzeń bez rekordu w pliku. Plik, <c>rollout_count</c> i linia wątków są w <see cref="UsageEntry.SourceData"/>;
/// kopie scala <see cref="Reconcile"/> (<see cref="CodexDedupe"/>) według głębokości forka.</item>
/// </list>
/// </remarks>
public sealed partial class CodexSource : UsageSourceBase
{
    public const string SourceId = "codex";
    public const string FallbackModel = "gpt-5";

    /// <summary>Model-plan auto-review; kierowany na model zależny od daty (ccusage <c>parser/model.rs:117-119</c>).</summary>
    public const string AutoReviewModel = "codex-auto-review";

    private static readonly string[] LineMarkers =
    [
        "token_count", "turn_context", "session_meta", "thread_settings_applied", "token_usage_record", "compacted",
        "\"usage\"", "\"input_tokens\"", "\"prompt_tokens\""
    ];

    private static readonly ConcurrentDictionary<string, (long Modified, SpeedTier Tier)> ConfigTiers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SessionIndex> _sessionIndex = new(StringComparer.OrdinalIgnoreCase);

    public override string Id => SourceId;
    public override string DisplayName => "Codex CLI";
    public override string Vendor => "OpenAI";
    public override string AccentColor => "#10A37F";
    public override int ParserVersion => 6;

    protected override string[] EnvironmentVariables => ["CODEX_HOME"];

    protected override string[] Executables => ["codex"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".codex")];

    /// <summary>
    /// Wymuszony poziom szybkości z ustawień (<c>standard</c>/<c>flex</c>/<c>fast</c>); <c>auto</c> lub brak zostawia poziom
    /// z logów i <c>config.toml</c> (ccusage <c>speed.rs:11-18</c>, <c>report/cost.rs</c>).
    /// </summary>
    public override UsageEntry ApplySettings(UsageEntry entry, SourceSettings settings) =>
        SpeedOverride(settings.Speed) is { } tier && entry.Speed != tier ? entry with { Speed = tier } : entry;

    /// <summary>Poziom wymuszony wartością ustawienia <see cref="SourceSettings.Speed"/>; null = <c>auto</c>.</summary>
    public static SpeedTier? SpeedOverride(string? speed) => speed?.Trim().ToLowerInvariant() switch
    {
        "standard" => SpeedTier.Standard,
        "flex" => SpeedTier.Flex,
        "fast" => SpeedTier.Fast,
        _ => null
    };

    /// <summary>
    /// Duplikaty o tym samym kluczu (kompakcja <c>resp:</c>, <c>exec:</c>; kopie <c>token_count</c> scala <see cref="CodexDedupe"/>): poziom zapisany w logu (<see cref="UsageEntry.Preference"/> = 1) wygrywa
    /// z poziomem z <c>config.toml</c>. Dwa zapisane łączą się jak ccusage <c>merge_codex_service_tiers</c>
    /// (<c>types.rs:70-89</c>): Standard &gt; Ultrafast &gt; Fast &gt; Flex &gt; nieznany. Kopie niesklasyfikowane z kilku
    /// katalogów <c>CODEX_HOME</c> o różnych <c>config.toml</c> dostają poziom z najwyższym mnożnikiem dla modelu
    /// (ccusage <c>README.md</c> „When attributions merge across homes”, <c>report/cost.rs</c>). Przy remisie zostaje pierwszy wpis.
    /// </summary>
    public override IComparer<UsageEntry>? DuplicateComparer { get; } = Comparer<UsageEntry>.Create((left, right) =>
    {
        var preference = left.Preference.CompareTo(right.Preference);
        if (preference != 0)
        {
            return preference;
        }

        if (left.Preference == 0)
        {
            var multiplier = ConfiguredMultiplier(left).CompareTo(ConfiguredMultiplier(right));
            if (multiplier != 0)
            {
                return multiplier;
            }
        }

        return CodexDedupe.TierRank(left.Speed).CompareTo(CodexDedupe.TierRank(right.Speed));
    });

    /// <summary>
    /// Mnożnik poziomu z <c>config.toml</c> dla modelu wpisu; brak poziomu nie wnosi nic (ccusage <c>ConfiguredTiers</c> z
    /// <c>None</c> jest pusty), poziom bez stawki modelu płaci 1×.
    /// </summary>
    private static double ConfiguredMultiplier(UsageEntry entry) => entry.Speed == SpeedTier.Unknown
        ? double.NegativeInfinity
        : CodexDedupe.Multiplier(entry.Speed, entry.Model ?? FallbackModel);

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            // Zmiana config.toml zmienia poziom wpisów bez poziomu w logach — wymusza ponowny odczyt rolloutów.
            var configModified = ConfigModifiedMs(root);
            foreach (var file in RolloutFiles(root))
            {
                var source = SourceFile.Create(file, root);
                yield return configModified > source.ModifiedMs ? source with { ModifiedMs = configModified } : source;
            }
        }
    }

    /// <summary>
    /// Rollouty katalogu: <c>sessions</c>, potem <c>archived_sessions</c>; bez nich sam katalog. Ta sama ścieżka względna
    /// (bez <c>.zst</c>) liczy się raz — wygrywa kopia aktywna i niespakowana (ccusage <c>paths.rs</c>, #1890).
    /// </summary>
    private static IEnumerable<string> RolloutFiles(string root)
    {
        var directories = new[] { "sessions", "archived_sessions" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .ToList();
        if (directories.Count == 0)
        {
            directories.Add(root);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var pattern in new[] { "*.jsonl", "*.jsonl.zst" })
            {
                foreach (var file in FileUtil.SafeEnumerateFiles(directory, pattern))
                {
                    var relative = Path.GetRelativePath(directory, file);
                    if (seen.Add(StripZst(relative)))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var result = ParseRollout(file.Path, file.Root, diagnostics, cancellationToken);
        var lineage = Lineage(file.Root, result.Meta);
        if (result.Meta.ParentId is not { } parentId)
        {
            return AssignKeys(result.Events, file.Path, lineage);
        }

        var parentPath = FindSessionFile(file.Root, parentId, file.Path);
        List<RawUsage> parentUsage = [];
        var parentCompactions = new Dictionary<string, long>(StringComparer.Ordinal);
        if (parentPath is not null)
        {
            var parent = ParseRollout(parentPath, file.Root, new ParseDiagnostics(), cancellationToken);
            var forkedAt = result.Meta.ForkedAtMs;

            // Prefiks zużycia rodzica do pierwszego zdarzenia po chwili forka (ccusage replay.rs replay_prefix).
            parentUsage = parent.Events
                .Where(item => item.Replayable)
                .TakeWhile(item => forkedAt is null || item.Entry.TimestampMs <= forkedAt)
                .Select(item => item.Raw)
                .ToList();
            foreach (var item in parent.Events.Where(item => !item.Replayable && item.Entry.DedupeKey is not null))
            {
                parentCompactions.TryAdd(item.Entry.DedupeKey!, item.Entry.TimestampMs);
            }
        }

        var output = new List<ParsedEvent>();
        var filter = new CodexReplayFilter<ParsedEvent>(
            new CodexForkReplay(parentUsage, parentPath is null ? parentId : null, result.Meta.Id, result.Meta.Spawned),
            item => item.Replayable,
            item => item.Raw,
            item =>
            {
                // Rekord kompakcji skopiowany z rodzica (ten sam response_id sprzed forka) — rodzic już go liczy.
                if (!item.Replayable && item.Entry.DedupeKey is { } key && parentCompactions.TryGetValue(key, out var recorded) &&
                    (result.Meta.ForkedAtMs is null || recorded <= result.Meta.ForkedAtMs))
                {
                    return;
                }

                output.Add(item);
            });

        string? missing = null;
        foreach (var step in result.Steps)
        {
            if (step.Event is { } parsed)
            {
                filter.Visit(parsed);
            }
            else if (step.Marker is { } marker)
            {
                missing ??= filter.ObserveMarker(marker, step.ThreadId);
            }
        }

        missing ??= filter.Finish();
        if (missing is not null)
        {
            diagnostics.Skip($"brak pliku sesji-rodzica {missing} (fork liczony w całości)");
        }

        return AssignKeys(output, file.Path, lineage);
    }

    /// <summary>
    /// Klucze i dane <c>token_count</c> po odfiltrowaniu replay (ccusage <c>CodexDedupe::record</c>, <c>dedupe.rs:382-447</c>):
    /// nazwane żądanie → <c>rec:&lt;response_id&gt;</c>; bez rekordu → <c>tc:</c> + sygnatura (czas, model, zużycie) i numer
    /// wśród identycznych zdarzeń bez rekordu w tym pliku (kopia powtarza numery oryginału, identyczne żądania jednego pliku
    /// zostają osobne). <see cref="UsageEntry.SourceData"/> (<see cref="CodexData"/>): plik, <c>rollout_count</c> (zdarzenia
    /// tej sygnatury w pliku do tego włącznie, z rekordem i bez), wątek i przodkowie — kopie z różnych plików scala
    /// <see cref="Reconcile"/>.
    /// </summary>
    private static List<UsageEntry> AssignKeys(IEnumerable<ParsedEvent> events, string path, (string? Thread, string? Ancestors) lineage)
    {
        var file = CodexData.FileIdentity(path);
        var occurrences = new Dictionary<string, (int Total, int Recordless)>(StringComparer.Ordinal);
        var entries = new List<UsageEntry>();
        foreach (var item in events)
        {
            if (!item.TokenCount)
            {
                // Rekord kompakcji (resp:) też niesie plik — kopie scala CodexDedupe (ccusage CodexResponseUsage); exec łączy klucz.
                entries.Add(item.Replayable ? item.Entry : item.Entry with { SourceData = CodexData.Data((CodexData.File, file)) });
                continue;
            }

            var entry = item.Entry;
            var raw = item.Raw;
            var signature = $"{entry.TimestampMs}:{entry.Model}:{raw.Input}:{raw.Cached}:{raw.CacheCreation}:{raw.Output}:{raw.Reasoning}:{raw.Total}";
            var (total, recordless) = occurrences.GetValueOrDefault(signature);
            occurrences[signature] = (total + 1, recordless + (item.RecordId is null ? 1 : 0));

            var key = item.RecordId is { } recordId
                ? CodexDedupe.RecordedKeyPrefix + recordId
                : $"{CodexDedupe.RecordlessKeyPrefix}{signature}:{recordless}";
            var data = CodexData.Data(
                (CodexData.File, file),
                (CodexData.RolloutCount, Invariant(total + 1)),
                (CodexData.Occurrence, item.RecordId is null ? Invariant(recordless) : null),
                (CodexData.RecordId, item.RecordId),
                (CodexData.Total, Invariant(raw.Total)),
                (CodexData.Thread, lineage.Thread),
                (CodexData.Ancestors, lineage.Ancestors));
            entries.Add(entry with { DedupeKey = key, SourceData = data });
        }

        return entries;
    }

    private static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Scala kopie <c>token_count</c> z różnych plików jak ccusage <c>aggregate/dedupe.rs</c> (#2208, #2366): nazwane żądania
    /// po <c>response_id</c>, kopie bez rekordu parowane z nazwanymi tej samej głębokości forka, reszta rozliczana po jednej
    /// na głębokość — szczegóły w <see cref="CodexDedupe"/>. Działa wyłącznie na wpisach (bez stanu instancji).
    /// </summary>
    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries) => CodexDedupe.Apply(entries);

    /// <summary>Liczniki tokenów Codexa: wejście zawiera odczyt i zapis cache (ccusage <c>CodexRawUsage</c>).</summary>
    internal readonly record struct RawUsage(long Input, long Cached, long Output, long Reasoning, long Total, long CacheCreation = 0)
    {
        public bool IsEmpty => Input == 0 && Cached == 0 && CacheCreation == 0 && Output == 0 && Reasoning == 0;

        public RawUsage Minus(RawUsage? previous) => previous is not { } p
            ? this
            : new RawUsage(
                Math.Max(0, Input - p.Input),
                Math.Max(0, Cached - p.Cached),
                Math.Max(0, Output - p.Output),
                Math.Max(0, Reasoning - p.Reasoning),
                Math.Max(0, Total - p.Total),
                Math.Max(0, CacheCreation - p.CacheCreation)).Normalize();

        /// <summary>Odczyt cache ≤ wejście, zapis cache ≤ wejście − odczyt (ccusage <c>normalize_codex_raw_usage</c>).</summary>
        public RawUsage Normalize()
        {
            var cached = Math.Min(Cached, Input);
            return this with { Cached = cached, CacheCreation = Math.Min(CacheCreation, Input - cached) };
        }
    }

    internal enum UsageState
    {
        Missing,
        Malformed,
        Decoded
    }

    /// <summary>
    /// Obiekt zużycia z aliasami pól (ccusage <c>types/wire.rs:459-497</c>): <c>input_tokens|prompt_tokens|input</c>,
    /// <c>cached_input_tokens|cache_read_input_tokens|cached_tokens</c>, <c>cache_write_input_tokens|cache_creation_input_tokens</c>,
    /// <c>output_tokens|completion_tokens|output</c>, <c>reasoning_output_tokens|reasoning_tokens</c>. Liczby jako tekst są
    /// akceptowane; obiekt lub tablica w miejscu liczby to błędne zużycie. <paramref name="lossy"/>: wartość niebędąca obiektem
    /// to brak (pola <c>info.*_token_usage</c>), w przeciwnym razie błąd (pole <c>usage</c>).
    /// </summary>
    internal static (UsageState State, RawUsage Usage) ReadUsage(JsonElement? element, bool lossy)
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (UsageState.Missing, default);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return (lossy ? UsageState.Missing : UsageState.Malformed, default);
        }

        var invalid = false;
        long? Field(string name)
        {
            if (!value.TryGetProperty(name, out var field))
            {
                return null;
            }

            switch (field.ValueKind)
            {
                case JsonValueKind.Number when field.TryGetInt64(out var number):
                    return number >= 0 ? number : null;
                case JsonValueKind.String when ulong.TryParse(field.GetString()?.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                    return parsed > long.MaxValue ? null : (long)parsed;
                case JsonValueKind.Object or JsonValueKind.Array:
                    invalid = true;
                    return null;
                default:
                    return null;
            }
        }

        var input = Field("input_tokens") ?? Field("prompt_tokens") ?? Field("input") ?? 0;
        var output = Field("output_tokens") ?? Field("completion_tokens") ?? Field("output") ?? 0;
        var reasoning = Field("reasoning_output_tokens") ?? Field("reasoning_tokens") ?? 0;
        var cached = Field("cached_input_tokens") ?? Field("cache_read_input_tokens") ?? Field("cached_tokens") ?? 0;
        var creation = Field("cache_write_input_tokens") ?? Field("cache_creation_input_tokens") ?? 0;
        var total = Field("total_tokens");

        // Pozostałe aliasy też muszą być poprawne (ccusage sprawdza każde znane pole).
        foreach (var name in new[] { "prompt_tokens", "input", "completion_tokens", "output", "reasoning_tokens", "cache_read_input_tokens", "cached_tokens", "cache_creation_input_tokens" })
        {
            Field(name);
        }

        if (invalid)
        {
            return (UsageState.Malformed, default);
        }

        // Codex wlicza reasoning do output; zapisany exec --json może pominąć total albo podać 0 (ccusage #1499).
        var usage = new RawUsage(input, cached, output, reasoning, total is > 0 ? total.Value : input + output, creation).Normalize();
        return (UsageState.Decoded, usage);
    }

    /// <param name="TokenCount">Zdarzenie <c>token_count</c> — klucz nadaje <see cref="AssignKeys"/> po filtrze replay.</param>
    /// <param name="RecordId"><c>response_id</c> rekordu, który ten <c>token_count</c> potwierdza (ccusage <c>record_response_id</c>).</param>
    internal sealed record ParsedEvent(UsageEntry Entry, RawUsage Raw, bool Replayable, bool TokenCount = false, string? RecordId = null);

    /// <summary>Krok rolloutu w kolejności pliku: zdarzenie zużycia albo znacznik właściciela (dla okna replay forka).</summary>
    internal readonly record struct Step(ParsedEvent? Event, CodexOwnershipMarker? Marker, string? ThreadId);

    /// <summary>Metadane z <c>session_meta</c> (ccusage <c>CodexSessionMetadata</c>).</summary>
    /// <param name="ParentId">Rodzic, którego historię plik kopiuje (fork do filtrowania replay).</param>
    /// <param name="LineageParentId">Rodzic w linii wątków — także przy <c>history_base</c> i rolloucie zastępczym
    /// (ccusage <c>lineage_parent_id</c>, głębokość forka).</param>
    internal sealed record SessionMeta(string? Id, string? ParentId, long? ForkedAtMs, bool Spawned, string? LineageParentId = null);

    internal sealed record RolloutResult(List<Step> Steps, SessionMeta Meta)
    {
        public IEnumerable<ParsedEvent> Events => Steps.Where(step => step.Event is not null).Select(step => step.Event!);
    }

    internal sealed class State
    {
        public string Path { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string? Cwd { get; set; }
        public string? Model { get; set; }
        public SpeedTier Tier { get; set; }
        public SpeedTier ConfiguredTier { get; set; }
        public RawUsage? PreviousTotals { get; set; }
        public SessionMeta Meta { get; set; } = new(null, null, null, false);
        public bool SeenSessionMeta { get; set; }
        public long? FileModifiedMs { get; set; }

        // Kompakcja / rekordy odpowiedzi.
        public HashSet<string> CompactedIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (ParsedEvent Event, RawUsage? Thread)> Pending { get; } = new(StringComparer.Ordinal);
        public string? LatestResponseId { get; set; }

        /// <summary>Rekord czeka na pierwszy rosnący <c>token_count</c> (ccusage <c>latest_record_awaits_snapshot</c>).</summary>
        public bool RecordAwaitsSnapshot { get; set; }

        // Wystąpienia identycznych zdarzeń exec w pliku (ccusage CodexFileOccurrences).
        public Dictionary<string, int> ExecOccurrences { get; } = new(StringComparer.Ordinal);
    }

    internal static RolloutResult ParseRollout(string path, string root, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var state = new State
        {
            Path = path,
            SessionId = SessionKey(root, path),
            ConfiguredTier = ReadConfiguredTier(root)
        };
        var steps = new List<Step>();
        var lines = FileUtil.ReadLinesAuto(path, out var truncated);
        if (truncated)
        {
            diagnostics.Skip("ucięty plik zstd (odczytano linie sprzed błędu)");
        }

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                continue;
            }

            // Znaczniki zapisane z ucieczkami (np. "us\u0061ge", "token\u005fcount") też kwalifikują linię — parser JSON
            // czyta je jak zwykłe; taka linia niebędąca poprawnym JSON-em jest pomijana bez diagnostyki
            // (ccusage parser.rs:598-604, CodexLineKind::Escaped).
            var escaped = false;
            if (!LineMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
            {
                if (!SpellsWithEscapes(line, LineMarkers))
                {
                    continue;
                }

                escaped = true;
            }

            if (!JsonUtil.TryParse(line, out var document))
            {
                if (!escaped)
                {
                    diagnostics.Skip("niepoprawny JSON");
                }

                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ParseLine(document.RootElement, state, diagnostics, steps);
                }
            }
        }

        return new RolloutResult(steps, state.Meta);
    }

    /// <summary>Przetwarza jedną linię; zdarzenia z zużyciem i znaczniki właściciela dopisuje do <paramref name="steps"/>.</summary>
    internal static void ParseLine(JsonElement root, State state, ParseDiagnostics diagnostics, List<Step> steps)
    {
        var type = root.Str("type");
        var payload = root.Prop("payload") is { ValueKind: JsonValueKind.Object } objectPayload ? objectPayload : (JsonElement?)null;
        var payloadType = payload?.Str("type");

        if (type == "session_meta")
        {
            if (payload is { } meta && !state.SeenSessionMeta)
            {
                state.SeenSessionMeta = true;
                state.Cwd = meta.Str("cwd") ?? state.Cwd;
                state.Meta = ReadSessionMeta(root, meta, state.Path);
            }

            return;
        }

        if (!IsSessionUsageType(type, payloadType))
        {
            ParseExecLine(root, state, diagnostics, steps);
            return;
        }

        // Znacznik właściciela przed zdarzeniem tej linii (ccusage parser.rs:261-273).
        var marker = type == "token_usage_record" ? CodexOwnershipMarker.UsageRecord
            : payloadType == "thread_settings_applied" ? CodexOwnershipMarker.ThreadSettings
            : (CodexOwnershipMarker?)null;
        if (marker is not null)
        {
            steps.Add(new Step(null, marker, payload?.Prop("thread_id").Str()));
        }

        switch (type)
        {
            case "turn_context":
                if (payload is { } context)
                {
                    state.Model = ModelFrom(context) ?? state.Model;
                    state.Cwd = context.Str("cwd") ?? state.Cwd;
                }

                return;

            case "compacted":
                if (payload?.Str("compaction_response_id") is { } compactedId)
                {
                    state.CompactedIds.Add(compactedId);
                    if (state.Pending.Remove(compactedId, out var pending) && state.EmittedIds.Add(compactedId))
                    {
                        steps.Add(new Step(pending.Event, null, null));
                    }
                }

                return;

            case "token_usage_record":
                if (payload is { } record)
                {
                    HandleUsageRecord(root, record, state, diagnostics, steps);
                }

                return;
        }

        if (payload is not { } eventPayload)
        {
            return;
        }

        if (payloadType == "thread_settings_applied")
        {
            ApplyThreadSettings(root, eventPayload, state);
            return;
        }

        HandleTokenCount(root, eventPayload, state, diagnostics, steps);
    }

    private static bool IsSessionUsageType(string? type, string? payloadType) =>
        type is "turn_context" or "token_usage_record" or "compacted" ||
        (type == "event_msg" && payloadType is "token_count" or "thread_settings_applied");

    /// <summary>
    /// <c>session_meta</c>: fork to <c>forked_from_id</c> lub <c>thread_spawn.parent_thread_id</c>, chyba że historia jest
    /// referencją (<c>history_base</c>) albo plik to rollout zastępczy <c>rollout-…-&lt;wątek&gt;_&lt;rollout&gt;</c> z id równym
    /// wątkowi (ccusage <c>replay.rs:430-470</c>).
    /// </summary>
    private static SessionMeta ReadSessionMeta(JsonElement root, JsonElement meta, string path)
    {
        var id = meta.Prop("id") is { ValueKind: JsonValueKind.String } idElement ? idElement.GetString() : null;
        var spawnParent = meta.Path("source", "subagent", "thread_spawn", "parent_thread_id") is { ValueKind: JsonValueKind.String } spawn &&
                          spawn.GetString() is { Length: > 0 } spawnId
            ? spawnId
            : null;
        var lineageParent = meta.Prop("forked_from_id") is { ValueKind: JsonValueKind.String } forked && forked.GetString() is { Length: > 0 } forkedId
            ? forkedId
            : spawnParent;

        var referenceHistory = meta.Prop("history_base") is { ValueKind: JsonValueKind.Object };
        var replacement = RolloutIdentity(System.IO.Path.GetFileName(path)) is { } identity &&
                          identity.Thread != identity.Rollout && id == identity.Thread;
        var parent = referenceHistory || replacement ? null : lineageParent;

        return new SessionMeta(
            string.IsNullOrWhiteSpace(id) ? null : id,
            parent,
            root.Prop("timestamp").TimestampMs() ?? meta.Prop("timestamp").TimestampMs(),
            spawnParent is not null,
            lineageParent);
    }

    /// <summary>
    /// <c>thread_settings_applied</c> z poprawnym timestampem: zapisany <c>service_tier</c> ustawia poziom kolejnych wywołań;
    /// nieznana wartość przywraca stan „niesklasyfikowany” (czyli <c>config.toml</c>), brak klucza zostawia poprzedni poziom
    /// (ccusage <c>parser.rs:501-514</c>, <c>types.rs:54-64</c>, #1438).
    /// </summary>
    private static void ApplyThreadSettings(JsonElement root, JsonElement payload, State state)
    {
        var timestamp = root.Prop("timestamp");
        var valid = timestamp switch
        {
            { ValueKind: JsonValueKind.String } text => !string.IsNullOrWhiteSpace(text.GetString()),
            { ValueKind: JsonValueKind.Number } number => number.TryGetInt64(out var value) && value >= 0,
            _ => false
        };
        if (!valid)
        {
            return;
        }

        if (payload.Path("thread_settings", "service_tier") is { ValueKind: JsonValueKind.String } tier)
        {
            state.Tier = ParseTier(tier.GetString());
        }
    }

    private static void HandleTokenCount(JsonElement root, JsonElement eventPayload, State state, ParseDiagnostics diagnostics, List<Step> steps)
    {
        if (eventPayload.Prop("info") is not { ValueKind: JsonValueKind.Object } info)
        {
            return; // token_count bez info = tylko limity
        }

        var (lastState, lastUsage) = ReadUsage(info.Prop("last_token_usage"), lossy: true);
        var (totalState, totalUsage) = ReadUsage(info.Prop("total_token_usage"), lossy: true);
        if (lastState == UsageState.Malformed || totalState == UsageState.Malformed)
        {
            diagnostics.Skip("token_count: niepoprawne usage");
            return;
        }

        RawUsage? total = totalState == UsageState.Decoded ? totalUsage : null;
        RawUsage? last = lastState == UsageState.Decoded ? lastUsage : null;
        var advanced = total is null || state.PreviousTotals != total;

        RawUsage? delta = null;
        if (advanced && last is not null)
        {
            delta = last;
        }
        else if (total is not null)
        {
            delta = total.Value.Minus(state.PreviousTotals);
        }

        if (total is not null)
        {
            state.PreviousTotals = total;
        }

        // Tylko pierwszy rosnący token_count po rekordzie może go nazwać (ccusage parser.rs:539-540).
        var recordAwaitsSnapshot = advanced && state.RecordAwaitsSnapshot;
        if (advanced)
        {
            state.RecordAwaitsSnapshot = false;
        }

        var timestamp = root.Prop("timestamp").TimestampMs();
        if (timestamp is null)
        {
            if (last is not null || total is not null)
            {
                diagnostics.Skip("brak lub zły timestamp");
            }

            return;
        }

        if (delta is not { IsEmpty: false } usage)
        {
            return;
        }

        // token_count potwierdzający oczekujący rekord odpowiedzi — rekord nie jest liczony osobno (#1821),
        // a token_count dostaje jego response_id jako klucz (ccusage parser.rs:547-566).
        string? recordId = null;
        if (advanced && state.LatestResponseId is { } latest && state.Pending.TryGetValue(latest, out var awaiting) &&
            (awaiting.Event.Raw == usage || (awaiting.Thread is not null && total is not null && awaiting.Thread == total)))
        {
            recordId = recordAwaitsSnapshot ? latest : null;
            state.Pending.Remove(latest);
            state.EmittedIds.Add(latest);
        }

        if ((ModelFrom(eventPayload) ?? ModelFrom(info)) is { } model)
        {
            state.Model = model;
        }

        var entry = CreateEntry(state, timestamp.Value, usage, null, state.Model, state.Model is null, state.Tier);
        steps.Add(new Step(new ParsedEvent(entry, usage, Replayable: true, TokenCount: true, recordId), null, null));
    }

    private static void HandleUsageRecord(JsonElement root, JsonElement record, State state, ParseDiagnostics diagnostics, List<Step> steps)
    {
        var responseId = record.Str("response_id");
        if (responseId is null || state.EmittedIds.Contains(responseId))
        {
            return;
        }

        var (usageState, usage) = ReadUsage(record.Prop("usage"), lossy: false);
        if (usageState == UsageState.Missing)
        {
            return;
        }

        var (threadState, threadUsage) = ReadUsage(record.Prop("thread_token_usage"), lossy: true);
        if (usageState == UsageState.Malformed || threadState == UsageState.Malformed)
        {
            diagnostics.Skip("token_usage_record: niepoprawne usage");
            return;
        }

        var timestamp = root.Prop("timestamp").TimestampMs();
        if (timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
            return;
        }

        if (usage.IsEmpty)
        {
            return;
        }

        // Rekord bez własnego modelu dziedziczy bieżący, ale jest oznaczony jako zastępczy (ccusage parser.rs:437-460).
        var model = ModelFrom(record);
        var entry = CreateEntry(state, timestamp.Value, usage, CodexDedupe.ResponseKeyPrefix + responseId, model ?? state.Model, model is null, state.Tier);
        var parsed = new ParsedEvent(entry, usage, Replayable: false);

        state.LatestResponseId = responseId;
        state.RecordAwaitsSnapshot = true;
        if (state.CompactedIds.Contains(responseId))
        {
            if (state.EmittedIds.Add(responseId))
            {
                steps.Add(new Step(parsed, null, null));
            }
        }
        else
        {
            state.Pending.TryAdd(responseId, (parsed, threadState == UsageState.Decoded ? threadUsage : null));
        }
    }

    /// <summary>
    /// Linia <c>codex exec --json</c>: pierwsze poprawne <c>usage</c>, <c>data.usage</c>, <c>result.usage</c> lub
    /// <c>response.usage</c>; czas z <c>timestamp</c>/<c>created_at</c>/<c>createdAt</c> (także w tych obiektach), w ostateczności
    /// czas modyfikacji pliku (ccusage <c>parser/exec.rs</c>, <c>parser/usage.rs</c>, <c>parser/timestamp.rs</c>).
    /// </summary>
    private static void ParseExecLine(JsonElement root, State state, ParseDiagnostics diagnostics, List<Step> steps)
    {
        var containers = new[] { "data", "result", "response" }
            .Select(name => root.Prop(name) is { ValueKind: JsonValueKind.Object } container ? container : (JsonElement?)null)
            .ToArray();

        var malformed = false;
        RawUsage? found = null;
        foreach (var candidate in new[] { root.Prop("usage") }.Concat(containers.Select(container => container?.Prop("usage"))))
        {
            var (usageState, usage) = ReadUsage(candidate, lossy: false);
            if (usageState == UsageState.Decoded)
            {
                found = usage;
                break;
            }

            malformed |= usageState == UsageState.Malformed;
        }

        if (found is not { } raw)
        {
            if (malformed)
            {
                diagnostics.Skip("exec: niepoprawne usage");
            }

            return;
        }

        if (raw.IsEmpty)
        {
            return;
        }

        var timestamp = ExecTimestamp(root) ?? containers.Select(container => container is { } value ? ExecTimestamp(value) : null)
            .FirstOrDefault(value => value is not null);
        timestamp ??= state.FileModifiedMs ??= FileModifiedMs(state.Path);

        var parsedModel = ModelFrom(root) ?? containers.Select(container => container is { } value ? ModelFrom(value) : null)
            .FirstOrDefault(value => value is not null);
        if (parsedModel is not null)
        {
            state.Model = parsedModel;
        }

        var signature = $"{timestamp}:{state.Model}:{raw.Input}:{raw.Cached}:{raw.CacheCreation}:{raw.Output}:{raw.Reasoning}:{raw.Total}";
        var occurrence = state.ExecOccurrences.TryGetValue(signature, out var count) ? count : 0;
        state.ExecOccurrences[signature] = occurrence + 1;

        // Exec nie zapisuje poziomu usługi — tylko config.toml.
        var entry = CreateEntry(state, timestamp.Value, raw, $"exec:{signature}:{occurrence}", state.Model, state.Model is null, SpeedTier.Unknown);
        steps.Add(new Step(new ParsedEvent(entry, raw, Replayable: true), null, null));
    }

    private static long? ExecTimestamp(JsonElement element) =>
        element.Prop("timestamp").TimestampMs() ?? element.Prop("created_at").TimestampMs() ?? element.Prop("createdAt").TimestampMs();

    private static long FileModifiedMs(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static UsageEntry CreateEntry(State state, long timestamp, RawUsage usage, string? dedupeKey, string? model, bool isFallback, SpeedTier tier)
    {
        model ??= FallbackModel;

        // codex-auto-review: serwer kieruje na model zależny od daty; wpis dostaje ten model i flagę zastępczego (#1753).
        if (model == AutoReviewModel)
        {
            model = ModelAliases.ForPricing(model, timestamp) ?? FallbackModel;
            isFallback = true;
        }

        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = timestamp,
            SessionId = state.SessionId,
            Project = state.Cwd is null ? null : Path.GetFileName(state.Cwd.TrimEnd('/', '\\')),
            ProjectPath = state.Cwd,
            Model = model,
            IsFallbackModel = isFallback,
            InputTokens = Math.Max(0, usage.Input - usage.Cached - usage.CacheCreation),
            CacheReadTokens = usage.Cached,
            CacheCreationTokens = usage.CacheCreation,
            OutputTokens = usage.Output,
            ReasoningTokens = usage.Reasoning,
            Speed = tier != SpeedTier.Unknown ? tier : state.ConfiguredTier,

            // Poziom zapisany w logu ma pierwszeństwo przed poziomem z config.toml przy duplikatach (DuplicateComparer).
            Preference = tier != SpeedTier.Unknown ? 1 : 0,
            DedupeKey = dedupeKey
        };
    }

    /// <summary>Długość odziedziczonego prefiksu: wspólny prefiks albo najdłuższy sufiks rodzica będący prefiksem dziecka.</summary>
    internal static int ReplayedParentLength(IReadOnlyList<RawUsage> parent, IReadOnlyList<RawUsage> child)
    {
        var prefix = 0;
        while (prefix < parent.Count && prefix < child.Count && parent[prefix] == child[prefix])
        {
            prefix++;
        }

        var patternLength = Math.Min(child.Count, parent.Count);
        var suffix = 0;
        for (var length = patternLength; length > prefix; length--)
        {
            var match = true;
            for (var index = 0; index < length && match; index++)
            {
                match = parent[parent.Count - length + index] == child[index];
            }

            if (match)
            {
                suffix = length;
                break;
            }
        }

        return Math.Max(prefix, suffix);
    }

    private string? FindSessionFile(string root, string sessionId, string excludePath)
    {
        var index = Index(root, known => known.Files.ContainsKey(sessionId));
        return index.Files.TryGetValue(sessionId, out var path) &&
               !string.Equals(path, excludePath, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    /// <summary>
    /// Indeks rolloutów katalogu z pierwszych linii <c>session_meta</c>: id wątku (i uuid z nazwy pliku) → plik,
    /// wątek → rodzic w linii (ccusage <c>CodexLineage</c>, <c>dedupe.rs:133-157</c>).
    /// </summary>
    private sealed record SessionIndex(
        DateTime Built,
        Dictionary<string, string> Files,
        Dictionary<string, string> Parents)
    {
        public static SessionIndex Empty { get; } = new(DateTime.MinValue, [], []);
    }

    /// <summary>Indeks katalogu; przebudowywany, gdy brak szukanego elementu, nie częściej niż co 30 s.</summary>
    private SessionIndex Index(string root, Func<SessionIndex, bool> contains)
    {
        var index = _sessionIndex.GetOrAdd(root, _ => SessionIndex.Empty);
        if (contains(index) || DateTime.UtcNow - index.Built <= TimeSpan.FromSeconds(30))
        {
            return index;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in RolloutFiles(root))
        {
            files.TryAdd(SessionIdFromFileName(file), file);
            if (ReadLineage(file) is not ({ } thread, var parent))
            {
                continue;
            }

            files.TryAdd(thread, file);
            if (parent is not null)
            {
                parents.TryAdd(thread, parent);
            }
        }

        index = new SessionIndex(DateTime.UtcNow, files, parents);
        _sessionIndex[root] = index;
        return index;
    }

    /// <summary>
    /// Wątek pliku i jego przodkowie od rodzica z własnego <c>session_meta</c> w górę, z indeksu katalogu pliku (ccusage
    /// <c>CodexLineage::ancestors</c>, <c>dedupe.rs:146-157</c>). Bez id wątku — brak linii (ccusage <c>record_lineage</c>).
    /// </summary>
    private (string? Thread, string? Ancestors) Lineage(string root, SessionMeta meta)
    {
        if (meta.Id is not { } thread)
        {
            return (null, null);
        }

        var ancestors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { thread };
        var parent = meta.LineageParentId;
        while (parent is not null && seen.Add(parent))
        {
            ancestors.Add(parent);
            var current = parent;
            parent = Index(root, known => known.Files.ContainsKey(current)).Parents.GetValueOrDefault(current);
        }

        return (thread, CodexData.List(ancestors));
    }

    /// <summary>
    /// Id wątku i rodzic w linii z pierwszej linii <c>session_meta</c> (bez czytania całego pliku; ccusage
    /// <c>replay.rs:416-470</c>).
    /// </summary>
    private static (string? Thread, string? Parent)? ReadLineage(string path)
    {
        try
        {
            string? first;
            if (FileUtil.IsZstd(path))
            {
                using var stream = FileUtil.OpenShared(path);
                using var zstd = new System.IO.Compression.ZstandardStream(stream, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zstd);
                first = reader.ReadLine();
            }
            else
            {
                first = FileUtil.ReadLines(path).FirstOrDefault();
            }

            if (first is null ||
                !(first.Contains("session_meta", StringComparison.Ordinal) || SpellsWithEscapes(first, ["session_meta"])) ||
                !JsonUtil.TryParse(first, out var document))
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.Str("type") != "session_meta" ||
                    root.Prop("payload") is not { ValueKind: JsonValueKind.Object } payload)
                {
                    return null;
                }

                var meta = ReadSessionMeta(root, payload, path);
                return (meta.Id, meta.LineageParentId);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Czy któryś znacznik jest zapisany z ucieczką <c>\uXXXX</c> (np. <c>"us\u0061ge"</c>, <c>"\u0074ype"</c>). Ucieczki
    /// znaków ASCII są dekodowane, pozostałe (<c>\\</c>, <c>\"</c>…) zostają jak w tekście; parser JSON czyta taką linię tak
    /// samo jak zapisaną wprost (ccusage <c>LinePrefilter::matches_escaped</c>, <c>parser.rs:598-604</c>).
    /// </summary>
    internal static bool SpellsWithEscapes(string line, IReadOnlyList<string> markers)
    {
        if (!line.Contains("\\u", StringComparison.Ordinal))
        {
            return false;
        }

        var decoded = new System.Text.StringBuilder(line.Length);
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character != '\\' || index + 1 >= line.Length)
            {
                decoded.Append(character);
                continue;
            }

            if (line[index + 1] == 'u' && index + 5 < line.Length &&
                ushort.TryParse(line.AsSpan(index + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var code) && code is >= 0x20 and < 0x7F)
            {
                decoded.Append((char)code);
                index += 5;
                continue;
            }

            decoded.Append(character).Append(line[index + 1]);
            index++;
        }

        var text = decoded.ToString();
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static string? ModelFrom(JsonElement element) =>
        element.Str("model") ?? element.Str("model_name") ?? element.Path("metadata", "model").Str();

    /// <summary>Nazwa poziomu z logów lub <c>config.toml</c> (ccusage <c>CodexServiceTier::from_codex_name</c>); nieznana → <see cref="SpeedTier.Unknown"/>.</summary>
    internal static SpeedTier ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        // Codex Desktop zapisuje "standard", CLI "default" (#1438).
        "default" or "standard" => SpeedTier.Standard,
        "fast" or "priority" => SpeedTier.Fast,
        "flex" => SpeedTier.Flex,
        "ultrafast" => SpeedTier.Ultrafast,
        _ => SpeedTier.Unknown
    };

    /// <summary>
    /// Klucz sesji: ścieżka rolloutu względem katalogu <c>sessions</c>/<c>archived_sessions</c> (lub katalogu głównego),
    /// bez <c>.zst</c> i <c>.jsonl</c>, z separatorem <c>/</c> (ccusage <c>parser.rs:704-716</c>).
    /// </summary>
    internal static string SessionKey(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var slash = relative.IndexOf('/');
        if (slash > 0 && relative[..slash] is "sessions" or "archived_sessions")
        {
            relative = relative[(slash + 1)..];
        }

        relative = StripZst(relative);
        if (relative.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[..^".jsonl".Length];
        }

        return relative.Length == 0 ? "unknown" : relative;
    }

    private static string StripZst(string path) =>
        path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase) ? path[..^".zst".Length] : path;

    /// <summary>
    /// Nazwa <c>rollout-RRRR-MM-DDTGG-MM-SS-&lt;wątek&gt;[_&lt;rollout&gt;].jsonl[.zst]</c> → (wątek, rollout); bez części
    /// <c>_rollout</c> oba są równe (ccusage <c>session_id.rs</c> <c>rollout_identity</c>).
    /// </summary>
    internal static (string Thread, string Rollout)? RolloutIdentity(string fileName)
    {
        var name = fileName;
        if (name.StartsWith("codex://threads/", StringComparison.Ordinal))
        {
            name = name["codex://threads/".Length..];
        }

        name = name.EndsWith(".zst", StringComparison.Ordinal) ? name[..^4] : name;
        name = name.EndsWith(".jsonl", StringComparison.Ordinal) ? name[..^6] : name;
        name = name[(name.LastIndexOf('/') + 1)..];
        if (!name.StartsWith("rollout-", StringComparison.Ordinal))
        {
            return null;
        }

        name = name["rollout-".Length..];
        if (name.Length < 20 || !RolloutTimestampRegex().IsMatch(name[..20]))
        {
            return null;
        }

        var ids = name[20..];
        var underscore = ids.IndexOf('_');
        var (thread, rollout) = underscore >= 0 ? (ids[..underscore], ids[(underscore + 1)..]) : (ids, ids);
        return IsCodexUuid(thread) && IsCodexUuid(rollout) ? (thread, rollout) : null;
    }

    private static bool IsCodexUuid(string value) => value.Length == 36 && UuidRegex().IsMatch(value);

    /// <summary>Nazwa pliku <c>rollout-2025-09-01T10-00-00-&lt;uuid&gt;.jsonl[.zst]</c> → uuid (indeks rodziców forków).</summary>
    private static string SessionIdFromFileName(string path)
    {
        var name = Path.GetFileName(path);
        name = name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        name = Path.GetFileNameWithoutExtension(name);
        var match = UuidRegex().Match(name);
        return match.Success ? match.Value : name;
    }

    private static long ConfigModifiedMs(string root)
    {
        var config = Path.Combine(root, "config.toml");
        return File.Exists(config) ? new DateTimeOffset(File.GetLastWriteTimeUtc(config)).ToUnixTimeMilliseconds() : 0;
    }

    /// <summary>Poziom z <c>config.toml</c> (TOML, profil wybrany przez <c>profile</c>; ccusage <c>speed.rs</c>), z pamięcią podręczną.</summary>
    private static SpeedTier ReadConfiguredTier(string root)
    {
        var config = Path.Combine(root, "config.toml");
        var modified = ConfigModifiedMs(root);
        if (modified == 0)
        {
            return SpeedTier.Unknown;
        }

        if (ConfigTiers.TryGetValue(config, out var cached) && cached.Modified == modified)
        {
            return cached.Tier;
        }

        SpeedTier tier;
        try
        {
            tier = CodexToml.ServiceTier(FileUtil.ReadAllText(config));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SpeedTier.Unknown;
        }

        ConfigTiers[config] = (modified, tier);
        return tier;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidRegex();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}-$")]
    private static partial Regex RolloutTimestampRegex();
}
