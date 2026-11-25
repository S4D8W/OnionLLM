using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Kilo Code (Kilo CLI) — baza SQLite <c>kilo.db</c>, tabela <c>message(id, session_id, data)</c>.
/// Katalogi: <c>KILO_DATA_DIR</c> (lista) lub <c>~/.local/share/kilo</c>.
/// </summary>
/// <remarks>
/// Kolumna <c>data</c> to JSON wiadomości (format OpenCode): <c>role</c>, <c>modelID</c>, <c>providerID</c>,
/// <c>tokens{input, output, reasoning, cache{read, write}}</c>, <c>time.created</c> (zawsze milisekundy), <c>cost</c>.
/// Liczymy tylko wiadomości <c>assistant</c>. <c>tokens.input</c> nie zawiera cache; <c>reasoning</c> jest poza
/// <c>output</c> i ccusage wycenia je stawką wyjścia — <see cref="UsageEntry.OutputTokens"/> = output + reasoning.
/// <c>cost</c> &gt; 0 to koszt zapisany (0 = darmowe albo nieznane, wtedy liczymy z cennika).
/// Deduplikacja po id wiadomości (ta sama baza w kilku katalogach danych).
/// </remarks>
public sealed class KiloSource : UsageSourceBase
{
    public const string SourceId = "kilo";
    public const string DatabaseFileName = "kilo.db";

    public override string Id => SourceId;
    public override string DisplayName => "Kilo Code";
    public override string Vendor => "Kilo Code";
    public override string AccentColor => "#D4B000";

    /// <summary>2: dostawca (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;

    /// <summary>
    /// <c>dostawca/model</c> (gdy dostawca nie jest <c>unknown</c> ani <c>kilo</c>), potem sama nazwa
    /// (ccusage <c>kilo/src/parser.rs</c> <c>model_candidates</c>).
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry)
    {
        if (entry.Model is null)
        {
            return [];
        }

        var prefix = OpenCodeSource.CandidatePrefix(entry.Provider);
        return prefix is null or "kilo" ? [entry.Model] : [$"{prefix}/{entry.Model}", entry.Model];
    }

    protected override string[] EnvironmentVariables => ["KILO_DATA_DIR"];

    protected override string[] Executables => ["kilo", "kilocode"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".local", "share", "kilo")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var database = Path.Combine(root, DatabaseFileName);
            if (File.Exists(database))
            {
                yield return SourceFile.Create(database, root, "-wal");
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var entries = new List<UsageEntry>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            if (!Columns(connection, "message").IsSupersetOf(["id", "session_id", "data"]))
            {
                diagnostics.Skip("baza bez tabeli message");
                return entries;
            }
        }
        catch (SqliteException)
        {
            diagnostics.Skip("nie można otworzyć bazy");
            return entries;
        }

        var directories = SessionDirectories(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, session_id, data FROM message";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = Text(reader, 2);
            if (data is null || !data.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!JsonUtil.TryParse(data, out var document))
            {
                diagnostics.Skip("niepoprawny JSON w message.data");
                continue;
            }

            using (document)
            {
                var entry = ParseMessage(document.RootElement, Text(reader, 0) ?? "", Text(reader, 1) ?? "", file.Path, directories, diagnostics);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    internal static UsageEntry? ParseMessage(JsonElement root, string rowId, string rowSessionId, string databasePath,
        IReadOnlyDictionary<string, string> directories, ParseDiagnostics diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object || root.Str("role") != "assistant")
        {
            return null;
        }

        if (root.Prop("tokens") is not { ValueKind: JsonValueKind.Object } tokens)
        {
            return null;
        }

        var input = tokens.Long("input");
        var output = tokens.Long("output");
        var reasoning = tokens.Long("reasoning");
        var cacheRead = tokens.Path("cache", "read").Long();
        var cacheWrite = tokens.Path("cache", "write").Long();
        // Sam "tokens.total" nie ma kubełka w OnionLLM (ccusage liczy go tylko do sumy) — pomijamy.
        if (input == 0 && output == 0 && reasoning == 0 && cacheRead == 0 && cacheWrite == 0)
        {
            return null;
        }

        var model = root.Str("modelID");
        if (model is null)
        {
            return null;
        }

        var created = root.Path("time", "created").Long();
        if (created <= 0)
        {
            diagnostics.Skip("brak time.created");
            return null;
        }

        var sessionId = root.Str("session_id") ?? rowSessionId;
        var messageId = root.Str("id") ?? $"{databasePath}:{rowId}";
        var cost = root.Prop("cost").Double();
        var directory = directories.GetValueOrDefault(sessionId);

        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = created,
            SessionId = sessionId,
            Project = directory is null ? null : Path.GetFileName(directory.TrimEnd('/', '\\')),
            ProjectPath = directory,
            Model = model,
            InputTokens = input,
            OutputTokens = output + reasoning,
            ReasoningTokens = reasoning,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheWrite,
            RecordedCostUsd = cost is > 0 ? cost : null,
            Provider = Pricing.ProviderNames.Normalize(root.Str("providerID")),
            DedupeKey = messageId
        };
    }

    /// <summary>Katalog roboczy sesji z tabeli <c>session</c> (schemat OpenCode), jeśli istnieje. Nie występuje w ccusage.</summary>
    private static Dictionary<string, string> SessionDirectories(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!Columns(connection, "session").IsSupersetOf(["id", "directory"]))
            {
                return result;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, directory FROM session WHERE directory IS NOT NULL";
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
}
