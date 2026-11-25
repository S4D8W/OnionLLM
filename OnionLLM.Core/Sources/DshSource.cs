using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// DeepSeek Harness (<c>dsh</c>) — logi sesji <c>sessions/&lt;projekt&gt;/&lt;sesja&gt;/session[.vN].jsonl[.zstd]</c>.
/// Katalogi: <c>DSH_HOME</c> (lista) lub <c>~/.dsh</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>W katalogu sesji czytana jest tylko najnowsza generacja <c>vN</c> (<c>session.jsonl</c> = 0).</item>
/// <item>Pierwsza linia to nagłówek <c>type: session</c> (id, createdAt, cwd, isSeeded).</item>
/// <item><c>request/header</c> i <c>request/context</c> ustawiają model; zużycie z <c>assistant/message</c>,
/// <c>assistant/attempt</c> (gdy brak <c>data.usage</c> — ostatni fragment <c>usage</c> ze <c>stream</c>) i <c>compaction/summary</c>.</item>
/// <item>Fork kopiuje log rodzica do znacznika <c>session/end-seed</c> z <c>inherited</c> — liczymy tylko to, co po ostatnim znaczniku;
/// log z <c>isSeeded</c> bez znacznika to wyłącznie zużycie rodzica.</item>
/// <item>Liczniki są rozłączne (<c>inputTokens</c> bez cache). Deduplikacja po (sesja, <c>seq</c>); <c>seq</c> zapisane
/// jako tekst lub ułamek jest pomijane jak w ccusage (<c>lenient_i64</c>) i wtedy klucz tworzą czas, model i liczniki.
/// Z duplikatów zostaje kopia z większą liczbą tokenów, potem wcześniejsza (<see cref="DuplicateComparer"/>).</item>
/// <item>Dostawca trasy (<c>provider</c>) trafia do <see cref="UsageEntry.Provider"/>; konta DeepSeek
/// (<c>deepseek-official</c>, <c>deepseek-account</c>) to bezpośrednie rozliczenie <c>deepseek</c>.</item>
/// </list>
/// </remarks>
public sealed class DshSource : UsageSourceBase
{
    public const string SourceId = "dsh";

    /// <summary>Identyfikator dostawcy dla bezpośredniego rozliczenia DeepSeek (harmonogram szczyt/poza szczytem).</summary>
    public const string DeepSeekProvider = "deepseek";

    /// <summary>2: dostawca trasy (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;
    public const string UnknownModel = "unknown";

    private const string CompressedSuffix = ".zstd";

    public override string Id => SourceId;
    public override string DisplayName => "DeepSeek Harness";
    public override string Vendor => "DeepSeek";
    public override string AccentColor => "#4D6BFE";

    protected override string[] EnvironmentVariables => ["DSH_HOME"];

