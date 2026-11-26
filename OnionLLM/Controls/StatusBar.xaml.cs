using System.ComponentModel;
using OnionLLM.Design;
using OnionLLM.Navigation;

namespace OnionLLM.Controls;

public partial class StatusBar : ContentView
{
    private bool _spinning;

    public StatusBar()
    {
        InitializeComponent();
        BindingContext = ShellViewModel.Instance;
        ShellViewModel.Instance.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsRefreshing) && ShellViewModel.Instance.IsRefreshing)
        {
            _ = SpinAsync();
        }
    }

    /// <summary>M15: the sync icon turns while syncing and stops at a full turn.</summary>
    private async Task SpinAsync()
    {
        if (_spinning || MotionTokens.Spin == 0)
        {
            return;
        }

        _spinning = true;
        try
        {
            while (ShellViewModel.Instance.IsRefreshing && MotionTokens.Spin > 0 && Window is not null)
            {
                SyncIcon.Rotation = 0;
                await SyncIcon.RotateToAsync(360, MotionTokens.Spin, Easing.Linear);
            }
        }
        finally
        {
            SyncIcon.Rotation = 0;
            _spinning = false;
        }
    }

    /// <summary>„Błąd aktualizacji — Szczegóły”: shows the error text (ErrorState details come with the screens).</summary>
    private void OnSyncTapped(object? sender, TappedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ShellViewModel.Instance.SyncTooltip))
        {
            ToastService.Show(ShellViewModel.Instance.SyncTooltip, ToastKind.Error);
        }
    }
}
