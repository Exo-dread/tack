using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Launcher;
using Tack.Launcher.Core;
using Tack.Services;

namespace Tack.Tests;

/// <summary>
/// The recent-files card in the launcher window, in this process: shown far off screen and never activated, driven
/// through its session, on this PC's arrangement (the main display at 125%, a 100% display to its left). Also draws
/// it, in light and dark, for looking at (Tack.Tests.exe --render-recents &lt;folder&gt;).
/// </summary>
internal static class LauncherRecentsScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("recent files on screen: the card's files are named buttons; the caption names the app, then the highlighted file's folder", CardOnScreen);
        yield return ("recent files on screen: the card is inside the launcher, clear of the orb and its app, at every spot", CardInside);
    }

    private static readonly Display Main = new(@"\\.\DISPLAY1", new PxRect(0, 0, 2560, 1440), new PxRect(0, 0, 2560, 1380), 1.25, true);
    private static readonly Display Left = new(@"\\.\DISPLAY2", new PxRect(-1920, 240, 1920, 1080), new PxRect(-1920, 240, 1920, 1032), 1.0, false);

    /// <summary>Made-up projects (nothing from this PC).</summary>
    private static readonly string[] Models =
    [
        @"D:\Projects\R06 Harbour Hospital\Models\ARC\R06-ARC-Central.rvt",
        @"D:\Projects\R06 Harbour Hospital\Models\STR\R06-STR-Central.rvt",
        @"D:\Projects\R06 Harbour Hospital\Models\MEP\R06-MEP-Coordination Model.rvt",
        @"D:\Projects\R06 Harbour Hospital\Families\Door-Single-Flush.rfa",
        @"\\fileserver\projects\R05 Library\Models\R05-ARC-Library Extension.rvt",
        @"D:\Projects\R06 Harbour Hospital\Models\ARC\R06-ARC-Level 01 Furniture Layout and Equipment.rvt",
        @"C:\Users\Ra\Documents\Families\Title Block A1.rfa",
        @"D:\Projects\R04 School\R04-ARC-Central.rvt",
    ];

    private static LauncherConfig Config() => new LauncherConfig
    {
        Enabled = true,
        RecentFiles = true,
        Slots =
        [
            new Shortcut { Label = "Folders", Kind = "group", Children = [new Shortcut { Label = "Downloads", Kind = "folder", Target = @"C:\Nowhere\Downloads" }] },
            new Shortcut { Label = "Revit 2024", Kind = "app", Target = @"C:\Nowhere\Revit 2024\Revit.exe" },
            new Shortcut { Label = "Navisworks Manage 2027", Kind = "app", Target = @"C:\Nowhere\Navisworks Manage 2027\Roamer.exe" },
            new Shortcut { Label = "ACC", Kind = "url", Target = "https://acc.autodesk.com" },
            new Shortcut { Label = "AutoCAD 2026", Kind = "app", Target = @"C:\Nowhere\AutoCAD 2026\acad.exe" },
            new Shortcut { Label = "Code", Kind = "app", Target = @"C:\Nowhere\Code\Code.exe" },
        ],
    }.Normalize();

    private static readonly (string Name, Display On, PxPoint Orb)[] Spots =
    [
        ("main display, centre", Main, new PxPoint(1280, 690)),
        ("main display, right edge", Main, new PxPoint(2560 - 37.5, 500)),
        ("main display, top-right corner", Main, new PxPoint(2560 - 37.5, 37.5)),
        ("main display, bottom edge", Main, new PxPoint(1280, 1380 - 37.5)),
        ("left display, right edge", Left, new PxPoint(-30, 700)),
        ("left display, bottom-left corner", Left, new PxPoint(-1920 + 30, 240 + 1032 - 30)),
    ];

    private sealed class Opened(RadialWindow window, LauncherSession session) : IDisposable
    {
        public RadialWindow Window { get; } = window;
        public LauncherSession Session { get; } = session;
        public Canvas Canvas => (Canvas)Window.Content;
        public LauncherScene Scene => Session.Scene;

        public Point Dip(PxPoint p) => new((p.X - Scene.Extent.X) / Scene.Scale, (p.Y - Scene.Extent.Y) / Scene.Scale);

        public void Refresh()
        {
            Window.Refresh();
            Ui.Pump();
        }

        public Border? Card => Canvas.Children.OfType<Border>().FirstOrDefault(b => Panel.GetZIndex(b) == 18);
        public Border Label => Canvas.Children.OfType<Border>().Single(b => Panel.GetZIndex(b) == 30);
        public Rect Bounds(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);

        public void Dispose()
        {
            Window.Close();
            Ui.Pump();
        }
    }

    /// <summary>Opened with a Shift+click, Revit's files read, the pointer resting on Revit until they fan out.</summary>
    private static Opened Open(Display on, PxPoint orb, bool animate = false)
    {
        var module = new LauncherModule(TempDir(), () => (0, 0));
        var scene = LauncherScene.Build(Config(), orb, on.Work, on.Scale, [Main, Left], _ => null);
        var session = new LauncherSession(scene);
        session.Begin(orb, 0);
        session.Release(orb, 80);
        session.SetRecents(new NodeRef(1), Models.Select(m => new RecentFile(m)).ToList());
        var window = new RadialWindow(module, session, animate) { Left = -32000, Top = -32000 };
        window.Show();
        var opened = new Opened(window, session);
        opened.Refresh();
        session.Move(scene.CenterOf(new NodeRef(1)), 1000);
        session.Tick(1000 + LauncherSession.ClickDwellMs + 1);
        opened.Refresh();
        return opened;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tack-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<AutomationPeer> PeersUnder(AutomationPeer root)
    {
        root.ResetChildrenCache();
        var all = new List<AutomationPeer>();
        foreach (var child in root.GetChildren() ?? new List<AutomationPeer>())
        {
            all.Add(child);
            all.AddRange(PeersUnder(child));
        }
        return all;
    }

    private static void CardOnScreen()
    {
        Ui.Run(errors =>
        {
            using var l = Open(Spots[0].On, Spots[0].Orb);
            Check.Equal(new NodeRef(1), l.Session.Recents ?? new NodeRef(-1), "Revit's files are out");
            Check.True(l.Card != null && l.Card.IsVisible, "the card is on show");
            var rows = PeersUnder(UIElementAutomationPeer.CreatePeerForElement(l.Window)).Where(p => p.GetClassName() == "LauncherRecentFile").ToList();
            Check.Equal(Models.Length, rows.Count, "a button per file");
            Check.Equal("R06-ARC-Central.rvt", rows[0].GetName(), "named by the file's name");
            Check.True(rows.All(p => p.GetAutomationControlType() == AutomationControlType.Button && p.GetPattern(PatternInterface.Invoke) != null), "buttons that can be invoked");
            Check.True(rows[0].GetHelpText().StartsWith("Opens with Revit 2024.", StringComparison.Ordinal), "saying what opens it");

            var caption = Ui.Descendants<TextBlock>(l.Card!).First();
            Check.Equal("Recent in Revit 2024", caption.Text, "the caption names the app");
            var names = Ui.Descendants<TextBlock>(l.Card!).Select(t => t.Text).ToList();
            Check.True(names.Contains("R06-ARC-Central") && names.Contains(".rvt") && names.Contains(".rfa"), "each row: the name, then the extension");

            l.Session.Move(l.Scene.RecentRows(new NodeRef(1))[2].Center, 1700);
            l.Refresh();
            Check.Equal(@"D:\Projects\R06 Harbour Hospital\Models\MEP", caption.Text, "then the highlighted file's folder");
            l.Session.Move(l.Scene.RecentRows(new NodeRef(1))[5].Center, 1800);
            l.Refresh();
            Check.Equal(RecentFiles.ShortFolder(@"D:\Projects\R06 Harbour Hospital\Models\ARC"), caption.Text, "(shortened when long)");
            l.Session.Move(l.Scene.RecentRows(new NodeRef(1))[2].Center, 1900);
            l.Refresh();
            Check.True(l.Label.Visibility != Visibility.Visible, "no floating label over the card (the row has the name)");

            // A long name is cut short in its row, never wider than the card.
            var longRow = l.Card!.Child is Canvas frame ? frame.Children.OfType<Border>().ElementAt(5) : null;
            Check.True(longRow != null && longRow.ActualWidth <= l.Card.ActualWidth, "rows fit the card");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void CardInside()
    {
        Ui.Run(errors =>
        {
            var problems = new List<string>();
            foreach (var (spot, on, orb) in Spots)
            {
                using var l = Open(on, orb);
                if (l.Card is not { } card) { problems.Add($"{spot}: no card"); continue; }
                var bounds = l.Bounds(card);
                if (!new Rect(0, 0, l.Window.Width + 0.5, l.Window.Height + 0.5).Contains(bounds)) problems.Add($"{spot}: the card is outside the launcher ({bounds})");
                var o = l.Dip(l.Scene.Orb);
                if (bounds.IntersectsWith(new Rect(o.X - 20, o.Y - 20, 40, 40))) problems.Add($"{spot}: the card covers the orb");
                var a = l.Dip(l.Scene.CenterOf(new NodeRef(1)));
                var app = new Rect(a.X - 20, a.Y - 20, 40, 40);
                var overlap = Rect.Intersect(bounds, app);
                if (!overlap.IsEmpty && overlap.Width * overlap.Height > 80) problems.Add($"{spot}: the card covers Revit");
            }
            Check.True(problems.Count == 0, "the card is placed well:\n" + string.Join("\n", problems));
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ================= Drawing it =================

    /// <summary>Draws the card fanned out of Revit, the third file highlighted, in light and dark, as PNGs in <paramref name="folder"/>.</summary>
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        int result = 0;
        Ui.Run(errors =>
        {
            var palettes = Application.Current.Resources.MergedDictionaries;
            var original = palettes[0];
            try
            {
                foreach (bool light in new[] { true, false })
                {
                    palettes[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tack;component/Themes/{(light ? "Light" : "Dark")}.xaml") };
                    AccentPalette.Apply(Application.Current.Resources, (Color)ColorConverter.ConvertFromString(light ? "#005FB8" : "#60CDFF"));
                    foreach (var (name, spot) in new[] { ("centre", Spots[0]), ("right-edge", Spots[1]) })
                    {
                        using var l = Open(spot.On, spot.Orb);
                        l.Session.Move(l.Scene.RecentRows(new NodeRef(1))[2].Center, 1700);
                        l.Refresh();
                        string path = Path.Combine(folder, $"recents-{name}-{(light ? "light" : "dark")}.png");
                        Save(l, path, light);
                        Console.WriteLine(path);
                    }
                }
            }
            finally
            {
                palettes[0] = original;
            }
            if (errors.Count > 0)
            {
                Console.WriteLine(string.Join("\n", errors));
                result = 1;
            }
        });
        return result;
    }

    private static void Save(Opened l, string path, bool light)
    {
        double scale = l.Scene.Scale;
        double w = l.Window.Width, h = l.Window.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // A plain desktop behind it, and the orb (its own window, not the launcher's) where it is.
            dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xE9, 0xEC, 0xF1) : Color.FromRgb(0x1C, 0x1F, 0x26)), null, new Rect(0, 0, w, h));
            var o = l.Dip(l.Scene.Orb);
            var bead = new RadialGradientBrush(light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x3A, 0x3E, 0x48), light ? Color.FromRgb(0xDD, 0xE1, 0xE8) : Color.FromRgb(0x14, 0x16, 0x1B));
            dc.DrawEllipse(bead, new Pen(new SolidColorBrush(light ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1), o, 20, 20);
            dc.DrawEllipse(new SolidColorBrush((Color)Application.Current.Resources["AccentColor"]), null, o, 3, 3);
            // (The window's own coordinates, not just where its content happens to be.)
            var window = new VisualBrush(l.Canvas) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            dc.DrawRectangle(window, null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
