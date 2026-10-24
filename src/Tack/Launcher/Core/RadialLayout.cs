namespace Tack.Launcher.Core;

/// <summary>Sizes of the launcher, in device-independent pixels (multiplied by the monitor's scale).</summary>
internal static class LauncherMetrics
{
    public const double OrbDiameter = 40;     // matches OrbVisual.Diameter
    public const double ItemDiameter = 40;
    public const double RingRadius = 74;      // orb centre to a shortcut's centre
    public const double ChildRingGap = 58;    // first ring to the group's shortcuts
    public const double MinSpacing = 46;      // centre to centre between neighbours (a 6 DIP gap)
    public const double MaxRingRadius = 190;  // never push shortcuts further out than this
    public const double MaxFanSpan = 100;     // degrees a group's shortcuts spread over (wider only near edges)
    public const double EdgeMargin = 6;       // keep shortcuts this far inside the work area
    public const double SelectorWidth = 168;
    public const double SelectorHeight = 112;
    public const double SelectorGap = 10;
    public const double LabelAllowance = 26;  // room for the label under a shortcut near the window's edge
    public const double GestureThreshold = 7; // movement that turns a Shift+press into a gesture
    public const double RecentsWidth = 260;   // an app's recent files: a card of rows beyond the shortcut
    public const double RecentsCaption = 24;
    public const double RecentsRow = 28;
    public const double RecentsPad = 5;
}

/// <summary>
/// Where the launcher's pieces go around the orb. The orb always stays the centre: near screen edges and
/// corners the shortcuts fan out as an inward-facing arc instead of a full circle, and nothing is ever
/// placed outside the orb's monitor's work area.
/// </summary>
internal static class RadialLayout
{
    /// <summary>
    /// Centres of the primary shortcuts, clockwise from the top. A full circle at fixed angles when it fits
    /// (always the same positions, for muscle memory); otherwise an evenly spaced arc in the largest open
    /// direction, with the radius grown just enough to keep shortcuts apart.
    /// </summary>
    public static IReadOnlyList<PxPoint> Primary(PxPoint orb, PxRect work, double scale, int count = LauncherConfig.SlotCount)
    {
        var inner = Inner(work, scale);
        double r = LauncherMetrics.RingRadius * scale;
        var circle = Enumerable.Range(0, count).Select(i => orb.Offset(-90 + i * 360.0 / count, r)).ToList();
        if (circle.All(inner.Contains)) return circle;
        return OnArc(orb, inner, r, count, scale, preferredCenter: null);
    }

    /// <summary>A group's shortcuts: a fan beyond the group, centred on its direction when possible.</summary>
    public static IReadOnlyList<PxPoint> Children(PxPoint orb, PxPoint parent, int count, PxRect work, double scale)
    {
        if (count <= 0) return Array.Empty<PxPoint>();
        var inner = Inner(work, scale);
        double r = orb.DistanceTo(parent) + LauncherMetrics.ChildRingGap * scale;
        // Many shortcuts go a little further out rather than wider round, so the fan stays within about
        // 100° and a straight drag from the group to any of them never passes over a neighbour on the ring.
        if (count > 1)
        {
            double step = LauncherMetrics.MaxFanSpan / (count - 1) * Math.PI / 180;
            r = Math.Max(r, LauncherMetrics.MinSpacing * scale / 2 / Math.Sin(step / 2));
        }
        return OnArc(orb, inner, r, count, scale, preferredCenter: orb.AngleTo(parent));
    }

    /// <summary>
    /// The display picker's box: just beyond the shortcut, in its direction, kept inside the work area, and
    /// clear of the orb, the shortcut and (as far as it can be) the other shortcuts on show (<paramref name="others"/>).
    /// </summary>
    public static PxRect SelectorBox(PxPoint orb, PxPoint item, PxRect work, double scale, double aspect, IReadOnlyList<PxPoint>? others = null)
    {
        double w = LauncherMetrics.SelectorWidth * scale, h = LauncherMetrics.SelectorHeight * scale;
        // Wide arrangements get a wider, flatter box (up to 1.5x), tall ones a narrower one.
        if (aspect > 2.2) { w *= 1.3; h *= 0.85; }
        else if (aspect < 1.0) { w *= 0.85; }
        return Card(orb, item, work, scale, w, h, others);
    }

