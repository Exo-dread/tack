using System.IO;
using Microsoft.Win32;

namespace Tack.Services;

/// <summary>
/// "Launch at Windows startup" via the per-user Run key. No admin rights, no scheduled tasks. The value is shared by every
/// copy of Tack on the PC (an installed one, a portable one, a test copy): a copy with the option off removes it only when it
/// names that copy (or a Tack.exe that's no longer there), so it never stops another copy starting with Windows.
/// </summary>
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
            else if (MayRemove(key.GetValue(ValueName) as string, Environment.ProcessPath)) key.DeleteValue(ValueName, throwOnMissingValue: false);
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

    /// <summary>
    /// The Run value <paramref name="value"/> may be removed by the copy at <paramref name="exe"/>: it names that copy (in
    /// any case), or a Tack.exe that isn't there any more. Another copy's value, or one that names nothing, is left alone.
    /// </summary>
    internal static bool MayRemove(string? value, string? exe)
    {
        if (ExeOf(value) is not { } named || string.IsNullOrEmpty(exe)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(named), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) || !File.Exists(named);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The program a Run value starts: the quoted path, or (unquoted) everything up to ".exe".</summary>
    private static string? ExeOf(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value[0] == '"')
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        int exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? value[..(exe + 4)] : null;
    }
}
