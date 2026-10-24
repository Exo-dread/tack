using System.IO;

namespace Tack.Launcher.Core;

/// <summary>File-system checks, injectable so the rules can be tested without the real disk.</summary>
internal interface IFileProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ExpandEnvironment(string path);

    /// <summary>The program Windows opens a document with (its default "open"), as a full path; null when unknown.</summary>
    string? DefaultProgramFor(string document) => null;
}

internal class RealFileProbe : IFileProbe
{
    public static readonly RealFileProbe Instance = new();

    // Network paths (\\server\share, and mapped network drives) aren't checked: an unreachable server would
    // stall the launcher, or Settings, for 20 s or more. If one is missing, Windows says so when it's opened.
    public bool FileExists(string path) => IsNetwork(path) || File.Exists(path);
    public bool DirectoryExists(string path) => IsNetwork(path) || Directory.Exists(path);
    public string ExpandEnvironment(string path) => Environment.ExpandEnvironmentVariables(path);

    /// <summary>On another computer: a UNC path (with either kind of slash), or a drive letter mapped to a network share.</summary>
    public static bool IsNetwork(string path)
    {
        if (WinPath.IsUnc(path)) return true;
        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':') return false;
        try { return new DriveInfo(path[..1]).DriveType == DriveType.Network; } // GetDriveType: no server is asked
        catch { return false; }
    }
}

/// <summary>
/// How to start a shortcut: the program or document to open and, separately, its arguments. Nothing is
/// ever joined into a command line or passed through a command shell.
/// </summary>
/// <param name="FileName">What ShellExecute opens: a program, a folder or an http(s) address.</param>
/// <param name="ExpectedImage">The program file name whose window should appear (e.g. code.exe, explorer.exe), when known.</param>
/// <param name="CanPlace">Whether a window can be identified and moved to another display afterwards.</param>
internal sealed record LaunchPlan(string FileName, string? Arguments, string? WorkingDirectory, string? ExpectedImage, bool CanPlace);

/// <summary>Validation and launch plans for the three shortcut types. Executable paths, folders and URLs never mix.</summary>
internal static class ShortcutRules
{
    /// <summary>What an app shortcut may point to: programs and the shell's own shortcut files.</summary>
    public static readonly string[] AppExtensions = [".exe", ".lnk", ".appref-ms", ".url", ".bat", ".cmd"];

    /// <summary>Null when the shortcut can be opened; otherwise a short, user-facing reason.</summary>
    public static string? Problem(Shortcut s, IFileProbe probe)
    {
        Plan(s, probe, out string? problem);
        return problem;
    }

    public static LaunchPlan? Plan(Shortcut s, IFileProbe probe, out string? problem)
    {
        problem = null;
        switch (s.KindValue)
        {
            case ShortcutKind.Group:
                problem = "A group opens its shortcuts.";
                return null;

            case ShortcutKind.Url:
                if (NormalizeUrl(s.Target) is not { } uri)
                {
                    problem = "Use a web address starting with https:// or http://.";
                    return null;
                }
                return new LaunchPlan(uri.AbsoluteUri, null, null, null, CanPlace: false);

            case ShortcutKind.Folder:
            {
                string? path = CleanPath(s.Target, probe, out problem, what: "folder");
                if (path == null) return null;
                if (!WinPath.IsFullyQualified(path)) { problem = "Use the folder's full path, like C:\\Projects."; return null; }
                if (!probe.DirectoryExists(path)) { problem = "This folder can't be found."; return null; }
                // Opened through the shell (the user's file manager), with the path as the thing to open.
                return new LaunchPlan(path, null, null, "explorer.exe", CanPlace: true);
            }

            default:
            {
                var app = AppPlan(s, probe, out problem);
                return app != null && s.Document != null ? DocumentPlan(app, s.Document, probe, out problem) : app;
            }
        }
    }

    private static LaunchPlan? AppPlan(Shortcut s, IFileProbe probe, out string? problem)
    {
        string? path = CleanPath(s.Target, probe, out problem, what: "program");
        if (path == null) return null;
        string ext = WinPath.Extension(path);
        if (!AppExtensions.Contains(ext))
        {
            problem = "Choose a program (.exe) or a shortcut (.lnk).";
            return null;
        }
        string? args = s.Arguments;
        if (args != null && HasControlChars(args, allowTab: true)) { problem = "The arguments contain line breaks."; return null; }
        if (args != null && args.Length > LauncherConfig.MaxArguments) { problem = "The arguments are too long."; return null; }

        if (WinPath.IsFullyQualified(path))
        {
            if (!probe.FileExists(path)) { problem = "This program can't be found. Was it moved or uninstalled?"; return null; }
            string? dir = WinPath.Directory(path);
            return new LaunchPlan(path, args, dir, ext == ".exe" ? WinPath.FileName(path) : null, CanPlace: ext is ".exe" or ".lnk");
        }
        // A bare program name (notepad.exe, code.exe): Windows finds it through App Paths and PATH.
        if (ext == ".exe" && path.IndexOfAny(['\\', '/', ':']) < 0)
            return new LaunchPlan(path, args, null, path, CanPlace: true);

        problem = "Use the program's full path, like C:\\Program Files\\App\\app.exe.";
        return null;
    }

