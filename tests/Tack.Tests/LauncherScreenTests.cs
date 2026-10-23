using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Tack.Launcher;
using Tack.Launcher.Core;

namespace Tack.Tests;

/// <summary>
/// The launcher window itself, in this process: shown far off screen and never activated (no input goes
/// anywhere), driven through its session the way the launcher drives it, on this PC's arrangement (the main
/// display at 125%, a 100% display to its left).
/// </summary>
internal static class LauncherScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("launcher on screen: every shortcut and display tile is a named button Narrator can find and invoke", Automation);
        yield return ("launcher on screen: a shortcut's name sits outward, clear of the orb and of the shortcuts beside it", LabelsOutward);
        yield return ("launcher on screen: with the display picker open, its caption names the shortcut and display, and no label covers it", PickerCaption);
        yield return ("launcher on screen: why a shortcut can't open is shown in full, clear of the orb and the other shortcuts", MessageInFull);
        yield return ("launcher settings on screen: '1 shortcut', a damaged entry opens, the main display is named, no error before anything's typed", SettingsCard);
    }

    private const string Missing = "This program can't be found. Was it moved or uninstalled?";

    private static readonly Display Main = new(@"\\.\DISPLAY1", new PxRect(0, 0, 2560, 1440), new PxRect(0, 0, 2560, 1380), 1.25, true);
    private static readonly Display Left = new(@"\\.\DISPLAY2", new PxRect(-1920, 240, 1920, 1080), new PxRect(-1920, 240, 1920, 1032), 1.0, false);

    private static LauncherConfig Config() => new LauncherConfig
    {
        Enabled = true,
        Slots =
        [
            new Shortcut { Label = "Folders", Kind = "group", Children = [new Shortcut { Label = "Downloads", Kind = "folder", Target = @"C:\Nowhere\Downloads" }, new Shortcut { Label = "Desktop", Kind = "folder", Target = @"C:\Nowhere\Desktop" }] },
            new Shortcut { Label = "File Explorer", Kind = "folder", Target = @"C:\Nowhere\Explorer" },
            new Shortcut { Label = "Navisworks Manage 2026", Kind = "folder", Target = @"C:\Nowhere\Navisworks" },
            new Shortcut { Label = "ACC", Kind = "url", Target = "https://acc.autodesk.com" },
            new Shortcut { Label = "Revit 2025", Kind = "app", Target = @"C:\Missing\Revit.exe" },
            new Shortcut { Label = "Revit 2024", Kind = "folder", Target = @"C:\Nowhere\Revit 2024" },
        ],
    }.Normalize();

    /// <summary>Where the orb can be. It snaps 10 DIP from an edge: its centre is then 30 DIP in.</summary>
    private static readonly (string Name, Display On, PxPoint Orb)[] Spots =
    [
        ("main display, centre", Main, new PxPoint(1280, 690)),
        ("main display, right edge", Main, new PxPoint(2560 - 37.5, 500)),
        ("main display, left edge", Main, new PxPoint(37.5, 690)),
        ("main display, top-right corner", Main, new PxPoint(2560 - 37.5, 37.5)),
        ("main display, bottom edge", Main, new PxPoint(1280, 1380 - 37.5)),
        ("left display, right edge", Left, new PxPoint(-30, 700)),
        ("left display, bottom-left corner", Left, new PxPoint(-1920 + 30, 240 + 1032 - 30)),
        ("left display, top edge", Left, new PxPoint(-960, 240 + 30)),
    ];

    private sealed class Launcher(RadialWindow window, LauncherSession session) : IDisposable
    {
        public RadialWindow Window { get; } = window;
        public LauncherSession Session { get; } = session;
        public Canvas Canvas => (Canvas)Window.Content;
        public LauncherScene Scene => Session.Scene;

        /// <summary>The window's coordinates (DIPs) of a point in the scene (physical pixels).</summary>
        public Point Dip(PxPoint p) => new((p.X - Scene.Extent.X) / Scene.Scale, (p.Y - Scene.Extent.Y) / Scene.Scale);

        public void Refresh()
        {
            Window.Refresh();
            Ui.Pump();
        }

        /// <summary>The floating label (shortcut names and messages): drawn above everything else.</summary>
        public Border Label => Canvas.Children.OfType<Border>().Single(b => Panel.GetZIndex(b) == 30);

        public Rect Bounds(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);

        public Rect Orb()
        {
            var o = Dip(Scene.Orb);
            double r = LauncherMetrics.OrbDiameter / 2;
            return new Rect(o.X - r, o.Y - r, 2 * r, 2 * r);
        }

        /// <summary>The shortcuts on show, by their automation name.</summary>
        public IEnumerable<(string Name, Rect Bounds)> Shortcuts() =>
            Canvas.Children.OfType<FrameworkElement>()
                .Where(e => e.Visibility == Visibility.Visible && UIElementAutomationPeer.CreatePeerForElement(e)?.GetClassName() == "LauncherShortcut")
                .Select(e => (AutomationProperties.GetName(e), Bounds(e)));

        public void Dispose()
        {
            Window.Close();
            Ui.Pump();
        }
    }

    /// <summary>Opened with a Shift+click (click mode), the way the launcher opens it, but far off screen.</summary>
    private static Launcher Open(Display on, PxPoint orb)
    {
        var module = new LauncherModule(TempDir(), () => (0, 0));
        var scene = LauncherScene.Build(Config(), orb, on.Work, on.Scale, [Main, Left], s => s.Target.StartsWith(@"C:\Missing", StringComparison.Ordinal) ? Missing : null);
        var session = new LauncherSession(scene);
        session.Begin(orb, 0);
        session.Release(orb, 80);
        var window = new RadialWindow(module, session, animate: false) { Left = -32000, Top = -32000 };
        window.Show();
        var launcher = new Launcher(window, session);
        launcher.Refresh();
        return launcher;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tack-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<AutomationPeer> PeersUnder(AutomationPeer root)
    {
        root.ResetChildrenCache(); // as UI Automation does when the content changes
        var all = new List<AutomationPeer>();
        foreach (var child in root.GetChildren() ?? new List<AutomationPeer>())
        {
            all.Add(child);
            all.AddRange(PeersUnder(child));
        }
        return all;
    }

    private static void Automation()
    {
        Ui.Run(errors =>
        {
            using var l = Open(Spots[0].On, Spots[0].Orb);
            // What Narrator walks: the window's automation tree.
            var tree = PeersUnder(UIElementAutomationPeer.CreatePeerForElement(l.Window));
            var shortcuts = tree.Where(p => p.GetClassName() == "LauncherShortcut").ToList();
            Check.Equal(8, shortcuts.Count, "six positions' shortcuts and the group's two are all there");
            var names = shortcuts.Select(p => p.GetName()).ToList();
            foreach (var expected in new[] { "1. Folders", "2. File Explorer", "3. Navisworks Manage 2026", "4. ACC", "5. Revit 2025", "6. Revit 2024", "Downloads", "Desktop" })
                Check.True(names.Contains(expected), $"'{expected}' is named ({string.Join(", ", names)})");
            foreach (var p in shortcuts)
            {
                Check.Equal(AutomationControlType.Button, p.GetAutomationControlType(), $"{p.GetName()}: a button");
                Check.True(p.GetPattern(PatternInterface.Invoke) != null, $"{p.GetName()}: can be invoked");
                Check.True(p.GetHelpText().Length > 0, $"{p.GetName()}: says what it opens");
                Check.True(p.GetChildren() == null || p.GetChildren().Count == 0, $"{p.GetName()}: one thing, not its icon and dots");
            }
            Check.True(shortcuts.Where(p => char.IsAsciiDigit(p.GetName()[0])).All(p => p.IsKeyboardFocusable()), "the ring's shortcuts can take the focus");
            Check.Equal(Missing, shortcuts.Single(p => p.GetName() == "5. Revit 2025").GetHelpText(), "a missing program says why");

            // The display picker's tiles.
            l.Session.SecondaryClick(l.Scene.CenterOf(new NodeRef(1)));
            l.Refresh();
            var tiles = PeersUnder(UIElementAutomationPeer.CreatePeerForElement(l.Window)).Where(p => p.GetClassName() == "LauncherDisplay").ToList();
            Check.Equal(2, tiles.Count, "a tile per display");
            Check.True(tiles.Any(p => p.GetName() == "Display 2 (main) · 2560×1440 · 125%") && tiles.Any(p => p.GetName() == "Display 1 · 1920×1080 · 100%"),
                $"each named ({string.Join(", ", tiles.Select(p => p.GetName()))})");
            Check.True(tiles.All(p => p.GetAutomationControlType() == AutomationControlType.Button && p.GetPattern(PatternInterface.Invoke) != null), "buttons that can be invoked");

            // Messages are read out as they appear.
            var said = Ui.Descendants<TextBlock>(l.Window).Where(t => AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Assertive).ToList();
            Check.Equal(1, said.Count, "the launcher's message is a live region");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void LabelsOutward()
    {
        Ui.Run(errors =>
        {
            var problems = new List<string>();
            foreach (var (spot, on, orb) in Spots)
            {
                using var l = Open(on, orb);
                void Hover(NodeRef node)
                {
                    l.Session.Move(l.Scene.CenterOf(node), 200);
                    l.Refresh();
                    string name = l.Scene.Entry(node)!.Label;
                    Check.True(l.Label.Visibility == Visibility.Visible && l.Label.ActualWidth > 0, $"{spot}: {name}'s label is shown");
                    var label = l.Bounds(l.Label);
                    Check.True(new Rect(0, 0, l.Window.Width + 0.5, l.Window.Height + 0.5).Contains(label), $"{spot}: {name}'s label stays inside the launcher");
                    if (label.IntersectsWith(l.Orb())) problems.Add($"{spot}: {name}'s label covers the orb");
                    foreach (var (other, bounds) in l.Shortcuts())
                    {
                        if (other.EndsWith(name, StringComparison.Ordinal)) continue;
                        var overlap = Rect.Intersect(label, bounds);
                        if (!overlap.IsEmpty && overlap.Width * overlap.Height > 1) problems.Add($"{spot}: {name}'s label covers {other} ({overlap.Width:0.#} x {overlap.Height:0.#} DIP)");
                    }
                }
                foreach (var node in l.Scene.FilledPrimary()) Hover(node);

                // With the group open (clicked), its shortcuts' names too.
                var group = l.Scene.CenterOf(new NodeRef(0));
                l.Session.Move(group, 300);
                l.Session.PointerDown(group);
                l.Session.PointerUp(group);
                l.Refresh();
                Check.Equal(0, l.Session.OpenGroup ?? -1, $"{spot}: the group is open");
                foreach (var child in l.Scene.ChildrenOf(0)) Hover(child);
            }
            Check.True(problems.Count == 0, "labels cover nothing:\n" + string.Join("\n", problems));
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void PickerCaption()
    {
        Ui.Run(errors =>
        {
            foreach (var (spot, on, orb) in Spots)
            {
                using var l = Open(on, orb);
                var explorer = new NodeRef(1);
                l.Session.SecondaryClick(l.Scene.CenterOf(explorer));
                l.Refresh();
                var caption = Ui.Descendants<TextBlock>(l.Window).First(t => t.Text.StartsWith("Open ", StringComparison.Ordinal));
                Check.Equal("Open File Explorer on", caption.Text, $"{spot}: the caption names the shortcut");
                Check.True(l.Label.Visibility != Visibility.Visible, $"{spot}: and no label goes over it");

                // Onto the main display's tile (number 2: displays are numbered left to right).
                var tile = l.Scene.TilesFor(explorer)[0];
                l.Session.Move(tile.Center, 400);
                l.Refresh();
                Check.Equal(new Hit(HitKind.Display, default, 0), l.Session.Highlight, $"{spot}: the main display's tile is highlighted");
                Check.Equal("Open File Explorer on Display 2 (main)", caption.Text, $"{spot}: the caption says where it'll open");
                Check.True(l.Label.Visibility != Visibility.Visible, $"{spot}: still no floating label");
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void SettingsCard()
    {
        Ui.Run(errors =>
        {
            string dir = TempDir();
            File.WriteAllText(Path.Combine(dir, LauncherConfigFile.FileName), """
                { "enabled": true, "slots": [
                  { "label": "Folders", "kind": "group", "children": [ { "label": "Downloads", "kind": "folder", "target": "C:\\Nowhere" } ] },
                  { "label": "Notepad", "kind": "app", "target": "notepad.exe", "launch": "preferred",
                    "monitor": { "device": null, "x": 0, "y": 0, "width": 2560, "height": 1440 } },
                  null, null, null, null ] }
                """);
            var module = new LauncherModule(dir, () => (0, 0));
            var view = new LauncherSettingsView(module, null!, null!); // the shell and panel are only for Browse and Remove's Undo
            using var window = Ui.Host(view);
            string Shown() => string.Join(" | ", Ui.Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text));
            void Press(Func<Button, bool> which)
            {
                Ui.Descendants<Button>(view).First(which).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Ui.Pump();
            }
            void PressNamed(string name) => Press(b => AutomationProperties.GetName(b) == name);
            TextBlock Problem() => Ui.Descendants<TextBlock>(view).Single(t => AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
            TextBox Box(string name) => Ui.Descendants<TextBox>(view).Single(b => AutomationProperties.GetName(b) == name);

            Check.True(Shown().Contains("Group · 1 shortcut") && !Shown().Contains("1 shortcuts"), "a group of one says '1 shortcut'\n" + Shown());

            // A remembered display without a device name (a hand-edited file) opens in the editor like any other.
            PressNamed("Edit Notepad");
            Check.Equal("Notepad", Box("Name").Text, "the editor opened");
            var mainDisplay = Ui.Descendants<RadioButton>(view).Where(r => (r.Content as string)?.EndsWith("(main)", StringComparison.Ordinal) == true).ToList();
            Check.Equal(1, mainDisplay.Count, "'Opens on' says which display is the main one");
            Press(b => b.Content as string == "Cancel");

            // A new shortcut: nothing is wrong before anything's typed.
            PressNamed("Add a shortcut at position 3");
            Check.True(Problem().Visibility != Visibility.Visible, "no error on an empty new shortcut");
            Ui.Type(Box("Program"), @"C:\Nowhere\tool.exe");
            Check.True(Problem().Visibility == Visibility.Visible && Problem().Text.Contains("can't be found"), "once typed, a problem is said");
            Box("Program").Text = "";
            Ui.Pump();
            Check.True(Problem().Visibility == Visibility.Visible && Problem().Text == "Choose a program.", "and an emptied field too");
            Ui.Descendants<RadioButton>(view).First(r => r.Content as string == "Website").IsChecked = true;
            Ui.Pump();
            Check.True(Problem().Visibility != Visibility.Visible, "another type: nothing wrong until something's typed");
            Press(b => b.Content as string == "Done");
            Check.True(Problem().Visibility == Visibility.Visible && Problem().Text.Contains("web address"), "Done says what's missing");
            Check.True(!File.ReadAllText(Path.Combine(dir, LauncherConfigFile.FileName)).Contains("\"url\""), "and nothing was saved");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void MessageInFull()
    {
        Ui.Run(errors =>
        {
            foreach (var (spot, on, orb) in Spots)
            {
                using var l = Open(on, orb);
                var r = l.Session.Key(LauncherKey.Digit, 5); // Revit 2025, not installed
                Check.True(r.Outcome == Outcome.Changed && l.Session.Message == Missing, $"{spot}: it says why");
                l.Refresh();
                var text = (TextBlock)l.Label.Child;
                Check.Equal(Missing, text.Text, $"{spot}: the whole message");
                Check.True(text.TextWrapping == TextWrapping.Wrap && text.ActualWidth <= 240.5, $"{spot}: wrapped, not cut off");
                Check.True(text.ActualHeight > text.FontSize * 2, $"{spot}: on two lines ({text.ActualHeight:0.#} DIP high)");
                Check.True(!l.Bounds(l.Label).IntersectsWith(l.Orb()), $"{spot}: clear of the orb");
                foreach (var (other, bounds) in l.Shortcuts())
                {
                    var overlap = Rect.Intersect(l.Bounds(l.Label), bounds);
                    Check.True(overlap.IsEmpty || overlap.Width * overlap.Height <= 1, $"{spot}: the message doesn't cover {other}");
                }
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }
}
