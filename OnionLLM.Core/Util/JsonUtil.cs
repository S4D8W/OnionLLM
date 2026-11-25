using System.Globalization;
using System.Text.Json;

namespace OnionLLM.Core.Util;

internal static class JsonUtil
{
    public static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Path(this JsonElement element, params string[] names)
    {
        JsonElement? current = element;
        foreach (var name in names)
        {
            current = current?.Prop(name);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static string? Str(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static string? Str(this JsonElement element, string name) => element.Prop(name).Str();

    /// <summary>Liczba całkowita (także zapisana jako tekst lub liczba zmiennoprzecinkowa); ujemne → 0.</summary>
    public static long Long(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return 0;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var integer):
                return Math.Max(0, integer);
            case JsonValueKind.Number when value.TryGetDouble(out var real):
                return Math.Max(0, (long)real);
            case JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return Math.Max(0, parsed);
            default:
                return 0;
        }
    }

    public static long Long(this JsonElement element, string name) => element.Prop(name).Long();

    public static bool IsNumber(this JsonElement? element) => element is { ValueKind: JsonValueKind.Number };

    public static double? Double(this JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (element is { ValueKind: JsonValueKind.String } text &&
            double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Znacznik czasu jako ISO 8601 lub liczba (sekundy, milisekundy albo mikrosekundy od epoki).
    /// </summary>
    public static long? TimestampMs(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericText))
            {
                return FromEpochNumber(numericText);
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return FromEpochNumber(number);
        }

        return null;
    }

    public static long? FromEpochNumber(double number)
    {
        if (number <= 0 || double.IsNaN(number) || double.IsInfinity(number))
        {
            return null;
        }

        // < 1e11 → sekundy, < 1e14 → milisekundy, w przeciwnym razie mikrosekundy.
        return number switch
        {
            < 1e11 => (long)(number * 1000),
            < 1e14 => (long)number,
            _ => (long)(number / 1000)
        };
    }
}
