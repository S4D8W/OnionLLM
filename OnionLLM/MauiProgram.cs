using Microsoft.Extensions.Logging;

namespace OnionLLM
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    // DESIGN.md §2.2.1 — aliases are the only font names used in XAML and code.
                    fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                    fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                    fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                    fonts.AddFont("Inter-Bold.ttf", "InterBold");
                    fonts.AddFont("InterTnum-Medium.ttf", "NumMedium");
                    fonts.AddFont("InterTnum-SemiBold.ttf", "NumSemiBold");
                    fonts.AddFont("JetBrainsMono-Regular.ttf", "MonoRegular");
                    fonts.AddFont("JetBrainsMono-Medium.ttf", "MonoMedium");
                    fonts.AddFont("lucide.ttf", "Lucide");
                });

#if WINDOWS
            // NavItem (DESIGN.md §4.7.2): icon + label aligned left; WinUI centres button content by default.
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("OnionNavItemAlignment", (handler, view) =>
            {
                if (view is Controls.NavItem)
                {
                    handler.PlatformView.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
                }
            });

            // TextField (DESIGN.md §4.2.0) draws its own frame; the inner WinUI TextBox must be borderless and transparent.
            Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("OnionBareEditor", (handler, view) =>
            {
                if (view is Controls.BareEditor)
                {
                    handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    handler.PlatformView.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            });
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("OnionBareEntry", (handler, view) =>
            {
                if (view is Controls.BareEntry)
                {
                    handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    handler.PlatformView.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            });
#endif

#if DEBUG
    		builder.Logging.AddDebug();

            // Developer aid: ONIONLLM_DIAG_LOG=<file> appends first-chance exceptions (XAML/resource errors surface here).
            if (Environment.GetEnvironmentVariable("ONIONLLM_DIAG_LOG") is { Length: > 0 } diagnosticLog)
            {
                AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
                {
                    try
                    {
                        File.AppendAllText(diagnosticLog, $"{DateTime.Now:O} {args.Exception.GetType().FullName}: {args.Exception.Message} | inner: {args.Exception.InnerException?.GetType().Name}: {args.Exception.InnerException?.Message} | at: {new System.Diagnostics.StackTrace(1, false).ToString().Replace(Environment.NewLine, " / ")[..Math.Min(4000, new System.Diagnostics.StackTrace(1, false).ToString().Length)]}{Environment.NewLine}");
                    }
                    catch (IOException)
                    {
                    }
                };
            }
#endif

            return builder.Build();
        }
    }
}
