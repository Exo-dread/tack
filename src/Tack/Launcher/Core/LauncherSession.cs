namespace Tack.Launcher.Core;

internal enum SessionMode
{
    Closed,
    /// <summary>Shift+press on the orb: open, but not yet a click or a gesture.</summary>
    Armed,
    /// <summary>Shift+press and drag: release picks whatever is highlighted, or cancels.</summary>
    Gesture,
    /// <summary>Open and waiting for clicks or keys (after a Shift+click, or a gesture that didn't finish).</summary>
    Click,
}

internal enum HitKind { None, Center, Item, EmptySlot, Display, File }

internal readonly record struct Hit(HitKind Kind, NodeRef Node = default, int Display = -1)
{
    public static readonly Hit Nothing = new(HitKind.None);
}

internal enum LauncherKey { Escape, Next, Previous, Activate, PickDisplay, Digit, Recents }

internal enum Outcome { None, Changed, Launch, Close }

/// <param name="Display">Index into <see cref="LauncherScene.Displays"/>; null opens the shortcut normally.</param>
internal sealed record LaunchRequest(NodeRef Node, Shortcut Entry, int? Display);

/// <param name="RestoreFocus">Closing without launching after the launcher took keyboard focus: give it back.</param>
/// <param name="TakeFocus">The launcher just entered click mode and wants the keyboard (Esc, arrows, Enter).</param>
internal readonly record struct SessionResult(Outcome Outcome, LaunchRequest? Launch = null, bool RestoreFocus = false, bool TakeFocus = false)
{
    public static readonly SessionResult Nothing = new(Outcome.None);
    public static readonly SessionResult Changed = new(Outcome.Changed);
}

/// <summary>
/// The launcher's interaction state machine. Input arrives as screen points and times (no WPF), so every
/// rule here is testable. Shift+click and Shift+press-and-drag are told apart by movement, never by time:
/// a press that never moves further than <see cref="LauncherMetrics.GestureThreshold"/> is a click, however
/// long it is held. Releasing a gesture over nothing cancels; nothing launches by accident.
/// </summary>
internal sealed class LauncherSession
{
    /// <summary>
    /// Resting on a shortcut this long (gesture) counts as a rest: with Shift held, its display picker opens;
    /// without, an app's recent files show (when that's on). Passing by never opens anything.
    /// </summary>
    public const long GestureDwellMs = 260;

    /// <summary>The same with ordinary mouse moves: longer, so nothing pops up while passing by.</summary>
    public const long ClickDwellMs = 550;

    private readonly LauncherScene _scene;
    private PxPoint _press;
    private long _hotSince;
    // The highlight came from the pointer resting on a shortcut: only then can a rest open something (see
    // Tick). A highlight moved with the keyboard never does; M, Shift+Enter and R do that.
    private bool _dwellArmed;
    private PxPoint? _pointer;
    private Hit _down = Hit.Nothing;

    public LauncherSession(LauncherScene scene) => _scene = scene;

    public LauncherScene Scene => _scene;
    public SessionMode Mode { get; private set; } = SessionMode.Closed;

    /// <summary>What's under the pointer, or selected with the keyboard.</summary>
    public Hit Highlight { get; private set; } = Hit.Nothing;

    /// <summary>The group whose shortcuts are fanned out, if any.</summary>
    public int? OpenGroup { get; private set; }

    /// <summary>The shortcut whose display picker is showing, if any (an app, a folder, or one of an app's recent files).</summary>
    public NodeRef? Selector { get; private set; }

    /// <summary>The app whose recent files are showing, if any (only with recent files turned on).</summary>
    public NodeRef? Recents { get; private set; }

    /// <summary>A short note shown in the launcher (e.g. why a shortcut can't open).</summary>
    public string? Message { get; private set; }

    /// <summary>The <see cref="Message"/> is a problem (shown as one), not just information.</summary>
    public bool IsWarning { get; private set; }

    public bool IsOpen => Mode != SessionMode.Closed;

    // ================= Gesture from the orb =================

    /// <summary>Shift+press on the orb.</summary>
    public SessionResult Begin(PxPoint press, long now)
    {
        Mode = SessionMode.Armed;
        _press = press;
        _hotSince = now;
        _dwellArmed = false;
        Highlight = Hit.Nothing;
        return SessionResult.Changed;
    }

