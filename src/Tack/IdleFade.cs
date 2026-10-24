using System.Windows;
using System.Windows.Threading;
using Tack.Interop;
using Tack.Model;

namespace Tack;

/// <summary>A window that can fade when idle: the orb, the panel (or a stand-in in tests).</summary>
internal interface IFadingWindow
{
    bool IsVisible { get; }

    IntPtr Handle { get; }

    /// <summary>Half opacity (easing out if <paramref name="animate"/>), or back at once.</summary>
    void SetFaded(bool faded, bool animate);
}

/// <summary>
/// Settings → Look → Fade when idle (off by default). The orb, and the panel while it's pinned open, drop to
/// half opacity once the pointer has been away from them for a few seconds, and come back the moment the
/// pointer is over them or the panel is used. Never while the panel has the focus (so never while you type in
/// it), while a menu, the launcher, a dialog or the undo toast is up, or while you drag or resize.
///
/// Nothing runs while idle: a one-shot timer starts when the pointer leaves (or the panel loses the focus),
/// runs out once, and the fade is a single short animation (none if Windows' animation effects are off).
/// Off, it only ever undoes a fade.
/// </summary>
internal sealed class IdleFade
{
    /// <summary>How long the pointer must be away before Tack fades.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private readonly Settings _settings;
    private readonly IFadingWindow _orb;
    private readonly IFadingWindow _panel;
    private readonly Func<bool> _orbBusy;
    private readonly Func<bool> _panelBusy;
    private readonly Func<IntPtr, bool> _pointerOver;
    private readonly DispatcherTimer _orbTimer;
    private readonly DispatcherTimer _panelTimer;

    /// <param name="orbBusy">True while the orb mustn't fade (its menu or the launcher is open, it's being dragged).</param>
    /// <param name="panelBusy">True while the panel mustn't fade (folded, unpinned, in use, a dialog or the toast is up).</param>
    /// <param name="pointerOver">Whether the pointer is over a window (the real pointer, unless a test says).</param>
    /// <param name="delay">How long the pointer must be away (<see cref="Delay"/>, unless a test says).</param>
    public IdleFade(Settings settings, IFadingWindow orb, IFadingWindow panel, Func<bool> orbBusy, Func<bool> panelBusy,
        Func<IntPtr, bool>? pointerOver = null, TimeSpan? delay = null)
    {
        _settings = settings;
        _orb = orb;
        _panel = panel;
        _orbBusy = orbBusy;
        _panelBusy = panelBusy;
        _pointerOver = pointerOver ?? PointerOver;
        _orbTimer = new DispatcherTimer { Interval = delay ?? Delay };
        _orbTimer.Tick += (_, _) => { _orbTimer.Stop(); FadeOrb(); };
        _panelTimer = new DispatcherTimer { Interval = delay ?? Delay };
        _panelTimer.Tick += (_, _) => { _panelTimer.Stop(); FadePanel(); };
    }

    /// <summary>True while a fade is waiting to happen (for tests: nothing waits while it's off).</summary>
    public bool IsWaiting => _orbTimer.IsEnabled || _panelTimer.IsEnabled;

    private bool On => _settings.FadeWhenIdle;

    /// <summary>Windows' "Animation effects" (Settings → Accessibility → Visual effects): off, the fade is a step.</summary>
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    // ---- the orb ----

    /// <summary>The pointer came over the orb (back at once), or left it (fade in a while).</summary>
    public void OrbPointer(bool over)
    {
        if (over) OrbAwake();
        else OrbIdle();
    }

    /// <summary>The orb is in use, or about to be (the pointer, a menu, the panel opening): full opacity now.</summary>
    public void OrbAwake()
    {
        _orbTimer.Stop();
        _orb.SetFaded(false, false);
    }

    /// <summary>The orb may be idle from now on: it fades after <see cref="Delay"/> if nothing happens.</summary>
    public void OrbIdle()
    {
        _orbTimer.Stop();
        if (On) _orbTimer.Start();
    }

    private void FadeOrb()
    {
        if (!On || !_orb.IsVisible || _orbBusy() || _pointerOver(_orb.Handle)) return;
        _orb.SetFaded(true, Animate);
    }

    // ---- the panel ----

    /// <summary>The pointer came over the panel (back at once), or left it (fade in a while, if it's pinned and not in use).</summary>
    public void PanelPointer(bool over)
    {
        if (over) PanelAwake();
        else PanelIdle();
    }

    /// <summary>The panel is in use (the focus, the toast, opening): full opacity now.</summary>
    public void PanelAwake()
    {
        _panelTimer.Stop();
        _panel.SetFaded(false, false);
    }

    /// <summary>The panel may be idle from now on: it fades after <see cref="Delay"/> if nothing happens.</summary>
    public void PanelIdle()
    {
        _panelTimer.Stop();
        if (On) _panelTimer.Start();
    }

    private void FadePanel()
    {
        if (!On || !_panel.IsVisible || _panelBusy() || _pointerOver(_panel.Handle)) return;
        _panel.SetFaded(true, Animate);
    }

    // ---- the setting ----

    /// <summary>Turned on: both may fade once idle. Turned off: both back at once, and nothing waits.</summary>
    public void SettingChanged()
    {
        if (On)
        {
            OrbIdle();
            PanelIdle();
        }
        else
        {
            OrbAwake();
            PanelAwake();
        }
    }

    /// <summary>
    /// Where the pointer really is, in case a leave was missed (a window that moved, a capture): over the
    /// window, it isn't idle. Read once, when a timer runs out.
    /// </summary>
    private static bool PointerOver(IntPtr hwnd)
    {
        var (x, y) = ScreenGeometry.CursorPosition();
        return ScreenGeometry.Contains(ScreenGeometry.GetWindowRect(hwnd), x, y);
    }
}
