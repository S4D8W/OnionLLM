using OnionLLM.Core;
using OnionLLM.ViewModels;

namespace OnionLLM.Navigation;

/// <summary>
/// Shared live snapshot for the surfaces outside the main window (mini view §7.2, tray §7.3, notifications §7.6):
/// rebuilt in the background after each data refresh, at most every <see cref="MinimumInterval"/>.
/// </summary>
public static class LiveState
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(20);
    private static DateTimeOffset _builtAt = DateTimeOffset.MinValue;
    private static int _building;

    public static event EventHandler? Updated;

    public static DashboardSnapshot? Snapshot { get; private set; }

    /// <summary>Alerts currently above a threshold (independent of what was already notified).</summary>
    public static IReadOnlyList<Alert> Alerts { get; private set; } = [];

    /// <summary>Highest active alert level — tray dot (§7.3); null = OK.</summary>
    public static AlertLevel? Level => Alerts.Count == 0 ? null : Alerts.Max(alert => alert.Level);

    public static DateTimeOffset UpdatedAt { get; private set; }

    public static void Start()
    {
        AppState.Refreshed += (_, _) => _ = UpdateAsync();
        AppState.Changed += (_, _) => _ = UpdateAsync(force: true);
    }

    public static async Task UpdateAsync(bool force = false)
    {
        if ((!force && DateTimeOffset.UtcNow - _builtAt < MinimumInterval) || Interlocked.Exchange(ref _building, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(() => AppState.Engine.BuildDashboard(AppState.Limits));
            _builtAt = DateTimeOffset.UtcNow;
            Snapshot = snapshot;
            UpdatedAt = DateTimeOffset.Now;
            Alerts = AlertEvaluator.Evaluate(Input(snapshot));
            MainThread.BeginInvokeOnMainThread(() => Updated?.Invoke(null, EventArgs.Empty));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            global::OnionLLM.Diagnostics.Trace("live state: " + exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _building, 0);
        }
    }

    public static AlertInput Input(DashboardSnapshot snapshot) => new()
    {
        Now = DateTimeOffset.Now,
        ToLocal = Fmt.ToLocal,
        Claude = AppState.Limits is { } limits && limits.TryGetValue("claude", out var claude) ? claude : null,
        Block = snapshot.ActiveBlock,
        BlockLimit = snapshot.BlockTokenLimit,
        Budgets = AppState.Budgets,
        Settings = AppState.Config.Notifications
    };

    /// <summary>Tray tooltip (§7.3): „OnionLLM · dziś 12,34 $ · 5 h: 62% · 7 dni: 41%”.</summary>
    public static string Summary
    {
        get
        {
            if (Snapshot is not { } snapshot)
            {
                return "OnionLLM";
            }

            var text = $"OnionLLM · dziś {Fmt.Cost(snapshot.Today.CostUsd)}";
            if (AppState.Limits is { } limits && limits.TryGetValue("claude", out var claude) && claude.IsAvailable)
            {
                text += $" · 5 h: {Fmt.Percent(claude.FiveHourUtilization * 100)} · 7 dni: {Fmt.Percent(claude.SevenDayUtilization * 100)}";
            }

            return text;
        }
    }
}