    /// <summary>The pointer moved while the button is down on the orb (Armed/Gesture), or over the launcher (Click).</summary>
    public SessionResult Move(PxPoint p, long now)
    {
        switch (Mode)
        {
            case SessionMode.Armed:
                if (p.DistanceTo(_press) < LauncherMetrics.GestureThreshold * _scene.Scale) return SessionResult.Nothing;
                Mode = SessionMode.Gesture;
                GestureHover(p, now);
                return SessionResult.Changed;
            case SessionMode.Gesture:
                return GestureHover(p, now) ? SessionResult.Changed : SessionResult.Nothing;
            case SessionMode.Click:
                // A "move" to where the pointer already was (WPF reports one when the window's content changes
                // under a resting pointer) says nothing new, and mustn't undo a highlight chosen with the keyboard.
                if (_pointer == p) return SessionResult.Nothing;
                _pointer = p;
                return Hover(HitTest(p, generous: false), p, now) ? SessionResult.Changed : SessionResult.Nothing;
            default:
                return SessionResult.Nothing;
        }
    }

    /// <summary>The button came up after a press on the orb.</summary>
    public SessionResult Release(PxPoint p, long now)
    {
        switch (Mode)
        {
            case SessionMode.Armed:
                // A click (no real movement): stay open for clicks and keys.
                Mode = SessionMode.Click;
                _pointer = p;
                return new SessionResult(Outcome.Changed, TakeFocus: true);

            case SessionMode.Gesture:
            {
                var hit = HitTest(p, generous: true);
                switch (hit.Kind)
                {
                    case HitKind.Display when Selector is { } sel && _scene.Entry(sel) is { } e:
                        return Launch(sel, e, hit.Display, viaGesture: true);
                    case HitKind.File:
                        Highlight = hit;
                        _pointer = p;
                        return Activate(hit.Node, viaGesture: true);
                    case HitKind.Item:
                        Highlight = hit;
                        _pointer = p;
                        if (_scene.Entry(hit.Node) is { IsGroup: true })
                        {
                            // Releasing on a group just leaves it open: choosing is safe, launching isn't implied.
                            OpenGroup = hit.Node.Slot;
                            Mode = SessionMode.Click;
                            return new SessionResult(Outcome.Changed, TakeFocus: true);
                        }
                        return Activate(hit.Node, viaGesture: true);
                    default:
                        // Released over empty space, an empty slot, the orb, or the picker's frame: cancel.
                        return Close(restoreFocus: false);
                }
            }
            default:
                return SessionResult.Nothing;
        }
    }

    /// <summary>Resting this long on another shortcut while a group is open (gesture) switches to it.</summary>
    public const long SwitchDwellMs = 350;

    private NodeRef? _pending;
    private long _pendingSince;

    private bool GestureHover(PxPoint p, long now)
    {
        var hit = HitTest(p, generous: true, out var blocked);
        if (blocked != _pending)
        {
            _pending = blocked;
            _pendingSince = now;
        }
        return Hover(hit, p, now);
    }

    /// <summary>
    /// The time passed (the controller ticks only while open): after a rest on a shortcut, opens its display
    /// picker while Shift is held, or shows an app's recent files (when that's on) while it isn't; and lets a
    /// rest on another shortcut close an open group (gesture). The picker never opens from a rest alone.
    /// </summary>
    /// <param name="shift">Shift is held down right now (read by the controller as it ticks).</param>
    public SessionResult Tick(long now, bool shift = false)
    {
        if (Mode == SessionMode.Gesture && _pending is { } pending && now - _pendingSince >= SwitchDwellMs)
        {
            _pending = null;
            // (A group's shortcut is only ever passed over while its group is open: it stays open.)
            OpenGroup = pending.IsChild ? pending.Slot : null;
            Selector = null;
            Recents = null;
            Highlight = new Hit(_scene.Entry(pending) == null ? HitKind.EmptySlot : HitKind.Item, pending);
            _hotSince = now;
            _dwellArmed = true;
            if (_scene.Entry(pending) is { IsGroup: true }) OpenGroup = pending.Slot;
            return SessionResult.Changed;
        }
        if (Mode is not (SessionMode.Gesture or SessionMode.Click) || !_dwellArmed) return SessionResult.Nothing;
        if (Highlight.Kind != HitKind.Item) return SessionResult.Nothing;
        var node = Highlight.Node;
        long dwell = Mode == SessionMode.Gesture ? GestureDwellMs : ClickDwellMs;
        if (now - _hotSince < dwell) return SessionResult.Nothing;

        // Resting with Shift held: the shortcut's displays (asked for, so they win over its recent files).
        if (shift)
        {
            if (Selector == node || !_scene.CanPickDisplay(node)) return SessionResult.Nothing;
            Selector = node;
            Recents = null; // the app's displays, not its files
            Message = null;
            return SessionResult.Changed;
        }

        // Resting without Shift: with recent files on, an app shows them (like a group's shortcuts). Nothing
        // else opens by itself. (A picker already open for it stays: that was asked for.)
        if (!_scene.MayHaveRecents(node) || Recents == node || Selector == node) return SessionResult.Nothing;
        var files = _scene.RecentsOf(node);
        if (files is not { Count: > 0 }) return SessionResult.Nothing; // none, or still being read (a later tick looks again)
        Recents = node;
        return SessionResult.Changed;
    }

