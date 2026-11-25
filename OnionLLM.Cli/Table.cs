using System.Globalization;
using System.Text;

namespace OnionLLM.Cli;

public enum Align
{
    Left,
    Right
}

/// <summary>Tabela tekstowa z ramką (znaki Unicode) i opcjonalnymi kolorami ANSI.</summary>
public sealed class Table(IReadOnlyList<(string Header, Align Align)> columns)
{
    private readonly List<string[]?> _rows = []; // null = separator
    private readonly List<string?> _rowColors = [];

    public void Add(string? color, params string[] cells)
    {
        _rows.Add(cells);
        _rowColors.Add(color);
    }

    public void Separator()
    {
        _rows.Add(null);
        _rowColors.Add(null);
    }

    public string Render()
    {
        var widths = columns.Select(column => VisibleLength(column.Header)).ToArray();
        foreach (var row in _rows.OfType<string[]>())
        {
            for (var index = 0; index < widths.Length && index < row.Length; index++)
            {
                widths[index] = Math.Max(widths[index], VisibleLength(row[index]));
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine(Line('┌', '┬', '┐', widths));
        builder.AppendLine(RowText(columns.Select(column => Ansi.Bold(column.Header)).ToArray(), widths, null, header: true));
        builder.AppendLine(Line('├', '┼', '┤', widths));
        for (var index = 0; index < _rows.Count; index++)
        {
            builder.AppendLine(_rows[index] is { } row ? RowText(row, widths, _rowColors[index]) : Line('├', '┼', '┤', widths));
        }

        builder.Append(Line('└', '┴', '┘', widths));
        return builder.ToString();
    }

    private string RowText(string[] cells, int[] widths, string? color, bool header = false)
    {
        var builder = new StringBuilder("│");
        for (var index = 0; index < widths.Length; index++)
        {
            var cell = index < cells.Length ? cells[index] : "";
            var padding = widths[index] - VisibleLength(cell);
            var aligned = columns[index].Align == Align.Right && !header
                ? new string(' ', padding) + cell
                : cell + new string(' ', padding);
            builder.Append(' ').Append(color is null ? aligned : Ansi.Color(color, aligned)).Append(" │");
        }

        return builder.ToString();
    }

    private static string Line(char left, char middle, char right, int[] widths) =>
        left + string.Join(middle, widths.Select(width => new string('─', width + 2))) + right;

    private static int VisibleLength(string text)
    {
        var length = 0;
        var inEscape = false;
        foreach (var character in text)
        {
            if (character == '\u001b')
            {
                inEscape = true;
                continue;
            }

            if (inEscape)
            {
                if (character == 'm')
                {
                    inEscape = false;
                }

                continue;
            }

            length++;
        }

        return length;
    }
}

public static class Ansi
{
    public static bool Enabled { get; set; }

    public const string Red = "31";
    public const string Green = "32";
    public const string Yellow = "33";
    public const string Blue = "34";
    public const string Magenta = "35";
    public const string Cyan = "36";
    public const string Grey = "90";

    public static string Color(string code, string text) => Enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;

    public static string Bold(string text) => Enabled ? $"\u001b[1m{text}\u001b[22m" : text;

    public static void Configure()
    {
        if (Environment.GetEnvironmentVariable("FORCE_COLOR") is { Length: > 0 } force && force != "0")
        {
            Enabled = true;
            return;
        }

        Enabled = Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;
    }
}

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
