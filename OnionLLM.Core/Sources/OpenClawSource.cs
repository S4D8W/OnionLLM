using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// OpenClaw (dawniej Clawdbot / Moltbot) — transkrypty sesji JSONL oraz bazy SQLite agentów.
/// <list type="bullet">
/// <item><c>&lt;root&gt;/**/&lt;uuid&gt;.jsonl</c> (typowo <c>agents/&lt;agent&gt;/sessions/</c>) oraz zarchiwizowane
/// <c>&lt;uuid&gt;.jsonl.deleted.&lt;ts&gt;</c> i <c>&lt;uuid&gt;.jsonl.reset.&lt;ts&gt;</c>.</item>
/// <item>OpenClaw 2026.9+: <c>&lt;root&gt;/agents/&lt;agent&gt;/agent/openclaw-agent.sqlite</c>, tabela <c>transcript_events</c>.</item>
/// </list>
/// Katalogi: <c>OPENCLAW_DIR</c> (lista) lub <c>~/.openclaw</c>, <c>~/.clawdbot</c>, <c>~/.moltbot</c>, <c>~/.moldbot</c>.
/// </summary>
/// <remarks>
/// Zdarzenia <c>model_change</c> i <c>custom/model-snapshot</c> ustawiają model dla kolejnych wiadomości.
/// Tokeny (<c>input</c>, <c>output</c>, <c>cacheRead</c>, <c>cacheWrite</c>) są rozłączne; <c>usage.cost.total</c> to koszt zapisany.
/// Deduplikacja jak ccusage (<c>openclaw/src/loader.rs</c>, #1727): wiersze SQLite mają pierwszeństwo przed kopiami
/// JSONL pozostawionymi przez <c>openclaw doctor --fix</c> (SQLite może mieć poprawiony koszt). Kopia JSONL odpada, gdy
/// SQLite ma zdarzenie o tej samej sesji, czasie, modelu i tokenach (bez kosztu). Zdarzenia SQLite z identyfikatorem
/// deduplikowane są po <c>(sesja, id)</c>, bez identyfikatora i linie JSONL — po treści razem z kosztem.
/// <see cref="UsageEntry.DedupeKey"/>: treść (sesja, czas, model, tokeny); <see cref="UsageEntry.SourceData"/>:
/// <c>koszt[␟id zdarzenia]</c>. Resztę robi <see cref="Reconcile"/>.
/// </remarks>
public sealed class OpenClawSource : UsageSourceBase
{
    public const string SourceId = "openclaw";
    public const string UnknownModel = "unknown";

    private const string DatabaseName = "openclaw-agent.sqlite";

    public override string Id => SourceId;
    public override string DisplayName => "OpenClaw";
    public override string Vendor => "OpenClaw";
    public override string AccentColor => "#FF5A36";

    /// <summary>2: dostawca, pierwszeństwo SQLite i klucze deduplikacji jak w ccusage; 3: koszt i id zdarzenia w <see cref="UsageEntry.SourceData"/>.</summary>
    public override int ParserVersion => 3;

    /// <summary>Separator części <see cref="UsageEntry.SourceData"/>.</summary>
    private const char KeySeparator = '\u001f';

    /// <summary>
    /// <c>[openclaw] model</c> (nadpisania cen w stylu ccusage), <c>host/model</c> dla hosta chmurowego (np. Vertex),
    /// potem sama nazwa (ccusage <c>openclaw/src/parser.rs</c> <c>entry_to_loaded</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        List<string> candidates = [$"[{SourceId}] {entry.Model}"];
        if (Pricing.ProviderNames.HostProvider(entry.Provider) is { } host)
        {
            candidates.Add($"{host}/{entry.Model}");
        }

