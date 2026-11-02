using System.Windows;
using Microsoft.Win32;

namespace Tack.Services;

/// <summary>
/// Light or dark, in one of a few colour themes: swaps the neutral palette (Themes/Dark.xaml or
/// Themes/Light.xaml, with the theme's own surfaces laid over it) that every DynamicResource brush
/// points at, so open windows restyle instantly. A theme is colours only: no pictures, fonts or animation.
/// </summary>
internal static class ThemeManager
{
    /// <summary>The colour themes, Classic first (the look Tack has always had). Ids are what tack.json keeps.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Palettes =
    [
        ("classic", "Classic"),
        ("midnight", "Midnight"),
        ("plum", "Plum"),
        ("moss", "Moss"),
    ];

    private static bool _applied;
    private static string _palette = "classic";

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

    /// <summary>
    /// Applies "system", "light" or "dark" in a colour theme (null: Classic). Returns true when the palette
    /// actually changed.
    /// </summary>
    public static bool Apply(string setting, string? palette = null)
    {
        bool light = setting == "light" || (setting == "system" && SystemPrefersLight());
        palette = IsPalette(palette) ? palette! : "classic";
        if (_applied && light == IsLight && palette == _palette) return false;

        Application.Current.Resources.MergedDictionaries[0] = Load(palette, light);
        IsLight = light;
        _palette = palette;
        _applied = true;
        Changed?.Invoke();
        return true;
    }

    public static bool IsPalette(string? id) => id != null && Palettes.Any(p => p.Id == id);

    /// <summary>The whole palette for a theme: the light or dark base, with the theme's surfaces over it.</summary>
    public static ResourceDictionary Load(string palette, bool light)
    {
        var mode = light ? "Light" : "Dark";
        var all = new ResourceDictionary();
        all.MergedDictionaries.Add(new ResourceDictionary { Source = Part(mode) });
        if (palette != "classic")
            all.MergedDictionaries.Add(new ResourceDictionary { Source = Part($"Palettes/{palette}.{mode}") });
        return all;
    }

    // Named by assembly, so it also loads when Tack runs inside another process (tests/Tack.Tests).
    private static Uri Part(string name) =>
        new($"pack://application:,,,/Tack;component/Themes/{name}.xaml", UriKind.Absolute);
}
