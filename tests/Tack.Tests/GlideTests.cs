using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Tack.Interop;
using Tack.Model;
using Tack.Sync;
using Tack.Views;

namespace Tack.Tests;

/// <summary>
/// The panel's size glide: the animator on a stand-in window with a clock the test turns (so timing is exact),
/// and the task list folding a ticked row away in a window far off screen that is never activated.
/// </summary>
internal static class GlideTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("panel glide: the clock starts on the glide's first frame, so a late first frame doesn't eat the start of the curve", ClockStartsOnFirstFrame);
        yield return ("panel glide: a resize that renders the window and raises a frame inside it moves one step, not the rest of the glide in a burst", OneStepPerFrame);
        yield return ("panel glide: when that render takes most of a refresh (software), the next step follows it at once, never faster than the display", StepsAtRenderPace);
        yield return ("panel glide: sent somewhere new mid-flight, it carries on from where it is and keeps its end; with little time left it gets enough", RetargetKeepsTime);
        yield return ("panel glide: in step with a row folding away, it follows the row's clock and ease-in, and a rounding pixel doesn't change that", InStepWithFold);
        yield return ("tasks on screen: while a ticked row folds away, the list already asks for its final height, so the panel never chases it", FoldAsksFinalHeight);
    }

    private const double Frame = 10; // 100 Hz, the main display here

    /// <summary>A window that only records where it was put, on a clock the test advances.</summary>
    private sealed class FakeWindow(Int32Rect rect) : IGlideWindow
    {
        public Int32Rect Rect { get; private set; } = rect;
        public double NowMs { get; set; } = 1000;
        public EventHandler? OnFrame { get; private set; }
        public List<(double T, Int32Rect R)> Steps { get; } = new();
        public Int32Rect? Landed { get; private set; }

        /// <summary>Runs inside each Place, as WPF's own render on a resize does.</summary>
        public Action<FakeWindow>? DuringPlace { get; set; }

        public void Place(Int32Rect r)
        {
            Rect = r;
            Steps.Add((NowMs, r));
            DuringPlace?.Invoke(this);
        }

        public void Land(Int32Rect r)
        {
            Rect = r;
            Landed = r;
            Steps.Add((NowMs, r));
        }

        public void Frames(EventHandler frame, bool on) => OnFrame = on ? frame : null;

        public double RefreshMs { get; set; } = Frame;

        public void Tick() => OnFrame?.Invoke(null, EventArgs.Empty);

        /// <summary>Frames every <paramref name="every"/> ms while the glide runs (or until <paramref name="until"/>).</summary>
        public void Run(double every = Frame, double until = double.MaxValue)
        {
            for (int i = 0; i < 1000 && OnFrame != null && NowMs + every <= until; i++)
            {
                NowMs += every;
                Tick();
            }
        }
    }

    private static readonly Int32Rect Start = new(-20000, -20000, 510, 600);

    private static Int32Rect Taller(int by) => new(Start.X, Start.Y, Start.Width, Start.Height + by);

    private static void ClockStartsOnFirstFrame()
    {
        var w = new FakeWindow(Start);
        var glide = new WindowBoundsAnimator(w);
        var target = Taller(240);
        glide.AnimateTo(target);
        double asked = w.NowMs;
        w.NowMs += 45; // the first frame comes late (the glide is asked for after a layout pass)
        w.Tick();
        Check.Equal(1, w.Steps.Count, "the first frame takes one step");
        Check.True(w.Steps[0].R.Height - Start.Height <= 1, $"a pixel, not {w.Steps[0].R.Height - Start.Height} px of the way");
        w.Run();

        Check.Equal(target, w.Landed, "lands on the target");
        Check.True(!glide.IsRunning, "and stops");
        var heights = w.Steps.Select(s => s.R.Height).Distinct().ToList();
        Check.True(heights.Count >= 15, $"the whole curve is on screen ({heights.Count} sizes)");
        int biggest = 0;
        for (int i = 1; i < w.Steps.Count; i++) biggest = Math.Max(biggest, Math.Abs(w.Steps[i].R.Height - w.Steps[i - 1].R.Height));
        Check.True(biggest <= 240 * 0.2, $"no step jumps (the biggest is {biggest} px of 240)");
        double first = w.Steps[0].T, last = w.Steps[^1].T;
        Check.True(last - first >= WindowBoundsAnimator.DurationMs - Frame, $"the ease-out lasts its {WindowBoundsAnimator.DurationMs} ms from the first step ({last - first} ms)");
        Check.True(last - asked <= 45 + WindowBoundsAnimator.DurationMs + Frame, $"and no longer ({last - asked} ms after it was asked for)");
    }

    private static void OneStepPerFrame()
    {
        var w = new FakeWindow(Start);
        int depth = 0;
        w.DuringPlace = win =>
        {
            // WPF renders a resized window inside SetWindowPos, and that render raises a frame.
            if (++depth > 64) throw new TestFailure("frames nest without end");
            win.NowMs += 4;
            win.Tick();
            depth--;
        };
        var glide = new WindowBoundsAnimator(w);
        glide.AnimateTo(Taller(240));
        var perFrame = new List<int>();
        for (int i = 0; i < 100 && w.OnFrame != null; i++)
        {
            int before = w.Steps.Count;
            w.NowMs += Frame;
            w.Tick();
            perFrame.Add(w.Steps.Count - before);
        }
        Check.True(perFrame.All(n => n <= 1), $"one step per frame (steps in each frame: {string.Join(",", perFrame)})");
        Check.True(perFrame.Count >= 12, $"over many frames ({perFrame.Count})");
        Check.Equal(Taller(240), w.Landed, "and it lands");
    }

    private static void StepsAtRenderPace()
    {
        foreach (double renderMs in new[] { 8.5, 13.0 })
        {
            var w = new FakeWindow(Start);
            int depth = 0;
            w.DuringPlace = win =>
            {
                // Drawing the whole panel in software, inside SetWindowPos: most of a 10 ms refresh, or more.
                if (++depth > 64) throw new TestFailure("frames nest without end");
                win.NowMs += renderMs;
                win.Tick();
                depth--;
            };
            var glide = new WindowBoundsAnimator(w);
            glide.AnimateTo(Taller(240));
            int most = 0;
            for (int i = 0; i < 300 && w.OnFrame != null; i++)
            {
                int before = w.Steps.Count;
                w.NowMs += 1; // the next frame comes as soon as the last is drawn
                w.Tick();
                most = Math.Max(most, w.Steps.Count - before);
            }
            var placed = w.Steps.Take(w.Steps.Count - 1).ToList(); // the landing aside
            Check.Equal(Taller(240), w.Landed, $"{renderMs} ms renders: it lands");
            double closest = Enumerable.Range(1, placed.Count - 1).Min(i => placed[i].T - placed[i - 1].T);
            Check.True(closest >= 0.8 * Frame - 0.01, $"{renderMs} ms renders: steps never closer than most of a refresh ({closest} ms)");
            Check.True(closest <= renderMs + 0.01, $"{renderMs} ms renders: each follows the last one's render at once ({closest} ms apart)");
            Check.True(placed.Count >= WindowBoundsAnimator.DurationMs / (renderMs + 1) - 2, $"{renderMs} ms renders: a step for each render ({placed.Count} steps)");
            Check.True(most >= 5, $"{renderMs} ms renders: without waiting for a frame of their own each (at most {most} steps in one frame)");
        }
    }

    private static void RetargetKeepsTime()
    {
        // Mid-flight: a little further. Same end time; no jump, no step back.
        var w = new FakeWindow(Start);
        var glide = new WindowBoundsAnimator(w);
        glide.AnimateTo(Taller(240));
        w.NowMs += Frame;
        w.Tick();
        double started = w.NowMs;
        w.Run(until: started + 60);
        int at = w.Rect.Height;
        int lastStep = w.Steps[^1].R.Height - w.Steps[^2].R.Height;
        glide.AnimateTo(Taller(260));
        w.NowMs += Frame;
        w.Tick();
        int next = w.Rect.Height - at;
        Check.True(next >= 0 && next <= lastStep * 1.5 + 2, $"it carries on from where it was ({next} px, after steps of {lastStep})");
        w.Run();
        Check.Equal(Taller(260), w.Landed, "to the new target");
        Check.True(w.Steps[^1].T <= started + WindowBoundsAnimator.DurationMs + Frame, $"by the glide's own end ({w.Steps[^1].T - started} ms, not a fresh {WindowBoundsAnimator.DurationMs} from the change)");
        for (int i = 1; i < w.Steps.Count; i++)
            Check.True(w.Steps[i].R.Height >= w.Steps[i - 1].R.Height, $"never back ({w.Steps[i - 1].R.Height} then {w.Steps[i].R.Height})");

        // Nearly there, then sent far: it gets time enough instead of a jump.
        var w2 = new FakeWindow(Start);
        var late = new WindowBoundsAnimator(w2);
        late.AnimateTo(Taller(240));
        w2.NowMs += Frame;
        w2.Tick();
        double started2 = w2.NowMs;
        w2.Run(until: started2 + 170);
        double changed = w2.NowMs;
        late.AnimateTo(Taller(-100));
        w2.Run();
        Check.Equal(Taller(-100), w2.Landed, "far: it gets there");
        Check.True(w2.Steps[^1].T - changed >= WindowBoundsAnimator.MinRetargetMs - Frame, $"over at least {WindowBoundsAnimator.MinRetargetMs} ms ({w2.Steps[^1].T - changed})");
        int jump = 0;
        for (int i = 1; i < w2.Steps.Count; i++) jump = Math.Max(jump, Math.Abs(w2.Steps[i].R.Height - w2.Steps[i - 1].R.Height));
        Check.True(jump <= 340 * 0.3, $"without a jump (biggest step {jump} px)");
    }

    private static void InStepWithFold()
    {
        var w = new FakeWindow(Start);
        var glide = new WindowBoundsAnimator(w);
        double foldStart = w.NowMs;
        const double foldMs = 200;
        const int row = 48;
        w.NowMs += 3; // asked for a moment later, after a layout pass
        glide.AnimateTo(Taller(-row), new GlideTiming(foldStart, foldMs, EaseIn: true));
        while (w.OnFrame != null && w.NowMs < foldStart + 400)
        {
            w.NowMs += Frame;
            if (Math.Abs(w.NowMs - foldStart - 103) < Frame / 2)
                glide.AnimateTo(Taller(-row - 1)); // the list's height rounds to the next pixel mid-fold
            w.Tick();
            // The row's own curve (ease in over its 200 ms); after the extra pixel, between that and the one a pixel further.
            double k = Math.Pow(Math.Min(1, (w.NowMs - foldStart) / foldMs), 3);
            bool nudged = w.NowMs - foldStart > 100;
            double high = Start.Height - row * k, low = Start.Height - (nudged ? row + 1 : row) * k;
            Check.True(w.Rect.Height >= low - 1 && w.Rect.Height <= high + 1, $"at {w.NowMs - foldStart} ms: {w.Rect.Height}, the row's curve says {low:F1}..{high:F1}");
        }
        Check.Equal(Taller(-row - 1), w.Landed, "lands on the final height");
        Check.True(w.Steps[^1].T <= foldStart + foldMs + Frame, $"as the row is gone ({w.Steps[^1].T - foldStart} ms)");
    }

    // ---- The list ----

    private static void FoldAsksFinalHeight()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            for (int i = 0; i < 6; i++) ws.AddTask($"Check clash report batch {i} for level {i % 4}");
            ws.Complete(ws.AddTask("Send the model")); // the Done heading is there already, so only the row goes
            var view = new SpaceView(ws);
            var tasks = new TasksView();
            tasks.Initialize(ws, view, null!);
            using var window = Ui.Host(tasks);
            const double width = 340;
            var foldMs = new List<double>();
            bool folding = false;
            tasks.RowFolding += ms =>
            {
                foldMs.Add(ms);
                folding = true;
            };

            double before = tasks.FittedHeight(width);
            var task = view.Open[2];
            var row = (FrameworkElement)tasks.OpenList.ItemContainerGenerator.ContainerFromItem(task);
            double slot = row.DesiredSize.Height;
            Check.True(slot > 20, $"the row has a height ({slot})");
            var box = Ui.Descendants<ToggleButton>(row).First(b => b.Name == "Check");

            // What the list asks for as the row starts to fold (still its full height), and with it half folded:
            // taken in the fold's own first moment, so they don't depend on how many frames a busy PC draws.
            // A list that chased the row would ask for its old height there, then half a row too much.
            var atStart = new List<(double Height, double Row)>();
            tasks.RowFolding += _ =>
            {
                tasks.UpdateLayout();
                atStart.Add((tasks.FittedHeight(width), row.DesiredSize.Height));
                row.Height = slot / 2; // as the fold has it halfway (its animation starts after this, from the full height)
                tasks.UpdateLayout();
                atStart.Add((tasks.FittedHeight(width), row.DesiredSize.Height));
                row.ClearValue(FrameworkElement.HeightProperty);
                tasks.UpdateLayout();
            };

            // And every frame drawn from then until the row is gone, however few.
            var asked = new List<(double Height, double Row)>();
            EventHandler sample = (_, _) =>
            {
                if (folding && !(task.IsDone && !task.IsChecking)) asked.Add((tasks.FittedHeight(width), row.DesiredSize.Height));
            };
            CompositionTarget.Rendering += sample;
            try
            {
                box.IsChecked = true; // as a click does: ticked, and the row folds after the grace period
                Ui.WaitUntil(() => task.IsDone && !task.IsChecking, 3000);
            }
            finally
            {
                CompositionTarget.Rendering -= sample;
            }
            Ui.Pump();

            double after = tasks.FittedHeight(width);
            Check.True(task.IsDone && !view.Open.Contains(task), "the task is done");
            Check.Sequence([200.0], foldMs, "the panel heard the row start to fold, and for how long");
            Check.True(Math.Abs(after - (before - slot)) < 1, $"the list lost the row ({before} → {after}, the row was {slot})");
            Check.True(atStart.Count == 2 && Math.Abs(atStart[0].Row - slot) < 0.5 && Math.Abs(atStart[1].Row - slot / 2) < 0.5,
                $"looked at as the fold starts, the row whole then half folded ({string.Join(", ", atStart.Select(a => a.Row.ToString("F1")))})");
            foreach (var (height, rowHeight) in atStart.Concat(asked))
                Check.True(Math.Abs(height - after) < 1, $"with the row at {rowHeight:F1} of {slot:F1}, the list asks for its final height ({after:F1}), not {height:F1}, " +
                    $"so the panel never chases it (frames: {string.Join(" ", asked.Select(a => a.Height.ToString("F1")))})");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }
}
