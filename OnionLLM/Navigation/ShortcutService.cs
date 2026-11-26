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
    Escape,
    Palette,
    Shortcuts,
    Search,
    Export,
    ToggleTheme,
    AlwaysOnTop,
    MiniView
}

/// <summary>Keys handled by the open command palette.</summary>
public enum PaletteKey
{
    Up,
    Down,
    PageUp,
    PageDown,
    Enter,
    Escape,
    Tab
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
    public const string BackLabel = "⌘[";
    public const string ForwardLabel = "⌘]";
    private const string Modifier = "⌘";
#else
    public const string SettingsShortcutLabel = "Ctrl+,";
    public const string BackLabel = "Alt+←";
    public const string ForwardLabel = "Alt+→";
    private const string Modifier = "Ctrl";
#endif

    public static event EventHandler<ShortcutAction>? Invoked;

    /// <summary>Keys for the open command palette (raised instead of shortcuts while it is open).</summary>
    public static event EventHandler<PaletteKey>? PaletteKeyPressed;

    /// <summary>Label for tooltips: „Ctrl+3” / „⌘3”, „Ctrl+Shift+L” / „⇧⌘L”.</summary>
    public static string Label(string key) => Modifier == "Ctrl" ? $"Ctrl+{key}" : (key.StartsWith("Shift+", StringComparison.Ordinal) ? "⇧⌘" + key[6..] : $"⌘{key}");

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

    /// <summary>Remembers the focused control before a modal overlay opens (palette), to restore it on close (§9.2).</summary>
    public static void SaveFocus()
    {
#if WINDOWS
        _savedFocus = _xamlRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(_xamlRoot) as Microsoft.UI.Xaml.Controls.Control;
#endif
    }

    public static void RestoreFocus()
    {
#if WINDOWS
        _savedFocus?.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        _savedFocus = null;
#endif
    }

#if WINDOWS
    private static Microsoft.UI.Xaml.XamlRoot? _xamlRoot;
    private static Microsoft.UI.Xaml.Controls.Control? _savedFocus;

