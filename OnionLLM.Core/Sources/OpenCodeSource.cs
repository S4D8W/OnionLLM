using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// OpenCode — baza SQLite <c>opencode.db</c> (lub <c>opencode-&lt;kanał&gt;.db</c>) oraz starsze pliki
/// <c>storage/message/&lt;sessionID&gt;/&lt;messageID&gt;.json</c>.
/// Katalogi: <c>OPENCODE_DATA_DIR</c> (lista) lub <c>${XDG_DATA_HOME:-~/.local/share}/opencode</c>.
/// </summary>
/// <remarks>
/// Odwzorowanie adaptera <c>opencode</c> z ccusage:
/// <list type="bullet">
/// <item>Tabela <c>message</c> (v1) i pliki JSON — ten sam format wiadomości (<c>modelID</c>, <c>providerID</c>,
/// <c>tokens</c>, <c>time.created</c>, <c>cost</c>).</item>
/// <item>Tabela <c>session_message</c> (v2) — wiersze <c>assistant</c> i ukończone <c>compaction</c>;
/// kopie historii w forkach (<c>session_v2.fork_session_id</c> + <c>fork_boundary</c>, <c>seq</c> ≤ granica) są pomijane.</item>
/// <item>Liczniki sesji z <c>session_v2</c> / <c>session</c> jako wpisy zbiorcze (<see cref="UsageEntry.IsSessionSummary"/>)
/// — używane tylko, gdy sesja nie ma wpisów szczegółowych (np. po revercie).</item>
/// </list>
/// Tokeny: <c>tokens.input</c> nie zawiera cache; <c>tokens.reasoning</c> jest poza <c>output</c>, a ccusage
/// wycenia je stawką wyjścia — <see cref="UsageEntry.OutputTokens"/> = output + reasoning.
/// <c>cost</c> &gt; 0 to koszt zapisany przez narzędzie (0 oznacza „nieznany”).
/// Deduplikacja po id wiadomości (wiersze bazy i pliki JSON tej samej wiadomości, kilka katalogów danych).
/// <para>
/// Suma sesji zastępuje zużycie sesji, która nie ma wierszy wiadomości (np. po revercie). Obejmuje też kompakcje,
/// więc wiersze kompakcji takiej sesji są wtedy pomijane (<see cref="Reconcile"/>, ccusage <c>opencode/src/loader.rs</c>
/// <c>EntryCollector::finish</c>, #2050). ccusage używa sum tylko w raporcie sesji bez filtra dat — zob. <see cref="IsSessionReportOnly"/>.
/// </para>
/// </remarks>
public sealed partial class OpenCodeSource : UsageSourceBase
{
    public const string SourceId = "opencode";
    public const string UnknownModel = "unknown";

    public override string Id => SourceId;
    public override string DisplayName => "OpenCode";
    public override string Vendor => "SST";
    public override string AccentColor => "#56B6C2";

    /// <summary>2: dostawca (<see cref="UsageEntry.Provider"/>) i znacznik kompakcji w kluczu deduplikacji.</summary>
    public override int ParserVersion => 2;

    /// <summary>Prefiks <see cref="UsageEntry.DedupeKey"/> wierszy kompakcji (v2 <c>session_message.type = compaction</c>).</summary>
    internal const string CompactionKeyPrefix = "compaction:";

    /// <summary>
    /// Wpis, który ccusage liczy tylko w raporcie sesji bez filtra dat: suma z tabel <c>session</c>/<c>session_v2</c>
    /// (datowana utworzeniem sesji i obejmująca całe jej zużycie; ccusage <c>opencode/src/loader/files.rs</c>
    /// <c>allow_aggregate_fallback</c> i <c>loader.rs</c> <c>has_temporal_filter</c>). Raporty okresowe, bloki i sesje
    /// z zakresem dat powinny go odrzucić przed deduplikacją — wtedy liczą się wiersze kompakcji tej sesji.
    /// </summary>
    public override bool IsSessionReportOnly(UsageEntry entry) => IsSessionTotal(entry);

