using System.Globalization;
using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Cline — rozszerzenie edytora (VS Code, Insiders, Cursor, Windsurf, VSCodium) oraz Cline CLI.
/// <list type="bullet">
/// <item>Zadanie rozszerzenia: <c>tasks/&lt;id&gt;/ui_messages.json</c> (sesja <c>task:&lt;id&gt;</c>).</item>
/// <item>Starsze zadanie CLI: <c>data/tasks/&lt;id&gt;/ui_messages.json</c> (sesja <c>task:&lt;id&gt;</c>).</item>
/// <item>Transkrypt CLI: <c>data/sessions/&lt;id&gt;/&lt;id&gt;.messages.json</c> lub <c>sessions/&lt;id&gt;/&lt;id&gt;.messages.json</c>
/// (sesja <c>cli:&lt;id&gt;</c>).</item>
/// </list>
/// Katalogi: <c>CLINE_DATA_DIR</c> (lista, zastępuje domyślne) lub <c>~/.cline</c> i
/// <c>&lt;edytor&gt;/User/globalStorage/saoudrizwan.claude-dev</c> w <c>%APPDATA%</c>, <c>~/.config</c>
/// (<c>XDG_CONFIG_HOME</c>) i <c>~/Library/Application Support</c>.
/// </summary>
/// <remarks>
/// Rozszerzenie: tylko ukończone zdarzenia <c>say/api_req_started</c> (bez <c>partial: true</c>) z własnym
/// <c>ts</c> (ms), <c>modelInfo.modelId</c> i JSON-em w <c>text</c> zawierającym wszystkie cztery liczniki
/// <c>tokensIn/tokensOut/cacheReads/cacheWrites</c> (całkowite, nieujemne). <c>tokensIn</c> nie zawiera cache.
/// CLI: wiadomości asystenta z <c>metrics.inputTokens/outputTokens/cacheReadTokens/cacheWriteTokens</c> i
/// <c>modelInfo.id</c>; <c>inputTokens</c> zawiera cache, więc odejmujemy oba liczniki cache (rekord, w którym
/// cache przekracza wejście, jest pomijany). Pomijane są rekordy z samymi zerami, model <c>unknown</c>,
/// agregaty <c>deleted_api_reqs</c> i <c>subagent_usage</c>. Koszt z pola <c>cost</c> (0 = brak kosztu).
/// ccusage nie deduplikuje wpisów — tylko pliki osiągalne z kilku katalogów są czytane raz.
/// Dostawca: <c>modelInfo.providerId</c> (rozszerzenie) lub <c>modelInfo.provider</c> (CLI) → <see cref="UsageEntry.Provider"/>.
/// </remarks>
public sealed class ClineSource : UsageSourceBase
{
    public const string SourceId = "cline";

    /// <summary>2: dostawca z <c>modelInfo</c> (<see cref="UsageEntry.Provider"/>).</summary>
    public override int ParserVersion => 2;

    private const string ExtensionId = "saoudrizwan.claude-dev";

    private static readonly string[] Editors = ["Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium"];

    public override string Id => SourceId;
    public override string DisplayName => "Cline";
    public override string Vendor => "Cline";
    public override string AccentColor => "#2F80ED";

    protected override string[] EnvironmentVariables => ["CLINE_DATA_DIR"];

    protected override string[] Executables => ["cline"];

    protected override IEnumerable<string> DefaultRoots()
    {
        var home = FileUtil.Home;
        yield return Path.Combine(home, ".cline");

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(home, ".config");
        }