    private static void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.UIElement { XamlRoot: { } xamlRoot })
        {
            _xamlRoot = xamlRoot;
        }

        var ctrl = IsDown(Windows.System.VirtualKey.Control);
        var alt = IsDown(Windows.System.VirtualKey.Menu);
        var shift = IsDown(Windows.System.VirtualKey.Shift);
        var inText = sender is Microsoft.UI.Xaml.UIElement element && IsTextInputFocused(element);
        var key = (int)e.Key;

        // Open command palette: arrows, Enter, Esc and Tab belong to it (focus trap, §9.2); typing goes to its field.
        if (CommandPaletteService.IsOpen)
        {
            PaletteKey? paletteKey = e.Key switch
            {
                Windows.System.VirtualKey.Up => PaletteKey.Up,
                Windows.System.VirtualKey.Down => PaletteKey.Down,
                Windows.System.VirtualKey.PageUp => PaletteKey.PageUp,
                Windows.System.VirtualKey.PageDown => PaletteKey.PageDown,
                Windows.System.VirtualKey.Enter => PaletteKey.Enter,
                Windows.System.VirtualKey.Escape => PaletteKey.Escape,
                Windows.System.VirtualKey.Tab => PaletteKey.Tab,
                Windows.System.VirtualKey.K when ctrl && !alt => PaletteKey.Escape,
                _ => null
            };
            if (paletteKey is { } pressed)
            {
                PaletteKeyPressed?.Invoke(null, pressed);
                e.Handled = true;
            }

            return;
        }

        ShortcutAction? action = null;
        var allowedInText = false;
        if (ctrl && !alt && !shift && e.Key == Windows.System.VirtualKey.K)
        {
            action = new ShortcutAction(ShortcutCommand.Palette);
            allowedInText = true;
        }
        else if (ctrl && !alt && !shift && e.Key == Windows.System.VirtualKey.F)
        {
            action = new ShortcutAction(ShortcutCommand.Search);
            allowedInText = true;
        }
        else if (!ctrl && !alt && !shift && (key == VkOemSlash || e.Key == Windows.System.VirtualKey.Divide))
        {
            action = new ShortcutAction(ShortcutCommand.Search);
        }
        else if (!ctrl && !alt && shift && key == VkOemSlash)
        {
            action = new ShortcutAction(ShortcutCommand.Shortcuts);
        }
        else if (ctrl && !alt && !shift && e.Key == Windows.System.VirtualKey.E)
        {
            action = new ShortcutAction(ShortcutCommand.Export);
        }
        else if (ctrl && !alt && shift && e.Key == Windows.System.VirtualKey.L)
        {
            action = new ShortcutAction(ShortcutCommand.ToggleTheme);
        }
        else if (ctrl && !alt && shift && e.Key == Windows.System.VirtualKey.T)
        {
            action = new ShortcutAction(ShortcutCommand.AlwaysOnTop);
        }
        else if (ctrl && !alt && shift && e.Key == Windows.System.VirtualKey.M)
        {
            action = new ShortcutAction(ShortcutCommand.MiniView);
            allowedInText = true;
        }
        else if (ctrl && !alt && !shift && key is >= (int)Windows.System.VirtualKey.Number1 and <= (int)Windows.System.VirtualKey.Number8)
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

        // Alt+↑ / Alt+↓ reorder (source cards): routed even when the card's „Więcej” button has focus.
        if (action is null && !inText && alt && !ctrl && e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            if (KeyboardRouter.Route(e.Key == Windows.System.VirtualKey.Up ? KeyboardAction.MoveUp : KeyboardAction.MoveDown))
            {
                e.Handled = true;
            }

            return;
        }

        if (action is null && !inText && sender is Microsoft.UI.Xaml.UIElement origin && IsNavigationKeyFree(origin))
        {
            // List navigation (DESIGN.md 4.5.1): arrows, Home/End, Enter, J/K, Ctrl+C go to the active table or drawer.
            KeyboardAction? list = e.Key switch
            {
                Windows.System.VirtualKey.Up when !ctrl && !alt => KeyboardAction.Up,
                Windows.System.VirtualKey.Down when !ctrl && !alt => KeyboardAction.Down,
                Windows.System.VirtualKey.Home when !ctrl && !alt => KeyboardAction.Home,
                Windows.System.VirtualKey.End when !ctrl && !alt => KeyboardAction.End,
                Windows.System.VirtualKey.Enter when !ctrl && !alt => KeyboardAction.Activate,
                Windows.System.VirtualKey.Right when !ctrl && !alt => KeyboardAction.Expand,
                Windows.System.VirtualKey.Left when !ctrl && !alt => KeyboardAction.Collapse,
                Windows.System.VirtualKey.J when !ctrl && !alt => KeyboardAction.Next,
                Windows.System.VirtualKey.K when !ctrl && !alt => KeyboardAction.Previous,
                Windows.System.VirtualKey.C when ctrl && !alt && !shift => KeyboardAction.Copy,
                _ => null
            };
            if (list is { } listAction && KeyboardRouter.Route(listAction))
            {
                e.Handled = true;
            }

            return;
        }

        if (action is null || (inText && !allowedInText))
        {
            return;
        }

        Raise(action.Value);
        // Esc stays unhandled so text fields can still clear themselves.
        e.Handled = action.Value.Command != ShortcutCommand.Escape;
    }

    private const int VkOemSlash = 191;

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Focus is not on a control that uses arrows or Enter itself (buttons, toggles, pickers, text).</summary>
    private static bool IsNavigationKeyFree(Microsoft.UI.Xaml.UIElement element) =>
        element.XamlRoot is not { } root ||
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not
            (Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.ToggleSwitch or
            Microsoft.UI.Xaml.Controls.ComboBox or Microsoft.UI.Xaml.Controls.Slider or Microsoft.UI.Xaml.Controls.CalendarView or
            Microsoft.UI.Xaml.Controls.CalendarDatePicker or Microsoft.UI.Xaml.Controls.DatePicker);

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
        view.Add(Accelerated("Paleta poleceń", "K", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Palette));
        view.Add(Accelerated("Szukaj", "F", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Search));
        view.Add(Accelerated("Eksportuj", "E", KeyboardAcceleratorModifiers.Cmd, ShortcutCommand.Export));
        view.Add(Accelerated("Przełącz motyw", "L", KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Shift, ShortcutCommand.ToggleTheme));
        view.Add(Accelerated("Zawsze na wierzchu", "T", KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Shift, ShortcutCommand.AlwaysOnTop));
        view.Add(Accelerated("Skróty klawiszowe", "/", KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Shift, ShortcutCommand.Shortcuts));
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
