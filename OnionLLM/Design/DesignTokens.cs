namespace OnionLLM.Design;

/// <summary>
/// Read access to the active design tokens for C# code (charts, animations, converters) — DESIGN.md §3.6.
/// Values come from the merged dictionaries in <c>App.xaml</c>, so they always reflect the current theme and density.
/// </summary>
public static class DesignTokens
{
    private static readonly string[] Categorical =
    [
        "ColorChartCat1", "ColorChartCat2", "ColorChartCat3", "ColorChartCat4",
        "ColorChartCat5", "ColorChartCat6", "ColorChartCat7", "ColorChartCat8"
    ];

    public static Color Color(string key) =>
        TryGet(key, out var value) && value is Color color ? color : Colors.Transparent;

    public static double Double(string key, double fallback = 0) =>
        TryGet(key, out var value) && value is double number ? number : fallback;

    public static Thickness Thickness(string key) =>
        TryGet(key, out var value) && value is Thickness thickness ? thickness : default;

    /// <summary>Font alias (e.g. <c>NumMedium</c>) — fonts are registered under the token name itself.</summary>
    public static string Font(string alias) => alias;

    /// <summary>Theme-tuned colour of a source (<c>color.source.&lt;id&gt;</c>), never the raw brand hex from Core.</summary>
    public static Color SourceColor(string sourceId) => Color(SourceKey(sourceId));

    public static Color SourceAvatar(string sourceId) => Color(SourceKey(sourceId) + "Avatar");

    public static Color SourceOnAvatar(string sourceId) => Color(SourceKey(sourceId) + "OnAvatar");

    /// <summary>Categorical chart colour <c>color.chart.cat.(index % 8 + 1)</c>.</summary>
    public static Color ChartCategorical(int index) => Color(Categorical[((index % 8) + 8) % 8]);

    /// <summary>Resource key of a source colour; unknown ids fall back to <c>ColorSourceUnknown</c>.</summary>
    public static string SourceKey(string sourceId)
    {
        var key = "ColorSource" + string.Concat(sourceId
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        return TryGet(key, out _) ? key : "ColorSourceUnknown";
    }

    private static bool TryGet(string key, out object? value)
    {
        value = null;
        return Application.Current?.Resources.TryGetValue(key, out value) == true;
    }
}
