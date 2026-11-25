using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Goose (Block) — baza SQLite <c>sessions.db</c>, tabela <c>sessions</c> (jeden wiersz na sesję).
/// Katalogi: <c>GOOSE_PATH_ROOT</c> (jedna ścieżka; baza w <c>data/sessions/sessions.db</c>) lub domyślnie
/// <c>~/.local/share/goose</c>, <c>~/Library/Application Support/goose</c>, <c>~/.local/share/Block/goose</c>
/// oraz (Windows, poza ccusage) <c>%APPDATA%\Block\goose\data</c>. W każdym katalogu sprawdzamy
/// <c>sessions.db</c>, <c>sessions/sessions.db</c> i <c>data/sessions/sessions.db</c>.
/// </summary>
/// <remarks>
/// Tokeny: <c>accumulated_input_tokens</c> (lub <c>input_tokens</c>), <c>accumulated_output_tokens</c>
/// (lub <c>output_tokens</c>); <c>accumulated_total_tokens</c>/<c>total_tokens</c> służy tylko do pominięcia
/// pustych wierszy (nadwyżki ponad wejście+wyjście ccusage nie wycenia). Goose nie zapisuje cache ani kosztu.
/// Model z <c>model_config_json.model_name</c>, czas z <c>created_at</c> (UTC). Wpisy są zbiorcze na sesję.
/// Dodatkowo (poza ccusage) <c>working_dir</c>, jeśli kolumna istnieje, wypełnia projekt.
/// Dostawca <c>provider_name</c> trafia do <see cref="UsageEntry.Provider"/> i do kandydatów cennika (<see cref="PricingCandidates"/>).
/// </remarks>
public sealed class GooseSource : UsageSourceBase
{
    public const string SourceId = "goose";

    /// <summary>2: dostawca <c>provider_name</c> (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;

    private const string EnvironmentVariable = "GOOSE_PATH_ROOT";
    private const string DatabaseName = "sessions.db";

    public override string Id => SourceId;
    public override string DisplayName => "Goose";
    public override string Vendor => "Block";
    public override string AccentColor => "#8B5E3C";

    protected override string[] Executables => ["goose"];

    protected override IEnumerable<string> DefaultRoots()
    {
        var home = FileUtil.Home;
        yield return Path.Combine(home, ".local", "share", "goose");
        yield return Path.Combine(home, "Library", "Application Support", "goose");
        yield return Path.Combine(home, ".local", "share", "Block", "goose");
        yield return Path.Combine(FileUtil.RoamingAppData, "Block", "goose", "data");
    }

