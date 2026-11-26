namespace OnionLLM.Navigation;

/// <summary>Global commands reachable by keyboard (DESIGN.md §9.6).</summary>
public enum ShortcutCommand
{
    Navigate,
    Settings,
    Refresh,
    ToggleSidebar,
    Back,
    Forward,
    Escape
}

/// <summary>A resolved shortcut: command + optional screen (for Ctrl+1…8).</summary>
public readonly record struct ShortcutAction(ShortcutCommand Command, Screen? Screen = null);

/// <summary>
/// Keyboard shortcuts (§9.6). Windows: <c>PreviewKeyDown</c> on the WinUI window root; macOS: menu bar items with
/// keyboard accelerators (built by <see cref="BuildMenuBar"/>). Shortcuts are ignored while a text field has focus,
/// except Ctrl+K, Esc, F5 and Ctrl+1–8.
/// </summary>
public static class ShortcutService
{
#if MACCATALYST || IOS
    public const string SettingsShortcutLabel = "⌘,";
    private const string Modifier = "⌘";
#else
    public const string SettingsShortcutLabel = "Ctrl+,";
    private const string Modifier = "Ctrl";
#endif

    public static event EventHandler<ShortcutAction>? Invoked;

    /// <summary>Label for tooltips: „Ctrl+3” / „⌘3”.</summary>
    public static string Label(string key) => Modifier == "Ctrl" ? $"Ctrl+{key}" : $"⌘{key}";

    public static void Raise(ShortcutAction action) => Invoked?.Invoke(null, action);

    public static void Attach(Window window)
    {
#if WINDOWS
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native || native.Content is not Microsoft.UI.Xaml.UIElement root)
        {
            return;
        }

        root.PreviewKeyDown -= OnPreviewKeyDown;
        root.PreviewKeyDown += OnPreviewKeyDown;
#endif
    }

#if WINDOWS
    private static void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(Windows.System.VirtualKey.Control);
        var alt = IsDown(Windows.System.VirtualKey.Menu);
        var shift = IsDown(Windows.System.VirtualKey.Shift);
        var inText = sender is Microsoft.UI.Xaml.UIElement element && IsTextInputFocused(element);
        var key = (int)e.Key;

        ShortcutAction? action = null;
        var allowedInText = false;
        if (ctrl && !alt && !shift && key is >= (int)Windows.System.VirtualKey.Number1 and <= (int)Windows.System.VirtualKey.Number8)
        {
            action = new ShortcutAction(ShortcutCommand.Navigate, (Screen)(key - (int)Windows.System.VirtualKey.Number1));
            allowedInText = true;
        }
        else if (ctrl && !alt && !shift && key is >= (int)Windows.System.VirtualKey.NumberPad1 and <= (int)Windows.System.VirtualKey.NumberPad8)
        {
            action = new ShortcutAction(ShortcutCommand.Navigate, (Screen)(key - (int)Windows.System.VirtualKey.NumberPad1));
            allowedInText = true;
        }
        else if (ctrl && !alt && key == 188) // VK_OEM_COMMA
        {
            action = new ShortcutAction(ShortcutCommand.Settings);
        }
        else if (e.Key == Windows.System.VirtualKey.F5 || (ctrl && !alt && e.Key == Windows.System.VirtualKey.R))
        {
            action = new ShortcutAction(ShortcutCommand.Refresh);
            allowedInText = e.Key == Windows.System.VirtualKey.F5;
        }
        else if (ctrl && !alt && !shift && e.Key == Windows.System.VirtualKey.B)
        {
            action = new ShortcutAction(ShortcutCommand.ToggleSidebar);
        }
        else if (alt && !ctrl && e.Key == Windows.System.VirtualKey.Left)
        {
            action = new ShortcutAction(ShortcutCommand.Back);
        }
        else if (alt && !ctrl && e.Key == Windows.System.VirtualKey.Right)
        {
            action = new ShortcutAction(ShortcutCommand.Forward);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            action = new ShortcutAction(ShortcutCommand.Escape);
            allowedInText = true;
        }

        if (action is null || (inText && !allowedInText))
        {
            return;
        }

        Raise(action.Value);
        // Esc stays unhandled so text fields can still clear themselves.
        e.Handled = action.Value.Command != ShortcutCommand.Escape;
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsTextInputFocused(Microsoft.UI.Xaml.UIElement element) =>
        element.XamlRoot is { } root &&
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is
            Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.RichEditBox or
            Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.AutoSuggestBox;
#endif

    /// <summary>
    /// macOS: shortcuts as menu bar commands with ⌘ accelerators (the native, discoverable place on the platform).
    /// On Windows the page keeps no menu bar — keys are handled in <see cref="Attach"/>.
    /// </summary>
    public static void BuildMenuBar(Page page, Func<Screen, bool> isAvailable)
    {
#if MACCATALYST
        var view = new MenuBarItem { Text = "Widok" };
        foreach (var info in ScreenInfo.All.Where(info => info.Screen != Screen.Settings && isAvailable(info.Screen)))
        {
            var item = new MenuFlyoutItem
            {
                Text = info.Label,
                Command = new Command(() => Raise(new ShortcutAction(ShortcutCommand.Navigate, info.Screen)))
            };
            item.KeyboardAccelerators.Add(new KeyboardAccelerator
            {
                Modifiers = KeyboardAcceleratorModifiers.Cmd,
                Key = ((int)info.Screen + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            view.Add(item);
        }

        view.Add(Accelerated("Ustawienia", ",", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Settings));
        view.Add(Accelerated("Odśwież", "R", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Refresh));
        view.Add(Accelerated("Zwiń panel", "B", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.ToggleSidebar));
        view.Add(Accelerated("Wstecz", "[", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Back));
        view.Add(Accelerated("Dalej", "]", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Forward));
        page.MenuBarItems.Clear();
        page.MenuBarItems.Add(view);

        static MenuFlyoutItem Accelerated(string text, string key, KeyboardAcceleratorModifiers modifiers, ShortcutCommand command)
        {
            var item = new MenuFlyoutItem { Text = text, Command = new Command(() => Raise(new ShortcutAction(command))) };
            item.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = modifiers, Key = key });
            return item;
        }
#endif
    }
}
