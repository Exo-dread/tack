using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Tack.Controls;
using Tack.Interop;
using Tack.Launcher;
using static Tack.Interop.NativeMethods;

namespace Tack.Windows;

/// <summary>
/// The collapsed state: a 40 px bead that floats above everything, can be dragged anywhere,
/// snaps neatly to screen edges, and never takes keyboard focus from the app you're in.
/// </summary>
public partial class OrbWindow : Window, IFadingWindow
{
    private const double SnapDistance = 28; // DIPs
    private const double EdgeGap = 10;      // DIPs

    private IntPtr _hwnd;
    private bool _pressed, _dragging;
    private bool _launcherPress; // a Shift+press handed to the radial launcher
    private (int X, int Y) _pressCursor;
    private double _grabX, _grabY;
    private (int X, int Y)? _intendedCenter;
    private EventHandler? _snapTick;

    public event Action? OpenRequested;
    public event Action? DragStarted;
    public event Action? DragFinished;
    public event Action<bool>? HoverChanged;

    public OrbWindow()
    {
        InitializeComponent();
        Orb.MouseEnter += (_, _) => SetHot(true);
        Orb.MouseLeave += (_, _) => SetHot(false);
        Orb.MouseLeftButtonDown += OnPress;
        Orb.MouseMove += OnDrag;
        Orb.MouseLeftButtonUp += OnRelease;
        Orb.LostMouseCapture += (_, _) =>
        {
            if (_pressed) OnRelease(this, null);
            else if (_launcherPress) { _launcherPress = false; Launcher?.CancelGesture(); }
        };
    }

    /// <summary>
    /// The optional radial launcher. Null while it's turned off, and then the orb behaves exactly as it
    /// always has; when on, it only takes presses made with Shift held (ordinary clicks and drags are untouched).
    /// </summary>
    internal IOrbLauncher? Launcher { get; set; }

    public IntPtr Handle => _hwnd;

    public bool IsDragging => _dragging;

    /// <summary>Centre of the bead in physical screen pixels.</summary>
    public (int X, int Y) Center
    {
        get
        {
            var r = ScreenGeometry.GetWindowRect(_hwnd);
            return (r.X + r.Width / 2, r.Y + r.Height / 2);
        }
    }

