using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Tack.Interop;
using Tack.Launcher.Core;
using Tack.Services;
using static Tack.Interop.NativeMethods;

namespace Tack.Launcher;

/// <summary>
/// Reads app shortcuts' recent files while the launcher opens (only with Settings → Launcher → Recent files on):
/// on one short-lived background thread, so the orb and the launcher never wait for it. Each app's files are
/// handed to the UI thread as soon as they're read. Nothing polls, watches or writes: Jump Lists, ini files and the
/// registry are only read, and network paths are never asked about (see <see cref="WindowsRecentSystem.Exists"/>).
/// </summary>
internal static class RecentsLoader
{
    // A reader stuck on something slow mustn't let them pile up: past two, an opening goes without (the display
    // picker, as when the option is off).
    private const int MaxRunning = 2;
    private static int _running;
    private static int _errorsLogged;

    public static void Start(IReadOnlyList<(NodeRef Node, Shortcut App)> apps, Action<NodeRef, IReadOnlyList<RecentFile>> found)
    {
        if (apps.Count == 0) return;
        if (Interlocked.Increment(ref _running) > MaxRunning)
        {
            Interlocked.Decrement(ref _running);
            foreach (var (node, _) in apps) found(node, []);
            return;
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        var work = apps.Select(a => (a.Node, App: a.App.Clone())).ToList();
        var thread = new Thread(() =>
        {
            int done = 0;
            void Post(NodeRef node, IReadOnlyList<RecentFile> files)
            {
                try { dispatcher.BeginInvoke(() => found(node, files)); }
                catch { /* Tack is closing */ }
            }
            try
            {
                var system = new WindowsRecentSystem();
                foreach (var (node, app) in work)
                {
                    IReadOnlyList<RecentFile> files;
                    try { files = RecentReader.Read(app, system); }
                    catch (Exception ex)
                    {
                        // (No paths in the log: what someone opened is theirs.)
                        if (Interlocked.Increment(ref _errorsLogged) <= 3) Log.Error("Launcher: couldn't read an app's recent files", ex);
                        files = [];
                    }
                    Post(node, files);
                    done++;
                }
                AppsIndex.RefreshIfOld();
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _errorsLogged) <= 3) Log.Error("Launcher: reading recent files stopped", ex);
                // The rest have none: resting on them offers the display picker, as without recent files.
                foreach (var (node, _) in work.Skip(done)) Post(node, []);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        })
        { IsBackground = true, Name = "Tack launcher recent files" };
        thread.SetApartmentState(ApartmentState.STA); // the shell's Jump List and Start menu objects are COM
        thread.Start();
    }
}

/// <summary>Windows' side of <see cref="IRecentSystem"/>: Jump Lists, the Start menu's app IDs, the registry and the disk.</summary>
internal sealed class WindowsRecentSystem : IRecentSystem
{
    private const long MaxTextBytes = 2 * 1024 * 1024; // Revit.ini is tens of KB

