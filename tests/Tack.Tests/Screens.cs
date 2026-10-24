using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Tack.Interop;
using Tack.Model;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Tack.Tests;

/// <summary>
/// Not a test: pictures of the panel as the screen shows it (DWM's acrylic included), over a striped window of
/// its own so nothing else on the screen is in them: each theme, with and without frosted glass, at Settings →
/// Look → Panel background's default, middle and most see-through; the panel and the orb faded when idle; and
/// the Look settings. A copy of Tack in this process (see <see cref="TestCopy"/>).
///
///   dotnet run --project tests/Tack.Tests -c Release -- --screens [folder]
///
/// Beside each picture it prints how much the stripes behind show through the panel's background: the
/// difference between a pixel over a white stripe and one over a black stripe (0: nothing shows; 255: all).
/// </summary>
internal static class Screens
{
    private const int Stripe = 24;

    public static int Run(string[] args)
    {
        string folder = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Path.GetTempPath(), "tack-screens"));
        Directory.CreateDirectory(folder);
        return TestCopy.Run(Frames.Seed, (shell, ws) => Script(shell, ws, folder));
    }

    private static Task Wait(int ms) => Task.Delay(ms);

    private static async Task Script(AppShell shell, Workspace ws, string folder)
    {
        var panel = shell.Panel;
        var s = ws.Settings;
        shell.Expand();
        await Wait(800);
        var rect = ScreenGeometry.GetWindowRect(panel.Handle);
        int pad = (int)Math.Round(28 * VisualTreeHelper.GetDpi(panel).DpiScaleX);
        var area = new Int32Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
        var stripes = Stripes(area);
        stripes.Show();
        ScreenGeometry.Place(new WindowInteropHelper(stripes).Handle, area.X, area.Y, area.Width, area.Height);
        WindowEffects.BringToTopmost(panel.Handle);
        await Wait(500);

        foreach (string theme in new[] { "dark", "light" })
        {
            s.Theme = theme;
            foreach (bool glass in new[] { true, false })
            {
                s.Glass = glass;
                double min = glass ? Settings.MinGlassBackground : Settings.MinSolidBackground;
                foreach (var (name, value) in new (string, double?)[] { ("default", null), ("middle", Math.Round((1 + min) / 2, 2)), ("most see-through", min) })
                {
                    if (glass) s.PanelBackgroundGlass = value; else s.PanelBackgroundSolid = value;
                    await Shot(panel, area, rect, folder, $"{theme}-{(glass ? "glass" : "solid")}-{name.Replace(' ', '-')}");
                }
                s.PanelBackgroundGlass = null;
                s.PanelBackgroundSolid = null;
            }

            // Fade when idle, and back.
            s.Glass = true;
            panel.SetFaded(true, animate: false);
            await Shot(panel, area, rect, folder, $"{theme}-glass-faded");
            panel.SetFaded(false, animate: false);
            await Shot(panel, area, rect, folder, $"{theme}-glass-back-from-faded");
            await FadeSequence(panel, area, rect, theme);

            // Settings → Look.
            panel.OpenSettings();
            await Wait(400);
            var look = Ui.Descendants<TextBlock>(panel.SettingsPage).First(t => t.Text == "Look");
            look.BringIntoView(new Rect(0, 0, 10, 420));
            s.PanelBackgroundGlass = 0.4;
            await Shot(panel, area, rect, folder, $"{theme}-settings-look");
            s.PanelBackgroundGlass = null;
            panel.CloseSettings(animate: false);
        }

        // The orb, whole and faded, over the stripes.
        s.Theme = "dark";
        stripes.Close();
        shell.Collapse();
        await Wait(700);
        var orb = ScreenGeometry.GetWindowRect(shell.Orb.Handle);
        var around = new Int32Rect(orb.X - orb.Width, orb.Y - orb.Height / 2, orb.Width * 2, orb.Height * 2);
        var behind = Stripes(around);
        behind.Show();
        ScreenGeometry.Place(new WindowInteropHelper(behind).Handle, around.X, around.Y, around.Width, around.Height);
        WindowEffects.BringToTopmost(shell.Orb.Handle);
        await Wait(400);
        TestCopy.SavePng(TestCopy.Capture(around), Path.Combine(folder, "orb.png"));
        shell.Orb.SetFaded(true, animate: false);
        await Wait(400);
        TestCopy.SavePng(TestCopy.Capture(around), Path.Combine(folder, "orb-faded.png"));
        shell.Orb.SetFaded(false, animate: false);
        behind.Close();
        Console.WriteLine($"Wrote the pictures to {folder}");
    }

    /// <summary>
    /// The animated fade over frosted glass, as the screen shows it: one pixel of the panel's margin over a white
    /// stripe, before and then at moments through the fade. It should move one way only, from the glass to the
    /// faded look, with no first frame of solid surface in between.
    /// </summary>
    private static async Task FadeSequence(Tack.Windows.PanelWindow panel, Int32Rect area, Int32Rect rect, string theme)
    {
        await Wait(450);
        var (x, y) = OnWhite(panel, area, rect);
        int At() => TestCopy.Pixel(TestCopy.Capture(new Int32Rect(area.X + x, area.Y + y, 1, 1)), 0, 0).G;
        var seen = new List<string> { $"before {At()}" };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        panel.SetFaded(true, animate: true);
        foreach (int ms in new[] { 20, 45, 90, 160, 450 })
        {
            await Wait(Math.Max(0, ms - (int)clock.ElapsedMilliseconds));
            seen.Add($"+{clock.ElapsedMilliseconds} ms {At()}");
        }
        panel.SetFaded(false, animate: false);
        Console.WriteLine($"{theme}-glass fade, over white: {string.Join(", ", seen)}");
    }

    /// <summary>A pixel of the panel's empty right margin over a white stripe, in <paramref name="area"/>'s pixels.</summary>
    private static (int X, int Y) OnWhite(Tack.Windows.PanelWindow panel, Int32Rect area, Int32Rect rect)
    {
        int y = rect.Y - area.Y + rect.Height * 2 / 3;
        int x = rect.X - area.X + rect.Width - (int)(18 * VisualTreeHelper.GetDpi(panel).DpiScaleX);
        return (x - ((x / Stripe) % 2 == 0 ? 0 : Stripe), y);
    }

    private static async Task Shot(Tack.Windows.PanelWindow panel, Int32Rect area, Int32Rect rect, string folder, string name)
    {
        await Wait(450); // DWM brings a new backdrop round over a few frames
        var image = TestCopy.Capture(area);
        TestCopy.SavePng(image, Path.Combine(folder, name + ".png"));
        // Two pixels of the panel's empty right margin, one over a white stripe and one over a black one.
        int y = rect.Y - area.Y + rect.Height * 2 / 3;
        int x = rect.X - area.X + rect.Width - (int)(18 * VisualTreeHelper.GetDpi(panel).DpiScaleX);
        int onWhite = x - ((x / Stripe) % 2 == 0 ? 0 : Stripe), onBlack = onWhite + Stripe;
        var a = TestCopy.Pixel(image, onWhite, y);
        var b = TestCopy.Pixel(image, onBlack, y);
        int through = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        Console.WriteLine($"{name,-34} shows through: {through,3}   (over white {a}, over black {b})");
    }

    /// <summary>A window of black and white stripes (and a word in colour) behind the panel, drawn in physical pixels.</summary>
    private static Window Stripes(Int32Rect area)
    {
        var canvas = new Canvas { Background = Brushes.White, Width = area.Width, Height = area.Height };
        for (int x = Stripe; x < area.Width; x += 2 * Stripe)
        {
            var bar = new Rectangle { Width = Stripe, Height = area.Height, Fill = Brushes.Black };
            Canvas.SetLeft(bar, x);
            canvas.Children.Add(bar);
        }
        var word = new TextBlock { Text = "BACKDROP", FontSize = 64, FontWeight = FontWeights.Bold, Foreground = Brushes.OrangeRed };
        Canvas.SetLeft(word, 40);
        Canvas.SetTop(word, area.Height / 3.0);
        canvas.Children.Add(word);
        return new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = -32000, Top = -32000, Width = 100, Height = 100, Focusable = false,
            Content = new Viewbox { Stretch = Stretch.Fill, Child = canvas },
        };
    }
}
