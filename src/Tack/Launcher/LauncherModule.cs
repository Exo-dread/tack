using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Tack.Interop;
using Tack.Launcher.Core;
using Tack.Services;
using static Tack.Interop.NativeMethods;

namespace Tack.Launcher;

/// <summary>What the orb hands to the launcher. The orb only talks to it when it's turned on.</summary>
internal interface IOrbLauncher
{
    /// <summary>Asked on every press on the orb: true takes the press (Shift is held and the launcher is on).</summary>
    bool WantsPress();
    void Press(int x, int y);
    void Move(int x, int y);
    void Release(int x, int y);
    void CancelGesture();
}

/// <summary>
/// The optional radial launcher: its settings, and the launcher window while it's open. Turned off (the
/// default) it holds the loaded settings and nothing else: no window, no timer, no hook. Every entry point
/// catches its own errors, so a launcher problem closes the launcher and never takes Tack down.
/// </summary>
internal sealed class LauncherModule : IOrbLauncher, IDisposable
{
    private readonly LauncherConfigFile _file;
    private readonly Func<(int X, int Y)> _orbCenter;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LauncherSession? _session;
    private RadialWindow? _window;
    private DispatcherTimer? _tick;
    private IntPtr _previousForeground;

    public LauncherModule(string dataFolder, Func<(int X, int Y)> orbCenter)
    {
        _file = new LauncherConfigFile(dataFolder);
        _orbCenter = orbCenter;
        // A missing file (the launcher was never turned on) costs one File.Exists and creates nothing.
        Config = _file.Load((message, ex) => Log.Error($"Launcher: {message}", ex));
    }

    public LauncherConfig Config { get; private set; }

    public bool Enabled => Config.Enabled;

    public bool IsOpen => _window != null;

    /// <summary>Turned on or off.</summary>
    public event Action? EnabledChanged;

    /// <summary>Shortcuts or options changed (settings view refreshes).</summary>
    public event Action? ConfigChanged;

    public event Action? Opened;
    public event Action? Closed;

    private long Now => _clock.ElapsedMilliseconds;

    // ================= Settings =================

    /// <summary>Changes the config, saves launcher.json and tells whoever's listening.</summary>
    public void Update(Action<LauncherConfig> change)
    {
        bool wasEnabled = Enabled;
        var next = Config.Clone();
        change(next);
        next.Normalize();
        Config = next;
        try { _file.Save(next); }
        catch (Exception ex) { Log.Error("Launcher: couldn't save launcher.json", ex); }

        if (wasEnabled != next.Enabled)
        {
            if (!next.Enabled)
            {
                ForceClose();
                WindowPlacer.StopAll();
                LauncherIcons.Clear();
                AppsIndex.Clear();
            }
            Log.Info(next.Enabled ? "Launcher turned on." : "Launcher turned off.");
            EnabledChanged?.Invoke();
        }
        if (!next.RecentFiles) AppsIndex.Clear();
        ConfigChanged?.Invoke();
    }

    public void SetEnabled(bool on) => Update(c =>
    {
        c.Enabled = on;
        if (on && c.Slots.All(s => s == null)) Seed(c);
    });