    /// <summary>An app's recent files have been read (once per opening; none is an answer too).</summary>
    public SessionResult SetRecents(NodeRef owner, IReadOnlyList<RecentFile> files)
    {
        _scene.SetRecents(owner, files);
        return Recents == owner.Owner ? SessionResult.Changed : SessionResult.Nothing;
    }

    // ================= Clicks on the open launcher =================

    public SessionResult PointerDown(PxPoint p)
    {
        if (Mode != SessionMode.Click) return SessionResult.Nothing;
        _down = HitTest(p, generous: false);
        return SessionResult.Nothing;
    }

    public SessionResult PointerUp(PxPoint p)
    {
        if (Mode != SessionMode.Click) return SessionResult.Nothing;
        var hit = HitTest(p, generous: false);
        var down = _down;
        _down = Hit.Nothing;
        if (hit != down) return SessionResult.Nothing; // pressed on one thing, released on another: nothing happens

        switch (hit.Kind)
        {
            case HitKind.Display when Selector is { } sel && _scene.Entry(sel) is { } e:
                return Launch(sel, e, hit.Display, viaGesture: false);
            case HitKind.Item:
            case HitKind.File:
                Highlight = hit;
                return Activate(hit.Node, viaGesture: false);
            case HitKind.EmptySlot:
                Highlight = hit;
                Message = "Add a shortcut here in Settings → Launcher.";
                IsWarning = false;
                return SessionResult.Changed;
            case HitKind.Center:
                return Close(restoreFocus: true);
            default:
                // Inside the picker's frame but between tiles (or the recent files' caption): nothing. Anywhere else:
                // like clicking outside.
                if (Selector is { } s && _scene.SelectorFor(s).Contains(p)) return SessionResult.Nothing;
                if (Recents is { } r && _scene.RecentsBox(r).Contains(p)) return SessionResult.Nothing;
                return Close(restoreFocus: true);
        }
    }

    /// <summary>Right-click a shortcut (or a recent file): choose its display without making that the default.</summary>
    public SessionResult SecondaryClick(PxPoint p)
    {
        if (Mode != SessionMode.Click) return SessionResult.Nothing;
        var hit = HitTest(p, generous: false);
        if (hit.Kind is not (HitKind.Item or HitKind.File) || !_scene.CanPickDisplay(hit.Node)) return SessionResult.Nothing;
        Highlight = hit;
        Selector = hit.Node;
        if (hit.Kind == HitKind.Item) Recents = null; // the app's displays, not its files
        Message = null;
        return SessionResult.Changed;
    }

    /// <summary>A shortcut chosen directly, not by pointing (UI Automation's Invoke, e.g. from Narrator): like clicking it.</summary>
    public SessionResult Choose(NodeRef node)
    {
        if (Mode != SessionMode.Click || _scene.Entry(node) == null) return SessionResult.Nothing;
        _dwellArmed = false;
        Message = null;
        Highlight = new Hit(node.IsFile ? HitKind.File : HitKind.Item, node);
        return Activate(node, viaGesture: false);
    }

    /// <summary>A display chosen directly in the open picker (UI Automation's Invoke): like clicking its tile.</summary>
    public SessionResult ChooseDisplay(int display)
    {
        if (Mode != SessionMode.Click || Selector is not { } sel || _scene.Entry(sel) is not { } e) return SessionResult.Nothing;
        return Launch(sel, e, display, viaGesture: false);
    }

