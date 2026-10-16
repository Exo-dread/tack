using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Tack.Interop;

/// <summary>
/// Glides a window between two physical-pixel rectangles. Each edge is interpolated on its own,
/// so an edge that doesn't move (the corner nearest the orb) stays perfectly still.
/// Retargeting mid-flight continues smoothly from wherever the window is.
/// </summary>
internal sealed class WindowBoundsAnimator
{
    private const double DurationMs = 180;

    private readonly Visual _root;
    private readonly IntPtr _hwnd;
    private readonly Stopwatch _clock = new();
    private Int32Rect _from, _to;

    public WindowBoundsAnimator(Visual root, IntPtr hwnd)
    {
        _root = root;
        _hwnd = hwnd;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Where the window is heading (or is, when idle).</summary>
    public Int32Rect Target => IsRunning ? _to : ScreenGeometry.GetWindowRect(_hwnd);

    public void AnimateTo(Int32Rect target)
    {
        var current = ScreenGeometry.GetWindowRect(_hwnd);
        if (!IsRunning && current == target) return;
        if (IsRunning && _to == target) return;

        _from = current;
        _to = target;
        _clock.Restart();
        if (IsRunning) return;
        IsRunning = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void JumpTo(Int32Rect target)
    {
        Stop();
        ScreenGeometry.PlaceExact(_root, _hwnd, target);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / DurationMs);
        if (t >= 1)
        {
            Stop();
            ScreenGeometry.PlaceExact(_root, _hwnd, _to);
            return;
        }

        double k = 1 - Math.Pow(1 - t, 3); // ease out
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * k);
        int left = Lerp(_from.X, _to.X);
        int top = Lerp(_from.Y, _to.Y);
        int right = Lerp(_from.X + _from.Width, _to.X + _to.Width);
        int bottom = Lerp(_from.Y + _from.Height, _to.Y + _to.Height);
        ScreenGeometry.Place(_hwnd, left, top, right - left, bottom - top);
    }
}
