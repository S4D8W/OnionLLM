using OnionLLM.Core.Config;
using OnionLLM.Core.Util;
using OnionLLM.Design;
using OnionLLM.Navigation;

namespace OnionLLM.ViewModels;

/// <summary>
/// Window-wide actions shared by shortcuts (§9.6), the command palette (§4.6.7) and Ustawienia — one implementation,
/// several entry points.
/// </summary>
public static class WindowActions
{
    /// <summary>Ctrl+Shift+L: Systemowy → Jasny → Ciemny, saved to <c>appearance.theme</c>.</summary>
    public static void CycleTheme()
    {
        var next = ThemeService.Next(AppState.Config.Appearance.Theme);
        if (Save(config => config.Appearance.Theme = next))
        {
            ToastService.Show("Motyw: " + ThemeLabel(next), ToastKind.Info);
        }
    }

    /// <summary>Komfortowa ↔ Kompaktowa, saved to <c>appearance.density</c>.</summary>
    public static void ToggleDensity()
    {
        var next = AppState.Config.Appearance.Density == DensityPreference.Compact ? DensityPreference.Comfortable : DensityPreference.Compact;
        if (Save(config => config.Appearance.Density = next))
        {
            ToastService.Show("Gęstość: " + (next == DensityPreference.Compact ? "kompaktowa" : "komfortowa"), ToastKind.Info);
        }
    }

    /// <summary>Ctrl+Shift+T, saved to <c>window.alwaysOnTop</c>.</summary>
    public static void ToggleAlwaysOnTop()
    {
        var next = !AppState.Config.Window.AlwaysOnTop;
        if (Save(config => config.Window.AlwaysOnTop = next))
        {
            ToastService.Show(next ? "Zawsze na wierzchu: włączone" : "Zawsze na wierzchu: wyłączone", ToastKind.Info);
        }
    }

    public static Task OpenDataFolderAsync() => Launcher.Default.OpenAsync(new Uri(FileUtil.DataDirectory));

    public static string ThemeLabel(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => "jasny",
        ThemePreference.Dark => "ciemny",
        _ => "systemowy"
    };

    private static bool Save(Action<OnionConfig> change)
    {
        try
        {
            var config = AppState.Config.Clone();
            change(config);
            AppState.SaveConfig(config);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ToastService.Show("Nie zapisano ustawienia: " + exception.Message, ToastKind.Error);
            return false;
        }
    }
}

/// <summary>Helpers for screens that load asynchronously.</summary>
public static class ScreenLoad
{
    /// <summary>Runs a refresh and waits until the screen is idle (a refresh already in progress is awaited too).</summary>
    public static async Task RefreshIdleAsync(Func<Task> refresh, Func<bool> isBusy)
    {
        await refresh();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (isBusy() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(MotionTokens.PollInterval);
        }
    }
}
