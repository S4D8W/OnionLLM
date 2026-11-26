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

    private static readonly Dictionary<string, string> FontFamilies = [];

#if WINDOWS
    private static readonly Lazy<bool> Packaged = new(() =>
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    });

    private static bool IsPackaged => Packaged.Value;
#endif

    /// <summary>
    /// Font family usable by canvas drawing for an alias (e.g. <c>NumMedium</c>). Controls take the alias; Win2D needs the
    /// resolved family (<c>ms-appx:///InterTnum-Medium.ttf#Inter Tnum</c>), which the MAUI font manager provides.
    /// </summary>
    public static string Font(string alias)
    {
        if (FontFamilies.TryGetValue(alias, out var family))
        {
            return family;
        }

#if WINDOWS
        if (Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(IFontManager)) is IFontManager fonts)
        {
            family = fonts.GetFontFamily(Microsoft.Maui.Font.OfSize(alias, 12)).Source;
            // Win2D resolves ms-appx font URIs only with package identity (MSIX); unpackaged builds get the family name.
            if (!IsPackaged && family.IndexOf('#') is var hash and >= 0)
            {
                family = family[(hash + 1)..];
            }

            FontFamilies[alias] = family;
            return family;
        }
#endif
        return alias;
    }

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
