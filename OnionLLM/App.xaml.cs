using OnionLLM.Design;

namespace OnionLLM
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            AppearanceService.Apply(AppState.Config.Appearance);
            AppState.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
            {
                AppearanceService.Apply(AppState.Config.Appearance);
                foreach (var window in Windows)
                {
                    ApplyWindowSettings(window);
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // DESIGN.md §2.9.1, §7.1: title without suffixes, start 1280×840, minimum 680×560.
            var window = new Window(new MainShellPage())
            {
                Title = "OnionLLM",
                Width = 1280,
                Height = 840,
                MinimumWidth = 680,
                MinimumHeight = 560
            };

            window.HandlerChanged += (_, _) =>
            {
                ApplyWindowSettings(window);
                Navigation.ShortcutService.Attach(window);
            };
            ThemeService.ThemeChanged += (_, _) => ApplyWindowSettings(window);
            return window;
        }

        private static void ApplyWindowSettings(Window window)
        {
#if WINDOWS
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            {
                return;
            }

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            SetWindowPos(handle, new IntPtr(AppState.Config.Window.AlwaysOnTop ? HwndTopmost : HwndNoTopmost), 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate);

            // §3.4 step 7 / §7.1: title bar matches the theme (background = bg.sidebar).
            var titleBar = nativeWindow.AppWindow.TitleBar;
            var background = ToWindows(DesignTokens.Color("ColorBgSidebar"));
            var foreground = ToWindows(DesignTokens.Color("ColorTextPrimary"));
            var inactive = ToWindows(DesignTokens.Color("ColorTextTertiary"));
            var hover = ToWindows(DesignTokens.Color("ColorSurface3"));
            titleBar.BackgroundColor = background;
            titleBar.InactiveBackgroundColor = background;
            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = inactive;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
#endif
        }

#if WINDOWS
        private const int HwndTopmost = -1;
        private const int HwndNoTopmost = -2;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        private static global::Windows.UI.Color ToWindows(Color color) =>
            global::Windows.UI.Color.FromArgb((byte)(color.Alpha * 255), (byte)(color.Red * 255), (byte)(color.Green * 255),
                (byte)(color.Blue * 255));

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
#endif
    }
}
