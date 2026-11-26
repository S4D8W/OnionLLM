using OnionLLM.Design;
using OnionLLM.Navigation;

namespace OnionLLM.Controls;

public partial class DialogHost : ContentView
{
    public DialogHost()
    {
        InitializeComponent();
        DialogService.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await UpdateAsync());
    }

    private async Task UpdateAsync()
    {
        var request = DialogService.Current;
        if (request is null)
        {
            IsVisible = false;
            return;
        }

        BindingContext = request;
        // §4.6.2: confirmations 440, short forms 560.
        Card.WidthRequest = DesignTokens.Double(request.Body is null ? "SizeDialogSm" : "SizeDialogMd");
        ConfirmButton.Style = (Style)Application.Current!.Resources[request.IsDanger ? "Button.Danger" : "Button.Primary"];
        IsVisible = true;
        SemanticScreenReader.Announce(request.Title);

        // M7: scale 0.96 → 1 + fade (base, enter); reduced motion → fade only.
        Card.Opacity = 0;
        Card.Scale = MotionTokens.AllowsTransforms ? DesignTokens.Double("ScaleDialogEnter", 1) : 1;
        await Task.WhenAll(
            Card.FadeToAsync(1, MotionTokens.Base, OnionEasing.Enter),
            Card.ScaleToAsync(1, MotionTokens.Base, OnionEasing.Enter));

        // Danger actions must not be confirmed by a stray Enter: focus „Anuluj”.
        (request.IsDanger ? CancelButton : ConfirmButton).Focus();
    }
}