    private static readonly (Guid Id, string Name)[] KnownFolderIds =
    [
        (new("6D809377-6AF0-444B-8957-A3773F02200E"), "ProgramFilesX64"),
        (new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), "ProgramFilesX86"),
        (new("6365D5A7-0F0D-45E5-87F6-0DA56B6A4F7D"), "ProgramFilesCommonX64"),
        (new("DE974D24-D9C6-4D3E-BF91-F4455120B917"), "ProgramFilesCommonX86"),
        (new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), "System"),
        (new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), "SystemX86"),
        (new("F38BF404-1D43-42F2-9305-67DE0B28FC23"), "Windows"),
        (new("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"), "UserProgramFiles"),
        (new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), "LocalAppData"),
        (new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), "RoamingAppData"),
    ];

    private readonly RegistryView _registry = new();
    private IReadOnlyList<(string Id, string Path)>? _knownFolders;

    public WindowsRecentSystem()
    {
        RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        WindowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        Packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        SystemFolders = [Environment.SystemDirectory, windows, WindowsApps];
        WindowsFolder = windows;
    }

    public IFileProbe Probe => RealFileProbe.Instance;
    public IRegistryView Registry => _registry;
    public string RoamingAppData { get; }
    public IReadOnlyList<string> SystemFolders { get; }
    private string WindowsFolder { get; }
    private string WindowsApps { get; }
    private string Packages { get; }

    /// <summary>Windows' own switches: "Show recently opened items in Start, Jump Lists and File Explorer", and the policy.</summary>
    public bool TrackingAllowed
    {
        get
        {
            const string advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            const string policy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
            if (_registry.Value(RegHive.CurrentUser, advanced, "Start_TrackDocs") is int track && track == 0) return false;
            if (_registry.Value(RegHive.CurrentUser, policy, "NoRecentDocsHistory") is int user && user != 0) return false;
            if (_registry.Value(RegHive.LocalMachine, policy, "NoRecentDocsHistory") is int machine && machine != 0) return false;
            return true;
        }
    }

    public IReadOnlyList<string> AppIdsFor(string program, Shortcut app)
    {
        // A Store app keeps its Jump List under its own app ID, which its execution alias names: the alias itself,
        // the Windows folder's program of the same name (notepad.exe opens the new Notepad), or the app's program
        // in its package folder (where App Paths sends notepad.exe), if the alias of that name points at it.
        string? alias = null;
        string sameName = Path.Combine(WindowsApps, WinPath.FileName(program));
        if (IsUnder(program, WindowsApps)) alias = ExecutionAlias.AppIdOf(program, out _);
        else if (IsUnder(program, WindowsFolder)) alias = ExecutionAlias.AppIdOf(sameName, out _);
        else if (IsUnder(program, Packages) && ExecutionAlias.AppIdOf(sameName, out string? target) is { } id &&
                 string.Equals(target, program, StringComparison.OrdinalIgnoreCase))
            alias = id;
        return AppIds.Candidates(program, app.Label, AppsIndex.Get(), alias, KnownFolders());
    }

    public IEnumerable<string> JumpList(string appId)
    {
        // Read when Pick gets to it: often the first list is enough.
        foreach (string path in JumpLists.Recent(appId, RecentFiles.PerSource)) yield return path;
    }

    public string? ReadText(string path)
    {
        try
        {
            if (RealFileProbe.IsNetwork(path) || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxTextBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return RecentFiles.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A file on this PC that's there. A network path (or a mapped network drive) isn't checked: that would have
    /// Tack contact a server by itself, and an unreachable one keeps Windows busy for 20 s. Windows says so when a
    /// missing one is opened.
    /// </summary>
    public bool Exists(string path) => RealFileProbe.IsNetwork(path) || File.Exists(path);

    private IReadOnlyList<(string Id, string Path)> KnownFolders()
    {
        if (_knownFolders != null) return _knownFolders;
        var list = new List<(string Id, string Path)>();
        foreach (var (id, _) in KnownFolderIds)
        {
            try
            {
                var guid = id;
                if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out IntPtr p) != 0) continue;
                try
                {
                    if (Marshal.PtrToStringUni(p) is { Length: > 2 } path) list.Add((id.ToString("B").ToUpperInvariant(), path));
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { /* not on this PC */ }
        }
        return _knownFolders = list;
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 2 && path.Length > folder.Length + 1 && path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Jump Lists, read through the shell (IApplicationDocumentLists): the Recent list of one AppUserModelID.</summary>
internal static class JumpLists
{
    public static IReadOnlyList<string> Recent(string appId, int max)
    {
        var paths = new List<string>();
        object? lists = null, array = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_ApplicationDocumentLists, throwOnError: false);
            if (type == null) return paths;
            lists = Activator.CreateInstance(type);
            if (lists is not IApplicationDocumentLists documents || documents.SetAppID(appId) != 0) return paths;
            var arrayId = typeof(IObjectArray).GUID;
            if (documents.GetList(ADLT_RECENT, (uint)max, ref arrayId, out array) != 0 || array is not IObjectArray items) return paths;
            if (items.GetCount(out uint count) != 0) return paths;
            var itemId = typeof(IShellItem).GUID;
            for (uint i = 0; i < Math.Min(count, (uint)max); i++)
            {
                if (items.GetAt(i, ref itemId, out object item) != 0) continue;
                try
                {
                    if (item is IShellItem shellItem && shellItem.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name) == 0 && name != IntPtr.Zero)
                    {
                        try { if (Marshal.PtrToStringUni(name) is { } path) paths.Add(path); }
                        finally { Marshal.FreeCoTaskMem(name); }
                    }
                }
                finally { Release(item); }
            }
        }
        catch (COMException) { /* no Jump List for this ID */ }
        catch (InvalidCastException) { }
        finally
        {
            Release(array);
            Release(lists);
        }
        return paths;
    }

    internal static void Release(object? o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
        catch { /* already gone */ }
    }
}

/// <summary>
/// The Start menu's apps (shell:AppsFolder): name, AppUserModelID and program, so an app's Jump List can be found
/// from the program a shortcut starts. Listing them takes the shell up to a second, so the list is kept: an opening
/// more than <see cref="KeepFor"/> later uses it once more and reads it again afterwards, on the same reader thread
/// (never on a timer). Forgotten when recent files or the launcher are turned off.
/// </summary>
internal static class AppsIndex
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    private const int MaxApps = 3000;
    private const int MaxMs = 3000;
    private static readonly object Gate = new();
    private static IReadOnlyList<AppEntry>? _apps;
    private static DateTime _read;

    public static IReadOnlyList<AppEntry> Get()
    {
        lock (Gate)
        {
            if (_apps != null) return _apps;
            _apps = Read();
            _read = DateTime.UtcNow;
            return _apps;
        }
    }

    /// <summary>After an opening's recent files are in: reads the list again if it's old (an app installed since).</summary>
    public static void RefreshIfOld()
    {
        lock (Gate)
        {
            if (_apps == null || DateTime.UtcNow - _read < KeepFor) return;
        }
        var fresh = Read();
        lock (Gate)
        {
            if (_apps == null) return; // turned off meanwhile
            _apps = fresh;
            _read = DateTime.UtcNow;
        }
    }

    /// <summary>Forgets the list (the launcher was turned off).</summary>
    public static void Clear()
    {
        lock (Gate) _apps = null;
    }

    private static IReadOnlyList<AppEntry> Read()
    {
        var apps = new List<AppEntry>();
        IShellItem2? folder = null;
        object? enumerator = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var itemId = typeof(IShellItem2).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref itemId, out folder) != 0 || folder == null) return apps;
            var handler = BHID_EnumItems;
            var enumId = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref handler, ref enumId, out enumerator) != 0 || enumerator is not IEnumShellItems items) return apps;
            while (apps.Count < MaxApps && clock.ElapsedMilliseconds < MaxMs && items.Next(1, out IShellItem2 item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    string? id = Text(item, PKEY_AppUserModel_ID);
                    if (string.IsNullOrEmpty(id)) continue;
                    string? target = Text(item, PKEY_Link_TargetParsingPath);
                    string name = item.GetDisplayName(SIGDN_NORMALDISPLAY, out IntPtr n) == 0 && n != IntPtr.Zero ? Take(n) ?? "" : "";
                    apps.Add(new AppEntry(name, id, string.IsNullOrEmpty(target) ? null : target));
                }
                finally { JumpLists.Release(item); }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        finally
        {
            JumpLists.Release(enumerator);
            JumpLists.Release(folder);
        }
        return apps;
    }

    private static string? Text(IShellItem2 item, PROPERTYKEY key)
    {
        var k = key;
        return item.GetString(ref k, out IntPtr value) == 0 && value != IntPtr.Zero ? Take(value) : null;
    }

    private static string? Take(IntPtr p)
    {
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}