    // ================= Keyboard =================

    public SessionResult Key(LauncherKey key, int digit = 0)
    {
        if (Mode == SessionMode.Closed) return SessionResult.Nothing;
        if (Mode is SessionMode.Armed or SessionMode.Gesture)
            return key == LauncherKey.Escape ? Close(restoreFocus: false) : SessionResult.Nothing;

        // The keyboard is in charge now: a pointer resting somewhere doesn't open a picker behind its back.
        _dwellArmed = false;
        Message = null;
        switch (key)
        {
            case LauncherKey.Escape:
                if (Selector is { } sel)
                {
                    Selector = null;
                    Highlight = new Hit(sel.IsFile ? HitKind.File : HitKind.Item, sel);
                    return SessionResult.Changed;
                }
                if (Recents is { } rec)
                {
                    Recents = null;
                    Highlight = new Hit(HitKind.Item, rec);
                    return SessionResult.Changed;
                }
                if (OpenGroup is { } g)
                {
                    OpenGroup = null;
                    Highlight = new Hit(HitKind.Item, new NodeRef(g));
                    return SessionResult.Changed;
                }
                return Close(restoreFocus: true);

            case LauncherKey.Next:
            case LauncherKey.Previous:
                return Cycle(key == LauncherKey.Next ? 1 : -1);

            case LauncherKey.Activate:
                switch (Highlight.Kind)
                {
                    case HitKind.Display when Selector is { } s && _scene.Entry(s) is { } e:
                        return Launch(s, e, Highlight.Display, viaGesture: false);
                    case HitKind.Item when _scene.Entry(Highlight.Node) is { IsGroup: true } grp:
                        OpenGroup = Highlight.Node.Slot;
                        if (grp.Children is { Count: > 0 }) Highlight = new Hit(HitKind.Item, new NodeRef(Highlight.Node.Slot, 0));
                        return SessionResult.Changed;
                    case HitKind.Item:
                    case HitKind.File:
                        return Activate(Highlight.Node, viaGesture: false);
                    default:
                        return Cycle(1);
                }

            case LauncherKey.PickDisplay:
                if (Highlight.Kind is not (HitKind.Item or HitKind.File) || !_scene.CanPickDisplay(Highlight.Node)) return SessionResult.Nothing;
                Selector = Highlight.Node;
                Highlight = new Hit(HitKind.Display, default, Math.Max(0, _scene.OrbDisplay()));
                return SessionResult.Changed;

            case LauncherKey.Recents:
                return ShowRecents();

            case LauncherKey.Digit when digit >= 1 && digit <= LauncherConfig.SlotCount:
                var node = new NodeRef(digit - 1);
                if (_scene.Entry(node) == null) return SessionResult.Nothing;
                Selector = null;
                Recents = null;
                OpenGroup = null;
                Highlight = new Hit(HitKind.Item, node);
                return Key(LauncherKey.Activate);

            default:
                return SessionResult.Nothing;
        }
    }

    /// <summary>R: the highlighted app's recent files (again: back to the app). The first one is highlighted.</summary>
    private SessionResult ShowRecents()
    {
        if (Highlight.Kind is not (HitKind.Item or HitKind.File)) return SessionResult.Nothing;
        var app = Highlight.Node.Owner;
        if (!_scene.MayHaveRecents(app)) return SessionResult.Nothing;
        if (Recents == app)
        {
            Recents = null;
            Highlight = new Hit(HitKind.Item, app);
            return SessionResult.Changed;
        }
        var files = _scene.RecentsOf(app);
        string label = _scene.Entry(app)?.Label ?? "";
        if (files is not { Count: > 0 })
        {
            Highlight = new Hit(HitKind.Item, app);
            Message = files == null ? $"Looking for {label}'s recent files…" : $"No recent files for {label}.";
            IsWarning = false;
            return SessionResult.Changed;
        }
        Selector = null;
        Recents = app;
        Highlight = new Hit(HitKind.File, app.WithFile(0));
        return SessionResult.Changed;
    }

