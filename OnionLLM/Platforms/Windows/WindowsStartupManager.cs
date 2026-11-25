#if WINDOWS
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace OnionLLM;

internal static class WindowsStartupManager
{
    private const string TaskId = "OnionLLMStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OnionLLM";

    public static async Task<bool> IsEnabledAsync()
    {
        if (!IsPackaged())
        {
            return IsRegisteredInRunKey();
        }

        var task = await StartupTask.GetAsync(TaskId);
        return IsEnabledState(task.State);
    }

    public static async Task<(bool Enabled, string Status)> SetEnabledAsync(bool enabled)
    {
        if (!IsPackaged())
        {
            SetRunKey(enabled);
            return (enabled, enabled
                ? "Aplikacja będzie uruchamiana przy logowaniu do systemu."
                : "Autostart aplikacji został wyłączony.");
        }

        var task = await StartupTask.GetAsync(TaskId);
        if (enabled)
        {
            if (IsEnabledState(task.State))
            {
                return (true, "Aplikacja jest już uruchamiana przy logowaniu do systemu.");
            }

            var state = await task.RequestEnableAsync();
            var isEnabled = IsEnabledState(state);
            return (isEnabled, isEnabled
                ? "Aplikacja będzie uruchamiana przy logowaniu do systemu."
                : "Windows nie zezwolił na uruchamianie aplikacji przy starcie.");
        }

        if (task.State == StartupTaskState.EnabledByPolicy)
        {
            return (true, "Autostart jest wymuszony przez zasady systemowe i nie może zostać wyłączony tutaj.");
        }

        if (task.State == StartupTaskState.Enabled)
        {
            task.Disable();
        }

        return (false, "Autostart aplikacji został wyłączony.");
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current.Id;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsEnabledState(StartupTaskState state) =>
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private static bool IsRegisteredInRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(RunValue) is string;
    }

    private static void SetRunKey(bool enabled)
    {
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Nie można ustalić ścieżki aplikacji.");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey)
                ?? throw new IOException("Nie można otworzyć klucza autostartu użytkownika.");
            key.SetValue(RunValue, $"\"{executable}\"");
            return;
        }

        using var existingKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        existingKey?.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
#endif
