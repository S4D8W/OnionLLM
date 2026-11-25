using System.Text;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Google Antigravity — bazy SQLite rozmów <c>conversations/*.db</c> z metadanymi zakodowanymi w protobuf.
/// <list type="bullet">
/// <item><c>gen_metadata(idx, data)</c> — GeneratorMetadata (wymagana tabela).</item>
/// <item><c>steps(idx, metadata)</c> — CortexStepMetadata (opcjonalnie, zużycie kroków i ponowień).</item>
/// <item><c>trajectory_metadata_blob(data)</c> — zapasowy znacznik czasu.</item>
/// </list>
/// Katalogi: <c>ANTIGRAVITY_DATA_DIR</c> (lista; katalog danych z <c>conversations/</c> albo sam katalog rozmów) lub
/// <c>~/.gemini/antigravity[-cli|-ide|-backup|-acp]/conversations</c> i <c>~/.config/antigravity/conversations</c>.
/// </summary>
/// <remarks>
/// Pola ModelUsage: 2 = input (bez cache), 3 = output łącznie, 4 = zapis cache, 5 = odczyt cache, 9 = reasoning,
/// 10 = widoczny output. <see cref="UsageEntry.OutputTokens"/> = output łącznie (widoczny + reasoning, tak wycenia ccusage).
/// Duplikaty (ten sam response/provider/message id lub krok) są scalane w obrębie bazy (maksimum pól),
/// a między bazami łączy je <see cref="UsageEntry.DedupeKey"/> z najlepszego identyfikatora.
/// </remarks>
public sealed class AntigravitySource : UsageSourceBase
{
    /// <summary>2: numer API (Vertex), nazwa modelu z logu i scalanie zdarzeń między bazami.</summary>
    public override int ParserVersion => 3;

    public const string SourceId = "antigravity";
    public const string DefaultModel = "gemini-internal-model";

    private const string PlaceholderPrefix = "model_placeholder_m";
    private const string UnresolvedIdPrefix = "antigravity-model-";

    public override string Id => SourceId;
    public override string DisplayName => "Antigravity";
    public override string Vendor => "Google";
    public override string AccentColor => "#34A853";

    protected override string[] EnvironmentVariables => ["ANTIGRAVITY_DATA_DIR"];

    protected override string[] Executables => ["antigravity"];

    protected override IEnumerable<string> DefaultRoots()
    {
        var gemini = Path.Combine(FileUtil.Home, ".gemini");
        return
        [
            Path.Combine(gemini, "antigravity", "conversations"),
            Path.Combine(gemini, "antigravity-cli", "conversations"),
            Path.Combine(gemini, "antigravity-ide", "conversations"),
            Path.Combine(gemini, "antigravity-backup", "conversations"),
            Path.Combine(gemini, "antigravity-acp", "conversations"),
            Path.Combine(FileUtil.Home, ".config", "antigravity", "conversations")
        ];
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            // Katalog danych z podkatalogiem conversations/ albo sam katalog rozmów.
            var nested = Path.Combine(root, "conversations");
            var directory = Directory.Exists(nested) ? nested : root;
            foreach (var file in FileUtil.SafeEnumerateFiles(directory, "*.db"))
            {
                if (seen.Add(FileUtil.NormalizePath(file)))
                {
                    yield return SourceFile.Create(file, root, "-wal");
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        List<UsageEvent> events;
        try
        {
            events = ReadDatabase(file.Path, diagnostics, cancellationToken);
        }
        catch (ProtoException)
        {
            // Jak w ccusage: baza wnosi zużycie tylko wtedy, gdy cały odczyt się powiódł.
            diagnostics.Skip("nie udało się zdekodować metadanych protobuf");
            return [];
        }

        return Deduplicate(events).Select(usage => ToEntry(usage, file.Path)).ToList();
    }

    private static UsageEntry ToEntry(UsageEvent usage, string path)
    {
        var key = EventKey.Create(usage, path);
        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = usage.Timestamp,
            SessionId = usage.SessionId,
            Project = "antigravity",
            Model = usage.Model,
            IsFallbackModel = usage.Model == DefaultModel,
            InputTokens = usage.Input,
            OutputTokens = usage.TotalOutput,
            CacheCreationTokens = usage.CacheWrite,
            CacheReadTokens = usage.CacheRead,
            ReasoningTokens = usage.Reasoning,
            Provider = usage.Provider == ApiProviderGoogleVertex ? Pricing.ProviderNames.Normalize("vertex") : null,
            DedupeKey = key.Format(),
            SourceData = key.Data
        };
    }

    // ---------------------------------------------------------------- dostawca i cennik

    /// <summary>Numery API z pola 7 <c>ModelInfo</c> / pola 6 <c>ModelUsage</c> (ccusage <c>antigravity/src/parser/model.rs</c>).</summary>
    private const ulong ApiProviderGoogleVertex = 3;
    private const ulong ApiProviderGoogleGemini = 24;
    private const ulong ApiProviderGoogleEvergreen = 30;

    private static readonly string[] GoogleProviderPrefixes = ["google", "gemini", "vertex_ai", "openrouter/google"];

    /// <summary>
    /// Kandydaci jak <c>price_antigravity</c>: najpierw dokładna nazwa z logu (gdy różni się od znormalizowanej), potem
    /// model (dla Vertex poprzedzony <c>vertex_ai/</c>, dla API Gemini z prefiksami Google po nazwie), wariant Flash bez
    /// poziomu effort, a na końcu nazwa bez <c>-thinking</c>. Numer API Vertex trafia też do <see cref="UsageEntry.Provider"/>
    /// (trasa rozliczenia hosta, #2226); pozostałe numery nie zmieniają trasy, więc są tylko w kluczu wpisu.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        var key = EventKey.Parse(entry);
        var provider = key?.Provider;
        List<string> candidates = [];
        if (key?.LoggedModel is { } logged)
        {
            candidates.Add(logged);
        }

        candidates.AddRange(ModelCandidates(entry.Model, provider));
        if (entry.Model.EndsWith("-thinking", StringComparison.Ordinal))
        {
            candidates.AddRange(ModelCandidates(entry.Model[..^"-thinking".Length], provider));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    internal static List<string> ModelCandidates(string model, ulong? provider)
    {
        List<string> models = [model];
        if (FlashPricingAlias(model) is { } alias)
        {
            models.Add(alias);
        }

        var (host, google) = provider switch
        {
            ApiProviderGoogleVertex => ((string[])["vertex_ai"], (string[])[]),
            ApiProviderGoogleGemini or ApiProviderGoogleEvergreen => ([], GoogleProviderPrefixes),
            _ => ([], [])
        };
        var candidates = new List<string>();
        foreach (var name in models)
        {
            candidates.AddRange(host.Select(prefix => $"{prefix}/{name}"));
            candidates.Add(name);
            candidates.AddRange(google.Select(prefix => $"{prefix}/{name}"));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary><c>gemini-3.6-flash-high</c> → <c>gemini-3.6-flash</c> (ccusage <c>gemini_flash_pricing_alias</c>).</summary>
    private static string? FlashPricingAlias(string model)
    {
        var dash = model.LastIndexOf('-');
        if (dash <= 0 || model[(dash + 1)..] is not ("high" or "medium" or "low"))
        {
            return null;
        }

        var family = model[..dash];
        return FlashEffortFamilies.Contains(family) ? family : null;
    }

    // ---------------------------------------------------------------- scalanie między bazami

    /// <summary>
    /// Zdarzenia z różnych baz (np. kopia rozmowy w <c>antigravity-backup</c>) dzielące dowolny identyfikator są scalane
    /// z maksimum każdego licznika, najlepiej rozwiązanym modelem i najlepszym czasem (ccusage
    /// <c>antigravity/src/loader.rs</c> <c>deduplicate_events</c>).
    /// </summary>
    public override IReadOnlyList<UsageEntry> Reconcile(IReadOnlyList<UsageEntry> entries)
    {
        var slots = new List<(UsageEntry Entry, EventKey Key)?>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<UsageEntry>();
        var merged = false;
        foreach (var entry in entries)
        {
            if (EventKey.Parse(entry) is not { } key || key.Identities.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            var matching = key.Identities.Where(indexes.ContainsKey).Select(identity => indexes[identity]).Distinct().Order().ToList();
            if (matching.Count == 0)
            {
                foreach (var identity in key.Identities)
                {
                    indexes[identity] = slots.Count;
                }

                slots.Add((entry, key));
                continue;
            }

            merged = true;
            var target = matching[0];
            var current = slots[target]!.Value;
            foreach (var duplicate in matching.Skip(1))
            {
                if (slots[duplicate] is { } other)
                {
                    slots[duplicate] = null;
                    current = MergeEntries(current, other);
                }
            }

            current = MergeEntries(current, (entry, key));
            slots[target] = current;
            foreach (var identity in current.Key.Identities)
            {
                indexes[identity] = target;
            }
        }

        if (!merged)
        {
            return entries;
        }

        result.AddRange(slots.Where(slot => slot is not null).Select(slot => slot!.Value.Entry));
        return result;
    }

    private static (UsageEntry Entry, EventKey Key) MergeEntries((UsageEntry Entry, EventKey Key) target, (UsageEntry Entry, EventKey Key) duplicate)
    {
        var (left, leftKey) = target;
        var (right, rightKey) = duplicate;
        // OutputTokens = output łącznie; widoczne wyjście = łącznie − reasoning (zob. Append).
        var output = Math.Max(left.OutputTokens - left.ReasoningTokens, right.OutputTokens - right.ReasoningTokens);
        var reasoning = Math.Max(left.ReasoningTokens, right.ReasoningTokens);
        var totalOutput = Math.Max(Math.Max(left.OutputTokens, right.OutputTokens), output + reasoning);

        var model = left.Model ?? DefaultModel;
        var logged = leftKey.LoggedModel;
        var rightModel = right.Model ?? DefaultModel;
        if (Resolution(rightModel) > Resolution(model))
        {
            model = rightModel;
            logged = rightKey.LoggedModel;
        }
        else if (model == rightModel && logged is null)
        {
            logged = rightKey.LoggedModel;
        }

        var useRightTime = rightKey.TimestampRank > leftKey.TimestampRank ||
                           (rightKey.TimestampRank == leftKey.TimestampRank && right.TimestampMs < left.TimestampMs);
        var provider = leftKey.Provider ?? rightKey.Provider;
        var key = leftKey with
        {
            TimestampRank = useRightTime ? rightKey.TimestampRank : leftKey.TimestampRank,
            Provider = provider,
            LoggedModel = logged,
            Identities = leftKey.Identities.Concat(rightKey.Identities.Where(identity => !leftKey.Identities.Contains(identity))).ToList()
        };
        var entry = left with
        {
            TimestampMs = useRightTime ? right.TimestampMs : left.TimestampMs,
            Model = model,
            IsFallbackModel = model == DefaultModel,
            InputTokens = Math.Max(left.InputTokens, right.InputTokens),
            OutputTokens = totalOutput,
            ReasoningTokens = reasoning,
            CacheCreationTokens = Math.Max(left.CacheCreationTokens, right.CacheCreationTokens),
            CacheReadTokens = Math.Max(left.CacheReadTokens, right.CacheReadTokens),
            Provider = left.Provider ?? right.Provider,
            DedupeKey = key.Format(),
            SourceData = key.Data
        };
        return (entry, key);
    }

    /// <summary>
    /// Opis wpisu Antigravity. <see cref="UsageEntry.DedupeKey"/>: najlepszy identyfikator (<c>response:</c>, <c>provider:</c>,
    /// <c>message:</c>, <c>step:</c>, <c>gen:</c>) i plik (klucze z różnych baz się różnią, scala je <see cref="Reconcile"/>).
    /// <see cref="UsageEntry.SourceData"/>: dane do scalania między bazami — ranga czasu, numer API, nazwa z logu i wszystkie identyfikatory.
    /// </summary>
    private sealed record EventKey(string Primary, string File, int TimestampRank, ulong? Provider, string? LoggedModel, List<string> Identities)
    {
        private const char Separator = '\u001f';
        private const char ListSeparator = '\u001e';

        public static EventKey Create(UsageEvent usage, string path) => new(
            usage.MessageId is { } id
                ? usage.MessageIdRank switch { 3 => "response:", 2 => "provider:", _ => "message:" } + id
                : usage.Identities.FirstOrDefault() ?? "",
            path, usage.TimestampRank, usage.Provider, usage.LoggedModel, usage.Identities);

        /// <summary>Wartość <see cref="UsageEntry.DedupeKey"/>.</summary>
        public string Format() => Primary + Separator + File;

        /// <summary>Wartość <see cref="UsageEntry.SourceData"/>.</summary>
        public string Data => string.Join(Separator,
            TimestampRank.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Provider?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", LoggedModel ?? "",
            string.Join(ListSeparator, Identities));

        public static EventKey? Parse(UsageEntry entry)
        {
            var key = entry.DedupeKey?.Split(Separator, 2);
            var parts = entry.SourceData?.Split(Separator);
            if (key is not { Length: 2 } || parts is not { Length: 4 } ||
                !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var rank))
            {
                return null;
            }

            ulong? provider = ulong.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
            return new EventKey(key[0], key[1], rank, provider, parts[2].Length == 0 ? null : parts[2],
                parts[3].Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries).ToList());
        }
    }

    // ---------------------------------------------------------------- odczyt bazy

    private sealed class ModelUsage
    {
        public ulong? ModelId { get; init; }
        public long Input { get; init; }
        public long TotalOutput { get; init; }
        public long CacheWrite { get; init; }
        public long CacheRead { get; init; }
        public long Reasoning { get; init; }
        public long VisibleOutput { get; init; }
        public string? MessageId { get; init; }
        public string? ResponseId { get; init; }
        public string? ProviderMessageId { get; init; }

        /// <summary>Numer API (pole 6), 0 = brak.</summary>
        public ulong? Provider { get; init; }

        public bool IsTokenBearing => Input > 0 || TotalOutput > 0 || CacheWrite > 0 || CacheRead > 0 || Reasoning > 0 || VisibleOutput > 0;

        public List<string> IdentityKeys()
        {
            var keys = new List<string>(3);
            if (!string.IsNullOrEmpty(ResponseId))
            {
                keys.Add("response:" + ResponseId);
            }

            if (!string.IsNullOrEmpty(ProviderMessageId))
            {
                keys.Add("provider:" + ProviderMessageId);
            }

            if (!string.IsNullOrEmpty(MessageId))
            {
                keys.Add("message:" + MessageId);
            }

            return keys;
        }

        public (string? Id, int Rank) PreferredMessageId() =>
            !string.IsNullOrEmpty(ResponseId) ? (ResponseId, 3)
            : !string.IsNullOrEmpty(ProviderMessageId) ? (ProviderMessageId, 2)
            : (string.IsNullOrEmpty(MessageId) ? null : MessageId, 1);

        public bool IsMirrorOf(ModelUsage primary) =>
            ModelId == primary.ModelId && Input == primary.Input && TotalOutput == primary.TotalOutput &&
            CacheWrite == primary.CacheWrite && CacheRead == primary.CacheRead && Reasoning == primary.Reasoning &&
            VisibleOutput == primary.VisibleOutput &&
            IdentityKeys().All(primary.IdentityKeys().Contains);
    }

    private sealed class Metadata
    {
        public string? Model { get; init; }
        public ulong? ModelId { get; init; }

        /// <summary>Numer API z <c>ModelInfo</c> (pole 7) kroku.</summary>
        public ulong? Provider { get; init; }
        public ModelUsage? Usage { get; init; }
        public List<ModelUsage> Retries { get; init; } = [];
        public long? Timestamp { get; init; }
        public List<long> StepIndices { get; init; } = [];
    }

    private sealed class UsageEvent
    {
        public long Timestamp { get; set; }
        public required string SessionId { get; init; }
        public required string Model { get; set; }
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheWrite { get; set; }
        public long CacheRead { get; set; }
        public long Reasoning { get; set; }
        public long TotalOutput { get; set; }
        public string? MessageId { get; set; }
        public ulong? Provider { get; set; }

        /// <summary>Surowa nazwa z logu, gdy różni się od znormalizowanej (ccusage <c>logged_model</c>).</summary>
        public string? LoggedModel { get; set; }
        public required List<string> Identities { get; init; }
        public int TimestampRank { get; set; }
        public int MessageIdRank { get; set; }
    }

    private static List<UsageEvent> ReadDatabase(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var events = new List<UsageEvent>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        {
            if (Scalar(connection, "PRAGMA page_count") is long pages && pages == 0)
            {
                return events; // pusta (niezainicjalizowana) baza
            }

            if (!TableExists(connection, "gen_metadata"))
            {
                if (TableExists(connection, "steps") || TableExists(connection, "trajectory_metadata_blob"))
                {
                    diagnostics.Skip("nieobsługiwany schemat bazy Antigravity");
                }

                return events; // np. conversation_summaries.db
            }

            var sessionId = Path.GetFileNameWithoutExtension(path);
            var trajectoryTimestamp = ReadTrajectoryTimestamp(connection);
            var generations = ReadRows(connection, "SELECT idx, data FROM gen_metadata ORDER BY idx ASC", ParseGeneratorMetadata, cancellationToken);
            var steps = TableExists(connection, "steps")
                ? ReadRows(connection, "SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL ORDER BY idx ASC", ParseStepMetadata, cancellationToken)
                : [];

            string? generationModel = null;
            for (var i = generations.Count - 1; i >= 0 && generationModel is null; i--)
            {
                generationModel = RowModel(generations[i].Metadata.Model, generations[i].Metadata.ModelId);
            }

            // Wstępne znaczniki czasu dla tożsamości (kroki i generacje z własnym czasem).
            var identityTimestamps = new Dictionary<string, (long Timestamp, int Rank)>(StringComparer.Ordinal);
            void Seed(long timestamp, IEnumerable<string> identities, Metadata metadata)
            {
                var usages = metadata.Usage is null ? metadata.Retries : metadata.Retries.Prepend(metadata.Usage);
                foreach (var identity in identities.Concat(usages.SelectMany(usage => usage.IdentityKeys())))
                {
                    identityTimestamps[identity] = identityTimestamps.TryGetValue(identity, out var known)
                        ? (Math.Min(known.Timestamp, timestamp), known.Rank)
                        : (timestamp, 3);
                }
            }

            foreach (var (idx, metadata) in steps)
            {
                if (metadata.Timestamp is { } timestamp)
                {
                    Seed(timestamp, [$"step:{sessionId}:{idx}"], metadata);
                }
            }

            foreach (var (idx, metadata) in generations)
            {
                if (metadata.Timestamp is { } timestamp)
                {
                    Seed(timestamp, GenerationIdentities(sessionId, idx, metadata.StepIndices), metadata);
                }
            }

            foreach (var (idx, metadata) in steps)
            {
                var model = RowModel(metadata.Model, metadata.ModelId) ?? generationModel;
                var context = new Context(model, metadata.ModelId, metadata.Provider, metadata.Timestamp, trajectoryTimestamp, sessionId,
                    [$"step:{sessionId}:{idx}"]);
                if (metadata.Usage is not null)
                {
                    Append(events, identityTimestamps, metadata.Usage, context, diagnostics);
                }

                foreach (var retry in metadata.Retries)
                {
                    Append(events, identityTimestamps, retry, context with { FallbackIdentities = [] }, diagnostics);
                }
            }

            string? currentModel = null;
            foreach (var (idx, metadata) in generations)
            {
                var resolved = RowModel(metadata.Model, metadata.ModelId) ??
                               (metadata.Usage?.ModelId is { } usageModelId ? ModelFromId(usageModelId) : null);
                if (resolved is not null)
                {
                    currentModel = resolved;
                }

                var context = new Context(currentModel, metadata.ModelId, null, metadata.Timestamp, trajectoryTimestamp, sessionId,
                    GenerationIdentities(sessionId, idx, metadata.StepIndices));
                if (metadata.Usage is not null)
                {
                    Append(events, identityTimestamps, metadata.Usage, context, diagnostics);
                }

                foreach (var retry in metadata.Retries)
                {
                    Append(events, identityTimestamps, retry, context with { FallbackIdentities = [] }, diagnostics);
                }
            }
        }

        return events;
    }

    private sealed record Context(
        string? Model,
        ulong? ModelId,
        ulong? Provider,
        long? Timestamp,
        long? TrajectoryTimestamp,
        string SessionId,
        IReadOnlyList<string> FallbackIdentities);

    private static void Append(List<UsageEvent> events, Dictionary<string, (long Timestamp, int Rank)> identityTimestamps,
        ModelUsage usage, Context context, ParseDiagnostics diagnostics)
    {
        if (!usage.IsTokenBearing)
        {
            return;
        }

        var identities = usage.IdentityKeys();
        if (identities.Count == 0)
        {
            identities.AddRange(context.FallbackIdentities);
        }

        (long Timestamp, int Rank)? resolved = context.Timestamp is { } own ? (own, 3) : null;
        if (resolved is null)
        {
            foreach (var identity in identities.Concat(context.FallbackIdentities))
            {
                if (identityTimestamps.TryGetValue(identity, out var known))
                {
                    resolved = known;
                    break;
                }
            }
        }

        if (resolved is null && context.TrajectoryTimestamp is { } trajectory)
        {
            resolved = (trajectory, 1);
        }

        if (resolved is not { } stamp)
        {
            diagnostics.Skip("brak znacznika czasu generacji, kroku i trajektorii");
            return;
        }

        var totalOutput = Math.Max(usage.TotalOutput, usage.VisibleOutput + usage.Reasoning);
        var output = Math.Max(usage.VisibleOutput, Math.Max(0, totalOutput - usage.Reasoning));
        var reasoning = Math.Max(usage.Reasoning, Math.Max(0, totalOutput - output));

        var idModel = usage.ModelId is { } modelId ? ModelFromId(modelId) : null;
        var rowModel = context.Model is null ? null : Normalize(context.Model);
        var sameModelId = context.ModelId == usage.ModelId;
        var model = (idModel is not null && IsResolved(idModel) ? idModel : null)
                    ?? (rowModel is not null && sameModelId && IsResolved(rowModel) ? rowModel : null)
                    ?? idModel
                    ?? rowModel
                    ?? DefaultModel;

        // Dokładna nazwa z logu jest pierwszym kandydatem cennika (ccusage append_usage_event, logged_model).
        var loggedModel = context.Model is { } raw && raw != model && (sameModelId || rowModel == model) ? raw : null;
        var (messageId, messageIdRank) = usage.PreferredMessageId();
        foreach (var identity in identities)
        {
            if (!identityTimestamps.TryGetValue(identity, out var old) ||
                stamp.Rank > old.Rank || (stamp.Rank == old.Rank && stamp.Timestamp < old.Timestamp))
            {
                identityTimestamps[identity] = stamp;
            }
        }

        events.Add(new UsageEvent
        {
            Timestamp = stamp.Timestamp,
            TimestampRank = stamp.Rank,
            SessionId = context.SessionId,
            Model = model,
            Input = usage.Input,
            Output = output,
            CacheWrite = usage.CacheWrite,
            CacheRead = usage.CacheRead,
            Reasoning = reasoning,
            TotalOutput = totalOutput,
            MessageId = messageId,
            MessageIdRank = messageIdRank,
            Provider = usage.Provider ?? context.Provider,
            LoggedModel = loggedModel,
            Identities = identities
        });
    }

    private static List<string> GenerationIdentities(string sessionId, long idx, List<long> stepIndices) =>
        stepIndices.Count == 0
            ? [$"gen:{sessionId}:{idx}"]
            : stepIndices.Select(step => $"step:{sessionId}:{step}").ToList();

    /// <summary>Scala zdarzenia dzielące dowolną tożsamość (maksimum liczników, najlepszy model/czas/id).</summary>
    private static List<UsageEvent> Deduplicate(List<UsageEvent> events)
    {
        var slots = new List<UsageEvent?>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var usage in events)
        {
            var matching = usage.Identities
                .Where(indexes.ContainsKey)
                .Select(identity => indexes[identity])
                .Distinct()
                .Order()
                .ToList();
            if (matching.Count == 0)
            {
                foreach (var identity in usage.Identities)
                {
                    indexes[identity] = slots.Count;
                }

                slots.Add(usage);
                continue;
            }

            var target = matching[0];
            foreach (var duplicate in matching.Skip(1))
            {
                if (slots[duplicate] is { } other)
                {
                    slots[duplicate] = null;
                    Merge(slots[target]!, other);
                }
            }

            Merge(slots[target]!, usage);
            foreach (var identity in slots[target]!.Identities)
            {
                indexes[identity] = target;
            }
        }

        return slots.OfType<UsageEvent>().ToList();
    }

    private static void Merge(UsageEvent target, UsageEvent duplicate)
    {
        target.Input = Math.Max(target.Input, duplicate.Input);
        target.Output = Math.Max(target.Output, duplicate.Output);
        target.CacheWrite = Math.Max(target.CacheWrite, duplicate.CacheWrite);
        target.CacheRead = Math.Max(target.CacheRead, duplicate.CacheRead);
        target.Reasoning = Math.Max(target.Reasoning, duplicate.Reasoning);
        target.TotalOutput = Math.Max(Math.Max(target.TotalOutput, duplicate.TotalOutput), target.Output + target.Reasoning);
        if (Resolution(duplicate.Model) > Resolution(target.Model))
        {
            target.Model = duplicate.Model;
            target.LoggedModel = duplicate.LoggedModel;
        }
        else if (target.Model == duplicate.Model && target.LoggedModel is null)
        {
            target.LoggedModel = duplicate.LoggedModel;
        }

        target.Provider ??= duplicate.Provider;

        if (duplicate.TimestampRank > target.TimestampRank ||
            (duplicate.TimestampRank == target.TimestampRank && duplicate.Timestamp < target.Timestamp))
        {
            target.Timestamp = duplicate.Timestamp;
            target.TimestampRank = duplicate.TimestampRank;
        }

        if (duplicate.MessageIdRank > target.MessageIdRank)
        {
            target.MessageId = duplicate.MessageId;
            target.MessageIdRank = duplicate.MessageIdRank;
        }

        foreach (var identity in duplicate.Identities)
        {
            if (!target.Identities.Contains(identity))
            {
                target.Identities.Add(identity);
            }
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static List<(long Idx, Metadata Metadata)> ReadRows(SqliteConnection connection, string sql,
        Func<byte[], Metadata> parse, CancellationToken cancellationToken)
    {
        var rows = new List<(long, Metadata)>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetValue(0) is not long idx || Blob(reader.GetValue(1)) is not { } blob)
            {
                throw new ProtoException("wiersz metadanych ma niepoprawne pola");
            }

            rows.Add((idx, parse(blob)));
        }

        return rows;
    }

    private static byte[]? Blob(object value) => value switch
    {
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        _ => null
    };

    private static long? ReadTrajectoryTimestamp(SqliteConnection connection)
    {
        if (!TableExists(connection, "trajectory_metadata_blob"))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM trajectory_metadata_blob ORDER BY rowid ASC";
        using var reader = command.ExecuteReader();
        long? timestamp = null;
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || Blob(reader.GetValue(0)) is not { } blob)
            {
                throw new ProtoException("wiersz trajektorii ma niepoprawne pola");
            }

            var fields = Proto.Decode(blob);
            var candidate = Proto.Bytes(fields, 2) is { } message ? ParseTimestamp(message) : null;
            timestamp ??= candidate;
        }

        return timestamp;
    }

    // ---------------------------------------------------------------- protobuf

    private static Metadata ParseGeneratorMetadata(byte[] blob)
    {
        var root = Proto.Decode(blob);
        var chatModel = Proto.Bytes(root, 1) ?? throw new ProtoException("brak pola chat model (1)");
        var fields = Proto.Decode(chatModel);
        var usage = Proto.Bytes(fields, 4) is { } usageBlob ? ParseModelUsage(usageBlob) : null;
        var retries = Proto.AllBytes(fields, 17).Select(ParseRetry).OfType<ModelUsage>().ToList();
        DropPrimaryMirror(usage, retries);
        long? timestamp = null;
        if (Proto.Bytes(fields, 9) is { } generationInfo && Proto.Bytes(Proto.Decode(generationInfo), 4) is { } timestampMessage)
        {
            timestamp = ParseTimestamp(timestampMessage);
        }

        return new Metadata
        {
            Model = Proto.Text(fields, 19) ?? Proto.Text(fields, 21),
            ModelId = Proto.Varint(fields, 3) is { } id && id != 0 ? id : null,
            Usage = usage,
            Retries = retries,
            Timestamp = timestamp,
            StepIndices = StepIndices(root)
        };
    }

    private static Metadata ParseStepMetadata(byte[] blob)
    {
        var fields = Proto.Decode(blob);
        var usage = Proto.Bytes(fields, 9) is { } usageBlob ? ParseModelUsage(usageBlob) : null;
        var retries = Proto.AllBytes(fields, 28).Select(ParseRetry).OfType<ModelUsage>().ToList();
        DropPrimaryMirror(usage, retries);
        string? model = null;
        ulong? modelId = null;
        ulong? provider = null;
        if (Proto.Bytes(fields, 24) is { } modelInfo)
        {
            var info = Proto.Decode(modelInfo);
            model = Proto.Text(info, 12) ?? Proto.Text(info, 8);
            modelId = Proto.Varint(info, 1) is { } id && id != 0 ? id : null;
            provider = Proto.Varint(info, 7) is { } api && api != 0 ? api : null;
        }

        var timestampMessage = Proto.Bytes(fields, 8) ?? Proto.Bytes(fields, 1);
        return new Metadata
        {
            Model = model,
            ModelId = modelId,
            Provider = provider,
            Usage = usage,
            Retries = retries,
            Timestamp = timestampMessage is null ? null : ParseTimestamp(timestampMessage)
        };
    }

    /// <summary>retry_infos powtarza główną próbę — usuwamy jedno lustrzane odbicie, zostawiając prawdziwe ponowienia.</summary>
    private static void DropPrimaryMirror(ModelUsage? primary, List<ModelUsage> retries)
    {
        if (primary is null)
        {
            return;
        }

        var index = retries.FindIndex(retry => retry.IsMirrorOf(primary));
        if (index >= 0)
        {
            retries.RemoveAt(index);
        }
    }

    private static ModelUsage? ParseRetry(byte[] blob) =>
        Proto.Bytes(Proto.Decode(blob), 2) is { } usage ? ParseModelUsage(usage) : null;

    private static ModelUsage ParseModelUsage(byte[] blob)
    {
        var fields = Proto.Decode(blob);
        return new ModelUsage
        {
            ModelId = Proto.Varint(fields, 1) is { } id && id != 0 ? id : null,
            Input = Count(Proto.Varint(fields, 2)),
            TotalOutput = Count(Proto.Varint(fields, 3)),
            CacheWrite = Count(Proto.Varint(fields, 4)),
            CacheRead = Count(Proto.Varint(fields, 5)),
            Reasoning = Count(Proto.Varint(fields, 9)),
            VisibleOutput = Count(Proto.Varint(fields, 10)),
            MessageId = Proto.Text(fields, 7),
            ResponseId = Proto.Text(fields, 11),
            ProviderMessageId = Proto.Text(fields, 12),
            Provider = Proto.Varint(fields, 6) is { } api && api != 0 ? api : null
        };
    }

    private static long Count(ulong? value) => value is { } number ? (long)Math.Min(number, long.MaxValue) : 0;

    /// <summary>google.protobuf.Timestamp: 1 = sekundy, 2 = nanosekundy.</summary>
    private static long? ParseTimestamp(byte[] blob)
    {
        var fields = Proto.Decode(blob);
        if (Proto.Varint(fields, 1) is not { } seconds || seconds == 0 || seconds > long.MaxValue / 1000)
        {
            return null;
        }

        var nanos = Math.Min(Proto.Varint(fields, 2) ?? 0, 999_999_999UL);
        return (long)seconds * 1000 + (long)(nanos / 1_000_000);
    }

    private static List<long> StepIndices(List<Proto.Field> root)
    {
        var indices = new List<long>();
        foreach (var field in root.Where(field => field.Number == 2))
        {
            if (field.Wire == 0)
            {
                if (field.Varint <= long.MaxValue)
                {
                    indices.Add((long)field.Varint);
                }
            }
            else if (field.Wire == 2 && field.Data is { } packed)
            {
                var position = 0;
                while (position < packed.Length)
                {
                    try
                    {
                        var value = Proto.ReadVarint(packed, ref position);
                        if (value > long.MaxValue)
                        {
                            break;
                        }

                        indices.Add((long)value);
                    }
                    catch (ProtoException)
                    {
                        break;
                    }
                }
            }
        }

        return indices;
    }

    private sealed class ProtoException(string message) : Exception(message);

    /// <summary>Minimalny dekoder formatu przewodowego protobuf (bez schematu).</summary>
    private static class Proto
    {
        public readonly record struct Field(int Number, int Wire, ulong Varint, byte[]? Data);

        public static List<Field> Decode(byte[] blob)
        {
            var fields = new List<Field>();
            var position = 0;
            while (position < blob.Length)
            {
                var tag = ReadVarint(blob, ref position);
                var number = tag >> 3;
                if (number == 0 || number > int.MaxValue)
                {
                    throw new ProtoException("niepoprawny numer pola protobuf");
                }

                var wire = (int)(tag & 7);
                switch (wire)
                {
                    case 0:
                        fields.Add(new Field((int)number, 0, ReadVarint(blob, ref position), null));
                        break;
                    case 1:
                        Take(blob, ref position, 8);
                        fields.Add(new Field((int)number, 1, 0, null));
                        break;
                    case 2:
                        var length = ReadVarint(blob, ref position);
                        if (length > int.MaxValue)
                        {
                            throw new ProtoException("za długie pole protobuf");
                        }

                        fields.Add(new Field((int)number, 2, 0, Take(blob, ref position, (int)length)));
                        break;
                    case 5:
                        Take(blob, ref position, 4);
                        fields.Add(new Field((int)number, 5, 0, null));
                        break;
                    default:
                        throw new ProtoException("nieobsługiwany typ przewodowy protobuf");
                }
            }

            return fields;
        }

        public static ulong ReadVarint(byte[] blob, ref int position)
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (position >= blob.Length)
                {
                    throw new ProtoException("ucięty varint protobuf");
                }

                var current = blob[position++];
                var payload = (ulong)(current & 0x7F);
                if (shift == 63 && payload > 1)
                {
                    throw new ProtoException("przepełnienie varint protobuf");
                }

                value |= payload << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                if (shift == 63)
                {
                    break;
                }
            }

            throw new ProtoException("przepełnienie varint protobuf");
        }

        private static byte[] Take(byte[] blob, ref int position, int length)
        {
            if (blob.Length - position < length)
            {
                throw new ProtoException("ucięta wartość protobuf");
            }

            var result = blob.AsSpan(position, length).ToArray();
            position += length;
            return result;
        }

        /// <summary>Ostatnie pole varint o danym numerze.</summary>
        public static ulong? Varint(List<Field> fields, int number)
        {
            for (var i = fields.Count - 1; i >= 0; i--)
            {
                if (fields[i].Number == number && fields[i].Wire == 0)
                {
                    return fields[i].Varint;
                }
            }

            return null;
        }

        /// <summary>Pierwsze pole bajtowe o danym numerze.</summary>
        public static byte[]? Bytes(List<Field> fields, int number) =>
            fields.FirstOrDefault(field => field.Number == number && field.Wire == 2).Data;

        public static List<byte[]> AllBytes(List<Field> fields, int number) =>
            fields.Where(field => field.Number == number && field.Wire == 2).Select(field => field.Data!).ToList();

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>Ostatnie pole bajtowe o danym numerze jako niepusty tekst UTF-8.</summary>
        public static string? Text(List<Field> fields, int number)
        {
            for (var i = fields.Count - 1; i >= 0; i--)
            {
                if (fields[i].Number != number || fields[i].Wire != 2)
                {
                    continue;
                }

                try
                {
                    var text = StrictUtf8.GetString(fields[i].Data!);
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
                catch (DecoderFallbackException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    // ---------------------------------------------------------------- modele

    private static string? RowModel(string? model, ulong? modelId) =>
        !string.IsNullOrWhiteSpace(model) ? model : modelId is { } id ? ModelFromId(id) : null;

    private static string? ModelFromId(ulong modelId) => Normalize(modelId switch
    {
        246 => "gemini-2.5-pro",
        312 => "gemini-2.5-flash",
        313 or 329 => "gemini-2.5-flash-thinking",
        330 => "gemini-2.5-flash-lite",
        281 or 282 => "claude-sonnet-4",
        290 or 291 => "claude-opus-4",
        333 or 334 => "claude-4.5-sonnet",
        340 or 341 => "claude-4.5-haiku",
        342 => "model_openai_gpt_oss_120b_medium",
        >= 1000 => PlaceholderPrefix + (modelId - 1000),
        _ => UnresolvedIdPrefix + modelId
    });

    /// <summary>0 = domyślny, 1 = nierozwiązany placeholder, 2 = rozwiązany model.</summary>
    private static int Resolution(string model) =>
        model == DefaultModel ? 0
        : model.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) || model.StartsWith(UnresolvedIdPrefix, StringComparison.Ordinal) ? 1
        : 2;

    private static bool IsResolved(string model) => Resolution(model) == 2;

    private static readonly string[] FlashEffortFamilies = ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash"];

    /// <summary>Nazwy wyświetlane i wewnętrzne aliasy Antigravity → stabilne identyfikatory modeli (jak ccusage).</summary>
    internal static string? Normalize(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();

        // "Gemini 3.8 Flash (High)" → gemini-3.8-flash-high
        var open = lower.IndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && lower.EndsWith(')'))
        {
            var baseName = lower[..open];
            var effort = lower[(open + 2)..^1];
            if (!baseName.Contains('-'))
            {
                var family = baseName.Replace(' ', '-');
                if (FlashEffortFamilies.Contains(family) && effort is "high" or "medium" or "low")
                {
                    return family + "-" + effort;
                }
            }
        }

        var paren = lower.IndexOf('(');
        var name = paren >= 0 ? lower[..paren].Trim() : lower;
        var normalized = name switch
        {
            "gemini 3.8 flash" or "gemini 3.8 flash thinking" => "gemini-3.8-flash",
            "gemini 3.7 flash" or "gemini 3.7 flash thinking" => "gemini-3.7-flash",
            "gemini 3.7 pro" or "gemini 3.7 pro thinking" => "gemini-3.7-pro",
            "gemini 3.6 flash" or "gemini 3 flash" => "gemini-3.6-flash",
            "gemini 3.6 pro" => "gemini-3.6-pro",
            "gemini 3 pro" or "gemini 3 pro thinking" => "gemini-3-pro",
            "gemini 2.5 flash" => "gemini-2.5-flash",
            "gemini 2.5 pro" => "gemini-2.5-pro",
            "gemini 2.0 flash" or "gemini 2 flash" => "gemini-2.0-flash",
            "gemini 2.0 pro" => "gemini-2.0-pro",
            "gemini 1.5 flash" => "gemini-1.5-flash",
            "gemini 1.5 pro" => "gemini-1.5-pro",
            "model_placeholder_m318" => "gemini-3.8-flash-high",
            "model_placeholder_m319" => "gemini-3.8-flash-medium",
            "model_placeholder_m320" => "gemini-3.8-flash-low",
            "model_placeholder_m298" => "gemini-3.7-flash-high",
            "model_placeholder_m299" => "gemini-3.7-flash-medium",
            "model_placeholder_m300" => "gemini-3.7-flash-low",
            "model_placeholder_m71" => "gemini-3.6-flash-high",
            "model_placeholder_m72" => "gemini-3.6-flash-medium",
            "model_placeholder_m73" => "gemini-3.6-flash-low",
            "model_placeholder_m417" or "model_placeholder_m418" or "model_placeholder_m419" => "gemini-4-argon",
            "model_placeholder_m402" => "claude-opus-5-5",
            "model_placeholder_m50" => "gemini-3.1-flash-lite",
            "model_placeholder_m26" => "claude-opus-4-6",
            "model_placeholder_m35" => "claude-sonnet-4-6",
            "model_placeholder_m36" or "model_placeholder_m37" or "model_placeholder_m16" => "gemini-3.1-pro",
            "model_placeholder_m18" or "model_placeholder_m84" or "model_placeholder_m47" => "gemini-3-flash-preview",
            "model_placeholder_m132" or "model_placeholder_m133" => "gemini-3.5-flash-high",
            "model_placeholder_m187" => "gemini-3.5-flash-extra-low",
            "model_placeholder_m20" => "gemini-3.5-flash-medium",
            "model_openai_gpt_oss_120b_medium" => "gpt-oss-120b-medium",
            "gemini-pro-default" or "gemini-pro-agent" => "gemini-3.1-pro",
            "gemini-3-flash-agent" or "gemini-3-flash-agent-a" or "gemini-3-flash-agent-b"
                or "gemini-3-flash-a" or "gemini-3-flash-b" => "gemini-3.5-flash-high",
            "gemini-3-flash-c" or "gemini-3-flash" => "gemini-3-flash-preview",
            "gemini-3.5-flash-low" => "gemini-3.5-flash-medium",
            "gemini-3.1-pro-high" or "gemini-3.1-pro-low" => "gemini-3.1-pro",
            "gemini-3-pro-high" or "gemini-3-pro-low" => "gemini-3-pro",
            "claude 3.7 sonnet" or "claude 3.7 sonnet thinking" => "claude-3-7-sonnet",
            "claude 3.5 sonnet" => "claude-3-5-sonnet",
            "claude 3.5 haiku" => "claude-3-5-haiku",
            "claude 3 opus" => "claude-3-opus",
            _ => null
        };
        if (normalized is not null)
        {
            return normalized;
        }

        var converted = name.Replace(' ', '-');
        return converted.StartsWith("gemini-", StringComparison.Ordinal) ||
               converted.StartsWith("claude-", StringComparison.Ordinal) ||
               converted.StartsWith("gpt-", StringComparison.Ordinal)
            ? converted
            : trimmed;
    }
}
