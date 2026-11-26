namespace OnionLLM.Navigation;

/// <summary>List/table navigation keys (DESIGN.md §4.5.1, §9.6).</summary>
public enum KeyboardAction
{
    Up,
    Down,
    Home,
    End,
    Activate,
    Expand,
    Collapse,
    Previous,
    Next,
    Copy
}

/// <summary>A component that consumes list navigation keys (tables).</summary>
public interface IKeyboardTarget
{
    bool HandleKey(KeyboardAction action);
}

/// <summary>Remembers the last list the user interacted with; keys go there while it is on screen.</summary>
public static class KeyboardRouter
{
    private static WeakReference<IKeyboardTarget>? _target;

    public static IKeyboardTarget? Target
    {
        get => _target is not null && _target.TryGetTarget(out var target) && target is VisualElement { Window: not null, IsVisible: true }
            ? target
            : null;
        set => _target = value is null ? null : new WeakReference<IKeyboardTarget>(value);
    }

    public static bool Route(KeyboardAction action)
    {
        // J/K move between items of an open drawer even when no table is focused (§4.6.1).
        if (DrawerService.Current is { } drawer && action is KeyboardAction.Next or KeyboardAction.Previous)
        {
            var command = action == KeyboardAction.Next ? drawer.NextCommand : drawer.PreviousCommand;
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                return true;
            }
        }

        return Target?.HandleKey(action) == true;
    }
}

/// <summary>Content of the detail drawer (§4.6.1).</summary>
public sealed class DrawerRequest
{
    /// <summary>Type overline: „SESJA”, „OKRES”, „BLOK”… (written normally, rendered upper-case).</summary>
    public required string Overline { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public required View Content { get; init; }

    public View? Footer { get; init; }

    public System.Windows.Input.ICommand? PreviousCommand { get; init; }

    public System.Windows.Input.ICommand? NextCommand { get; init; }

    /// <summary>„Więcej” menu: Kopiuj ID, Kopiuj jako JSON, Otwórz folder projektu…</summary>
    public IList<Controls.MenuEntry> MoreEntries { get; init; } = [];

    /// <summary>Called after the drawer closes (the screen clears its selection and restores focus).</summary>
    public Action? Closed { get; init; }
}

/// <summary>Single detail drawer of the window; replaces every data <c>DisplayAlert</c> (§4.6.1).</summary>
public static class DrawerService
{
    public static event EventHandler? Changed;

    public static DrawerRequest? Current { get; private set; }

    public static bool IsOpen => Current is not null;

    /// <summary>Opens or replaces the drawer content (crossfade when already open).</summary>
    public static void Open(DrawerRequest request)
    {
        Current = request;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Close()
    {
        var closing = Current;
        if (closing is null)
        {
            return;
        }

        Current = null;
        Changed?.Invoke(null, EventArgs.Empty);
        closing.Closed?.Invoke();
    }
}

/// <summary>Walks the logical tree of a screen (global shortcuts look for its SearchField / export button).</summary>
public static class ElementTree
{
    public static IEnumerable<T> Find<T>(Element? root)
    {
        if (root is null)
        {
            yield break;
        }

        if (root is T match)
        {
            yield return match;
        }

        foreach (var child in ((IElementController)root).LogicalChildren)
        {
            foreach (var found in Find<T>(child))
            {
                yield return found;
            }
        }
    }

    /// <summary>First element of type <typeparamref name="T"/> that is visible together with all its ancestors.</summary>
    public static T? FirstVisible<T>(Element? root) where T : VisualElement =>
        Find<T>(root).FirstOrDefault(element => IsShown(element, root));

    public static bool IsShown(Element element, Element? root)
    {
        for (Element? current = element; current is not null; current = current.Parent)
        {
            if (current is VisualElement { IsVisible: false })
            {
                return false;
            }

            if (ReferenceEquals(current, root))
            {
                break;
            }
        }

        return true;
    }
}
