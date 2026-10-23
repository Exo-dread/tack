using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Launcher.Core;
using static Tack.Interop.NativeMethods;

namespace Tack.Launcher;

/// <summary>
/// Program and folder icons from the shell (SHGetFileInfo), cached by path for the session: a few KB each,
/// at most 54 shortcuts. Websites and groups use a glyph: nothing is ever fetched from the network.
/// Cleared when the launcher is turned off.
/// </summary>
internal static class LauncherIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => Cache.Clear();

    /// <summary>A Segoe Fluent / MDL2 glyph for shortcuts without a shell icon.</summary>
    public static string Glyph(Shortcut s) => s.KindValue switch
    {
        ShortcutKind.Group => "",  // ViewAll: four tiles, several things behind one
        ShortcutKind.Url => "",    // Globe
        ShortcutKind.Folder => "", // Folder
        _ => "",                   // An app window (a generic app): unlike a group's tiles
    };

    public static ImageSource? For(Shortcut s)
    {
        if (s.KindValue is ShortcutKind.Group or ShortcutKind.Url) return null;
        string? path = Resolve(s);
        if (path == null) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var icon = Extract(path);
        Cache[path] = icon;
        return icon;
    }

    private static string? Resolve(Shortcut s)
    {
        string t = s.Target.Trim().Trim('"');
        if (t.Contains('%')) t = Environment.ExpandEnvironmentVariables(t);
        // Never touch network paths (or mapped network drives) for an icon: an unreachable server would stall the launcher.
        if (t.Length == 0 || RealFileProbe.IsNetwork(t)) return null;
        if (WinPath.IsFullyQualified(t)) return File.Exists(t) || Directory.Exists(t) ? t : null;
        if (s.KindValue == ShortcutKind.App && t.IndexOfAny(['\\', '/', ':']) < 0)
        {
            // A bare program name: look where Windows' own programs live.
            foreach (string dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                string candidate = Path.Combine(dir, t);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            IntPtr ok = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch
        {
            return null; // a glyph is fine
        }
    }
}
