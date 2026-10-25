using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Tack.Interop;

/// <summary>
/// A glide that runs in step with something else on screen (a row folding away): it started at
/// <see cref="StartMs"/> on the animator's clock and lasts <see cref="DurationMs"/>, eased in or out the same way.
/// </summary>
internal readonly record struct GlideTiming(double StartMs, double DurationMs, bool EaseIn);

/// <summary>What a <see cref="WindowBoundsAnimator"/> moves, and its clock: the real window, or a stand-in in tests.</summary>
internal interface IGlideWindow
{
    /// <summary>Where the window is (physical pixels).</summary>
    Int32Rect Rect { get; }

    /// <summary>One step. Resizing a WPF window renders it at once, and that render raises a frame of its own inside this call.</summary>
    void Place(Int32Rect r);

    /// <summary>The last step: exactly there, at the monitor's DPI.</summary>
    void Land(Int32Rect r);

    /// <summary>Milliseconds on a steady clock.</summary>
    double NowMs { get; }

    /// <summary>Starts or stops calling <paramref name="frame"/> once per rendered frame.</summary>
    void Frames(EventHandler frame, bool on);

    /// <summary>How long one refresh of the window's display takes (ms).</summary>
    double RefreshMs { get; }
}

/// <summary>
/// Glides a window between two physical-pixel rectangles. Each edge is interpolated on its own,
/// so an edge that doesn't move (the corner nearest the orb) stays perfectly still.
/// A glide's clock starts on its first frame, once a first one-pixel step has landed, so the whole
/// ease-out is on screen. Retargeting mid-flight continues smoothly from wherever the window is and
/// keeps to the time that's left. A glide <see cref="GlideTiming">in step</see> with something else
/// follows that thing's own clock and curve.
/// <para>
/// Pace: each step resizes the window, and WPF renders a resized window inside that call, which is a
/// frame in its own right. Stepping from inside it would set off the next step at once (a burst, if
/// those renders take no time), so it's only noted; once the step returns, the next one follows
/// straight away if a refresh's worth of time (or nearly) has gone by, so in software the glide moves
/// at the pace the window can be drawn, never faster than the display, and each step costs one render.
/// </para>
/// </summary>
internal sealed class WindowBoundsAnimator
{
    public const double DurationMs = 180;

    /// <summary>A glide sent somewhere new with less time than this left gets this long to get there.</summary>
    public const double MinRetargetMs = 120;

    /// <summary>A retarget this small (a rounding pixel or two) never changes a glide's timing.</summary>
    private const int NudgePx = 3;

    private readonly IGlideWindow _window;
    private double _fromLeft, _fromTop, _fromRight, _fromBottom; // where the current curve starts (it can lie outside the window's path)
    private Int32Rect _to;
    private double _start = double.NaN; // NaN: waiting for the glide's first frame
    private double _duration;
    private bool _easeIn;
    private bool _placing;
    private bool _rendered; // a frame was drawn inside the last step's resize
    private double _steppedAt = double.NegativeInfinity; // when the last step was taken
    private double _minStepMs; // steps never come closer than this: most of one refresh of the display
    private double _shownAt; // the moment on the glide's clock that the window's last step stands for

    public WindowBoundsAnimator(Visual root, IntPtr hwnd) : this(new HwndGlideWindow(root, hwnd)) { }

    internal WindowBoundsAnimator(IGlideWindow window) => _window = window;

    public bool IsRunning { get; private set; }

    /// <summary>Where the window is heading (or is, when idle).</summary>
    public Int32Rect Target => IsRunning ? _to : _window.Rect;

    /// <summary>Glides to <paramref name="target"/>: on its own ease-out, or <paramref name="inStep"/> with something else.</summary>
    public void AnimateTo(Int32Rect target, GlideTiming? inStep = null)
    {
        var current = _window.Rect;
        if (!IsRunning && current == target) return;
        if (IsRunning && _to == target) return;

        double now = _window.NowMs;
        // Where the window is stands for the moment of its last step (or now, before it has moved).
        double shown = IsRunning && !double.IsNaN(_start) ? _shownAt : now;
        if (inStep is { } s)
        {
            // Follow the other thing's clock and curve, picking up from wherever the window is.
            _start = s.StartMs;
            _duration = Math.Max(1, s.DurationMs);
            _easeIn = s.EaseIn;
            Aim(current, target, Ease(Progress(shown)));
        }
        else if (IsRunning && !double.IsNaN(_start))
        {
            double left = _start + _duration - now;
            if (left >= MinRetargetMs || IsNudge(_to, target))
            {
                // The same curve and the same end, aimed at the new target from where the window is.
                Aim(current, target, Ease(Progress(shown)));
            }
            else
            {
                // Too little time left for the new distance: a fresh ease-out from here. (For a cubic
                // ease-out that is exactly what's left of the old curve, so the speed doesn't jump.)
                _start = shown;
                _duration = Math.Max(left, MinRetargetMs);
                _easeIn = false;
                Aim(current, target, 0);
            }
        }
        else
        {
            // A new glide, or one still waiting for its first frame: its clock starts on that frame.
            _start = double.NaN;
            _duration = DurationMs;
            _easeIn = false;
            Aim(current, target, 0);
        }

        _to = target;
        if (IsRunning) return;
        IsRunning = true;
        _minStepMs = 0.8 * _window.RefreshMs;
        _window.Frames(OnFrame, true);
    }

