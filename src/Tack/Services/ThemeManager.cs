using System.Windows;
using Microsoft.Win32;

namespace Tack.Services;

/// <summary>
/// Light or dark: swaps the neutral palette (Themes/Dark.xaml or Themes/Light.xaml) that every
/// DynamicResource brush points at, so open windows restyle instantly.
/// </summary>
internal static class ThemeManager
{
    private static bool _applied;

    public static bool IsLight { get; private set; }

    /// <summary>Raised after the palette changes.</summary>
    public static event Action? Changed;

    /// <summary>Windows' "app mode" (Settings → Personalization → Colors).</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Applies "system", "light" or "dark". Returns true when the palette actually changed.</summary>
    public static bool Apply(string setting)
    {
        bool light = setting == "light" || (setting == "system" && SystemPrefersLight());
        if (_applied && light == IsLight) return false;

        var palettes = Application.Current.Resources.MergedDictionaries;
        palettes[0] = new ResourceDictionary
        {
            // Named by assembly, so it also loads when Tack runs inside another process (tests/Tack.Tests).
            Source = new Uri($"pack://application:,,,/Tack;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute),
        };
        IsLight = light;
        _applied = true;
        Changed?.Invoke();
        return true;
    }
}
