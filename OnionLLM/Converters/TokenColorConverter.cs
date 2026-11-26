using System.Globalization;
using OnionLLM.Design;

namespace OnionLLM.Converters;

/// <summary>
/// Resolves a colour token key (e.g. <c>ColorStatusWarning</c>) chosen by a view model into the current theme's colour.
/// View models expose keys, never hex values (DESIGN.md §10.1); they re-raise the property on <c>ThemeChanged</c>.
/// </summary>
public sealed class TokenColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is string key ? DesignTokens.Color(key) : Colors.Transparent;
        return targetType == typeof(Brush) ? new SolidColorBrush(color) : color;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
