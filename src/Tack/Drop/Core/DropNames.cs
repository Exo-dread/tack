using System.IO;
using System.Text;
using Tack.Launcher.Core;
using Tack.Model;
using Tack.Sync;

namespace Tack.Drop.Core;

/// <summary>Names from a drag (file names, page titles, subjects), made safe to show, to save under and to use as a task's text.</summary>
internal static class DropNames
{
    /// <summary>
    /// File types that run code when opened. Tack never keeps a copy of one, from a virtual file or a temporary
    /// folder (Windows' own list, AssocIsDangerous, is asked as well on the PC).
    /// </summary>
    public static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst", ".msc", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".vb", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".hta",
        ".lnk", ".url", ".website", ".appref-ms", ".application", ".gadget", ".jar", ".reg", ".inf", ".chm", ".scf", ".shb", ".shs",
        ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".diagcab", ".xbap", ".xll", ".hlp", ".ins", ".isp",
        ".mshxml", ".py", ".pyw", ".pl", ".rb", ".sys", ".dll", ".ocx", ".drv",
    };

    /// <summary>Names Windows keeps for devices: a file can't have them, with or without an extension.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private const int MaxFileName = 120;

    /// <summary>Characters that turn text around on screen (so "invoice‮fdp.exe" can pass for a PDF). Never kept in a name.</summary>
    public static bool IsDirectionControl(char c) => c is '\u200E' or '\u200F' or '\u061C' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// <summary>One line of at most <paramref name="max"/> characters: line breaks and control characters become spaces, direction controls go, runs of space collapse.</summary>
    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 16));
        foreach (char c in text)
        {
            if (sb.Length > max * 2 + 16) break; // enough to fill the line however much collapses
            if (IsDirectionControl(c)) continue;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return SpaceView.Shorten(JsText.Trim(JsText.CleanTaskText(sb.ToString())), max);
    }

    /// <summary>The last part of a path ("C:\Models\Clash report L3.pdf" → "Clash report L3.pdf"); a drive's root keeps its letter.</summary>
    public static string LastSegment(string path)
    {
        string p = path.TrimEnd('\\', '/');
        if (p.Length == 0) return "";
        int i = p.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? p[(i + 1)..] : p;
    }

    /// <summary>"Clash report L3.pdf" → "Clash report L3"; a name that's all extension (".gitignore") stays as it is.</summary>
    public static string WithoutExtension(string name)
    {
        string ext = WinPath.Extension(name);
        return ext.Length > 0 ? name[..^ext.Length] : name;
    }

    /// <summary>
    /// Whether a name on a network share looks like a file's ("Clash report L3.pdf") rather than a folder's
    /// ("Phase 2.01"): Tack doesn't ask a server which it is, so a short extension with a letter in it decides.
    /// </summary>
    public static bool LooksLikeFile(string name)
    {
        string ext = WinPath.Extension(name);
        if (ext.Length is < 2 or > 6) return false;
        bool letter = false;
        foreach (char c in ext.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
            letter |= char.IsAsciiLetter(c);
        }
        return letter;
    }

    public static bool IsEmail(string name) => WinPath.Extension(name) is ".eml" or ".msg";

    /// <summary>An internet shortcut, as browsers offer one with every dragged link.</summary>
    public static bool IsShortcut(string name) => WinPath.Extension(name) is ".url" or ".website";

    /// <summary>
    /// A name to save a dragged file under, inside Tack's own folder: the last part only (no "..\", no drive),
    /// without characters Windows refuses or that turn text around, never a device name, at most 120 characters
    /// (its extension kept), and <paramref name="fallback"/> when nothing is left.
    /// </summary>
    public static string SafeFileName(string? raw, string fallback)
    {
        string name = LastSegment(raw ?? "");
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsControl(c) || IsDirectionControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') continue;
            sb.Append(c);
        }
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c == '.')) name = fallback;
        int dot = name.IndexOf('.');
        if (Reserved.Contains((dot >= 0 ? name[..dot] : name).TrimEnd(' '))) name = "_" + name;
        if (name.Length > MaxFileName)
        {
            string ext = WinPath.Extension(name);
            if (ext.Length > 16) ext = "";
            int cut = MaxFileName - ext.Length;
            if (char.IsHighSurrogate(name[cut - 1])) cut--;
            name = name[..cut].TrimEnd('.', ' ') + ext;
        }
        return name;
    }
}

