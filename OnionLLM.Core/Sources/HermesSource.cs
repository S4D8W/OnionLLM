using System.Globalization;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Hermes Agent (Nous Research) — tabela <c>sessions</c> w <c>&lt;HERMES_HOME&gt;/state.db</c>.
/// Katalogi: <c>HERMES_HOME</c> (lista) lub <c>~/.hermes</c>.
/// </summary>
/// <remarks>
/// Hermes zapisuje tylko sumy na poziomie sesji, więc każdy wiersz to wpis zbiorczy (<see cref="UsageEntry.IsSessionSummary"/>)
/// z czasem <c>started_at</c> (sekundy). Tokeny reasoning są podzbiorem <c>output_tokens</c> (nie dodajemy ich ponownie).
/// Koszt zapisany: <c>actual_cost_usd</c>, a gdy brak — <c>estimated_cost_usd</c>. Ta sama sesja w kilku bazach liczy się raz.
/// Dostawca: <c>billing_provider</c>, a gdy brak — wywnioskowany z nazwy modelu (<see cref="UsageEntry.Provider"/>).
/// </remarks>
public sealed class HermesSource : UsageSourceBase
{
    public const string SourceId = "hermes";

    /// <summary>2: dostawca <c>billing_provider</c> (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;

    public override string Id => SourceId;
    public override string DisplayName => "Hermes Agent";
    public override string Vendor => "Nous Research";
    public override string AccentColor => "#8E6CEF";

    protected override string[] EnvironmentVariables => ["HERMES_HOME"];

    protected override string[] Executables => ["hermes"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".hermes")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var database = Path.Combine(root, "state.db");
            if (File.Exists(database))
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
            Pooling = false
        }.ToString();

        var entries = new List<UsageEntry>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var columns = Columns(connection, "sessions");
        if (!columns.Contains("id") || !columns.Contains("model") || !columns.Contains("started_at"))
        {
            diagnostics.Skip("baza bez obsługiwanej tabeli sessions");
            return entries;
        }

        string Column(string name, string fallback = "NULL") => columns.Contains(name) ? name : fallback;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, model, started_at, {Column("message_count", "0")},
                   {Column("input_tokens", "0")}, {Column("output_tokens", "0")},
                   {Column("cache_read_tokens", "0")}, {Column("cache_write_tokens", "0")}, {Column("reasoning_tokens", "0")},
                   {Column("estimated_cost_usd")}, {Column("actual_cost_usd")}, {Column("source")},
                   {Column("billing_provider")}
            FROM sessions
            WHERE model IS NOT NULL AND TRIM(model) != ''
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Count(reader, 4);
            var output = Count(reader, 5);
            var cacheRead = Count(reader, 6);
            var cacheWrite = Count(reader, 7);
            var reasoning = Count(reader, 8);
            var estimated = NonNegative(reader, 9);
            var actual = NonNegative(reader, 10);
            // Brak kosztu rzeczywistego → szacunek Hermesa; zapisane 0 jest przekazywane dalej.
            var cost = actual ?? estimated;
            if (input + output + cacheRead + cacheWrite + reasoning == 0 && (cost ?? 0) == 0)
            {
                continue;
            }

            var sessionId = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            var model = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(model))
            {
                diagnostics.Skip("wiersz bez id sesji lub modelu");
                continue;
            }

            var startedAt = reader.IsDBNull(2) ? (double?)null : ToDouble(reader.GetValue(2));
            if (startedAt is not { } seconds || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                diagnostics.Skip("brak lub zły started_at");
                continue;
            }

            var origin = reader.IsDBNull(11) ? null : Convert.ToString(reader.GetValue(11), CultureInfo.InvariantCulture);
            entries.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = (long)Math.Truncate(seconds * 1000),
                SessionId = sessionId,
                // Kanał sesji (np. cli, telegram) jako projekt — Hermes nie zapisuje katalogu roboczego.
                Project = string.IsNullOrWhiteSpace(origin) ? null : origin,
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheCreationTokens = cacheWrite,
                CacheReadTokens = cacheRead,
                ReasoningTokens = reasoning,
                RecordedCostUsd = cost,
                IsSessionSummary = true,
                Provider = NormalizeProvider(reader.IsDBNull(12) ? null : Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture), model),
                DedupeKey = "hermes:" + sessionId
            });
        }

        return entries;
    }

    /// <summary>
    /// Kandydaci cennika (ccusage <c>hermes/src/parser.rs</c> <c>model_candidates</c>): <c>dostawca/model</c>, potem sama nazwa.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        return provider is null ? [model] : [$"{provider}/{model}", model];
    }

    /// <summary>
    /// <c>billing_provider</c> → identyfikator dostawcy (ccusage <c>normalize_provider</c>): hosty Vertex → <c>vertex_ai</c>,
    /// aliasy złożone, a bez zapisu — dostawca z nazwy modelu. Null = sam Hermes (bez trasy rozliczenia).
    /// </summary>
    internal static string? NormalizeProvider(string? value, string model)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return InferProvider(model);
        }

        if (Pricing.ProviderNames.HostProvider(trimmed) is { } host)
        {
            return host;
        }

        var normalized = trimmed.ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "anthropic" or "claude" => "anthropic",
            "openai" or "openai_codex" => "openai",
            "google" or "google_ai" or "gemini" => "google",
            "hermes" or "unknown" => null,
            _ => normalized
        };
    }

    private static string? InferProvider(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.StartsWith("claude-", StringComparison.Ordinal) || lower.StartsWith("claude/", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (lower.StartsWith("gpt", StringComparison.Ordinal) || lower.StartsWith("chatgpt", StringComparison.Ordinal) ||
            (lower.Length > 1 && lower[0] == 'o' && char.IsAsciiDigit(lower[1])))
        {
            return "openai";
        }

        return lower.StartsWith("gemini-", StringComparison.Ordinal) || lower.StartsWith("gemini/", StringComparison.Ordinal)
            ? "google"
            : null;
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

    private static long Count(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        return ToDouble(reader.GetValue(ordinal)) is { } value && value > 0 && !double.IsInfinity(value)
            ? (long)Math.Min(value, long.MaxValue)
            : 0;
    }

    private static double? NonNegative(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || ToDouble(reader.GetValue(ordinal)) is not { } value || double.IsNaN(value)
            ? null
            : Math.Max(0, value);

    private static double? ToDouble(object value) => value switch
    {
        long integer => integer,
        double real => real,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}
