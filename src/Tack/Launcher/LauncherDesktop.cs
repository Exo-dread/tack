using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Tack.Launcher.Core;
using Tack.Services;
using static Tack.Interop.NativeMethods;

namespace Tack.Launcher;

/// <summary>The connected displays, from Windows (EnumDisplayMonitors), read each time the launcher opens. No caching, no polling.</summary>
internal static class DisplayInfo
{
    public static List<Display> Current()
    {
        var list = new List<Display>();
        MonitorEnumProc proc = (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(monitor, ref info))
            {
                double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
                list.Add(new Display(info.szDevice ?? "", ToRect(info.rcMonitor), ToRect(info.rcWork), scale,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    public static PxRect ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}

/// <summary>Windows' known folders (Downloads isn't in Environment.SpecialFolder).</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? Downloads()
    {
        try
        {
            var id = DownloadsId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }
}

/// <summary>
/// Opens a shortcut the way Windows normally does (ShellExecute): the program, folder or address is the thing
/// opened, and arguments are passed separately, so nothing is ever run through a command shell.
/// </summary>
internal static class ShortcutLauncher
{
    /// <returns>Null when it opened (or the user declined Windows' admin prompt); otherwise a message to show.</returns>
    public static string? Start(LaunchPlan plan, Display? target, IReadOnlyList<Display> displays, IntPtr userWindow)
    {
        bool place = target != null && plan.CanPlace;
        // Windows on screen before the launch, so the app's *new* window can be told apart from ones it already had.
        var before = place ? WindowPlacer.VisibleTopLevelWindows() : null;
        int? pid = null;
        try
        {
            var psi = new ProcessStartInfo(plan.FileName) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(plan.Arguments)) psi.Arguments = plan.Arguments;
            if (plan.WorkingDirectory != null) psi.WorkingDirectory = plan.WorkingDirectory;

            // Tack just received the click, so it may bring windows forward; pass that on so the app (or the
            // already-running instance it hands over to) can come to the front instead of flashing in the taskbar.
            AllowSetForegroundWindow(ASFW_ANY);
            using var process = Process.Start(psi);
            try { pid = process?.Id; } catch (InvalidOperationException) { /* no process handle (e.g. handed to a running app) */ }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return null; // "Do you want to allow this app…?" answered No: nothing to report.
        }
        catch (Exception ex)
        {
            Log.Error($"Launcher: couldn't open {plan.FileName}", ex);
            return ex is Win32Exception { NativeErrorCode: 2 or 3 } ? "Windows can't find this any more." : "Windows couldn't open this shortcut.";
        }

        if (place) WindowPlacer.Begin(pid, plan.ExpectedImage, target!, displays, before!, userWindow);
        return null;
    }
}

/// <summary>
/// Best-effort: moves a just-launched app's main window to the chosen display. Event-driven and bounded: two
/// WinEvent hooks (window shown, foreground changed) for at most 15 s (60 s if the app shows a splash screen
/// first), then a few checks over two seconds in case the app restores its own saved position. Never asks for
/// administrator rights; a window Windows won't let Tack move (an elevated app) is simply left where it opened.
/// </summary>
internal sealed class WindowPlacer
{
    private static readonly List<WindowPlacer> Active = new();

    private readonly WinEventProc _proc; // must stay referenced while hooked
    private readonly uint? _pid;
    private readonly string? _image;
    private readonly Display _target;
    private readonly IReadOnlyList<Display> _displays;
    private readonly HashSet<IntPtr> _before;
    private readonly IntPtr _ignore;
    private readonly Dictionary<uint, bool> _matchByPid = new();
    private readonly DispatcherTimer _timeout = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _fallback = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _verify = new(DispatcherPriority.Background);
    private readonly Stopwatch _sincePlaced = new();
    private IntPtr _showHook, _foregroundHook;
    private IntPtr _window, _existing;
    private int _verifyIndex, _corrections;
    private bool _extended, _done;

    private WindowPlacer(int? pid, string? image, Display target, IReadOnlyList<Display> displays, HashSet<IntPtr> before, IntPtr ignore)
    {
        _ignore = ignore;
        _pid = pid is int p && p > 0 ? (uint)p : null;
        _image = image;
        _target = target;
        _displays = displays;
        _before = before;
        _proc = OnWinEvent;
        _timeout.Tick += (_, _) => Finish("no matching window appeared in time");
        _fallback.Tick += (_, _) =>
        {
            _fallback.Stop();
            if (_window == IntPtr.Zero && _existing != IntPtr.Zero) Place(_existing);
        };
        _verify.Tick += (_, _) => Verify();
    }

    /// <param name="ignore">The window the user was in before opening the launcher: never mistaken for the app handing over.</param>
    public static void Begin(int? pid, string? image, Display target, IReadOnlyList<Display> displays, HashSet<IntPtr> before, IntPtr ignore)
    {
        if (pid == null && image == null) return; // nothing to recognise the window by
        var placer = new WindowPlacer(pid, image, target, displays, before, ignore);
        Active.Add(placer);
        placer.Start();
    }

    /// <summary>Stops every observation (launcher turned off, Tack quitting).</summary>
    public static void StopAll()
    {
        foreach (var p in Active.ToList()) p.Finish(null);
    }

    public static HashSet<IntPtr> VisibleTopLevelWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindowsProc proc = (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd)) set.Add(hwnd);
            return true;
        };
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return set;
    }