    protected override string[] Executables => ["dsh"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".dsh")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            // Najnowsza generacja logu w każdym katalogu sesji.
            var newest = new Dictionary<string, (int Generation, string Path)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in FileUtil.SafeEnumerateFiles(Path.Combine(root, "sessions"), "session*"))
            {
                if (SessionGeneration(Path.GetFileName(file)) is not { } generation)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(file) ?? "";
                if (!newest.TryGetValue(directory, out var current) || generation > current.Generation)
                {
                    newest[directory] = (generation, file);
                }
            }

            foreach (var (_, path) in newest.Values.OrderBy(value => value.Path, StringComparer.Ordinal))
            {
                yield return SourceFile.Create(path, root);
            }
        }
    }

    /// <summary><c>session.jsonl</c> → 0, <c>session.v4.jsonl</c> → 4 (także z sufiksem <c>.zstd</c>); inne → null.</summary>
    internal static int? SessionGeneration(string name)
    {
        if (name.EndsWith(CompressedSuffix, StringComparison.Ordinal))
        {
            name = name[..^CompressedSuffix.Length];
        }

        if (!name.StartsWith("session", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return null;
        }

        var stem = name["session".Length..^".jsonl".Length];
        if (stem.Length == 0)
        {
            return 0;
        }

        if (!stem.StartsWith(".v", StringComparison.Ordinal) || stem.Length == 2 || !stem[2..].All(char.IsAsciiDigit))
        {
            return null;
        }

        return int.TryParse(stem[2..], out var generation) ? generation : null;
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var fold = new SessionFold(file.Path);
        foreach (var line in ReadLines(file.Path, diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fold.Record(line, diagnostics);
        }

        return fold.Finish(diagnostics);
    }

    /// <summary>Linie logu; dla <c>.zstd</c> — dekompresja ramek, a ucięta ostatnia ramka zachowuje wcześniejsze linie.</summary>
    private static IEnumerable<string> ReadLines(string path, ParseDiagnostics diagnostics)
    {
        if (!path.EndsWith(CompressedSuffix, StringComparison.Ordinal))
        {
            foreach (var line in FileUtil.ReadLines(path))
            {
                yield return line;
            }

            yield break;
        }

        var lines = new List<string>();
        var pending = new StringBuilder();
        try
        {
            using var stream = FileUtil.OpenShared(path);
            using var zstd = new ZstandardStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(zstd, Encoding.UTF8);
            var buffer = new char[16 * 1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        lines.Add(pending.ToString().TrimEnd('\r'));
                        pending.Clear();
                    }
                    else
                    {
                        pending.Append(buffer[i]);
                    }
                }
            }

            if (pending.Length > 0)
            {
                lines.Add(pending.ToString());
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // Ucięta / uszkodzona ramka: zostają pełne linie sprzed niej, niedokończona linia jest odrzucana.
            diagnostics.Skip("ucięty lub uszkodzony strumień zstd");
        }

        foreach (var line in lines)
        {
            yield return line;
        }
    }

    private sealed class SessionFold(string path)
    {
        private bool _headerRead;
        private bool _headerAccepted;
        private string _sessionId = "";
        private long? _createdAt;
        private string? _cwd;
        private bool _awaitingSeedEnd;
        private string? _model;
        private string? _provider;
        private int _undated;
        private readonly List<UsageEntry> _entries = [];

        public void Record(string line, ParseDiagnostics diagnostics)
        {
            if (!_headerRead)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ReadHeader(line, diagnostics);
                }

                return;
            }

            // Prefiltr luźny: typy bywają zapisane z ucieczką ukośnika (request\/context).
            if (!_headerAccepted ||
                !(line.Contains("request", StringComparison.Ordinal) ||
                  line.Contains("end-seed", StringComparison.Ordinal) ||
                  line.Contains("assistant", StringComparison.Ordinal) ||
                  line.Contains("compaction", StringComparison.Ordinal)))
            {
                return;
            }

            if (!JsonUtil.TryParse(line, out var document))
            {
                diagnostics.Skip("niepoprawny JSON");
                return;
            }

            using (document)
            {
                RecordEvent(document.RootElement);
            }
        }

        private void ReadHeader(string line, ParseDiagnostics diagnostics)
        {
            _headerRead = true;
            if (!JsonUtil.TryParse(line, out var document))
            {
                diagnostics.Skip("niepoprawny JSON");
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.Str("type") != "session")
                {
                    diagnostics.Skip("pierwszy rekord nie jest nagłówkiem sesji");
                    return;
                }

                _headerAccepted = true;
                _awaitingSeedEnd = root.Prop("isSeeded") is { ValueKind: JsonValueKind.True };
                _sessionId = root.Str("id") ?? SessionIdFromPath(path);
                _createdAt = Millis(root.Prop("createdAt"));
                _cwd = root.Str("cwd");
            }
        }

        private void RecordEvent(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || root.Prop("data") is not { ValueKind: JsonValueKind.Object } data)
            {
                return;
            }

            JsonElement? usage;
            string? model;
            string? provider;
            switch (root.Str("type"))
            {
                case "request/header":
                    if (data.Path("header", "config") is { ValueKind: JsonValueKind.Object } config)
                    {
                        UpdateRoute(config.Str("provider"), config.Str("model"));
                    }

                    return;
                case "request/context":
                    UpdateRoute(data.Str("provider"), data.Str("model"));
                    return;
                case "session/end-seed":
                    // Fork może mieć kilka znaczników dziedziczenia — liczy się tylko to, co po ostatnim.
                    if (data.Prop("inherited") is { ValueKind: JsonValueKind.True })
                    {
                        _entries.Clear();
                        _undated = 0;
                        _awaitingSeedEnd = false;
                    }

                    return;
                case "assistant/message" or "assistant/attempt":
                    usage = SettledUsage(data);
                    model = _model;
                    provider = _provider;
                    break;
                case "compaction/summary":
                    usage = data.Prop("usage") is { ValueKind: JsonValueKind.Object } summaryUsage ? summaryUsage : null;
                    model = data.Str("model");
                    provider = data.Str("provider");
                    break;
                default:
                    return;
            }

            if (usage is not { } counters)
            {
                return;
            }

            var inputElement = counters.Prop("inputTokens");
            var input = inputElement.Long();
            var cacheRead = counters.Long("cacheReadTokens");
            var cacheWrite = counters.Long("cacheWriteTokens");
            var total = counters.Long("totalTokens");
            // totalTokens = output + cały prompt: brakujący output da się odtworzyć, gdy input jest zapisany.
            var output = counters.Prop("outputTokens") is { ValueKind: not JsonValueKind.Null } outputElement
                ? ((JsonElement?)outputElement).Long()
                : inputElement is { ValueKind: not JsonValueKind.Null } ? Math.Max(0, total - input - cacheRead - cacheWrite) : 0;
            var sum = input + output + cacheRead + cacheWrite;
            var remainder = Math.Max(0, total - sum);
            if (sum + remainder == 0)
            {
                return;
            }

            var timestamp = Millis(root.Prop("time")) ?? _createdAt;
            if (timestamp is null)
            {
                _undated++;
                return;
            }

            var seq = root.Prop("seq") is { ValueKind: JsonValueKind.Number } seqElement && seqElement.TryGetInt64(out var number)
                ? number
                : (long?)null;
            var effectiveModel = model ?? UnknownModel;
            var replayOutput = output == 0 ? remainder : output;
            _entries.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = timestamp.Value,
                SessionId = _sessionId,
                Project = _cwd is null ? SessionProject(path) : Path.GetFileName(_cwd.TrimEnd('/', '\\')),
                ProjectPath = _cwd,
                Model = effectiveModel,
                IsFallbackModel = model is null,
                InputTokens = input,
                OutputTokens = output,
                CacheCreationTokens = cacheWrite,
                CacheReadTokens = cacheRead,
                Provider = BillingProvider(provider),
                DedupeKey = seq is { } s
                    ? $"{_sessionId}:{s}"
                    : $"{_sessionId}:{timestamp.Value}:{effectiveModel}:{input}:{replayOutput}:{cacheWrite}:{cacheRead}"
            });
        }

        /// <summary>Rekord bez modelu nie zmienia trasy; z modelem ustawia też dostawcę (także pustego) — ccusage <c>update_route</c>.</summary>
        private void UpdateRoute(string? provider, string? model)
        {
            if (!string.IsNullOrEmpty(model))
            {
                _provider = string.IsNullOrEmpty(provider) ? null : provider;
                _model = model;
            }
        }

        public List<UsageEntry> Finish(ParseDiagnostics diagnostics)
        {
            if (_awaitingSeedEnd)
            {
                return []; // log zasiany bez znacznika końca = wyłącznie zużycie rodzica
            }

            for (var i = 0; i < _undated; i++)
            {
                diagnostics.Skip("żądanie bez time i bez createdAt w nagłówku");
            }

            return _entries;
        }
    }

    /// <summary>
    /// Dostawca do wpisu: konta DeepSeek (<c>deepseek</c>, <c>deepseek-official</c>, <c>deepseek-account</c>, także z <c>_</c>)
    /// → <see cref="DeepSeekProvider"/> (ccusage <c>billing_route</c> → <c>Direct("deepseek")</c>), pozostali znormalizowani.
    /// </summary>
    internal static string? BillingProvider(string? logged)
    {
        var normalized = Pricing.ProviderNames.Normalize(logged);
        return normalized?.Replace('_', '-') is "deepseek" or "deepseek-official" or "deepseek-account"
            ? DeepSeekProvider
            : normalized;
    }

    /// <summary>
    /// Kandydaci cennika (ccusage <c>dsh/src/parser.rs</c> <c>model_candidates</c>): przy bezpośrednim rozliczeniu DeepSeek
    /// nazwa z logu, potem <c>deepseek/</c>…; przy innym dostawcy najpierw <c>dostawca/model</c>, potem nazwa z logu.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Provider, entry.Model);

    internal static IReadOnlyList<string> ModelCandidates(string? provider, string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        if (provider == DeepSeekProvider)
        {
            return [model, $"{DeepSeekProvider}/{model}"];
        }

        return Pricing.ProviderNames.Qualified(model, provider) is { } qualified && qualified != model
            ? [qualified, model]
            : [model];
    }

    /// <summary>
    /// Wybór między kopiami tego samego żądania (ccusage <c>DshUsageEntry::preferred_over</c>): więcej tokenów łącznie,
    /// potem więcej wyjścia, wejścia i zapisu do cache, potem wcześniejszy znacznik czasu, na końcu model, dostawca i cwd.
    /// </summary>
    public override IComparer<UsageEntry>? DuplicateComparer { get; } = Comparer<UsageEntry>.Create((left, right) =>
    {
        var rank = (left.TotalTokens, left.OutputTokens, left.InputTokens, left.CacheCreationTokens)
            .CompareTo((right.TotalTokens, right.OutputTokens, right.InputTokens, right.CacheCreationTokens));
        if (rank != 0)
        {
            return rank;
        }

        var earlier = right.TimestampMs.CompareTo(left.TimestampMs);
        if (earlier != 0)
        {
            return earlier;
        }

        foreach (var (a, b) in new[] { (left.Model, right.Model), (left.Provider, right.Provider), (left.ProjectPath, right.ProjectPath) })
        {
            var order = string.CompareOrdinal(a, b);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    });

    /// <summary>Ustalone zużycie: <c>data.usage</c>, a gdy brak — ostatni fragment <c>usage</c> w <c>data.stream</c>.</summary>
    private static JsonElement? SettledUsage(JsonElement data)
    {
        if (data.Prop("usage") is { ValueKind: JsonValueKind.Object } usage)
        {
            return usage;
        }

        if (data.Prop("stream") is not { ValueKind: JsonValueKind.Array } stream)
        {
            return null;
        }

        JsonElement? last = null;
        foreach (var record in stream.EnumerateArray())
        {
            if (record.Str("type") == "chunk" &&
                record.Prop("chunk") is { ValueKind: JsonValueKind.Object } chunk &&
                chunk.Str("type") == "usage" &&
                chunk.Prop("usage") is { ValueKind: JsonValueKind.Object } chunkUsage)
            {
                last = chunkUsage;
            }
        }

        return last;
    }

    private static long? Millis(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        long? millis = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (long)real,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
        return millis > 0 ? millis : null;
    }

    private static string SessionIdFromPath(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(name) ? UnknownModel : name;
    }

    /// <summary>Katalog projektu: <c>sessions/&lt;projekt&gt;/&lt;sesja&gt;/plik</c>.</summary>
    private static string? SessionProject(string path)
    {
        var session = Path.GetDirectoryName(path);
        var project = session is null ? null : Path.GetFileName(Path.GetDirectoryName(session));
        return string.IsNullOrEmpty(project) ? null : project;
    }
}
