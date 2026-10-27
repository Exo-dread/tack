using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Interop;
using static Tack.Interop.NativeMethods;

namespace Tack.Windows;

/// <summary>
/// Opening and folding the panel the way a phone opens an app: the panel grows out of the orb (from <see cref="Small"/>
/// to full size about the orb's centre, fading in, easing out), and folds back into it a little quicker, easing in.
///
/// DWM does the moving. Software rendering can't scale the live panel smoothly (a full-panel frame is ~13 ms at 150%),
/// and DWM draws the panel's glass, shadow and corners for the window's own rectangle, which can't be scaled at all. So
/// the panel stays put, cloaked (shown, laid out, drawn and focused, but not on screen), and a DWM thumbnail of it (a live
/// picture DWM draws, scales and fades itself) moves in a click-through window of its own over it. A short-lived thread
/// gives DWM the thumbnail's size and opacity once per refresh (waiting on <see cref="DwmFlush"/>), so the motion keeps
/// the display's own pace whatever the UI thread is doing, and costs next to nothing. Once grown, the real panel is
/// uncloaked under the thumbnail and the thumbnail goes; folding, the panel is cloaked once the thumbnail covers it, and
/// hidden when the fold is over.
///
/// DWM has no acrylic for a window that hasn't been on screen since it was shown (a thumbnail of it gets a flat grey), so
/// over frosted glass the panel grows with the theme's solid surface and the glass clears in once it's there. Folding,
/// the thumbnail shows the glass as it was on screen. Windows' animation effects off
/// (Settings â†’ Accessibility â†’ Visual effects), the panel shows and hides at once. Nothing runs or is held while still.
/// </summary>
internal sealed class Unfold
{
    /// <summary>How long an opening takes (ms).</summary>
    public const double OpenMs = 240;

    /// <summary>How long a fold takes (ms): a little quicker than opening.</summary>
    public const double FoldMs = 180;

    /// <summary>Over glass, the glass clears in this long once the panel has grown (ms).</summary>
    public const double SettleMs = 140;

    /// <summary>
    /// Folding over glass, the glass turns into the solid surface over this long (ms) before the thumbnail takes over
    /// (see <see cref="Fold"/>): a quick fade rather than a snap.
    /// </summary>
    public const double SolidFadeMs = 30;

    /// <summary>The scale the panel grows from and folds to.</summary>
    public const double Small = 0.85;

    private readonly object _gate = new();
    private UnfoldWindow? _window;
    private IntPtr _thumb;      // the thumbnail of the panel, while one is shown
    private IntPtr _source;     // the window it's of
    private int _run;           // each opening or fold; a step left over from an earlier one does nothing
    private bool _opening;      // the thumbnail on show is an opening's (a fold carries on from it)
    private IntPtr _cloaked;    // the panel, while Unfold has it cloaked
    private PanelWindow? _solid; // the panel, while it opens solid over glass
    private double _scale = Small, _opacity; // what DWM was last told (under _gate)

    /// <summary>Windows' animation effects: off, the panel shows and hides at once.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>The window the thumbnail is drawn in (for tests).</summary>
    internal UnfoldWindow? Window => _window;

    /// <summary>True while a thumbnail of the panel is shown (opening, settling or folding).</summary>
    public bool IsRunning => _thumb != IntPtr.Zero;

    /// <summary>For tests: when DWM was given each step of the motion (<see cref="Stopwatch"/> timestamps), while set.</summary>
    internal ConcurrentQueue<long>? Trace { get; set; }

    /// <summary>Makes the thumbnail's window ahead of time (hidden, empty), so the first opening doesn't wait for it.</summary>
    public void Prepare() => (_window ??= new UnfoldWindow()).EnsureHandle();

