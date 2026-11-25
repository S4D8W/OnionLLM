using Microsoft.Extensions.DependencyInjection;

namespace OnionLLM
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "OnionLLM · Token Monitor",
                Width = 1200,
                Height = 820,
                MinimumWidth = 680,
                MinimumHeight = 560
            };

#if WINDOWS
            window.HandlerChanged += (_, _) => KeepWindowOnTop(window);
#endif
            return window;
        }

#if WINDOWS
        private static void KeepWindowOnTop(Window window)
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            {
                return;
            }

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
        }

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