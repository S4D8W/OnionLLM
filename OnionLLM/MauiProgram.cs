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

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
