using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using static Tack.Interop.NativeMethods;

namespace Tack.Interop;

/// <summary>
/// Event-driven awareness of what the rest of the desktop is doing, with no polling:
/// foreground-window changes (WinEvent hook) and full-screen apps (app-bar notifications).
/// </summary>
internal sealed class ShellWatcher : IDisposable
{
    private readonly WinEventProc _foregroundProc; // must stay referenced while hooked
    private readonly IntPtr _hook;
    private readonly HwndSource _appBarWindow;
    private readonly uint _appBarMessage;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private bool _appBarRegistered;

    /// <summary>Raised when another application becomes the foreground window.</summary>
    public event Action? ForegroundChanged;

    /// <summary>Raised when the shell reports that a full-screen app opened or closed.</summary>
    public event Action? FullscreenChanged;

    public ShellWatcher()
    {
        _foregroundProc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _appBarMessage = RegisterWindowMessage("Tack.AppBarNotify");
        _appBarWindow = new HwndSource(new HwndSourceParameters("TackShell")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _appBarWindow.AddHook(WndProc);

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _appBarWindow.Handle,
            uCallbackMessage = _appBarMessage,
        };
        // Registering without ever calling ABM_SETPOS reserves no screen space; we only
        // want the shell's full-screen notifications.
        _appBarRegistered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_SYSTEM_FOREGROUND) ForegroundChanged?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_appBarMessage && wParam.ToInt32() == ABN_FULLSCREENAPP)
        {
            handled = true;
            FullscreenChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>True when the foreground window belongs to another process and covers the given monitor.</summary>
    public bool IsFullscreenAppOn(MonitorArea monitor)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownProcessId) return false;
        if (IsZoomed(fg)) return false; // maximised windows aren't full-screen (matters with an auto-hide taskbar)

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        string name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        var r = ScreenGeometry.GetWindowRect(fg);
        var b = monitor.Bounds;
        return r.X <= b.X && r.Y <= b.Y && r.X + r.Width >= b.X + b.Width && r.Y + r.Height >= b.Y + b.Height;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        if (_appBarRegistered)
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _appBarWindow.Handle };
            SHAppBarMessage(ABM_REMOVE, ref data);
            _appBarRegistered = false;
        }
        _appBarWindow.RemoveHook(WndProc);
        _appBarWindow.Dispose();
    }
}
