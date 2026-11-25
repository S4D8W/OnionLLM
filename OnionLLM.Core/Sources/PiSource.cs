using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// pi-agent (badlogic/pi-mono) — sesje JSONL w magazynie sesji <c>~/.pi/agent/sessions/&lt;projekt&gt;/*.jsonl</c>.
/// Katalogi: <c>PI_AGENT_DIR</c> (lista katalogów <b>sesji</b>, nie katalogów agenta) lub <c>~/.pi/agent/sessions</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Zużycie: linie <c>type: message</c> z <c>message.role = assistant</c> i <c>message.usage</c>
/// (<c>input</c>, <c>output</c>, <c>cacheRead</c>, <c>cacheWrite</c> — rozłączne; <c>cost.total</c> = koszt zapisany).</item>
/// <item>Projekt = pierwszy katalog pod magazynem; sesja = nazwa pliku po pierwszym <c>_</c>.</item>
/// <item>Pliki w katalogach <c>subagent-artifacts/</c> są kopiami debugowymi i są pomijane.</item>
/// <item>Fork (nagłówek <c>parentSession</c>) powtarza historię rodzica: pomijamy wiodący prefiks rekordów
/// zgodnych z aktywną gałęzią rodzica (do znacznika czasu forka), jak ccusage. Rodzic jest szukany we wszystkich
/// katalogach tego samego magazynu (ccusage <c>pi/src/loader.rs</c> <c>PiReplayPlan</c>), a odcisk pliku forka
/// obejmuje łańcuch rodziców, więc zmiana rodzica wymusza ponowne przeliczenie forka.</item>
/// <item>Magazyny: domyślny (<see cref="SourceSettings.Paths"/> i <see cref="SourceSettings.AgentDirs"/>, potem
/// <c>PI_AGENT_DIR</c>) oraz nazwane <see cref="SourceSettings.Stores"/> z modelem <c>[nazwa] model</c>
/// (zob. <c>PiSourceStores.cs</c>).</item>
/// </list>
/// </remarks>
public sealed partial class PiSource : UsageSourceBase
{
    public const string SourceId = "pi";

    /// <summary>Limit głębokości łańcucha forków (ochrona przed cyklami).</summary>
    private const int MaxLineageDepth = 64;

    public override string Id => SourceId;
    public override string DisplayName => "pi-agent";
    public override string Vendor => "pi";
    public override string AccentColor => "#E05D44";

    /// <summary>2: nazwane magazyny (<c>[nazwa] model</c>), forki między katalogami magazynu.</summary>
    public override int ParserVersion => 2;

    protected override string[] EnvironmentVariables => ["PI_AGENT_DIR"];

