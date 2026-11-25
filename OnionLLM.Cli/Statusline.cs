using System.Globalization;
using System.Text.Json;
using OnionLLM.Core;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;

namespace OnionLLM.Cli;

/// <summary>
/// Linia statusu dla Claude Code (hook <c>statusLine</c> w <c>~/.claude/settings.json</c>).
/// Wejście: JSON z stdin (<c>session_id</c>, <c>transcript_path</c>, <c>model</c>, <c>cost</c>, <c>context_window</c>, <c>effort</c>).
/// </summary>
internal static class Statusline
{
    /// <summary>Wejście zamiast stdin (testy).</summary>
    internal static TextReader? InputOverride { get; set; }

    /// <summary>Zamiast budowania linii (testy ścieżki błędu): wyjątek rzucany przed obliczeniami.</summary>
    internal static Func<Exception?>? FailureOverride { get; set; }

    public const string ErrorText = "❌ Błąd generowania linii statusu";

    public static async Task<int> RunAsync(UsageEngine engine, CliArguments arguments)
    {
        var input = InputOverride is { } reader ? await reader.ReadToEndAsync() : Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : "";
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("statusline oczekuje JSON z Claude Code na stdin.");
            return 1;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine("Niepoprawny JSON na stdin: " + exception.Message);
            return 1;
        }

        using (document)
        {
            var root = document.RootElement;
            var sessionId = Str(root, "session_id") ?? "";
            var transcript = Str(root, "transcript_path");
            var refreshSeconds = arguments.GetDouble("--refresh-interval") ?? 1;
            var cacheEnabled = !arguments.Has("--no-cache");
            var cachePath = StatuslineCache.PathFor(sessionId);
            var mtime = StatuslineCache.ReadTranscriptMtime(transcript);
            var optionsKey = StatuslineCache.KeyFor(arguments);
            var initial = cacheEnabled ? StatuslineCache.Read(cachePath) : null;

            if (initial?.CachedOutput(mtime, optionsKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), refreshSeconds) is { } cached)
            {
                Console.WriteLine(cached);
                return 0;
            }

            if (cacheEnabled)
            {
                StatuslineCache.Write(cachePath, StatuslineCache.Updating(initial, transcript, mtime, optionsKey));
            }

