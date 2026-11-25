using System.Globalization;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// ZCode (Z.ai) — księga zużycia SQLite <c>&lt;root&gt;/cli/db/db.sqlite</c>, tabela <c>model_usage</c> złączona z <c>session</c>.
/// Katalogi: <c>ZCODE_HOME</c> (lista) lub <c>~/.zcode</c>.
/// </summary>
/// <remarks>
/// Liczone są tylko wiersze <c>status = 'completed'</c>; <c>started_at</c> to milisekundy.
/// <c>input_tokens</c> zawiera odczyt i zapis cache — odejmujemy oba (z ograniczeniem do wartości input).
/// ZCode nie zapisuje kosztu. Deduplikacja po <c>model_usage.id</c> (ta sama baza pod kilkoma korzeniami).
/// Cennik (ccusage <c>zcode/src/parser.rs</c>): dostawca <c>provider_id</c> trafia do <see cref="UsageEntry.Provider"/>.
/// Z.ai (np. <c>builtin:zai-coding-plan</c>, albo brak dostawcy i model <c>glm-*</c>) — kandydaci <c>zai/…</c>, potem nazwa
/// surowa, a zapis do cache po stawce wejścia. Inny dostawca (np. własny z ID w postaci UUID) — bez ceny z katalogu.
/// </remarks>
public sealed class ZCodeSource : UsageSourceBase
{
    public const string SourceId = "zcode";

    /// <summary>2: dostawca (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;

    private static readonly string[] RequiredModelColumns = ["id", "session_id", "started_at", "model_id", "status", "input_tokens", "output_tokens"];
    private static readonly string[] RequiredSessionColumns = ["id", "directory"];

    public override string Id => SourceId;
    public override string DisplayName => "ZCode";
    public override string Vendor => "Z.ai";
    public override string AccentColor => "#2D6BFF";

    protected override string[] EnvironmentVariables => ["ZCODE_HOME"];

    protected override string[] Executables => ["zcode"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".zcode")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            var database = Path.Combine(root, "cli", "db", "db.sqlite");
            if (File.Exists(database) && seen.Add(FileUtil.NormalizePath(database)))
            {
                yield return SourceFile.Create(database, root, "-wal");
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        var entries = new List<UsageEntry>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var modelColumns = Columns(connection, "model_usage");
        var sessionColumns = Columns(connection, "session");
        if (RequiredModelColumns.Any(column => !modelColumns.Contains(column)) ||
            RequiredSessionColumns.Any(column => !sessionColumns.Contains(column)))
        {
            diagnostics.Skip("nieobsługiwany schemat bazy ZCode");
            return entries;
        }

        var cacheCreation = modelColumns.Contains("cache_creation_input_tokens") ? "m.cache_creation_input_tokens" : "0";
        var cacheRead = modelColumns.Contains("cache_read_input_tokens") ? "m.cache_read_input_tokens" : "0";
        var provider = modelColumns.Contains("provider_id") ? "m.provider_id" : "NULL";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.id, m.session_id, m.started_at, m.model_id, m.input_tokens, m.output_tokens,
                   {cacheCreation}, {cacheRead}, {provider}, s.directory
            FROM model_usage m LEFT JOIN session s ON s.id = m.session_id
            WHERE m.status = 'completed'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Count(reader, 4);
            var output = Count(reader, 5);
            // Oba kubełki cache są częścią input_tokens.
            var cached = Math.Min(Count(reader, 7), input);
            var written = Math.Min(Count(reader, 6), input - cached);
            var fresh = input - cached - written;
            if (fresh + output + cached + written == 0)
            {
                continue;
            }

            var id = Text(reader, 0);
            var sessionId = Text(reader, 1);
            var model = Text(reader, 3)?.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(model))
            {
                diagnostics.Skip("wiersz bez id, sesji lub modelu");
                continue;
            }

