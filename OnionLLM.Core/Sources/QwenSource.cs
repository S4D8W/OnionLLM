using System.Text.Json;
using OnionLLM.Core.Config;
using OnionLLM.Core.Models;
using OnionLLM.Core.Util;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Qwen Code — czaty JSONL w <c>projects/&lt;projekt&gt;/chats/*.jsonl</c> oraz <c>chats/archive/*.jsonl</c>.
/// Katalogi: <c>QWEN_DATA_DIR</c> (lista) lub <c>~/.qwen</c>.
/// </summary>
/// <remarks>
/// Zużycie z rekordów <c>type == "assistant"</c> z <c>usageMetadata</c> (format Gemini API):
/// <list type="bullet">
/// <item><c>cachedContentTokenCount</c> jest częścią <c>promptTokenCount</c> — wejście = prompt − cache.</item>
/// <item>Wyjście = <c>candidatesTokenCount</c> (przycięte do total − prompt); bez candidates: total − prompt.</item>
/// <item><c>thoughtsTokenCount</c> leżące poza candidates (wg <c>totalTokenCount</c>) doliczamy do wyjścia —
/// ccusage wycenia je stawką wyjścia. <see cref="UsageEntry.ReasoningTokens"/> = thoughts (≤ wyjście).</item>
/// </list>
/// Sesja przeniesiona do <c>archive/</c> bywa w obu miejscach — klucz deduplikacji jak w ccusage
/// (sesja, tekst znacznika czasu, model, liczniki).
/// </remarks>
public sealed class QwenSource : UsageSourceBase
{
    public const string SourceId = "qwen";
    public const string FallbackModel = "unknown";

    public override string Id => SourceId;
    public override string DisplayName => "Qwen Code";
    public override string Vendor => "Alibaba";
    public override string AccentColor => "#FF6A00";

    /// <summary>2: <c>candidatesTokenCount: null</c> = 0 (pole obecne).</summary>
    public override int ParserVersion => 2;

    protected override string[] EnvironmentVariables => ["QWEN_DATA_DIR"];

    protected override string[] Executables => ["qwen"];

    protected override IEnumerable<string> DefaultRoots() => [Path.Combine(FileUtil.Home, ".qwen")];

    public override IEnumerable<SourceFile> EnumerateFiles(OnionConfig config)
    {
        foreach (var root in ResolveRoots(config))
        {
            var projects = Path.Combine(root, "projects");
            if (!Directory.Exists(projects))
            {
                continue;
            }

            string[] projectDirectories;
            try
            {
                projectDirectories = Directory.GetDirectories(projects);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var project in projectDirectories.Order(StringComparer.Ordinal))
            {
                var chats = Path.Combine(project, "chats");
                // Tylko bezpośrednio w chats/ oraz płasko w chats/archive/ (bez głębszych podkatalogów).
                var files = FileUtil.SafeEnumerateFiles(chats, "*.jsonl", SearchOption.TopDirectoryOnly)
                    .Concat(FileUtil.SafeEnumerateFiles(Path.Combine(chats, "archive"), "*.jsonl", SearchOption.TopDirectoryOnly));
                foreach (var file in files)
                {
                    yield return SourceFile.Create(file, root);
                }
            }
        }
    }

    public override IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var project = ProjectFromPath(file.Path);
        foreach (var line in FileUtil.ReadLines(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Contains("\"usageMetadata\"", StringComparison.Ordinal))
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
                var entry = ParseLine(document.RootElement, file.Path, project, diagnostics);
                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
    }

    internal static UsageEntry? ParseLine(JsonElement record, string path, string project, ParseDiagnostics diagnostics)
    {
        if (record.ValueKind != JsonValueKind.Object || record.Str("type") != "assistant")
        {
            return null;
        }

        if (record.Prop("usageMetadata") is not { } usageElement)
        {
            return null;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Skip("usageMetadata nie jest obiektem");
            return null;
        }

        var promptElement = usageElement.Prop("promptTokenCount");
        var hasPrompt = promptElement is { ValueKind: JsonValueKind.Number or JsonValueKind.String };
        var prompt = promptElement.Long();
        var candidatesElement = usageElement.Prop("candidatesTokenCount");
        // Obecne pole (także null albo tekst) liczy się jako podane, z wartością 0, gdy nie jest liczbą
        // (ccusage qwen/src/parser.rs: present_u64 → Some(lenient_u64)).
        var hasCandidates = candidatesElement is not null;
        var candidates = candidatesElement.Long();
        var thoughts = usageElement.Long("thoughtsTokenCount");
        var cached = usageElement.Long("cachedContentTokenCount");
        var total = usageElement.Long("totalTokenCount");

        var cacheRead = Math.Min(cached, prompt);
        long output;
        if (hasCandidates)
        {
            output = total > prompt ? Math.Min(candidates, total - prompt) : candidates;
        }
        else
        {
            // Qwen Code (cost-ledger.ts): total − prompt = wyjście razem z myśleniem.
            output = hasPrompt ? Math.Max(0, total - prompt) : 0;
        }

        var input = prompt - cacheRead;
        var remainder = Math.Max(0, total - (input + output + cacheRead));
        var thoughtsOnly = !hasCandidates && total == 0;
        var extraReasoning = thoughtsOnly ? thoughts : Math.Min(thoughts, remainder);
        var extraTotal = thoughtsOnly ? extraReasoning : remainder;

        // Same "totalTokenCount" (bez promptu) nie ma kubełka w OnionLLM — pomijamy.
        if (input == 0 && output == 0 && cacheRead == 0 && extraReasoning == 0)
        {
            return null;
        }

        var timestampText = record.Prop("timestamp").Str();
        var timestamp = record.Prop("timestamp").TimestampMs();
        if (timestampText is null || timestamp is null)
        {
            diagnostics.Skip("brak lub zły timestamp");
            return null;
        }

        var sessionId = record.Str("sessionId") ?? $"{project}-{Path.GetFileNameWithoutExtension(path)}";
        var model = record.Str("model");
        var cwd = record.Str("cwd");
        var outputTokens = output + extraReasoning;

        return new UsageEntry
        {
            Source = SourceId,
            TimestampMs = timestamp.Value,
            SessionId = sessionId,
            Project = project,
            ProjectPath = cwd,
            Model = model ?? FallbackModel,
            IsFallbackModel = model is null,
            InputTokens = input,
            CacheReadTokens = cacheRead,
            OutputTokens = outputTokens,
            ReasoningTokens = Math.Min(thoughts, outputTokens),
            // Ta sama sesja w chats/ i chats/archive/ liczy się raz (jak entry_id w ccusage).
            DedupeKey = string.Join('\u001F', sessionId, timestampText, model ?? FallbackModel, input, output, cacheRead, extraTotal)
        };
    }

    /// <summary><c>.../projects/&lt;projekt&gt;/chats/[archive/]plik.jsonl</c> → <c>projekt</c>.</summary>
    private static string ProjectFromPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && string.Equals(Path.GetFileName(directory), "archive", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory);
        }

        if (directory is not null && string.Equals(Path.GetFileName(directory), "chats", StringComparison.OrdinalIgnoreCase))
        {
            var project = Path.GetFileName(Path.GetDirectoryName(directory));
            if (!string.IsNullOrWhiteSpace(project))
            {
                return project;
            }
        }

        return "unknown";
    }
}
