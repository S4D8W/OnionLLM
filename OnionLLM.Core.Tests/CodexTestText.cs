using System.Globalization;

namespace OnionLLM.Core.Tests;

internal static class CodexTestText
{
    /// <summary>Wstawia wartości w miejsca <c>@0@</c>, <c>@1@</c>… szablonu linii JSON (bez ucieczek nawiasów klamrowych).</summary>
    public static string Fill(this string template, params object[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            template = template.Replace($"@{index}@", Convert.ToString(values[index], CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return template;
    }
}
