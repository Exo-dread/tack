using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Interop;
using Tack.Model;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Tack.Tests;

/// <summary>
/// Not a test: the panel opening and folding (<see cref="Tack.Windows.Unfold"/>) as the screen shows it, a picture every
/// few milliseconds, over a window of its own in four colours (so nothing else on the screen is in them): dark and light,
/// with and without frosted glass, see-through, faded when idle, with the orb on other edges and another display, and
/// folding back mid-opening and opening again mid-fold. Writes a strip of pictures per run, and prints the steps DWM was
/// given (how many, how far apart) and whether the panel ended up as it should (on screen or hidden, never left cloaked,
/// the add box focused). A copy of Tack in this process (see <see cref="TestCopy"/>); nothing is typed or clicked.
///
///   dotnet run --project tests/Tack.Tests -c Release -- --unfold [folder]
/// </summary>
internal static class UnfoldShots
{
    public static int Run(string[] args)
    {
        string folder = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Path.GetTempPath(), "tack-unfold"));
        Directory.CreateDirectory(folder);
        return TestCopy.Run(Frames.Seed, (shell, ws) => Script(shell, ws, folder));
    }

    private static Task Wait(int ms) => Task.Delay(ms);

    private static readonly int[] OpenAt = [0, 20, 40, 60, 80, 100, 120, 140, 160, 190, 220, 250, 280, 320, 400, 600];
    private static readonly int[] FoldAt = [0, 20, 40, 60, 80, 100, 120, 140, 160, 180, 200, 230, 300];

    private static int _problems;

    private static async Task Script(AppShell shell, Workspace ws, string folder)
    {
        var s = ws.Settings;
        var panel = shell.Panel;
        Console.WriteLine($"Animation effects: {(SystemParameters.ClientAreaAnimation ? "on" : "off")}");

        shell.Expand();
        await Wait(700);
        var rect = ScreenGeometry.GetWindowRect(panel.Handle);
        shell.Collapse();
        await Wait(700);

        var backdrop = Backdrop();
        backdrop.Show();
        Cover(backdrop, rect);
        WindowEffects.BringToTopmost(shell.Orb.Handle);
        await Wait(400);

        foreach (var (theme, glass, background, name) in new (string, bool, double?, string)[]
        {
            ("dark", true, null, "dark-glass"), ("dark", false, null, "dark-solid"), ("light", true, null, "light-glass"),
            ("light", false, null, "light-solid"), ("dark", false, Settings.MinSolidBackground, "dark-solid-see-through"),
            ("light", true, Settings.MinGlassBackground, "light-glass-see-through"),
        })
        {
            s.Theme = theme;
            s.Glass = glass;
            s.PanelBackgroundGlass = glass ? background : null;
            s.PanelBackgroundSolid = glass ? null : background;
            await Wait(300);
            await Sequence(shell, rect, folder, name);
        }
        s.Theme = "dark";
        s.Glass = true;
        s.PanelBackgroundGlass = s.PanelBackgroundSolid = null;

        // Folding from a panel faded when idle (pinned, the pointer away).
        shell.Expand();
        await Wait(500);
        panel.SetFaded(true, animate: false);
        var fold = await Shots(Area(rect), FoldAt, shell.Collapse, shell);
        Sheet(fold, Path.Combine(folder, "faded-fold.png"));
        await Settled(shell, expanded: false, "faded-fold");

        // Changing its mind: folding back mid-opening, and opening again mid-fold.
        var flip = await Shots(Area(rect), [0, 40, 80, 100, 120, 140, 160, 200, 260, 320], shell.Expand, shell, switchAt: 80, then: shell.Collapse);
        Sheet(flip, Path.Combine(folder, "fold-mid-opening.png"));
        await Settled(shell, expanded: false, "fold-mid-opening");
        shell.Expand();
        await Wait(600);
        flip = await Shots(Area(rect), [0, 40, 80, 100, 120, 140, 160, 200, 260, 320, 500], shell.Collapse, shell, switchAt: 70, then: shell.Expand);
        Sheet(flip, Path.Combine(folder, "open-mid-fold.png"));
        await Settled(shell, expanded: true, "open-mid-fold");
        shell.Collapse();
        await Wait(600);

        // The orb on other edges, and on another display: the panel grows out of it wherever it is.
        var displays = Tack.Launcher.DisplayInfo.Current();
        var m = displays.First(d => d.Primary).Work;
        var main = new Int32Rect((int)m.X, (int)m.Y, (int)m.Width, (int)m.Height);
        var spots = new List<(string, int, int)>
        {
            ("left-bottom", main.X + 40, main.Y + main.Height - 60),
            ("right-bottom", main.X + main.Width - 40, main.Y + main.Height - 60),
            ("left-top", main.X + 40, main.Y + 80),
        };
        foreach (var other in displays.Where(d => !d.Primary).Take(1))
            spots.Add(("other-display", (int)(other.Work.X + other.Work.Width - 40), (int)(other.Work.Y + other.Work.Height / 3)));
        foreach (var (name, x, y) in spots)
        {
            s.OrbX = x;
            s.OrbY = y;
            shell.Orb.MoveCenterTo(x, y);
            shell.Expand();
            await Wait(600);
            var at = ScreenGeometry.GetWindowRect(panel.Handle);
            shell.Collapse();
            await Wait(600);
            Cover(backdrop, at);
            WindowEffects.BringToTopmost(shell.Orb.Handle);
            await Wait(300);
            await Sequence(shell, at, folder, "orb-" + name);
        }
        backdrop.Close();
        Console.WriteLine(_problems == 0 ? "The panel always ended up as it should." : $"{_problems} problem(s): see above.");
        Console.WriteLine($"Wrote the pictures to {folder}");
    }

    private static Int32Rect Area(Int32Rect rect)
    {
        int pad = (int)Math.Round(40 * ScreenGeometry.FromRect(rect).Scale);
        return new Int32Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
    }

    private static async Task Sequence(AppShell shell, Int32Rect rect, string folder, string name)
    {
        var area = Area(rect);
        var open = await Shots(area, OpenAt, shell.Expand, shell);
        string opened = Steps(shell);
        await Settled(shell, expanded: true, name);
        await Wait(300);
        var fold = await Shots(area, FoldAt, shell.Collapse, shell);
        string folded = Steps(shell);
        await Settled(shell, expanded: false, name);
        Sheet(open.Concat(fold).ToList(), Path.Combine(folder, name + ".png"));
        Console.WriteLine($"{name,-24} open: {opened}; fold: {folded}");
    }

    /// <summary>The steps DWM was given since the last call: how many, and the time between them.</summary>
    private static string Steps(AppShell shell)
    {
        var trace = shell.Unfold.Trace;
        shell.Unfold.Trace = null;
        if (trace == null || trace.IsEmpty) return "no steps";
        var t = trace.Select(x => x * 1000.0 / Stopwatch.Frequency).ToList();
        var gaps = t.Zip(t.Skip(1), (a, b) => b - a).ToList();
        return gaps.Count == 0 ? "1 step" : $"{t.Count} steps over {t[^1] - t[0]:F0} ms, gap mean {gaps.Average():F1} max {gaps.Max():F1} ms";
    }

    /// <summary>Checks the panel ended up on screen and focused, or hidden; and never left cloaked.</summary>
    private static async Task Settled(AppShell shell, bool expanded, string name)
    {
        await Wait(500);
        var panel = shell.Panel;
        bool cloaked = NativeMethods.DwmGetWindowAttribute(panel.Handle, NativeMethods.DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;
        var problems = new List<string>();
        if (panel.IsVisible != expanded) problems.Add(expanded ? "the panel isn't shown" : "the panel is still shown");
        if (cloaked) problems.Add("the panel is cloaked");
        if (shell.Unfold.IsRunning || shell.Unfold.Window?.IsVisible == true) problems.Add("the thumbnail is still up");
        if (expanded && Keyboard.FocusedElement is not TextBox) problems.Add($"focus is in {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}");
        if (problems.Count == 0) return;
        _problems++;
        Console.WriteLine($"  {name}: {string.Join(", ", problems)}");
    }

    /// <summary>Starts <paramref name="start"/> and takes a picture at each of <paramref name="times"/> (ms); <paramref name="then"/> at <paramref name="switchAt"/>.</summary>
    private static async Task<List<(int Ms, BitmapSource Image)>> Shots(Int32Rect area, int[] times, Action start, AppShell shell, int switchAt = -1, Action? then = null)
    {
        var list = new List<(int, BitmapSource)>();
        shell.Unfold.Trace = new ConcurrentQueue<long>();
        var clock = Stopwatch.StartNew();
        start();
        bool switched = then == null;
        foreach (int ms in times)
        {
            if (!switched && ms >= switchAt)
            {
                int until = switchAt - (int)clock.ElapsedMilliseconds;
                if (until > 0) await Wait(until);
                then!();
                switched = true;
            }
            int wait = ms - (int)clock.ElapsedMilliseconds;
            if (wait > 0) await Wait(wait);
            list.Add(((int)clock.ElapsedMilliseconds, TestCopy.Capture(area)));
        }
        return list;
    }

    /// <summary>Every picture of a run side by side, at half size, with when it was taken.</summary>
    private static void Sheet(List<(int Ms, BitmapSource Image)> shots, string path)
    {
        double k = 0.5;
        double w = shots[0].Image.PixelWidth * k, h = shots[0].Image.PixelHeight * k;
        int columns = 8;
        int rows = (shots.Count + columns - 1) / columns;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, columns * (w + 6), rows * (h + 26)));
            for (int i = 0; i < shots.Count; i++)
            {
                double x = i % columns * (w + 6), y = i / columns * (h + 26);
                dc.DrawImage(shots[i].Image, new Rect(x, y + 20, w, h));
                dc.DrawText(new FormattedText($"{shots[i].Ms} ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.White, 1), new Point(x + 4, y + 2));
            }
        }
        var bitmap = new RenderTargetBitmap((int)(columns * (w + 6)), (int)(rows * (h + 26)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        TestCopy.SavePng(bitmap, path);
    }

    /// <summary>A window in four colours, with a word across it, behind the panel and the orb.</summary>
    private static Window Backdrop()
    {
        var grid = new Grid { Width = 400, Height = 400 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        foreach (var (row, column, brush) in new[] { (0, 0, Brushes.White), (0, 1, Brushes.Black), (1, 0, Brushes.RoyalBlue), (1, 1, Brushes.DarkOrange) })
        {
            var block = new Rectangle { Fill = brush };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        var word = new TextBlock { Text = "BACKDROP", FontSize = 60, FontWeight = FontWeights.Bold, Foreground = Brushes.Crimson, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumnSpan(word, 2);
        Grid.SetRowSpan(word, 2);
        grid.Children.Add(word);
        return new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = -32000, Top = -32000, Width = 100, Height = 100, Focusable = false,
            Content = new Viewbox { Stretch = Stretch.Fill, Child = grid },
        };
    }

    private static void Cover(Window backdrop, Int32Rect rect)
    {
        int pad = (int)Math.Round(90 * ScreenGeometry.FromRect(rect).Scale);
        ScreenGeometry.Place(new WindowInteropHelper(backdrop).Handle, rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
    }
}