    public static bool IsSessionTotal(UsageEntry entry) =>
        entry.Source.Equals(SourceId, StringComparison.OrdinalIgnoreCase) && entry.IsSessionSummary;

    /// <summary>
    /// Kandydaci cennika jak <c>open_code_model_candidates</c> (ccusage <c>opencode/src/parser.rs</c>): nazwa z logu,
    /// alias OpenCode, nazwa znormalizowana, a potem te same z prefiksem dostawcy (<c>dostawca/model</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        var resolved = ResolveModelAlias(model);
        var normalized = NormalizeModelName(resolved);
        List<string> names = [model];
        if (resolved != model)
        {
            names.Add(resolved);
        }

        if (normalized != resolved && normalized != model)
        {
            names.Add(normalized);
        }

        var candidates = new List<string>(names);
        if (CandidatePrefix(provider) is { } prefix)
        {
            candidates.AddRange(names.Select(name => $"{prefix}/{name}"));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Prefiks dostawcy w kluczach cennika: host chmurowy albo nazwa z <c>-</c> → <c>_</c> (ccusage <c>normalize_provider</c>).</summary>
    internal static string? CandidatePrefix(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Trim() == UnknownModel)
        {
            return null;
        }

        return Pricing.ProviderNames.HostProvider(provider) ?? provider.Trim().Replace('-', '_');
    }

    /// <summary>Aliasy OpenCode (ccusage <c>resolve_open_code_model_name</c>).</summary>
    internal static string ResolveModelAlias(string model) => model switch
    {
        "gemini-3-pro-high" => "gemini-3-pro-preview",
        "k2p6" => "kimi-k2.6",
        _ => model
    };

    /// <summary><c>claude-opus-4.6</c> / <c>claude-opus-46</c> → <c>claude-opus-4-6</c> (ccusage <c>normalize_open_code_model_name</c>).</summary>
    internal static string NormalizeModelName(string model)
    {
        foreach (var family in new[] { "claude-haiku-", "claude-opus-", "claude-sonnet-" })
        {
            if (!model.StartsWith(family, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = model[family.Length..];
            var dot = rest.IndexOf('.');
            if (dot > 0 && rest[..dot].All(char.IsAsciiDigit) && dot + 1 < rest.Length && char.IsAsciiDigit(rest[dot + 1]))
            {
                return $"{family}{rest[..dot]}-{rest[(dot + 1)..]}";
            }

            if (rest.Length >= 2 && char.IsAsciiDigit(rest[0]) && char.IsAsciiDigit(rest[1]))
            {
                return $"{family}{rest[0]}-{rest[1]}{rest[2..]}";
            }
        }

        return model;
    }

    /// <summary>
    /// Ustawienie „Licz tury claude-agent” (ccusage <c>--include-claude-agent</c>): wpis przestaje być lustrem sesji
    /// Claude Code i liczy się zawsze (ccusage <c>ccusage-adapter-all/src/loader.rs</c> <c>load_opencode_entries</c>).
    /// </summary>
    public override UsageEntry ApplySettings(UsageEntry entry, SourceSettings settings) =>
        settings.IncludeClaudeAgent && entry.MirrorOf is not null ? entry with { MirrorOf = null } : entry;

    /// <summary>
    /// Sesja bez wierszy wiadomości, ale z sumą sesji: suma zawiera kompakcje, więc wiersze kompakcji tej sesji
    /// są pomijane (ccusage <c>EntryCollector::finish</c>). Sumy sesji z wiadomościami odrzuca potem poziom sesji.
    /// </summary>
    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries)
    {
        if (!entries.Any(entry => entry.IsSessionSummary))
        {
            return entries;
        }

        var messageSessions = new HashSet<string>(StringComparer.Ordinal);
        var summarySessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.IsSessionSummary)
            {
                summarySessions.Add(entry.SessionId);
            }
            else if (!IsCompaction(entry))
            {
                messageSessions.Add(entry.SessionId);
            }
        }

