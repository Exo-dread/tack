using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Windows;
using static Tack.Interop.NativeMethods;

namespace Tack;

/// <summary>
/// Coordinates Tack's two states — the floating orb and the expanded panel — and everything
/// around them: placement across monitors, the global shortcut, full-screen courtesy and saving.
/// </summary>
internal sealed class AppShell : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const double PanelInset = 8;          // keep the panel this far inside the work area (DIPs)
    private const double MaxAutoHeight = 620;     // an auto-fitting Tasks panel stops growing here, then scrolls
    private const double MaxAutoScreenShare = 0.75; // ...or at this share of the screen's height, if smaller

    private readonly Workspace _ws;
    private readonly StateStore _store;
    private readonly OrbWindow _orb;
    private readonly PeekWindow _peek;
    private readonly PanelWindow _panel;
    private readonly HotkeyManager _hotkey;
    private readonly ShellWatcher _watcher;
    private readonly DispatcherTimer _peekDelay;
    private readonly DispatcherTimer _collapseCheck;
    private readonly DispatcherTimer _trimDelay;
    private readonly DispatcherTimer _midnight;
    private readonly DispatcherTimer _fullscreenRecheck;
    private int _rechecksLeft;

    private WindowBoundsAnimator? _panelBounds;
    private bool _expanded;
    private bool _hiddenForFullscreen;
    private bool _dialogOpen;
    private bool _anchorRight, _anchorBottom;

    public AppShell(Workspace ws, StateStore store)
    {
        _ws = ws;
        _store = store;

        _orb = new OrbWindow();
        _peek = new PeekWindow();
        _panel = new PanelWindow(ws, this);
        _hotkey = new HotkeyManager();
        _watcher = new ShellWatcher();

        _peekDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _peekDelay.Tick += (_, _) => { _peekDelay.Stop(); ShowPeek(); };

        _collapseCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _collapseCheck.Tick += (_, _) => { _collapseCheck.Stop(); CollapseIfAbandoned(); };

        _trimDelay = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(4) };
        _trimDelay.Tick += (_, _) => { _trimDelay.Stop(); TrimMemory(); };

        _midnight = new DispatcherTimer();
        _midnight.Tick += (_, _) => { _ws.RefreshDay(); ScheduleMidnight(); };

        // Apps often go full-screen a moment after they take focus, so look again shortly after.
        _fullscreenRecheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fullscreenRecheck.Tick += (_, _) => { EvaluateFullscreen(); if (--_rechecksLeft <= 0) _fullscreenRecheck.Stop(); };

        _orb.OpenRequested += Expand;
        _orb.HoverChanged += OnOrbHover;
        _orb.DragStarted += () => { _peekDelay.Stop(); _peek.Dismiss(); };
        _orb.DragFinished += RememberOrbPosition;

        _panel.Deactivated += (_, _) => { if (_expanded && !_ws.Settings.Pinned) _collapseCheck.Start(); };
        _panel.UserMovedOrResized += RememberAnchorFromPanel;
        _panel.SizeMoveStarted += () => _panelBounds?.Stop();
        _panel.PreferredSizeChanged += () => FitPanel(animate: true);

        _hotkey.Pressed += Toggle;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _watcher.FullscreenChanged += EvaluateFullscreen;

        _ws.SummaryChanged += (_, _) => RefreshOrb();
        _ws.Settings.PropertyChanged += OnSettingChanged;
    }

    public string DataFolder => _store.Folder;

    public bool HotkeyActive { get; private set; }

    // ================= Lifecycle =================

    public void Start(bool openPanel)
    {
        ApplyTheme();
        ResumeHotkey();
        StartupRegistration.Refresh(_ws.Settings.LaunchAtStartup);
        ScheduleMidnight();

        new WindowInteropHelper(_orb).EnsureHandle();
        new WindowInteropHelper(_panel).EnsureHandle();
        _panelBounds = new WindowBoundsAnimator(_panel, _panel.Handle);
        HwndSource.FromHwnd(_orb.Handle)!.AddHook(BroadcastHook);

        RefreshOrb();
        var (cx, cy) = InitialOrbCenter();
        _orb.MoveCenterTo(cx, cy);
        _orb.Show();
        _orb.MoveCenterTo(cx, cy); // again, now that the window knows its monitor's DPI
        _orb.SnapIntoPlace(animate: false);
        _orb.Orb.ContextMenu = BuildOrbMenu();

        if (openPanel) Expand();
        else
        {
            _orb.PlayAppear();
            _trimDelay.Start();
        }

        EvaluateFullscreen();
    }

    /// <summary>True once Tack is shutting down; windows may then really close.</summary>
    public bool IsQuitting { get; private set; }

    public void Quit()
    {
        IsQuitting = true;
        _store.Flush();
        Application.Current.Shutdown();
    }

    /// <summary>Windows is signing out or shutting down.</summary>
    public void PrepareForSessionEnd()
    {
        IsQuitting = true;
        _store.Flush();
    }

    private ContextMenu BuildOrbMenu()
    {
        MenuItem Item(string header, Action action, string? gesture = null, bool bold = false)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        var open = Item("Open Tack", Expand, bold: true);
        menu.Items.Add(open);
        menu.Items.Add(Item("Settings", () => { Expand(); _panel.OpenSettings(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Tack", Quit));
        menu.Opened += (_, _) =>
        {
            open.InputGestureText = HotkeyActive ? _ws.Settings.Hotkey : "";
            _peekDelay.Stop();
            _peek.Dismiss();
        };
        return menu;
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _watcher.Dispose();
    }

    // ================= Expand / collapse =================

    public void Toggle()
    {
        if (!_expanded) Expand();
        else if (!_panel.IsActive) BringPanelForward();
        else Collapse();
    }

    public void Expand()
    {
        if (_expanded)
        {
            BringPanelForward();
            return;
        }
        _expanded = true;
        _peekDelay.Stop();
        _peek.Dismiss();
        _trimDelay.Stop();
        _ws.RefreshDay();

        var rect = ComputePanelRect(out _anchorRight, out _anchorBottom);
        // Move while hidden (so it opens on the right monitor), then pin the exact size once
        // WPF has picked up that monitor's DPI.
        ScreenGeometry.Place(_panel.Handle, rect.X, rect.Y, rect.Width, rect.Height);
        _panel.Show();
        ScreenGeometry.PlaceExact(_panel, _panel.Handle, rect);
        WindowEffects.BringToTopmost(_panel.Handle);
        _panel.Activate();
        _panel.PlayEntrance(_anchorRight, _anchorBottom);

        if (_orb.IsVisible) _orb.PlayDisappear(() => { if (_expanded) _orb.Hide(); });
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _collapseCheck.Stop();
        _panelBounds?.Stop();
        _store.Flush();

        _panel.PlayExit(() =>
        {
            if (_expanded) return; // re-opened mid-animation
            _panel.Hide();
            _panel.CloseSettings(animate: false);
            _trimDelay.Start();
        });

        if (!_hiddenForFullscreen)
        {
            var (cx, cy) = InitialOrbCenter();
            _orb.MoveCenterTo(cx, cy);
            _orb.Show();
            _orb.MoveCenterTo(cx, cy);
            WindowEffects.BringToTopmost(_orb.Handle);
            _orb.PlayAppear();
        }
    }

    private void BringPanelForward()
    {
        if (!_panel.IsVisible) _panel.Show();
        WindowEffects.BringToTopmost(_panel.Handle);
        _panel.Activate();
        _panel.FocusDefault();
    }

    private void CollapseIfAbandoned()
    {
        if (!_expanded || _ws.Settings.Pinned || _panel.IsActive || _dialogOpen) return;
        Collapse();
    }

    // ================= Placement =================

    private (int X, int Y) InitialOrbCenter()
    {
        var s = _ws.Settings;
        if (s.OrbX is int x && s.OrbY is int y) return ClampOrb(x, y);

        // First run: on the right edge of the primary screen, a third of the way down.
        var mon = ScreenGeometry.FromPoint(0, 0);
        int half = mon.Px(OrbVisual.Diameter / 2 + 14);
        return (mon.Work.X + mon.Work.Width - half, mon.Work.Y + (int)(mon.Work.Height * 0.3));
    }

    /// <summary>Brings a remembered position back on screen (e.g. after a monitor was unplugged).</summary>
    private static (int X, int Y) ClampOrb(int x, int y)
    {
        var mon = ScreenGeometry.FromPoint(x, y);
        int half = mon.Px(OrbVisual.Diameter / 2 + 6);
        var w = mon.Work;
        return (Math.Clamp(x, w.X + half, w.X + w.Width - half), Math.Clamp(y, w.Y + half, w.Y + w.Height - half));
    }

    /// <summary>
    /// The panel unfolds from the orb toward the middle of the screen: the orb's corner of the
    /// panel sits exactly where the orb was.
    /// </summary>
    private Int32Rect ComputePanelRect(out bool anchorRight, out bool anchorBottom)
    {
        var (cx, cy) = InitialOrbCenter();
        var mon = ScreenGeometry.FromPoint(cx, cy);
        var work = mon.Work;
        var (w, h) = PanelSizePx(mon);
        int half = mon.Px(OrbVisual.Diameter) / 2;

        anchorRight = cx > work.X + work.Width / 2;
        anchorBottom = cy > work.Y + work.Height / 2;
        int x = anchorRight ? cx + half - w : cx - half;
        int y = anchorBottom ? cy + half - h : cy - half;
        return ScreenGeometry.Clamp(new Int32Rect(x, y, w, h), work, mon.Px(PanelInset));
    }

    /// <summary>The current tab's preferred size on a given monitor, never larger than its work area.</summary>
    private (int Width, int Height) PanelSizePx(MonitorArea mon)
    {
        double maxAuto = Math.Min(MaxAutoHeight, mon.Work.Height / mon.Scale * MaxAutoScreenShare);
        var dip = _panel.PreferredSize(maxAuto);
        int inset = mon.Px(PanelInset);
        return (Math.Min(mon.Px(dip.Width), mon.Work.Width - 2 * inset),
                Math.Min(mon.Px(dip.Height), mon.Work.Height - 2 * inset));
    }

    /// <summary>
    /// Resizes the open panel to what the current tab wants (tab switch, settings, tasks added or
    /// finished). The corner nearest the orb stays put, so the panel grows and shrinks away from it
    /// instead of jumping, and it is nudged back inside the screen if it would spill over.
    /// A size the user dragged to is respected: it *is* the tab's preferred size.
    /// </summary>
    private void FitPanel(bool animate)
    {
        if (!_expanded || _panelBounds == null || !_panel.IsVisible || _panel.IsInSizeMove) return;

        var basis = _panelBounds.Target; // where the panel is, or is already heading
        var mon = ScreenGeometry.FromRect(basis);
        var (w, h) = PanelSizePx(mon);
        int x = _anchorRight ? basis.X + basis.Width - w : basis.X;
        int y = _anchorBottom ? basis.Y + basis.Height - h : basis.Y;
        var target = ScreenGeometry.Clamp(new Int32Rect(x, y, w, h), mon.Work, mon.Px(PanelInset));

        if (animate) _panelBounds.AnimateTo(target);
        else _panelBounds.JumpTo(target);
    }

    private void RememberOrbPosition()
    {
        var (x, y) = _orb.Center;
        _ws.Settings.OrbX = x;
        _ws.Settings.OrbY = y;
    }

    /// <summary>After the user moves the panel, the orb folds back into the panel's nearest corner.</summary>
    private void RememberAnchorFromPanel()
    {
        var r = ScreenGeometry.GetWindowRect(_panel.Handle);
        var mon = ScreenGeometry.FromRect(r);
        var work = mon.Work;
        int half = mon.Px(OrbVisual.Diameter) / 2;
        bool right = r.X + r.Width / 2 > work.X + work.Width / 2;
        bool bottom = r.Y + r.Height / 2 > work.Y + work.Height / 2;
        _ws.Settings.OrbX = right ? r.X + r.Width - half : r.X + half;
        _ws.Settings.OrbY = bottom ? r.Y + r.Height - half : r.Y + half;
        _anchorRight = right;
        _anchorBottom = bottom;
    }

    // ================= Orb and peek =================

    private void RefreshOrb() =>
        _orb.Refresh(_ws.OpenCount, _ws.Progress, allDone: _ws.OpenCount == 0 && _ws.DoneToday > 0);

    private void OnOrbHover(bool hot)
    {
        if (hot && !_expanded && !_orb.IsDragging) _peekDelay.Start();
        else
        {
            _peekDelay.Stop();
            _peek.Dismiss();
        }
    }

    private void ShowPeek()
    {
        if (_expanded || !_orb.IsVisible || _orb.IsDragging) return;
        _ws.RefreshDay();
        _peek.ShowFor(_orb, _ws, HotkeyActive ? _ws.Settings.Hotkey : "");
    }

    // ================= Desktop awareness =================

    private void OnForegroundChanged()
    {
        // Other always-on-top windows can climb above us; quietly reclaim the top spot.
        if (_orb.IsVisible) WindowEffects.BringToTopmost(_orb.Handle);
        if (_expanded && _ws.Settings.Pinned) WindowEffects.BringToTopmost(_panel.Handle);
        EvaluateFullscreen();
        _rechecksLeft = 3;
        _fullscreenRecheck.Stop();
        _fullscreenRecheck.Start();
    }

    private void EvaluateFullscreen()
    {
        var (cx, cy) = _orb.IsVisible ? _orb.Center : InitialOrbCenter();
        bool hide = _ws.Settings.HideInFullscreen && _watcher.IsFullscreenAppOn(ScreenGeometry.FromPoint(cx, cy));
        if (hide == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hide;
        Log.Info(hide ? "Full-screen app detected: stepping aside." : "Full-screen app gone: back on screen.");

        if (hide)
        {
            _peek.Dismiss();
            _orb.Hide();
            if (_expanded && _ws.Settings.Pinned) _panel.Hide();
        }
        else if (_expanded)
        {
            if (!_panel.IsVisible) { _panel.Show(); WindowEffects.BringToTopmost(_panel.Handle); }
        }
        else
        {
            var (x, y) = InitialOrbCenter();
            _orb.MoveCenterTo(x, y);
            _orb.Show();
            _orb.PlayAppear();
        }
    }

    private IntPtr BroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SETTINGCHANGE or WM_DWMCOLORIZATIONCOLORCHANGED:
                // Windows' light/dark mode or accent colour may have changed.
                _orb.Dispatcher.BeginInvoke(ApplyTheme);
                break;
            case WM_DISPLAYCHANGE:
                // A monitor was added, removed or rearranged: make sure the orb is still reachable.
                _orb.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_expanded) return;
                    var (x, y) = InitialOrbCenter();
                    _orb.MoveCenterTo(x, y);
                    _orb.SnapIntoPlace(animate: false);
                });
                break;
        }
        return IntPtr.Zero;
    }

    // ================= Settings =================

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = _ws.Settings;
        switch (e.PropertyName)
        {
            case nameof(Settings.Glass): _panel.ApplyGlass(s.Glass); break;
            case nameof(Settings.Theme): ApplyTheme(); break;
            case nameof(Settings.Accent): ApplyAccent(); break;
            case nameof(Settings.LaunchAtStartup): StartupRegistration.Set(s.LaunchAtStartup); break;
            case nameof(Settings.HideInFullscreen): EvaluateFullscreen(); break;
            case nameof(Settings.Pinned):
                if (s.Pinned) WindowEffects.BringToTopmost(_panel.Handle);
                else if (_expanded && !_panel.IsActive) _collapseCheck.Start();
                break;
        }
    }

    /// <summary>Light or dark palette, then the matching accent shade and window frame.</summary>
    private void ApplyTheme()
    {
        bool changed = ThemeManager.Apply(_ws.Settings.Theme);
        ApplyAccent();
        if (!changed) return;
        _panel.ApplyGlass(_ws.Settings.Glass);
        _orb.InvalidateAccent();
    }

    private void ApplyAccent()
    {
        var accent = AccentPalette.Resolve(_ws.Settings.Accent, ThemeManager.IsLight);
        var resources = Application.Current.Resources;
        if (resources.Contains("AccentColor") && resources["AccentColor"] is System.Windows.Media.Color current && current == accent) return;
        AccentPalette.Apply(resources, accent);
        _orb.InvalidateAccent();
    }

    public void SuspendHotkey() => _hotkey.Unregister();

    public void ResumeHotkey()
    {
        var hk = Hotkey.Parse(_ws.Settings.Hotkey);
        HotkeyActive = hk.IsValid && _hotkey.Register(hk);
        if (hk.IsValid && !HotkeyActive) Log.Info($"Shortcut {hk} is unavailable (registered by another app).");
    }

    /// <summary>
    /// First run only: use the first shortcut no other app has claimed, so the welcome tips
    /// name a shortcut that actually works on this PC.
    /// </summary>
    public void ChooseFreeHotkey()
    {
        // Win-based fallbacks are conventionally global, so they won't steal shortcuts from other apps.
        foreach (string candidate in new[] { "Ctrl+Alt+Space", "Win+Ctrl+T", "Win+Alt+N", "Ctrl+Alt+Shift+T" })
        {
            if (!TryUseHotkey(Hotkey.Parse(candidate))) continue;
            _ws.Settings.Hotkey = candidate;
            return;
        }
        _ws.Settings.Hotkey = "";
    }

    /// <summary>Checks that a shortcut can be registered (used by the shortcut recorder).</summary>
    public bool TryUseHotkey(Hotkey hotkey)
    {
        bool ok = _hotkey.Register(hotkey);
        _hotkey.Unregister();
        return ok;
    }

    public void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open data folder", ex); }
    }

    /// <summary>Settings → Import: picks a tack.json (from Tack Focus, another PC or a backup) and merges it in.</summary>
    public void ImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a tack.json",
            Filter = "Tack files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        bool picked;
        _dialogOpen = true; // the panel loses focus to the dialog; it mustn't fold away meanwhile
        try { picked = dialog.ShowDialog(_panel) == true; }
        finally { _dialogOpen = false; }
        BringPanelForward();
        if (!picked) return;

        var (message, undo) = FileImport.Run(_ws, dialog.FileName);
        _store.Flush();
        _panel.ShowToast(message, undo);
    }

    // ================= Housekeeping =================

    private void ScheduleMidnight()
    {
        var next = DateTime.Today.AddDays(1).AddSeconds(2);
        _midnight.Interval = next - DateTime.Now;
        _midnight.Start();
    }

    /// <summary>
    /// Once folded away and idle, hand unused memory back to Windows. Pages come back
    /// on demand the next time Tack is used.
    /// </summary>
    private static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
        EmptyWorkingSet(GetCurrentProcess());
    }
}
