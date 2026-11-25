namespace OnionLLM.ViewModels;

/// <summary>
/// Zapamiętany stan widoków (okres, zakres, źródło, podział, sortowanie) między uruchomieniami — w MAUI Preferences,
/// nie w pliku konfiguracji. W aplikacji zastępuje sekcje <c>commands.&lt;raport&gt;</c> z configu ccusage.
/// Błędy magazynu (np. brak dostępu) są ignorowane: widok startuje wtedy z wartościami domyślnymi.
/// </summary>
public static class ViewState
{
    private const string Prefix = "view.";

    public static int GetInt(string key, int fallback, int max = int.MaxValue)
    {
        try
        {
            var value = Preferences.Default.Get(Prefix + key, fallback);
            return value >= 0 && value <= max ? value : fallback;
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            return fallback;
        }
    }

    public static bool GetBool(string key, bool fallback)
    {
        try
        {
            return Preferences.Default.Get(Prefix + key, fallback);
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            return fallback;
        }
    }

    public static string GetString(string key, string fallback)
    {
        try
        {
            return Preferences.Default.Get(Prefix + key, fallback) ?? fallback;
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            return fallback;
        }
    }

    public static DateTime GetDate(string key, DateTime fallback)
    {
        var text = GetString(key, "");
        return DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var value)
            ? value
            : fallback;
    }

    public static void Set(string key, int value) => Try(() => Preferences.Default.Set(Prefix + key, value));

    public static void Set(string key, bool value) => Try(() => Preferences.Default.Set(Prefix + key, value));

    public static void Set(string key, string value) => Try(() => Preferences.Default.Set(Prefix + key, value));

    public static void Set(string key, DateTime value) =>
        Set(key, value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
        }
    }

    private static bool IsStorageError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotImplementedException or
            NotSupportedException or FormatException or InvalidCastException;
}
