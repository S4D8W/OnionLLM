using System.Globalization;

namespace OnionLLM.Converters;

/// <summary>Negates a boolean (e.g. show content when an empty state is not shown).</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
