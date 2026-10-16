using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Tack.Interop;

namespace Tack.Services;

/// <summary>
/// Resolves the accent colour (the Windows accent by default, or a preset) and publishes it
/// as application resources so every DynamicResource reference updates live.
/// Each accent has two shades, like Windows 11 itself: a bright one for dark surfaces and a
/// deeper one for light surfaces.
/// </summary>
internal static class AccentPalette
{
    /// <summary>Value is what's stored in settings (the dark-theme shade); Light is its light-theme twin.</summary>
    public static readonly (string Name, string Value, string Light)[] Presets =
    {
        ("System", "system", "system"),
        ("Sky", "#60CDFF", "#005FB8"),
        ("Mint", "#5FD9A8", "#0E7A55"),
        ("Amber", "#FFC567", "#9A5B00"),
        ("Coral", "#FF8F7E", "#C0392B"),
        ("Lilac", "#B9A4FF", "#6B4FCF"),
    };

    public static Color Resolve(string setting, bool light)
    {
        if (string.Equals(setting, "system", StringComparison.OrdinalIgnoreCase))
            return SystemAccent(light);

        foreach (var preset in Presets)
            if (string.Equals(preset.Value, setting, StringComparison.OrdinalIgnoreCase))
                return Parse(light ? preset.Light : preset.Value) ?? SystemAccent(light);

        // A hand-edited custom colour: use it as given on dark, deepened on light.
        return Parse(setting) is { } custom ? (light ? Mix(custom, Colors.Black, 0.35) : custom) : SystemAccent(light);
    }

    /// <summary>
    /// The Windows accent in the shade Windows 11 uses for the theme:
    /// "Light 2" on dark surfaces, "Dark 1" on light ones.
    /// </summary>
    public static Color SystemAccent(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 20)
                return light ? Color.FromRgb(p[16], p[17], p[18]) : Color.FromRgb(p[4], p[5], p[6]);
        }
        catch { /* fall back below */ }

        if (NativeMethods.DwmGetColorizationColor(out uint argb, out _) == 0)
        {
            var c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return light ? Mix(c, Colors.Black, 0.15) : Mix(c, Colors.White, 0.45);
        }
        return Parse(light ? "#005FB8" : "#60CDFF")!.Value;
    }

    public static void Apply(ResourceDictionary resources, Color accent)
    {
        resources["AccentColor"] = accent;
        resources["AccentBrush"] = Frozen(accent);
        resources["AccentSoftBrush"] = Frozen(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        resources["AccentFaintBrush"] = Frozen(Color.FromArgb(0x16, accent.R, accent.G, accent.B));
        resources["AccentBorderBrush"] = Frozen(Color.FromArgb(0x8C, accent.R, accent.G, accent.B));
        resources["OnAccentBrush"] = Frozen(PrefersWhiteText(accent) ? Colors.White : Color.FromRgb(0x10, 0x12, 0x16));
    }

    /// <summary>
    /// White ticks and text on the accent when they reach 3:1 contrast (as Windows does on its
    /// deeper light-theme accents); near-black on the bright dark-theme ones.
    /// </summary>
    public static bool PrefersWhiteText(Color background) => 1.05 / (RelativeLuminance(background) + 0.05) >= 3.0;

    private static Color? Parse(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (FormatException) { return null; }
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static Color Mix(Color c, Color with, double amount) => Color.FromRgb(
        (byte)(c.R + (with.R - c.R) * amount),
        (byte)(c.G + (with.G - c.G) * amount),
        (byte)(c.B + (with.B - c.B) * amount));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