    /// <summary>Visible diameter of the bead in physical pixels on its current monitor.</summary>
    public int DiameterPx => (int)Math.Round(OrbVisual.Diameter * ScreenGeometry.FromWindow(_hwnd).Scale);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(_hwnd, noActivate: true);
        WindowEffects.ExcludeFromPeek(_hwnd);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DPICHANGED when !_dragging && _intendedCenter is { } c:
                // WPF resizes the window for the new DPI; keep the bead's centre where we put it.
                Dispatcher.BeginInvoke(() => MoveCenterTo(c.X, c.Y));
                break;
        }
        return IntPtr.Zero;
    }

    public void Refresh(int openCount, double progress, bool allDone)
    {
        Orb.Count = openCount;
        Orb.Progress = progress;
        Orb.AllDone = allDone;
    }

    public void InvalidateAccent() => Orb.InvalidateVisual();

    /// <summary>Positions the bead so its centre sits at the given physical pixel.</summary>
    public void MoveCenterTo(int cx, int cy)
    {
        _intendedCenter = (cx, cy);
        var target = ScreenGeometry.FromPoint(cx, cy);
        int size = target.Px(Width);
        ScreenGeometry.PlaceExact(this, _hwnd, new Int32Rect(cx - size / 2, cy - size / 2, size, size));
    }

    // ---- Appearance ----

    public void PlayAppear()
    {
        Anim.Set(Orb, OpacityProperty, 0);
        Anim.Fade(Orb, 1, 160);
        Anim.Scale(Orb, 1, 260, Anim.Spring, from: 0.55);
    }

    public void PlayDisappear(Action completed)
    {
        SetHot(false);
        Anim.Fade(Orb, 0, 110);
        Anim.Scale(Orb, 0.6, 120, Anim.EaseIn, completed: completed);
    }

    /// <summary>Fade when idle: the whole window at half opacity (easing out unless <paramref name="animate"/> is false), or back at once.</summary>
    public void SetFaded(bool faded, bool animate)
    {
        if (faded == IsFaded) return;
        IsFaded = faded;
        if (!faded) Anim.Set(this, OpacityProperty, 1);
        else if (animate) Anim.Fade(this, PanelWindow.IdleOpacity, 320, from: 1);
        else Anim.Set(this, OpacityProperty, PanelWindow.IdleOpacity);
    }

    public bool IsFaded { get; private set; }

    private void SetHot(bool hot)
    {
        if (_dragging) return;
        Orb.IsHot = hot;
        Anim.Scale(Orb, hot ? 1.07 : 1.0, 140);
        HoverChanged?.Invoke(hot);
    }

    // ---- Click vs. drag ----

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (Launcher is { } launcher && launcher.WantsPress())
        {
            var at = ScreenGeometry.CursorPosition();
            _launcherPress = true;
            Orb.CaptureMouse();
            Anim.Scale(Orb, 0.94, 90);
            e.Handled = true;
            launcher.Press(at.X, at.Y);
            return;
        }
        StopSnap();
        _pressed = true;
        _pressCursor = ScreenGeometry.CursorPosition();
        var r = ScreenGeometry.GetWindowRect(_hwnd);
        _grabX = (_pressCursor.X - r.X) / (double)Math.Max(1, r.Width);
        _grabY = (_pressCursor.Y - r.Y) / (double)Math.Max(1, r.Height);
        Orb.CaptureMouse();
        Anim.Scale(Orb, 0.94, 90);
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_launcherPress)
        {
            var at = ScreenGeometry.CursorPosition();
            Launcher?.Move(at.X, at.Y);
            return;
        }
        if (!_pressed) return;
        var cursor = ScreenGeometry.CursorPosition();
        if (!_dragging)
        {
            double threshold = 4 * ScreenGeometry.FromWindow(_hwnd).Scale;
            if (Math.Abs(cursor.X - _pressCursor.X) < threshold && Math.Abs(cursor.Y - _pressCursor.Y) < threshold) return;
            _dragging = true;
            Orb.IsHot = false;
            HoverChanged?.Invoke(false);
            Anim.Scale(Orb, 1.0, 120);
            DragStarted?.Invoke();
        }
        var r = ScreenGeometry.GetWindowRect(_hwnd);
        ScreenGeometry.Place(_hwnd, cursor.X - (int)Math.Round(_grabX * r.Width), cursor.Y - (int)Math.Round(_grabY * r.Height));
    }

    private void OnRelease(object sender, MouseButtonEventArgs? e)
    {
        if (_launcherPress && e != null)
        {
            _launcherPress = false; // before releasing capture, so LostMouseCapture doesn't cancel
            if (Orb.IsMouseCaptured) Orb.ReleaseMouseCapture();
            Anim.Scale(Orb, 1.0, 120);
            var at = ScreenGeometry.CursorPosition();
            Launcher?.Release(at.X, at.Y);
            return;
        }
        if (!_pressed) return;
        _pressed = false;
        if (Orb.IsMouseCaptured) Orb.ReleaseMouseCapture();

        if (_dragging)
        {
            _dragging = false;
            SnapIntoPlace();
        }
        else
        {
            Anim.Scale(Orb, Orb.IsMouseOver ? 1.07 : 1.0, 120);
            if (e != null) OpenRequested?.Invoke();
        }
    }

    // ---- Edge snapping ----

    /// <summary>Keeps the bead fully on screen and glides it flush to a nearby edge.</summary>
    public void SnapIntoPlace(bool animate = true)
    {
        var r = ScreenGeometry.GetWindowRect(_hwnd);
        var (cx, cy) = (r.X + r.Width / 2, r.Y + r.Height / 2);
        var mon = ScreenGeometry.FromPoint(cx, cy);
        var w = mon.Work;
        int half = mon.Px(OrbVisual.Diameter) / 2;
        int gap = mon.Px(EdgeGap), snap = mon.Px(SnapDistance);

        int left = cx - half, right = cx + half, top = cy - half, bottom = cy + half;
        int tx = cx, ty = cy;
        if (left - w.X < snap) tx = w.X + gap + half;
        else if (w.X + w.Width - right < snap) tx = w.X + w.Width - gap - half;
        if (top - w.Y < snap) ty = w.Y + gap + half;
        else if (w.Y + w.Height - bottom < snap) ty = w.Y + w.Height - gap - half;

        _intendedCenter = (tx, ty);
        if (!animate || (tx == cx && ty == cy))
        {
            MoveCenterTo(tx, ty);
            DragFinished?.Invoke();
            return;
        }

        var clock = Stopwatch.StartNew();
        const double duration = 180;
        _snapTick = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
            double k = 1 - Math.Pow(1 - t, 3);
            MoveCenterTo((int)Math.Round(cx + (tx - cx) * k), (int)Math.Round(cy + (ty - cy) * k));
            if (t >= 1)
            {
                StopSnap();
                DragFinished?.Invoke();
            }
        };
        CompositionTarget.Rendering += _snapTick;
    }

    private void StopSnap()
    {
        if (_snapTick == null) return;
        CompositionTarget.Rendering -= _snapTick;
        _snapTick = null;
    }
}
