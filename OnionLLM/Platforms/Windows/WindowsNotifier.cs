#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Platforms.Windows;

/// <summary>
/// Windows App Notifications (DESIGN.md §7.6): title · body · „Otwórz” (default) · „Wycisz na 1 h”; warnings silent,
/// exceeded thresholds with the default system sound. Works unpackaged (the SDK registers the COM activator).
/// </summary>
internal static class WindowsNotifier
{
    private static bool _registered;

    public static bool Register()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked;
            manager.Register();
            _registered = true;

            // The click started the app (MSIX, app not running): handle it like a click in the running app.
            if (Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification } activation &&
                activation.Data is AppNotificationActivatedEventArgs launch)
            {
                Handle(launch);
            }

            return AppNotificationManager.IsSupported();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or TypeLoadException or DllNotFoundException)
        {
            global::OnionLLM.Diagnostics.Trace("app notifications unavailable: " + exception.Message);
            return false;
        }
    }

    public static void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    public static bool Show(Alert alert)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddArgument("limit", alert.Limit)
                .AddText(alert.Title)
                .AddText(alert.Body)
                .AddButton(new AppNotificationButton("Otwórz").AddArgument("action", "open").AddArgument("limit", alert.Limit))
                .AddButton(new AppNotificationButton("Wycisz na 1 h").AddArgument("action", "mute"));
            if (alert.Level == AlertLevel.Warning)
            {
                builder.MuteAudio();
            }

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return notification.Id != 0;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            global::OnionLLM.Diagnostics.Trace("notification not shown: " + exception.Message);
            return false;
        }
    }

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Handle(args);

    private static void Handle(AppNotificationActivatedEventArgs args)
    {
        var action = args.Arguments.TryGetValue("action", out var value) ? value : "open";
        var limit = args.Arguments.TryGetValue("limit", out var key) ? key : string.Empty;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // At launch the main window may not exist yet.
            for (var attempt = 0; attempt < 100 && WindowService.Main?.Handler is null; attempt++)
            {
                await Task.Delay(Design.MotionTokens.PollInterval);
            }

            if (action == "mute")
            {
                NotificationCenter.Mute(TimeSpan.FromHours(1));
                return;
            }

            await WindowService.ShowMainAsync(NotificationCenter.ScreenFor(limit));
        });
    }
}
#endif
