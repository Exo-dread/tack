using static Tack.Interop.NativeMethods;

namespace Tack.Interop;

/// <summary>
/// DWM and window-style helpers: Windows 11 acrylic backdrop, rounded corners,
/// tool-window behaviour and topmost handling.
/// </summary>
internal static class WindowEffects
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private static readonly int Build = Environment.OSVersion.Version.Build;

    /// <summary>Windows 11 22H2+ exposes the official system backdrop API.</summary>
    public static bool SupportsSystemBackdrop => Build >= 22621;

    /// <summary>Windows 11 rounds window corners natively.</summary>
    public static bool SupportsRoundedCorners => Build >= 22000;

    /// <summary>Applies light/dark frame, rounded corners and (optionally) the acrylic backdrop.</summary>
    /// <param name="clear">
    /// Nothing behind the panel at all (no acrylic, no frame): what's behind the window shows through its own
    /// transparency, unblurred (with <see cref="SetClear"/>). For a see-through background without glass, and
    /// a panel faded when idle.
    /// </param>
    /// <returns>True when the acrylic backdrop is active.</returns>
    public static bool ApplyPanelChrome(IntPtr hwnd, bool glass, bool dark, bool clear = false)
    {
        // Also decides whether DWM tints the acrylic dark or light.
        SetInt(hwnd, Build >= 18985 ? DWMWA_USE_IMMERSIVE_DARK_MODE : DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, dark ? 1 : 0);

        if (SupportsRoundedCorners)
            SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        if (!SupportsSystemBackdrop)
            return false;

        // The glass frame must cover the whole client area for the backdrop to show through. (Without glass
        // DWM fills that frame with the theme's solid colour, so a clear panel has none.)
        var margins = clear ? new MARGINS() : new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        glass &= !clear;
        return SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, glass ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE) && glass;
    }

    /// <summary>
    /// Has DWM show the window's own per-pixel transparency with nothing drawn behind it (blur-behind on an
    /// empty region, which since Windows 8 blurs nothing), or stop. Goes with a clear panel chrome.
    /// </summary>
    public static void SetClear(IntPtr hwnd, bool clear)
    {
        IntPtr region = clear ? CreateRectRgn(0, 0, -1, -1) : IntPtr.Zero;
        var blur = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE | (clear ? DWM_BB_BLURREGION : 0),
            fEnable = clear,
            hRgnBlur = region,
        };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        if (region != IntPtr.Zero) DeleteObject(region);
    }

    /// <summary>Sets the 1px DWM border colour (Windows 11). Pass null for the system default.</summary>
    public static void SetBorderColor(IntPtr hwnd, System.Windows.Media.Color? color)
    {
        if (!SupportsRoundedCorners) return;
        int value = color is { } c ? c.R | (c.G << 8) | (c.B << 16) : unchecked((int)0xFFFFFFFF);
        SetInt(hwnd, DWMWA_BORDER_COLOR, value);
    }

    public static void DisableTransitions(IntPtr hwnd, bool disabled) =>
        SetInt(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, disabled ? 1 : 0);

    /// <summary>Keeps the window visible when the user peeks at the desktop.</summary>
    public static void ExcludeFromPeek(IntPtr hwnd) => SetInt(hwnd, DWMWA_EXCLUDED_FROM_PEEK, 1);

    /// <summary>
    /// Hides the window from Alt+Tab and the taskbar, and optionally prevents it from
    /// taking focus when clicked (used by the orb so clicking it never steals focus).
    /// </summary>
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate = false, bool clickThrough = false)
    {
        long ex = GetExStyle(hwnd);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        if (clickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        SetExStyle(hwnd, ex);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Removes minimize/maximize/system-menu styles so Win+Up/Down and Alt+Space can't mangle the window.</summary>
    public static void StripWindowMenu(IntPtr hwnd)
    {
        long style = GetStyle(hwnd);
        style &= ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetStyle(hwnd, style);
    }

    /// <summary>Re-asserts topmost z-order without activating or moving the window.</summary>
    public static void BringToTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // DWM drops the acrylic backdrop to a flat colour whenever a window is drawn as
    // inactive. A pinned panel is inactive most of the time, so we always tell DefWindowProc
    // the frame is active. Subclassing runs ahead of WPF's own window procedure.
    private static readonly SubclassProc AlwaysActiveProc = AlwaysActiveSubclass;

    public static void KeepFrameActive(IntPtr hwnd) => SetWindowSubclass(hwnd, AlwaysActiveProc, new IntPtr(0x7AC), IntPtr.Zero);

    private static IntPtr AlwaysActiveSubclass(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_NCACTIVATE)
            return DefWindowProc(hwnd, msg, new IntPtr(1), new IntPtr(-1));
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static bool SetInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
}
