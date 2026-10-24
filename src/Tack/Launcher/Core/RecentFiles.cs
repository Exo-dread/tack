using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tack.Launcher.Core;

/// <summary>One of an app's recent files: a full path, checked to exist when it was read (network paths aren't checked).</summary>
internal sealed record RecentFile(string Path)
{
    public string Name => WinPath.FileName(Path);

    /// <summary>The name without its extension ("Level 1" for "Level 1.rvt"); the whole name when it has none.</summary>
    public string Stem
    {
        get
        {
            string name = Name;
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    /// <summary>The extension as written (".rvt", ".NWD"), or "".</summary>
    public string Extension
    {
        get
        {
            string name = Name;
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name[dot..] : "";
        }
    }

    public string Folder => WinPath.Directory(Path) ?? "";
}

/// <summary>
/// Picking an app's recent files from the lists Windows and the app keep. Everything here is plain data: the
/// lists come in as strings (from a Jump List, an ini file, the registry) and leave as checked full paths.
/// Those lists can be written by any program running as the user, so every entry is cleaned on its own, and
/// one that isn't a plain document path (a program, a script, a shortcut, a URL, a device path) is dropped.
/// </summary>
internal static class RecentFiles
{
    /// <summary>The most an app shows.</summary>
    public const int Max = 8;

    /// <summary>Entries looked at in each list, and in all of them together: enough to skip files that are gone.</summary>
    public const int PerSource = 40;
    public const int MaxLooked = 160;

    /// <summary>Never shown as a recent file: these run something rather than open a document.</summary>
    public static readonly string[] Refused =
    [
        ".exe", ".com", ".bat", ".cmd", ".pif", ".scr", ".cpl", ".msc", ".msi", ".msp", ".lnk", ".url", ".appref-ms",
        ".application", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".reg", ".jar", ".inf",
        ".library-ms", ".search-ms", ".settingcontent-ms",
    ];

    /// <summary>A full path to a document, written the Windows way, or null when the entry isn't one.</summary>
    public static string? Clean(string? raw)
    {
        if (raw == null) return null;
        string t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1].Trim();
        if (t.Length < 4 || t.Length > LauncherConfig.MaxTarget) return null;
        if (t.Any(char.IsControl) || t.IndexOfAny(WinPath.InvalidChars) >= 0) return null;
        t = t.Replace('/', '\\');
        if (!WinPath.IsFullyQualified(t)) return null;              // relative paths, cloud entries ("cld://…"), web addresses
        if (t.StartsWith(@"\\?\", StringComparison.Ordinal) || t.StartsWith(@"\\.\", StringComparison.Ordinal)) return null; // device paths
        if (t.IndexOf(':', 2) >= 0) return null;                    // alternate data streams ("file.txt:hidden")
        if (t.EndsWith('\\')) return null;                          // a folder, not a file
        foreach (string segment in t.Split('\\'))
        {
            if (segment is "." or "..") return null;                // no walking up out of a folder
            if (segment.Length > 0 && segment != segment.TrimEnd(' ', '.')) return null; // "name.rvt." and "name " aren't what they look like
        }
        if (Refused.Contains(WinPath.Extension(t))) return null;
        return t;
    }

    /// <summary>
    /// Up to <paramref name="max"/> distinct files that exist, in the order the lists give them: the first list
    /// first. A later list is only read when the earlier ones didn't give enough (they're enumerated lazily).
    /// </summary>
    public static IReadOnlyList<RecentFile> Pick(IEnumerable<IEnumerable<string?>> sources, Func<string, bool> exists, int max = Max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<RecentFile>();
        int looked = 0;
        foreach (var source in sources)
        {
            int fromThis = 0;
            foreach (var raw in source)
            {
                if (++fromThis > PerSource) break;
                if (++looked > MaxLooked) return picked;
                var path = Clean(raw);
                if (path == null || !seen.Add(path)) continue;
                bool there;
                try { there = exists(path); }
                catch { there = false; }
                if (!there) continue;
                picked.Add(new RecentFile(path));
                if (picked.Count >= max) return picked;
            }
        }
        return picked;
    }

    /// <summary>A folder short enough to read at a glance: "D:\Projects\R06\Models", or "D:\…\R06\Models" when long.</summary>
    public static string ShortFolder(string folder, int max = 46)
    {
        if (folder.Length <= max) return folder;
        var parts = folder.Split('\\');
        // The root: "D:" or, for \\server\share, the server and share.
        int rootParts = folder.StartsWith(@"\\", StringComparison.Ordinal) ? Math.Min(4, parts.Length) : 1;
        string root = string.Join('\\', parts.Take(rootParts));
        var tail = new List<string>();
        int length = root.Length + 2;
        for (int i = parts.Length - 1; i >= rootParts; i--)
        {
            if (tail.Count > 0 && length + parts[i].Length + 1 > max) break;
            tail.Insert(0, parts[i]);
            length += parts[i].Length + 1;
        }
        string shortened = root + "\\…\\" + string.Join('\\', tail);
        // One very long folder name: keep its end.
        return shortened.Length <= max + 12 ? shortened : "…" + folder[^(max - 1)..];
    }

    /// <summary>Text from an ini file: UTF-16 (Revit's), UTF-8 or ANSI, with or without a byte-order mark.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // No mark: UTF-16 has a zero in every other byte of ASCII text.
        int zeros = 0, sample = Math.Min(bytes.Length, 512);
        for (int i = 1; i < sample; i += 2) if (bytes[i] == 0) zeros++;
        if (sample >= 4 && zeros * 4 >= sample) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The same folder, however it's written (trailing slash, case, slashes).</summary>
    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string N(string s) => s.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}

internal enum RegHive { CurrentUser, LocalMachine }

/// <summary>Read-only registry access, injectable so the app lists can be tested with fixture data.</summary>
internal interface IRegistryView
{
    IReadOnlyList<string> SubKeys(RegHive hive, string path);
    IReadOnlyList<string> ValueNames(RegHive hive, string path);

    /// <summary>A string, an int (DWORD), a long (QWORD), or null when the key or value isn't there.</summary>
    object? Value(RegHive hive, string path, string name);
}

/// <summary>
/// The recent-files lists some programs keep themselves, for when the Jump List has too few: Revit's Revit.ini,
/// AutoCAD's and Navisworks' "Recent File List" in the registry. Each is matched to the very program the shortcut
/// starts (by its folder), so Revit 2024 never shows Revit 2022's files.
/// </summary>
internal static class AppMru
{
    private const int MaxKeys = 64;
    private const int MaxEntries = 200;

    public static IEnumerable<string> For(string program, IRecentSystem system) => WinPath.FileName(program).ToLowerInvariant() switch
    {
        "revit.exe" => Lazy(() => Revit(program, system)),
        "acad.exe" => Lazy(() => AutoCad(program, system.Registry)),
        "roamer.exe" => Lazy(() => Navisworks(program, system.Registry)),
        _ => [],
    };

    /// <summary>Read only when enumerated; a list that can't be read is just empty.</summary>
    private static IEnumerable<string> Lazy(Func<IReadOnlyList<string>> read)
    {
        IReadOnlyList<string> list;
        try { list = read(); }
        catch { list = []; }
        foreach (string s in list) yield return s;
    }

    // ---- Revit: %APPDATA%\Autodesk\Revit\Autodesk Revit 2024\Revit.ini, [Recent File List] File1=… ----

    /// <summary>"Autodesk Revit 2024" for …\Revit 2024\Revit.exe (and "Autodesk Revit LT 2024"); null for anything else.</summary>
    public static string? RevitProfile(string program)
    {
        if (!string.Equals(WinPath.FileName(program), "Revit.exe", StringComparison.OrdinalIgnoreCase)) return null;
        string? dir = WinPath.Directory(program);
        if (dir == null) return null;
        string folder = WinPath.FileName(dir.TrimEnd('\\'));
        return Regex.IsMatch(folder, @"^Revit( LT)? 20\d\d$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "Autodesk " + folder : null;
    }

    public static string? RevitIniPath(string program, string roamingAppData) =>
        RevitProfile(program) is { } profile ? System.IO.Path.Combine(roamingAppData, "Autodesk", "Revit", profile, "Revit.ini") : null;

    private static IReadOnlyList<string> Revit(string program, IRecentSystem system) =>
        RevitIniPath(program, system.RoamingAppData) is { } ini && system.ReadText(ini) is { } text ? RevitIni(text) : [];

    /// <summary>The [Recent File List] section's FileN entries, by N (Revit doesn't write them in order).</summary>
    public static IReadOnlyList<string> RevitIni(string text)
    {
        var found = new List<(int N, string Path)>();
        bool inSection = false;
        int lines = 0;
        foreach (string raw in text.Split('\n'))
        {
            if (++lines > 50_000 || found.Count >= MaxEntries) break;
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[Recent File List]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0 || Number(line[..eq].Trim(), "File") is not { } n) continue;
            found.Add((n, line[(eq + 1)..].Trim()));
        }
        return found.OrderBy(f => f.N).Select(f => f.Path).ToList();
    }

    // ---- AutoCAD: HKCU\Software\Autodesk\AutoCAD\R25.1\ACAD-9101:409\Recent File List, File1=… ----

    public static IReadOnlyList<string> AutoCad(string program, IRegistryView reg)
    {
        string? dir = WinPath.Directory(program);
        const string root = @"SOFTWARE\Autodesk\AutoCAD";
        foreach (string release in reg.SubKeys(RegHive.LocalMachine, root).Take(MaxKeys))
        {
            if (!release.StartsWith('R') || !IsKeyName(release)) continue;
            foreach (string product in reg.SubKeys(RegHive.LocalMachine, $@"{root}\{release}").Take(MaxKeys))
            {
                if (!IsKeyName(product)) continue;
                // AcadLocation names the folder this release's acad.exe is in.
                if (reg.Value(RegHive.LocalMachine, $@"{root}\{release}\{product}", "AcadLocation") is not string location || !RecentFiles.SameFolder(location, dir)) continue;
                return Numbered(reg, RegHive.CurrentUser, $@"Software\Autodesk\AutoCAD\{release}\{product}\Recent File List", "File");
            }
        }
        return [];
    }

    // ---- Navisworks: HKCU\Software\Autodesk\Navisworks Manage\24.0\Recent File List\1\Path, LastOpened ----

    private static readonly string[] NavisworksProducts = ["Navisworks Manage", "Navisworks Simulate", "Navisworks Freedom"];

    public static IReadOnlyList<string> Navisworks(string program, IRegistryView reg)
    {
        string? dir = WinPath.Directory(program);
        foreach (string product in NavisworksProducts)
        {
            foreach (string version in reg.SubKeys(RegHive.LocalMachine, $@"SOFTWARE\Autodesk\{product}").Take(MaxKeys))
            {
                if (!IsKeyName(version)) continue;
                if (reg.Value(RegHive.LocalMachine, $@"SOFTWARE\Autodesk\{product}\{version}\Location", "Path") is not string location || !RecentFiles.SameFolder(location, dir)) continue;
                string list = $@"Software\Autodesk\{product}\{version}\Recent File List";
                var entries = new List<(int N, long Last, string Path)>();
                foreach (string sub in reg.SubKeys(RegHive.CurrentUser, list).Take(MaxEntries))
                {
                    if (Number(sub, "") is not { } n) continue;
                    if (reg.Value(RegHive.CurrentUser, $@"{list}\{sub}", "Path") is not string path) continue;
                    long last = reg.Value(RegHive.CurrentUser, $@"{list}\{sub}", "LastOpened") switch { long l => l, int i => i, _ => 0 };
                    entries.Add((n, last, path));
                }
                // Most recently opened first (the numbers usually agree; they break ties).
                return entries.OrderByDescending(e => e.Last).ThenBy(e => e.N).Select(e => e.Path).ToList();
            }
        }
        return [];
    }

    /// <summary>Values named prefix1, prefix2… ("File1"), in number order.</summary>
    private static IReadOnlyList<string> Numbered(IRegistryView reg, RegHive hive, string path, string prefix)
    {
        var found = new List<(int N, string Value)>();
        foreach (string name in reg.ValueNames(hive, path))
        {
            if (found.Count >= MaxEntries) break;
            if (Number(name, prefix) is { } n && reg.Value(hive, path, name) is string value) found.Add((n, value));
        }
        return found.OrderBy(f => f.N).Select(f => f.Value).ToList();
    }

    /// <summary>The N in "File12" (prefix "File"), 1 to 1000; null for "FileTime12", "File", "File-1".</summary>
    private static int? Number(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string digits = name[prefix.Length..];
        return digits.Length is > 0 and <= 4 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 1000 ? n : null;
    }

    /// <summary>A registry key name to build another path from: one segment, no control characters.</summary>
    private static bool IsKeyName(string name) => name.Length is > 0 and <= 128 && name.IndexOf('\\') < 0 && !name.Any(char.IsControl);
}

/// <summary>A Start menu entry: what it's called, its AppUserModelID, and the program it starts (none for Store apps).</summary>
internal sealed record AppEntry(string Name, string AppId, string? Target);

/// <summary>
/// Which Jump Lists belong to a program. Windows keeps an app's Jump List under its AppUserModelID: the one the
/// app sets itself ("Microsoft.VisualStudioCode"), the one the Start menu gives its shortcut
/// ("Microsoft.AutoGenerated.{…}", for Revit, Navisworks, AutoCAD), a Store app's (through its execution alias,
/// for Notepad), or one made from the program's path. The candidates are tried in that order.
/// </summary>
internal static class AppIds
{
    public const int Max = 6;

    /// <param name="aliasAppId">The Store app behind an execution alias of the same name, if any (notepad.exe).</param>
    /// <param name="knownFolders">Known folders as (id, path), e.g. ("{6D809377-…}", "C:\Program Files").</param>
    public static IReadOnlyList<string> Candidates(string program, string? label, IReadOnlyList<AppEntry> apps, string? aliasAppId,
        IReadOnlyList<(string Id, string Path)> knownFolders)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) return;
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }

        // Start menu entries for this very program: the one named like the shortcut first, then the plainest
        // ("Revit 2024" before "Revit Viewer 2024", "Navisworks Manage 2026" before "… (BIM 360)").
        var exact = apps.Where(a => a.Target != null && SameFile(a.Target, program))
            .OrderBy(a => string.Equals(a.Name, label?.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Name.Length)
            .ToList();
        foreach (var a in exact) Add(a.AppId);
        Add(aliasAppId);
        // Another copy of the same program (VS Code installed for everyone and for you): only an ID the app chose
        // itself, which doesn't depend on where it's installed. Never Revit 2022's for Revit 2024.
        if (exact.Count == 0)
        {
            string file = WinPath.FileName(program);
            foreach (var a in apps.Where(a => a.Target != null && IsExplicit(a.AppId) && string.Equals(WinPath.FileName(a.Target), file, StringComparison.OrdinalIgnoreCase)))
                Add(a.AppId);
        }
        Add(Implicit(program, knownFolders));
        return ids.Take(Max).ToList();
    }

    /// <summary>An ID the app (or package) chose: not one Windows made from a shortcut or a path.</summary>
    public static bool IsExplicit(string id) =>
        !id.StartsWith("Microsoft.AutoGenerated.", StringComparison.OrdinalIgnoreCase) && id.IndexOfAny(['\\', '/']) < 0;

    /// <summary>The ID Windows gives a program that sets none: its path, with the known folder it's in written as that folder's id.</summary>
    public static string Implicit(string program, IReadOnlyList<(string Id, string Path)> knownFolders)
    {
        (string Id, string Path)? best = null;
        foreach (var k in knownFolders)
        {
            string root = k.Path.TrimEnd('\\');
            if (root.Length < 3 || program.Length <= root.Length + 1 || program[root.Length] != '\\') continue;
            if (!program.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || root.Length > best.Value.Path.TrimEnd('\\').Length) best = k;
        }
        return best is { } b ? b.Id + program[b.Path.TrimEnd('\\').Length..] : program;
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(a.Trim().Replace('/', '\\'), b.Trim().Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>What reading recent files needs from Windows. The real one is in LauncherRecents.cs; tests use fixtures.</summary>
internal interface IRecentSystem
{
    /// <summary>False when Windows is set not to keep recently opened items (Settings → Personalization → Start).</summary>
    bool TrackingAllowed { get; }

    IFileProbe Probe { get; }
    IRegistryView Registry { get; }
    string RoamingAppData { get; }

    /// <summary>System32 and the Windows folder, where bare program names (notepad.exe) are found.</summary>
    IReadOnlyList<string> SystemFolders { get; }

    /// <summary>The Jump List IDs to read for a program (see <see cref="AppIds"/>).</summary>
    IReadOnlyList<string> AppIdsFor(string program, Shortcut app);

    /// <summary>A Jump List's recent items, most recent first (read when enumerated).</summary>
    IEnumerable<string> JumpList(string appId);

    /// <summary>A small text file, or null (missing, too big, unreadable).</summary>
    string? ReadText(string path);

    /// <summary>Whether a file is there. Network paths aren't asked about (Tack never contacts a server by itself).</summary>
    bool Exists(string path);
}

/// <summary>An app shortcut's recent files: its Jump List first, then the app's own list, up to <see cref="RecentFiles.Max"/>.</summary>
internal static class RecentReader
{
    public static IReadOnlyList<RecentFile> Read(Shortcut app, IRecentSystem system)
    {
        if (app.KindValue != ShortcutKind.App || !system.TrackingAllowed) return [];
        string? program = ProgramOf(app, system);
        if (program == null) return [];
        return RecentFiles.Pick(Sources(program, app, system), system.Exists);
    }

    private static IEnumerable<IEnumerable<string?>> Sources(string program, Shortcut app, IRecentSystem system)
    {
        foreach (string id in system.AppIdsFor(program, app)) yield return system.JumpList(id);
        yield return AppMru.For(program, system);
    }

    /// <summary>
    /// The program (.exe) an app shortcut starts, as a full path: its own, or for a bare name (notepad.exe) the one
    /// Windows would find through App Paths or the system folders. Null for anything else (.lnk, .bat, missing).
    /// </summary>
    public static string? ProgramOf(Shortcut app, IRecentSystem system)
    {
        var plan = ShortcutRules.Plan(app, system.Probe, out _);
        if (plan == null || WinPath.Extension(plan.FileName) != ".exe") return null;
        if (WinPath.IsFullyQualified(plan.FileName)) return plan.FileName;

        string name = plan.FileName;
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            if (system.Registry.Value(hive, $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}", "") is not string registered) continue;
            string path = registered.Trim().Trim('"');
            if (path.Contains('%')) path = system.Probe.ExpandEnvironment(path);
            if (WinPath.IsFullyQualified(path) && WinPath.Extension(path) == ".exe" && path.IndexOfAny(WinPath.InvalidChars) < 0 && system.Probe.FileExists(path)) return path;
        }
        foreach (string folder in system.SystemFolders)
        {
            string path = folder.TrimEnd('\\') + "\\" + name;
            if (system.Probe.FileExists(path)) return path;
        }
        return null;
    }
}
