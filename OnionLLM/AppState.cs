using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Storage;

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

    /// <summary>Postęp bieżącej synchronizacji per źródło (import historii, §7.5); czyszczony na początku każdej synchronizacji.</summary>
    public static IReadOnlyDictionary<string, SyncProgress> Progress
    {
        get
        {
            var current = _progress;
            lock (current)
            {
                return new Dictionary<string, SyncProgress>(current, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Nowy odczyt <see cref="Progress"/> (na wątku UI, najwyżej co <see cref="Design.MotionTokens.ProgressInterval"/>).</summary>
    public static event EventHandler? ProgressChanged;

    private static Dictionary<string, SyncProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private static long _progressPostedAt;

    /// <summary>Synchronizacja + cennik + limity (limity co 5 min); bezpieczne do wołania z UI.</summary>
    public static async Task RefreshDataAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _syncing);
        SyncStateChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            await Engine.LoadPricingAsync(cancellationToken: cancellationToken);
            _progress = new Dictionary<string, SyncProgress>(StringComparer.OrdinalIgnoreCase);
            await Engine.SyncAsync(force, TimeSpan.FromSeconds(10), cancellationToken, new ProgressSink());
            MainThread.BeginInvokeOnMainThread(() => ProgressChanged?.Invoke(null, EventArgs.Empty));
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

    /// <summary>Collects progress from the sync thread; posts to the UI thread at most every poll interval and at source ends.</summary>
    private sealed class ProgressSink : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value)
        {
            var current = _progress;
            lock (current)
            {
                current[value.Source] = value;
            }

            var now = Environment.TickCount64;
            var due = now - Interlocked.Read(ref _progressPostedAt) >= Design.MotionTokens.ProgressInterval.TotalMilliseconds;
            if (due || value.Files == value.TotalFiles)
            {
                Interlocked.Exchange(ref _progressPostedAt, now);
                MainThread.BeginInvokeOnMainThread(() => ProgressChanged?.Invoke(null, EventArgs.Empty));
            }
        }
    }
}
