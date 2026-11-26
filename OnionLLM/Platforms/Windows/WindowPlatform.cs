#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using OnionLLM.Navigation;

namespace OnionLLM.Platforms.Windows;

/// <summary>Win32 / WinUI window operations for the tray and the mini view (DESIGN.md §7.1–7.3).</summary>
internal static class WindowPlatform
{
    private const string MiniXKey = "mini.x";
    private const string MiniYKey = "mini.y";

    public static IntPtr Handle(Window window) =>
        window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native ? WinRT.Interop.WindowNative.GetWindowHandle(native) : IntPtr.Zero;

    private static AppWindow? AppWindowOf(Window window) =>
        window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native ? native.AppWindow : null;

    public static void Hide(Window window) => AppWindowOf(window)?.Hide();

    public static void Show(Window window)
    {
        if (AppWindowOf(window) is not { } appWindow)
        {
            return;
        }

        appWindow.Show();
        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        SetForegroundWindow(Handle(window));
    }

    /// <summary>X on the main window: hide to the tray instead of closing (§7.1); otherwise quit the whole app.</summary>
    public static void HookMainClosing(Window window)
    {
        if (AppWindowOf(window) is not { } appWindow)
        {
            return;
        }

        appWindow.Closing += (_, args) =>
        {
            if (WindowService.HideInsteadOfClose())
            {
                args.Cancel = true;
                return;
            }

            // Without the tray the main window owns the process: close the mini view too.
            MainThread.BeginInvokeOnMainThread(WindowService.Quit);
        };
    }

    /// <summary>
    /// Mini view window (§7.2): no system frame, not resizable, rounded corners, always on top by option, remembered
    /// position (default: top-right corner of the work area, margin 16).
    /// </summary>
    public static void ConfigureMini(Window window, bool alwaysOnTop)
    {
        if (AppWindowOf(window) is not { } appWindow)
        {
            return;
        }

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = alwaysOnTop;
        }

        appWindow.IsShownInSwitchers = false;
        var hwnd = Handle(window);
        var corner = DwmCornerRound;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = window.DisplayDensity > 0 ? window.DisplayDensity : 1;
        var margin = (int)(Design.DesignTokens.Double("Space400", 16) * scale);
        var width = (int)(window.Width * scale);
        var x = ViewModels.ViewState.GetInt(MiniXKey, area.X + area.Width - width - margin);
        var y = ViewModels.ViewState.GetInt(MiniYKey, area.Y + margin);
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - margin * 4));
        appWindow.Move(new global::Windows.Graphics.PointInt32(x, y));
        appWindow.Changed += (sender, args) =>
        {
            if (args.DidPositionChange)
            {
                ViewModels.ViewState.Set(MiniXKey, sender.Position.X);
                ViewModels.ViewState.Set(MiniYKey, sender.Position.Y);
            }
        };
    }

    public static void SetAlwaysOnTop(Window window, bool value)
    {
        if (AppWindowOf(window)?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = value;
        }
    }

    /// <summary>Native drag of a frameless window from a header element (§7.2 „własny nagłówek do przeciągania”).</summary>
    public static void MakeDragHandle(View header, Window window)
    {
        header.HandlerChanged += (_, _) =>
        {
            if (header.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
            {
                element.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(element).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
                    {
                        ReleaseCapture();
                        SendMessage(Handle(window), WmNcLButtonDown, HtCaption, IntPtr.Zero);
                    }
                };
            }
        };
    }

    /// <summary>Sets the window height in DIPs (content-sized mini view).</summary>
    public static void Resize(Window window, double width, double height)
    {
        if (AppWindowOf(window) is { } appWindow)
        {
            var scale = window.DisplayDensity > 0 ? window.DisplayDensity : 1;
            appWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        }
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;
    private const uint WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
#endif
