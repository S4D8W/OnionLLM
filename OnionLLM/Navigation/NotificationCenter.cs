using OnionLLM.ViewModels;

namespace OnionLLM.Navigation;

/// <summary>
/// System notifications (DESIGN.md §5.8, §7.6): after every live update the new alerts are sent once per limit window,
/// unless muted („Wycisz na 1 h / do jutra”) or in quiet hours. Without system notifications (other platforms,
/// registration failure) the alert is shown as an in-app toast.
/// </summary>
public static class NotificationCenter
{
    private const string SentKey = "alerts.sent";
    private const string MutedKey = "alerts.mutedUntil";

    private static AlertLog? _log;

    public static event EventHandler? MuteChanged;

    /// <summary>Windows App Notifications registered (packaged build).</summary>
    public static bool IsSystemAvailable { get; private set; }

    /// <summary>System notifications can be shown — App Notifications or, unpackaged, the tray icon notification.</summary>
    public static bool CanNotify =>
#if WINDOWS
        IsSystemAvailable || Platforms.Windows.TrayIcon.IsVisible;
#else
        false;
#endif

    public static DateTimeOffset? MutedUntil
    {
        get
        {
            var ticks = ViewModels.ViewState.GetString(MutedKey, string.Empty);
            return long.TryParse(ticks, out var value) && value > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : null;
        }
    }

    public static bool IsMuted => MutedUntil is not null;

    public static void Start()
    {
#if WINDOWS
        IsSystemAvailable = Platforms.Windows.WindowsNotifier.Register();
#endif
        LiveState.Updated += (_, _) => Dispatch();
    }

    public static void Stop()
    {
#if WINDOWS
        Platforms.Windows.WindowsNotifier.Unregister();
#endif
    }

    /// <summary>„Wycisz powiadomienia”: for <paramref name="duration"/>, until tomorrow (null) — or unmute (<see cref="Unmute"/>).</summary>
    public static void Mute(TimeSpan? duration)
    {
        var until = duration is { } span ? DateTimeOffset.Now + span : new DateTimeOffset(DateTime.Today.AddDays(1));
        ViewModels.ViewState.Set(MutedKey, until.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        MuteChanged?.Invoke(null, EventArgs.Empty);
        ToastService.Show($"Powiadomienia wyciszone do {Fmt.Time(until)}", ToastKind.Info);
    }

    public static void Unmute()
    {
        ViewModels.ViewState.Set(MutedKey, string.Empty);
        MuteChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>First hide to the tray (§7.1): „OnionLLM działa w tle”.</summary>
    public static void SendBackgroundNotice() => Send(new Alert("background", "test", AlertLevel.Warning, "OnionLLM działa w tle",
        "Kliknij ikonę w zasobniku, aby otworzyć."));

    /// <summary>„Wyślij powiadomienie testowe” (§6.9.4).</summary>
    public static void SendTest() => Send(new Alert("test", "test", AlertLevel.Warning, "Powiadomienie testowe",
        "Tak będą wyglądać powiadomienia o limitach i budżetach."));

    private static void Dispatch()
    {
        var settings = AppState.Config.Notifications;
        _log ??= AlertLog.Parse(ViewModels.ViewState.GetString(SentKey, string.Empty));
        var fresh = _log.TakeNew(LiveState.Alerts, DateTimeOffset.UtcNow, Fmt.ToLocal(DateTimeOffset.Now), settings, MutedUntil);
        if (fresh.Count == 0)
        {
            return;
        }

        ViewModels.ViewState.Set(SentKey, _log.Serialize());
        foreach (var alert in fresh)
        {
            Send(alert);
        }
    }

    private static void Send(Alert alert)
    {
        global::OnionLLM.Diagnostics.Trace($"notify: {alert.Key} „{alert.Title}”");
#if WINDOWS
        if (IsSystemAvailable && Platforms.Windows.WindowsNotifier.Show(alert))
        {
            return;
        }

        if (Platforms.Windows.TrayIcon.ShowNotification(alert.Title, alert.Body, alert.Level == AlertLevel.Warning, ScreenFor(alert.Limit)))
        {
            return;
        }
#endif
        ToastService.Show($"{alert.Title}. {alert.Body}", alert.Level == AlertLevel.Exceeded ? ToastKind.Error : ToastKind.Info);
    }

    /// <summary>Screen opened by a notification click.</summary>
    public static Screen ScreenFor(string limit) => limit switch
    {
        "block" or "burn" => Screen.Blocks,
        "test" => Screen.Settings,
        _ => Screen.Limits
    };
}