        candidates.Add(entry.Model);
        return candidates;
    }

    /// <summary>
    /// Pierwszeństwo SQLite (<see cref="UsageEntry.Preference"/> = 1): zdarzenia z id — pierwsze o danej
    /// <c>(sesja, id)</c>; bez id — pomijane, gdy treść (z kosztem) ma zdarzenie z id; kopie JSONL — pomijane, gdy SQLite
    /// ma zdarzenie o tej samej treści bez kosztu (ccusage <c>openclaw/src/loader.rs</c> <c>load_entries</c>).
    /// </summary>
    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries)
    {
        if (!entries.Any(entry => entry.Preference > 0))
        {
            return entries;
        }

        var parsed = entries.Select(entry => (Entry: entry, Key: Key.Parse(entry))).ToList();
        var events = new HashSet<(string, string)>();
        var contents = new HashSet<(string, string)>();
        var migrations = new HashSet<string>(StringComparer.Ordinal);
        var keep = new bool[parsed.Count];
        var order = Enumerable.Range(0, parsed.Count)
            .Where(index => parsed[index].Key is { Sqlite: true })
            .OrderBy(index => parsed[index].Key!.EventId is null ? 1 : 0)
            .ToList();
        foreach (var index in order)
        {
            var (entry, key) = parsed[index];
            var content = (key!.Migration, key.Cost);
            var isNew = key.EventId is { } eventId ? events.Add((entry.SessionId, eventId)) : !contents.Contains(content);
            if (isNew)
            {
                contents.Add(content);
                migrations.Add(key.Migration);
                keep[index] = true;
            }
        }

        for (var index = 0; index < parsed.Count; index++)
        {
            // Linia JSONL (albo wpis bez rozpoznanego klucza): odpada tylko przy kopii w SQLite lub tej samej treści.
            var key = parsed[index].Key;
            if (key is null)
            {
                keep[index] = true;
            }
            else if (!key.Sqlite)
            {
                keep[index] = !migrations.Contains(key.Migration) && contents.Add((key.Migration, key.Cost));
            }
        }

        return parsed.Where((_, index) => keep[index]).Select(item => item.Entry).ToList();
    }

    /// <param name="Migration">Sesja, czas, model i tokeny — łączy kopię JSONL z jej zdarzeniem w SQLite.</param>
    private sealed record Key(string Migration, string Cost, string? EventId, bool Sqlite)
    {
        /// <summary>Wartość <see cref="UsageEntry.SourceData"/>: koszt i opcjonalnie id zdarzenia SQLite.</summary>
        public static string Data(double? cost, string? eventId) =>
            (cost is { } value ? value.ToString("R", CultureInfo.InvariantCulture) : "-") +
            (eventId is null ? "" : KeySeparator + eventId);

        public static Key? Parse(UsageEntry entry)
        {
            if (entry.DedupeKey is not { } migration || entry.SourceData is not { } data)
            {
                return null;
            }

            var parts = data.Split(KeySeparator, 2);
            return new Key(migration, parts[0], parts.Length == 2 ? parts[1] : null, entry.Preference > 0);
        }
    }

    protected override string[] EnvironmentVariables => ["OPENCLAW_DIR"];

    protected override string[] Executables => ["openclaw"];

    protected override IEnumerable<string> DefaultRoots() =>
    [
        Path.Combine(FileUtil.Home, ".openclaw"),
        Path.Combine(FileUtil.Home, ".clawdbot"),
        Path.Combine(FileUtil.Home, ".moltbot"),
        Path.Combine(FileUtil.Home, ".moldbot")
    ];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var agents = Path.Combine(root, "agents");
            if (Directory.Exists(agents))
            {
                IEnumerable<string> agentDirectories;
                try
                {
                    agentDirectories = Directory.GetDirectories(agents);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    agentDirectories = [];
                }

                foreach (var agent in agentDirectories.Order(StringComparer.Ordinal))
                {
                    var database = Path.Combine(agent, "agent", DatabaseName);
                    if (File.Exists(database))
                    {
                        yield return SourceFile.Create(database, root, "-wal");
                    }
                }
            }

            foreach (var file in FileUtil.SafeEnumerateFiles(root, "*.jsonl*"))
            {
                if (IsSessionFile(Path.GetFileName(file)))
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken) =>
        file.Path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
            ? ParseDatabase(file.Path, diagnostics, cancellationToken)
            : ParseJsonl(file.Path, diagnostics, cancellationToken);

    /// <summary><c>x.jsonl</c>, <c>x.jsonl.deleted.*</c>, <c>x.jsonl.reset.*</c>.</summary>
    internal static bool IsSessionFile(string name)
    {
        var index = name.IndexOf(".jsonl", StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var suffix = name[index..];
        return suffix == ".jsonl" ||
               suffix.StartsWith(".jsonl.deleted.", StringComparison.Ordinal) ||
               suffix.StartsWith(".jsonl.reset.", StringComparison.Ordinal);
    }

    /// <summary>Stan modelu/dostawcy w obrębie jednego transkryptu (pliku lub sesji w bazie).</summary>
    private sealed class TranscriptState(string sessionId, string? agent)
    {
        public string SessionId { get; } = sessionId;
        public string? Agent { get; } = agent;
        public string? Model { get; set; }
        public string? Provider { get; set; }
    }

    private static IEnumerable<UsageEntry> ParseJsonl(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var state = new TranscriptState(SessionIdFromPath(path), AgentFromPath(path));
        foreach (var line in FileUtil.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 || !MatchesPrefilter(line))
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
                var entry = ReadRecord(document.RootElement, state, null, false, diagnostics);
                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
    }

    private static List<UsageEntry> ParseDatabase(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var entries = new List<UsageEntry>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        // Agent: agents/<agent>/agent/openclaw-agent.sqlite.
        var agent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "");

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'transcript_events'";
            if (check.ExecuteScalar() is null)
            {
                diagnostics.Skip("baza bez tabeli transcript_events");
                return entries;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, event_json, created_at FROM transcript_events ORDER BY session_id ASC, seq ASC";
        using var reader = command.ExecuteReader();
        TranscriptState? state = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(sessionId))
            {
                diagnostics.Skip("wiersz bez session_id");
                continue;
            }

            if (state is null || state.SessionId != sessionId)
            {
                state = new TranscriptState(sessionId, string.IsNullOrEmpty(agent) ? null : agent);
            }

            if (reader.IsDBNull(1))
            {
                diagnostics.Skip("wiersz bez event_json");
                continue;
            }

            var json = reader.GetString(1);
            if (!MatchesPrefilter(json))
            {
                continue;
            }

            // created_at to milisekundy — zapasowy czas, gdy zdarzenie go nie ma.
            long? fallback = null;
            if (!reader.IsDBNull(2) && reader.GetValue(2) is long created && created >= 0)
            {
                fallback = created;
            }

            if (!JsonUtil.TryParse(json, out var document))
            {
                diagnostics.Skip("niepoprawny JSON");
                continue;
            }

            using (document)
            {
                var entry = ReadRecord(document.RootElement, state, fallback, true, diagnostics);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static bool MatchesPrefilter(string line) =>
        line.Contains("\"model_change\"", StringComparison.Ordinal) ||
        line.Contains("\"model-snapshot\"", StringComparison.Ordinal) ||
        line.Contains("\"usage\"", StringComparison.Ordinal);

    private static UsageEntry? ReadRecord(JsonElement root, TranscriptState state, long? fallbackTimestamp, bool fromDatabase,
        ParseDiagnostics diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = root.Str("type");
        if (type == "model_change" || (type == "custom" && root.Str("customType") == "model-snapshot"))
        {
            // Źródło modelu: obiekt "data" (model-snapshot) albo pola najwyższego poziomu.
            var source = root.Prop("data") is { ValueKind: JsonValueKind.Object } data ? data : root;
            var model = source.Str("modelId") ?? source.Str("model");
            var provider = source.Str("provider");
            if (model is not null)
            {
                state.Model = model;
            }

            if (provider is not null)
            {
                state.Provider = provider;
            }

            return null;
        }

        if (type != "message")
        {
            return null;
        }

        if (root.Prop("message") is not { ValueKind: JsonValueKind.Object } message || message.Str("role") != "assistant")
        {
            return null;
        }

        if (message.Prop("usage") is not { } usage)
        {
            return null;
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Skip("niepoprawne pole usage");
            return null;
        }

        var input = usage.Long("input");
        var output = usage.Long("output");
        var cacheRead = usage.Long("cacheRead");
        var cacheWrite = usage.Long("cacheWrite");
        var sum = input + output + cacheRead + cacheWrite;
        var remainder = Math.Max(0, usage.Long("totalTokens") - sum);
        if (sum + remainder == 0)
        {
            return null;
        }

        var timestamp = TimestampOf(message.Prop("timestamp")) ?? TimestampOf(root.Prop("timestamp")) ?? fallbackTimestamp;
        if (timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
            return null;
        }

        var modelName = message.Str("modelId") ?? message.Str("model") ?? state.Model ?? UnknownModel;
        double? cost = usage.Prop("cost") is { ValueKind: JsonValueKind.Object } costElement ? costElement.Prop("total").Double() : null;
        if (cost is { } value && (double.IsNaN(value) || double.IsInfinity(value)))
        {
            cost = null;
        }

        var migration = $"{state.SessionId}:{timestamp.Value}:{modelName}:{input}:{output}:{cacheWrite}:{cacheRead}:{remainder}";
        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = timestamp.Value,
            SessionId = state.SessionId,
            Project = state.Agent,
            Provider = Pricing.ProviderNames.Normalize(message.Str("provider") ?? state.Provider),
            Preference = fromDatabase ? 1 : 0,
            Model = modelName,
            IsFallbackModel = modelName == UnknownModel,
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = cacheWrite,
            CacheReadTokens = cacheRead,
            RecordedCostUsd = cost,
            DedupeKey = migration,
            SourceData = Key.Data(cost, fromDatabase ? root.Str("id") : null)
        };
    }

    /// <summary>Liczba = milisekundy (OpenClaw), tekst = ISO 8601.</summary>
    private static long? TimestampOf(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
            {
                return integer >= 0 ? integer : null;
            }

            return value.TryGetDouble(out var real) && real >= 0 && !double.IsInfinity(real) ? (long)Math.Truncate(real) : null;
        }

        return value.ValueKind == JsonValueKind.String ? element.TimestampMs() : null;
    }

    /// <summary><c>&lt;uuid&gt;.jsonl[.deleted.*|.reset.*]</c> → <c>&lt;uuid&gt;</c>.</summary>
    internal static string SessionIdFromPath(string path)
    {
        var name = Path.GetFileName(path);
        var index = name.IndexOf(".jsonl", StringComparison.Ordinal);
        return index > 0 ? name[..index] : name;
    }

    /// <summary><c>.../agents/&lt;agent&gt;/sessions/x.jsonl</c> → <c>&lt;agent&gt;</c>.</summary>
    private static string? AgentFromPath(string path)
    {
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 2; i >= 1; i--)
        {
            if (parts[i - 1] == "agents")
            {
                return parts[i];
            }
        }

        return null;
    }
}
