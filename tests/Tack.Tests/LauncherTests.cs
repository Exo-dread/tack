using System.IO;
using System.Text;
using Tack.Launcher.Core;

namespace Tack.Tests;

/// <summary>
/// The radial launcher's pure parts: config and launcher.json, shortcut rules (types never mix, no command
/// strings), the layout around the orb on every edge, corner and scale, the display map, the interaction
/// state machine (Shift+click vs Shift+drag, cancel, groups, display picking) and window placement maths.
/// Nothing here touches WPF or Win32, so these run on any OS.
/// </summary>
internal static class LauncherTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("launcher: off by default, six empty positions, and loading never creates launcher.json", DefaultIsOffAndNoFile);
        yield return ("launcher: config is cleaned (unknown kinds, a third level, too many children, duplicate ids)", NormalizeConfig);
        yield return ("launcher: launcher.json round-trips; damaged files fall back to the backup, then to the default", ConfigFileRoundTrip);
        yield return ("launcher: app, folder and website shortcuts are validated separately", ShortcutRulesByType);
        yield return ("launcher: targets are never joined into a command line", NoCommandStrings);
        yield return ("launcher: only http(s) addresses open as websites", UrlRules);
        yield return ("launcher: a full circle at fixed positions where there's room", FullCircle);
        yield return ("launcher: near edges, corners and between monitors, shortcuts stay on the orb's work area and apart", EdgesAndCorners);
        yield return ("launcher: a group's shortcuts fan out beyond it and stay on screen", ChildrenFan);
        yield return ("launcher: the display picker stays on screen and clear of the orb", SelectorPlacement);
        yield return ("launcher: the display map keeps the real arrangement (1, 2 and 4 displays, negative coordinates)", MonitorMapArrangement);
        yield return ("launcher: Shift+click opens for clicks; small jitter is still a click", ShiftClickOpens);
        yield return ("launcher: Shift+drag to a shortcut and release launches it", GestureLaunch);
        yield return ("launcher: releasing a gesture over nothing, an empty slot or the orb cancels", GestureCancels);
        yield return ("launcher: groups open while dragging, and releasing on a group just leaves it open", GestureGroups);
        yield return ("launcher: drag on past an app, onto a display, release: launches there", GestureDisplay);
        yield return ("launcher: launch modes (normal, preferred, ask) and a disconnected preferred display", LaunchModes);
        yield return ("launcher: click mode: click, click outside, press-and-slide-off, right-click for displays", ClickMode);
        yield return ("launcher: a missing target says why and launches nothing", MissingTarget);
        yield return ("launcher: keyboard: arrows, Enter, Esc, M for displays, digits", Keyboard);
        yield return ("launcher: keyboard with two displays: no picker opens by itself, the next key goes to the next shortcut, Esc closes", KeyboardWithTwoDisplays);
        yield return ("launcher: a damaged remembered display (device null) loads and opens without an error", DamagedMonitorRef);
        yield return ("launcher: network paths, with either slash, are recognised and never checked on disk", NetworkPaths);
        yield return ("launcher: a per-monitor-aware app is moved at its own size and rescales once, to the intended size", PerMonitorAwareMove);
        yield return ("launcher: in a corner, dragging toward a shortcut still picks it", CornerGesture);
        yield return ("launcher: every group shortcut is reachable in one drag, wherever the orb is", GroupGestureEverywhere);
        yield return ("launcher: main-window detection skips splash screens, tool windows and owned dialogs", MainWindowRules);
        yield return ("launcher: moving a window keeps its relative place, scales for DPI and fits the target", MoveWindowMaths);
        yield return ("launcher: remembered displays are found again, or reported gone", MonitorMatching);
    }

    // ================= Helpers =================

    private static Display Mon(double x, double y, double w, double h, double scale = 1, bool primary = false, string? device = null, double taskbar = 48) =>
        new(device ?? $"\\\\.\\DISPLAY{(int)(x + y + w)}", new PxRect(x, y, w, h), new PxRect(x, y, w, h - taskbar * scale), scale, primary);

    private sealed class FakeProbe(params string[] existing) : IFileProbe
    {
        private readonly HashSet<string> _files = existing.Where(e => !e.EndsWith('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = existing.Where(e => e.EndsWith('\\')).Select(e => e.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => _files.Contains(path);
        public bool DirectoryExists(string path) => _dirs.Contains(path.TrimEnd('\\'));
        public string ExpandEnvironment(string path) => path.Replace("%USERPROFILE%", @"C:\Users\Ra", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly FakeProbe Probe = new(@"C:\Apps\Code\Code.exe", @"C:\Apps\Revit.exe", @"C:\Users\Ra\Downloads\", @"C:\Projects\R06\");

    private static Shortcut App(string label, string target = @"C:\Apps\Code\Code.exe", string launch = "normal", MonitorRef? mon = null) =>
        new() { Label = label, Kind = "app", Target = target, Launch = launch, Monitor = mon };

    private static Shortcut Folder(string label, string target) => new() { Label = label, Kind = "folder", Target = target };

    private static Shortcut Group(string label, params Shortcut[] children) => new() { Label = label, Kind = "group", Children = children.ToList() };

    /// <summary>Slot 0 a group (Downloads, R06), slot 1 an app, slot 2 a website, slot 3 an "ask" app, 4 empty, 5 a missing app.</summary>
    private static LauncherConfig SampleConfig() => new LauncherConfig
    {
        Enabled = true,
        Slots =
        [
            Group("Files", Folder("Downloads", @"%USERPROFILE%\Downloads"), Folder("R06", @"C:\Projects\R06")),
            App("Code"),
            new Shortcut { Label = "GitHub", Kind = "url", Target = "https://github.com" },
            App("Revit", @"C:\Apps\Revit.exe", "ask"),
            null,
            App("Gone", @"C:\Apps\Gone.exe"),
        ],
    }.Normalize();

    private static readonly Display Main = Mon(0, 0, 2560, 1440, 1.5, primary: true, device: @"\\.\DISPLAY1");
    private static readonly Display Left = Mon(-1920, 200, 1920, 1080, 1, device: @"\\.\DISPLAY2");

    private static LauncherSession Session(LauncherConfig? config = null, PxPoint? orb = null, IReadOnlyList<Display>? displays = null)
    {
        var ds = displays ?? [Main];
        var d = ds[0];
        var scene = LauncherScene.Build(config ?? SampleConfig(), orb ?? new PxPoint(1280, 700), d.Work, d.Scale, ds, s => ShortcutRules.Problem(s, Probe));
        return new LauncherSession(scene);
    }

    private static PxPoint At(LauncherSession s, int slot, int child = -1) => s.Scene.CenterOf(new NodeRef(slot, child));

    private static PxPoint Toward(PxPoint from, PxPoint to, double fraction) =>
        new(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction);

    /// <summary>Shift+press on the orb and drag along a path (several moves, like a real mouse).</summary>
    private static void Drag(LauncherSession s, params PxPoint[] path)
    {
        var from = s.Scene.Orb;
        long t = 0;
        Check.Equal(Outcome.Changed, s.Begin(from, t).Outcome, "press opens");
        foreach (var to in path)
        {
            for (int i = 1; i <= 6; i++) s.Move(Toward(from, to, i / 6.0), t += 16);
            from = to;
        }
    }

    private static void AllApart(IReadOnlyList<PxPoint> pts, double min, string what)
    {
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
                Check.True(pts[i].DistanceTo(pts[j]) >= min - 0.5, $"{what}: {i} and {j} are {pts[i].DistanceTo(pts[j]):0.0}px apart (< {min:0.0})");
    }

    private static void AllInside(IReadOnlyList<PxPoint> pts, PxRect work, double r, string what)
    {
        foreach (var p in pts)
            Check.True(work.Contains(PxRect.Around(p, r, r)), $"{what}: ({p.X:0},{p.Y:0}) r={r:0} is outside {work}");
    }

    // ================= Config =================

    private static void DefaultIsOffAndNoFile()
    {
        var c = LauncherConfig.CreateDefault();
        Check.True(!c.Enabled, "off by default");
        Check.Equal(LauncherConfig.SlotCount, c.Slots.Count, "six positions");
        Check.True(c.Slots.All(s => s == null), "all empty");

        string dir = TempDir();
        var file = new LauncherConfigFile(dir);
        var loaded = file.Load();
        Check.True(!loaded.Enabled, "missing file: off");
        Check.True(!File.Exists(file.Path), "loading doesn't create launcher.json");
        Check.Equal(0, Directory.GetFiles(dir).Length, "nothing written to the data folder");
    }

    private static void NormalizeConfig()
    {
        var many = Enumerable.Range(0, 12).Select(i => App($"a{i}")).ToArray();
        var c = new LauncherConfig
        {
            Slots =
            [
                new Shortcut { Kind = "teleporter", Label = "x", Target = "y" },
                Group("G", [Group("nested", App("deep")), App("ok"), .. many]),
                new Shortcut { Id = "same", Kind = "URL", Target = "https://a.example", Launch = "preferred", Monitor = new MonitorRef { Width = 1, Height = 1 } },
                new Shortcut { Id = "same", Kind = "app", Target = @"C:\x\Thing.exe", Label = "  two\nlines  ", Launch = "preferred" },
                new Shortcut { Kind = "folder", Target = @"C:\Projects\R06\", Label = "" },
                App("six"), App("seven (dropped)"),
            ],
        }.Normalize();

        Check.Equal(6, c.Slots.Count, "exactly six slots");
        Check.True(c.Slots[0] == null, "unknown kind is dropped (slot stays put, empty)");
        var g = c.Slots[1]!;
        Check.Equal(LauncherConfig.MaxChildren, g.Children!.Count, "at most eight in a group");
        Check.True(g.Children.All(ch => !ch.IsGroup), "no third level");
        Check.Equal("ok", g.Children[0].Label, "the nested group was dropped, the rest kept in order");
        Check.Equal("url", c.Slots[2]!.Kind, "kinds are case-insensitive");
        Check.Equal("normal", c.Slots[2]!.Launch, "websites can't target a display");
        Check.True(c.Slots[2]!.Monitor == null, "...and keep no display");
        Check.True(c.Slots[2]!.Id != c.Slots[3]!.Id, "duplicate ids are replaced");
        Check.Equal("two lines", c.Slots[3]!.Label, "labels are one trimmed line");
        Check.Equal("normal", c.Slots[3]!.Launch, "'preferred' without a display falls back to normal");
        Check.Equal("R06", c.Slots[4]!.Label, "an empty label becomes the folder name");
        Check.Equal("six", c.Slots[5]!.Label, "extra slots are dropped");
        var again = LauncherConfigFile.Serialize(c.Clone().Normalize());
        Check.Equal(Encoding.UTF8.GetString(LauncherConfigFile.Serialize(c)), Encoding.UTF8.GetString(again), "normalising is idempotent");
    }

    private static void ConfigFileRoundTrip()
    {
        string dir = TempDir();
        var file = new LauncherConfigFile(dir);
        var c = SampleConfig();
        c.ReduceMotion = true;
        file.Save(c);
        var back = file.Load();
        Check.True(back.Enabled && back.ReduceMotion, "flags round-trip");
        Check.Equal(Encoding.UTF8.GetString(LauncherConfigFile.Serialize(c)), Encoding.UTF8.GetString(LauncherConfigFile.Serialize(back)), "content round-trips");
        string json = File.ReadAllText(file.Path);
        Check.True(json.Contains("\"slots\"") && json.Contains("\"kind\": \"group\""), "readable camelCase JSON");
        Check.True(!json.Contains("kindValue") && !json.Contains("canTargetDisplay"), "computed properties aren't written");

        // A second save keeps the first as .bak; damage the main file and the backup is used.
        c.ReduceMotion = false;
        file.Save(c);
        File.WriteAllText(file.Path, "{ not json");
        var errors = new List<string>();
        var recovered = file.Load((m, _) => errors.Add(m));
        Check.True(recovered.Enabled && recovered.ReduceMotion, "recovered from launcher.json.bak");
        Check.Equal(1, errors.Count, "the damaged file was reported once");

        File.WriteAllText(file.Path + ".bak", "[]");
        var fallback = file.Load();
        Check.True(!fallback.Enabled, "both unreadable: the default (off), never a crash");

        // BOM and comments from hand editing.
        var parsed = LauncherConfigFile.Parse(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ // mine\n \"enabled\": true, \"slots\": [ { \"kind\": \"url\", \"target\": \"https://b1m.example\" }, ] }")).ToArray());
        Check.True(parsed!.Enabled, "BOM and comments are fine");
        Check.Equal("b1m.example", parsed.Slots[0]!.Label, "a website's default label is its host");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tack-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ================= Shortcut rules =================

    private static void ShortcutRulesByType()
    {
        var ok = ShortcutRules.Plan(App("Code"), Probe, out var problem);
        Check.True(problem == null && ok != null, "an existing program is fine");
        Check.Equal(@"C:\Apps\Code\Code.exe", ok!.FileName, "program path");
        Check.Equal(@"C:\Apps\Code", ok.WorkingDirectory, "starts in its own folder");
        Check.Equal("Code.exe", ok.ExpectedImage, "its window is looked for by program name");
        Check.True(ok.CanPlace, "programs can be moved to a display");

        string? P(Shortcut s) => ShortcutRules.Problem(s, Probe);
        Check.True(P(App("x", @"C:\Apps\Missing.exe")) != null, "a missing program is reported");
        Check.True(P(App("x", @"""C:\Apps\Revit.exe""")) == null, "quotes from 'Copy as path' are removed");
        Check.True(P(App("x", @"Apps\Revit.exe")) != null, "relative paths are refused");
        Check.True(P(App("x", @"C:\Apps\notes.txt")) != null, "documents aren't programs");
        Check.True(P(App("x", @"C:\Apps\script.ps1")) != null, "scripts that open in an editor aren't programs");
        Check.True(P(App("x", "notepad.exe")) == null, "a bare program name is found by Windows");
        Check.True(P(App("x", "")) != null, "an empty target is reported");
        var bare = ShortcutRules.Plan(App("x", "notepad.exe"), Probe, out _)!;
        Check.True(bare.WorkingDirectory == null && bare.ExpectedImage == "notepad.exe", "bare names: no folder, looked for by name");

        Check.True(P(Folder("d", @"%USERPROFILE%\Downloads")) == null, "%VARIABLES% are expanded");
        Check.True(P(Folder("d", @"C:\Nope")) != null, "a missing folder is reported");
        Check.True(P(Folder("d", @"Downloads")) != null, "folders need a full path");
        Check.True(P(Folder("d", @"C:\Apps\Revit.exe")) != null, "a file is not a folder");
        var folder = ShortcutRules.Plan(Folder("d", @"C:\Projects\R06"), Probe, out _)!;
        Check.Equal(@"C:\Projects\R06", folder.FileName, "the folder itself is opened");
        Check.True(folder.Arguments == null, "with no arguments");
        Check.Equal("explorer.exe", folder.ExpectedImage, "in File Explorer");

        Check.True(P(Group("g")) != null, "groups don't launch");
        Check.True(P(new Shortcut { Kind = "url", Target = "C:\\Apps\\Revit.exe" }) != null, "a path is not a website");
    }

    private static void NoCommandStrings()
    {
        var withArgs = App("Code");
        withArgs.Arguments = @"""C:\Projects\R06"" --new-window & calc";
        var plan = ShortcutRules.Plan(withArgs, Probe, out var problem)!;
        Check.True(problem == null, "arguments are the user's own");
        Check.Equal(@"C:\Apps\Code\Code.exe", plan.FileName, "the program stays exactly the program");
        Check.Equal(withArgs.Arguments, plan.Arguments, "arguments are passed as they are, separately");

        Check.True(ShortcutRules.Problem(App("x", @"C:\Apps\Code\Code.exe"" & calc.exe"), Probe) != null, "a quote inside a path is refused");
        Check.True(ShortcutRules.Problem(App("x", "C:\\Apps\\Revit.exe\r\ncalc.exe"), Probe) != null, "line breaks in a path are refused");
        Check.True(ShortcutRules.Problem(App("x", @"C:\Apps\Revit.exe | calc"), Probe) != null, "pipes are refused");
        var multi = App("Code");
        multi.Arguments = "one\ntwo";
        Check.True(ShortcutRules.Problem(multi, Probe) != null, "line breaks in arguments are refused");
        Check.True(ShortcutRules.Problem(Folder("x", @"C:\Projects\R06"" /select,C:\x"), Probe) != null, "a quote inside a folder path is refused");
    }

    private static void UrlRules()
    {
        string? U(string s) => ShortcutRules.NormalizeUrl(s)?.AbsoluteUri;
        Check.Equal("https://github.com/", U("https://github.com"), "https");
        Check.Equal("http://intranet.local/wiki", U("http://intranet.local/wiki"), "http");
        Check.Equal("https://github.com/Exo-dread/tack", U("github.com/Exo-dread/tack"), "a bare host gets https://");
        Check.Equal("https://localhost:3000/", U("localhost:3000"), "host:port");
        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe", "mailto:a@b.c", "ftp://x.example",
                     "C:\\Apps\\Revit.exe", "ms-settings:display", "https://", "two words.com", "", "github", "\\\\server\\share" })
            Check.True(U(bad) == null, $"'{bad}' is not a website");
    }

    // ================= Layout =================

    private static void FullCircle()
    {
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var work = new PxRect(0, 0, 2560 * scale, 1392 * scale);
            var orb = new PxPoint(1000 * scale, 600 * scale);
            var pts = RadialLayout.Primary(orb, work, scale);
            Check.Equal(6, pts.Count, "six positions");
            for (int i = 0; i < 6; i++)
            {
                Check.True(Math.Abs(pts[i].DistanceTo(orb) - LauncherMetrics.RingRadius * scale) < 0.01, $"@{scale}: {i} on the ring");
                Check.True(Math.Abs(Diff(orb.AngleTo(pts[i]), -90 + 60 * i)) < 0.01, $"@{scale}: slot {i} at its fixed angle");
            }
            Check.True(Math.Abs(pts[0].X - orb.X) < 0.01 && pts[0].Y < orb.Y, "slot 1 is straight above the orb");
            var elsewhere = RadialLayout.Primary(new PxPoint(400 * scale, 900 * scale), work, scale);
            for (int i = 0; i < 6; i++)
                Check.True(Math.Abs(Diff(orb.AngleTo(pts[i]), new PxPoint(400 * scale, 900 * scale).AngleTo(elsewhere[i]))) < 0.01, "same angles anywhere with room (muscle memory)");
        }
    }

    private static double Diff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static void EdgesAndCorners()
    {
        var displays = new[]
        {
            Mon(0, 0, 1920, 1080, 1),
            Mon(0, 0, 2560, 1440, 1.5),
            Mon(-1920, 0, 1920, 1080, 1.25),        // left of the main display: negative coordinates
            Mon(1920, -1200, 1200, 1920, 1),        // portrait, above the main display's top
            Mon(-3840, 400, 3840, 2160, 2.25),
            Mon(0, 0, 800, 600, 2),                 // tiny: last-resort clamping
        };
        foreach (var d in displays)
        {
            double s = d.Scale, half = 30 * s; // the orb snaps 10 DIP from the edge: centre 30 DIP in
            var w = d.Work;
            var spots = new (string Name, PxPoint Orb)[]
            {
                ("left", new(w.X + half, w.Y + w.Height / 2)), ("right", new(w.Right - half, w.Y + w.Height / 2)),
                ("top", new(w.X + w.Width / 2, w.Y + half)), ("bottom", new(w.X + w.Width / 2, w.Bottom - half)),
                ("top-left", new(w.X + half, w.Y + half)), ("top-right", new(w.Right - half, w.Y + half)),
                ("bottom-left", new(w.X + half, w.Bottom - half)), ("bottom-right", new(w.Right - half, w.Bottom - half)),
                ("middle", w.Center),
            };
            bool tiny = w.Width / s < 500 || w.Height / s < 400;
            foreach (var (name, orb) in spots)
            {
                string what = $"{d.Bounds.Width}x{d.Bounds.Height}@{s} {name}";
                var pts = RadialLayout.Primary(orb, w, s);
                Check.Equal(6, pts.Count, what);
                AllInside(pts, w, LauncherMetrics.ItemDiameter / 2 * s, what);
                if (!tiny) AllApart(pts, LauncherMetrics.MinSpacing * s, what);
                foreach (var p in pts)
                    Check.True(p.DistanceTo(orb) >= (LauncherMetrics.OrbDiameter / 2 + LauncherMetrics.ItemDiameter / 2) * s, $"{what}: clear of the orb");
                if (!tiny && name != "middle")
                    foreach (var p in pts) Check.True(p.DistanceTo(orb) <= LauncherMetrics.MaxRingRadius * s + 0.5, $"{what}: within reach");
                // Positions run in one direction along the arc (no zig-zag), starting from the top end, or
                // the left end on a top or bottom edge: stable, predictable order for muscle memory.
                if (!tiny)
                {
                    var angles = pts.Select(p => orb.AngleTo(p)).ToList();
                    int sign = Math.Sign(Normalize(angles[1] - angles[0]) < 180 ? 1 : -1);
                    for (int i = 1; i < 6; i++)
                    {
                        double step = Normalize(sign * (angles[i] - angles[i - 1]));
                        Check.True(step is > 0 and < 180, $"{what}: one direction at {i}");
                    }
                    if (name is "left" or "right" or "middle") Check.True(pts[0].Y <= pts[5].Y + 0.5, $"{what}: starts at the top");
                    if (name is "top" or "bottom") Check.True(pts[0].X < pts[5].X, $"{what}: starts on the left");
                }
            }
        }
    }

    private static double Normalize(double a) => ((a % 360) + 360) % 360;

    private static void ChildrenFan()
    {
        foreach (var d in new[] { Mon(0, 0, 1920, 1080, 1), Mon(-1920, 0, 1920, 1080, 1.5) })
        {
            var w = d.Work;
            double s = d.Scale;
            foreach (var orb in new[] { w.Center, new PxPoint(w.X + 30 * s, w.Y + 30 * s), new PxPoint(w.Right - 30 * s, w.Bottom - 30 * s), new PxPoint(w.Right - 30 * s, w.Center.Y) })
            {
                var primary = RadialLayout.Primary(orb, w, s);
                for (int slot = 0; slot < 6; slot++)
                {
                    foreach (int n in new[] { 1, 3, 8 })
                    {
                        string what = $"@{s} orb ({orb.X:0},{orb.Y:0}) slot {slot} n={n}";
                        var kids = RadialLayout.Children(orb, primary[slot], n, w, s);
                        Check.Equal(n, kids.Count, what);
                        AllInside(kids, w, LauncherMetrics.ItemDiameter / 2 * s, what);
                        AllApart(kids, LauncherMetrics.MinSpacing * s, what);
                        foreach (var k in kids)
                        {
                            Check.True(k.DistanceTo(orb) > primary[slot].DistanceTo(orb) + 20 * s, $"{what}: beyond the first ring");
                            foreach (var p in primary) Check.True(k.DistanceTo(p) >= LauncherMetrics.ItemDiameter * s - 0.5, $"{what}: doesn't cover a primary shortcut");
                        }
                        if (orb == w.Center)
                        {
                            double toFirst = Diff(orb.AngleTo(kids[0]), orb.AngleTo(primary[slot]));
                            double toLast = Diff(orb.AngleTo(kids[^1]), orb.AngleTo(primary[slot]));
                            Check.True(Math.Abs(toFirst - toLast) < 1.5, $"{what}: centred on the group's direction");
                        }
                    }
                }
            }
        }
    }

    private static void SelectorPlacement()
    {
        var ds = new[] { Main, Left };
        foreach (var orb in new[] { Main.Work.Center, new PxPoint(45, 45), new PxPoint(2515, 1300), new PxPoint(2515, 600) })
        {
            var s = Session(orb: orb, displays: ds);
            foreach (var node in s.Scene.AllNodes().Where(s.Scene.CanPickDisplay))
            {
                var box = s.Scene.SelectorFor(node);
                string what = $"orb ({orb.X},{orb.Y}) {node}";
                Check.True(Main.Work.Contains(box), $"{what}: picker on screen");
                Check.True(box.OverlapArea(PxRect.Around(orb, s.Scene.OrbRadius, s.Scene.OrbRadius)) < 1, $"{what}: picker doesn't cover the orb");
                Check.True(s.Scene.Extent.Contains(box), $"{what}: inside the launcher window");
                // Nor the shortcuts on show with it: the ring, and the rest of its own group.
                double r = s.Scene.ItemRadius;
                foreach (var other in s.Scene.AllNodes().Where(o => o != node && (!o.IsChild || o.Slot == node.Slot) && s.Scene.Entry(o) != null))
                    Check.True(box.OverlapArea(PxRect.Around(s.Scene.CenterOf(other), r, r)) < 1, $"{what}: picker doesn't cover {other}");
                var tiles = s.Scene.TilesFor(node);
                Check.Equal(2, tiles.Count, $"{what}: a tile per display");
                foreach (var t in tiles) Check.True(box.Contains(t), $"{what}: tiles inside the picker");
            }
            Check.True(Main.Work.Contains(s.Scene.Extent), "the launcher window stays on the orb's work area");
        }
    }

    private static void MonitorMapArrangement()
    {
        var box = new PxRect(100, 100, 160, 90);
        var one = RadialLayout.MonitorMap([new PxRect(0, 0, 1920, 1080)], box);
        Check.True(box.Contains(one[0]) && one[0].Width > 150, "one display fills the box");

        // External main display, two portable monitors either side, the laptop below: the user's real layout.
        var four = new[]
        {
            new PxRect(0, 0, 2560, 1440),        // main external
            new PxRect(-1920, 180, 1920, 1080),  // portable 1, left (negative X)
            new PxRect(2560, 180, 1920, 1080),   // portable 2, right
            new PxRect(640, 1440, 1280, 800),    // laptop, below
        };
        var tiles = RadialLayout.MonitorMap(four, box);
        Check.Equal(4, tiles.Count, "four tiles");
        foreach (var t in tiles) Check.True(box.Contains(t), "inside the box");
        Check.True(tiles[1].Right <= tiles[0].X + 0.01 && tiles[0].Right <= tiles[2].X + 0.01, "left, main, right in order");
        Check.True(tiles[3].Y >= tiles[0].Bottom - 0.01, "the laptop is below the main display");
        Check.True(Math.Abs(tiles[0].Width / tiles[0].Height - 2560.0 / 1440) < 0.1, "proportions kept");
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++) Check.True(tiles[i].OverlapArea(tiles[j]) < 0.01, $"tiles {i} and {j} don't overlap");

        var two = RadialLayout.MonitorMap([new PxRect(0, 0, 1920, 1080), new PxRect(0, -1080, 1920, 1080)], box);
        Check.True(two[1].Bottom <= two[0].Y + 0.01, "a display above (negative Y) is drawn above");
        var numbers = MonitorMatch.Numbers([Mon(0, 0, 2560, 1440), Mon(-1920, 180, 1920, 1080), Mon(2560, 180, 1920, 1080), Mon(640, 1440, 1280, 800)]);
        Check.Equal("2,1,4,3", string.Join(",", numbers), "numbered left to right, as arranged (not Windows' numbering)");
    }

    // ================= Interaction =================

    private static void ShiftClickOpens()
    {
        var s = Session();
        s.Begin(s.Scene.Orb, 0);
        Check.Equal(SessionMode.Armed, s.Mode, "armed on press");
        s.Move(new PxPoint(s.Scene.Orb.X + 3, s.Scene.Orb.Y - 4), 500); // a shaky hand: under the threshold (7 DIP x 1.5)
        var r = s.Release(new PxPoint(s.Scene.Orb.X + 3, s.Scene.Orb.Y - 4), 1500);
        Check.Equal(SessionMode.Click, s.Mode, "a click, however long held, opens for clicks");
        Check.True(r.TakeFocus && r.Launch == null, "takes the keyboard, launches nothing");

        // Shift+click the orb again (it's under the launcher): closes, giving focus back.
        s.PointerDown(s.Scene.Orb);
        var close = s.PointerUp(s.Scene.Orb);
        Check.Equal(Outcome.Close, close.Outcome, "clicking the orb again closes (toggle)");
        Check.True(close.RestoreFocus, "and hands focus back");
    }

    private static void GestureLaunch()
    {
        var s = Session();
        Drag(s, At(s, 1));
        Check.Equal(SessionMode.Gesture, s.Mode, "moving past the threshold is a gesture");
        Check.Equal(new Hit(HitKind.Item, new NodeRef(1)), s.Highlight, "the app is highlighted");
        var r = s.Release(At(s, 1), 400);
        Check.Equal(Outcome.Launch, r.Outcome, "release launches");
        Check.Equal(new NodeRef(1), r.Launch!.Node, "the highlighted one");
        Check.True(r.Launch.Display == null, "normally (no display chosen)");

        // Imprecise: release between the orb and the shortcut, a little off its line, still picks it by direction.
        var s2 = Session();
        var near = Toward(s2.Scene.Orb, At(s2, 2), 0.6);
        Drag(s2, new PxPoint(near.X + 8, near.Y));
        Check.Equal(new NodeRef(2), s2.Release(new PxPoint(near.X + 8, near.Y), 300).Launch?.Node ?? new NodeRef(-1), "direction picks it");
    }

    private static void GestureCancels()
    {
        var s = Session();
        var empty = s.Scene.Orb.Offset(-90 + 60 * 0.5, LauncherMetrics.RingRadius * 1.5 * 2.6); // between slots, far out
        Drag(s, empty);
        var r = s.Release(empty, 300);
        Check.Equal(Outcome.Close, r.Outcome, "released over nothing: cancelled");
        Check.True(r.Launch == null && !r.RestoreFocus, "nothing launched; focus never moved");

        var s2 = Session();
        Drag(s2, At(s2, 4));
        Check.Equal(HitKind.EmptySlot, s2.Highlight.Kind, "an empty position");
        Check.Equal(Outcome.Close, s2.Release(At(s2, 4), 300).Outcome, "released on an empty position: cancelled");

        var s3 = Session();
        Drag(s3, At(s3, 1), s3.Scene.Orb);
        Check.Equal(Outcome.Close, s3.Release(s3.Scene.Orb, 600).Outcome, "dragged back to the orb: cancelled");

        var s4 = Session();
        Drag(s4, At(s4, 1));
        Check.Equal(Outcome.Close, s4.Key(LauncherKey.Escape).Outcome, "Esc mid-gesture cancels");
        Check.Equal(SessionMode.Closed, s4.Mode, "closed");
        Check.Equal(SessionResult.Nothing, s4.Release(At(s4, 1), 700), "and the release afterwards does nothing");
    }

    private static void GestureGroups()
    {
        var s = Session();
        Drag(s, At(s, 0));
        Check.Equal(0, s.OpenGroup ?? -1, "dragging onto a group opens it");
        Drag2(s, At(s, 0), At(s, 0, 1));
        Check.Equal(new Hit(HitKind.Item, new NodeRef(0, 1)), s.Highlight, "then onto one of its shortcuts");
        var r = s.Release(At(s, 0, 1), 900);
        Check.Equal(Outcome.Launch, r.Outcome, "release launches the group's shortcut");
        Check.Equal(@"C:\Projects\R06", r.Launch!.Entry.Target, "R06");

        var s2 = Session();
        Drag(s2, At(s2, 0));
        var r2 = s2.Release(At(s2, 0), 300);
        Check.True(r2.Outcome == Outcome.Changed && r2.TakeFocus && s2.Mode == SessionMode.Click && s2.OpenGroup == 0, "released on a group: it stays open for a click");

        // An open group isn't closed by merely passing over a neighbour on the way to its shortcuts...
        var s3 = Session();
        Drag(s3, At(s3, 0), At(s3, 1));
        Check.True(s3.OpenGroup == 0 && s3.Highlight.Kind != HitKind.Item, "passing over a neighbour keeps the group open");
        Check.Equal(Outcome.Close, s3.Release(At(s3, 1), 200).Outcome, "and releasing there launches nothing");
        // ...but resting on it switches,
        var s4 = Session();
        Drag(s4, At(s4, 0), At(s4, 1));
        s4.Tick(16 * 12 + LauncherSession.SwitchDwellMs + 1);
        Check.True(s4.OpenGroup == null && s4.Highlight == new Hit(HitKind.Item, new NodeRef(1)), "resting on another shortcut switches to it");
        Check.Equal(new NodeRef(1), s4.Release(At(s4, 1), 900).Launch?.Node ?? new NodeRef(-1), "and it can be launched");
        // ...and so does heading back toward the orb and out to another one.
        var s5 = Session();
        Drag(s5, At(s5, 0), Toward(s5.Scene.Orb, At(s5, 1), 0.4), At(s5, 1));
        Check.True(s5.OpenGroup == null && s5.Highlight == new Hit(HitKind.Item, new NodeRef(1)), "back toward the orb, then out: switched");
    }

    /// <summary>Continue a gesture already in progress.</summary>
    private static void Drag2(LauncherSession s, PxPoint from, PxPoint to)
    {
        long t = 1000;
        for (int i = 1; i <= 8; i++) s.Move(Toward(from, to, i / 8.0), t += 16);
    }

    private static void GestureDisplay()
    {
        var ds = new[] { Main, Left };
        var s = Session(displays: ds);
        var app = At(s, 1);
        Drag(s, app);
        Check.True(s.Selector == null, "not straight away");
        // Keep going outward past the app: the picker appears.
        var beyond = s.Scene.Orb.Offset(s.Scene.Orb.AngleTo(app), s.Scene.Orb.DistanceTo(app) + s.Scene.ItemRadius * 0.9);
        Drag2(s, app, beyond);
        Check.Equal(new NodeRef(1), s.Selector ?? new NodeRef(-1), "dragging on past the app reveals the display picker");
        var tile = s.Scene.TilesFor(new NodeRef(1))[1].Center;
        Drag2(s, beyond, tile);
        Check.Equal(new Hit(HitKind.Display, default, 1), s.Highlight, "the second display is highlighted");
        var r = s.Release(tile, 1600);
        Check.Equal(Outcome.Launch, r.Outcome, "release launches");
        Check.Equal(1, r.Launch!.Display ?? -1, "on that display");

        // Hovering (no outward movement) reveals it after the dwell.
        var s2 = Session(displays: ds);
        Drag(s2, At(s2, 1));
        s2.Tick(16 * 6 + 100); // the drag ended at 96 ms
        Check.True(s2.Selector == null, "not before the dwell");
        s2.Tick(16 * 6 + LauncherSession.GestureDwellMs + 1);
        Check.Equal(new NodeRef(1), s2.Selector ?? new NodeRef(-1), "after the dwell");
        // Releasing on the app itself (not a display) still uses its default.
        Check.True(s2.Release(At(s2, 1), 2000).Launch is { Display: null }, "release on the app: default behaviour");

        // One display: no picker, ever.
        var s3 = Session();
        Drag(s3, At(s3, 1));
        Drag2(s3, At(s3, 1), s3.Scene.Orb.Offset(s3.Scene.Orb.AngleTo(At(s3, 1)), 200));
        s3.Tick(10_000);
        Check.True(s3.Selector == null, "single display: no picker");

        // Websites never get a picker (their windows belong to the browser).
        var s4 = Session(displays: ds);
        Drag(s4, At(s4, 2));
        s4.Tick(10_000);
        Check.True(s4.Selector == null, "no picker for websites");
    }

    private static void LaunchModes()
    {
        var ds = new[] { Main, Left };
        var cfg = SampleConfig();
        cfg.Slots[1]!.Launch = "preferred";
        cfg.Slots[1]!.Monitor = MonitorRef.From(Left);

        var s = Session(cfg, displays: ds);
        Drag(s, At(s, 1));
        Check.Equal(1, s.Release(At(s, 1), 300).Launch?.Display ?? -1, "preferred: launches on the remembered display");

        var unplugged = Session(cfg, displays: [Main]);
        Drag(unplugged, At(unplugged, 1));
        var r = unplugged.Release(At(unplugged, 1), 300);
        Check.True(r.Outcome == Outcome.Launch && r.Launch!.Display == null, "remembered display gone: opens normally");

        // Ask: a click (or release on it) shows the picker instead of launching.
        var ask = Session(displays: ds);
        Drag(ask, At(ask, 3));
        var r2 = ask.Release(At(ask, 3), 300);
        Check.True(r2.Outcome == Outcome.Changed && ask.Selector == new NodeRef(3) && ask.Mode == SessionMode.Click, "ask: the picker opens and waits");
        var t0 = ask.Scene.TilesFor(new NodeRef(3))[0].Center;
        ask.Move(t0, 400);
        ask.PointerDown(t0);
        var r3 = ask.PointerUp(t0);
        Check.True(r3.Outcome == Outcome.Launch && r3.Launch!.Display == 0, "clicking a display launches there");

        var askOne = Session(displays: [Main]);
        Drag(askOne, At(askOne, 3));
        Check.True(askOne.Release(At(askOne, 3), 300).Launch is { Display: null }, "ask with one display: just opens");
    }

    private static void ClickMode()
    {
        LauncherSession Open(IReadOnlyList<Display>? ds = null)
        {
            var s = Session(displays: ds);
            s.Begin(s.Scene.Orb, 0);
            s.Release(s.Scene.Orb, 80);
            return s;
        }
        SessionResult Click(LauncherSession s, PxPoint p) { s.Move(p, 100); s.PointerDown(p); return s.PointerUp(p); }

        var a = Open();
        var r = Click(a, At(a, 1));
        Check.True(r.Outcome == Outcome.Launch && r.Launch!.Node == new NodeRef(1), "click a shortcut: it launches");

        var b = Open();
        Check.True(Click(b, At(b, 0)).Outcome == Outcome.Changed && b.OpenGroup == 0, "click a group: its shortcuts appear");
        var child = Click(b, At(b, 0, 0));
        Check.True(child.Outcome == Outcome.Launch && child.Launch!.Entry.Label == "Downloads", "click one of them: it launches");

        var c = Open();
        Click(c, At(c, 0));
        Click(c, At(c, 0));
        Check.True(c.OpenGroup == null, "clicking the group again folds it");
        var outside = new PxPoint(c.Scene.Extent.X + 2, c.Scene.Extent.Y + 2);
        var o = Click(c, outside);
        Check.True(o.Outcome == Outcome.Close && o.RestoreFocus, "click on empty space: closes and gives focus back");

        var d = Open();
        d.PointerDown(At(d, 1));
        Check.Equal(SessionResult.Nothing, d.PointerUp(At(d, 5)), "pressed on one shortcut, released on another: nothing");
        Check.Equal(SessionMode.Click, d.Mode, "still open");

        var e = Open([Main, Left]);
        e.SecondaryClick(At(e, 1));
        Check.Equal(new NodeRef(1), e.Selector ?? new NodeRef(-1), "right-click: choose a display for a normal shortcut");
        var t = e.Scene.TilesFor(new NodeRef(1))[1];
        var between = new PxPoint(e.Scene.SelectorFor(new NodeRef(1)).X + 3, e.Scene.SelectorFor(new NodeRef(1)).Y + 3);
        Check.Equal(SessionResult.Nothing, Click(e, between), "clicking the picker's frame does nothing");
        Check.True(Click(e, t.Center).Launch is { Display: 1 }, "then a display: launches there");

        var f = Open([Main, Left]);
        f.Move(At(f, 1), 1000);
        f.Tick(1000 + LauncherSession.ClickDwellMs + 1);
        Check.Equal(new NodeRef(1), f.Selector ?? new NodeRef(-1), "resting on a shortcut offers the picker without forcing it");
        Check.True(Click(f, At(f, 1)).Launch is { Display: null }, "clicking the shortcut itself still opens it normally");

        var g = Open();
        Check.True(Click(g, At(g, 4)).Outcome == Outcome.Changed && g.Message != null && g.Mode == SessionMode.Click, "an empty position explains itself");
    }

    private static void MissingTarget()
    {
        var s = Session();
        Drag(s, At(s, 5));
        var r = s.Release(At(s, 5), 300);
        Check.True(r.Outcome == Outcome.Changed && r.Launch == null, "nothing launches");
        Check.True(s.Message != null && s.Message.Contains("can't be found"), "it says why");
        Check.True(s.Mode == SessionMode.Click && r.TakeFocus, "and stays open (Esc closes)");
        Check.Equal(Outcome.Close, s.Key(LauncherKey.Escape).Outcome, "Esc closes");

        var shown = Session();
        Check.True(shown.Scene.ProblemOf(new NodeRef(5)) != null && shown.Scene.ProblemOf(new NodeRef(1)) == null, "problems are known before anything is clicked");
    }

    private static void Keyboard()
    {
        var s = Session(displays: [Main, Left]);
        s.Begin(s.Scene.Orb, 0);
        s.Release(s.Scene.Orb, 50);
        s.Key(LauncherKey.Next);
        Check.Equal(new NodeRef(0), s.Highlight.Node, "first shortcut");
        s.Key(LauncherKey.Previous);
        Check.Equal(new NodeRef(5), s.Highlight.Node, "wraps backwards, skipping the empty position");
        s.Key(LauncherKey.Next);
        s.Key(LauncherKey.Activate);
        Check.True(s.OpenGroup == 0 && s.Highlight.Node == new NodeRef(0, 0), "Enter on a group opens it and moves into it");
        s.Key(LauncherKey.Next);
        Check.Equal(new NodeRef(0, 1), s.Highlight.Node, "arrows move within the group");
        s.Key(LauncherKey.Next);
        Check.Equal(new NodeRef(0, 0), s.Highlight.Node, "and wrap within it");
        s.Key(LauncherKey.Escape);
        Check.True(s.OpenGroup == null && s.Highlight.Node == new NodeRef(0), "Esc folds the group first");
        s.Key(LauncherKey.Next);
        s.Key(LauncherKey.PickDisplay);
        Check.True(s.Selector == new NodeRef(1) && s.Highlight.Kind == HitKind.Display, "M shows the picker and moves into it");
        s.Key(LauncherKey.Next);
        var r = s.Key(LauncherKey.Activate);
        Check.True(r.Outcome == Outcome.Launch && r.Launch!.Node == new NodeRef(1) && r.Launch.Display != null, "Enter launches on the chosen display");

        var d = Session();
        d.Begin(d.Scene.Orb, 0);
        d.Release(d.Scene.Orb, 50);
        Check.True(d.Key(LauncherKey.Digit, 2).Launch?.Node == new NodeRef(1), "2 launches the second position");
        var e = Session();
        e.Begin(e.Scene.Orb, 0);
        e.Release(e.Scene.Orb, 50);
        Check.Equal(SessionResult.Nothing, e.Key(LauncherKey.Digit, 5), "an empty position's digit does nothing");
        var esc = e.Key(LauncherKey.Escape);
        Check.True(esc.Outcome == Outcome.Close && esc.RestoreFocus, "Esc closes and restores focus");
    }

    /// <summary>
    /// The real timings: the launcher ticks every 40 ms while it's open, and people take a second or so to
    /// press the first key. A highlight moved with the keys must not open the display picker by itself.
    /// </summary>
    private static void KeyboardWithTwoDisplays()
    {
        var ds = new[] { Main, Left };
        long t = 0;
        LauncherSession Open()
        {
            var s = Session(displays: ds);
            t = 0;
            s.Begin(s.Scene.Orb, t);
            s.Release(s.Scene.Orb, t += 80);
            return s;
        }
        void Wait(LauncherSession s, long ms)
        {
            for (long end = t + ms; t < end;) s.Tick(t += 40);
        }

        var a = Open();
        Wait(a, 1000);
        a.Key(LauncherKey.Next); // the group
        Wait(a, 300);
        a.Key(LauncherKey.Next); // Code: an app, which could open on either display
        Check.Equal(new Hit(HitKind.Item, new NodeRef(1)), a.Highlight, "the app is highlighted");
        Wait(a, LauncherSession.ClickDwellMs * 4);
        Check.True(a.Selector == null, "no display picker opens by itself after a key");
        a.Key(LauncherKey.Next);
        Check.Equal(new Hit(HitKind.Item, new NodeRef(2)), a.Highlight, "the next key goes to the next shortcut, not into a picker");
        a.Key(LauncherKey.Previous);
        Wait(a, 1000);
        var r = a.Key(LauncherKey.Activate);
        Check.True(r.Outcome == Outcome.Launch && r.Launch!.Node == new NodeRef(1) && r.Launch.Display == null, "Enter opens the highlighted shortcut, normally");

        var b = Open();
        b.Key(LauncherKey.Next);
        b.Key(LauncherKey.Next);
        Wait(b, 2000);
        var esc = b.Key(LauncherKey.Escape);
        Check.True(esc.Outcome == Outcome.Close && esc.RestoreFocus && b.Mode == SessionMode.Closed, "Esc closes, however long the keys rested on an app");

        // M still asks for the picker; Esc steps back out of it, and it doesn't come back by itself.
        var c = Open();
        c.Key(LauncherKey.Next);
        c.Key(LauncherKey.Next);
        c.Key(LauncherKey.PickDisplay);
        Check.Equal(new NodeRef(1), c.Selector ?? new NodeRef(-1), "M shows the picker");
        c.Key(LauncherKey.Escape);
        Wait(c, 2000);
        Check.True(c.Selector == null && c.Highlight == new Hit(HitKind.Item, new NodeRef(1)), "Esc closes the picker, and it stays closed");
        Check.Equal(Outcome.Close, c.Key(LauncherKey.Escape).Outcome, "the next Esc closes the launcher");

        // A "move" to where the pointer already is (WPF reports one when the content under a resting pointer
        // changes) says nothing new: the keyboard's highlight stays, and no picker is armed by it.
        var d = Open();
        d.Key(LauncherKey.Next);
        d.Key(LauncherKey.Next);
        Check.Equal(SessionResult.Nothing, d.Move(d.Scene.Orb, t += 40), "the pointer didn't move: nothing changes");
        Check.Equal(new Hit(HitKind.Item, new NodeRef(1)), d.Highlight, "the keyboard's highlight stays");

        // Resting the pointer on a shortcut still offers its picker (see ClickMode), keyboard or not.
        var e = Open();
        e.Key(LauncherKey.Next);
        e.Move(At(e, 1), t += 40);
        Wait(e, LauncherSession.ClickDwellMs + 80);
        Check.Equal(new NodeRef(1), e.Selector ?? new NodeRef(-1), "the pointer resting on an app offers the picker");
    }

    private static void DamagedMonitorRef()
    {
        var parsed = LauncherConfigFile.Parse(Encoding.UTF8.GetBytes("""
            { "enabled": true, "slots": [
              { "kind": "app", "label": "Code", "target": "C:\\Apps\\Code\\Code.exe", "launch": "preferred",
                "monitor": { "device": null, "x": -1920, "y": 200, "width": 1920, "height": 1080 } } ] }
            """))!;
        var m = parsed.Slots[0]!.Monitor!;
        Check.Equal("", m.Device, "a missing device name is read as none");
        Check.Equal(1, MonitorMatch.Find(m, [Main, Left]) ?? -1, "the display is still found by its bounds");
        Check.True(MonitorMatch.Find(new MonitorRef { Device = null!, Width = 5, Height = 5 }, [Main, Left]) == null, "matching never trips over a null name");

        var s = Session(parsed, displays: [Main, Left]);
        s.Begin(s.Scene.Orb, 0);
        s.Release(s.Scene.Orb, 80);
        var r = s.Key(LauncherKey.Digit, 1);
        Check.True(r.Outcome == Outcome.Launch && r.Launch!.Display == 1, "it opens, on the remembered display");
    }

    private static void NetworkPaths()
    {
        Check.True(WinPath.IsUnc(@"\\server\share") && WinPath.IsUnc("//server/share") && WinPath.IsUnc(@"\\?\C:\x"), "two slashes of either kind");
        Check.True(!WinPath.IsUnc(@"C:\Projects") && !WinPath.IsUnc(@"\Projects") && !WinPath.IsUnc("notepad.exe"), "anything else isn't");
        Check.True(RealFileProbe.IsNetwork("//192.0.2.1/share/app.exe") && RealFileProbe.IsNetwork(@"\\192.0.2.1\share"), "network paths, either slash");
        Check.True(!RealFileProbe.IsNetwork(Environment.SystemDirectory), "a local drive is checked as usual");

        // Never asked of the disk (an unreachable server would keep Windows busy for 20 s or more).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check.True(RealFileProbe.Instance.FileExists(@"\\192.0.2.1\share\Tool.exe") && RealFileProbe.Instance.DirectoryExists("//192.0.2.1/share/Projects"),
            "assumed to exist; Windows says so if not, when it's opened");
        Check.True(clock.ElapsedMilliseconds < 1000, $"and answered at once ({clock.ElapsedMilliseconds} ms)");

        // Written with forward slashes, a path is cleaned to the Windows form before anything looks at it.
        var probe = new FakeProbe(@"\\server\share\Tools\app.exe", @"\\server\projects\R06\");
        var app = ShortcutRules.Plan(App("x", "//server/share/Tools/app.exe"), probe, out var problem);
        Check.True(problem == null && app != null, "a forward-slash network program is fine");
        Check.Equal(@"\\server\share\Tools\app.exe", app!.FileName, "and opened as \\\\server\\share\\…");
        var folder = ShortcutRules.Plan(Folder("p", "//server/projects/R06"), probe, out problem);
        Check.True(problem == null && folder != null && folder.FileName == @"\\server\projects\R06", "a forward-slash network folder too");
        Check.Equal(@"C:\Apps\Code\Code.exe", ShortcutRules.Plan(App("x", "C:/Apps/Code/Code.exe"), Probe, out _)?.FileName, "and a local path written with forward slashes");
    }

    private static void PerMonitorAwareMove()
    {
        // This PC: the main display at 125% (left 0), a 100% display to its left.
        var main = Mon(0, 0, 2560, 1440, 1.25, primary: true, device: @"\\.\DISPLAY1");
        var left = Mon(-1920, 240, 1920, 1080, 1.0, device: @"\\.\DISPLAY2");
        var notepad = new PxRect(34, 299, 1938, 819);

        var intended = Placement.MoveTo(notepad, main, left);
        Check.True(Math.Abs(intended.Width - 1938 * 0.8) < 1 && Math.Abs(intended.Height - 819 * 0.8) < 1, "it should look the same size there (1550 x 655 at 100%)");
        var asked = Placement.Request(intended, main, left, appRescales: true);
        Check.True(asked.X == intended.X && asked.Y == intended.Y, "asked for the intended place");
        Check.True(Math.Abs(asked.Width - 1938) < 1 && Math.Abs(asked.Height - 819) < 1, "at its current size: the app rescales itself when it arrives");
        // Windows then sends WM_DPICHANGED (x0.8, keeping the top-left corner); the app takes that size.
        var arrived = new PxRect(asked.X, asked.Y, Math.Round(asked.Width * 0.8), Math.Round(asked.Height * 0.8));
        Check.True(!Placement.SizeIsOff(arrived, intended) && left.Work.Contains(arrived), "it ends where meant, at the size meant");
        // Asking for the scaled size as well scaled it twice: 1240 x 524, 36% less area. That's noticed (and corrected once).
        var twice = new PxRect(intended.X, intended.Y, Math.Round(intended.Width * 0.8), Math.Round(intended.Height * 0.8));
        Check.True(Placement.SizeIsOff(twice, intended), "a window scaled twice is noticed");
        Check.True(!Placement.SizeIsOff(new PxRect(0, 0, intended.Width + 3, intended.Height - 2), intended), "a few pixels either way aren't");

        // Back again (100% -> 125%): grows by 1.25 once, not 1.56.
        var back = Placement.MoveTo(arrived, left, main);
        var askedBack = Placement.Request(back, left, main, appRescales: true);
        var arrivedBack = new PxRect(askedBack.X, askedBack.Y, Math.Round(askedBack.Width * 1.25), Math.Round(askedBack.Height * 1.25));
        Check.True(!Placement.SizeIsOff(arrivedBack, back) && Math.Abs(arrivedBack.Width - 1938) <= 2, "back on the main display at its old size");

        // Too big for the target: clamped there, so asked for the clamped size before the rescale.
        var huge = Placement.MoveTo(new PxRect(0, 0, 2560, 1392), main, left);
        var askedHuge = Placement.Request(huge, main, left, appRescales: true);
        Check.True(!Placement.SizeIsOff(new PxRect(askedHuge.X, askedHuge.Y, askedHuge.Width * 0.8, askedHuge.Height * 0.8), huge) && left.Work.Contains(huge),
            "a window too big for the target still fits after it rescales");

        // Windows scales the others itself (DPI unaware, system aware): they're asked for the final size, as before.
        Check.Equal(intended, Placement.Request(intended, main, left, appRescales: false), "an app Windows scales: the final size");
        var same = Mon(2560, 0, 1920, 1080, 1.25);
        var sameScale = Placement.MoveTo(notepad, main, same);
        Check.Equal(sameScale, Placement.Request(sameScale, main, same, appRescales: true), "same scale: nothing to undo");
        var restored = Placement.RestoredOn(new PxRect(100, 100, 1600, 900), main, left);
        var askedRestored = Placement.Request(restored, main, left, appRescales: true);
        Check.True(Math.Abs(askedRestored.Width - 1600) < 1 && askedRestored.X == restored.X, "a maximised window's restored size gets the same treatment");
    }

    private static void CornerGesture()
    {
        var w = Main.Work;
        foreach (var orb in new[] { new PxPoint(w.X + 45, w.Y + 45), new PxPoint(w.Right - 45, w.Bottom - 45), new PxPoint(w.Right - 45, w.Center.Y) })
        {
            for (int slot = 0; slot < 6; slot++)
            {
                if (slot == 4) continue;
                var s = Session(orb: orb);
                var target = At(s, slot);
                Drag(s, target);
                var r = s.Release(target, 500);
                bool ok = slot == 0 ? r.Outcome == Outcome.Changed && s.OpenGroup == 0
                    : slot == 5 ? s.Message != null
                    : r.Launch?.Node == new NodeRef(slot);
                Check.True(ok, $"orb ({orb.X},{orb.Y}) slot {slot}: {r.Outcome} {r.Launch?.Node}");
            }
        }
    }

    private static void GroupGestureEverywhere()
    {
        var kids = Enumerable.Range(0, 8).Select(i => Folder($"f{i}", @"C:\Projects\R06")).ToArray();
        var cfg = new LauncherConfig
        {
            Slots = [Group("A", kids), App("Code"), Group("B", kids.Take(3).Select(k => k.Clone()).ToArray()), App("Code"), Group("C", kids.Take(5).Select(k => k.Clone()).ToArray()), App("Code")],
        }.Normalize();
        foreach (var d in new[] { Main, Left, Mon(0, 0, 1920, 1080, 1) })
        {
            var w = d.Work;
            double m = 30 * d.Scale;
            foreach (var orb in new[] { w.Center, new PxPoint(w.X + m, w.Center.Y), new PxPoint(w.Right - m, w.Center.Y), new PxPoint(w.Center.X, w.Y + m),
                         new PxPoint(w.Center.X, w.Bottom - m), new PxPoint(w.X + m, w.Y + m), new PxPoint(w.Right - m, w.Bottom - m), new PxPoint(w.Right - m, w.Y + m) })
            {
                foreach (int slot in new[] { 0, 2, 4 })
                {
                    int n = cfg.Slots[slot]!.Children!.Count;
                    for (int j = 0; j < n; j++)
                    {
                        var s = Session(cfg, orb, [d]);
                        Drag(s, At(s, slot));
                        Drag2(s, At(s, slot), At(s, slot, j));
                        var r = s.Release(At(s, slot, j), 2000);
                        Check.True(r.Launch?.Node == new NodeRef(slot, j),
                            $"@{d.Scale} orb ({orb.X:0},{orb.Y:0}) group {slot} child {j}: {r.Outcome} {r.Launch?.Node} highlight {s.Highlight}");
                    }
                }
            }
        }
    }

    // ================= Placement =================

    private static void MainWindowRules()
    {
        var main = new WindowTraits(Visible: true, IsRoot: true, HasOwner: false, ToolWindow: false, HasFrame: true, Cloaked: false, 1200, 800);
        Check.True(Placement.LooksLikeMainWindow(main), "an ordinary main window");
        Check.True(!Placement.LooksLikeMainWindow(main with { HasFrame = false }), "a frameless splash screen");
        Check.True(!Placement.LooksLikeMainWindow(main with { ToolWindow = true }), "a tool window");
        Check.True(!Placement.LooksLikeMainWindow(main with { HasOwner = true }), "an owned dialog");
        Check.True(!Placement.LooksLikeMainWindow(main with { Width = 200, Height = 120 }), "a small helper window");
        Check.True(!Placement.LooksLikeMainWindow(main with { Width = 300, Height = 200 }, scale: 2), "small at 200%");
        Check.True(!Placement.LooksLikeMainWindow(main with { Cloaked = true }), "a cloaked (hidden) window");
        Check.True(!Placement.LooksLikeMainWindow(main with { Visible = false }), "an invisible window");
        Check.True(!Placement.LooksLikeMainWindow(main with { IsRoot = false }), "a child window");
    }

    private static void MoveWindowMaths()
    {
        var laptop = Mon(0, 0, 1920, 1080, 1.25, primary: true);
        var external = Mon(-2560, -200, 2560, 1440, 1.0);
        var leftHalf = new PxRect(laptop.Work.X, laptop.Work.Y, 960, laptop.Work.Height);
        var moved = Placement.MoveTo(leftHalf, laptop, external);
        Check.True(external.Work.Contains(moved), "fits the target work area");
        Check.Equal(external.Work.X, moved.X, "a window on the left stays on the left");
        Check.True(Math.Abs(moved.Width - 960 * 1.0 / 1.25) < 1, "scaled by the DPI ratio (looks the same size)");
        Check.True(Placement.IsOn(moved, external) && !Placement.IsOn(moved, laptop), "now on the target");

        var centred = Placement.MoveTo(new PxRect(460, 240, 1000, 600), Mon(0, 0, 1920, 1080), Mon(1920, 0, 3840, 2160, 1.5));
        Check.True(Math.Abs(centred.Center.X - (1920 + 3840 / 2.0)) < 2, "a centred window stays centred");

        var huge = Placement.MoveTo(new PxRect(0, 0, 3800, 2100), Mon(0, 0, 3840, 2160), Mon(3840, 0, 1280, 800));
        Check.True(Mon(3840, 0, 1280, 800).Work.Contains(huge), "too big for the target: shrunk to fit");

        var restored = Placement.RestoredOn(new PxRect(100, 100, 1600, 900), laptop, external);
        Check.True(external.Work.Contains(restored) && Placement.IsOn(restored, external), "maximised windows get a restored rectangle on the target");

        var displays = new[] { laptop, external };
        Check.Equal(1, Placement.DisplayOf(new PxRect(-800, 100, 600, 400), displays), "display of a window");
        Check.Equal(0, Placement.DisplayOf(new PxRect(-100, 100, 1200, 400), displays), "straddling: where most of it is");
    }

    private static void MonitorMatching()
    {
        var a = Mon(0, 0, 1920, 1080, device: @"\\.\DISPLAY1");
        var b = Mon(1920, 0, 2560, 1440, device: @"\\.\DISPLAY2");
        var refB = MonitorRef.From(b);
        Check.Equal(1, MonitorMatch.Find(refB, [a, b]) ?? -1, "exact match");
        Check.Equal(0, MonitorMatch.Find(refB, [b with { Device = @"\\.\DISPLAY7" }, a]) ?? -1, "renumbered: same bounds");
        Check.Equal(0, MonitorMatch.Find(refB, [b with { Bounds = new PxRect(1920, 0, 1920, 1080) }]) ?? -1, "resolution changed: same device");
        Check.True(MonitorMatch.Find(refB, [a]) == null, "unplugged: not found");
        Check.True(MonitorMatch.Describe(a with { Primary = true }, 1).Contains("main"), "the main display is named");
    }
}
