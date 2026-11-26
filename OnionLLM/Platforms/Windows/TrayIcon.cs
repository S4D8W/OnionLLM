#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OnionLLM.Navigation;
using OnionLLM.ViewModels;

namespace OnionLLM.Platforms.Windows;

/// <summary>
/// Tray icon (DESIGN.md §7.3) with Win32 <c>Shell_NotifyIcon</c>: monochrome onion matching the taskbar theme with a
/// status dot (warning / danger) from the active alerts, tooltip with today's cost and limits, left click — mini view,
/// double click — main window, right click — menu. Messages arrive through a subclass of the main window.
/// </summary>
internal static class TrayIcon
{
    private const uint CallbackMessage = 0x8000 + 0x4F; // WM_APP + 79
    private const uint IconId = 1;
    private const string IconFolder = "tray";

    private static IntPtr _hwnd;
    private static IntPtr _icon;
    private static string _iconKey = string.Empty;
    private static bool _added;
    private static SubclassProc? _proc;
    private static uint _taskbarCreated;
    private static IDispatcherTimer? _clickTimer;

    public static bool IsVisible => _added;

    private static Screen _balloonScreen = Screen.Limits;

    /// <summary>
    /// Fallback system notification for the unpackaged build (App Notifications unavailable): a tray notification —
    /// title, text, the app icon; silent for warnings. A click opens the main window on <paramref name="screen"/>.
    /// </summary>
    public static bool ShowNotification(string title, string body, bool silent, Screen screen)
    {
        if (!_added)
        {
            return false;
        }

        _balloonScreen = screen;
        var data = Data(NifInfo, LiveState.Summary);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = body.Length > 255 ? body[..255] : body;
        data.dwInfoFlags = NiifUser | NiifLargeIcon | (silent ? NiifNoSound : 0);
        data.hBalloonIcon = _icon;
        return Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Attaches to the main window and shows the icon when enabled in the config.</summary>
    public static void Attach(Window main)
    {
        _hwnd = WindowPlatform.Handle(main);
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _proc = WindowProc;
        SetWindowSubclass(_hwnd, _proc, IconId, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        LiveState.Updated += (_, _) => Update();
        NotificationCenter.MuteChanged += (_, _) => Update();
        AppState.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(Update);
        Update();
    }

    /// <summary>Adds, refreshes or removes the icon to match the config and the live state.</summary>
    public static void Update()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!AppState.Config.Tray.Enabled || WindowService.IsQuitting)
        {
            Remove();
            return;
        }

        var state = LiveState.Level switch { AlertLevel.Exceeded => "danger", AlertLevel.Warning => "warning", _ => "ok" };
        var key = $"tray-{(TaskbarIsLight() ? "light" : "dark")}-{state}";
        if (key != _iconKey)
        {
            var path = Path.Combine(AppContext.BaseDirectory, IconFolder, key + ".ico");
            var icon = LoadImage(IntPtr.Zero, path, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
            if (icon != IntPtr.Zero)
            {
                if (_icon != IntPtr.Zero)
                {
                    DestroyIcon(_icon);
                }

                _icon = icon;
                _iconKey = key;
            }
        }

        var tooltip = LiveState.Summary + (NotificationCenter.IsMuted ? " · powiadomienia wyciszone" : "");
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip, tooltip);
        if (!_added)
        {
            _added = Shell_NotifyIcon(NimAdd, ref data);
            data.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIcon(NimSetVersion, ref data);
        }
        else
        {
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    public static void Remove()
    {
        if (!_added)
        {
            return;
        }

        var data = Data(0, string.Empty);
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
        _iconKey = string.Empty;
    }

    private static NotifyIconData Data(uint flags, string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
    };

    /// <summary>Light taskbar → dark glyph (registry <c>SystemUsesLightTheme</c>).</summary>
    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (notification)
            {
                case WmLButtonUp:
                    // Single click toggles the mini view — after the double-click time, so a double click only opens the window.
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _clickTimer ??= Application.Current!.Dispatcher.CreateTimer();
                        _clickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
                        _clickTimer.IsRepeating = false;
                        _clickTimer.Tick -= OnSingleClick;
                        _clickTimer.Tick += OnSingleClick;
                        _clickTimer.Start();
                    });
                    break;
                case WmLButtonDblClk:
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        _clickTimer?.Stop();
                        WindowService.CloseMini(showMain: false);
                        await WindowService.ShowMainAsync();
                    });
                    break;
                case WmContextMenu:
                case WmRButtonUp:
                    ShowMenu();
                    break;
                case NinBalloonUserClick:
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        WindowService.CloseMini(showMain: false);
                        await WindowService.ShowMainAsync(_balloonScreen);
                    });
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false; // Explorer restarted: add the icon again
            MainThread.BeginInvokeOnMainThread(Update);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static void OnSingleClick(object? sender, EventArgs e) => WindowService.ToggleMini();

    private enum Command : uint
    {
        Open = 1,
        Mini,
        Refresh,
        MuteHour,
        MuteTomorrow,
        Unmute,
        AlwaysOnTop,
        Settings,
        Quit
    }

    /// <summary>Menu (§7.3): Otwórz · Mini-widok · | · Odśwież teraz · Wycisz ▸ · Zawsze na wierzchu · | · Ustawienia · Zakończ.</summary>
    private static void ShowMenu()
    {
        var menu = CreatePopupMenu();
        var mute = CreatePopupMenu();
        AppendMenu(mute, MfString, (UIntPtr)(uint)Command.MuteHour, "1 godzina");
        AppendMenu(mute, MfString, (UIntPtr)(uint)Command.MuteTomorrow, "Do jutra");
        AppendMenu(mute, MfString | (NotificationCenter.IsMuted ? 0 : MfGrayed), (UIntPtr)(uint)Command.Unmute, "Wyłącz wyciszenie");
        AppendMenu(menu, MfString | MfDefault, (UIntPtr)(uint)Command.Open, "Otwórz OnionLLM");
        AppendMenu(menu, MfString | (WindowService.IsMiniOpen ? MfChecked : 0), (UIntPtr)(uint)Command.Mini, "Mini-widok");
        AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
        AppendMenu(menu, MfString, (UIntPtr)(uint)Command.Refresh, "Odśwież teraz");
        AppendMenu(menu, MfPopup, (UIntPtr)mute.ToInt64(), "Wycisz powiadomienia");
        AppendMenu(menu, MfString | (AppState.Config.Window.AlwaysOnTop ? MfChecked : 0), (UIntPtr)(uint)Command.AlwaysOnTop, "Zawsze na wierzchu");
        AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
        AppendMenu(menu, MfString, (UIntPtr)(uint)Command.Settings, "Ustawienia");
        AppendMenu(menu, MfString, (UIntPtr)(uint)Command.Quit, "Zakończ");

        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);
        var chosen = (Command)TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (chosen)
            {
                case Command.Open:
                    await WindowService.ShowMainAsync();
                    break;
                case Command.Mini:
                    WindowService.ToggleMini();
                    break;
                case Command.Refresh:
                    await ShellViewModel.Instance.RefreshAsync(force: true);
                    break;
                case Command.MuteHour:
                    NotificationCenter.Mute(TimeSpan.FromHours(1));
                    break;
                case Command.MuteTomorrow:
                    NotificationCenter.Mute(null);
                    break;
                case Command.Unmute:
                    NotificationCenter.Unmute();
                    break;
                case Command.AlwaysOnTop:
                    WindowActions.ToggleAlwaysOnTop();
                    break;
                case Command.Settings:
                    await WindowService.ShowMainAsync(Screen.Settings);
                    break;
                case Command.Quit:
                    WindowService.Quit();
                    break;
            }
        });
    }

    // ===== Win32 =====

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifUser = 0x4, NiifNoSound = 0x10, NiifLargeIcon = 0x20;
    private const uint NinBalloonUserClick = 0x0400 + 5;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const uint MfString = 0x0, MfGrayed = 0x1, MfChecked = 0x8, MfPopup = 0x10, MfDefault = 0x1000, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmNoNotify = 0x80;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
#endif
