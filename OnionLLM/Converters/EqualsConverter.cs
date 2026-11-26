using System.Globalization;

namespace OnionLLM.Converters;

/// <summary>True when the bound value equals the converter parameter (e.g. „which field was just saved”).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
