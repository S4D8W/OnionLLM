using System.Globalization;

namespace OnionLLM.Core.Text;

/// <summary>
/// Formatowanie liczb w wyjściu tekstowym CLI i statusline (kultura niezmienna, „$12.34”, „1h 5m”) — wspólne dla CLI
/// i podglądu statusline w aplikacji. UI używa własnego <c>Fmt</c> (pl-PL, DESIGN.md §5.1).
/// </summary>
public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string Cost(double value) => "$" + value.ToString("N2", Culture);

    public static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.##", Culture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", Culture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", Culture) + "k",
        _ => value.ToString(Culture)
    };

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
    }

    /// <summary>
    /// Czytelne ID sesji: ID będące ścieżką (Codex: <c>2026/11/24/rollout-…-&lt;uuid&gt;</c>) skracane do nazwy pliku
    /// (UUID wątku), długie ID obcinane do <paramref name="max"/> znaków.
    /// </summary>
    public static string SessionId(string id, int max = 13)
    {
        var name = id;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
            foreach (var extension in new[] { ".jsonl.zst", ".jsonl", ".json" })
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^extension.Length];
                    break;
                }
            }

            if (name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(name, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}.*$");
                name = match.Success ? match.Value : name["rollout-".Length..];
            }
        }

        return name.Length > max ? name[..max] + "…" : name;
    }

    /// <summary>Skrócona nazwa modelu: bez prefiksu dostawcy i sufiksu daty.</summary>
    public static string Model(string model)
    {
        // Wiersze bez nazwy modelu (np. Copilot: same kredyty, model „unknown”).
        if (model is "unknown" or "(nieznany)" or "")
        {
            return "nieznany model";
        }

        var name = model;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            name = name[7..];
        }

        return System.Text.RegularExpressions.Regex.Replace(name, "-\\d{8}$", "");
    }
}