    protected override string[] Executables => ["pi"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".pi", "agent", "sessions")];

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        if (IsConfigErrorFile(file))
        {
            diagnostics.Skip(file.Root);
            yield break;
        }

        var store = StoreFor(file.Root);
        var project = ProjectFor(file.Path, file.Root);
        var sessionId = SessionIdFromPath(file.Path);
        var data = ReadSession(file.Path, diagnostics, cancellationToken);

        var skip = ReplayPrefixLength(file.Path, store.Roots, data, cancellationToken);
        foreach (var record in data.Usage.Skip(skip))
        {
            var model = store.Label(record.Model);
            yield return new UsageEntry
            {
                Source = SourceId,
                TimestampMs = record.Timestamp,
                SessionId = sessionId,
                Project = project,
                ProjectPath = data.Cwd,
                Model = model,
                InputTokens = record.Input,
                OutputTokens = record.Output,
                CacheCreationTokens = record.CacheWrite,
                CacheReadTokens = record.CacheRead,
                RecordedCostUsd = record.Cost,
                DedupeKey = string.Create(CultureInfo.InvariantCulture,
                    $"{project}:{sessionId}:{record.TimestampText}:{model}:{record.Input}:{record.ReplayOutput}:{record.CacheWrite}:{record.CacheRead}:{record.ReplayRemainder}:{record.Cost}")
            };
        }
    }

    internal sealed record UsageRecord(
        string TimestampText,
        long Timestamp,
        string? Model,
        long Input,
        long Output,
        long CacheWrite,
        long CacheRead,
        long Remainder,
        double? Cost,
        string? EntryId)
    {
        public long ReplayOutput => Output == 0 ? Remainder : Output;

        public long ReplayRemainder => Output == 0 ? 0 : Remainder;

        /// <summary>Sygnatura do porównania z rekordami rodzica (czas, model, tokeny, total, koszt).</summary>
        public bool SameUsage(UsageRecord other) =>
            Timestamp == other.Timestamp &&
            Model == other.Model &&
            Input == other.Input &&
            ReplayOutput == other.ReplayOutput &&
            CacheWrite == other.CacheWrite &&
            CacheRead == other.CacheRead &&
            Input + Output + CacheWrite + CacheRead + Remainder == other.Input + other.Output + other.CacheWrite + other.CacheRead + other.Remainder &&
            (Cost ?? 0).Equals(other.Cost ?? 0);
    }

    internal sealed class SessionData
    {
        public bool HasHeader { get; set; }
        public long? HeaderTimestamp { get; set; }
        public string? ParentSession { get; set; }
        public bool ParentMalformed { get; set; }
        public string? Cwd { get; set; }
        public List<UsageRecord> Usage { get; } = [];
        public List<(string? Id, string? ParentId)> Links { get; } = [];
    }

    internal static SessionData ReadSession(string path, ParseDiagnostics? diagnostics, CancellationToken cancellationToken, bool headerOnly = false)
    {
        var data = new SessionData();
        var index = 0;
        foreach (var line in FileUtil.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = index++ == 0;
            if (line.Length == 0)
            {
                continue;
            }

            if (!first && headerOnly)
            {
                break;
            }

            if (!JsonUtil.TryParse(line, out var document))
            {
                diagnostics?.Skip("niepoprawny JSON");
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = root.Str("type");
                if (first && type == "session")
                {
                    ReadHeader(root, data);
                }

                if (headerOnly)
                {
                    break;
                }

                if (type != "session")
                {
                    data.Links.Add((root.Str("id"), root.Str("parentId")));
                }

                var record = ReadUsage(root, type, diagnostics);
                if (record is not null)
                {
                    data.Usage.Add(record);
                }
            }
        }

        return data;
    }

    private static void ReadHeader(JsonElement root, SessionData data)
    {
        data.HasHeader = true;
        data.HeaderTimestamp = root.Prop("timestamp") is { ValueKind: JsonValueKind.String } ts ? ((JsonElement?)ts).TimestampMs() : null;
        data.Cwd = root.Str("cwd");
        if (root.Prop("parentSession") is { } parent)
        {
            // Pole obecne, ale puste / nie-tekstowe (także null) = linia niepewna; forka wtedy nie przycinamy.
            data.ParentSession = ((JsonElement?)parent).Str();
            data.ParentMalformed = data.ParentSession is null;
        }
    }

    private static UsageRecord? ReadUsage(JsonElement root, string? type, ParseDiagnostics? diagnostics)
    {
        if (type is not null && type != "message")
        {
            return null;
        }

        if (root.Prop("message") is not { ValueKind: JsonValueKind.Object } message ||
            message.Str("role") != "assistant" ||
            message.Prop("usage") is not { } usageValue)
        {
            return null;
        }

        if (usageValue.ValueKind != JsonValueKind.Object)
        {
            diagnostics?.Skip("niepoprawne pole usage");
            return null;
        }

        var input = usageValue.Long("input");
        var output = usageValue.Long("output");
        var cacheRead = usageValue.Long("cacheRead");
        var cacheWrite = usageValue.Long("cacheWrite");
        var sum = input + output + cacheRead + cacheWrite;
        var remainder = Math.Max(0, usageValue.Long("totalTokens") - sum);
        if (sum + remainder == 0)
        {
            return null;
        }

        var timestampText = root.Str("timestamp");
        var timestamp = timestampText is null ? null : root.Prop("timestamp").TimestampMs();
        if (timestamp is null)
        {
            diagnostics?.Skip("brak lub zły timestamp");
            return null;
        }

        double? cost = usageValue.Prop("cost") is { ValueKind: JsonValueKind.Object } costElement
            ? costElement.Prop("total").Double()
            : null;
        if (cost is { } value && (double.IsNaN(value) || double.IsInfinity(value) || value < 0))
        {
            cost = null;
        }

        return new UsageRecord(timestampText!, timestamp.Value, message.Str("model"), input, output, cacheWrite, cacheRead,
            remainder, cost, root.Str("id"));
    }

    /// <summary>
    /// Liczba wiodących rekordów forka, które powtarzają historię rodzica (0 = brak forka lub niepewna linia).
    /// </summary>
    internal static int ReplayPrefixLength(string childPath, IReadOnlyList<string> storeRoots, SessionData child, CancellationToken cancellationToken)
    {
        if (!child.HasHeader || child.HeaderTimestamp is not { } forkTimestamp || child.ParentMalformed ||
            child.ParentSession is null || child.Usage.Count == 0)
        {
            return 0;
        }

        var normalizedChild = FileUtil.NormalizePath(childPath);
        var parentPath = ResolveParent(child.ParentSession, normalizedChild, storeRoots);
        if (parentPath is null || string.Equals(parentPath, normalizedChild, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Cała linia przodków musi być poprawna (nagłówek z czasem, rozwiązywalny rodzic, brak cykli).
        if (!HasValidLineage(parentPath, storeRoots, normalizedChild, cancellationToken))
        {
            return 0;
        }

        // Niespójne drzewo dziecka — nie ryzykujemy odrzucenia zużycia.
        if (ActiveUsage(child) is null)
        {
            return 0;
        }

        SessionData parent;
        try
        {
            parent = ReadSession(parentPath, null, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var parentUsage = ActiveUsage(parent);
        if (parentUsage is null)
        {
            return 0;
        }

        // Porównujemy rekordy dziecka w kolejności fizycznej z aktywną gałęzią rodzica (do czasu forka).
        var matched = 0;
        foreach (var (childRecord, parentRecord) in child.Usage.Zip(parentUsage.TakeWhile(record => record.Timestamp <= forkTimestamp)))
        {
            if (!childRecord.SameUsage(parentRecord))
            {
                break;
            }

            matched++;
        }

        return matched;
    }

    private static bool HasValidLineage(string parentPath, IReadOnlyList<string> storeRoots, string childPath, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { childPath };
        var current = parentPath;
        for (var depth = 0; depth < MaxLineageDepth; depth++)
        {
            if (!visited.Add(current))
            {
                return false; // cykl
            }

            SessionData header;
            try
            {
                header = ReadSession(current, null, cancellationToken, headerOnly: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (!header.HasHeader || header.HeaderTimestamp is null || header.ParentMalformed)
            {
                return false;
            }

            if (header.ParentSession is null)
            {
                return true;
            }

            var next = ResolveParent(header.ParentSession, current, storeRoots);
            if (next is null)
            {
                return false;
            }

            current = next;
        }

        return false;
    }

    /// <summary>Ścieżka rodzica — musi istnieć w jednym z katalogów tego samego magazynu i nie być artefaktem subagenta.</summary>
    private static string? ResolveParent(string parentSession, string childPath, IReadOnlyList<string> storeRoots)
    {
        string candidate;
        try
        {
            candidate = Path.IsPathRooted(parentSession)
                ? FileUtil.NormalizePath(parentSession)
                : FileUtil.NormalizePath(Path.Combine(Path.GetDirectoryName(childPath) ?? "", parentSession));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!storeRoots.Any(storeRoot => candidate.StartsWith(FileUtil.NormalizePath(storeRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
            !candidate.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            IsSubagentArtifact(candidate) ||
            !File.Exists(candidate))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// Rekordy zużycia aktywnej gałęzi: dla sesji bez powiązań <c>id/parentId</c> (wersja 1) — kolejność fizyczna;
    /// dla drzewa — ścieżka od ostatniego wpisu (liścia) do korzenia. null = niespójne drzewo.
    /// </summary>
    internal static List<UsageRecord>? ActiveUsage(SessionData data)
    {
        if (data.Links.All(link => link.Id is null && link.ParentId is null))
        {
            return data.Usage;
        }

        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? leaf = null;
        foreach (var (id, parentId) in data.Links)
        {
            if (id is null || !parents.TryAdd(id, parentId))
            {
                return null;
            }

            leaf = id;
        }

        if (leaf is null)
        {
            return data.Usage;
        }

        if (parents.Values.Any(parent => parent is not null && !parents.ContainsKey(parent)))
        {
            return null;
        }

        var usageById = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var record in data.Usage)
        {
            if (record.EntryId is not null)
            {
                usageById.TryAdd(record.EntryId, record);
            }
        }

        var active = new List<UsageRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = leaf;
        while (current is not null)
        {
            if (!visited.Add(current))
            {
                return null; // cykl
            }

            if (usageById.TryGetValue(current, out var record))
            {
                active.Add(record);
            }

            current = parents[current];
        }

        active.Reverse();
        return active;
    }

    internal static string ProjectFor(string path, string storeRoot)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return "unknown";
        }

        var relative = Path.GetRelativePath(storeRoot, directory);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return "unknown";
        }

        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary><c>2025-12-27T10-00-00-000Z_&lt;uuid&gt;.jsonl</c> → <c>&lt;uuid&gt;</c>.</summary>
    internal static string SessionIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var index = name.IndexOf('_');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static bool IsSubagentArtifact(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment == "subagent-artifacts");
}