    /// <summary>
    /// Opens the panel, already placed at <paramref name="rect"/>, shown cloaked (<see cref="WindowEffects.Cloak"/>) and given
    /// the focus: it grows out of <paramref name="origin"/> (the orb's centre, physical pixels) and is uncloaked once whole.
    /// Carries on from wherever a fold still running has got to.
    /// </summary>
    public void Open(PanelWindow panel, Int32Rect rect, (int X, int Y) origin)
    {
        bool carrying = IsRunning; // a fold still under way: the panel hasn't been hidden yet
        var (scale, opacity) = carrying ? Current() : (Small, 0.0);
        int run = Interlocked.Increment(ref _run);
        _cloaked = panel.Handle;
        _opening = true;
        var place = new Placement(rect, origin);
        if (!Begin(panel.Handle, place, scale, opacity))
        {
            Cancel();
            return;
        }
        // DWM has no acrylic for a panel that hasn't been on screen since it was shown: it grows solid, and the glass
        // clears in after. (A fold still under way has the glass as it was.)
        if (!carrying)
        {
            _solid = panel;
            panel.HoldSolid();
        }
        double ms = Math.Max(120, OpenMs * (1 - scale) / (1 - Small));
        // From the panel's first frame (it was shown a moment ago), so the thumbnail never shows what the window held before.
        OnNextFrame(() =>
        {
            if (run != _run) return;
            // The content is all there well before the panel has finished growing.
            // Its clock a refresh ahead: the first step DWM shows has already begun.
            Step(run, place, ms, t => (Lerp(scale, 1, EaseOut(t)), Lerp(opacity, 1, EaseOut(Math.Min(1, t / 0.6)))), () =>
            {
                // Grown: the real panel under the thumbnail (the very same picture), then the thumbnail goes, and the glass clears in.
                Uncloak();
                _opening = false;
                DwmFlush(); // the panel is on screen
                End(run);
                ReleaseSolid(SettleMs);
            }, leadMs: 1000 / 120.0);
        });
    }

    /// <summary>
    /// Folds the panel into <paramref name="origin"/>: its thumbnail takes its place and shrinks away. The panel is cloaked
    /// once the thumbnail covers it, and <paramref name="hide"/> hides it when the fold is over (hiding moves the focus and
    /// redraws, which would cost the fold frames). An opening still running folds back from where it is.
    /// </summary>
    public void Fold(PanelWindow panel, Int32Rect rect, (int X, int Y) origin, Action hide)
    {
        long called = Stopwatch.GetTimestamp();
        int run = Interlocked.Increment(ref _run);
        var place = new Placement(rect, origin);
        void Folded()
        {
            if (run != _run) return;
            hide();
            ReleaseSolid(0);
            Uncloak();
            End(run);
        }

        if (IsRunning && _opening)
        {
            var (scale, opacity) = Current(); // the panel is still cloaked
            _opening = false;
            double ms = Math.Max(90, FoldMs * (scale - Small) / (1 - Small));
            Step(run, place, ms, t => (Lerp(scale, Small, EaseIn(t)), Lerp(opacity, 0, EaseIn(t))), Folded);
            return;
        }

        _opening = false;
        ReleaseSolid(0);
        // The thumbnail over the panel, the very same picture. DWM drops a window's acrylic once it's cloaked (its thumbnail
        // gets a flat grey): over glass, the panel fades to solid first (quickly, not a snap), and is cloaked a couple of
        // frames after that's drawn.
        bool glass = panel.IsGlassActive && !panel.IsClear;
        if (glass)
        {
            _solid = panel;
            panel.HoldSolid(SolidFadeMs);
        }
        if (!Begin(panel.Handle, place, 1, 1))
        {
            Cancel();
            hide();
            return;
        }
        _cloaked = panel.Handle; // cloaked by the motion's thread, once the thumbnail covers it
        // The fold's clock runs from now, not from when the waits are over: its start is the slow end of the curve, so
        // nothing is lost by it, and the fold doesn't seem to hesitate.
        Step(run, place, FoldMs, t => (Lerp(1, Small, EaseIn(t)), 1 - EaseIn(t)), Folded,
            waitFrames: glass ? 2 : 1, waitMs: glass ? SolidFadeMs : 0, cloak: panel.Handle, clockFrom: called);
    }