        summarySessions.ExceptWith(messageSessions);
        return summarySessions.Count == 0
            ? entries
            : entries.Where(entry => !(IsCompaction(entry) && summarySessions.Contains(entry.SessionId))).ToList();
    }

    internal static bool IsCompaction(UsageEntry entry) =>
        entry.DedupeKey?.StartsWith(CompactionKeyPrefix, StringComparison.Ordinal) == true;

    protected override string[] EnvironmentVariables => ["OPENCODE_DATA_DIR"];

    protected override string[] Executables => ["opencode"];

    protected override IEnumerable<string> DefaultRoots()
    {
        // Jak w ccusage: XDG_DATA_HOME (tylko ścieżka bezwzględna), w przeciwnym razie ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(FileUtil.Home, ".local", "share");
        return [Path.Combine(dataHome, "opencode")];
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            if (DatabasePath(root) is { } database)
            {
                yield return SourceFile.Create(database, root, "-wal");
            }

            foreach (var file in FileUtil.SafeEnumerateFiles(Path.Combine(root, "storage", "message"), "*.json"))
            {
                yield return SourceFile.Create(file, root);
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken) =>
        file.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? ParseDatabase(file.Path, diagnostics, cancellationToken)
            : ParseMessageFile(file.Path, diagnostics);

    /// <summary><c>opencode.db</c>, a gdy go brak — pierwsza (alfabetycznie) baza kanału <c>opencode-*.db</c>.</summary>
    internal static string? DatabasePath(string root)
    {
        var main = Path.Combine(root, "opencode.db");
        if (File.Exists(main))
        {
            return main;
        }

        return FileUtil.SafeEnumerateFiles(root, "opencode-*.db", SearchOption.TopDirectoryOnly)
            .Where(path => ChannelDbRegex().IsMatch(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static List<UsageEntry> ParseMessageFile(string path, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        string text;
        try
        {
            text = FileUtil.ReadAllText(path);
        }
        catch (IOException)
        {
            diagnostics.Skip("nie można odczytać pliku");
            return entries;
        }

        if (!JsonUtil.TryParse(text, out var document))
        {
            diagnostics.Skip("niepoprawny JSON");
            return entries;
        }

        using (document)
        {
            if (FromV1Message(document.RootElement, null, null, null, diagnostics) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
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

        using var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (SqliteException)
        {
            diagnostics.Skip("nie można otworzyć bazy");
            return entries;
        }

        var message = Columns(connection, "message");
        var sessionMessage = Columns(connection, "session_message");
        var sessionV2 = Columns(connection, "session_v2");
        // Liczniki z tabeli session są czytane (jak w ccusage) tylko obok schematu v2 (session_message).
        var session = Columns(connection, "session");
        var directories = SessionDirectories(connection, session, sessionV2);

        if (message.IsSupersetOf(["id", "session_id", "data"]))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, session_id, data FROM message";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = Text(reader, 2);
                if (data is null || !JsonUtil.TryParse(data, out var document))
                {
                    diagnostics.Skip("niepoprawny JSON w message.data");
                    continue;
                }

                using (document)
                {
                    var sessionId = Text(reader, 1);
                    var entry = FromV1Message(document.RootElement, Text(reader, 0), sessionId,
                        sessionId is null ? null : directories.GetValueOrDefault(sessionId), diagnostics);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
        }

        if (sessionMessage.IsSupersetOf(["id", "session_id", "type", "data"]))
        {
            var forkCutoffs = ForkCopyCutoffs(connection, sessionV2, sessionMessage);
            string Column(string name) => sessionMessage.Contains(name) ? name : "NULL";
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, session_id, type, data, {Column("time_created")}, {Column("seq")} FROM session_message";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = Text(reader, 2);
                if (type is not ("assistant" or "compaction"))
                {
                    continue;
                }

                var id = Text(reader, 0) ?? "";
                var sessionId = Text(reader, 1) ?? "";
                long? seq = reader.IsDBNull(5) ? null : Integer(reader, 5);
                // Kopia historii rodzica w forku — już policzona w sesji rodzica.
                if (seq is not null && forkCutoffs.TryGetValue(sessionId, out var cutoff) && seq <= cutoff)
                {
                    continue;
                }

                var data = Text(reader, 3);
                if (data is null || !JsonUtil.TryParse(data, out var document))
                {
                    diagnostics.Skip("niepoprawny JSON w session_message.data");
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    // Nieukończone / nieudane kompakcje nie mają modelu, któremu można przypisać zużycie.
                    if (type == "compaction" && root.Prop("status").Str() != "completed")
                    {
                        continue;
                    }

                    long? rowCreated = reader.IsDBNull(4) ? null : Integer(reader, 4);
                    var entry = FromV2Message(root, id, sessionId, rowCreated, directories.GetValueOrDefault(sessionId), diagnostics);
                    if (entry is not null)
                    {
                        entries.Add(type == "compaction" && entry.DedupeKey is { } key
                            ? entry with { DedupeKey = CompactionKeyPrefix + key }
                            : entry);
                    }
                }
            }

        }

        entries.AddRange(SessionAggregates(connection, "session_v2", sessionV2, directories, diagnostics, cancellationToken));
        if (sessionMessage.Count > 0)
        {
            entries.AddRange(SessionAggregates(connection, "session", session, directories, diagnostics, cancellationToken));
        }

        return entries;
    }

    private static UsageEntry? FromV1Message(JsonElement root, string? rowId, string? rowSessionId, string? directory, ParseDiagnostics diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return Build(new Usage(
            Id: rowId ?? root.Str("id"),
            SessionId: rowSessionId ?? root.Str("sessionID"),
            Created: root.Path("time", "created").Long(),
            Model: root.Str("modelID"),
            Provider: root.Str("providerID"),
            Tokens: root.Prop("tokens"),
            Cost: root.Prop("cost").Double(),
            IsAggregate: false,
            Directory: directory)
        {
            NativeSession = NativeClaudeSession(root, root.Str("providerID"))
        }, diagnostics);
    }

    private static UsageEntry? FromV2Message(JsonElement root, string id, string sessionId, long? rowCreated, string? directory, ParseDiagnostics diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modelRef = root.Prop("model");
        var model = modelRef?.Str("id") ?? modelRef?.Str("modelID") ?? root.Str("modelID");
        var provider = modelRef?.Str("providerID") ?? root.Str("providerID");
        var created = root.Path("time", "created").Long();
        return Build(new Usage(
            Id: id,
            SessionId: sessionId,
            Created: created > 0 ? created : rowCreated ?? 0,
            Model: model,
            Provider: provider,
            Tokens: root.Prop("tokens"),
            Cost: root.Prop("cost").Double(),
            IsAggregate: false,
            Directory: directory)
        {
            NativeSession = NativeClaudeSession(root, provider)
        }, diagnostics);
    }

    /// <summary>
    /// Dostawca <c>claude-agent</c> zapisuje id sesji transkryptu Claude Code w <c>content[].native.sessionID</c> (ccusage #2007).
    /// Takie tury liczy już źródło Claude — wpis dostaje <see cref="UsageEntry.MirrorOf"/>.
    /// </summary>
    private static string? NativeClaudeSession(JsonElement root, string? provider)
    {
        if (provider != "claude-agent")
        {
            return null;
        }

        foreach (var container in new[] { root.Prop("content"), root.Path("message", "content"), root.Prop("parts") })
        {
            if (container is not { ValueKind: JsonValueKind.Array } parts)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.Path("native", "sessionID").Str() is { } session)
                {
                    return session;
                }
            }
        }

        return null;
    }

    private sealed record Usage(string? Id, string? SessionId, long Created, string? Model, string? Provider,
        JsonElement? Tokens, double? Cost, bool IsAggregate, string? Directory)
    {
        public long Input { get; init; }
        public long Output { get; init; }
        public long Reasoning { get; init; }
        public long CacheRead { get; init; }
        public long CacheWrite { get; init; }
        public string? NativeSession { get; init; }
    }

    private static UsageEntry? Build(Usage usage, ParseDiagnostics diagnostics)
    {
        long input = usage.Input, output = usage.Output, reasoning = usage.Reasoning, cacheRead = usage.CacheRead, cacheWrite = usage.CacheWrite;
        if (!usage.IsAggregate)
        {
            if (usage.Tokens is not { ValueKind: JsonValueKind.Object } tokens)
            {
                return null;
            }

            input = tokens.Long("input");
            output = tokens.Long("output");
            reasoning = tokens.Long("reasoning");
            cacheRead = tokens.Path("cache", "read").Long();
            cacheWrite = tokens.Path("cache", "write").Long();
        }

        double? recordedCost = usage.Cost is > 0 ? usage.Cost : null;
        var costOnly = usage.IsAggregate && recordedCost is not null;
        // Sam "tokens.total" nie ma kubełka w OnionLLM (ccusage liczy go tylko do sumy) — pomijamy.
        if (input == 0 && output == 0 && reasoning == 0 && cacheRead == 0 && cacheWrite == 0 && !costOnly)
        {
            return null;
        }

        if (usage.Model is null || usage.Provider is null)
        {
            return null;
        }

        if (usage.Created <= 0)
        {
            diagnostics.Skip("brak time.created");
            return null;
        }

        var id = string.IsNullOrEmpty(usage.Id) ? null : usage.Id;
        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = usage.Created,
            SessionId = usage.SessionId ?? "unknown",
            Project = usage.Directory is null ? null : Path.GetFileName(usage.Directory.TrimEnd('/', '\\')),
            ProjectPath = usage.Directory,
            Model = usage.Model,
            IsFallbackModel = usage.Model == UnknownModel,
            InputTokens = input,
            OutputTokens = output + reasoning,
            ReasoningTokens = reasoning,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheWrite,
            RecordedCostUsd = recordedCost,
            IsSessionSummary = usage.IsAggregate,
            MirrorOf = usage.NativeSession is null ? null : $"{ClaudeSource.SourceId}:{usage.NativeSession}",
            Provider = Pricing.ProviderNames.Normalize(usage.Provider),
            DedupeKey = id
        };
    }

    /// <summary>Liczniki z tabel sesji (zawierają też kompakcje i zużycie usunięte revertem).</summary>
    private static List<UsageEntry> SessionAggregates(SqliteConnection connection, string table, HashSet<string> columns,
        Dictionary<string, string> directories, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var entries = new List<UsageEntry>();
        if (!columns.IsSupersetOf(["id", "time_created", "cost", "tokens_input", "tokens_output", "tokens_cache_read", "tokens_cache_write"]))
        {
            return entries;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, time_created, cost, tokens_input, tokens_output, tokens_cache_read, tokens_cache_write,
                   {(columns.Contains("tokens_reasoning") ? "tokens_reasoning" : "0")}, {(columns.Contains("model") ? "model" : "NULL")}
            FROM {table}
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = Text(reader, 0);
            if (sessionId is null)
            {
                continue;
            }

            var (model, provider) = ParseSessionModel(Text(reader, 8));
            var entry = Build(new Usage(
                Id: $"session:{sessionId}",
                SessionId: sessionId,
                Created: reader.IsDBNull(1) ? 0 : Integer(reader, 1),
                Model: model,
                Provider: provider,
                Tokens: null,
                Cost: reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
                IsAggregate: true,
                Directory: directories.GetValueOrDefault(sessionId))
            {
                Input = Count(reader, 3),
                Output = Count(reader, 4),
                CacheRead = Count(reader, 5),
                CacheWrite = Count(reader, 6),
                Reasoning = Count(reader, 7)
            }, diagnostics);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Kolumna <c>model</c> sesji: JSON <c>{"id","providerID"}</c>, JSON-owy tekst albo sama nazwa.</summary>
    internal static (string Model, string Provider) ParseSessionModel(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return (UnknownModel, UnknownModel);
        }

        if (JsonUtil.TryParse(value, out var document))
        {
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.String && root.GetString()?.Trim() is { Length: > 0 } name)
                {
                    return (name, UnknownModel);
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    var model = root.Str("id") ?? root.Str("modelID");
                    if (model is null)
                    {
                        return (UnknownModel, UnknownModel);
                    }

                    return (model, root.Str("providerID") ?? root.Str("provider") ?? UnknownModel);
                }
            }
        }

        return (value, UnknownModel);
    }

    /// <summary>
    /// Fork v2 kopiuje wiersze rodzica z nowymi id, ale tym samym <c>seq</c>. Granica:
    /// <c>through</c> → seq wiadomości granicznej, <c>before</c> → ostatni seq rodzica przed nią.
    /// </summary>
    private static Dictionary<string, long> ForkCopyCutoffs(SqliteConnection connection, HashSet<string> sessionV2, HashSet<string> sessionMessage)
    {
        var cutoffs = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!sessionV2.IsSupersetOf(["id", "fork_session_id", "fork_boundary"]) ||
            !sessionMessage.IsSupersetOf(["id", "session_id", "seq"]))
        {
            return cutoffs;
        }

        var forks = new List<(string Fork, string Parent, string Boundary)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, fork_session_id, fork_boundary FROM session_v2 WHERE fork_session_id IS NOT NULL AND fork_boundary IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Text(reader, 0) is { } fork && Text(reader, 1) is { } parent && Text(reader, 2) is { } boundary)
                {
                    forks.Add((fork, parent, boundary));
                }
            }
        }

        foreach (var (fork, parent, boundary) in forks)
        {
            if (ForkCutoff(connection, parent, boundary) is { } cutoff)
            {
                cutoffs[fork] = cutoff;
            }
        }

        return cutoffs;
    }

    private static long? ForkCutoff(SqliteConnection connection, string parentId, string boundaryJson)
    {
        if (!JsonUtil.TryParse(boundaryJson, out var document))
        {
            return null;
        }

        string? type, messageId;
        using (document)
        {
            type = document.RootElement.Str("type");
            messageId = document.RootElement.Str("messageID");
        }

        if (type is null || messageId is null)
        {
            return null;
        }

        long boundarySeq;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT seq FROM session_message WHERE session_id = $parent AND id = $id LIMIT 1";
            command.Parameters.AddWithValue("$parent", parentId);
            command.Parameters.AddWithValue("$id", messageId);
            var value = command.ExecuteScalar();
            if (value is null or DBNull)
            {
                return null;
            }

            boundarySeq = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        switch (type)
        {
            case "through":
                return boundarySeq;
            case "before":
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT MAX(seq) FROM session_message WHERE session_id = $parent AND seq < $seq";
                command.Parameters.AddWithValue("$parent", parentId);
                command.Parameters.AddWithValue("$seq", boundarySeq);
                var value = command.ExecuteScalar();
                return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            default:
                return null;
        }
    }

    /// <summary>Katalog roboczy sesji (kolumna <c>directory</c>), jeśli schemat go zawiera. Nie występuje w ccusage.</summary>
    private static Dictionary<string, string> SessionDirectories(SqliteConnection connection, params HashSet<string>[] tables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = ["session", "session_v2"];
        for (var index = 0; index < names.Length && index < tables.Length; index++)
        {
            if (!tables[index].IsSupersetOf(["id", "directory"]))
            {
                continue;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT id, directory FROM {names[index]} WHERE directory IS NOT NULL";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (Text(reader, 0) is { } id && Text(reader, 1) is { } directory)
                    {
                        result.TryAdd(id, directory);
                    }
                }
            }
            catch (SqliteException)
            {
            }
        }

        return result;
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static string? Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal) switch
        {
            string text => text,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture)
        };
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long Integer(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static long Count(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Math.Max(0, Integer(reader, ordinal));

    [GeneratedRegex("^opencode-[A-Za-z0-9_-]+\\.db$")]
    private static partial Regex ChannelDbRegex();
}