    /// <summary>Arrow keys and Tab move around the current ring: the picker's displays, an app's recent files, a group's shortcuts, or the primary ring.</summary>
    private SessionResult Cycle(int dir)
    {
        if (Selector != null)
        {
            int n = _scene.Displays.Count;
            if (n == 0) return SessionResult.Nothing;
            int at = Highlight.Kind == HitKind.Display ? Highlight.Display : (dir > 0 ? -1 : n);
            Highlight = new Hit(HitKind.Display, default, ((at + dir) % n + n) % n);
            return SessionResult.Changed;
        }
        if (Recents is { } owner && Highlight.Kind == HitKind.File)
        {
            var files = _scene.FilesOf(owner);
            if (files.Count == 0) return SessionResult.Nothing;
            int at = Math.Clamp(Highlight.Node.File, 0, files.Count - 1);
            Highlight = new Hit(HitKind.File, files[((at + dir) % files.Count + files.Count) % files.Count]);
            return SessionResult.Changed;
        }

        IReadOnlyList<NodeRef> ring = Highlight.Kind == HitKind.Item && Highlight.Node.IsChild && OpenGroup == Highlight.Node.Slot
            ? _scene.ChildrenOf(Highlight.Node.Slot)
            : _scene.FilledPrimary();
        if (ring.Count == 0) return SessionResult.Nothing;
        int i = Highlight.Kind == HitKind.Item ? IndexOf(ring, Highlight.Node) : -1;
        int next = i < 0 ? (dir > 0 ? 0 : ring.Count - 1) : ((i + dir) % ring.Count + ring.Count) % ring.Count;
        Highlight = new Hit(HitKind.Item, ring[next]);
        if (!ring[next].IsChild && OpenGroup != ring[next].Slot) OpenGroup = null;
        if (Recents != null && Recents != ring[next]) Recents = null;
        return SessionResult.Changed;
    }

    private static int IndexOf(IReadOnlyList<NodeRef> ring, NodeRef n)
    {
        for (int i = 0; i < ring.Count; i++) if (ring[i] == n) return i;
        return -1;
    }

    // ================= Shared =================

    /// <summary>Choose a shortcut without a display: groups open; others follow their default behaviour.</summary>
    private SessionResult Activate(NodeRef node, bool viaGesture)
    {
        var e = _scene.Entry(node);
        if (e == null) return SessionResult.Nothing;
        if (e.IsGroup)
        {
            OpenGroup = OpenGroup == node.Slot && !viaGesture ? null : node.Slot;
            Selector = null;
            Recents = null;
            return SessionResult.Changed;
        }
        if (_scene.ProblemOf(node) is { } problem)
            return ShowMessage(problem, viaGesture);

        switch (e.LaunchValue)
        {
            case LaunchMode.Preferred:
                // A display that's gone (unplugged): open normally rather than guess.
                return Launch(node, e, _scene.PreferredDisplay(e), viaGesture);
            case LaunchMode.Ask when _scene.CanPickDisplay(node):
                Selector = node;
                Highlight = new Hit(node.IsFile ? HitKind.File : HitKind.Item, node);
                Mode = SessionMode.Click;
                return new SessionResult(Outcome.Changed, TakeFocus: viaGesture);
            default:
                return Launch(node, e, null, viaGesture);
        }
    }

    private SessionResult Launch(NodeRef node, Shortcut e, int? display, bool viaGesture)
    {
        if (_scene.ProblemOf(node) is { } problem) return ShowMessage(problem, viaGesture);
        if (display is { } d && (d < 0 || d >= _scene.Displays.Count)) display = null;
        Mode = SessionMode.Closed;
        return new SessionResult(Outcome.Launch, new LaunchRequest(node, e, display));
    }

    /// <summary>Something went wrong (bad target, launch failed): stay open and say so. Nothing else happens.</summary>
    public SessionResult ShowMessage(string message, bool takeFocus) => Say(message, warning: true, takeFocus);

    /// <summary>
    /// Something is taking a while (a program on a slow or unreachable server): stay open and say so, so it can
    /// be waited for or dismissed (Esc, a click elsewhere) while Tack carries on.
    /// </summary>
    public SessionResult ShowNote(string note) => Say(note, warning: false, takeFocus: true);

    private SessionResult Say(string text, bool warning, bool takeFocus)
    {
        bool wasGesture = Mode is SessionMode.Gesture or SessionMode.Armed or SessionMode.Closed;
        Mode = SessionMode.Click;
        Message = text;
        IsWarning = warning;
        _dwellArmed = false;
        return new SessionResult(Outcome.Changed, TakeFocus: takeFocus || wasGesture);
    }

