using System.Globalization;

namespace OnionLLM.Cli;

/// <summary>Prosty parser argumentów: <c>[źródło] [komenda] [--opcje]</c>.</summary>
public sealed class CliArguments
{
    private static readonly Dictionary<string, string> ShortAliases = new(StringComparer.Ordinal)
    {
        ["-s"] = "--since",
        ["-u"] = "--until",
        ["-j"] = "--json",
        ["-m"] = "--mode",
        ["-O"] = "--offline",
        ["-o"] = "--order",
        ["-b"] = "--breakdown",
        ["-z"] = "--timezone",
        ["-p"] = "--project",
        ["-a"] = "--active",
        ["-r"] = "--recent",
        ["-t"] = "--token-limit",
        ["-n"] = "--session-length",
        ["-l"] = "--session-length", // dawny skrót OnionLLM; ccusage używa -n
        ["-w"] = "--start-of-week",
        ["-B"] = "--visual-burn-rate",
        ["-q"] = "--jq",
        ["-d"] = "--debug",
        ["-v"] = "--version",
        ["-V"] = "--version",
        ["-h"] = "--help"
    };

    /// <summary>Opcje bez wartości.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--json", "--csv", "--offline", "--no-offline", "--breakdown", "--instances", "--no-cost", "--debug", "--active",
        "--recent", "--help", "--no-sync", "--force", "--compact", "--version", "--diagnostics", "--by-agent", "--color", "--no-color",
        "--include-claude-agent", "--cache", "--no-cache", "--single-thread", "--mcp"
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public static CliArguments Parse(IReadOnlyList<string> args, string? sessionCommand = null)
    {
        var result = new CliArguments();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith('-') || argument == "-")
            {
                result.Positionals.Add(argument);
                continue;
            }

            string name;
            string? inlineValue = null;
            var equals = argument.IndexOf('=');
            if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }
            else
            {
                name = argument;
            }

            // -i: w komendzie session to --id, w raportach --instances (jak w ccusage).
            if (name == "-i")
            {
                name = result.Positionals.Contains("session") ? "--id" : "--instances";
            }

            name = ShortAliases.GetValueOrDefault(name, name);
            if (Flags.Contains(name))
            {
                result._options[name] = inlineValue ?? "true";
                continue;
            }

            if (inlineValue is not null)
            {
                result._options[name] = inlineValue;
            }
            else if (index + 1 < args.Count)
            {
                result._options[name] = args[++index];
            }
            else
            {
                throw new ArgumentException($"Opcja {name} wymaga wartości.");
            }
        }

        return result;
    }

    public bool Has(string name) => _options.TryGetValue(name, out var value) && value != "false";

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public int? GetInt(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"{name}: oczekiwano liczby całkowitej, otrzymano '{value}'.");
    }

    public double? GetDouble(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"{name}: oczekiwano liczby, otrzymano '{value}'.");
    }

    public IEnumerable<string> OptionNames => _options.Keys;
}