            var startedAt = reader.IsDBNull(2) ? null : reader.GetValue(2) switch
            {
                long integer => integer,
                double real => (long?)real,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
            if (startedAt is not > 0)
            {
                diagnostics.Skip("brak lub zły started_at");
                continue;
            }

            var directory = Text(reader, 9);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = null;
            }

            entries.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = startedAt.Value,
                SessionId = sessionId,
                Project = directory is null ? null : Path.GetFileName(directory.TrimEnd('/', '\\')),
                ProjectPath = directory,
                Model = model,
                InputTokens = fresh,
                OutputTokens = output,
                CacheCreationTokens = written,
                CacheReadTokens = cached,
                Provider = Pricing.ProviderNames.Normalize(Text(reader, 8)),
                DedupeKey = id
            });
        }

        return entries;
    }

    /// <summary>
    /// Kandydaci cennika (ccusage <c>ordered_pricing_candidates</c>): dla Z.ai najpierw <c>zai/</c> + nazwa surowa i małymi
    /// literami, potem te nazwy bez prefiksu. Dostawca spoza Z.ai nie dostaje ceny z katalogu — ccusage wycenia go wyłącznie
    /// nadpisaniem ceny, którego to źródło nie widzi (zob. raport Fazy 11), więc taki wpis pozostaje bez ceny.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        var raw = model?.Trim();
        if (string.IsNullOrEmpty(raw) || (provider is not null && !IsZaiProvider(provider)))
        {
            return [];
        }

        var lower = raw.ToLowerInvariant();
        var candidates = new List<string>();
        if (IsZai(raw, provider))
        {
            candidates.Add("zai/" + raw);
            candidates.Add("zai/" + lower);
        }

        candidates.Add(raw);
        candidates.Add(lower);
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Z.ai rozlicza nową treść po zwykłej stawce wejścia, nawet gdy księga nazywa ją zapisem do cache
    /// (ccusage <c>zcode/src/parser.rs</c>, #1675) — stawka zapisu do cache = stawka wejścia (także w progach).
    /// </summary>
    public override Pricing.ModelPricing AdjustPricing(UsageEntry entry, Pricing.ModelPricing pricing)
    {
        if (!IsZai(entry.Model ?? "", entry.Provider))
        {
            return pricing;
        }

        static Pricing.RateSet AtInput(Pricing.RateSet rates, double? input) =>
            rates with { CacheWrite = input, CacheWrite1h = input };

        var baseInput = pricing.Base.Input;
        return pricing with
        {
            Base = AtInput(pricing.Base, baseInput),
            Tiers = pricing.Tiers.Select(tier => tier with { Rates = AtInput(tier.Rates, tier.Rates.Input ?? baseInput) }).ToArray()
        };
    }

    /// <summary>Wpis rozliczany przez Z.ai: dostawca Z.ai albo brak dostawcy i model <c>glm-*</c>.</summary>
    internal static bool IsZai(string model, string? provider)
    {
        if (provider is not null)
        {
            return IsZaiProvider(provider);
        }

        var lower = model.Trim().ToLowerInvariant();
        return lower.StartsWith("glm-", StringComparison.Ordinal) || lower.StartsWith("glm/", StringComparison.Ordinal);
    }

    /// <summary>Identyfikatory Z.ai w ZCode (ccusage <c>is_zai_provider</c>).</summary>
    internal static bool IsZaiProvider(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized is "zai" or "z.ai" or "zai-coding-plan" or "builtin:zai-coding-plan" or "builtin:bigmodel-coding-plan" ||
               normalized.StartsWith("account:zai-", StringComparison.Ordinal) ||
               normalized.StartsWith("builtin:zai-", StringComparison.Ordinal);
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
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

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long Count(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        return reader.GetValue(ordinal) switch
        {
            long integer => Math.Max(0, integer),
            double real when !double.IsNaN(real) => (long)Math.Clamp(real, 0, long.MaxValue),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => Math.Max(0, parsed),
            _ => 0
        };
    }
}