    public SessionResult Close(bool restoreFocus)
    {
        Mode = SessionMode.Closed;
        Selector = null;
        Recents = null;
        OpenGroup = null;
        Highlight = Hit.Nothing;
        return new SessionResult(Outcome.Close, RestoreFocus: restoreFocus);
    }

    /// <summary>Updates highlight, open group and picker for the pointer. True when anything visible changed.</summary>
    private bool Hover(Hit hit, PxPoint p, long now)
    {
        var before = (Highlight, OpenGroup, Selector, Recents, Message);
        bool gesture = Mode == SessionMode.Gesture;

        if (hit.Kind == HitKind.Item)
        {
            if (Highlight != hit)
            {
                _hotSince = now;
                _dwellArmed = true;
            }
            var e = _scene.Entry(hit.Node);
            if (!hit.Node.IsChild)
            {
                if (e is { IsGroup: true }) { if (gesture) OpenGroup = hit.Node.Slot; }
                else if (gesture) OpenGroup = null;
            }
            if (Selector is { } s && s != hit.Node) Selector = null;
            // In a gesture, reaching another shortcut puts its recent files away (only possible once it's taken
            // over: see HitTest). With clicks they stay until another shortcut is rested on or chosen.
            if (gesture && Recents is { } r && r != hit.Node) Recents = null;
            // (Passing over a shortcut, or dragging on past it, never opens its display picker: a rest with Shift
            // held does, or a right-click. See Tick.)
            Message = null;
        }
        else if (hit.Kind == HitKind.File)
        {
            if (Selector is { } s && s != hit.Node) Selector = null;
            Message = null;
        }
        else if (hit.Kind == HitKind.Center && gesture)
        {
            // Back to the orb: start over.
            OpenGroup = null;
            Selector = null;
            Recents = null;
        }
        Highlight = hit;
        return before != (Highlight, OpenGroup, Selector, Recents, Message);
    }

    /// <summary>
    /// What's under a point. Generous (gestures): a shortcut is chosen by direction within its ring's band, so
    /// imprecise or fast movements still land; otherwise a hit needs to be on (or very near) the shortcut.
    /// The display picker, when showing, is on top of everything.
    /// </summary>
    public Hit HitTest(PxPoint p, bool generous) => HitTest(p, generous, out _);

