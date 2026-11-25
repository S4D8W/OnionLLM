namespace OnionLLM
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            SizeChanged += (_, _) =>
            {
                FlyoutBehavior = Width >= 900
                    ? FlyoutBehavior.Locked
                    : FlyoutBehavior.Flyout;
            };
        }
    }
}