    /// <summary>
    /// An app's recent files: a card of <paramref name="rows"/> rows under a caption, placed like the display picker
    /// (beyond the shortcut, on screen, clear of the orb, the shortcut and the others on show).
    /// </summary>
    public static PxRect RecentsBox(PxPoint orb, PxPoint item, PxRect work, double scale, int rows, IReadOnlyList<PxPoint>? others = null)
    {
        double w = LauncherMetrics.RecentsWidth * scale;
        double h = (LauncherMetrics.RecentsCaption + Math.Max(1, rows) * LauncherMetrics.RecentsRow + LauncherMetrics.RecentsPad) * scale;
        return Card(orb, item, work, scale, w, h, others);
    }

    /// <summary>The rows of a recent-files card, top to bottom.</summary>
    public static IReadOnlyList<PxRect> RecentRows(PxRect box, int rows, double scale)
    {
        double inset = 4 * scale, top = box.Y + LauncherMetrics.RecentsCaption * scale, row = LauncherMetrics.RecentsRow * scale;
        return Enumerable.Range(0, Math.Max(0, rows)).Select(i => new PxRect(box.X + inset, top + i * row, box.Width - 2 * inset, row)).ToList();
    }

    /// <summary>A card of w × h beyond a shortcut: in its direction if it fits, else turned a little, always on screen.</summary>
    private static PxRect Card(PxPoint orb, PxPoint item, PxRect work, double scale, double w, double h, IReadOnlyList<PxPoint>? others)
    {
        var area = work.Deflate(LauncherMetrics.EdgeMargin * scale);
        double angle = orb.AngleTo(item);
        double itemR = LauncherMetrics.ItemDiameter / 2 * scale, gap = LauncherMetrics.SelectorGap * scale;
        double baseDistance = orb.DistanceTo(item) + itemR + gap;

        // Try the shortcut's own direction first, then turn a little either way, and take the placement
        // that covers least of the orb and the shortcut (they must stay visible and reachable).
        var keepClear = PxRect.Around(orb, itemR, itemR).Union(PxRect.Around(item, itemR, itemR));
        PxRect best = default;
        double bestScore = double.MaxValue;
        foreach (double turn in new[] { 0.0, 25, -25, 50, -50, 90, -90, 180 })
        {
            double a = angle + turn;
            // Distance from the box centre to its edge in this direction, so the box clears the item.
            double rad = a * Math.PI / 180;
            double half = Math.Min(w / 2 / Math.Max(1e-6, Math.Abs(Math.Cos(rad))), h / 2 / Math.Max(1e-6, Math.Abs(Math.Sin(rad))));
            var c = orb.Offset(a, baseDistance + half);
            var box = PxRect.Around(c, w / 2, h / 2).ClampInside(area);
            double score = box.OverlapArea(keepClear) * 10 + Math.Abs(turn) + box.Center.DistanceTo(c) * 0.5;
            // Covering another shortcut hides it: that costs as much as covering the orb.
            foreach (var other in others ?? Array.Empty<PxPoint>()) score += box.OverlapArea(PxRect.Around(other, itemR, itemR)) * 10;
            if (score < bestScore) { bestScore = score; best = box; }
            if (score < 1) break;
        }
        return best;
    }