/// <summary>
/// What an attachment may point to, and how it's opened: the same rules as the launcher's shortcuts
/// (<see cref="ShortcutRules"/>, <see cref="WinPath"/>): a file or folder by its full path, a web page by an http(s)
/// address, through ShellExecute with nothing joined into a command line. Paths are taken exactly as they were
/// dropped (nothing expanded), and Windows' device paths (\\?\, \\.\) never count.
/// </summary>
internal static class AttachmentRules
{
    /// <summary>A dropped file or folder path Tack will keep, or null: full (C:\… or \\server\share\…), at most 2048 characters, nothing Windows refuses.</summary>
    public static string? CleanPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > DropLimits.MaxTarget) return null;
        foreach (char c in path)
            if (char.IsControl(c) || Array.IndexOf(WinPath.InvalidChars, c) >= 0) return null;
        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) return null;
        if (!WinPath.IsFullyQualified(p)) return null;
        if (p.IndexOf(':', 2) >= 0) return null; // a stream inside a file (name:stream), or junk
        var parts = (WinPath.IsUnc(p) ? p[2..] : p[3..]).Split('\\');
        if (WinPath.IsUnc(p) && (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)) return null; // \\server\share at least
        foreach (string part in parts)
            if (part is "." or "..") return null;
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>A dropped web address Tack will keep, or null: http or https, written out in full, at most 2048 characters, with no user name or password in it.</summary>
    public static Uri? CleanUrl(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > DropLimits.MaxTarget) return null;
        if (!t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
        if (ShortcutRules.NormalizeUrl(t) is not { } uri || uri.UserInfo.Length > 0) return null;
        return uri.AbsoluteUri.Length <= DropLimits.MaxTarget ? uri : null;
    }

    /// <summary>A saved copy's place in the attachments folder: "&lt;12 hex&gt;\&lt;file name&gt;" exactly.</summary>
    public static bool IsSavedTarget(string target)
    {
        int slash = target.IndexOf('\\');
        if (slash != 12 || target.Length <= 13) return false;
        foreach (char c in target.AsSpan(0, 12))
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        string name = target[13..];
        return DropNames.SafeFileName(name, "") == name && name.Length > 0;
    }

    /// <summary>The full path of a saved copy, inside <paramref name="folder"/> and nowhere else; null if <paramref name="target"/> isn't one.</summary>
    public static string? SavedPath(string folder, string target)
    {
        if (!IsSavedTarget(target)) return null;
        string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(Path.Combine(root, target));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>What the paperclip says it opens: the file or folder's name, the site it goes to (so a title can't hide where), or the email.</summary>
    public static string Tip(Attachment a) => a.Kind switch
    {
        AttachmentKind.Link => CleanUrl(a.Target) is { } uri ? $"Open {uri.IdnHost}" : "Open link",
        _ when a.Saved && DropNames.IsEmail(a.Label) => "Open email",
        AttachmentKind.Folder => $"Open folder {a.Label}",
        _ => $"Open {a.Label}",
    };

    /// <summary>How to open an attachment, or null with a short reason (a file that's gone says so).</summary>
    public static LaunchPlan? Plan(Attachment a, string folder, IFileProbe probe, Func<string, bool> isProgram, out string? problem)
    {
        problem = null;
        switch (a.Kind)
        {
            case AttachmentKind.Link:
                if (CleanUrl(a.Target) is not { } uri) { problem = "Tack can't open this address."; return null; }
                return new LaunchPlan(uri.AbsoluteUri, null, null, null, CanPlace: false);

            case AttachmentKind.Folder:
            {
                if (CleanPath(a.Target) is not { } path) { problem = "Tack can't open this folder."; return null; }
                if (!probe.DirectoryExists(path)) { problem = "This folder can't be found. Was it moved or deleted?"; return null; }
                return new LaunchPlan(path, null, null, "explorer.exe", CanPlace: false);
            }

            default:
            {
                string? path = a.Saved ? SavedPath(folder, a.Target) : CleanPath(a.Target);
                if (path == null) { problem = "Tack can't open this file."; return null; }
                if (a.Saved && isProgram(WinPath.Extension(path))) { problem = "Tack doesn't open programs it was given."; return null; }
                if (!probe.FileExists(path))
                {
                    problem = a.Saved ? "Tack's copy of this can't be found." : "This file can't be found. Was it moved or deleted?";
                    return null;
                }
                return new LaunchPlan(path, null, null, null, CanPlace: false);
            }
        }
    }
}