    /// <summary>
    /// The first time it's turned on: the user's own Downloads, Documents and Desktop (found on this PC, never
    /// assumed) and File Explorer, so there's something to try. Everything else starts empty.
    /// </summary>
    private static void Seed(LauncherConfig c)
    {
        var folders = new List<Shortcut>();
        void AddFolder(string label, string? path)
        {
            // (A folder redirected to a network share isn't checked: that could stall turning the launcher on.)
            if (!string.IsNullOrEmpty(path) && RealFileProbe.Instance.DirectoryExists(path))
                folders.Add(new Shortcut { Label = label, Kind = "folder", Target = path });
        }
        AddFolder("Downloads", KnownFolders.Downloads());
        AddFolder("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        c.Slots[0] = folders.Count > 0 ? new Shortcut { Label = "Folders", Kind = "group", Children = folders } : null;
        c.Slots[1] = new Shortcut { Label = "File Explorer", Kind = "app", Target = "explorer.exe" };
    }

    // ================= From the orb (gesture) =================

    public bool WantsPress()
    {
        try { return Enabled && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0; }
        catch { return false; }
    }

    public void Press(int x, int y) => Guard(() => Open(new PxPoint(x, y)));

    public void Move(int x, int y) => Guard(() => Apply(_session?.Move(new PxPoint(x, y), Now)));

    public void Release(int x, int y) => Guard(() => Apply(_session?.Release(new PxPoint(x, y), Now)));

    public void CancelGesture() => Guard(() =>
    {
        if (_session?.Mode is SessionMode.Armed or SessionMode.Gesture) Apply(_session.Close(restoreFocus: false));
    });

    // ================= From the launcher window (click mode) =================

    public void OnPointerMove(RadialWindow w) => FromWindow(w, s => s.Move(Cursor(), Now));
    public void OnPointerDown(RadialWindow w) => FromWindow(w, s => s.PointerDown(Cursor()));
    public void OnPointerUp(RadialWindow w) => FromWindow(w, s => s.PointerUp(Cursor()));
    public void OnSecondaryClick(RadialWindow w) => FromWindow(w, s => s.SecondaryClick(Cursor()));
    public void OnKey(RadialWindow w, LauncherKey key, int digit) => FromWindow(w, s => s.Key(key, digit));

    /// <summary>UI Automation's Invoke on a shortcut or a display tile (Narrator, other assistive tools).</summary>
    public void OnInvoke(RadialWindow w, NodeRef node) => FromWindow(w, s => s.Choose(node));
    public void OnInvokeDisplay(RadialWindow w, int display) => FromWindow(w, s => s.ChooseDisplay(display));

    /// <summary>Alt+F4 on the launcher: close it all (not a step back like Esc), giving focus back.</summary>
    public void OnCloseRequested(RadialWindow w) => FromWindow(w, s => s.Close(restoreFocus: true));

    /// <summary>
    /// The window closed without the launcher closing it (Windows asked it to): let go of it as
    /// <see cref="Close"/> would, so no timer runs on and the orb's peek works again.
    /// </summary>
    public void OnWindowClosed(RadialWindow w) => Guard(() =>
    {
        if (w != _window) return; // closed by the launcher itself, which already let go
        Teardown();
        Closed?.Invoke();
    });

    /// <summary>Clicked another app (or another monitor): the launcher folds away, leaving focus where the click went.</summary>
    public void OnDeactivated(RadialWindow w) => FromWindow(w, s => s.Mode == SessionMode.Click ? s.Close(restoreFocus: false) : SessionResult.Nothing);

    private void FromWindow(RadialWindow w, Func<LauncherSession, SessionResult> input) => Guard(() =>
    {
        if (w != _window || _session == null) return; // a window that's already closing
        Apply(input(_session));
    });

    private static PxPoint Cursor()
    {
        var (x, y) = ScreenGeometry.CursorPosition();
        return new PxPoint(x, y);
    }

    // ================= Opening and closing =================

    private void Open(PxPoint press)
    {
        ForceClose();
        var (cx, cy) = _orbCenter();
        var mon = ScreenGeometry.FromPoint(cx, cy);
        var work = new PxRect(mon.Work.X, mon.Work.Y, mon.Work.Width, mon.Work.Height);
        var displays = DisplayInfo.Current();
        if (displays.Count == 0)
            displays.Add(new Display("", new PxRect(mon.Bounds.X, mon.Bounds.Y, mon.Bounds.Width, mon.Bounds.Height), work, mon.Scale, true));

        var scene = LauncherScene.Build(Config, new PxPoint(cx, cy), work, mon.Scale, displays,
            s => ShortcutRules.Problem(s, RealFileProbe.Instance));
        _session = new LauncherSession(scene);
        _previousForeground = GetForegroundWindow();
        _session.Begin(press, Now);

        bool animate = !Config.ReduceMotion && SystemParameters.ClientAreaAnimation;
        var window = new RadialWindow(this, _session, animate);
        _window = window;
        new WindowInteropHelper(window).EnsureHandle();
        var e = scene.Extent;
        var rect = new Int32Rect((int)Math.Round(e.X), (int)Math.Round(e.Y), Math.Max(1, (int)Math.Round(e.Width)), Math.Max(1, (int)Math.Round(e.Height)));
        // Move while hidden (so it opens on the orb's monitor at its DPI), show without activating, then pin it exactly.
        ScreenGeometry.Place(window.Handle, rect.X, rect.Y, rect.Width, rect.Height);
        window.Show();
        ScreenGeometry.PlaceExact(window, window.Handle, rect);
        WindowEffects.BringToTopmost(window.Handle);
        window.Refresh();
        window.PlayOpen();

        // While open (and only then): a light tick for hover delays, and to notice Esc (and Shift) during a gesture
        // (the user's app has the keyboard then; Tack doesn't hook it).
        _tick = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _tick.Tick += (_, _) => Guard(OnTick);
        _tick.Start();
        if (scene.RecentFilesOn) ReadRecents(_session);
        Opened?.Invoke();
    }

    /// <summary>
    /// With recent files on: the app shortcuts' recent files, read now (each time the launcher opens, never in
    /// between) on a background thread. Each app's arrive on their own; resting on an app shows them once they're in.
    /// </summary>
    private void ReadRecents(LauncherSession session)
    {
        var scene = session.Scene;
        var apps = scene.AllNodes().Where(scene.MayHaveRecents).Select(n => (n, scene.Entry(n)!)).ToList();
        RecentsLoader.Start(apps, (node, files) => Guard(() =>
        {
            if (_session != session || _window == null) return; // closed (or opened again) meanwhile
            Apply(session.SetRecents(node, files));
        }));
    }

    private void OnTick()
    {
        if (_session == null) return;
        if (_session.Mode is SessionMode.Armed or SessionMode.Gesture && (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
        {
            Apply(_session.Key(LauncherKey.Escape));
            return;
        }
        // Shift is read the same way (no hook): held while resting on a shortcut, it opens the display picker.
        bool shift;
        try { shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0; }
        catch { shift = false; }
        Apply(_session.Tick(Now, shift));
    }

    private void Apply(SessionResult? result)
    {
        if (result is not { } r || _session == null) return;
        switch (r.Outcome)
        {
            case Outcome.Changed:
                _window?.Refresh();
                if (r.TakeFocus) _window?.TakeFocus();
                break;
            case Outcome.Launch:
                Launch(r.Launch!);
                break;
            case Outcome.Close:
                Close(r.RestoreFocus);
                break;
        }
    }

    private void Launch(LaunchRequest request)
    {
        var scene = _session!.Scene;
        var plan = ShortcutRules.Plan(request.Entry, DesktopFileProbe.Instance, out string? problem);
        if (plan == null)
        {
            // Stay open and say why; nothing else happens.
            Apply(_session.ShowMessage(problem ?? "Windows couldn't open this shortcut.", takeFocus: true));
            return;
        }

        Display? target = request.Display is int d ? scene.Displays[d] : null;
        var displays = scene.Displays;
        IntPtr userWindow = _previousForeground;
        // Windows on screen before the launch, so the app's *new* window can be told apart from ones it already had.
        var before = target != null && plan.CanPlace ? WindowPlacer.VisibleTopLevelWindows() : null;
        var window = _window;
        int id = ++_launches;
        ShortcutLauncher.Start(plan, (pid, error) => Guard(() =>
        {
            if (error == null && before != null && Enabled) WindowPlacer.Begin(pid, plan.ExpectedImage, target!, displays, before, userWindow);
            Started(id, window, request.Entry, error);
        }));

        // Usually that's a moment, and the launcher then closes (or says what went wrong). A program on a slow or
        // unreachable server can take Windows much longer: say so after a short wait, and let it be dismissed.
        _slowStart?.Stop();
        _slowStart = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(SlowStartMs) };
        _slowStart.Tick += (_, _) => Guard(() =>
        {
            _slowStart?.Stop();
            _slowStart = null;
            if (id == _launches && window == _window && _session != null)
                Apply(_session.ShowNote($"Opening {request.Entry.Label}…"));
        });
        _slowStart.Start();
    }

    /// <summary>Waiting this long for Windows to start a shortcut shows "Opening…".</summary>
    private const int SlowStartMs = 400;

    private int _launches;
    private DispatcherTimer? _slowStart;

    /// <summary>Windows has started the shortcut (or failed to). Closes the launcher, or says why, if it's still waiting for this one.</summary>
    private void Started(int id, RadialWindow? window, Shortcut entry, string? error)
    {
        if (id == _launches)
        {
            _slowStart?.Stop();
            _slowStart = null;
        }
        if (id != _launches || window == null || window != _window || _session == null)
        {
            // The launcher was closed (or used again) while Windows was busy: nothing to show it on.
            if (error != null) Log.Info($"Launcher: {entry.Label}: {error}");
            return;
        }
        if (error != null)
        {
            // Stay open and say why; nothing else happens.
            Apply(_session.ShowMessage(error, takeFocus: true));
            return;
        }
        Close(restoreFocus: false);
    }

    /// <summary>Closes with the collapse animation, handing focus back if the launcher had taken it.</summary>
    public void Close(bool restoreFocus = false)
    {
        var window = _window;
        if (window == null) return;
        var previous = _previousForeground;
        Teardown();
        if (restoreFocus && window.IsActive && previous != IntPtr.Zero && IsWindow(previous)) SetForegroundWindow(previous);
        window.PlayClose(() => SafeClose(window));
        Closed?.Invoke();
    }

    /// <summary>Closes at once (Tack expanding, full-screen app, display change, turned off, an error).</summary>
    public void ForceClose()
    {
        var window = _window;
        if (window == null) { Teardown(); return; }
        Teardown();
        SafeClose(window);
        Closed?.Invoke();
    }

    private void Teardown()
    {
        _tick?.Stop();
        _tick = null;
        _slowStart?.Stop();
        _slowStart = null;
        _window = null;
        _session = null;
        _previousForeground = IntPtr.Zero;
    }

    private static void SafeClose(Window w)
    {
        try { w.Close(); }
        catch (Exception ex) { Log.Error("Launcher: closing its window failed", ex); }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error("Launcher error (closed it; Tack carries on)", ex);
            try { ForceClose(); } catch { /* already logged */ }
        }
    }

    public void Dispose()
    {
        ForceClose();
        WindowPlacer.StopAll();
    }
}
