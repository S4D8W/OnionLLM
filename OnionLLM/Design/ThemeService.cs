using OnionLLM.Core.Config;
using OnionLLM.Resources.Styles;

namespace OnionLLM.Design;

/// <summary>Indices of the swappable dictionaries in <c>App.xaml</c> (DESIGN.md §3.2).</summary>
internal static class DesignResources
{
    public const int Theme = 2;
    public const int Sources = 3;
    public const int Density = 4;

    public static void Replace(int index, ResourceDictionary dictionary)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null)
        {
            return;
        }

        var current = merged.ToList();
        if (index >= current.Count || current[index].GetType() == dictionary.GetType())
        {
            return;
        }

        if (merged is IList<ResourceDictionary> list)
        {
            list[index] = dictionary;
            return;
        }

        current[index] = dictionary;
        merged.Clear();
        foreach (var item in current)
        {
            merged.Add(item);
        }
    }
}

/// <summary>
/// Light/dark/system theme (DESIGN.md §3.4): swaps <c>Tokens.Light/Dark</c> and <c>Tokens.Sources.Light/Dark</c>,
/// sets <see cref="Application.UserAppTheme"/> for native controls and follows the OS while in “Systemowy”.
/// </summary>
public static class ThemeService
{
    private static ThemePreference _preference = ThemePreference.System;
    private static bool _listening;

    /// <summary>Raised after the token dictionaries changed — charts call <c>Invalidate()</c>.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>M17: the window wraps the dictionary swap in a crossfade (set by the main window host).</summary>
    public static Func<Action, Task>? Transition { get; set; }

    public static AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;

    public static void Apply(ThemePreference preference)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        _preference = preference;
        if (!_listening)
        {
            application.RequestedThemeChanged += (_, _) =>
            {
                if (_preference == ThemePreference.System)
                {
                    Update();
                }
            };
            _listening = true;
        }

        application.UserAppTheme = preference switch
        {
            ThemePreference.Light => AppTheme.Light,
            ThemePreference.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
        Update();
    }

    /// <summary>Cycles Systemowy → Jasny → Ciemny (Ctrl+Shift+L, §9.6).</summary>
    public static ThemePreference Next(ThemePreference current) => current switch
    {
        ThemePreference.System => ThemePreference.Light,
        ThemePreference.Light => ThemePreference.Dark,
        _ => ThemePreference.System
    };

    private static void Update()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var theme = _preference switch
        {
            ThemePreference.Light => AppTheme.Light,
            ThemePreference.Dark => AppTheme.Dark,
            _ => application.PlatformAppTheme == AppTheme.Dark ? AppTheme.Dark : AppTheme.Light
        };
        var changed = theme != EffectiveTheme;
        EffectiveTheme = theme;
        if (changed && Transition is { } transition)
        {
            _ = transition(() => Swap(theme, changed));
            return;
        }

        Swap(theme, changed);
    }

    private static void Swap(AppTheme theme, bool changed)
    {
        if (theme == AppTheme.Dark)
        {
            DesignResources.Replace(DesignResources.Theme, new TokensDark());
            DesignResources.Replace(DesignResources.Sources, new TokensSourcesDark());
        }
        else
        {
            DesignResources.Replace(DesignResources.Theme, new TokensLight());
            DesignResources.Replace(DesignResources.Sources, new TokensSourcesLight());
        }

        if (changed)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}

/// <summary>Comfortable/compact density (DESIGN.md §2.8): swaps <c>Tokens.Comfortable/Compact</c> at runtime.</summary>
public static class DensityService
{
    public static event EventHandler? DensityChanged;

    public static DensityPreference Current { get; private set; } = DensityPreference.Comfortable;

    public static void Apply(DensityPreference density)
    {
        DesignResources.Replace(DesignResources.Density,
            density == DensityPreference.Compact ? new TokensCompact() : new TokensComfortable());
        if (density != Current)
        {
            Current = density;
            DensityChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}

/// <summary>Applies the <c>appearance</c> settings from the config (start-up and every config change).</summary>
public static class AppearanceService
{
    public static void Apply(AppearanceSettings appearance)
    {
        ThemeService.Apply(appearance.Theme);
        DensityService.Apply(appearance.Density);
        MotionTokens.Apply(appearance.Motion);
    }
}
