using OnionLLM.Core.Config;

namespace OnionLLM.Design;

/// <summary>
/// Animation durations (DESIGN.md §2.7.1), already adjusted to the active motion mode (§2.7.4):
/// <c>Full</c> — full catalogue (unless the OS asks to reduce motion), <c>Reduced</c> — fades only, <c>Off</c> — all zero.
/// Code never checks the mode itself for durations; it checks <see cref="AllowsTransforms"/> / <see cref="AllowsLoops"/>
/// only to pick between movement and a plain fade.
/// </summary>
public static class MotionTokens
{
    private const uint FastMs = 120;
    private const uint BaseMs = 200;
    private const uint SlowMs = 280;
    private const uint SlowerMs = 480;
    private const uint CounterMs = 600;
    private const uint AmbientMs = 2400;
    private const uint StaggerMs = 30;
    private const uint ShimmerMs = 1400;
    private const uint SpinMs = 800;

    public static event EventHandler? Changed;

    /// <summary>Search filtering debounce (§4.2.2).</summary>
    public static TimeSpan SearchDebounce { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Text-field autosave delay after the last keystroke (§5.10).</summary>
    public static TimeSpan AutosaveDelay { get; } = TimeSpan.FromMilliseconds(600);

    /// <summary>How long „Zapisano” / the copy check mark stays visible (§5.10, §5.12).</summary>
    public static TimeSpan FeedbackVisible { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Toast auto-close (§4.6.3).</summary>
    public static TimeSpan ToastVisible { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Import progress updates reach the UI at most this often (§7.5).</summary>
    public static TimeSpan ProgressInterval { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Polling step while waiting for a screen to finish loading (command palette drill-down).</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Mode after combining the user setting with the OS reduce-motion preference.</summary>
    public static MotionPreference EffectiveMode { get; private set; } = MotionPreference.Full;

    public static bool AllowsTransforms => EffectiveMode == MotionPreference.Full;

    public static bool AllowsLoops => EffectiveMode == MotionPreference.Full;

    public static uint Instant => 0;

    public static uint Fast => Scale(FastMs);

    public static uint Base => Scale(BaseMs);

    public static uint Slow => Scale(SlowMs);

    /// <summary>Chart entry and rings; in reduced mode a short fade (M4/M5 → fade 120 ms).</summary>
    public static uint Slower => EffectiveMode == MotionPreference.Reduced ? FastMs : Scale(SlowerMs);

    /// <summary>Count-up (M3); reduced mode changes the number instantly.</summary>
    public static uint Counter => AllowsTransforms ? CounterMs : 0;

    public static uint Ambient => AllowsLoops ? AmbientMs : 0;

    public static uint Stagger => AllowsTransforms ? StaggerMs : 0;

    public static uint Shimmer => AllowsLoops ? ShimmerMs : 0;

    public static uint Spin => EffectiveMode == MotionPreference.Off ? 0 : SpinMs;

    public static void Apply(MotionPreference preference)
    {
        var effective = preference == MotionPreference.Full && SystemPrefersReducedMotion()
            ? MotionPreference.Reduced
            : preference;
        if (effective == EffectiveMode)
        {
            return;
        }

        EffectiveMode = effective;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static uint Scale(uint milliseconds) => EffectiveMode == MotionPreference.Off ? 0 : milliseconds;

    private static bool SystemPrefersReducedMotion()
    {
        try
        {
#if WINDOWS
            return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
#elif MACCATALYST || IOS
            return UIKit.UIAccessibility.IsReduceMotionEnabled;
#else
            return false;
#endif
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
