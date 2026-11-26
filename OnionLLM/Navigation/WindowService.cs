using OnionLLM.Views;

namespace OnionLLM.Navigation;

/// <summary>
/// Windows of the app (DESIGN.md §7.1–7.3): the main window (hidden to the tray instead of closing), the compact
/// mini view (§7.2) and quitting. Platform work (borderless mini window, hiding, tray) lives in <c>Platforms/Windows</c>.
/// </summary>
public static class WindowService
{
    private const string HiddenNoticeKey = "tray.hiddenNoticeShown";

    public static event EventHandler? Changed;

    public static Window? Main => Application.Current?.Windows.FirstOrDefault(window => window.Page is MainShellPage);

    public static Window? Mini { get; private set; }

    public static bool IsMiniOpen => Mini is not null;

    public static bool IsMainHidden { get; private set; }

    public static bool IsQuitting { get; private set; }

    /// <summary>Brings the main window back (tray double click, notification, mini view „Rozwiń”), optionally on a screen.</summary>
    public static async Task ShowMainAsync(Screen? screen = null)
    {
        if (Main is not { } main)
        {
            return;
        }

#if WINDOWS
        Platforms.Windows.WindowPlatform.Show(main);
#endif
        IsMainHidden = false;
        Changed?.Invoke(null, EventArgs.Empty);
        if (screen is { } target)
        {
            await ShellViewModel.Instance.NavigateAsync(target);
        }
    }

    public static void HideMain()
    {
        if (Main is not { } main)
        {
            return;
        }

#if WINDOWS
        Platforms.Windows.WindowPlatform.Hide(main);
#endif
        IsMainHidden = true;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Main window close (X): with the tray icon on and „Zamknięcie okna minimalizuje do zasobnika” — hide; the first time
    /// show „OnionLLM działa w tle…” (§7.1). Returns true when the close was turned into hiding.
    /// </summary>
    public static bool HideInsteadOfClose()
    {
        var config = AppState.Config;
        if (IsQuitting || !config.Tray.Enabled || !config.Window.CloseToTray || !TrayVisible)
        {
            return false;
        }

        HideMain();
        if (!ViewModels.ViewState.GetBool(HiddenNoticeKey, false))
        {
            ViewModels.ViewState.Set(HiddenNoticeKey, true);
            NotificationCenter.SendBackgroundNotice();
        }

        return true;
    }

    /// <summary>Tray icon exists on this platform (Windows).</summary>
    public static bool TrayAvailable =>
#if WINDOWS
        true;
#else
        false;
#endif

    /// <summary>The tray icon was actually added (not only enabled) — hiding the window is safe only then.</summary>
    public static bool TrayVisible =>
#if WINDOWS
        Platforms.Windows.TrayIcon.IsVisible;
#else
        false;
#endif

    /// <summary>Ctrl+Shift+M, sidebar „Tryb kompaktowy”, palette, tray (§7.2).</summary>
    public static void ToggleMini()
    {
        if (Mini is null)
        {
            OpenMini();
        }
        else
        {
            CloseMini(showMain: true);
        }
    }

    public static void OpenMini()
    {
        if (Mini is not null || Application.Current is not { } application)
        {
            return;
        }

        var page = new MiniViewPage();
        var window = new Window(page) { Title = "OnionLLM — mini-widok", Width = Design.DesignTokens.Double("SizeMiniWidth", 360), Height = Design.DesignTokens.Double("SizeMiniMinHeight", 140) };
        Mini = window;
        window.Destroying += (_, _) =>
        {
            page.Detach();
            if (!ReferenceEquals(Mini, window))
            {
                return;
            }

            // Closed by the system (Alt+F4…): never leave the app without a visible window.
            Mini = null;
            if (IsMainHidden && !IsQuitting)
            {
                _ = ShowMainAsync();
            }

            Changed?.Invoke(null, EventArgs.Empty);
        };
        application.OpenWindow(Mini);
        HideMain();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void CloseMini(bool showMain)
    {
        if (Mini is { } mini && Application.Current is { } application)
        {
            Mini = null;
            (mini.Page as MiniViewPage)?.Detach();
            application.CloseWindow(mini);
        }

        if (showMain && IsMainHidden)
        {
            _ = ShowMainAsync();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>„Zakończ” (tray menu) and closing the main window without the tray.</summary>
    public static void Quit()
    {
        if (IsQuitting)
        {
            return;
        }

        IsQuitting = true;
#if WINDOWS
        Platforms.Windows.TrayIcon.Remove();
#endif
        NotificationCenter.Stop();
        if (Mini is { } mini)
        {
            Mini = null;
            Application.Current?.CloseWindow(mini);
        }

        Application.Current?.Quit();
    }
}