            string text;
            try
            {
                if (FailureOverride?.Invoke() is { } failure)
                {
                    throw failure;
                }

                text = Build(engine, arguments, root, sessionId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Jak ccusage: po błędzie pokazujemy ostatni poprawny wynik, a blokadę zwalniamy.
                Console.WriteLine(string.IsNullOrEmpty(initial?.LastOutput) ? ErrorText : initial.LastOutput);
                if (cacheEnabled)
                {
                    StatuslineCache.Release(cachePath);
                }

                Console.Error.WriteLine("statusline: " + exception.Message);
                return 1;
            }

            Console.WriteLine(text);
            if (cacheEnabled)
            {
                StatuslineCache.Write(cachePath, StatuslineCache.Completed(text, transcript, mtime, optionsKey));
            }

            return 0;
        }
    }

    /// <summary><c>--model-label-aliases "id=Etykieta,id2=Inna"</c> (ccusage <c>modelLabelAliases</c>): klucz porównywany dokładnie z nazwą modelu.</summary>
    internal static IReadOnlyDictionary<string, string> ParseModelLabelAliases(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Klucz może zawierać '=' rzadko, ARN-y Bedrock zawierają ':' i '/', więc dzielimy po ostatnim '='.
            var separator = pair.LastIndexOf('=');
            if (separator > 0 && separator < pair.Length - 1)
            {
                result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }

        return result;
    }

    private static string Build(UsageEngine engine, CliArguments arguments, JsonElement root, string sessionId)
    {
        var claude = engine.FindSource(ClaudeSource.SourceId)!;
        if (!arguments.Has("--no-sync"))
        {
            engine.Ledger.Sync([claude], engine.Config);
        }

        var now = DateTimeOffset.Now;
        var reports = engine.Reports;
        var calculator = reports.Calculator;
        var today = reports.Today(now);
        var since = today.AddDays(-2);
        var entries = engine.LoadEntries(reports.StartOfDayMs(since), null, [ClaudeSource.SourceId]);

        // Sesja może trwać dłużej niż okno — koszt liczymy ze wszystkich jej wpisów.
        var sessionEntries = engine.LoadEntries(null, null, [ClaudeSource.SourceId])
            .Where(entry => entry.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var onionSessionCost = sessionEntries.Sum(entry => calculator.Calculate(entry).CostUsd);
        var ccSessionCost = root.TryGetProperty("cost", out var cost) && cost.TryGetProperty("total_cost_usd", out var total) &&
                            total.TryGetDouble(out var value)
            ? value
            : (double?)null;

        var todayCost = entries.Where(entry => reports.LocalDate(entry.TimestampMs) == today)
            .Sum(entry => calculator.Calculate(entry).CostUsd);

        var blocks = BlockBuilder.Build(entries, calculator, now, engine.Config.SessionLengthHours);
        var block = blocks.Active;

        // Model i poziom effort.
        var modelName = root.TryGetProperty("model", out var model)
            ? Str(model, "display_name") ?? Str(model, "id") ?? "?"
            : "?";
        // Opcja CLI ma pierwszeństwo przed modelLabelAliases z konfiguracji.
        if (ParseModelLabelAliases(arguments.Get("--model-label-aliases")).TryGetValue(modelName, out var alias) ||
            engine.Config.ModelLabelAliases.TryGetValue(modelName, out alias))
        {
            modelName = alias;
        }

        var effort = root.TryGetProperty("effort", out var effortElement) ? Str(effortElement, "level") : null;
        var modelSegment = $"🤖 {modelName}{(effort is null ? "" : $" ({effort})")}";

        // Koszt sesji.
        var costSource = (arguments.Get("--cost-source") ?? "auto").ToLowerInvariant();
        // N/A zamiast $0.00, gdy Claude Code nie podał kosztu (ccusage statusline.rs).
        var ccText = ccSessionCost is { } ccValue ? Format.Cost(ccValue) : "N/A";
        var sessionText = costSource switch
        {
            "cc" => ccText,
            "onion" or "ccusage" or "calculate" => Format.Cost(onionSessionCost),
            "both" => $"({ccText} cc / {Format.Cost(onionSessionCost)} onion)",
            _ => Format.Cost(ccSessionCost ?? onionSessionCost)
        };

        var blockText = block is null
            ? "brak aktywnego bloku"
            : $"{Format.Cost(block.Totals.CostUsd)} blok ({Format.Duration(block.Remaining(now))} do końca)";
        var segments = new List<string>
        {
            modelSegment,
            $"💰 {sessionText} sesja / {Format.Cost(todayCost)} dziś / {blockText}"
        };

        if (block?.BurnRate is { } burn)
        {
            var color = burn.Level switch { BurnRateLevel.High => Ansi.Red, BurnRateLevel.Moderate => Ansi.Yellow, _ => Ansi.Green };
            var visual = (arguments.Get("--visual-burn-rate") ?? "off").ToLowerInvariant();
            var emoji = burn.Level switch { BurnRateLevel.High => "🚨", BurnRateLevel.Moderate => "⚠️", _ => "🟢" };
            var label = Renderer.LevelText(burn.Level);
            var suffix = visual switch
            {
                "emoji" => " " + emoji,
                "text" => $" ({label})",
                "emoji-text" => $" {emoji} ({label})",
                _ => ""
            };
            segments.Add("🔥 " + Ansi.Color(color, $"{Format.Cost(burn.CostPerHour)}/h") + suffix);
        }

        segments.Add(ContextSegment(engine, arguments, root, sessionEntries));
        return string.Join(" | ", segments);
    }

    private static string ContextSegment(UsageEngine engine, CliArguments arguments, JsonElement root, List<UsageEntry> sessionEntries)
    {
        long? tokens = null;
        long? size = null;
        if (root.TryGetProperty("context_window", out var window) && window.ValueKind == JsonValueKind.Object)
        {
            if (window.TryGetProperty("current_usage", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                tokens = LongOf(current, "input_tokens") + LongOf(current, "cache_creation_input_tokens") + LongOf(current, "cache_read_input_tokens");
            }
            else if (window.TryGetProperty("total_input_tokens", out var totalInput) && totalInput.TryGetInt64(out var totalValue))
            {
                tokens = totalValue;
            }

            if (window.TryGetProperty("context_window_size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) && sizeValue > 0)
            {
                size = sizeValue;
            }
        }

        // Kontekst głównego wątku: ostatni wpis spoza sidechain/subagenta (Preference == 1), bez wpisów advisora.
        var last = sessionEntries
            .Where(entry => entry.Model is not null && entry.Preference == 1 &&
                            !ClaudeSource.IsAdvisor(entry))
            .MaxBy(entry => entry.TimestampMs);
        if (tokens is null && last is not null)
        {
            tokens = last.InputTokens + last.CacheCreationTokens + last.CacheReadTokens;
        }

        if (size is null && last is not null)
        {
            // Kandydaci cennika źródła (np. wpisy fast nazwane <model>-fast), nie surowa nazwa modelu.
            size = engine.Calculator.ResolvePricing(last)?.MaxInputTokens;
        }

        size ??= 200_000;
        if (tokens is null)
        {
            return "🧠 N/A";
        }

        var percent = tokens.Value * 100.0 / size.Value;
        var low = arguments.GetDouble("--context-low-threshold") ?? 50;
        var medium = arguments.GetDouble("--context-medium-threshold") ?? 80;
        var color = percent < low ? Ansi.Green : percent < medium ? Ansi.Yellow : Ansi.Red;
        return "🧠 " + Ansi.Color(color, $"{Format.Number(tokens.Value)} ({percent.ToString("0", CultureInfo.InvariantCulture)}%)");
    }

    private static long LongOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