        var appData = FileUtil.RoamingAppData;
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(home, "AppData", "Roaming");
        }

        foreach (var editor in Editors)
        {
            yield return Path.Combine(appData, editor, "User", "globalStorage", ExtensionId);
            yield return Path.Combine(configHome, editor, "User", "globalStorage", ExtensionId);
            yield return Path.Combine(home, "Library", "Application Support", editor, "User", "globalStorage", ExtensionId);
        }
    }

    internal enum FileKind
    {
        Extension,
        CliTask,
        Cli
    }

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            var candidates = new List<string>();
            candidates.AddRange(TaskFiles(Path.Combine(root, "tasks")));
            candidates.AddRange(TaskFiles(Path.Combine(root, "data", "tasks")));
            candidates.AddRange(SessionFiles(Path.Combine(root, "data", "sessions")));
            candidates.AddRange(SessionFiles(Path.Combine(root, "sessions")));
            candidates.Sort(StringComparer.Ordinal);

            foreach (var file in candidates)
            {
                // Ten sam plik bywa osiągalny z dwóch katalogów (np. ~/.cline i ~/.cline/data) — czytamy go raz.
                if (seen.Add(FileUtil.NormalizePath(file)))
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    private static IEnumerable<string> TaskFiles(string directory) =>
        SafeDirectories(directory)
            .Select(task => Path.Combine(task, "ui_messages.json"))
            .Where(File.Exists);

    private static IEnumerable<string> SessionFiles(string directory) =>
        SafeDirectories(directory)
            .Select(session => Path.Combine(session, Path.GetFileName(session) + ".messages.json"))
            .Where(File.Exists);

    private static string[] SafeDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory)
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static (FileKind Kind, string SessionId)? Classify(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            ["tasks", var id, "ui_messages.json"] => (FileKind.Extension, id),
            ["data", "tasks", var id, "ui_messages.json"] => (FileKind.CliTask, id),
            ["data", "sessions", var id, var name] when name == id + ".messages.json" => (FileKind.Cli, id),
            ["sessions", var id, var name] when name == id + ".messages.json" => (FileKind.Cli, id),
            _ => null
        };
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Classify(file.Root, file.Path) is not { } classified)
        {
            return [];
        }

        var text = FileUtil.ReadAllText(file.Path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (!JsonUtil.TryParse(text, out var document))
        {
            diagnostics.Skip("niepoprawny JSON Cline");
            return [];
        }

        using (document)
        {
            return classified.Kind == FileKind.Cli
                ? ParseCli(document.RootElement, "cli:" + classified.SessionId, diagnostics)
                : ParseExtension(document.RootElement, "task:" + classified.SessionId, diagnostics);
        }
    }

    internal static List<UsageEntry> ParseExtension(JsonElement root, string sessionId, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var message in root.EnumerateArray())
        {
            if (message.Str("type") != "say" || message.Str("say") != "api_req_started")
            {
                continue;
            }

            if (message.Prop("partial") is { ValueKind: JsonValueKind.True })
            {
                continue;
            }

            if (message.Prop("text") is not { ValueKind: JsonValueKind.String } textElement ||
                !JsonUtil.TryParse(textElement.GetString() ?? "", out var payloadDocument))
            {
                continue;
            }

            using (payloadDocument)
            {
                var metrics = payloadDocument.RootElement;
                if (metrics.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var model = ValidModel(message.Path("modelInfo", "modelId").Str());
                if (model is null)
                {
                    continue;
                }

                if (UnsignedInteger(metrics.Prop("tokensIn")) is not { } input ||
                    UnsignedInteger(metrics.Prop("tokensOut")) is not { } output ||
                    UnsignedInteger(metrics.Prop("cacheWrites")) is not { } cacheWrite ||
                    UnsignedInteger(metrics.Prop("cacheReads")) is not { } cacheRead)
                {
                    continue;
                }

                if (!IsCounted(input, output, cacheWrite, cacheRead))
                {
                    continue;
                }

                if (MillisecondTimestamp(message.Prop("ts")) is not { } timestamp)
                {
                    diagnostics.Skip("rekord Cline bez poprawnego ts");
                    continue;
                }

                entries.Add(CreateEntry(sessionId, model, message.Path("modelInfo", "providerId").Str(), timestamp, input, output,
                    cacheWrite, cacheRead, Cost(metrics.Prop("cost"))));
            }
        }

        return entries;
    }

    internal static List<UsageEntry> ParseCli(JsonElement root, string sessionId, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        if (root.Prop("messages") is not { ValueKind: JsonValueKind.Array } messages)
        {
            return entries;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.Str("role") != "assistant" || message.Prop("metrics") is not { ValueKind: JsonValueKind.Object } metrics)
            {
                continue;
            }

            if (UnsignedInteger(metrics.Prop("cacheReadTokens")) is not { } cacheRead ||
                UnsignedInteger(metrics.Prop("cacheWriteTokens")) is not { } cacheWrite ||
                UnsignedInteger(metrics.Prop("inputTokens")) is not { } inclusiveInput ||
                UnsignedInteger(metrics.Prop("outputTokens")) is not { } output)
            {
                continue;
            }

            // inputTokens CLI zawiera cache — rekord niespójny (cache > wejście) pomijamy, nie przycinamy.
            long input;
            try
            {
                input = checked(inclusiveInput - checked(cacheRead + cacheWrite));
            }
            catch (OverflowException)
            {
                continue;
            }

            if (input < 0 || !IsCounted(input, output, cacheWrite, cacheRead))
            {
                continue;
            }

            var model = ValidModel(message.Path("modelInfo", "id").Str());
            if (model is null)
            {
                continue;
            }

            // Obecne, ale niepoprawne ts nie jest zastępowane polem timestamp.
            long? timestamp = message.Prop("ts") is { } ts
                ? MillisecondTimestamp(ts)
                : message.Prop("timestamp") is { ValueKind: JsonValueKind.String } iso ? ((JsonElement?)iso).TimestampMs() : null;
            if (timestamp is null)
            {
                diagnostics.Skip("rekord Cline bez poprawnego ts");
                continue;
            }

            entries.Add(CreateEntry(sessionId, model, message.Path("modelInfo", "provider").Str(), timestamp.Value, input, output,
                cacheWrite, cacheRead, Cost(metrics.Prop("cost"))));
        }

        return entries;
    }

    private static UsageEntry CreateEntry(string sessionId, string model, string? provider, long timestamp, long input, long output,
        long cacheWrite, long cacheRead, double? cost) => new()
    {
        Source = SourceId,
        TimestampMs = timestamp,
        SessionId = sessionId,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheCreationTokens = cacheWrite,
        CacheReadTokens = cacheRead,
        RecordedCostUsd = cost,
        Provider = Pricing.ProviderNames.Normalize(provider)
    };

    /// <summary>
    /// Kandydaci cennika (ccusage <c>cline/src/parser.rs</c> <c>loaded_entry</c>): dostawca z logu daje dokładnego kandydata
    /// <c>dostawca/model</c> (tylko dla nazwy bez ukośnika), który wyprzedza samą nazwę modelu.
    /// </summary>
    public override IEnumerable<string> PricingCandidates(UsageEntry entry) => ModelCandidates(entry.Model, entry.Provider);

    internal static IReadOnlyList<string> ModelCandidates(string? model, string? provider)
    {
        if (string.IsNullOrEmpty(model))
        {
            return [];
        }

        return !model.Contains('/') && Pricing.ProviderNames.Qualified(model, provider) is { } qualified
            ? [qualified, model]
            : [model];
    }

    private static string? ValidModel(string? model) =>
        model is null || model.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : model;

    private static bool IsCounted(long input, long output, long cacheWrite, long cacheRead)
    {
        if (input == 0 && output == 0 && cacheWrite == 0 && cacheRead == 0)
        {
            return false;
        }

        try
        {
            _ = checked(input + output + cacheWrite + cacheRead);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Licznik musi być nieujemną liczbą całkowitą JSON (nie tekstem, nie ułamkiem).</summary>
    private static long? UnsignedInteger(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var value) && value >= 0 ? value : null;

    /// <summary><c>ts</c> to całkowita liczba milisekund od epoki (0 jest poprawne, jak w ccusage).</summary>
    private static long? MillisecondTimestamp(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var value) && value is >= 0 and <= 253402300799999
            ? value
            : null;

    /// <summary>Koszt zapisany przez Cline; 0 traktujemy jak brak kosztu (wycenia go cennik).</summary>
    private static double? Cost(JsonElement? element)
    {
        var value = element switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetDouble(out var parsed) => parsed,
            { ValueKind: JsonValueKind.String } text when double.TryParse(text.GetString()?.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (double?)null
        };
        return value is > 0 && double.IsFinite(value.Value) ? value : null;
    }
}
