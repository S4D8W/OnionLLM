namespace OnionLLM.ViewModels;

/// <summary>
/// Formatowanie w interfejsie (DESIGN.md §5.1–5.2). Reguły czyste są w <c>Fmt.Format.cs</c>; tutaj — konwersja do
/// strefy czasowej z ustawień (<c>AppState</c>).
/// </summary>
public static partial class Fmt
{
    /// <summary>Converts a timestamp to the configured display time zone.</summary>
    public static DateTime ToLocal(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, AppState.Engine.Reports.TimeZone).DateTime;

    public static DateTime NowLocal => ToLocal(DateTimeOffset.UtcNow);

    /// <summary>Custom format in the display time zone (prefer the dedicated helpers below).</summary>
    public static string LocalTime(DateTimeOffset value, string format = "dd.MM HH:mm") =>
        ToLocal(value).ToString(format, Culture);

    public static string Time(DateTimeOffset value) => Time(ToLocal(value));

    public static string ShortDate(DateTimeOffset value) => ShortDate(ToLocal(value), NowLocal);

    public static string DateTimeShort(DateTimeOffset value) => DateTimeShort(ToLocal(value), NowLocal);

    public static string Relative(DateTimeOffset value) => Relative(ToLocal(value), NowLocal);
}
