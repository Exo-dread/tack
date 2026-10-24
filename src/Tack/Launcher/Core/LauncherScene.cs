namespace Tack.Launcher.Core;

/// <summary>
/// A shortcut's position in the launcher: a primary slot, or one of a group's shortcuts; or, with
/// <see cref="File"/>, one of that shortcut's recent files.
/// </summary>
internal readonly record struct NodeRef(int Slot, int Child = -1, int File = -1)
{
    public bool IsChild => Child >= 0;
    public bool IsFile => File >= 0;
    public NodeRef Parent => new(Slot);

    /// <summary>The shortcut itself (for a recent file, the app it belongs to).</summary>
    public NodeRef Owner => new(Slot, Child);

    public NodeRef WithFile(int file) => new(Slot, Child, file);

    public override string ToString()
    {
        string shortcut = IsChild ? $"{Slot}.{Child}" : Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return IsFile ? $"{shortcut}#{File}" : shortcut;
    }
}

/// <summary>
/// Everything the launcher shows for one opening, laid out once: where each shortcut, each group's fan and
/// each display picker goes. Pure data, built from the config, the orb's position and the displays.
/// </summary>
internal sealed class LauncherScene
{
    private readonly Dictionary<NodeRef, PxRect> _selectors = new();
    private readonly Dictionary<NodeRef, IReadOnlyList<PxRect>> _tiles = new();
    private readonly Dictionary<NodeRef, string?> _problems = new();
    private readonly Dictionary<NodeRef, IReadOnlyList<RecentFile>> _recents = new();
    private readonly Dictionary<NodeRef, PxRect> _recentBoxes = new();
    private readonly Dictionary<NodeRef, Shortcut> _fileEntries = new();
    private readonly Func<Shortcut, string?> _problem;

    private LauncherScene(LauncherConfig config, PxPoint orb, PxRect work, double scale, IReadOnlyList<Display> displays,
        Func<Shortcut, string?> problem)
    {
        Orb = orb;
        Work = work;
        Scale = scale;
        Displays = displays;
        _problem = problem;
        Slots = config.Slots.Take(LauncherConfig.SlotCount).ToList();
        while (Slots.Count < LauncherConfig.SlotCount) Slots.Add(null);
        Primary = RadialLayout.Primary(orb, work, scale, LauncherConfig.SlotCount);

        var children = new Dictionary<int, IReadOnlyList<PxPoint>>();
        for (int i = 0; i < Slots.Count; i++)
        {
            if (Slots[i] is { IsGroup: true } g)
                children[i] = RadialLayout.Children(orb, Primary[i], g.Children?.Count ?? 0, work, scale);
        }
        Children = children;
        Aspect = RadialLayout.Aspect(displays);
        RecentFilesOn = config.RecentFiles;

        // The window covers everything that can appear, so it never has to move or grow mid-gesture. How many recent
        // files an app has isn't known yet (they're read as it opens), and near an edge a shorter card can sit
        // somewhere else than a full one: room is kept for each.
        double r = ItemRadius + LauncherMetrics.LabelAllowance * scale;
        var extent = PxRect.Around(orb, OrbRadius + 4 * scale, OrbRadius + 4 * scale);
        foreach (var node in AllNodes())
        {
            extent = extent.Union(PxRect.Around(CenterOf(node), r, r));
            if (CanPickDisplay(node)) extent = extent.Union(SelectorFor(node));
            if (MayHaveRecents(node))
                for (int rows = 1; rows <= RecentFiles.Max; rows++) extent = extent.Union(Card(node, rows));
        }
        Extent = extent.Intersect(work);
    }

    public static LauncherScene Build(LauncherConfig config, PxPoint orb, PxRect work, double scale,
        IReadOnlyList<Display> displays, Func<Shortcut, string?> problem) =>
        new(config, orb, work, Math.Max(0.5, scale), displays, problem);