    public void JumpTo(Int32Rect target)
    {
        Stop();
        _window.Land(target);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _window.Frames(OnFrame, false);
    }

    private double Progress(double now) => double.IsNaN(_start) ? 0 : Math.Clamp((now - _start) / _duration, 0, 1);

    private double Ease(double t) => _easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3);

    private static bool IsNudge(Int32Rect a, Int32Rect b) =>
        Math.Abs(a.X - b.X) <= NudgePx && Math.Abs(a.Y - b.Y) <= NudgePx &&
        Math.Abs(a.X + a.Width - (b.X + b.Width)) <= NudgePx && Math.Abs(a.Y + a.Height - (b.Y + b.Height)) <= NudgePx;

    /// <summary>
    /// Sets the curve's start so that, <paramref name="k"/> of the way along it, the window is at
    /// <paramref name="current"/>: the glide carries on from there to <paramref name="target"/> without a jump.
    /// </summary>
    private void Aim(Int32Rect current, Int32Rect target, double k)
    {
        double From(double now, double to) => k >= 0.999 ? now : (now - to * k) / (1 - k);
        _fromLeft = From(current.X, target.X);
        _fromTop = From(current.Y, target.Y);
        _fromRight = From(current.X + current.Width, target.X + target.Width);
        _fromBottom = From(current.Y + current.Height, target.Y + target.Height);
    }

    private Int32Rect At(double k)
    {
        int Lerp(double a, int b) => (int)Math.Round(a + (b - a) * k);
        int left = Lerp(_fromLeft, _to.X);
        int top = Lerp(_fromTop, _to.Y);
        int right = Lerp(_fromRight, _to.X + _to.Width);
        int bottom = Lerp(_fromBottom, _to.Y + _to.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private void Place(Int32Rect r)
    {
        _placing = true;
        _rendered = false;
        _steppedAt = _window.NowMs;
        try { _window.Place(r); }
        finally { _placing = false; }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsRunning) return;
        if (_placing)
        {
            // A frame drawn inside a step's resize: stepping from here would set off the next step at once,
            // and the glide would run through in a burst. It's noted, for the loop below.
            _rendered = true;
            return;
        }
        Step();
        // That step was drawn as it was taken (a frame of its own): the next follows it here, at the pace
        // the window can be drawn, but never sooner than most of a refresh after the last, so never in a
        // burst, and never from inside a render. Otherwise (no frame drawn, or too soon) the next frame takes it.
        for (int i = 0; i < 1000 && IsRunning && _rendered && _window.NowMs - _steppedAt >= _minStepMs; i++)
            Step();
    }

    private void Step()
    {
        _rendered = false;
        if (double.IsNaN(_start))
        {
            // A glide is asked for after a layout pass, and its first frame can come tens of ms later.
            // Starting the clock only now, once a first one-pixel step has landed (which also pays for
            // the first resize), keeps that wait from eating the start of the curve.
            var current = _window.Rect;
            int Toward(int a, int b) => a + Math.Sign(b - a);
            int left = Toward(current.X, _to.X), top = Toward(current.Y, _to.Y);
            int right = Toward(current.X + current.Width, _to.X + _to.Width), bottom = Toward(current.Y + current.Height, _to.Y + _to.Height);
            var first = new Int32Rect(left, top, right - left, bottom - top);
            if (first != _to) Place(first);
            Aim(first, _to, 0);
            _start = _shownAt = _window.NowMs;
            if (first == _to) Finish();
            return;
        }

        double now = _window.NowMs;
        double t = Progress(now);
        if (t >= 1)
        {
            Finish();
            return;
        }

        var r = At(Ease(t));
        _shownAt = now;
        if (r != _window.Rect) Place(r);
    }

    private void Finish()
    {
        Stop();
        _window.Land(_to);
    }

    /// <summary>The real window: SetWindowPos, a Stopwatch, and WPF's per-frame Rendering event.</summary>
    private sealed class HwndGlideWindow(Visual root, IntPtr hwnd) : IGlideWindow
    {
        public Int32Rect Rect => ScreenGeometry.GetWindowRect(hwnd);

        public void Place(Int32Rect r) => ScreenGeometry.Place(hwnd, r.X, r.Y, r.Width, r.Height);

        public void Land(Int32Rect r) => ScreenGeometry.PlaceExact(root, hwnd, r);

        public double NowMs => Clock;

        public double RefreshMs => ScreenGeometry.RefreshMs(hwnd);

        public void Frames(EventHandler frame, bool on)
        {
            if (on) CompositionTarget.Rendering += frame;
            else CompositionTarget.Rendering -= frame;
        }
    }

    /// <summary>The window's clock (ms), for timing a glide in step with something that starts now.</summary>
    public static double Clock => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