    /// <summary>
    /// Where a label (a shortcut's name, or a message) goes: on the far side of its shortcut from the orb, or,
    /// where that would cover something (on an arc at a screen edge the shortcuts sit close), beside, below or
    /// above it, whichever covers least of <paramref name="keepClear"/> (the orb, the shortcuts and picker on
    /// show). Always inside <paramref name="window"/>. Any one unit will do (the launcher uses its DIPs).
    /// </summary>
    /// <param name="gap">From the anchor's centre to the label's nearest edge.</param>
    public static PxRect LabelBox(PxPoint anchor, PxPoint orb, double width, double height, double gap, PxRect window, IReadOnlyList<PxRect> keepClear)
    {
        double dx = anchor.X - orb.X, dy = anchor.Y - orb.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) { dx = 0; dy = 1; } // about the launcher as a whole: under the orb first
        else { dx /= length; dy /= length; }
        double side = dx >= 0 ? 1 : -1;
        double reach = gap + Math.Abs(dx) * width / 2 + Math.Abs(dy) * height / 2;
        var centres = new[]
        {
            new PxPoint(anchor.X + dx * reach, anchor.Y + dy * reach),     // outward, in line with the orb
            new PxPoint(anchor.X + side * (gap + width / 2), anchor.Y),     // beside it, away from the orb
            new PxPoint(anchor.X, anchor.Y + gap + height / 2),             // below
            new PxPoint(anchor.X, anchor.Y - gap - height / 2),             // above
            new PxPoint(anchor.X - side * (gap + width / 2), anchor.Y),     // beside it, on the orb's side
        };
        PxRect best = default;
        double bestScore = double.MaxValue;
        for (int i = 0; i < centres.Length; i++)
        {
            var box = PxRect.Around(centres[i], width / 2, height / 2).ClampInside(window);
            // Covering something costs most; then being pushed off its place by the window's edge; then the order above.
            double score = i * 2 + box.Center.DistanceTo(centres[i]) * 0.5;
            foreach (var k in keepClear) score += box.OverlapArea(k) * 10;
            if (score < bestScore) { bestScore = score; best = box; }
        }
        return best;
    }

    /// <summary>
    /// Fits every display's rectangle into a box, keeping their real arrangement and proportions
    /// (negative coordinates and unusual layouts included). Returned in the same order.
    /// </summary>
    public static IReadOnlyList<PxRect> MonitorMap(IReadOnlyList<PxRect> monitors, PxRect box, double gap = 2)
    {
        if (monitors.Count == 0) return Array.Empty<PxRect>();
        var all = monitors.Aggregate(default(PxRect), (u, m) => u.Union(m));
        if (all.IsEmpty) return monitors.Select(_ => default(PxRect)).ToList();
        double k = Math.Min(box.Width / all.Width, box.Height / all.Height);
        double ox = box.X + (box.Width - all.Width * k) / 2, oy = box.Y + (box.Height - all.Height * k) / 2;
        return monitors.Select(m =>
        {
            var r = new PxRect(ox + (m.X - all.X) * k, oy + (m.Y - all.Y) * k, m.Width * k, m.Height * k);
            double g = Math.Min(gap, Math.Min(r.Width, r.Height) / 4);
            return r.Deflate(g / 2);
        }).ToList();
    }

    /// <summary>The union of all displays' bounds: width / height.</summary>
    public static double Aspect(IReadOnlyList<Display> displays)
    {
        var all = displays.Aggregate(default(PxRect), (u, d) => u.Union(d.Bounds));
        return all.IsEmpty ? 1.6 : all.Width / all.Height;
    }

    private static PxRect Inner(PxRect work, double scale) =>
        work.Deflate((LauncherMetrics.ItemDiameter / 2 + LauncherMetrics.EdgeMargin) * scale);

    /// <summary>
    /// Evenly spaced points on an arc around the orb, inside <paramref name="inner"/>. Finds the open angles at
    /// this radius (1° steps), picks the widest open run (or the one nearest the preferred direction), and grows
    /// the radius until the points fit apart. As a last resort (a tiny screen) the points are clamped inside.
    /// </summary>
    private static IReadOnlyList<PxPoint> OnArc(PxPoint orb, PxRect inner, double r, int count, double scale, double? preferredCenter)
    {
        double spacing = LauncherMetrics.MinSpacing * scale, maxR = Math.Max(r, LauncherMetrics.MaxRingRadius * scale);
        for (; ; r += 6 * scale)
        {
            double stepMin = count > 1 ? 2 * Math.Asin(Math.Min(1, spacing / (2 * r))) * 180 / Math.PI : 0;
            double need = stepMin * (count - 1);
            var run = OpenRun(orb, inner, r, preferredCenter, need);
            if (run is { } open && open.Length >= need)
            {
                // Spread as far as 60° apart (like the full circle), but not beyond the open run.
                double step = count > 1 ? Math.Min(60, open.Length / (count - 1)) : 0;
                if (preferredCenter is { } pc) step = Math.Max(stepMin, Math.Min(step, stepMin * 1.15));
                double span = step * (count - 1);
                double center = open.Start + open.Length / 2;
                if (preferredCenter is { } p)
                {
                    // Stay as close to the group's direction as the open run allows.
                    double rel = Normalize360(p - open.Start);
                    if (rel > open.Length) rel = rel - open.Length > (360 - open.Length) / 2 ? 0 : open.Length;
                    center = open.Start + Math.Clamp(rel, span / 2, open.Length - span / 2);
                }
                return Ordered(orb, r, center - span / 2, step, count);
            }
            if (r >= maxR)
            {
                double start = run?.Start ?? preferredCenter ?? -90;
                double step = count > 1 ? Math.Max(stepMin, (run?.Length ?? 360) / Math.Max(1, count - 1)) : 0;
                return Enumerable.Range(0, count).Select(i => inner.Clamp(orb.Offset(start + i * step, r))).ToList();
            }
        }
    }

    /// <summary>
    /// Points along an arc, numbered from the end people read first: the one nearer the top, or on a top or
    /// bottom edge (a tie) the left one. So at the right edge the shortcuts run top to bottom, at the top
    /// edge left to right, the way the full circle starts at the top.
    /// </summary>
    private static IReadOnlyList<PxPoint> Ordered(PxPoint orb, double r, double first, double step, int count)
    {
        var pts = Enumerable.Range(0, count).Select(i => orb.Offset(first + i * step, r)).ToList();
        if (count < 2) return pts;
        PxPoint a = pts[0], b = pts[^1];
        bool reverse = Math.Abs(a.Y - b.Y) > 0.5 ? b.Y < a.Y : b.X < a.X;
        if (reverse) pts.Reverse();
        return pts;
    }

    private readonly record struct Run(double Start, double Length);

    /// <summary>The open (all-inside) angular runs at a radius: the longest, or with a preferred direction the
    /// one containing it (if long enough), else the nearest long-enough one.</summary>
    private static Run? OpenRun(PxPoint orb, PxRect inner, double r, double? preferred, double need)
    {
        var open = new bool[360];
        for (int a = 0; a < 360; a++) open[a] = inner.Contains(orb.Offset(a, r));
        if (open.All(o => o)) return new Run(preferred is { } p ? p - 180 : -90, 360);

        // Collect circular runs, starting from a closed angle so none is split.
        int closed = Array.IndexOf(open, false);
        if (closed < 0) return null;
        var runs = new List<Run>();
        int startAt = -1;
        for (int k = 1; k <= 360; k++)
        {
            int a = (closed + k) % 360;
            if (open[a] && startAt < 0) startAt = closed + k;
            if ((!open[a] || k == 360) && startAt >= 0)
            {
                int end = open[a] ? closed + k : closed + k - 1;
                // Only sampled (known-open) angles count, so every point placed on the run is inside.
                runs.Add(new Run(startAt, end - startAt));
                startAt = -1;
            }
        }
        if (runs.Count == 0) return null;
        if (preferred is not { } pref) return runs.MaxBy(x => x.Length);

        var fitting = runs.Where(x => x.Length >= need).ToList();
        var pool = fitting.Count > 0 ? fitting : runs;
        return pool.MinBy(x => AngularDistance(pref, x));
    }

    private static double AngularDistance(double angle, Run run)
    {
        double rel = Normalize360(angle - run.Start);
        if (rel <= run.Length) return 0;
        return Math.Min(rel - run.Length, 360 - rel);
    }

    private static double Normalize360(double a) => ((a % 360) + 360) % 360;
}