    /// <summary>
    /// One of an app's recent files, opened with that app: the program with the file as its own argument (after the
    /// shortcut's arguments), so a Revit 2024 model opens in Revit 2024 even where .rvt files open in Revit 2022.
    /// When Windows opens that kind of file with this very program anyway, the file itself is opened instead, so a
    /// copy that's already running can take it (AutoCAD). Programs, scripts and shortcuts are never opened this way.
    /// </summary>
    private static LaunchPlan? DocumentPlan(LaunchPlan app, string document, IFileProbe probe, out string? problem)
    {
        problem = null;
        if (WinPath.Extension(app.FileName) != ".exe")
        {
            problem = "Recent files open with a program (.exe).";
            return null;
        }
        string? file = RecentFiles.Clean(document);
        if (file == null)
        {
            problem = "Tack can't open this file from here.";
            return null;
        }
        if (!probe.FileExists(file))
        {
            problem = "This file can't be found. Was it moved or deleted?";
            return null;
        }
        string? handler = null;
        try { handler = probe.DefaultProgramFor(file); }
        catch { /* not known: open it with the program */ }
        if (handler != null && WinPath.IsFullyQualified(app.FileName) &&
            string.Equals(handler.Trim().Trim('"'), app.FileName, StringComparison.OrdinalIgnoreCase))
            return new LaunchPlan(file, null, WinPath.Directory(file), app.ExpectedImage, app.CanPlace);

        // A document path never contains a quote (that's refused), so in quotes it stays one argument.
        string quoted = "\"" + file + "\"";
        return app with { Arguments = string.IsNullOrEmpty(app.Arguments) ? quoted : app.Arguments + " " + quoted };
    }

    /// <summary>An http(s) address, adding https:// when the user typed just a host ("github.com").</summary>
    public static Uri? NormalizeUrl(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > LauncherConfig.MaxTarget || HasControlChars(t, allowTab: false) || t.Contains(' ')) return null;
        if (!t.Contains("://", StringComparison.Ordinal))
        {
            // A bare host: "github.com", "intranet.local/wiki". Anything with a colon before a slash
            // (mailto:, javascript:, C:\) is not a web address.
            int colon = t.IndexOf(':'), slash = t.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash) && !LooksLikeHostPort(t)) return null;
            if (!t.Contains('.') && !t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return null;
            t = "https://" + t;
        }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }

    private static bool LooksLikeHostPort(string t)
    {
        int colon = t.IndexOf(':');
        int end = t.IndexOf('/', colon);
        string port = end < 0 ? t[(colon + 1)..] : t[(colon + 1)..end];
        return port.Length is > 0 and <= 5 && port.All(char.IsAsciiDigit) && colon > 0;
    }

    /// <summary>
    /// Trims, removes one pair of surrounding quotes (paths pasted from Explorer's "Copy as path"), expands
    /// %VARIABLES% and writes slashes the Windows way (so //server/share is seen as the network path it is).
    /// </summary>
    private static string? CleanPath(string? target, IFileProbe probe, out string? problem, string what)
    {
        problem = null;
        string t = (target ?? "").Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1].Trim();
        if (t.Length == 0) { problem = $"Choose a {what}."; return null; }
        if (HasControlChars(t, allowTab: false) || t.IndexOfAny(WinPath.InvalidChars) >= 0)
        {
            problem = $"This {what} path contains characters Windows doesn't allow.";
            return null;
        }
        if (t.Contains('%')) t = probe.ExpandEnvironment(t);
        t = t.Replace('/', '\\');
        if (t.Length > LauncherConfig.MaxTarget) { problem = "This path is too long."; return null; }
        return t;
    }

    private static bool HasControlChars(string s, bool allowTab) =>
        s.Any(c => char.IsControl(c) && !(allowTab && c == '\t'));
}

/// <summary>
/// Windows path rules, independent of the OS the code runs on (System.IO.Path follows the host OS,
/// which would make the tests behave differently on Linux).
/// </summary>
internal static class WinPath
{
    /// <summary>Characters no Windows file or folder path may contain (wildcards included).</summary>
    public static readonly char[] InvalidChars = ['"', '<', '>', '|', '*', '?'];

    /// <summary>C:\... or \\server\share\... (not relative, not drive-relative like C:foo).</summary>
    public static bool IsFullyQualified(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])) return true;
        return path.Length >= 5 && IsSeparator(path[0]) && IsSeparator(path[1]) && !IsSeparator(path[2]);
    }

    /// <summary>Starts with two slashes of either kind: \\server\share, //server/share, \\?\… (never checked on disk).</summary>
    public static bool IsUnc(string path) => path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    public static string FileName(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>Lower-case extension with its dot ("" when none).</summary>
    public static string Extension(string path)
    {
        string name = FileName(path);
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : "";
    }

    public static string? Directory(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        if (i <= 0) return null;
        string dir = path[..i];
        return dir.Length == 2 && dir[1] == ':' ? dir + "\\" : dir;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
