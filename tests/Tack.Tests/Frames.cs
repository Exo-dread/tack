using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Interop;
using Tack.Model;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Not a test: times Tack's animations frame by frame. A copy of Tack runs in this process on the real
/// display (so at that display's own refresh rate), with its own data in a temp folder, no shortcut, never
/// launched at startup, and TACK_INSTANCE=uitestLook. Nothing is typed or clicked: each animation is set off
/// the way Tack's own handlers set it off (the orb's hover handlers, the add box's Enter, a row's check box).
///
///   dotnet run --project tests/Tack.Tests -c Release -- --frames [out.json] [--label name] [--rounds 3]
///
/// For each animation it looks at every frame WPF ticks (see <see cref="UseRendering"/>) and counts the ones
/// that draw something new, how far apart they come, and how many of the display's refreshes showed no new
/// frame. Also CPU time per animation, CPU while idle, and memory folded and open. See <see cref="TestCopy"/>.
///
/// --exp tries, in this process only, what docs/DESIGN.md ("Smooth enough, in software") weighed and set aside:
/// dfr100 (Timeline.DesiredFrameRate at the display's rate), flush (DwmFlush every frame), cache (BitmapCache),
/// snap (the panel's entrance as a picture), cost (what a full-panel frame costs), noshadow / toastplain (the
/// peek and toast without their shadow), fade (Fade when idle on). TACK_GPU=1 compares the GPU renderer.
/// </summary>
internal static class Frames
{
    public static int Run(string[] args)
    {
        string outPath = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Path.GetTempPath(), "tack-frames.json"));
        string label = TestCopy.Option(args, "--label") ?? "run";
        int rounds = int.TryParse(TestCopy.Option(args, "--rounds"), out int r) && r > 0 ? r : 3;
        UseRendering = TestCopy.Option(args, "--probe") == "rendering";
        Experiment = TestCopy.Option(args, "--exp") ?? "";

        Report? report = null;
        int code = TestCopy.Run(Seed, async (shell, ws) =>
        {
            report = new Report(label, RenderOptions.ProcessRenderMode + " rendering" + (UseRendering ? ", frames seen by CompositionTarget.Rendering" : ", frames seen by a probe clock"), TestCopy.Displays());
            await Script(shell, ws, report, rounds);
        }, () =>
        {
            if (Experiment.StartsWith("dfr", StringComparison.Ordinal))
                System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline),
                    new FrameworkPropertyMetadata { DefaultValue = (int?)int.Parse(Experiment[3..]) });
        });
        if (code != 0 || report == null) return code == 0 ? 1 : code;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.Table());
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>A pinned, dark, frosted panel with a list a BIM coordinator might have, the orb where it first appears.</summary>
    internal static void Seed(Workspace ws)
    {
        foreach (string text in new[]
        {
            "Send the clash report to the MEP team", "Update the BIM execution plan", "Federate the latest structural model",
            "Check the level 3 duct clearances in Navisworks", "Answer the architect about the stair core", "Purge the Revit families",
            "Book the coordination meeting", "Export the IFC for the contractor", "Review the fire dampers schedule",
        })
            ws.AddTask(text);
        ws.AddTask("Review the coordination clashes", asNow: true);
        ws.Complete(ws.AddTask("Back up the central model"));
        // A note with some Markdown, so the Notes tab shows what it usually does (formatted, with Format notes on).
        ws.ActiveSheet.Text = NoteSheet.ForTextBox(string.Join("\n",
            "# Level 3 coordination", "Clash run *Tuesday*, report to **MEP** by `Thu 12:00`.", "",
            "## Today", "- [x] Federate the structural model", "- [ ] Check duct clearances on L3", "- [ ] Answer the architect about the stair core", "",
            "1. Purge unused families", "2. Export the IFC", "   - with base quantities", "",
            "> Keep penetrations off the transfer beams.", "", "Plain line one", "Plain line two"));
    }

    private static Task Wait(int ms) => Task.Delay(ms);

    private static async Task Script(AppShell shell, Workspace ws, Report report, int rounds)
    {
        var orb = shell.Orb;
        var panel = shell.Panel;
        var tasks = panel.TasksPage;
        int orbHz = TestCopy.RefreshRate(orb.Handle);
        if (Experiment.Contains("cache"))
        {
            foreach (UIElement e in new UIElement[] { panel.Chrome, panel.TasksPage, panel.NotesPage, panel.SettingsPage, shell.Peek.Bubble, orb.Orb })
                e.CacheMode = new BitmapCache();
        }
        if (Experiment.Contains("noshadow")) shell.Peek.Bubble.Effect = null;
        if (Experiment.Contains("toastplain")) panel.Toast.Effect = null;
        if (Experiment.Contains("flush")) CompositionTarget.Rendering += (_, _) => DwmFlush();

        // --exp fade: with Fade when idle on (the orb fades a few seconds in; the panel, pinned, once it's idle).
        if (Experiment.Contains("fade")) ws.Settings.FadeWhenIdle = true;
        // Folded and idle: the orb only, memory handed back (a few seconds after start).
        await Wait(8000);
        report.Memory.Add(Snapshot("folded, idle"));
        report.IdleCpuMs.Add(await IdleCpu(3000));

        for (int round = 1; round <= rounds; round++)
        {
            await Record(report, "orb: hover", orbHz, 220, () => Hover(orb, true), shell);
            await Wait(240); // the peek comes 420 ms after the pointer arrives
            await Record(report, "orb: peek appears", orbHz, 260, () => { }, shell);
            await Wait(300);
            await Record(report, "orb: pointer leaves", orbHz, 220, () => Hover(orb, false), shell);
            await Wait(400);

            await Record(report, "panel: open", orbHz, 320, shell.Expand, shell);
            int hz = TestCopy.RefreshRate(panel.Handle);
            await Wait(500);
            if (Experiment.Contains("cost"))
            {
                await Record(report, "exp: fade the background only", hz, 300, () => Tack.Controls.Anim.Fade(panel.Surface, 1, 250, from: 0.6), shell);
                await Wait(300);
                await Record(report, "exp: fade the content only", hz, 300, () => Tack.Controls.Anim.Fade(panel.Chrome, 1, 250, from: 0.6), shell);
                await Wait(300);
                await Record(report, "exp: slide the content only", hz, 300, () => Tack.Controls.Anim.Slide(panel.Chrome, 0, 12, 250), shell);
                await Wait(300);
                await Record(report, "exp: fade the header only", hz, 300, () => Tack.Controls.Anim.Fade(panel.Header, 1, 250, from: 0.6), shell);
                await Wait(300);
            }
            if (Experiment.Contains("snap"))
            {
                await Record(report, "exp: entrance, live", hz, 260, () => { Tack.Controls.Anim.Fade(panel.Chrome, 1, 170, from: 0); Tack.Controls.Anim.Scale(panel.Chrome, 1, 220, Tack.Controls.Anim.EaseOut, from: 0.96); }, shell);
                await Wait(300);
                await Record(report, "exp: entrance, snapshot", hz, 260, () => SnapshotEntrance(panel), shell);
                await Wait(300);
            }
            await Record(report, "panel: tab to Notes", hz, 300, () => panel.SelectTab("notes"), shell);
            await Wait(400);
            await Record(report, "panel: tab to Tasks", hz, 300, () => panel.SelectTab("tasks"), shell);
            await Wait(400);

            await Record(report, "row: add", hz, 320, () => Add(tasks, $"Coordinate the riser shafts, round {round}"), shell);
            await Wait(400);

            // Tick: the circle fills at once; the row folds away after the grace period (and the panel glides with it).
            var folding = new TaskCompletionSource();
            void OnFolding(double _) => folding.TrySetResult();
            tasks.RowFolding += OnFolding;
            await Record(report, "row: tick", hz, 260, () => Check(tasks, 1), shell);
            await folding.Task.WaitAsync(TimeSpan.FromSeconds(3));
            tasks.RowFolding -= OnFolding;
            await Record(report, "row: fold away (ticked)", hz, 240, () => { }, shell);
            await Wait(400);

            await Record(report, "row: delete (fold + toast)", hz, 440, () => Delete(tasks, 2), shell);
            await Wait(400);
            await Record(report, "toast", hz, 260, () => panel.ShowToast("Moved to Work", () => { }), shell);
            await Wait(400);

            await Record(report, "settings: open", hz, 300, panel.OpenSettings, shell);
            await Wait(400);
            await Record(report, "settings: close", hz, 300, () => panel.CloseSettings(), shell);
            await Wait(400);

            await Record(report, "panel: fold into orb", orbHz, 340, shell.Collapse, shell);
            await Wait(900);
        }

        // Open and still: nothing should run.
        shell.Expand();
        await Wait(7000); // past the last toast's 6 s
        report.Memory.Add(Snapshot("panel open, idle"));
        report.IdleCpuMs.Add(await IdleCpu(3000));
        shell.Collapse();
        await Wait(8000);
        report.Memory.Add(Snapshot("folded again, idle"));
    }

    // ---------------------------------------------------------------- setting animations off as Tack's handlers do

    private static void Hover(Tack.Windows.OrbWindow orb, bool on) =>
        orb.Orb.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = on ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });

    private static void Add(Tack.Views.TasksView tasks, string text)
    {
        var box = tasks.AddBox;
        box.Text = text;
        var source = PresentationSource.FromVisual(box) ?? throw new InvalidOperationException("the add box isn't on screen");
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static FrameworkElement Row(Tack.Views.TasksView tasks, int index) =>
        (FrameworkElement)tasks.OpenList.ItemContainerGenerator.ContainerFromIndex(index);

    private static void Check(Tack.Views.TasksView tasks, int index) =>
        Ui.Descendants<ToggleButton>(Row(tasks, index)).First(b => b.Name == "Check").IsChecked = true;

    private static void Delete(Tack.Views.TasksView tasks, int index)
    {
        var button = Ui.Descendants<Button>(Row(tasks, index)).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Delete task");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    /// <summary>The entrance on a picture of the panel's content (an experiment: is a scaled bitmap cheaper than the live content?).</summary>
    private static void SnapshotEntrance(Tack.Windows.PanelWindow panel)
    {
        var chrome = panel.Chrome;
        var dpi = VisualTreeHelper.GetDpi(chrome);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(chrome.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(chrome.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(chrome);
        bitmap.Freeze();
        var image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new Point(1, 0) };
        panel.Root.Children.Add(image);
        chrome.Opacity = 0;
        Tack.Controls.Anim.Fade(image, 1, 170, from: 0);
        Tack.Controls.Anim.Scale(image, 1, 220, Tack.Controls.Anim.EaseOut, from: 0.96, completed: () =>
        {
            chrome.Opacity = 1;
            panel.Root.Children.Remove(image);
        });
    }

    // ---------------------------------------------------------------- recording

    /// <summary>
    /// How frames are seen. "clock" (the default): a probe animation of its own, on an object outside any window,
    /// whose clock WPF ticks with every other animation's; it asks for no frames that Tack's own animations don't.
    /// "rendering": CompositionTarget.Rendering, which (as WPF documents) makes WPF render every frame it can,
    /// so it shows the most WPF would draw, not what Tack's animations get on their own.
    /// </summary>
    internal static bool UseRendering { get; set; }

    /// <summary>--exp: changes tried in this process only, to compare (cache, noshadow).</summary>
    internal static string Experiment { get; set; } = "";

    /// <summary>
    /// Starts recording, sets the animation off, and records for <paramref name="ms"/>. Each frame is looked
    /// at for what it would draw (every element's opacity, transform, place and size, and each window's
    /// rectangle): a frame where any of that changed is an update the screen gets; one where nothing changed
    /// draws nothing new.
    /// </summary>
    private static async Task Record(Report report, string name, int hz, int ms, Action start, AppShell shell)
    {
        var frames = new List<(double T, bool Changed)>();
        var heights = new HashSet<int>();
        int last = Look(shell);
        void OnFrame()
        {
            int now = Look(shell);
            frames.Add((Now(), now != last));
            last = now;
            if (shell.Panel.IsVisible) heights.Add(ScreenGeometry.GetWindowRect(shell.Panel.Handle).Height);
        }
        EventHandler onRendering = (_, _) => OnFrame();
        var probe = new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms + 500))).CreateClock();
        var probed = new TranslateTransform();
        probe.CurrentTimeInvalidated += (_, _) => OnFrame();

        // The panel opening and folding moves as a DWM thumbnail, stepped by a thread of its own: its steps are frames too.
        var steps = new System.Collections.Concurrent.ConcurrentQueue<long>();
        shell.Unfold.Trace = steps;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm0 = Dwm.Read();
        double t0 = Now();
        if (UseRendering) CompositionTarget.Rendering += onRendering;
        else probed.ApplyAnimationClock(TranslateTransform.XProperty, probe);
        start();
        await Wait(ms);
        if (UseRendering) CompositionTarget.Rendering -= onRendering;
        else
        {
            probe.Controller!.Stop();
            probed.ApplyAnimationClock(TranslateTransform.XProperty, null);
        }
        double t1 = Now();
        var dwm1 = Dwm.Read();
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        shell.Unfold.Trace = null;
        if (!steps.IsEmpty)
        {
            // The thumbnail's steps alone (what moves), then with the UI thread's frames (the orb's own animation).
            var moved = steps.Select(x => (x * 1000.0 / Stopwatch.Frequency, true)).ToList();
            report.Add(name + ", DWM steps", hz, t0, moved, t1 - t0, dwm1.Frame - dwm0.Frame, 0, heights.Count);
            frames.AddRange(moved);
            frames.Sort((a, b) => a.T.CompareTo(b.T));
        }
        report.Add(name, hz, t0, frames, t1 - t0, dwm1.Frame - dwm0.Frame, cpuMs, heights.Count);
    }

    /// <summary>A fingerprint of what Tack's windows would draw now.</summary>
    private static int Look(AppShell shell)
    {
        var hash = new HashCode();
        foreach (Window? w in new Window?[] { shell.Orb, shell.Panel, shell.Peek, shell.Unfold.Window })
        {
            if (w is not { IsVisible: true }) continue;
            var r = ScreenGeometry.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            hash.Add(r.X); hash.Add(r.Y); hash.Add(r.Width); hash.Add(r.Height);
            hash.Add(Math.Round(w.Opacity, 4));
            Walk(w, ref hash);
        }
        return hash.ToHashCode();
    }

    private static void Walk(DependencyObject node, ref HashCode hash)
    {
        if (node is UIElement e)
        {
            if (e.Visibility != Visibility.Visible) return;
            hash.Add(Math.Round(e.Opacity, 4));
            var m = e.RenderTransform.Value;
            if (!m.IsIdentity)
            {
                hash.Add(Math.Round(m.M11, 4)); hash.Add(Math.Round(m.M12, 4)); hash.Add(Math.Round(m.M22, 4));
                hash.Add(Math.Round(m.OffsetX, 3)); hash.Add(Math.Round(m.OffsetY, 3));
            }
            var at = VisualTreeHelper.GetOffset((Visual)node);
            hash.Add(Math.Round(at.X, 3)); hash.Add(Math.Round(at.Y, 3));
            hash.Add(Math.Round(e.RenderSize.Width, 3)); hash.Add(Math.Round(e.RenderSize.Height, 3));
            if (e is System.Windows.Shapes.Shape shape) hash.Add(Math.Round(shape.StrokeDashOffset, 3));
            if (e is Tack.Controls.OrbVisual orb) hash.Add(orb.IsHot);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), ref hash);
    }

    private static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private static async Task<double> IdleCpu(int ms)
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        await Wait(ms);
        return (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
    }

    private static MemorySnapshot Snapshot(string when)
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return new MemorySnapshot(when, p.PrivateMemorySize64 / 1048576.0, p.WorkingSet64 / 1048576.0);
    }

    // ---------------------------------------------------------------- the report

    internal sealed record MemorySnapshot(string When, double PrivateMb, double WorkingSetMb);

    internal sealed class Scenario
    {
        public string Name { get; init; } = "";
        public int Hz { get; init; }
        public int Runs { get; set; }
        public double WindowMs { get; set; }
        public int UiFrames { get; set; }
        /// <summary>Frames that drew something new, and the time between them (ms).</summary>
        public int Updates { get; set; }
        public List<double> Gaps { get; } = new();
        public double AnimatedMs { get; set; }
        public List<double> FirstMs { get; } = new();
        /// <summary>Refreshes during the animation that showed no new frame (the screen held the last one).</summary>
        public int Held { get; set; }
        public int Refreshes { get; set; }
        public long DwmFrames { get; set; }
        public double CpuMs { get; set; }
        public int GlideHeights { get; set; }

        public double UiFps => WindowMs > 0 ? UiFrames / WindowMs * 1000 : 0;
        public double UpdatesPerSecond => AnimatedMs > 0 ? Gaps.Count / AnimatedMs * 1000 : 0;
        public double Mean => Gaps.Count > 0 ? Gaps.Average() : 0;
        public double P50 => Pct(0.5);
        public double P95 => Pct(0.95);
        public double Max => Gaps.Count > 0 ? Gaps.Max() : 0;
        public double Spread => Gaps.Count > 1 ? Math.Sqrt(Gaps.Sum(g => (g - Mean) * (g - Mean)) / (Gaps.Count - 1)) : 0;
        public double HeldShare => Refreshes > 0 ? (double)Held / Refreshes : 0;
        public double First => FirstMs.Count > 0 ? FirstMs.Average() : 0;

        private double Pct(double q)
        {
            if (Gaps.Count == 0) return 0;
            var s = Gaps.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)Math.Floor(q * s.Count))];
        }
    }

    internal sealed class Report(string label, string renderMode, List<string> displays)
    {
        public string Label { get; } = label;
        public string RenderMode { get; } = renderMode;
        public List<string> Displays { get; } = displays;
        public List<Scenario> Scenarios { get; } = new();
        public List<MemorySnapshot> Memory { get; } = new();
        public List<double> IdleCpuMs { get; } = new();

        public void Add(string name, int hz, double t0, List<(double T, bool Changed)> frames, double windowMs, long dwmFrames, double cpuMs, int heights)
        {
            var s = Scenarios.FirstOrDefault(x => x.Name == name);
            if (s == null) Scenarios.Add(s = new Scenario { Name = name, Hz = hz });
            s.Runs++;
            s.WindowMs += windowMs;
            s.UiFrames += frames.Count;
            s.DwmFrames += dwmFrames;
            s.CpuMs += cpuMs;
            s.GlideHeights = Math.Max(s.GlideHeights, heights);
            var updates = frames.Where(f => f.Changed).Select(f => f.T).ToList();
            s.Updates += updates.Count;
            if (updates.Count == 0) return;
            s.FirstMs.Add(updates[0] - t0);
            double period = 1000.0 / hz;
            for (int i = 1; i < updates.Count; i++)
            {
                double gap = updates[i] - updates[i - 1];
                s.Gaps.Add(gap);
                // A gap of n refreshes leaves the screen showing the same frame for n - 1 of them.
                int n = Math.Max(1, (int)Math.Round(gap / period));
                s.Refreshes += n;
                s.Held += n - 1;
            }
            s.AnimatedMs += updates[^1] - updates[0];
        }

        public string Table()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Label}: {RenderMode}{(Experiment.Length > 0 ? ", trying " + Experiment : "")}; {string.Join("; ", Displays)}");
            sb.AppendLine($"{"animation",-34} {"Hz",4} {"first",6} {"upd/s",6} {"mean",6} {"p50",6} {"p95",6} {"max",6} {"spread",6} {"held",5} {"ui fps",6} {"cpu ms",7} {"steps",5}");
            foreach (var s in Scenarios)
                sb.AppendLine($"{s.Name,-34} {s.Hz,4} {s.First,6:F0} {s.UpdatesPerSecond,6:F1} {s.Mean,6:F1} {s.P50,6:F1} {s.P95,6:F1} {s.Max,6:F1} {s.Spread,6:F1} {s.HeldShare,5:P0} {s.UiFps,6:F0} {s.CpuMs / s.Runs,7:F1} {s.GlideHeights,5}");
            sb.AppendLine("first: ms until the first new frame. upd/s, mean/p50/p95/max, spread (std dev): new frames, and ms between them.");
            sb.AppendLine("held: refreshes that showed no new frame mid-animation. ui fps: UI-thread frames. cpu ms: per run. steps: panel heights shown.");
            foreach (var m in Memory) sb.AppendLine($"memory, {m.When}: private {m.PrivateMb:F1} MB, working set {m.WorkingSetMb:F1} MB");
            sb.AppendLine($"idle CPU: {string.Join(", ", IdleCpuMs.Select(c => $"{c:F0} ms in 3 s"))}");
            return sb.ToString();
        }
    }

    /// <summary>DWM's own count of refreshes and composed frames (the whole desktop's).</summary>
    private static class Dwm
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TimingInfo
        {
            public uint cbSize;
            public uint rateRefreshNum, rateRefreshDen;
            public ulong qpcRefreshPeriod;
            public uint rateComposeNum, rateComposeDen;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

        public static (long Frame, long Refresh) Read()
        {
            var info = new TimingInfo { cbSize = (uint)Marshal.SizeOf<TimingInfo>() };
            return DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 ? ((long)info.cFrame, (long)info.cRefresh) : (0, 0);
        }
    }
}