    /// <param name="blocked">A shortcut the pointer is right on that didn't count because a group is open (see <see cref="Tick"/>).</param>
    private Hit HitTest(PxPoint p, bool generous, out NodeRef? blocked)
    {
        blocked = null;
        double itemR = _scene.ItemRadius;
        if (Selector is { } sel)
        {
            var tiles = _scene.TilesFor(sel);
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (new PxRect(t.X - 2, t.Y - 2, t.Width + 4, t.Height + 4).Contains(p)) return new Hit(HitKind.Display, default, i);
            }
            if (_scene.SelectorFor(sel).Contains(p)) return Hit.Nothing;
        }
        if (Recents is { } owner)
        {
            var rows = _scene.RecentRows(owner);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (new PxRect(row.X - 1, row.Y, row.Width + 2, row.Height).Contains(p)) return new Hit(HitKind.File, owner.WithFile(i));
            }
            if (_scene.RecentsBox(owner).Contains(p)) return Hit.Nothing; // its caption or edge
            if (generous) return BesideRecents(p, owner, out blocked);
        }

        double fromOrb = p.DistanceTo(_scene.Orb);
        if (fromOrb <= _scene.OrbRadius * 1.1) return new Hit(HitKind.Center);

        double reach = itemR * (generous ? 1.45 : 1.12);
        if (OpenGroup is { } g)
        {
            var near = Nearest(_scene.ChildrenOf(g), p, reach);
            if (near is { } c) return new Hit(HitKind.Item, c);
        }
        // While a group is fanned out during a gesture, the way to its shortcuts often passes over a
        // neighbouring shortcut (always, near screen edges, where the fan can't be centred on its group).
        // So the group stays open: another shortcut takes over only after resting on it for a moment
        // (see Tick), or by heading back toward the orb and out again.
        double ring = _scene.Orb.DistanceTo(_scene.Primary[0]);
        bool sticky = generous && OpenGroup != null;
        bool Other(NodeRef n) => sticky && n.Slot != OpenGroup;

        var nearPrimary = Nearest(Enumerable.Range(0, _scene.Slots.Count).Select(i => new NodeRef(i)).ToList(), p, reach);
        if (nearPrimary is { } np)
        {
            if (!Other(np)) return new Hit(_scene.Entry(np) == null ? HitKind.EmptySlot : HitKind.Item, np);
            if (p.DistanceTo(_scene.CenterOf(np)) <= itemR) blocked = np;
        }

        if (!generous) return Hit.Nothing;

        // Direction-based choice for gestures.
        if (OpenGroup is { } og && _scene.ChildrenOf(og) is { Count: > 0 } kids)
        {
            double inner = _scene.Orb.DistanceTo(_scene.CenterOf(new NodeRef(og))) + itemR;
            double outer = kids.Max(k => _scene.Orb.DistanceTo(_scene.CenterOf(k))) + itemR * 1.8;
            if (fromOrb > inner && fromOrb < outer && ByDirection(kids, p) is { } k) return new Hit(HitKind.Item, k);
        }
        var filled = _scene.FilledPrimary();
        if (filled.Count > 0 && fromOrb > _scene.OrbRadius * 1.25 && fromOrb < ring + itemR * 1.6 && ByDirection(filled, p) is { } kp)
        {
            // With a group open, other shortcuts are chosen by direction only close to the orb.
            if (!Other(kp) || fromOrb <= ring * 0.6) return new Hit(HitKind.Item, kp);
        }
        return Hit.Nothing;
    }

    /// <summary>
    /// A gesture with an app's recent files out: on the way to them the pointer may pass over another shortcut
    /// (near screen edges the card can't always sit straight beyond its app), so the app stays the one: another
    /// shortcut takes over only after resting on it (see <see cref="Tick"/>), or by heading back toward the orb.
    /// </summary>
    private Hit BesideRecents(PxPoint p, NodeRef owner, out NodeRef? blocked)
    {
        blocked = null;
        double itemR = _scene.ItemRadius, fromOrb = p.DistanceTo(_scene.Orb);
        if (fromOrb <= _scene.OrbRadius * 1.1) return new Hit(HitKind.Center);
        if (p.DistanceTo(_scene.CenterOf(owner)) <= itemR * 1.45) return new Hit(HitKind.Item, owner);

        var onShow = (OpenGroup is { } g ? _scene.ChildrenOf(g) : Array.Empty<NodeRef>())
            .Concat(Enumerable.Range(0, _scene.Slots.Count).Select(i => new NodeRef(i))).ToList();
        if (Nearest(onShow, p, itemR) is { } other && other != owner)
        {
            blocked = other;
            return Hit.Nothing;
        }
        double ring = _scene.Orb.DistanceTo(_scene.Primary[0]);
        var filled = _scene.FilledPrimary();
        if (filled.Count > 0 && fromOrb > _scene.OrbRadius * 1.25 && fromOrb <= ring * 0.6 && ByDirection(filled, p) is { } kp)
            return new Hit(HitKind.Item, kp);
        return Hit.Nothing;
    }

    private NodeRef? Nearest(IReadOnlyList<NodeRef> nodes, PxPoint p, double reach)
    {
        NodeRef? best = null;
        double bestD = reach;
        foreach (var n in nodes)
        {
            double d = p.DistanceTo(_scene.CenterOf(n));
            if (d <= bestD) { bestD = d; best = n; }
        }
        return best;
    }

    /// <summary>The node whose direction from the orb is closest to the pointer's, within half the gap to its neighbours.</summary>
    private NodeRef? ByDirection(IReadOnlyList<NodeRef> nodes, PxPoint p)
    {
        double a = _scene.Orb.AngleTo(p);
        var angles = nodes.Select(n => _scene.Orb.AngleTo(_scene.CenterOf(n))).ToList();
        NodeRef? best = null;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            double diff = Diff(a, angles[i]);
            // Half the angle to the nearest neighbour (30° on the full circle), at most 30°.
            double half = 30;
            for (int j = 0; j < nodes.Count; j++) if (j != i) half = Math.Min(half, Diff(angles[i], angles[j]) / 2);
            if (diff <= half && diff < bestDiff) { bestDiff = diff; best = nodes[i]; }
        }
        return best;
    }

    private static double Diff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }
}