/// <summary>
/// A Store app's execution alias (%LOCALAPPDATA%\Microsoft\WindowsApps\notepad.exe): a reparse point that names
/// the app's AppUserModelID. Read with FSCTL_GET_REPARSE_POINT; nothing is started.
/// </summary>
internal static class ExecutionAlias
{
    /// <param name="target">The program the alias starts.</param>
    public static string? AppIdOf(string aliasPath, out string? target)
    {
        target = null;
        try
        {
            if (!File.Exists(aliasPath)) return null;
            using var handle = CreateFile(aliasPath, FILE_READ_ATTRIBUTES, FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out uint length, IntPtr.Zero)) return null;
            return Parse(buffer.AsSpan(0, (int)Math.Min(length, (uint)buffer.Length)), out target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// REPARSE_DATA_BUFFER for IO_REPARSE_TAG_APPEXECLINK: tag, length, reserved, then a version and four
    /// NUL-terminated UTF-16 strings: package family, AppUserModelID, program, type. The second is the one.
    /// </summary>
    public static string? Parse(ReadOnlySpan<byte> data) => Parse(data, out _);

    public static string? Parse(ReadOnlySpan<byte> data, out string? target)
    {
        target = null;
        if (data.Length < 16 || BitConverter.ToUInt32(data[..4]) != IO_REPARSE_TAG_APPEXECLINK) return null;
        int end = Math.Min(data.Length, 8 + BitConverter.ToUInt16(data.Slice(4, 2)));
        var text = data[12..end];
        var strings = new List<string>();
        int start = 0;
        for (int i = 0; i + 1 < text.Length && strings.Count < 4; i += 2)
        {
            if (text[i] != 0 || text[i + 1] != 0) continue;
            strings.Add(Encoding.Unicode.GetString(text[start..i]));
            start = i + 2;
        }
        string? id = strings.Count >= 2 ? strings[1] : null;
        if (strings.Count >= 3 && strings[2].Length is > 3 and <= 1024 && !strings[2].Any(char.IsControl)) target = strings[2];
        return id is { Length: > 0 and <= 256 } && id.Contains('!') && !id.Any(char.IsControl) ? id : null;
    }
}

/// <summary>Read-only registry access for <see cref="AppMru"/> and the Windows settings above.</summary>
internal sealed class RegistryView : IRegistryView
{
    private static RegistryKey Root(RegHive hive) => hive == RegHive.CurrentUser ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;

    public IReadOnlyList<string> SubKeys(RegHive hive, string path)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch { return []; }
    }

    public IReadOnlyList<string> ValueNames(RegHive hive, string path)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: false);
            return key?.GetValueNames() ?? [];
        }
        catch { return []; }
    }

    public object? Value(RegHive hive, string path, string name)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: false);
            return key?.GetValue(name) switch
            {
                string s => s,
                int i => i,
                long l => l,
                _ => null,
            };
        }
        catch { return null; }
    }
}

/// <summary>Which program Windows opens a kind of file with (AssocQueryString: its default "open").</summary>
internal static class FileAssociations
{
    public static string? ProgramFor(string document)
    {
        try
        {
            string ext = WinPath.Extension(document);
            if (ext.Length < 2) return null;
            uint length = 1024;
            var output = new StringBuilder((int)length);
            int hr = AssocQueryString(ASSOCF_NOTRUNCATE, ASSOCSTR_EXECUTABLE, ext, "open", output, ref length);
            return hr == 0 && output.Length > 0 ? output.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The launcher's file probe on Windows: <see cref="RealFileProbe"/>, plus what kind of file opens with what.</summary>
internal sealed class DesktopFileProbe : RealFileProbe, IFileProbe
{
    public static new readonly DesktopFileProbe Instance = new();

    public string? DefaultProgramFor(string document) => FileAssociations.ProgramFor(document);
}
