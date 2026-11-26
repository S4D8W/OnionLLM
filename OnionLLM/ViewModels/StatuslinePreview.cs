using OnionLLM.Core.Config;
using OnionLLM.Core.Sources;
using OnionLLM.Core.Text;

namespace OnionLLM.ViewModels;

/// <summary>
/// Live statusline preview (DESIGN.md §6.9.8): the CLI composer from Core fed with the most recent Claude Code session
/// (model shown with its Claude Code display name, e.g. „Opus 4.1”) and the label aliases being edited.
/// </summary>
public static class StatuslinePreview
{
    public static Task<string> RenderAsync(string aliasesText) => Task.Run(() =>
    {
        var engine = AppState.Engine;
        var reports = engine.Reports;
        var now = DateTimeOffset.Now;
        var last = engine.LoadEntries(reports.StartOfDayMs(reports.Today(now).AddDays(-30)), null, [ClaudeSource.SourceId])
            .Where(entry => entry.Model is not null)
            .MaxBy(entry => entry.TimestampMs);
        if (last is null)
        {
            return "Brak sesji Claude Code z ostatnich 30 dni — podgląd pojawi się po pierwszej sesji.";
        }

        Dictionary<string, string> aliases;
        try
        {
            aliases = ConfigTextFormats.ParseMap(aliasesText, "Etykiety modeli");
        }
        catch (ConfigException)
        {
            aliases = [];
        }

        var input = new StatuslineInput { SessionId = last.SessionId, ModelName = DisplayName(last.Model!) };
        return StatuslineComposer.Compose(engine, input, new StatuslineOptions { ModelLabelAliases = aliases }, now);
    });

    /// <summary>„claude-opus-4-1-20250805” → „Opus 4.1” (the <c>display_name</c> Claude Code sends).</summary>
    public static string DisplayName(string model)
    {
        var name = Fmt.Model(model);
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !char.IsLetter(parts[0][0]))
        {
            return model;
        }

        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var version = string.Join('.', parts.Skip(1).TakeWhile(part => part.All(char.IsDigit)));
        return version.Length > 0 ? $"{family} {version}" : family;
    }
}
