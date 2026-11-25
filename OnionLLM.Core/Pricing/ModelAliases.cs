namespace OnionLLM.Core.Pricing;

/// <summary>
/// Modele-plany, za którymi w zależności od daty stał inny model (do wyceny). Nazwa wyświetlana się nie zmienia.
/// </summary>
public static class ModelAliases
{
    private sealed record Route(DateTimeOffset From, string Model);

    private static readonly Dictionary<string, Route[]> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        // ccusage #1106: plan kimi-for-coding przeszedł z Kimi K2.5 na K2.6 2026-04-20T15:28:10.072Z.
        ["kimi-for-coding"] =
        [
            new(DateTimeOffset.MinValue, "moonshot/kimi-k2.5"),
            new(DateTimeOffset.Parse("2026-04-20T15:28:10.072Z"), "moonshot/kimi-k2.6")
        ],
        // ccusage codex/parser/model.rs: auto-review Codexa kierowany na aktualny model wg daty wydania (UTC).
        ["codex-auto-review"] =
        [
            new(DateTimeOffset.MinValue, "gpt-5"),
            new(DateTimeOffset.Parse("2025-09-15T00:00:00Z"), "gpt-5-codex"),
            new(DateTimeOffset.Parse("2025-11-13T00:00:00Z"), "gpt-5.1-codex"),
            new(DateTimeOffset.Parse("2025-12-11T00:00:00Z"), "gpt-5.2-codex"),
            new(DateTimeOffset.Parse("2026-02-05T00:00:00Z"), "gpt-5.3-codex"),
            new(DateTimeOffset.Parse("2026-03-05T00:00:00Z"), "gpt-5.4"),
            new(DateTimeOffset.Parse("2026-07-30T00:00:00Z"), "gpt-5.6-luna")
        ]
    };

    /// <summary>Model używany do wyceny wpisu z chwili <paramref name="timestampMs"/>.</summary>
    public static string? ForPricing(string? model, long timestampMs)
    {
        if (model is null)
        {
            return null;
        }

        var bare = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        if (!Routes.TryGetValue(bare, out var routes))
        {
            return model;
        }

        var moment = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        return routes.Last(route => route.From <= moment).Model;
    }

    /// <summary>
    /// Alias z konfiguracji (<c>modelAliases</c>, odpowiednik <c>CCUSAGE_MODEL_ALIASES</c>, ccusage <c>model_aliases.rs</c>):
    /// <c>x</c> → cel, a <c>x-fast</c> → <c>cel-fast</c>. Bez aliasu zwraca <paramref name="model"/>.
    /// </summary>
    public static string ResolveConfigured(string model, IReadOnlyDictionary<string, string>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return model;
        }

        if (aliases.TryGetValue(model, out var alias) && !string.IsNullOrWhiteSpace(alias))
        {
            return alias;
        }

        if (model.EndsWith("-fast", StringComparison.Ordinal) &&
            aliases.TryGetValue(model[..^"-fast".Length], out var fastAlias) && !string.IsNullOrWhiteSpace(fastAlias))
        {
            return fastAlias + "-fast";
        }

        return model;
    }

    /// <summary>
    /// Parsuje aliasy w formacie <c>CCUSAGE_MODEL_ALIASES</c>: obiekt JSON albo pary <c>a=b</c> oddzielone <c>,</c>, <c>;</c> lub nową linią.
    /// </summary>
    public static Dictionary<string, string> Parse(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return result;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(trimmed);
                if (parsed is not null)
                {
                    return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }

            if (trimmed.EndsWith('}'))
            {
                trimmed = trimmed[1..^1];
            }
        }

        foreach (var pair in trimmed.Split([',', ';', '\n']))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var from = pair[..separator].Trim();
            var to = pair[(separator + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0)
            {
                result[from] = to;
            }
        }

        return result;
    }
}
