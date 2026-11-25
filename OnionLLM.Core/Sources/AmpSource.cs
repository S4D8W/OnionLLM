using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Amp (ampcode.com) — pliki wątków <c>threads/**/*.json</c> (zwykle <c>T-{uuid}.json</c>).
/// Katalogi: <c>AMP_DATA_DIR</c> (lista rozdzielona przecinkami) lub <c>~/.local/share/amp</c>.
/// </summary>
/// <remarks>
/// Dwa schematy (jak w ccusage):
/// <list type="bullet">
/// <item><c>usageLedger.events[]</c> — tokeny (<c>tokens.input/output/total</c>) i kredyty; rozbicie cache
/// pochodzi z <c>messages[].usage</c> wiadomości asystenta wskazanej przez <c>toMessageId</c>.</item>
/// <item>Gdy brak księgi — <c>messages[].usage</c> wiadomości asystenta (<c>inputTokens</c>, <c>outputTokens</c>,
/// <c>cacheCreationInputTokens</c>, <c>cacheReadInputTokens</c>, <c>totalTokens</c>).</item>
/// </list>
/// <c>inputTokens</c> Ampa nie zawiera cache (schemat Anthropic), więc liczniki są rozłączne.
/// Reszta z <c>total</c> (tokeny niesklasyfikowane) w ccusage trafia tylko do sumy raportu — tu jej nie liczymy,
/// ale wpis zostaje (np. z kredytami). Amp nie zapisuje kosztu USD; ccusage nie deduplikuje wpisów.
/// </remarks>
public sealed class AmpSource : UsageSourceBase
{
    public const string SourceId = "amp";

    public override string Id => SourceId;
    public override string DisplayName => "Amp";
    public override string Vendor => "Sourcegraph";
    public override string AccentColor => "#E5484D";

    protected override string[] EnvironmentVariables => ["AMP_DATA_DIR"];

    protected override string[] Executables => ["amp"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".local", "share", "amp")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(config))
        {
            // Akceptujemy katalog danych Ampa albo bezpośrednio katalog threads.
            var threads = Path.Combine(root, "threads");
            if (!Directory.Exists(threads) &&
                string.Equals(Path.GetFileName(root), "threads", StringComparison.OrdinalIgnoreCase))
            {
                threads = root;
            }

            foreach (var file in FileUtil.SafeEnumerateFiles(threads, "*.json"))
            {
                if (seen.Add(FileUtil.NormalizePath(file)))
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = FileUtil.ReadAllText(file.Path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (!JsonUtil.TryParse(text, out var document))
        {
            diagnostics.Skip("niepoprawny JSON wątku Amp");
            return [];
        }

        using (document)
        {
            return ParseThread(document.RootElement, diagnostics);
        }
    }

    internal static List<UsageEntry> ParseThread(JsonElement thread, ParseDiagnostics diagnostics)
    {
        var entries = new List<UsageEntry>();
        if (thread.ValueKind != JsonValueKind.Object)
        {
            return entries;
        }

        var messages = thread.Prop("messages") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().ToArray()
            : [];
        var events = thread.Path("usageLedger", "events") is { ValueKind: JsonValueKind.Array } ledger
            ? ledger.EnumerateArray().ToArray()
            : null;
        var threadId = thread.Str("id");

        if (events is not null)
        {
            var cache = CacheTokensByMessageId(messages);
            foreach (var item in events)
            {
                if (LedgerUsage(item, cache) is not { } usage)
                {
                    continue;
                }

                if (threadId is null)
                {
                    diagnostics.Skip("wątek Amp z zużyciem bez id");
                    return [];
                }

                var model = item.Str("model");
                if (model is null)
                {
                    diagnostics.Skip("rekord Amp bez modelu");
                    continue;
                }

                if (item.Prop("timestamp").TimestampMs() is not { } timestamp)
                {
                    diagnostics.Skip("brak lub zły timestamp");
                    continue;
                }

                entries.Add(CreateEntry(threadId, model, timestamp, usage, Credits(item.Prop("credits"))));
            }

            return entries;
        }

        foreach (var message in messages)
        {
            if (MessageUsage(message) is not { } usage)
            {
                continue;
            }

            if (threadId is null)
            {
                diagnostics.Skip("wątek Amp z zużyciem bez id");
                return [];
            }

            var record = message.Prop("usage")!.Value;
            var model = record.Str("model") ?? message.Str("model");
            if (model is null)
            {
                diagnostics.Skip("rekord Amp bez modelu");
                continue;
            }

            if ((record.Prop("timestamp").TimestampMs() ?? message.Prop("timestamp").TimestampMs()) is not { } timestamp)
            {
                diagnostics.Skip("brak lub zły timestamp");
                continue;
            }

            entries.Add(CreateEntry(threadId, model, timestamp, usage, Credits(record.Prop("credits"))));
        }

        return entries;
    }

    private readonly record struct Usage(long Input, long Output, long CacheWrite, long CacheRead);

    private static Usage? Counted(Usage usage, long total)
    {
        var known = usage.Input + usage.Output + usage.CacheWrite + usage.CacheRead;
        // Jak ccusage: rekord liczy się, gdy ma dowolny licznik albo nadwyżkę „total” ponad znane liczniki.
        return known > 0 || total > known ? usage : null;
    }

    private static Usage? LedgerUsage(JsonElement item, Dictionary<long, (long Write, long Read)> cache)
    {
        if (item.Prop("tokens") is not { ValueKind: JsonValueKind.Object } tokens)
        {
            return null;
        }

        var (write, read) = MessageId(item.Prop("toMessageId")) is { } id && cache.TryGetValue(id, out var found)
            ? found
            : default;
        return Counted(new Usage(tokens.Long("input"), tokens.Long("output"), write, read), tokens.Long("total"));
    }

    private static Usage? MessageUsage(JsonElement message)
    {
        if (message.Str("role") != "assistant" || message.Prop("usage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return null;
        }

        return Counted(
            new Usage(
                usage.Long("inputTokens"),
                usage.Long("outputTokens"),
                usage.Long("cacheCreationInputTokens"),
                usage.Long("cacheReadInputTokens")),
            usage.Long("totalTokens"));
    }

    private static Dictionary<long, (long Write, long Read)> CacheTokensByMessageId(JsonElement[] messages)
    {
        var result = new Dictionary<long, (long, long)>();
        foreach (var message in messages)
        {
            if (message.Str("role") != "assistant" || message.Prop("messageId") is not { ValueKind: JsonValueKind.Number } idElement ||
                !idElement.TryGetInt64(out var id))
            {
                continue;
            }

            var usage = message.Prop("usage");
            result[id] = (usage?.Prop("cacheCreationInputTokens").Long() ?? 0, usage?.Prop("cacheReadInputTokens").Long() ?? 0);
        }

        return result;
    }

    private static long? MessageId(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var value) => value,
        { ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), out var value) => value,
        _ => null
    };

    private static double? Credits(JsonElement? element) => element.Double() is { } value && double.IsFinite(value) ? value : null;

    private static UsageEntry CreateEntry(string threadId, string model, long timestamp, Usage usage, double? credits) => new()
    {
        Source = SourceId,
        TimestampMs = timestamp,
        SessionId = threadId,
        Model = model,
        InputTokens = usage.Input,
        OutputTokens = usage.Output,
        CacheCreationTokens = usage.CacheWrite,
        CacheReadTokens = usage.CacheRead,
        Credits = credits
    };
}
