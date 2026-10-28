using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Sync;
using Tack.Windows;

namespace Tack.Tests;

/// <summary>
/// The spaces strip under the panel's header, as pills: their shape and spacing, + in sight at the end, the space
/// on show filled with the accent, what a screen reader hears, the keys, and the change of pill as it animates.
/// The panel in this process, shown far off screen and never activated.
///
///   dotnet run --project tests/Tack.Tests -c Release -- --render-pills [folder]
///
/// draws the strip in each theme, with and without glass, in the accent presets, at rest, hovered, with the focus
/// ring, with many spaces, while the pill changes, and with the new-space box, to look at.
/// </summary>
internal static class SpaceStripTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("space pills on screen: only Main, no strip at all; a second space, a strip of pills; not over Settings", Golden);
        yield return ("space pills on screen: 24 px, fully rounded, 6 px apart, no dots; + a round pill of its own, always in sight", Shape);
        yield return ("space pills on screen: the space on show is the one filled with the accent, its name in the accent's text colour", Filled);
        yield return ("space pills on screen: a radio group named Spaces, each pill its space's name and Alt key; one tab stop", Semantics);
        yield return ("space pills on screen: arrows, Home and End move along and show the space they land on", Keys);
        yield return ("space pills on screen: the change of pill fades and settles in under 260 ms, then the template holds it", Settles);
        yield return ("space pills on screen: More spaces past a thousand is a pill too, and the new-space box is rounded to match", MoreAndNew);
    }

    // ---------------------------------------------------------------- the panel

    /// <summary>A Tack shell over a temp folder, its panel shown far off screen and never activated.</summary>
    private sealed class Shell : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tack-pill-tests", Guid.NewGuid().ToString("N"));
        private readonly FolderSync _sync;
        private readonly StateStore _store;

        public Shell(Action<Settings>? settings = null, Action<Workspace>? seed = null)
        {
            Directory.CreateDirectory(_folder);
            var store = _store = new StateStore(_folder);
            Ws = store.Attach(new StateDocument { Settings = new Settings { Hotkey = "", Theme = "light" } });
            settings?.Invoke(Ws.Settings);
            seed?.Invoke(Ws);
            _sync = new FolderSync(Ws, store);
            App = new AppShell(Ws, store, _sync);
            new WindowInteropHelper(Panel).EnsureHandle();
            // As AppShell.Start does (which isn't run here: it touches launch at startup); later changes go through Settings.
            ThemeManager.Apply(Ws.Settings.Theme);
            AccentPalette.Apply(Application.Current.Resources, AccentPalette.Resolve(Ws.Settings.Accent, ThemeManager.IsLight));
            Panel.ApplyGlass(Ws.Settings.Glass);
            Panel.ShowActivated = false;
            Panel.Left = -32000;
            Panel.Top = -32000;
            Panel.Show();
            ScreenGeometry.Place(Panel.Handle, -32000, -32000, 340, 480);
            Ui.Pump();
        }

        public Workspace Ws { get; }
        public AppShell App { get; }
        public PanelWindow Panel => App.Panel;

        public List<RadioButton> Pills => Panel.StripTabs.Children.OfType<RadioButton>().ToList();

        public RadioButton Pill(string name) => Pills.First(p => (string)p.Content == name);

        public void Dispose()
        {
            Ws.Settings.Theme = "light";
            Ui.Pump();
            Panel.Hide();
            App.Dispose();
            _sync.Dispose();
            Ui.Pump();
            _store.Flush();
            try { Directory.Delete(_folder, recursive: true); } catch { /* the log may still be open */ }
        }
    }

    private static void Spaces(Workspace ws)
    {
        foreach (string name in new[] { "Personal", "Dev", "Work" }) ws.AddSpace(name);
    }

    private static Border Part(Control c, string name) => (Border)c.Template.FindName(name, c);

    private static Rect Bounds(FrameworkElement e, Visual to) =>
        e.TransformToAncestor(to).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    private static void Golden() => Ui.Run(errors =>
    {
        using var shell = new Shell();
        var panel = shell.Panel;
        Check.Equal(Visibility.Collapsed, panel.Strip.Visibility, "only Main: no strip");
        Check.Equal(0, shell.Pills.Count, "and no pills made");

        var work = shell.Ws.AddSpace("Work");
        Ui.Pump();
        Check.Equal(Visibility.Visible, panel.Strip.Visibility, "a second space: the strip");
        Check.Sequence(new[] { "All", "Main", "Work" }, shell.Pills.Select(p => (string)p.Content).ToList(), "All, then each space");
        Check.True(panel.StripTabs.Children.Cast<UIElement>().All(c => c is RadioButton), "pills only, no dots between them");

        panel.OpenSettings();
        Ui.Pump();
        Check.Equal(Visibility.Collapsed, panel.Strip.Visibility, "not over Settings");
        panel.CloseSettings(animate: false);
        Ui.Pump();
        Check.Equal(Visibility.Visible, panel.Strip.Visibility, "back once Settings closes");

        shell.Ws.DeleteSpace(work.Id);
        Ui.Pump();
        Check.Equal(Visibility.Collapsed, panel.Strip.Visibility, "only Main again: no strip");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Shape() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: Spaces);
        var panel = shell.Panel;
        panel.UpdateLayout();
        var pills = shell.Pills;
        Check.Equal(5, pills.Count, "All, Main and three spaces");
        foreach (var pill in pills)
        {
            Check.Equal(24.0, pill.ActualHeight, $"{pill.Content}: 24 px tall");
            var bd = Part(pill, "Bd");
            Check.Equal(new CornerRadius(12), bd.CornerRadius, $"{pill.Content}: rounded by half its height");
            Check.Equal(new CornerRadius(12), Part(pill, "Sel").CornerRadius, $"{pill.Content}: its filled layer too");
            Check.True(pill.ActualWidth >= 24 + 12, $"{pill.Content}: room either side of its name ({pill.ActualWidth})");
            Check.True(pill.FocusVisualStyle == panel.FindResource("PillFocusRing"), $"{pill.Content}: a focus ring that follows the pill");
        }
        for (int i = 1; i < pills.Count; i++)
        {
            double gap = Bounds(pills[i], panel.StripTabs).Left - Bounds(pills[i - 1], panel.StripTabs).Right;
            Check.True(Math.Abs(gap - PanelWindow.PillGap) < 0.01, $"{pills[i].Content}: 6 px after the one before ({gap})");
        }

        // The focus ring (3 px out) has room inside the scroller, which clips it (give or take the
        // layout rounding to whole device pixels: 2.67 at 150%).
        var first = Bounds(pills[0], panel.StripScroller);
        const double ring = 2.5;
        Check.True(first.Left >= ring && first.Top >= ring && panel.StripScroller.ActualHeight - first.Bottom >= ring, $"room for the ring ({first})");
        // In line with the panel's content (the add box and the list start 16 px in).
        // Within a device pixel: layout rounds to whole ones (2/3 px at 150%).
        double left = Bounds(pills[0], panel.Chrome).Left;
        Check.True(Math.Abs(left - 16) <= 0.7, $"the first pill lines up with the content ({left})");

        var plus = panel.NewSpaceButton;
        Check.True(plus.ActualWidth == 24 && plus.ActualHeight == 24, $"+ is round, 24 px ({plus.ActualWidth}x{plus.ActualHeight})");
        Check.Equal(new CornerRadius(12), Part(plus, "Bd").CornerRadius, "+ rounded like the pills");
        double right = panel.Chrome.ActualWidth - Bounds(plus, panel.Chrome).Right;
        Check.True(Math.Abs(right - 16) <= 0.7, $"+ ends 16 px from the edge, as the content does ({right})");

        // Many spaces: they scroll, + stays in sight.
        for (int i = 0; i < 30; i++) shell.Ws.AddSpace($"Project {i}");
        Ui.Pump();
        panel.UpdateLayout();
        Check.True(panel.StripScroller.ScrollableWidth > 0, "many spaces scroll sideways");
        var plusNow = Bounds(plus, panel.Chrome);
        Check.True(plusNow.Right <= panel.Chrome.ActualWidth && plusNow.Left > Bounds(panel.StripScroller, panel.Chrome).Right - 0.5, "+ stays in sight after them");
        Check.True(panel.StripScroller.OpacityMask != null, "fading at the edge with more beyond");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Filled() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: Spaces);
        var panel = shell.Panel;
        foreach (string theme in new[] { "light", "dark" })
        {
            // The presets, plus hand-edited mid-tone accents (and this PC's own Windows accent as "system"),
            // where white text used to pass a 3:1 rule meant for ticks but not 4.5:1 for a 12 px label.
            foreach (string accent in AccentPalette.Presets.Select(p => p.Value).Concat(new[] { "#009FAA", "#E74856", "#8E8CD8", "#0099BC", "#C239B3" }))
            {
                shell.Ws.Settings.Theme = theme;
                shell.Ws.Settings.Accent = accent;
                Ui.Pump();
                var main = shell.Pill("Main");
                Check.True(main.IsChecked == true, "Main on show");
                Check.Equal(1.0, Part(main, "Sel").Opacity, $"{theme} {accent}: Main filled");
                var fill = ((SolidColorBrush)Part(main, "Sel").Background).Color;
                Check.Equal((Color)Application.Current.Resources["AccentColor"], fill, $"{theme} {accent}: with the accent");
                var text = ((SolidColorBrush)System.Windows.Documents.TextElement.GetForeground(Part(main, "Sel"))).Color;
                Check.True(Application.Current.Resources["OnAccentTextBrush"] is SolidColorBrush onText && onText.Color == text, $"{theme} {accent}: its name in the accent's text colour");
                Check.True(Ratio(text, fill) >= 4.5, $"{theme} {accent}: which reads as text ({Ratio(text, fill):F2}:1)");
                foreach (var other in shell.Pills.Where(p => p != main))
                    Check.Equal(0.0, Part(other, "Sel").Opacity, $"{theme} {accent}: {other.Content} not filled");
            }
        }
        shell.Ws.Settings.Accent = "system";
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Semantics() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: Spaces);
        var panel = shell.Panel;
        var group = UIElementAutomationPeer.CreatePeerForElement(panel.StripTabs);
        Check.Equal(AutomationControlType.Group, group.GetAutomationControlType(), "a group");
        Check.Equal("Spaces", group.GetName(), "named Spaces");
        var pills = shell.Pills;
        for (int i = 0; i < pills.Count; i++)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(pills[i]);
            Check.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType(), $"{pills[i].Content}: a radio button");
            Check.Equal((string)pills[i].Content, peer.GetName(), $"{pills[i].Content}: named for its space, once");
            Check.Equal($"Alt+{i}", AutomationProperties.GetAcceleratorKey(pills[i]), $"{pills[i].Content}: its key");
            Check.Equal(pills[i].IsChecked == true, pills[i].IsTabStop, $"{pills[i].Content}: Tab stops only at the space on show");
            Check.Equal("Spaces", pills[i].GroupName, "one group");
        }
        Check.Equal("New space", AutomationProperties.GetName(panel.NewSpaceButton), "+ says what it does");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Press(RadioButton from, Key key) =>
        from.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(from), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void Keys() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: Spaces);
        var panel = shell.Panel;
        string Shown() => (string)shell.Pills.Single(p => p.IsChecked == true).Content;
        Check.Equal("Main", Shown(), "Main to start");
        Press(shell.Pill("Main"), System.Windows.Input.Key.Right);
        Ui.Pump();
        Check.Equal("Personal", Shown(), "Right: the next");
        Check.Equal(shell.Ws.Settings.SpaceOnShow, (string)shell.Pill("Personal").Tag, "and on show");
        Press(shell.Pill("Personal"), System.Windows.Input.Key.End);
        Ui.Pump();
        Check.Equal("Work", Shown(), "End: the last");
        Press(shell.Pill("Work"), System.Windows.Input.Key.Right);
        Ui.Pump();
        Check.Equal("All", Shown(), "Right past the end: round to the first");
        Press(shell.Pill("All"), System.Windows.Input.Key.Left);
        Ui.Pump();
        Check.Equal("Work", Shown(), "Left before the first: round to the last");
        Press(shell.Pill("Work"), System.Windows.Input.Key.Home);
        Ui.Pump();
        Check.Equal("All", Shown(), "Home: the first");
        Check.True(shell.Pill("All").IsTabStop && !shell.Pill("Work").IsTabStop, "the tab stop follows");
        Press(shell.Pill("All"), System.Windows.Input.Key.Down);
        Ui.Pump();
        Check.Equal("Main", Shown(), "Down: the next too");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Settles() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: Spaces);
        var panel = shell.Panel;
        double[] durations = [PanelWindow.PillFadeIn, PanelWindow.PillFadeOut, PanelWindow.PillSettle];
        Check.True(durations.All(ms => ms >= 90 && ms <= 260), "every animation between 90 and 260 ms (docs/DESIGN.md)");
        var main = shell.Pill("Main");
        var dev = shell.Pill("Dev");
        panel.ShowSpace((string)dev.Tag);
        Ui.Pump();
        var on = Part(dev, "Sel");
        var off = Part(main, "Sel");
        if (SystemParameters.ClientAreaAnimation)
        {
            Check.True(on.HasAnimatedProperties && off.HasAnimatedProperties, "the fill moves across, animated");
            Ui.WaitUntil(() => !on.HasAnimatedProperties && !off.HasAnimatedProperties, 2000);
            Check.True(!on.HasAnimatedProperties && !off.HasAnimatedProperties, "and lets go when done");
        }
        else
        {
            Check.True(!on.HasAnimatedProperties && !off.HasAnimatedProperties, "Windows doesn't animate: at once");
        }
        Check.Equal(1.0, on.Opacity, "Dev filled");
        Check.Equal(0.0, off.Opacity, "Main not");

        // Back and forth faster than it settles: it ends where the space on show is.
        panel.ShowSpace(SpaceField.Main);
        panel.ShowSpace((string)dev.Tag);
        panel.ShowSpace(SpaceField.Main);
        Ui.WaitUntil(() => !on.HasAnimatedProperties && !off.HasAnimatedProperties, 2000);
        Check.Equal(1.0, off.Opacity, "Main filled in the end");
        Check.Equal(0.0, on.Opacity, "Dev not");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void MoreAndNew() => Ui.Run(errors =>
    {
        using var shell = new Shell(seed: ws =>
        {
            for (int i = 0; i < SpaceView.MaxListed + 3; i++) ws.AddSpace($"S{i}");
        });
        var panel = shell.Panel;
        var more = panel.StripTabs.Children.OfType<Button>().Single();
        Check.True(more.Style == panel.FindResource("SpaceButton"), "More spaces is a pill");
        Check.Equal("More spaces, 4 more", AutomationProperties.GetName(more), "and says how many (Main, past a thousand, and three)");
        Check.Equal(SpaceView.MaxListed + 1, shell.Pills.Count, "All and a thousand");

        panel.NewSpaceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Ui.Pump();
        Check.Equal(Visibility.Visible, panel.NewSpaceBox.Visibility, "+ opens the name box");
        Check.Equal(Visibility.Collapsed, panel.NewSpaceButton.Visibility, "in its place");
        var box = Ui.Descendants<Border>(panel.NewSpaceBox).First();
        Check.Equal(new CornerRadius(12), box.CornerRadius, "rounded like the pills");
        Check.Equal(24.0, panel.NewSpaceBox.ActualHeight, "as tall");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static double Ratio(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Lin(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // ---------------------------------------------------------------- pictures

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        int result = 0;
        Ui.Run(errors =>
        {
            foreach (string theme in new[] { "light", "dark" })
            foreach (bool glass in new[] { false, true })
            foreach (var (accentName, accent) in new[] { ("system", "system"), ("sky", "#60CDFF"), ("amber", "#FFC567"), ("lilac", "#B9A4FF") })
            {
                if (glass && accentName != "system") continue;
                using var shell = new Shell(s => { s.Theme = theme; s.Glass = glass; s.Accent = accent; }, Spaces);
                var panel = shell.Panel;
                string tag = $"{theme}-{(glass ? "glass" : "solid")}-{accentName}";
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}.png"), theme == "light", glass);
                if (accentName != "system") continue;

                // Hovered (what the template's trigger sets) and the focus ring.
                var dev = shell.Pill("Dev");
                var bd = Part(dev, "Bd");
                bd.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
                bd.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");
                dev.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                Ui.Pump();
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-hover-dev.png"), theme == "light", glass);
                bd.ClearValue(Border.BackgroundProperty);
                bd.ClearValue(Border.BorderBrushProperty);
                dev.ClearValue(Control.ForegroundProperty);
                Ui.Pump();
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-focus-main.png"), theme == "light", glass, ring: shell.Pill("Main"));
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-focus-work.png"), theme == "light", glass, ring: shell.Pill("Work"));

                // The change of pill, part way.
                panel.ShowSpace((string)shell.Pill("Work").Tag);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (int at in new[] { 40, 90, 150 })
                {
                    Ui.WaitUntil(() => clock.ElapsedMilliseconds >= at, 1000);
                    Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-change-{at}ms.png"), theme == "light", glass);
                }
                Ui.WaitUntil(() => false, 300);
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-change-done.png"), theme == "light", glass);

                // The new-space box.
                panel.NewSpaceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Ui.Pump();
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-new.png"), theme == "light", glass);

                // Many: scrolled, faded at the edge, + in sight.
                for (int i = 0; i < 12; i++) shell.Ws.AddSpace($"Riverside {i}");
                Ui.Pump();
                Save(panel, System.IO.Path.Combine(folder, $"pills-{tag}-many.png"), theme == "light", glass);
            }
            if (errors.Count > 0)
            {
                Console.WriteLine(string.Join("\n", errors));
                result = 1;
            }
        });
        return result;
    }

    private static void Save(PanelWindow panel, string path, bool light, bool glass, Control? ring = null)
    {
        panel.UpdateLayout();
        const double scale = 1.5; // 150%, as on the main display here
        var root = panel.Root;
        double w = root.ActualWidth, h = Math.Min(root.ActualHeight, 150);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // What glass would show through: a desktop, a little busy.
            dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xD8, 0xDE, 0xE8) : Color.FromRgb(0x20, 0x24, 0x2C)), null, new Rect(0, 0, w, h));
            if (glass)
                dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0x9F, 0xB8, 0xD8) : Color.FromRgb(0x2C, 0x3A, 0x52)), null, new Rect(w * 0.55, 0, w * 0.45, h));
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            if (ring != null)
            {
                // The pill's focus visual, as WPF puts it over the pill.
                var template = (ControlTemplate)((Style)panel.FindResource("PillFocusRing")).Setters.OfType<Setter>().First().Value;
                var adorner = new Control { Template = template, Width = ring.ActualWidth, Height = ring.ActualHeight };
                adorner.Measure(new Size(ring.ActualWidth, ring.ActualHeight));
                adorner.Arrange(new Rect(0, 0, ring.ActualWidth, ring.ActualHeight));
                var at = Bounds(ring, root);
                var host = new Border { Child = adorner, Padding = new Thickness(4), Width = at.Width + 8, Height = at.Height + 8 };
                host.Measure(new Size(host.Width, host.Height));
                host.Arrange(new Rect(0, 0, host.Width, host.Height));
                dc.DrawRectangle(new VisualBrush(host) { Stretch = Stretch.None }, null, new Rect(at.X - 4, at.Y - 4, host.Width, host.Height));
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
        Console.WriteLine(path);
    }
}
