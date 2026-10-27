using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
/// Settings → Look: the panel background slider, fade when idle, and the peek's stand-in picture. The panel
/// and peek in this process, never shown on screen (or far off it) and never activated.
/// </summary>
internal static class LookTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("look: the new settings stay out of tack.json until used, keep to their bounds and read back", SettingsInFile);
        yield return ("look: at the least solid a background may be without glass, text keeps 4.5:1 (secondary 3:1) over white or black, in either theme", Contrast);
        yield return ("look on screen: by default the panel's background is the theme's own brush and chrome, exactly as before", DefaultLook);
        yield return ("look on screen: the slider sets only the background, live; near the default is the default; glass and solid keep their own", Slider);
        yield return ("look on screen: see-through without glass clears what's behind the panel, and solid again puts it back as it was", ClearWithoutGlass);
        yield return ("fade when idle: off, nothing waits and nothing fades; on, the orb and panel fade once the pointer has been away, and come back at once", FadeWhenIdle);
        yield return ("fade when idle: never while in use, a menu or the toast is up, or with the pointer still over it; animated only if Windows animates", FadeNotWhenBusy);
        yield return ("fade when idle on screen: the panel goes half see-through, then whole again with its own background", PanelFades);
        yield return ("tab switch on screen: the page left fades under the new one, Tasks to Notes and Notes to Tasks alike", LeavingPageUnder);
        yield return ("fold over glass on screen: the glass fades to the solid surface (not a snap), and is the glass again after", SolidFades);
        yield return ("peek on screen: the picture that stands in for the bubble while it moves is the very same pixels", PeekPicture);
        yield return ("toast on screen: a picture slides in for it, the very same pixels, and the live toast (Undo and all) takes over", ToastPicture);
        yield return ("drop bubble on screen: a picture stands in for it as it comes and goes, the very same pixels, of what it says now", BubblePicture);
    }

    // ---------------------------------------------------------------- settings

    private static void SettingsInFile()
    {
        static JsonElement Saved(Settings settings)
        {
            var doc = new StateDocument { Settings = settings };
            return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(doc, StateJsonContext.Default.StateDocument)).RootElement.GetProperty("settings");
        }

        var plain = Saved(new Settings());
        foreach (string key in new[] { "panelBackgroundGlass", "panelBackgroundSolid", "fadeWhenIdle" })
            Check.True(!plain.TryGetProperty(key, out _), $"{key} isn't written until it's used (tack.json, and the shared vectors, as before)");

        var s = new Settings { PanelBackgroundGlass = 0.4, PanelBackgroundSolid = 0.9, FadeWhenIdle = true };
        var saved = Saved(s);
        Check.Equal(0.4, saved.GetProperty("panelBackgroundGlass").GetDouble(), "glass written");
        Check.Equal(0.9, saved.GetProperty("panelBackgroundSolid").GetDouble(), "solid written");
        Check.True(saved.GetProperty("fadeWhenIdle").GetBoolean(), "fade written");
        var back = JsonSerializer.Deserialize("{\"settings\":" + saved.GetRawText() + "}", StateJsonContext.Default.StateDocument)!.Settings;
        Check.Equal(0.4, back.PanelBackgroundGlass, "glass read back");
        Check.Equal(0.9, back.PanelBackgroundSolid, "solid read back");
        Check.True(back.FadeWhenIdle, "fade read back");

        // Hand edits: kept to the bounds, whole percent; nonsense is the default.
        s.PanelBackgroundSolid = 0.1;
        Check.Equal(Settings.MinSolidBackground, s.PanelBackgroundSolid, "never less solid than readable without glass");
        s.PanelBackgroundGlass = 0;
        Check.Equal(Settings.MinGlassBackground, s.PanelBackgroundGlass, "never less than the glass minimum");
        s.PanelBackgroundGlass = 7;
        Check.Equal(1.0, s.PanelBackgroundGlass, "at most solid");
        s.PanelBackgroundGlass = 0.4567;
        Check.Equal(0.46, s.PanelBackgroundGlass, "whole percent");
        s.PanelBackgroundGlass = double.NaN;
        Check.Equal(null, s.PanelBackgroundGlass, "not a number: the default");
        var hand = JsonSerializer.Deserialize("{\"settings\":{\"panelBackgroundSolid\":0.2,\"panelBackgroundGlass\":-3}}", StateJsonContext.Default.StateDocument)!.Settings;
        Check.Equal(Settings.MinSolidBackground, hand.PanelBackgroundSolid, "a hand-edited 0.2 reads as the minimum");
        Check.Equal(Settings.MinGlassBackground, hand.PanelBackgroundGlass, "a hand-edited -3 reads as the minimum");
    }

    private static void Contrast() => Ui.Run(_ =>
    {
        foreach (string theme in new[] { "Dark", "Light" })
        {
            var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tack;component/Themes/{theme}.xaml") };
            Color Of(string key) => ((SolidColorBrush)palette[key]).Color;
            var surface = Of("SolidSurfaceBrush");
            surface.A = (byte)Math.Round(Settings.MinSolidBackground * 255);
            foreach (var behind in new[] { Colors.White, Colors.Black })
            {
                // As DWM and WPF blend: in the screen's own (gamma) values.
                var background = Over(surface, behind);
                double primary = Ratio(Over(Of("TextPrimaryBrush"), background), background);
                double secondary = Ratio(Over(Of("TextSecondaryBrush"), background), background);
                Check.True(primary >= 4.5, $"{theme} over {behind}: primary text {primary:F2}:1");
                Check.True(secondary >= 3, $"{theme} over {behind}: secondary text {secondary:F2}:1");
            }
        }
    });

    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // ---------------------------------------------------------------- the panel

    /// <summary>A Tack shell over a temp folder, its windows made but never shown (or shown far off screen, never activated).</summary>
    private sealed class Shell : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "tack-look-tests", Guid.NewGuid().ToString("N"));
        private readonly FolderSync _sync;
        private readonly StateStore _store;

        public Shell(Action<Settings>? settings = null)
        {
            Directory.CreateDirectory(_folder);
            var store = _store = new StateStore(_folder);
            Ws = store.Attach(new StateDocument { Settings = new Settings { Hotkey = "", Theme = "light" } });
            settings?.Invoke(Ws.Settings);
            _sync = new FolderSync(Ws, store);
            App = new AppShell(Ws, store, _sync);
            new WindowInteropHelper(Panel).EnsureHandle();
            Ui.Pump();
        }

        public Workspace Ws { get; }
        public AppShell App { get; }
        public PanelWindow Panel => App.Panel;
        public Settings Settings => Ws.Settings;

        /// <summary>Shows the panel far off screen, without activating it, so Settings lays out.</summary>
        public void ShowOffScreen()
        {
            Panel.ShowActivated = false;
            Panel.Left = -32000;
            Panel.Top = -32000;
            Panel.Show();
            ScreenGeometry.Place(Panel.Handle, -32000, -32000, 520, 900);
            Ui.Pump();
        }

        public void Dispose()
        {
            Panel.Hide();
            App.Dispose();
            _sync.Dispose();
            Ui.Pump();
            _store.Flush(); // no save left waiting to go into a folder that's about to be gone
            try { Directory.Delete(_folder, recursive: true); } catch { /* the log may still be open */ }
        }
    }

    private static bool IsPaletteBrush(Border surface) =>
        surface.ReadLocalValue(Border.BackgroundProperty)?.GetType().Name == "ResourceReferenceExpression";

    private static void DefaultLook()
    {
        Ui.Run(errors =>
        {
            foreach (bool glass in new[] { true, false })
            {
                using var shell = new Shell(s => s.Glass = glass);
                var panel = shell.Panel;
                Check.True(IsPaletteBrush(panel.Surface), $"glass {glass}: the palette's own brush, as before");
                Check.True(!panel.IsClear, $"glass {glass}: the frame drawn as before (not clear)");
                var brush = (SolidColorBrush)panel.Surface.Background;
                string key = panel.IsGlassActive ? "GlassTintBrush" : "SolidSurfaceBrush";
                Check.Equal(((SolidColorBrush)panel.FindResource(key)).Color, brush.Color, $"glass {glass}: {key}");
                Check.Equal(1.0, panel.Root.Opacity, $"glass {glass}: whole");
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void LeavingPageUnder()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell();
            shell.ShowOffScreen();
            var panel = shell.Panel;
            panel.SelectTab("tasks", animate: false);
            Ui.Pump();
            foreach (var (to, arriving, leaving) in new[] { ("notes", (UIElement)panel.NotesPage, (UIElement)panel.TasksPage), ("tasks", panel.TasksPage, panel.NotesPage) })
            {
                panel.SelectTab(to);
                Check.True(leaving.Visibility == Visibility.Visible && !leaving.IsHitTestVisible, $"to {to}: the page left is still fading");
                Check.True(DrawOrder(leaving).CompareTo(DrawOrder(arriving)) < 0, $"to {to}: under the page arriving ({DrawOrder(leaving)} vs {DrawOrder(arriving)})");
                Ui.WaitUntil(() => leaving.Visibility == Visibility.Collapsed, 2000);
                Check.True(leaving.ReadLocalValue(System.Windows.Controls.Panel.ZIndexProperty) == DependencyProperty.UnsetValue, $"to {to}: and as it was once gone");
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    /// <summary>Where a child of a panel is drawn: by Z index, then by its place among the children (later is on top).</summary>
    private static (int Z, int Index) DrawOrder(UIElement e) =>
        (System.Windows.Controls.Panel.GetZIndex(e), ((System.Windows.Controls.Panel)VisualTreeHelper.GetParent(e)).Children.IndexOf(e));

    private static void SolidFades()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell(s => s.Glass = true);
            shell.ShowOffScreen();
            var panel = shell.Panel;
            Ui.Pump();
            if (!panel.IsGlassActive) return; // no glass on this Windows: nothing to fade
            var tint = ((SolidColorBrush)panel.Surface.Background).Color;
            var solid = ((SolidColorBrush)panel.FindResource("SolidSurfaceBrush")).Color;
            panel.HoldSolid(Unfold.SolidFadeMs);
            Check.Equal(tint, ((SolidColorBrush)panel.Surface.Background).Color, "the fold's solid starts from the glass as it was");
            Ui.WaitUntil(() => ((SolidColorBrush)panel.Surface.Background).Color == solid, 1000);
            Check.Equal(solid, ((SolidColorBrush)panel.Surface.Background).Color, "and fades to the solid surface");
            panel.ReleaseSolid(0);
            Check.Equal(tint, ((SolidColorBrush)panel.Surface.Background).Color, "then the glass again");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Slider()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell(s => s.Glass = true);
            shell.ShowOffScreen();
            var panel = shell.Panel;
            panel.OpenSettings();
            Ui.Pump();
            var settings = panel.SettingsPage;
            var slider = settings.BackgroundSlider;
            bool glass = WindowEffects.SupportsSystemBackdrop;
            double def = panel.DefaultBackground(glass);
            double min = glass ? Settings.MinGlassBackground : Settings.MinSolidBackground;
            Check.True(Math.Abs(slider.Value - (1 - def) * 100) < 0.001, $"the slider starts at the default ({slider.Value} for {def})");
            Check.Equal(Math.Round((1 - min) * 100), slider.Maximum, "and goes as far as the minimum");
            Check.True(slider.Value == Math.Round(slider.Value) && slider.SmallChange == 5, "in whole percent (a screen reader says 45, not 0.45), five at a step");
            Check.Equal("Default", AutomationProperties.GetItemStatus(slider), "and says it's at the default");
            Check.Equal(Visibility.Hidden, settings.BackgroundReset.Visibility, "no Default button at the default");

            // The tick sits under the thumb's centre at the default.
            settings.UpdateLayout();
            var thumb = Ui.Descendants<System.Windows.Controls.Primitives.Thumb>(slider).First();
            var row = (Visual)slider.Parent;
            double thumbCentre = thumb.TransformToAncestor(row).Transform(new Point(thumb.ActualWidth / 2, 0)).X;
            double tickCentre = settings.BackgroundTick.TransformToAncestor(row).Transform(new Point(settings.BackgroundTick.ActualWidth / 2, 0)).X;
            Check.True(Math.Abs(thumbCentre - tickCentre) <= 1.5, $"the tick marks the default ({tickCentre:F1} vs the thumb at {thumbCentre:F1})");

            if (!glass)
            {
                Check.True(!slider.IsEnabled, "without the system backdrop (Windows 10) the slider is off");
                return;
            }
            var chrome = panel.Chrome;
            var textBefore = Ui.Descendants<TextBlock>(chrome).Select(t => t.Opacity).ToList();
            slider.Value = 60.3; // dragged: 40% solid, at once (snapped to a whole percent)
            Ui.Pump();
            Check.Equal(0.4, shell.Settings.PanelBackgroundGlass, "kept as it moves");
            Check.Equal("40% solid", AutomationProperties.GetItemStatus(slider), "said as how solid it is");
            var brush = (SolidColorBrush)panel.Surface.Background;
            Check.True(!IsPaletteBrush(panel.Surface), "the panel's background follows the slider live");
            Check.Equal((byte)Math.Round(0.4 * 255), brush.Color.A, "40% solid");
            var tint = ((SolidColorBrush)panel.FindResource("GlassTintBrush")).Color;
            Check.True(brush.Color.R == tint.R && brush.Color.G == tint.G && brush.Color.B == tint.B, "in the theme's own tint");
            Check.Equal(1.0, chrome.Opacity, "the content isn't touched");
            Check.Sequence(textBefore, Ui.Descendants<TextBlock>(chrome).Select(t => t.Opacity).ToList(), "nor any text");
            Check.Equal(Visibility.Visible, settings.BackgroundReset.Visibility, "Default shows");

            slider.Value = (1 - def) * 100 + 1; // let go next to the tick: that's the default
            Ui.Pump();
            Check.Equal(null, shell.Settings.PanelBackgroundGlass, "next to the default is the default");
            Check.True(IsPaletteBrush(panel.Surface), "and the palette's own brush again");

            slider.Value = 75;
            Ui.Pump();
            Check.Equal(Settings.MinGlassBackground, shell.Settings.PanelBackgroundGlass, "all the way: the glass minimum");
            settings.BackgroundReset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Ui.Pump();
            Check.Equal(null, shell.Settings.PanelBackgroundGlass, "Default puts it back");

            // Without glass the slider is about the solid panel, which has a value of its own.
            shell.Settings.PanelBackgroundGlass = 0.3;
            shell.Settings.Glass = false;
            Ui.Pump();
            Check.Equal(0.0, slider.Value, "without glass it starts solid");
            Check.Equal(Math.Round((1 - Settings.MinSolidBackground) * 100), slider.Maximum, "and stays readable");
            slider.Value = 10;
            Ui.Pump();
            Check.Equal(0.9, shell.Settings.PanelBackgroundSolid, "its own value");
            Check.Equal(0.3, shell.Settings.PanelBackgroundGlass, "the glass one kept");
            shell.Settings.Glass = true;
            Ui.Pump();
            Check.True(Math.Abs(slider.Value - 70) < 0.001, $"back with glass, the glass value ({slider.Value})");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void ClearWithoutGlass()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell(s => s.Glass = false);
            var panel = shell.Panel;
            if (!WindowEffects.SupportsSystemBackdrop) return; // Windows 10: always solid
            shell.Settings.PanelBackgroundSolid = 0.85;
            Ui.Pump();
            Check.True(panel.IsClear, "see-through without glass: nothing behind the panel but the screen");
            var solid = ((SolidColorBrush)panel.FindResource("SolidSurfaceBrush")).Color;
            var brush = (SolidColorBrush)panel.Surface.Background;
            Check.Equal(Color.FromArgb((byte)Math.Round(0.85 * 255), solid.R, solid.G, solid.B), brush.Color, "the solid surface, 85% there");
            shell.Settings.PanelBackgroundSolid = null;
            Ui.Pump();
            Check.True(!panel.IsClear, "solid again: the frame as before");
            Check.True(IsPaletteBrush(panel.Surface), "and the palette's own brush");
            shell.Settings.PanelBackgroundSolid = 0.9;
            shell.Settings.Glass = true;
            Ui.Pump();
            Check.True(!panel.IsClear, "with glass, never clear (the glass shows instead)");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ---------------------------------------------------------------- fade when idle

    private sealed class FakeWindow(int id) : IFadingWindow
    {
        public bool IsVisible { get; set; } = true;
        public IntPtr Handle { get; } = new(id);
        public bool Faded { get; private set; }
        public bool? Animated { get; private set; }
        public int Changes { get; private set; }

        public void SetFaded(bool faded, bool animate)
        {
            if (faded == Faded) return;
            Faded = faded;
            Changes++;
            if (faded) Animated = animate;
        }
    }

    private const int Delay = 40;

    private static void FadeWhenIdle()
    {
        Ui.Run(errors =>
        {
            var settings = new Settings();
            FakeWindow orb = new(1), panel = new(2);
            var fade = new IdleFade(settings, orb, panel, () => false, () => false, _ => false, TimeSpan.FromMilliseconds(Delay));

            // Off (the default): pointer leaves, panel loses the focus: nothing waits, nothing fades.
            fade.OrbPointer(false);
            fade.PanelIdle();
            Check.True(!fade.IsWaiting, "off: no timer");
            Ui.WaitUntil(() => false, Delay * 4);
            Check.True(!orb.Faded && !panel.Faded && orb.Changes == 0 && panel.Changes == 0, "off: nothing fades");

            settings.FadeWhenIdle = true;
            fade.SettingChanged();
            Check.True(fade.IsWaiting, "on: the wait starts");
            Ui.WaitUntil(() => orb.Faded && panel.Faded, Delay * 20);
            Check.True(orb.Faded && panel.Faded, "both fade once idle");
            Check.True(!fade.IsWaiting, "and nothing waits any more (nothing runs while idle)");

            fade.OrbPointer(true);
            Check.True(!orb.Faded, "the pointer over the orb: back at once");
            fade.PanelPointer(true);
            Check.True(!panel.Faded, "over the panel: back at once");
            fade.PanelPointer(false);
            fade.PanelAwake(); // the focus came back before the wait ran out
            Ui.WaitUntil(() => false, Delay * 4);
            Check.True(!panel.Faded, "in use again before the wait ran out: no fade");

            fade.OrbPointer(false);
            Ui.WaitUntil(() => orb.Faded, Delay * 20);
            settings.FadeWhenIdle = false;
            fade.SettingChanged();
            Check.True(!orb.Faded && !panel.Faded, "turned off: both whole at once");
            Check.True(!fade.IsWaiting, "and nothing waits");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void FadeNotWhenBusy()
    {
        Ui.Run(errors =>
        {
            var settings = new Settings { FadeWhenIdle = true };
            FakeWindow orb = new(1), panel = new(2);
            bool orbBusy = false, panelBusy = false, over = false;
            var fade = new IdleFade(settings, orb, panel, () => orbBusy, () => panelBusy, _ => over, TimeSpan.FromMilliseconds(Delay));

            orbBusy = panelBusy = true; // a menu is open on the orb; the panel has the focus, or the toast is up
            fade.OrbIdle();
            fade.PanelIdle();
            Ui.WaitUntil(() => !fade.IsWaiting, Delay * 20);
            Check.True(!orb.Faded && !panel.Faded, "busy: no fade");

            orbBusy = panelBusy = false;
            over = true; // a leave was missed: the pointer is still there
            fade.OrbIdle();
            fade.PanelIdle();
            Ui.WaitUntil(() => !fade.IsWaiting, Delay * 20);
            Check.True(!orb.Faded && !panel.Faded, "the pointer still over it: no fade");

            orb.IsVisible = false; // folded into the panel
            over = false;
            fade.OrbIdle();
            Ui.WaitUntil(() => !fade.IsWaiting, Delay * 20);
            Check.True(!orb.Faded, "a hidden orb doesn't fade");

            orb.IsVisible = true;
            fade.OrbIdle();
            Ui.WaitUntil(() => orb.Faded, Delay * 20);
            Check.Equal(SystemParameters.ClientAreaAnimation, orb.Animated, "it eases only if Windows' animation effects are on");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void PanelFades()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell(s => s.Glass = true);
            var panel = shell.Panel;
            panel.SetFaded(true, animate: false);
            Check.Equal(PanelWindow.IdleOpacity, panel.Root.Opacity, "half");
            Check.True(panel.IsClear, "see-through: nothing behind it but the screen");
            if (WindowEffects.SupportsSystemBackdrop)
            {
                Check.True(!panel.IsGlassActive, "no glass while faded (it can't be half there)");
                Check.Equal(((SolidColorBrush)panel.FindResource("SolidSurfaceBrush")).Color, ((SolidColorBrush)panel.Surface.Background).Color, "as a solid panel at half");
            }
            panel.SetFaded(false, animate: false);
            Check.Equal(1.0, panel.Root.Opacity, "whole again at once");
            Check.True(!panel.IsClear, "with its frame");
            Check.True(IsPaletteBrush(panel.Surface), "and the palette's own background, as before");

            // With a background of its own, it comes back with that.
            shell.Settings.PanelBackgroundGlass = 0.7;
            panel.SetFaded(true, animate: false);
            panel.SetFaded(false, animate: false);
            if (panel.IsGlassActive) Check.Equal((byte)Math.Round(0.7 * 255), ((SolidColorBrush)panel.Surface.Background).Color.A, "its own background again");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ---------------------------------------------------------------- the peek

    private static void PeekPicture()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.AddTask("Check the clash report", asNow: true);
            ws.AddTask("Send the model");
            var peek = new PeekWindow { Left = -32000, Top = -32000 };
            peek.Fill(new SpaceView(ws), "");
            peek.Show();
            Ui.Pump();
            var live = Pixels(peek.Root);
            peek.StandIn.Stand();
            Ui.Pump();
            Check.Equal(0.0, peek.Bubble.Opacity, "the picture stands in (the bubble isn't drawn at all)");
            Check.Equal(Visibility.Visible, peek.Picture.Visibility, "shown");
            var picture = Pixels(peek.Root);
            Check.Equal(live.Length, picture.Length, "the same size");
            int differ = 0;
            for (int i = 0; i < live.Length; i++) if (Math.Abs(live[i] - picture[i]) > 1) differ++;
            Check.True(differ == 0, $"the very same pixels ({differ} values differ)");
            peek.StandIn.Live(keep: false);
            Check.True(peek.Picture.Source == null, "let go after");
            Check.Equal(1.0, peek.Bubble.Opacity, "the live bubble back");
            peek.Close();
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });

    }

    private static void BubblePicture()
    {
        Ui.Run(errors =>
        {
            var bubble = new DropBubble { Left = -32000, Top = -32000 };
            bubble.Fill("Added 3 tasks", "In Work", undo: true, muted: false);
            bubble.Show();
            Ui.Pump();
            var live = Pixels(bubble.Root);
            bubble.StandIn.Take();
            bubble.StandIn.Stand();
            Ui.Pump();
            Check.Equal(0.0, bubble.Bubble.Opacity, "the picture stands in (the bubble, shadow and all, isn't drawn)");
            Check.Equal(Visibility.Visible, bubble.Picture.Visibility, "shown");
            var picture = Pixels(bubble.Root);
            Check.Equal(live.Length, picture.Length, "the same size");
            int differ = 0;
            for (int i = 0; i < live.Length; i++) if (Math.Abs(live[i] - picture[i]) > 1) differ++;
            Check.True(differ == 0, $"the very same pixels ({differ} values differ)");
            bubble.StandIn.Live(keep: true);
            Check.Equal(1.0, bubble.Bubble.Opacity, "the live bubble takes over");

            // It says something else, then goes: the picture it fades as is of what it says now.
            bubble.Fill("Added a task", null, undo: false, muted: false);
            Ui.Pump();
            var now = Pixels(bubble.Root);
            bubble.Dismiss();
            Check.True(bubble.StandIn.IsStanding, "it fades as a picture");
            Tack.Controls.Anim.Set(bubble, UIElement.OpacityProperty, 1); // hold the fade, to look
            Ui.Pump();
            var going = Pixels(bubble.Root);
            differ = 0;
            for (int i = 0; i < now.Length && i < going.Length; i++) if (Math.Abs(now[i] - going[i]) > 1) differ++;
            Check.True(now.Length == going.Length && differ == 0, $"of what it says now ({differ} values differ)");
            bubble.Close();
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void ToastPicture()
    {
        Ui.Run(errors =>
        {
            using var shell = new Shell();
            shell.ShowOffScreen();
            var panel = shell.Panel;
            bool undone = false;
            panel.ShowToast("Task deleted", () => undone = true);
            Check.True(panel.ToastStandIn.IsStanding, "a picture slides in");
            Check.True(panel.IsToastShown, "the toast is up meanwhile");
            Check.True(panel.ToastUndo.IsHitTestVisible && panel.Toast.IsVisible, "and its Undo can be clicked as it comes");
            Ui.WaitUntil(() => !panel.ToastStandIn.IsStanding, 2000);
            Check.True(!panel.ToastStandIn.IsStanding && panel.Toast.Opacity == 1, "still, the live toast takes over");
            var live = Pixels(panel.ToastHost);
            panel.ToastStandIn.Stand();
            Ui.Pump();
            var picture = Pixels(panel.ToastHost);
            Check.Equal(live.Length, picture.Length, "the same size");
            int differ = 0;
            for (int i = 0; i < live.Length; i++) if (Math.Abs(live[i] - picture[i]) > 1) differ++;
            Check.True(differ == 0, $"the very same pixels, shadow included ({differ} values differ)");
            panel.ToastStandIn.Live(keep: true);
            Check.True(panel.Undo() && undone, "Undo works");
            Ui.WaitUntil(() => !panel.IsToastShown, 2000);
            Check.Equal(Visibility.Collapsed, panel.Toast.Visibility, "then it's gone");
            Check.True(panel.ToastPicture.Source == null, "and its picture let go");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    /// <summary>What an element draws, at its window's DPI.</summary>
    private static byte[] Pixels(FrameworkElement root)
    {
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * dpi.DpiScaleX), (int)Math.Round(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
