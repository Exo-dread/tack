using System.Windows;
using static Tack.Interop.NativeMethods;

namespace Tack.Interop;

/// <summary>A monitor's bounds, work area (excluding the taskbar) and scale, all in physical pixels.</summary>
internal readonly record struct MonitorArea(Int32Rect Bounds, Int32Rect Work, double Scale)
{
    public int Px(double dip) => (int)Math.Round(dip * Scale);
}

/// <summary>
/// Multi-monitor helpers. Everything here works in physical pixels so that window
/// placement is exact regardless of each monitor's DPI scaling.
/// </summary>
internal static class ScreenGeometry
{
    public static MonitorArea FromPoint(int x, int y) =>
        FromHandle(MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST));

    public static MonitorArea FromRect(Int32Rect r)
    {
        var rect = new RECT(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
        return FromHandle(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
    }

    public static MonitorArea FromWindow(IntPtr hwnd) =>
        FromHandle(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    private static MonitorArea FromHandle(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = 1.0;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        return new MonitorArea(ToInt32Rect(info.rcMonitor), ToInt32Rect(info.rcWork), scale);
    }

    /// <summary>
    /// What the screen shows in a rectangle (physical pixels), as DWM composed it: a window's acrylic included.
    /// Null if it can't be read. (No CAPTUREBLT: that would make the pointer flicker, and Tack's own panel isn't layered.)
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? Capture(Int32Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0 || r.Width > 16384 || r.Height > 16384) return null;
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr dc = NativeMethods.CreateCompatibleDC(screen), bitmap = NativeMethods.CreateCompatibleBitmap(screen, r.Width, r.Height);
        try
        {
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            IntPtr old = NativeMethods.SelectObject(dc, bitmap);
            bool copied = NativeMethods.BitBlt(dc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(dc, old);
            if (!copied) return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception) { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (dc != IntPtr.Zero) NativeMethods.DeleteDC(dc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>How long one refresh of the display a window is on takes (ms); 60 Hz if Windows doesn't say.</summary>
    public static double RefreshMs(IntPtr hwnd)
    {
        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var mode = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        bool known = NativeMethods.GetMonitorInfoEx(monitor, ref info)
            && NativeMethods.EnumDisplaySettings(info.szDevice, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode)
            && mode.dmDisplayFrequency is >= 24 and <= 500;
        return 1000.0 / (known ? mode.dmDisplayFrequency : 60);
    }

    public static Int32Rect GetWindowRect(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var r);
        return ToInt32Rect(r);
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>Moves (and optionally resizes) a window in physical pixels without activating it.</summary>
    public static void Place(IntPtr hwnd, int x, int y, int width = 0, int height = 0)
    {
        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (width <= 0 || height <= 0) flags |= SWP_NOSIZE;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
    }

    /// <summary>
    /// Places a WPF window at an exact physical rectangle and makes sure WPF renders it at the
    /// DPI of the monitor it ends up on. WPF applies DPI changes for hidden windows late (on Show)
    /// and can miss them when monitors are rearranged; if its scale disagrees with Windows, we
    /// replay WM_DPICHANGED with the rectangle we actually want.
    /// </summary>
    public static void PlaceExact(System.Windows.Media.Visual root, IntPtr hwnd, Int32Rect r)
    {
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        uint actual = GetDpiForWindow(hwnd);
        double wpf = System.Windows.Media.VisualTreeHelper.GetDpi(root).PixelsPerInchX;
        if (actual == 0 || Math.Abs(wpf - actual) < 0.5) return;

        var rect = new RECT(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
        SendMessage(hwnd, WM_DPICHANGED, new IntPtr((actual << 16) | actual), ref rect);
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Keeps a rectangle fully inside the given work area (with an optional inset).</summary>
    public static Int32Rect Clamp(Int32Rect r, Int32Rect work, int inset = 0)
    {
        int minX = work.X + inset, minY = work.Y + inset;
        int maxX = work.X + work.Width - inset - r.Width;
        int maxY = work.Y + work.Height - inset - r.Height;
        int x = maxX < minX ? minX : Math.Clamp(r.X, minX, maxX);
        int y = maxY < minY ? minY : Math.Clamp(r.Y, minY, maxY);
        return new Int32Rect(x, y, r.Width, r.Height);
    }

    public static bool Contains(Int32Rect r, int x, int y) =>
        x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    private static Int32Rect ToInt32Rect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