    public PxPoint Orb { get; }
    public PxRect Work { get; }
    public double Scale { get; }
    public IReadOnlyList<Display> Displays { get; }
    public List<Shortcut?> Slots { get; }
    public IReadOnlyList<PxPoint> Primary { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<PxPoint>> Children { get; }
    public double Aspect { get; }

    /// <summary>The launcher window's rectangle: everything that can appear, inside the work area.</summary>
    public PxRect Extent { get; }

    public double ItemRadius => LauncherMetrics.ItemDiameter / 2 * Scale;
    public double OrbRadius => LauncherMetrics.OrbDiameter / 2 * Scale;
    public bool MultipleDisplays => Displays.Count >= 2;

    public Shortcut? Entry(NodeRef n)
    {
        if (n.Slot < 0 || n.Slot >= Slots.Count) return null;
        if (n.IsFile) return FileEntry(n);
        var s = Slots[n.Slot];
        if (!n.IsChild) return s;
        return s?.Children is { } c && n.Child < c.Count ? c[n.Child] : null;
    }

    public PxPoint CenterOf(NodeRef n)
    {
        if (n.IsFile)
        {
            var rows = RecentRows(n.Owner);
            return n.File < rows.Count ? rows[n.File].Center : CenterOf(n.Owner);
        }
        if (!n.IsChild) return Primary[n.Slot];
        return Children.TryGetValue(n.Slot, out var c) && n.Child < c.Count ? c[n.Child] : Primary[n.Slot];
    }

    /// <summary>Why a shortcut can't open right now (missing program, bad address), checked once per opening.</summary>
    public string? ProblemOf(NodeRef n)
    {
        if (_problems.TryGetValue(n, out var p)) return p;
        var e = Entry(n);
        p = e == null || e.IsGroup ? null : _problem(e);
        _problems[n] = p;
        return p;
    }

    public IEnumerable<NodeRef> AllNodes()
    {
        for (int i = 0; i < Slots.Count; i++)
        {
            yield return new NodeRef(i);
            if (Slots[i] is { IsGroup: true } g)
                for (int j = 0; j < (g.Children?.Count ?? 0); j++) yield return new NodeRef(i, j);
        }
    }

    public IReadOnlyList<NodeRef> ChildrenOf(int slot)
    {
        int n = Entry(new NodeRef(slot))?.Children?.Count ?? 0;
        return Enumerable.Range(0, n).Select(j => new NodeRef(slot, j)).ToList();
    }

    public IReadOnlyList<NodeRef> FilledPrimary() =>
        Enumerable.Range(0, Slots.Count).Where(i => Slots[i] != null).Select(i => new NodeRef(i)).ToList();

    /// <summary>A display picker makes sense for apps and folders, when more than one display is connected.</summary>
    public bool CanPickDisplay(NodeRef n) => MultipleDisplays && Entry(n) is { CanTargetDisplay: true };

    public PxRect SelectorFor(NodeRef n)
    {
        if (!_selectors.TryGetValue(n, out var box))
        {
            box = RadialLayout.SelectorBox(Orb, CenterOf(n), Work, Scale, Aspect, OthersOnShowWith(n));
            // A recent file's picker: inside the window, which was sized before the files were known.
            if (n.IsFile) box = box.ClampInside(Extent);
            _selectors[n] = box;
        }
        return box;
    }

    /// <summary>What's on show with a shortcut: the primary shortcuts and, for a group's shortcut, the rest of that group.</summary>
    private List<PxPoint> OthersOnShowWith(NodeRef n)
    {
        var self = n.Owner;
        return AllNodes().Where(o => o != self && (!o.IsChild || o.Slot == n.Slot) && Entry(o) != null).Select(CenterOf).ToList();
    }

    // ================= Recent files =================

    /// <summary>Resting on an app shows the files it opened last (Settings → Launcher → Recent files).</summary>
    public bool RecentFilesOn { get; }

    /// <summary>An app shortcut whose program (.exe) can be found, with recent files turned on.</summary>
    public bool MayHaveRecents(NodeRef n) =>
        RecentFilesOn && !n.IsFile && Entry(n) is { KindValue: ShortcutKind.App } e &&
        WinPath.Extension(e.Target.Trim().Trim('"')) == ".exe" && ProblemOf(n) == null;

    /// <summary>The app's recent files, most recent first; null while they're still being read.</summary>
    public IReadOnlyList<RecentFile>? RecentsOf(NodeRef owner) => _recents.TryGetValue(owner.Owner, out var files) ? files : null;

    /// <summary>The app's recent files have been read (at most <see cref="RecentFiles.Max"/>; none is an answer too).</summary>
    public void SetRecents(NodeRef owner, IReadOnlyList<RecentFile> files)
    {
        owner = owner.Owner;
        if (!MayHaveRecents(owner)) return;
        _recents[owner] = files.Take(RecentFiles.Max).ToList();
        _recentBoxes.Remove(owner);
        bool Mine(NodeRef k) => k.IsFile && k.Owner == owner;
        foreach (var k in _selectors.Keys.Where(Mine).ToList()) _selectors.Remove(k);
        foreach (var k in _tiles.Keys.Where(Mine).ToList()) _tiles.Remove(k);
        foreach (var k in _problems.Keys.Where(Mine).ToList()) _problems.Remove(k);
        foreach (var k in _fileEntries.Keys.Where(Mine).ToList()) _fileEntries.Remove(k);
    }

    /// <summary>The card the app's recent files are shown in: beyond it, on screen, inside the window.</summary>
    public PxRect RecentsBox(NodeRef owner)
    {
        owner = owner.Owner;
        if (_recentBoxes.TryGetValue(owner, out var box)) return box;
        box = Card(owner, Math.Max(1, RecentsOf(owner)?.Count ?? 0)).ClampInside(Extent);
        _recentBoxes[owner] = box;
        return box;
    }

    /// <summary>One row per recent file, top to bottom (none while they're being read).</summary>
    public IReadOnlyList<PxRect> RecentRows(NodeRef owner) =>
        RadialLayout.RecentRows(RecentsBox(owner), RecentsOf(owner)?.Count ?? 0, Scale);

    /// <summary>The app's recent files as nodes, in order.</summary>
    public IReadOnlyList<NodeRef> FilesOf(NodeRef owner) =>
        Enumerable.Range(0, RecentsOf(owner)?.Count ?? 0).Select(i => owner.Owner.WithFile(i)).ToList();

    private PxRect Card(NodeRef owner, int rows) => RadialLayout.RecentsBox(Orb, CenterOf(owner), Work, Scale, rows, OthersOnShowWith(owner));

    /// <summary>
    /// A recent file stands in as a copy of its app's shortcut that opens that file: same program, arguments and
    /// display choice, so launching, the display picker and window placement work as they do for the app.
    /// </summary>
    private Shortcut? FileEntry(NodeRef n)
    {
        if (_fileEntries.TryGetValue(n, out var entry)) return entry;
        if (Entry(n.Owner) is not { KindValue: ShortcutKind.App } app || RecentsOf(n.Owner) is not { } files || n.File >= files.Count) return null;
        var file = files[n.File];
        entry = new Shortcut
        {
            Id = app.Id, Label = file.Name, Kind = "app", Target = app.Target, Arguments = app.Arguments,
            Launch = app.Launch, Monitor = app.Monitor, Document = file.Path,
        };
        _fileEntries[n] = entry;
        return entry;
    }

    /// <summary>The selector's display tiles (same order as <see cref="Displays"/>), below its caption.</summary>
    public IReadOnlyList<PxRect> TilesFor(NodeRef n)
    {
        if (_tiles.TryGetValue(n, out var tiles)) return tiles;
        var box = SelectorFor(n);
        double pad = 10 * Scale, caption = 20 * Scale;
        var area = new PxRect(box.X + pad, box.Y + caption, box.Width - 2 * pad, box.Height - caption - pad);
        tiles = RadialLayout.MonitorMap(Displays.Select(d => d.Bounds).ToList(), area, 3 * Scale);
        _tiles[n] = tiles;
        return tiles;
    }

    /// <summary>The display a shortcut prefers, if it is still connected.</summary>
    public int? PreferredDisplay(Shortcut e) => e.Monitor is { } m ? MonitorMatch.Find(m, Displays) : null;

    /// <summary>The display the orb is on (the picker marks it).</summary>
    public int OrbDisplay()
    {
        for (int i = 0; i < Displays.Count; i++) if (Displays[i].Bounds.Contains(Orb)) return i;
        return -1;
    }
}

/// <summary>Finds a remembered display among the connected ones.</summary>
internal static class MonitorMatch
{
    /// <summary>Same device and bounds; else same device (resolution changed); else same bounds (renumbered). Null: disconnected.</summary>
    public static int? Find(MonitorRef m, IReadOnlyList<Display> displays)
    {
        bool SameBounds(Display d) =>
            Math.Abs(d.Bounds.X - m.X) < 1 && Math.Abs(d.Bounds.Y - m.Y) < 1 &&
            Math.Abs(d.Bounds.Width - m.Width) < 1 && Math.Abs(d.Bounds.Height - m.Height) < 1;
        bool SameDevice(Display d) => !string.IsNullOrEmpty(m.Device) && string.Equals(d.Device, m.Device, StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < displays.Count; i++) if (SameDevice(displays[i]) && SameBounds(displays[i])) return i;
        for (int i = 0; i < displays.Count; i++) if (SameDevice(displays[i])) return i;
        for (int i = 0; i < displays.Count; i++) if (SameBounds(displays[i])) return i;
        return null;
    }

    /// <summary>
    /// Display numbers as people read them: left to right, then top to bottom (not Windows' internal numbering,
    /// which needn't match the arrangement). Returns, for each display, its 1-based number.
    /// </summary>
    public static int[] Numbers(IReadOnlyList<Display> displays)
    {
        var order = Enumerable.Range(0, displays.Count)
            .OrderBy(i => displays[i].Bounds.X).ThenBy(i => displays[i].Bounds.Y).ToList();
        var numbers = new int[displays.Count];
        for (int k = 0; k < order.Count; k++) numbers[order[k]] = k + 1;
        return numbers;
    }

    public static string Describe(Display d, int number) =>
        $"Display {number}{(d.Primary ? " (main)" : "")} · {d.Bounds.Width:0}×{d.Bounds.Height:0} · {d.Scale * 100:0}%";
}
