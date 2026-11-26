using System.Globalization;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;
using OnionLLM.Core.Sources;

namespace OnionLLM.Core.Text;

/// <summary>Dane od Claude Code dla jednej linii statusu (pola JSON ze stdin, wszystkie opcjonalne).</summary>
public sealed record StatuslineInput
{
    public required string SessionId { get; init; }

    /// <summary><c>model.display_name</c> lub <c>model.id</c>; null = „?”.</summary>
    public string? ModelName { get; init; }

    /// <summary><c>effort.level</c>.</summary>
    public string? Effort { get; init; }

    /// <summary><c>cost.total_cost_usd</c> wyliczony przez Claude Code.</summary>
    public double? ClaudeCodeCost { get; init; }

    /// <summary>Tokeny kontekstu z <c>context_window</c>; null = z ostatniego wpisu sesji.</summary>
    public long? ContextTokens { get; init; }

    /// <summary><c>context_window.context_window_size</c>; null = z cennika modelu (domyślnie 200 k).</summary>
    public long? ContextSize { get; init; }
}

/// <summary>Opcje statusline (CLI: <c>--cost-source</c>, <c>--visual-burn-rate</c>, progi kontekstu, aliasy etykiet).</summary>
public sealed record StatuslineOptions
{
    public string CostSource { get; init; } = "auto";

    public string VisualBurnRate { get; init; } = "off";

    public double ContextLow { get; init; } = 50;

    public double ContextMedium { get; init; } = 80;

    /// <summary>Aliasy z opcji CLI — mają pierwszeństwo przed <c>modelLabelAliases</c> z konfiguracji.</summary>
    public IReadOnlyDictionary<string, string> ModelLabelAliases { get; init; } = new Dictionary<string, string>();

    /// <summary>Kolorowanie fragmentu (kod ANSI, tekst); domyślnie bez kolorów.</summary>
    public Func<string, string, string> Colorize { get; init; } = (_, text) => text;
}

/// <summary>
/// Składa linię statusu Claude Code: model · koszt sesji / dziś / blok · tempo · kontekst. Jedna implementacja dla
/// polecenia <c>statusline</c> i podglądu w Ustawieniach → Statusline (DESIGN.md §6.9.8).
/// </summary>
public static class StatuslineComposer
{
    public const string Red = "31";
    public const string Green = "32";
    public const string Yellow = "33";

    public static string Compose(UsageEngine engine, StatuslineInput input, StatuslineOptions options, DateTimeOffset now)
    {
        var reports = engine.Reports;
        var calculator = reports.Calculator;
        var today = reports.Today(now);
        var entries = engine.LoadEntries(reports.StartOfDayMs(today.AddDays(-2)), null, [ClaudeSource.SourceId]);

        // Sesja może trwać dłużej niż okno — koszt liczymy ze wszystkich jej wpisów.
        var sessionEntries = engine.LoadEntries(null, null, [ClaudeSource.SourceId])
            .Where(entry => entry.SessionId.Equals(input.SessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var onionSessionCost = sessionEntries.Sum(entry => calculator.Calculate(entry).CostUsd);
        var todayCost = entries.Where(entry => reports.LocalDate(entry.TimestampMs) == today).Sum(entry => calculator.Calculate(entry).CostUsd);
        var block = BlockBuilder.Build(entries, calculator, now, engine.Config.SessionLengthHours).Active;

        var modelName = input.ModelName ?? "?";
        if (options.ModelLabelAliases.TryGetValue(modelName, out var alias) || engine.Config.ModelLabelAliases.TryGetValue(modelName, out alias))
        {
            modelName = alias;
        }

        var modelSegment = $"🤖 {modelName}{(input.Effort is null ? "" : $" ({input.Effort})")}";

        // N/A zamiast $0.00, gdy Claude Code nie podał kosztu (ccusage statusline.rs).
        var ccText = input.ClaudeCodeCost is { } ccValue ? Format.Cost(ccValue) : "N/A";
        var sessionText = options.CostSource.ToLowerInvariant() switch
        {
            "cc" => ccText,
            "onion" or "ccusage" or "calculate" => Format.Cost(onionSessionCost),
            "both" => $"({ccText} cc / {Format.Cost(onionSessionCost)} onion)",
            _ => Format.Cost(input.ClaudeCodeCost ?? onionSessionCost)
        };

        var blockText = block is null
            ? "brak aktywnego bloku"
            : $"{Format.Cost(block.Totals.CostUsd)} blok ({Format.Duration(block.Remaining(now))} do końca)";
        var segments = new List<string> { modelSegment, $"💰 {sessionText} sesja / {Format.Cost(todayCost)} dziś / {blockText}" };

        if (block?.BurnRate is { } burn)
        {
            var color = burn.Level switch { BurnRateLevel.High => Red, BurnRateLevel.Moderate => Yellow, _ => Green };
            var emoji = burn.Level switch { BurnRateLevel.High => "🚨", BurnRateLevel.Moderate => "⚠️", _ => "🟢" };
            var label = LevelText(burn.Level);
            var suffix = options.VisualBurnRate.ToLowerInvariant() switch
            {
                "emoji" => " " + emoji,
                "text" => $" ({label})",
                "emoji-text" => $" {emoji} ({label})",
                _ => ""
            };
            segments.Add("🔥 " + options.Colorize(color, $"{Format.Cost(burn.CostPerHour)}/h") + suffix);
        }

        segments.Add(ContextSegment(engine, input, options, sessionEntries));
        return string.Join(" | ", segments);
    }

    public static string LevelText(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "wysokie",
        BurnRateLevel.Moderate => "umiarkowane",
        _ => "normalne"
    };

    private static string ContextSegment(UsageEngine engine, StatuslineInput input, StatuslineOptions options, List<UsageEntry> sessionEntries)
    {
        var tokens = input.ContextTokens;
        var size = input.ContextSize;

        // Kontekst głównego wątku: ostatni wpis spoza sidechain/subagenta (Preference == 1), bez wpisów advisora.
        var last = sessionEntries
            .Where(entry => entry.Model is not null && entry.Preference == 1 && !ClaudeSource.IsAdvisor(entry))
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
        var color = percent < options.ContextLow ? Green : percent < options.ContextMedium ? Yellow : Red;
        return "🧠 " + options.Colorize(color, $"{Format.Number(tokens.Value)} ({percent.ToString("0", CultureInfo.InvariantCulture)}%)");
    }
}
