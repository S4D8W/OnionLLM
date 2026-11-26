namespace OnionLLM.Controls;

public partial class ToastHost : ContentView
{
    public ToastHost()
    {
        InitializeComponent();
        // Let clicks pass through the empty area around the stack.
        InputTransparent = true;
        CascadeInputTransparent = false;
    }
}
