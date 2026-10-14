using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Tack.Interop;

namespace Tack.Services;

/// <summary>
/// Resolves the accent colour (the Windows accent by default, or a preset) and publishes it
/// as application resources so every DynamicResource reference updates live.
/// </summary>
internal static class AccentPalette
{
    public static readonly (string Name, string Value)[] Presets =
    {
        ("System", "system"),
        ("Sky", "#60CDFF"),
        ("Mint", "#5FD9A8"),
        ("Amber", "#FFC567"),
        ("Coral", "#FF8F7E"),
        ("Lilac", "#B9A4FF"),
    };

    public static Color Resolve(string setting)
    {
        if (!string.Equals(setting, "system", StringComparison.OrdinalIgnoreCase))
        {
            try { return (Color)ColorConverter.ConvertFromString(setting); }
            catch (FormatException) { /* fall through to system */ }
        }
        return SystemAccent();
    }

    /// <summary>The Windows accent tuned for dark surfaces (the "Light 2" shade Windows 11 itself uses).</summary>
    public static Color SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 8)
                return Color.FromRgb(p[4], p[5], p[6]);
        }
        catch { /* fall back below */ }

        if (NativeMethods.DwmGetColorizationColor(out uint argb, out _) == 0)
        {
            var c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return Lighten(c, 0.45);
        }
        return (Color)ColorConverter.ConvertFromString("#60CDFF");
    }

    public static void Apply(ResourceDictionary resources, Color accent)
    {
        resources["AccentColor"] = accent;
        resources["AccentBrush"] = Frozen(accent);
        resources["AccentSoftBrush"] = Frozen(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        resources["AccentFaintBrush"] = Frozen(Color.FromArgb(0x16, accent.R, accent.G, accent.B));
        resources["AccentBorderBrush"] = Frozen(Color.FromArgb(0x8C, accent.R, accent.G, accent.B));
        resources["OnAccentBrush"] = Frozen(Luminance(accent) > 0.45 ? Color.FromRgb(0x10, 0x12, 0x16) : Colors.White);
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