    private void Start()
    {
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, flags);
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags);
        if (_showHook == IntPtr.Zero && _foregroundHook == IntPtr.Zero)
        {
            Finish("couldn't watch for the window");
            return;
        }
        _timeout.Interval = TimeSpan.FromMilliseconds(Placement.ObserveMs);
        _timeout.Start();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_done || _window != IntPtr.Zero || hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;
        try
        {
            if (GetAncestor(hwnd, GA_ROOT) != hwnd || !Matches(hwnd)) return;
            Consider(hwnd, isShow: evt == EVENT_OBJECT_SHOW);
        }
        catch (Exception ex)
        {
            Log.Error("Launcher: window placement failed", ex);
            Finish(null);
        }
    }

    private void Consider(IntPtr hwnd, bool isShow)
    {
        bool isNew = !_before.Contains(hwnd);
        if (!IsMainWindow(hwnd))
        {
            // A splash screen or loader from the app: its main window may take a while (Revit, Navisworks).
            if (isShow && isNew && !_extended)
            {
                _extended = true;
                _timeout.Stop();
                _timeout.Interval = TimeSpan.FromMilliseconds(Placement.ObserveSlowAppMs);
                _timeout.Start();
            }
            return;
        }
        if (isNew)
        {
            _fallback.Stop();
            Place(hwnd);
        }
        else if (!isShow && _existing == IntPtr.Zero && hwnd != _ignore && !StartedProcessRunning())
        {
            // An existing window came forward and the process Tack started has gone (or there was none): a
            // single-instance app handing over to the copy that's already running. Give a new window a moment
            // to appear (VS Code opens one for a new folder) before settling for this one. While the started
            // process is still running it's a new instance, and the user's existing windows are left alone.
            _existing = hwnd;
            _fallback.Start();
        }
    }

    private bool StartedProcessRunning()
    {
        if (_pid is not uint pid) return false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    private static PxRect RectOf(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT r);
        return DisplayInfo.ToRect(r);
    }

    private bool Matches(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        if (_pid == pid) return true;
        if (_image == null) return false;
        if (_matchByPid.TryGetValue(pid, out bool m)) return m;
        m = ImagePath(pid) is { } path && string.Equals(Path.GetFileName(path), _image, StringComparison.OrdinalIgnoreCase);
        _matchByPid[pid] = m;
        return m;
    }

    private static string? ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private bool IsMainWindow(IntPtr hwnd)
    {
        long style = GetStyle(hwnd), ex = GetExStyle(hwnd);
        var rect = RectOf(hwnd);
        bool cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;
        var traits = new WindowTraits(
            Visible: IsWindowVisible(hwnd),
            IsRoot: (style & WS_CHILD) == 0,
            HasOwner: GetWindow(hwnd, GW_OWNER) != IntPtr.Zero,
            ToolWindow: (ex & WS_EX_TOOLWINDOW) != 0,
            HasFrame: (style & WS_CAPTION) == WS_CAPTION || (style & WS_THICKFRAME) != 0,
            Cloaked: cloaked,
            Width: rect.Width, Height: rect.Height);
        int d = Placement.DisplayOf(rect, _displays);
        return Placement.LooksLikeMainWindow(traits, d >= 0 ? _displays[d].Scale : 1);
    }

    private void Place(IntPtr hwnd)
    {
        _window = hwnd;
        _timeout.Stop();
        if (!Move()) return;
        _sincePlaced.Restart();
        _verifyIndex = 0;
        ScheduleVerify();
    }

    private void ScheduleVerify()
    {
        if (_verifyIndex >= Placement.VerifyDelaysMs.Length)
        {
            Finish(null);
            return;
        }
        double wait = Math.Max(10, Placement.VerifyDelaysMs[_verifyIndex] - _sincePlaced.Elapsed.TotalMilliseconds);
        _verify.Interval = TimeSpan.FromMilliseconds(wait);
        _verify.Start();
    }

    private void Verify()
    {
        _verify.Stop();
        _verifyIndex++;
        if (!IsWindow(_window)) { Finish(null); return; }
        if (!Placement.IsOn(RectOf(_window), _target))
        {
            // The app put itself back (a saved position). Try again, twice at most.
            if (++_corrections > 2 || !Move()) { Finish("the app keeps its own position"); return; }
        }
        ScheduleVerify();
    }

    /// <returns>False when Windows refused (usually an app running as administrator); placement then stops.</returns>
    private bool Move()
    {
        if (!IsWindow(_window)) { Finish(null); return false; }
        var rect = RectOf(_window);
        if (Placement.IsOn(rect, _target)) return true;
        int fromIndex = Placement.DisplayOf(rect, _displays);
        var from = fromIndex >= 0 ? _displays[fromIndex] : _target;

        bool ok;
        bool maximized = IsWindowZoomed(_window), minimized = IsIconic(_window);
        if (maximized || minimized)
        {
            // Give it a restored rectangle on the target, then maximise it there if it was maximised.
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            ok = GetWindowPlacement(_window, ref wp);
            if (ok)
            {
                var (dx, dy) = WorkspaceOffset();
                var normal = new PxRect(wp.rcNormalPosition.Left + dx, wp.rcNormalPosition.Top + dy, wp.rcNormalPosition.Width, wp.rcNormalPosition.Height);
                var t = Placement.RestoredOn(normal, from, _target);
                wp.rcNormalPosition = new RECT((int)(t.X - dx), (int)(t.Y - dy), (int)(t.Right - dx), (int)(t.Bottom - dy));
                bool reMaximize = maximized || (minimized && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0);
                wp.showCmd = SW_SHOWNORMAL;
                ok = SetWindowPlacement(_window, ref wp);
                if (ok && reMaximize) ShowWindow(_window, SW_SHOWMAXIMIZED);
            }
        }
        else
        {
            var t = Placement.MoveTo(rect, from, _target);
            ok = SetWindowPos(_window, IntPtr.Zero, (int)t.X, (int)t.Y, (int)t.Width, (int)t.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            Finish(error == 5 ? "Windows didn't allow moving it (the app may be running as administrator)" : $"moving it failed (error {error})");
            return false;
        }
        return true;
    }

    /// <summary>
    /// WINDOWPLACEMENT uses workspace coordinates: relative to the main display's work area, so they differ
    /// from screen coordinates when the taskbar is on the top or left. This converts workspace → screen.
    /// </summary>
    private (double Dx, double Dy) WorkspaceOffset()
    {
        var primary = _displays.FirstOrDefault(d => d.Primary);
        return primary == null ? (0, 0) : (primary.Work.X - primary.Bounds.X, primary.Work.Y - primary.Bounds.Y);
    }

    private void Finish(string? why)
    {
        if (_done) return;
        _done = true;
        _timeout.Stop();
        _fallback.Stop();
        _verify.Stop();
        if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _showHook = _foregroundHook = IntPtr.Zero;
        Active.Remove(this);
        if (why != null) Log.Info($"Launcher: left {_image ?? "the app"} where it opened: {why}.");
    }
}
