using System.Globalization;
using System.Text.Json;
using OnionLLM.Core;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Text;

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

        long? contextTokens = null;
        long? contextSize = null;
        if (root.TryGetProperty("context_window", out var window) && window.ValueKind == JsonValueKind.Object)
        {
            if (window.TryGetProperty("current_usage", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                contextTokens = LongOf(current, "input_tokens") + LongOf(current, "cache_creation_input_tokens") + LongOf(current, "cache_read_input_tokens");
            }
            else if (window.TryGetProperty("total_input_tokens", out var totalInput) && totalInput.TryGetInt64(out var totalValue))
            {
                contextTokens = totalValue;
            }

            if (window.TryGetProperty("context_window_size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) && sizeValue > 0)
            {
                contextSize = sizeValue;
            }
        }

        var input = new StatuslineInput
        {
            SessionId = sessionId,
            ModelName = root.TryGetProperty("model", out var model) ? Str(model, "display_name") ?? Str(model, "id") : null,
            Effort = root.TryGetProperty("effort", out var effortElement) ? Str(effortElement, "level") : null,
            ClaudeCodeCost = root.TryGetProperty("cost", out var cost) && cost.TryGetProperty("total_cost_usd", out var total) && total.TryGetDouble(out var value)
                ? value
                : null,
            ContextTokens = contextTokens,
            ContextSize = contextSize
        };
        var options = new StatuslineOptions
        {
            CostSource = arguments.Get("--cost-source") ?? "auto",
            VisualBurnRate = arguments.Get("--visual-burn-rate") ?? "off",
            ContextLow = arguments.GetDouble("--context-low-threshold") ?? 50,
            ContextMedium = arguments.GetDouble("--context-medium-threshold") ?? 80,
            ModelLabelAliases = ParseModelLabelAliases(arguments.Get("--model-label-aliases")),
            Colorize = Ansi.Color
        };
        return StatuslineComposer.Compose(engine, input, options, DateTimeOffset.Now);
    }

    private static long LongOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
