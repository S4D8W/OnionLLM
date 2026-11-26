using OnionLLM.Navigation;

namespace OnionLLM.Controls;

public partial class Sidebar : ContentView
{
    public Sidebar()
    {
        InitializeComponent();
        BindingContext = ShellViewModel.Instance;
    }
}
