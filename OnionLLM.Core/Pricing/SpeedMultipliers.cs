namespace OnionLLM.Core.Pricing;

/// <summary>
/// Mnożniki poziomów szybkości dla modeli, których cennik ich nie podaje (ccusage
/// <c>fast-multiplier-overrides.json</c>, <c>flex-multiplier-overrides.json</c>, <c>ultrafast-multiplier-overrides.json</c>, <c>speed.rs</c>).
/// Model spoza listy płaci 1× — np. flex daje zniżkę tylko modelom z listy, a <c>gpt-5.1</c> w trybie fast kosztuje tyle co standard.
/// </summary>
public static class SpeedMultipliers
{
    private sealed record Table(Dictionary<string, double> Exact, Dictionary<string, double> NormalizedPrefix);

    private static readonly Table FastTable = new(
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = 2.0, ["gpt-5.6-terra"] = 2.0, ["gpt-5.6-luna"] = 2.0, ["gpt-5.5"] = 2.5, ["gpt-5.4"] = 2.0,
            ["gpt-5.3-codex"] = 2.0, ["gpt-6-astra"] = 2.0, ["gpt-6-sol"] = 2.0, ["gpt-6.1-sol"] = 2.0, ["gpt-6.1-terra"] = 2.0,
            ["gpt-6-luna"] = 2.0
        },
        new(StringComparer.OrdinalIgnoreCase) { ["claude-opus-4-6"] = 6.0, ["claude-opus-4-7"] = 6.0, ["claude-opus-4-8"] = 2.0 });

    private static readonly Table FlexTable = new(
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-6-astra"] = 0.5, ["gpt-6.1-sol"] = 0.5, ["gpt-6.1-terra"] = 0.5, ["gpt-6-luna"] = 0.5, ["gpt-6-sol"] = 0.5,
            ["gpt-5.6-sol"] = 0.5, ["gpt-5.6-terra"] = 0.5, ["gpt-5.6-luna"] = 0.5, ["gpt-5.5"] = 0.5, ["gpt-5.5-pro"] = 0.5,
            ["gpt-5.4"] = 0.5, ["gpt-5.4-mini"] = 0.5, ["gpt-5.4-nano"] = 0.5, ["gpt-5.4-pro"] = 0.5, ["gpt-5.2"] = 0.5,
            ["gpt-5.1"] = 0.5, ["gpt-5"] = 0.5, ["gpt-5-mini"] = 0.5, ["gpt-5-nano"] = 0.5, ["o3"] = 0.5, ["o4-mini"] = 0.5
        },
        new(StringComparer.OrdinalIgnoreCase));

    private static readonly Table UltrafastTable = new(
        new(StringComparer.OrdinalIgnoreCase) { ["gpt-6-astra"] = 6.0, ["gpt-6.1-sol"] = 6.0 },
        new(StringComparer.OrdinalIgnoreCase));

    public static double? Fast(string model) => Lookup(FastTable, model);

    public static double? Flex(string model) => Lookup(FlexTable, model);

    public static double? Ultrafast(string model) => Lookup(UltrafastTable, model);

    private static double? Lookup(Table table, string model)
    {
        if (table.Exact.TryGetValue(model, out var exact))
        {
            return exact;
        }

        if (PricingData.PricingAlias(model) is { } alias && table.Exact.TryGetValue(alias, out var aliased))
        {
            return aliased;
        }

        foreach (var segment in model.Split('/', ':'))
        {
            var parts = new List<string> { segment };
            for (var dot = segment.IndexOf('.'); dot >= 0; dot = segment.IndexOf('.', dot + 1))
            {
                parts.Add(segment[(dot + 1)..]);
            }

            foreach (var part in parts)
            {
                var withoutDate = PricingKeys.WithoutDateSuffix(part);
                if (table.Exact.TryGetValue(part, out var value) || table.Exact.TryGetValue(withoutDate, out value))
                {
                    return value;
                }

                if (PricingData.PricingAlias(withoutDate) is { } partAlias && table.Exact.TryGetValue(partAlias, out value))
                {
                    return value;
                }

                var normalized = PricingKeys.Identity(part);
                foreach (var (prefix, multiplier) in table.NormalizedPrefix)
                {
                    if (MatchesModelSuffix(normalized, prefix))
                    {
                        return multiplier;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>ccusage <c>matches_model_suffix</c>: rodzina na granicy słowa, po niej koniec lub <c>-</c>.</summary>
    private static bool MatchesModelSuffix(string part, string family)
    {
        var index = part.LastIndexOf(family, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var before = index == 0 || !char.IsLetterOrDigit(part[index - 1]);
        var end = index + family.Length;
        return before && (end == part.Length || part[end] == '-');
    }
}
