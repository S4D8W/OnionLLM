using System.Globalization;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Storage;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// GitHub Copilot (Copilot CLI oraz agent Copilota w Visual Studio).
/// <list type="bullet">
/// <item><c>session-store.db</c> → tabela <c>assistant_usage_events</c> (wpis per wywołanie, najdokładniejsze; poziom 0).</item>
/// <item><c>session-state/&lt;id&gt;/events.jsonl</c> → <c>session.usage_record</c> (per wywołanie), przedziały między kolejnymi
/// <c>session.shutdown</c> oraz niewyjaśnione kredyty z <c>session.usage_checkpoint</c> (<see cref="ParseEvents"/>; poziom 1).</item>
/// <item><c>otel/**/*.jsonl</c> i plik z <c>COPILOT_OTEL_FILE_EXPORTER_PATH</c> → eksport OpenTelemetry (poziom 1).</item>
/// </list>
/// Wpisy z plików sesji i OTel są uzgadniane między plikami w <see cref="Reconcile"/> (ccusage <c>copilot/src/loader.rs</c>).
/// Katalogi: <c>COPILOT_HOME</c> lub <c>~/.copilot</c> oraz <c>%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli</c>.
/// </summary>
/// <remarks>
/// W obu formatach <c>input_tokens</c> zawiera tokeny odczytu i zapisu cache — odejmujemy je,
/// aby liczniki były rozłączne. <c>total_nano_aiu</c> to koszt w 1e-11 USD (1 AIU = 1 kredyt = 0,01 USD).
/// Dane potrzebne do uzgadniania (identyfikatory wywołań, liczba żądań, surowa nazwa modelu, kontekst trace OTel) są zapisane
/// w <see cref="UsageEntry.SourceData"/> (<see cref="CopilotKey"/>), bo ledger przechowuje wpisy, a nie pliki.
/// </remarks>
public sealed partial class CopilotSource : UsageSourceBase
{
    public const string SourceId = "copilot";

    /// <summary>Zmienna wskazująca pojedynczy plik eksportu OTel (ccusage <c>copilot/src/paths.rs:90-100</c>).</summary>
    public const string OtelExporterPathVariable = "COPILOT_OTEL_FILE_EXPORTER_PATH";

    /// <summary>Przelicznik nano-AIU na USD (sprawdzony na stawkach z <c>token_details_json</c>).</summary>
    public const double NanoAiuToUsd = 1e-11;

    /// <summary>Nano-AIU w jednym kredycie (AIU).</summary>
    public const double NanoAiuPerCredit = 1e9;

    private readonly Func<string?> _otelExporterPath;

    public CopilotSource() : this(() => Environment.GetEnvironmentVariable(OtelExporterPathVariable))
    {
    }

    /// <param name="otelExporterPath">Odczyt <c>COPILOT_OTEL_FILE_EXPORTER_PATH</c> (w testach bez zmiany środowiska procesu).</param>
    internal CopilotSource(Func<string?> otelExporterPath)
    {
        _otelExporterPath = otelExporterPath;
    }

    public override string Id => SourceId;
    public override string DisplayName => "GitHub Copilot";
    public override string Vendor => "GitHub";
    public override string AccentColor => "#7C5CFC";

    /// <summary>
    /// 4: usage_record, przedziały shutdown, kredyty, normalizacja modeli, identyfikatory wywołań w kluczu; 5: dane pomocnicze w
    /// <see cref="UsageEntry.SourceData"/>; 6: OTel — każde wywołanie z pliku, kontekst trace i reszta z <c>total_tokens</c> w danych pomocniczych.
    /// </summary>
    public override int ParserVersion => 6;

    protected override string[] EnvironmentVariables => ["COPILOT_HOME"];

    protected override string[] Executables => ["copilot"];

    protected override IEnumerable<string> DefaultRoots() =>
    [
        Path.Combine(FileUtil.Home, ".copilot"),
        Path.Combine(FileUtil.LocalAppData, "Microsoft", "VisualStudio", "CopilotCli")
    ];

