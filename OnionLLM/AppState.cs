using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;

namespace OnionLLM;

/// <summary>Współdzielony stan aplikacji: konfiguracja i silnik (jeden na proces).</summary>
public static class AppState
{
    private static readonly Lazy<UsageEngine> LazyEngine = new(() =>
    {
        var config = ConfigLoader.LoadOrDefault(out var error);
        ConfigError = error;
        return new UsageEngine(config);
    });

    public static UsageEngine Engine => LazyEngine.Value;

    public static OnionConfig Config => Engine.Config;

    /// <summary>Błąd wczytania pliku konfiguracji (użyto wartości domyślnych).</summary>
    public static string? ConfigError { get; private set; }

    public static IReadOnlyDictionary<string, ProviderLimits>? Limits { get; set; }

    public static DateTimeOffset LimitsReadAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Wywoływane po zmianie konfiguracji lub danych (strony odświeżają widoki).</summary>
    public static event EventHandler? Changed;

    public static void SaveConfig(OnionConfig config)
    {
        ConfigLoader.SaveUser(config);
        Engine.UpdateConfig(config);
        ConfigError = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

    private static int _syncing;

    /// <summary>Trwa synchronizacja (StatusBar, ikona odświeżania — M15).</summary>
    public static bool IsSyncing => Volatile.Read(ref _syncing) > 0;

    /// <summary>Koniec ostatniej udanej synchronizacji.</summary>
    public static DateTimeOffset? LastSyncSucceededAt { get; private set; }

    /// <summary>Komunikat ostatniego błędu synchronizacji; null po udanej synchronizacji.</summary>
    public static string? LastSyncError { get; private set; }

    /// <summary>Zmiana <see cref="IsSyncing"/>, <see cref="LastSyncSucceededAt"/> lub <see cref="LastSyncError"/>.</summary>
    public static event EventHandler? SyncStateChanged;

    /// <summary>Synchronizacja + cennik + limity (limity co 5 min); bezpieczne do wołania z UI.</summary>
    public static async Task RefreshDataAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _syncing);
        SyncStateChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            await Engine.LoadPricingAsync(cancellationToken: cancellationToken);
            await Engine.SyncAsync(force, TimeSpan.FromSeconds(10), cancellationToken);
            LastSyncSucceededAt = DateTimeOffset.UtcNow;
            LastSyncError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastSyncError = exception.Message;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _syncing);
            SyncStateChanged?.Invoke(null, EventArgs.Empty);
        }

        if (force || DateTimeOffset.UtcNow - LimitsReadAt > TimeSpan.FromMinutes(5))
        {
            try
            {
                Limits = await Engine.ReadLimitsAsync(cancellationToken);
                LimitsReadAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or HttpRequestException)
            {
            }
        }
    }
}