    /// <summary>Stops at once: no thumbnail, the panel uncloaked (a full-screen app, or shown or hidden without animation).</summary>
    public void Cancel()
    {
        int run = Interlocked.Increment(ref _run);
        _opening = false;
        ReleaseSolid(0);
        Uncloak();
        End(run);
    }

    // ---------------------------------------------------------------- the thumbnail

    /// <summary>The thumbnail's window over the panel, the thumbnail of <paramref name="source"/> in it, at the given scale and opacity.</summary>
    private bool Begin(IntPtr source, Placement place, double scale, double opacity)
    {
        var w = _window ??= new UnfoldWindow();
        w.EnsureHandle();
        if (!w.IsVisible) w.Show();
        var r = place.Panel;
        ScreenGeometry.Place(w.Handle, r.X, r.Y, r.Width, r.Height);
        WindowEffects.BringToTopmost(w.Handle);
        if (_thumb != IntPtr.Zero && _source != source) Unregister();
        if (_thumb == IntPtr.Zero)
        {
            if (DwmRegisterThumbnail(w.Handle, source, out IntPtr thumb) != 0) return false;
            lock (_gate)
            {
                _thumb = thumb;
                _source = source;
            }
        }
        Apply(_run, _thumb, place, scale, opacity);
        return true;
    }