    /// <summary><c>GOOSE_PATH_ROOT</c> to jedna ścieżka (przecinek może być częścią nazwy), więc nie dzielimy jej.</summary>
    public override IReadOnlyList<string> ResolveRoots(OnionConfig config)
    {
        if (config.GetSource(Id).Paths.Any(path => !string.IsNullOrWhiteSpace(path)))
        {
            return base.ResolveRoots(config);
        }

        var value = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        return string.IsNullOrEmpty(value)
            ? base.ResolveRoots(config)
            : [FileUtil.NormalizePath(Path.Combine(FileUtil.ExpandHome(value), "data", "sessions"))];
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            var candidates = File.Exists(root)
                ? new[] { root }
                : new[]
                {
                    Path.Combine(root, DatabaseName),
                    Path.Combine(root, "sessions", DatabaseName),
                    Path.Combine(root, "data", "sessions", DatabaseName)
                };
            foreach (var database in candidates)
            {
                if (File.Exists(database) && seen.Add(FileUtil.NormalizePath(database)))
                {
                    yield return SourceFile.Create(database, root, "-wal");
                }
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
        if (!columns.Contains("id") || !columns.Contains("model_config_json") || !columns.Contains("created_at"))
        {
            diagnostics.Skip("baza Goose bez wymaganych kolumn sessions");
            return entries;
        }

        string Column(string name) => columns.Contains(name) ? name : "NULL";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, model_config_json, created_at,
                   {Column("total_tokens")}, {Column("input_tokens")}, {Column("output_tokens")},
                   {Column("accumulated_total_tokens")}, {Column("accumulated_input_tokens")}, {Column("accumulated_output_tokens")},
                   {Column("working_dir")}, {Column("provider_name")}
            FROM sessions
            WHERE model_config_json IS NOT NULL AND TRIM(model_config_json) != ''
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Positive(reader, 7) ?? Positive(reader, 4) ?? 0;
            var output = Positive(reader, 8) ?? Positive(reader, 5) ?? 0;
            var total = Positive(reader, 6) ?? Positive(reader, 3) ?? input + output;
            if (input == 0 && output == 0 && total == 0)
            {
                continue;
            }

            var id = Text(reader, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                diagnostics.Skip("wiersz Goose bez id sesji");
                continue;
            }

            var model = ModelName(Text(reader, 1));
            if (model is null)
            {
                diagnostics.Skip("wiersz Goose bez modelu");
                continue;
            }

            if (ParseTimestamp(Text(reader, 2)) is not { } timestamp)
            {
                diagnostics.Skip("wiersz Goose z niepoprawnym created_at");
                continue;
            }

            var workingDir = Text(reader, 9);
            if (string.IsNullOrWhiteSpace(workingDir))
            {
                workingDir = null;
            }

            entries.Add(new UsageEntry
            {
                Source = SourceId,
                TimestampMs = timestamp,
                SessionId = id,
                Model = model,
                Project = workingDir is null ? null : Path.GetFileName(workingDir.TrimEnd('/', '\\')),
                ProjectPath = workingDir,
                InputTokens = input,
                OutputTokens = output,
                IsSessionSummary = true,
                Provider = Pricing.ProviderNames.Normalize(Text(reader, 10))
            });
        }

        return entries;
    }

    /// <summary>
    /// Kandydaci cennika (ccusage <c>goose/src/parser.rs</c>): dostawca z logu daje dokładnego kandydata
    /// <c>dostawca/model</c>, który wyprzedza samą nazwę; dostawca wywnioskowany z nazwy modelu jest tylko zapasowy.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        if (provider is not null)
        {
            var id = Pricing.ProviderNames.HostProvider(provider) ?? provider.Replace('-', '_');
            return id == SourceId ? [model] : [$"{id}/{model}", model];
        }

        var inferred = InferProvider(model);
        return inferred is null ? [model] : [model, $"{inferred}/{model}"];
    }

    /// <summary>Dostawca z nazwy modelu, gdy Goose go nie zapisał (ccusage <c>normalize_provider</c>); null = <c>goose</c>.</summary>
    private static string? InferProvider(string model)
    {
        if (model.StartsWith("claude-", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (model.StartsWith("gpt-", StringComparison.Ordinal) || model.StartsWith("chatgpt-", StringComparison.Ordinal) ||
            model.StartsWith('o'))
        {
            return "openai";
        }

        if (model.StartsWith("gemini-", StringComparison.Ordinal))
        {
            return "google";
        }

        return model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ? "openrouter" : null;
    }

    private static string? ModelName(string? config)
    {
        if (string.IsNullOrWhiteSpace(config) || !JsonUtil.TryParse(config, out var document))
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.Str("model_name");
        }
    }

    /// <summary>
    /// <c>created_at</c>: liczba epoki (sekundy &lt; 1e10, inaczej ms), ISO 8601 albo <c>yyyy-MM-dd HH:mm:ss</c>
    /// / <c>yyyy-MM-dd</c> (SQLite <c>CURRENT_TIMESTAMP</c> = UTC).
    /// </summary>
    internal static long? ParseTimestamp(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number switch
            {
                <= 0 => null,
                < 10_000_000_000 => number * 1000,
                _ => number
            };
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? Positive(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            var value = Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return value > 0 ? value : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
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
}
