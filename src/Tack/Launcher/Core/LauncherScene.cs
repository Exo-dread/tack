namespace Tack.Launcher.Core;

/// <summary>A shortcut's position in the launcher: a primary slot, or one of a group's shortcuts.</summary>
internal readonly record struct NodeRef(int Slot, int Child = -1)
{
    public bool IsChild => Child >= 0;
    public NodeRef Parent => new(Slot);
    public override string ToString() => IsChild ? $"{Slot}.{Child}" : Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        // The window covers everything that can appear, so it never has to move or grow mid-gesture.
        double r = ItemRadius + LauncherMetrics.LabelAllowance * scale;
        var extent = PxRect.Around(orb, OrbRadius + 4 * scale, OrbRadius + 4 * scale);
        foreach (var node in AllNodes())
        {
            extent = extent.Union(PxRect.Around(CenterOf(node), r, r));
            if (CanPickDisplay(node)) extent = extent.Union(SelectorFor(node));
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
        var s = Slots[n.Slot];
        if (!n.IsChild) return s;
        return s?.Children is { } c && n.Child < c.Count ? c[n.Child] : null;
    }

    public PxPoint CenterOf(NodeRef n)
    {
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
            _selectors[n] = box = RadialLayout.SelectorBox(Orb, CenterOf(n), Work, Scale, Aspect);
        return box;
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
        bool SameDevice(Display d) => m.Device.Length > 0 && string.Equals(d.Device, m.Device, StringComparison.OrdinalIgnoreCase);

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