    /// <summary>
    /// Plays a motion on a thread of its own: once per refresh, the thumbnail at <paramref name="at"/>(t) (t from 0 to 1 over
    /// <paramref name="ms"/>), then a wait for DWM's next frame; <paramref name="done"/> on the UI thread at the end. It starts
    /// after <paramref name="waitFrames"/> of DWM's frames (for the thumbnail to be on screen, or WPF to draw a change), with
    /// its clock <paramref name="leadMs"/> ahead. With <paramref name="waitMs"/>, the waits last at least that long too (and
    /// then <paramref name="waitFrames"/> more), for a change WPF animates. With <paramref name="clockFrom"/> (a
    /// <see cref="Stopwatch"/> timestamp), the clock runs from then rather than from the end of the waits. With
    /// <paramref name="cloak"/>, that window is cloaked as the waits end (the thumbnail covering it), so the two are never
    /// both seen. A newer motion (or Cancel) stops it.
    /// </summary>
    private void Step(int run, Placement place, double ms, Func<double, (double Scale, double Opacity)> at, Action done,
        int waitFrames = 0, double waitMs = 0, IntPtr cloak = default, double leadMs = 0, long clockFrom = 0)
    {
        var dispatcher = _window!.Dispatcher;
        IntPtr thumb = _thumb;
        var trace = Trace;
        var thread = new Thread(() =>
        {
            long waitStart = Stopwatch.GetTimestamp();
            while (waitMs > 0 && Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds < waitMs && Volatile.Read(ref _run) == run) Flush();
            for (int i = 0; i < waitFrames && Volatile.Read(ref _run) == run; i++) Flush();
            if (cloak != IntPtr.Zero)
            {
                bool cloaked;
                lock (_gate) cloaked = Volatile.Read(ref _run) != run || WindowEffects.Cloak(cloak, true);
                if (!cloaked) dispatcher.Invoke(() => { if (run == _run) WindowEffects.Cloak(cloak, true); });
            }
            long start = clockFrom != 0 ? clockFrom : Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _run) == run)
            {
                double t = Math.Min(1, (Stopwatch.GetElapsedTime(start).TotalMilliseconds + leadMs) / ms);
                var (scale, opacity) = at(t);
                if (!Apply(run, thumb, place, scale, opacity)) return;
                trace?.Enqueue(Stopwatch.GetTimestamp());
                Flush();
                if (t >= 1)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Send, () => { if (run == _run) done(); });
                    return;
                }
            }
        })
        { IsBackground = true, Name = "Tack unfold", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Waits for DWM's next frame; if it can't say (or says at once), doesn't spin.</summary>
    private static void Flush()
    {
        long before = Stopwatch.GetTimestamp();
        if (DwmFlush() != 0 || Stopwatch.GetElapsedTime(before).TotalMilliseconds < 1) Thread.Sleep(4);
    }

    /// <summary>Tells DWM where the thumbnail is and how opaque, unless a newer motion has started.</summary>
    private bool Apply(int run, IntPtr thumb, Placement place, double scale, double opacity)
    {
        lock (_gate)
        {
            if (run != Volatile.Read(ref _run) || thumb == IntPtr.Zero || thumb != _thumb) return false;
            _scale = scale;
            _opacity = opacity;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = place.At(scale),
                opacity = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255),
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            return DwmUpdateThumbnailProperties(thumb, ref props) == 0;
        }
    }

    private (double Scale, double Opacity) Current()
    {
        lock (_gate) return (_scale, _opacity);
    }

    /// <summary>The thumbnail gone and its window hidden.</summary>
    private void End(int run)
    {
        if (run != _run) return;
        _opening = false;
        Unregister();
        if (_window?.IsVisible == true) _window.Hide();
    }

    private void Unregister()
    {
        lock (_gate)
        {
            if (_thumb == IntPtr.Zero) return;
            DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
            _source = IntPtr.Zero;
        }
    }

    private void ReleaseSolid(double ms)
    {
        _solid?.ReleaseSolid(ms);
        _solid = null;
    }

    private void Uncloak()
    {
        lock (_gate) // after a motion's thread has cloaked the panel, or seen it needn't
        {
            if (_cloaked == IntPtr.Zero) return;
            WindowEffects.Cloak(_cloaked, false);
            _cloaked = IntPtr.Zero;
        }
    }

    /// <summary>Runs <paramref name="action"/> as WPF starts drawing its next frame (whatever else the UI thread has queued).</summary>
    private static void OnNextFrame(Action action)
    {
        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            action();
        }
        CompositionTarget.Rendering += OnRendering;
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    /// <summary>Decelerating (cubic), as <see cref="Controls.Anim.EaseOut"/>.</summary>
    internal static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>
    /// Accelerating (quadratic): gentler than a cubic at the start, so a fold is seen to go at once rather than after a
    /// pause, and still speeding up into the orb.
    /// </summary>
    internal static double EaseIn(double t) => t * t;

    /// <summary>The panel's rectangle (physical pixels; the thumbnail's window covers it exactly) and the point it scales about.</summary>
    internal readonly struct Placement
    {
        public Placement(Int32Rect panel, (int X, int Y) origin)
        {
            Panel = panel;
            // The orb's centre, kept on the panel: the panel never grows from outside itself.
            OriginX = Math.Clamp(origin.X, panel.X, panel.X + panel.Width) - panel.X;
            OriginY = Math.Clamp(origin.Y, panel.Y, panel.Y + panel.Height) - panel.Y;
        }

        public Int32Rect Panel { get; }

        public int OriginX { get; }

        public int OriginY { get; }

        /// <summary>Where the panel is drawn at <paramref name="scale"/>, in its window's own coordinates.</summary>
        public RECT At(double scale) => new(
            (int)Math.Round(OriginX * (1 - scale)),
            (int)Math.Round(OriginY * (1 - scale)),
            (int)Math.Round(OriginX + (Panel.Width - OriginX) * scale),
            (int)Math.Round(OriginY + (Panel.Height - OriginY) * scale));
    }
}

/// <summary>The click-through, never-activated, empty layered window the panel's thumbnail is drawn in.</summary>
internal sealed class UnfoldWindow : Window
{
    public UnfoldWindow()
    {
        Title = "Tack";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Left = Top = -32000;
        Width = Height = 1;
    }

    public IntPtr Handle { get; private set; }

    public void EnsureHandle()
    {
        if (Handle == IntPtr.Zero) Handle = new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(Handle, noActivate: true, clickThrough: true);
        WindowEffects.ExcludeFromPeek(Handle);
        WindowEffects.DisableTransitions(Handle, true);
    }

    // Only DWM's picture of the panel, which a screen reader finds in the panel itself.
    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
}