    public override bool IsInstalled(OnionConfig config)
    {
        if (base.IsInstalled(config) || OtelExporterFile() is not null)
        {
            return true;
        }

        var extensionRoots = new[]
        {
            Path.Combine(FileUtil.Home, ".vscode", "extensions"),
            Path.Combine(FileUtil.Home, ".vscode-insiders", "extensions")
        };
        return extensionRoots.Any(root =>
        {
            try
            {
                return Directory.Exists(root) &&
                       Directory.EnumerateDirectories(root, "github.copilot*", SearchOption.TopDirectoryOnly).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            var database = Path.Combine(root, "session-store.db");
            if (File.Exists(database))
            {
                yield return SourceFile.Create(database, root, "-wal");
            }

            foreach (var file in FileUtil.SafeEnumerateFiles(Path.Combine(root, "otel"), "*.jsonl"))
            {
                if (seen.Add(Path.GetFullPath(file)))
                {
                    yield return SourceFile.Create(file, root);
                }
            }

            // Tylko session-state/<id>/events.jsonl — bez zagnieżdżonych katalogów (ccusage paths.rs:39-52).
            foreach (var session in SafeDirectories(Path.Combine(root, "session-state")))
            {
                var events = Path.Combine(session, "events.jsonl");
                if (File.Exists(events))
                {
                    yield return SourceFile.Create(events, root);
                }
            }
        }

        // Jawnie wskazany plik eksportu OTel jest czytany zawsze, także poza katalogiem otel/.
        if (OtelExporterFile() is { } exporter && seen.Add(exporter))
        {
            yield return SourceFile.Create(exporter, Path.GetDirectoryName(exporter) ?? exporter);
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken) =>
        file.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? ParseDatabase(file.Path, diagnostics, cancellationToken)
            : IsSessionStateFile(file.Path)
                ? ParseEvents(file.Path, diagnostics, cancellationToken)
                : ParseOtel(file.Path, diagnostics, cancellationToken);

    /// <summary>Najpierw surowa nazwa z logu (np. <c>claude-opus-4.6-1m-internal</c>), potem znormalizowana (ccusage <c>loader.rs:174-180</c>).</summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        var raw = CopilotKey.Field(entry, CopilotKey.RawModel);
        if (raw is not null)
        {
            yield return raw;
        }

        if (entry.Model is not null)
        {
            yield return entry.Model;
        }
    }

    /// <summary>
    /// Kopie tego samego wpisu: <c>usage_record</c> zostaje w sesji wskazanej przez <c>sourceSessionId</c>, potem w sesji o najniższym ID
    /// (ccusage <c>reconcile.rs:55-80</c>); pozostałe — najwcześniejsza kopia (OTel, <c>otel.rs:224-231</c>), potem reguła domyślna.
    /// </summary>
    public override IComparer<UsageEntry>? DuplicateComparer => CopyComparer;

    private static readonly IComparer<UsageEntry> CopyComparer = Comparer<UsageEntry>.Create((left, right) =>
    {
        if (CopilotKey.KindOf(left) == CopilotEntryKind.UsageRecord && CopilotKey.KindOf(right) == CopilotEntryKind.UsageRecord)
        {
            var rank = RecordRank(right).CompareTo(RecordRank(left));
            if (rank != 0)
            {
                return rank;
            }
        }

        var earlier = right.TimestampMs.CompareTo(left.TimestampMs);
        return earlier != 0 ? earlier : Deduplicator.DefaultComparer.Compare(left, right);
    });

    /// <summary>Ranga kopii <c>usage_record</c> (niższa lepsza): czy sesja pliku różni się od <c>sourceSessionId</c>, potem ID sesji.</summary>
    private static (bool Foreign, string Session) RecordRank(UsageEntry entry) =>
        (CopilotKey.Field(entry, CopilotKey.SourceSession) != entry.SessionId, entry.SessionId);

    /// <summary>
    /// Nazwa modelu do raportów i deduplikacji: bez sufiksów <c>-1m-internal</c> / <c>-1m</c> (ccusage <c>parser.rs:111-124</c>).
    /// <c>Logged</c> to surowa nazwa, gdy różni się od znormalizowanej.
    /// </summary>
    internal static (string Model, string? Logged) ModelNames(string logged)
    {
        var raw = logged.Trim();
        var model = raw.EndsWith("-1m-internal", StringComparison.Ordinal) ? raw[..^"-1m-internal".Length]
            : raw.EndsWith("-1m", StringComparison.Ordinal) ? raw[..^"-1m".Length]
            : raw;
        return (model, raw == model ? null : raw);
    }

    private string? OtelExporterFile()
    {
        var value = _otelExporterPath()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(FileUtil.ExpandHome(value));
            return File.Exists(full) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>session-state/&lt;id&gt;/events.jsonl</c>; każdy inny plik <c>.jsonl</c> to eksport OTel.</summary>
    private static bool IsSessionStateFile(string path)
    {
        var session = Path.GetDirectoryName(path);
        return string.Equals(Path.GetFileName(path), "events.jsonl", StringComparison.OrdinalIgnoreCase) &&
               session is not null &&
               string.Equals(Path.GetFileName(Path.GetDirectoryName(session)), "session-state", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<UsageEntry> ParseDatabase(string path, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var entries = new List<UsageEntry>();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            var columns = Columns(connection, "assistant_usage_events");
            if (columns.Count == 0)
            {
                return entries;
            }

            string Column(string name, string fallback = "NULL") => columns.Contains(name) ? "e." + name : fallback;

            var hasSessions = Columns(connection, "sessions").Contains("cwd");
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT e.session_id, e.model, {Column("input_tokens", "0")}, {Column("output_tokens", "0")},
                       {Column("cache_read_tokens", "0")}, {Column("cache_write_tokens", "0")}, {Column("reasoning_tokens", "0")},
                       {Column("total_nano_aiu")}, {Column("created_at")}, {Column("event_id")}, e.id,
                       {(hasSessions ? "s.cwd" : "NULL")}
                FROM assistant_usage_events e
                {(hasSessions ? "LEFT JOIN sessions s ON s.id = e.session_id" : "")}
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var createdAt = reader.IsDBNull(8) ? null : reader.GetString(8);
                if (createdAt is null || !TryParseTimestamp(createdAt, out var timestamp))
                {
                    diagnostics.Skip("wiersz bez poprawnego created_at");
                    continue;
                }

                var sessionId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var (model, logged) = reader.IsDBNull(1) ? (null, null) : ModelNames(reader.GetString(1));
                var input = Long(reader, 2);
                var output = Long(reader, 3);
                var cacheRead = Long(reader, 4);
                var cacheWrite = Long(reader, 5);
                var reasoning = Long(reader, 6);
                double? nanoAiu = reader.IsDBNull(7) ? null : reader.GetDouble(7);
                var cwd = reader.IsDBNull(11) ? null : reader.GetString(11);

                entries.Add(new UsageEntry
                {
                    Source = SourceId,
                    TimestampMs = timestamp,
                    SessionId = sessionId,
                    Model = model,
                    Project = ProjectName(cwd),
                    ProjectPath = cwd,
                    InputTokens = Math.Max(0, input - cacheRead - cacheWrite),
                    OutputTokens = output,
                    CacheReadTokens = cacheRead,
                    CacheCreationTokens = cacheWrite,
                    ReasoningTokens = reasoning,
                    RecordedCostUsd = nanoAiu * NanoAiuToUsd,
                    Credits = nanoAiu / NanoAiuPerCredit,
                    // Treść zamiast id wiersza — ta sama sesja bywa w dwóch bazach (CLI i Visual Studio).
                    DedupeKey = $"{sessionId}:{createdAt}:{model}:{input}:{output}",
                    SourceData = CopilotKey.Data((CopilotKey.RawModel, logged))
                });
            }
        }

        return entries;
    }

    private static string? ProjectName(string? cwd) => cwd is null ? null : Path.GetFileName(cwd.TrimEnd('/', '\\'));

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

    private static long Long(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Math.Max(0, Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture));

    private static bool TryParseTimestamp(string text, out long timestamp)
    {
        // created_at bywa ISO z 'Z' albo "yyyy-MM-dd HH:mm:ss" (datetime('now') w SQLite = UTC).
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            timestamp = parsed.ToUnixTimeMilliseconds();
            return true;
        }

        timestamp = 0;
        return false;
    }
}
