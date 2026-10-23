namespace Tack.Launcher.Core;

/// <summary>What Tack can see of another app's top-level window, to decide if it's that app's main window.</summary>
internal readonly record struct WindowTraits(
    bool Visible, bool IsRoot, bool HasOwner, bool ToolWindow, bool HasFrame, bool Cloaked, double Width, double Height);

/// <summary>Pure rules for moving a launched app's window to the chosen display (best effort).</summary>
internal static class Placement
{
    /// <summary>Smaller than this is a splash screen, a toast or a helper window, not a main window (physical pixels at 100%).</summary>
    public const double MinMainWidth = 240, MinMainHeight = 160;

    /// <summary>
    /// A main window: visible, top-level, unowned, not a tool window, with a frame (caption or resizable
    /// border), and a sensible size. Splash screens are usually frameless popups, so they're skipped (and
    /// the wait for the real window continues).
    /// </summary>
    public static bool LooksLikeMainWindow(WindowTraits t, double scale = 1) =>
        t.Visible && t.IsRoot && !t.HasOwner && !t.ToolWindow && !t.Cloaked && t.HasFrame &&
        t.Width >= MinMainWidth * scale && t.Height >= MinMainHeight * scale;

    /// <summary>True when a window's centre is on the display.</summary>
    public static bool IsOn(PxRect window, Display d) => d.Bounds.Contains(window.Center);

    /// <summary>The display a window is (mostly) on: the one containing its centre, else the one it overlaps most.</summary>
    public static int DisplayOf(PxRect window, IReadOnlyList<Display> displays)
    {
        for (int i = 0; i < displays.Count; i++) if (IsOn(window, displays[i])) return i;
        int best = -1;
        double area = 0;
        for (int i = 0; i < displays.Count; i++)
        {
            double a = window.OverlapArea(displays[i].Bounds);
            if (a > area) { area = a; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Where a (normal, not maximised) window goes on another display: the same relative position in the work
    /// area (a window on the left stays on the left), resized by the displays' scale ratio so it looks the same
    /// size, and never larger than the target's work area. This is where it should end up; what to ask Windows
    /// for depends on the app (<see cref="Request"/>).
    /// </summary>
    public static PxRect MoveTo(PxRect window, Display from, Display to)
    {
        double ratio = from.Scale > 0 ? to.Scale / from.Scale : 1;
        double w = Math.Min(window.Width * ratio, to.Work.Width);
        double h = Math.Min(window.Height * ratio, to.Work.Height);

        static double Fraction(double pos, double start, double free) => free > 0 ? Math.Clamp((pos - start) / free, 0, 1) : 0.5;
        double fx = Fraction(window.X, from.Work.X, from.Work.Width - window.Width);
        double fy = Fraction(window.Y, from.Work.Y, from.Work.Height - window.Height);

        double x = to.Work.X + fx * (to.Work.Width - w);
        double y = to.Work.Y + fy * (to.Work.Height - h);
        return new PxRect(Math.Round(x), Math.Round(y), Math.Round(w), Math.Round(h));
    }

    /// <summary>
    /// For a maximised or minimised window: a restored rectangle centred in the target's work area (Windows then
    /// maximises it there). Keeps the restored size, scaled and clamped like <see cref="MoveTo"/>.
    /// </summary>
    public static PxRect RestoredOn(PxRect restored, Display from, Display to)
    {
        double ratio = from.Scale > 0 ? to.Scale / from.Scale : 1;
        double w = Math.Min(Math.Max(restored.Width * ratio, MinMainWidth), to.Work.Width);
        double h = Math.Min(Math.Max(restored.Height * ratio, MinMainHeight), to.Work.Height);
        return new PxRect(Math.Round(to.Work.X + (to.Work.Width - w) / 2), Math.Round(to.Work.Y + (to.Work.Height - h) / 2), Math.Round(w), Math.Round(h));
    }

    /// <summary>
    /// What to ask Windows for so a window ends up at <paramref name="intended"/> (from <see cref="MoveTo"/> or
    /// <see cref="RestoredOn"/>). An app that's per-monitor DPI aware (most current ones: Explorer, Notepad,
    /// browsers, Office, WPF and Electron apps) resizes itself by the scale ratio when it arrives on a display
    /// with another scale (WM_DPICHANGED, keeping its top-left corner), so it's given its size before that
    /// rescale; scaling it here as well would scale it twice. Other windows are given the intended size.
    /// </summary>
    public static PxRect Request(PxRect intended, Display from, Display to, bool appRescales)
    {
        double ratio = from.Scale > 0 ? to.Scale / from.Scale : 1;
        if (!appRescales || Math.Abs(ratio - 1) < 0.001) return intended;
        return new PxRect(intended.X, intended.Y, Math.Round(intended.Width / ratio), Math.Round(intended.Height / ratio));
    }

    /// <summary>
    /// True when a window that's on the right display didn't end up the size Tack meant (an app that rescaled
    /// itself differently, or not at all): worth one correction. A few pixels either way don't count.
    /// </summary>
    public static bool SizeIsOff(PxRect actual, PxRect intended) =>
        Math.Abs(actual.Width - intended.Width) > Math.Max(4, intended.Width * 0.03) ||
        Math.Abs(actual.Height - intended.Height) > Math.Max(4, intended.Height * 0.03);

    /// <summary>
    /// Retry delays (ms after the window was first moved) for checking that the app didn't move itself back
    /// (many restore a saved position right after showing). Bounded: three checks, then Tack lets go.
    /// </summary>
    public static readonly int[] VerifyDelaysMs = [350, 900, 2000];

    /// <summary>How long Tack watches for the new window: 15 s, extended once to 60 s if the app shows a splash screen first.</summary>
    public const int ObserveMs = 15_000, ObserveSlowAppMs = 60_000;
}
