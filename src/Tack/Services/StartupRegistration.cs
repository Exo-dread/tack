using Microsoft.Win32;

namespace Tack.Services;

/// <summary>"Launch at Windows startup" via the per-user Run key. No admin rights, no scheduled tasks.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tack";

    private static string Command => $"\"{Environment.ProcessPath}\" --startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup registration", ex);
        }
    }

    /// <summary>Keeps the registered path current if the user moved Tack.exe.</summary>
    public static void Refresh(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        string? current = key?.GetValue(ValueName) as string;
        if (enabled && current != Command) Set(true);
        else if (!enabled && current != null) Set(false);
    }
}
