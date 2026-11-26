using System.Collections.ObjectModel;
using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Navigation;

public enum ToastKind
{
    Success,
    Info,
    Error
}

/// <summary>One toast (DESIGN.md §4.6.3).</summary>
public sealed class ToastItem
{
    public required string Message { get; init; }

    public ToastKind Kind { get; init; }

    public string? ActionText { get; init; }

    public Command? ActionCommand { get; init; }

    public required Command CloseCommand { get; init; }

    public bool HasAction => ActionText is not null;

    public string Glyph => Kind switch
    {
        ToastKind.Success => Icons.CircleCheck,
        ToastKind.Error => Icons.CircleAlert,
        _ => Icons.Info
    };

    /// <summary>Lighter status colour readable on <c>surface.inverse</c> (dark-theme status tokens are designed for it).</summary>
    public string GlyphColorKey => Kind switch
    {
        ToastKind.Success => "ColorStatusSuccess",
        ToastKind.Error => "ColorStatusDanger",
        _ => "ColorStatusInfo"
    };
}

/// <summary>
/// Toast queue (§4.6.3): max 3 (newest at the bottom), auto-close after 5 s, errors stay until closed;
/// announced to screen readers.
/// </summary>
public static class ToastService
{
    private const int MaxToasts = 3;

    public static ObservableCollection<ToastItem> Items { get; } = [];

    public static void Show(string message, ToastKind kind = ToastKind.Success, string? actionText = null, Action? action = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ToastItem? item = null;
            item = new ToastItem
            {
                Message = message,
                Kind = kind,
                ActionText = actionText,
                ActionCommand = action is null ? null : new Command(() => { action(); Close(item!); }),
                CloseCommand = new Command(() => Close(item!))
            };
            Items.Add(item);
            while (Items.Count > MaxToasts)
            {
                Items.RemoveAt(0);
            }

            SemanticScreenReader.Announce(message);
            if (kind != ToastKind.Error)
            {
                _ = CloseLaterAsync(item);
            }
        });
    }

    private static async Task CloseLaterAsync(ToastItem item)
    {
        await Task.Delay(Design.MotionTokens.ToastVisible);
        Close(item);
    }

    private static void Close(ToastItem item) => MainThread.BeginInvokeOnMainThread(() => Items.Remove(item));
}

/// <summary>Confirmation request shown by the dialog host (§4.6.2).</summary>
public sealed class DialogRequest : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new();

    public DialogRequest()
    {
        ConfirmCommand = new Command(() => Complete(true));
        CancelCommand = new Command(() => Complete(false));
    }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public required string ConfirmText { get; init; }

    public string CancelText { get; init; } = "Anuluj";

    /// <summary>Danger action: Enter does not confirm (needs Tab + Enter or a click).</summary>
    public bool IsDanger { get; init; }

    public Command ConfirmCommand { get; }

    public Command CancelCommand { get; }

    public Task<bool> Result => _result.Task;

    public void Complete(bool confirmed)
    {
        if (_result.TrySetResult(confirmed))
        {
            DialogService.Close(this);
        }
    }
}

/// <summary>Modal confirmation dialogs (§4.6.2) — only for irreversible or costly actions, never for data.</summary>
public static class DialogService
{
    public static event EventHandler? Changed;

    public static DialogRequest? Current { get; private set; }

    public static Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        Current?.Complete(false);
        var request = new DialogRequest { Title = title, Message = message, ConfirmText = confirmText, IsDanger = danger };
        Current = request;
        Changed?.Invoke(null, EventArgs.Empty);
        return request.Result;
    }

    internal static void Close(DialogRequest request)
    {
        if (ReferenceEquals(Current, request))
        {
            Current = null;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
