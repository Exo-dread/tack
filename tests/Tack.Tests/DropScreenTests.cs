using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Controls;
using Tack.Drop;
using Tack.Drop.Core;
using Tack.Model;
using Tack.Services;
using Tack.Sync;
using Tack.Views;
using Tack.Windows;

namespace Tack.Tests;

/// <summary>
/// Dropping onto the orb, on screen: the orb's own drag routing, the module between the orb, its bubble and the
/// panel's toast (with stand-ins that record, so nothing appears on the real screen), and the highlight, the
/// bubble and the paperclips laid out and drawn in light and dark (far off screen, never activated). With
/// TACK_RENDERS set to a folder, the drawings are saved there as PNGs to look at.
/// </summary>
internal static class DropScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("drop on screen: the orb takes drags only once it has a drop target, hands each step on and reports its effect", OrbRouting);
        yield return ("drop on screen: dragged over, the orb lights up and the bubble says what a drop adds; let go, the toast and bubble offer one Undo", ModuleFlow);
        yield return ("drop and snippets share the panel's one Undo: a snippet's toast retires the drop's, and each lapses once, as the panel's slot says", SharedUndo);
        yield return ("drop and fade when idle: a drag over the orb tells the shell, so a faded orb comes back for it", OrbDragHover);
        yield return ("drop on screen: the orb shows a plus and an accent halo while it's a target, and is exactly as before when it isn't", OrbHighlight);
        yield return ("drop on screen: the bubble keeps Undo inside it and its words in one line each, in light and dark", BubbleLayout);
        yield return ("tasks on screen: a task with an attachment shows a paperclip Narrator can name, in the list, Done and the focus card; others none", Paperclips);
        yield return ("settings on screen: running as administrator, Settings says other apps can't drop onto the orb; otherwise nothing shows", ElevatedHint);
    }

    /// <summary>Windows keeps other apps' drags from an app running as administrator; Settings says why drops don't land.</summary>
    private static void ElevatedHint() => Ui.Run(_ =>
    {
        try
        {
            Elevation.Override = true;
            Check.Equal(Visibility.Visible, new SettingsView().ElevatedHint.Visibility, "as administrator, the hint shows");
            Elevation.Override = false;
            Check.Equal(Visibility.Collapsed, new SettingsView().ElevatedHint.Visibility, "otherwise it doesn't");
        }
        finally
        {
            Elevation.Override = null;
        }
    });

    // ================= Stand-ins =================

    /// <summary>The panel's toast, as it behaves: one Undo on offer, settled when it lapses, is used or is replaced by none.</summary>
    private sealed class Toast : IUndoToast
    {
        private Action? _undo;
        public List<string> Shown { get; } = new();
        public List<string> Announced { get; } = new();
        public bool HasPendingUndo => _undo != null;
        public event Action? UndoSettled;

        public void ShowToast(string message, Action? undo = null)
        {
            bool settled = _undo != null && undo == null;
            _undo = undo;
            Shown.Add(message);
            if (settled) UndoSettled?.Invoke();
        }

        public void Announce(string text) => Announced.Add(text);
        public bool Offers(Action undo) => _undo == undo;

        public bool UndoIf(Action undo)
        {
            if (_undo != undo) return false;
            Lapse();
            undo();
            return true;
        }

        public void Lapse()
        {
            if (_undo == null) return;
            _undo = null;
            UndoSettled?.Invoke();
        }
    }

    /// <summary>The panel's toast as PanelWindow wires it: one <see cref="UndoSlot"/> for every toast, its Settled as UndoSettled.</summary>
    private sealed class SlotToast : IUndoToast
    {
        private readonly UndoSlot _slot = new();
        public SlotToast() => _slot.Settled += () => UndoSettled?.Invoke();
        public List<string> Shown { get; } = new();
        public bool HasPendingUndo => _slot.CanUndo;
        public event Action? UndoSettled;
        public void ShowToast(string message, Action? undo = null) => Show(message, undo, null);
        public void Show(string message, Action? undo, Action? lapsed)
        {
            _slot.Hold(undo, lapsed);
            Shown.Add(message);
        }
        public void Announce(string text) { }
        public bool Offers(Action undo) => _slot.Holds(undo);
        public bool UndoIf(Action undo)
        {
            if (!_slot.Holds(undo)) return false;
            _slot.Take()!();
            return true;
        }
        public void Lapse() => _slot.Lapse();
    }

    private sealed class Bubble : IDropBubble
    {
        public string State { get; private set; } = "hidden";
        public string? Title { get; private set; }
        public string? Caption { get; private set; }
        public bool Refused { get; private set; }
        public Action? OfferedUndo { get; private set; }
        public bool IsShown => State != "hidden";
        public event Action<Action>? UndoRequested;

        public void ShowHint(string title, string? caption, bool refused) => (State, Title, Caption, Refused, OfferedUndo) = ("hint", title, caption, refused, null);
        public void ShowDone(string title, string? caption, Action undo, TimeSpan showFor) => (State, Title, Caption, Refused, OfferedUndo) = ("done", title, caption, false, undo);
        public void ShowNote(string title, TimeSpan showFor) => (State, Title, Caption, OfferedUndo) = ("note", title, null, null);
        public void Dismiss() => (State, OfferedUndo) = ("hidden", null);
        public void ClickUndo() => UndoRequested?.Invoke(OfferedUndo!);
    }

    // ================= The orb =================

    private static DragEventArgs DragArgs(RoutedEvent routed, IDataObject data, DragDropEffects allowed, DependencyObject target)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 5);
        var e = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point(20, 20)]);
        e.RoutedEvent = routed;
        return e;
    }

    private sealed class Target : IOrbDropTarget
    {
        public List<string> Steps { get; } = new();
        public DragDropEffects DragEnter(IDataObject data, DragDropEffects allowed) { Steps.Add("enter"); return DragDropEffects.Copy; }
        public DragDropEffects DragOver(DragDropEffects allowed) { Steps.Add("over"); return DragDropEffects.Copy; }
        public void DragLeave() => Steps.Add("leave");
        public DragDropEffects Drop(IDataObject data, DragDropEffects allowed) { Steps.Add("drop"); return DragDropEffects.Link; }
    }

    private static void OrbRouting()
    {
        Ui.Run(errors =>
        {
            var orb = new OrbWindow();
            var bead = orb.Orb;
            var data = new DataObject(DataFormats.UnicodeText, "x");
            Check.True(!bead.AllowDrop, "without a drop target the orb takes no drop, as before");
            var none = DragArgs(DragDrop.DragEnterEvent, data, DragDropEffects.All, bead);
            bead.RaiseEvent(none);
            Check.Equal(DragDropEffects.None, none.Effects, "and says so");

            var target = new Target();
            orb.DropTarget = target;
            Check.True(bead.AllowDrop, "with one, it does");
            foreach (var (routed, want) in new[] { (DragDrop.DragEnterEvent, DragDropEffects.Copy), (DragDrop.DragOverEvent, DragDropEffects.Copy), (DragDrop.DropEvent, DragDropEffects.Link) })
            {
                var e = DragArgs(routed, data, DragDropEffects.All, bead);
                bead.RaiseEvent(e);
                Check.True(e.Handled, $"{routed.Name}: handled");
                Check.Equal(want, e.Effects, $"{routed.Name}: the target's effect");
            }
            bead.RaiseEvent(DragArgs(DragDrop.DragLeaveEvent, data, DragDropEffects.All, bead));
            Check.Sequence(["enter", "over", "drop", "leave"], target.Steps, "each step handed on");

            orb.ShowDropTarget(true);
            Check.True(bead.IsDropTarget, "lit up");
            orb.DropTarget = null;
            Check.True(!bead.AllowDrop && !bead.IsDropTarget, "and back to as before when it's taken away");
            orb.Close();
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void ModuleFlow()
    {
        Ui.Run(errors =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
                var view = new SpaceView(ws);
                var module = new DropModule(folder, ws, view, new DropContext { Probe = Launcher.Core.RealFileProbe.Instance, IsProgram = DesktopDrop.IsProgram });
                var toast = new Toast();
                var bubble = new Bubble();
                var lit = new List<bool>();
                int made = 0;
                module.Attach(lit.Add, toast, () => { made++; return bubble; });
                Check.Equal(0, made, "no bubble until something is dragged over");
                Check.True(!File.Exists(Path.Combine(folder, AttachmentStore.FileName)), "and no attachments.json");

                // Something Tack can't take: nothing at all happens.
                var picture = new DataObject();
                picture.SetData("PNG", new MemoryStream([1, 2, 3]));
                Check.Equal(DragDropEffects.None, module.DragEnter(picture, DragDropEffects.Copy), "a picture isn't taken");
                Check.Equal(0, lit.Count + made, "no light, no bubble");
                module.DragLeave();

                // Over the orb: lit, and the bubble says what letting go will do.
                string a = Path.Combine(folder, "Clash report L3.pdf"), b = Path.Combine(folder, "Level 3");
                File.WriteAllText(a, "x");
                Directory.CreateDirectory(b);
                var files = new DataObject(DataFormats.FileDrop, new[] { a, b });
                Check.Equal(DragDropEffects.Link, module.DragEnter(files, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move), "linked");
                Check.Equal(DragDropEffects.Copy, module.DragOver(DragDropEffects.Copy | DragDropEffects.Move), "copied when the source won't link (Ctrl)");
                Check.True(lit[^1], "the orb lit");
                Check.Equal("hint", bubble.State, "the bubble");
                Check.Equal("Drop to add 2 tasks", bubble.Title, "says what it adds");
                Check.Equal(null, bubble.Caption, "with only Main, nothing more");
                Check.True(module.BubbleShown, "the peek waits");
                module.DragLeave();
                Check.True(!lit[^1] && bubble.State == "hidden", "gone again when the drag moves on");

                // Too many: not lit, and the bubble says why.
                var many = new DataObject(DataFormats.FileDrop, Enumerable.Range(0, 30).Select(i => $@"C:\f{i}.pdf").ToArray());
                Check.Equal(DragDropEffects.None, module.DragEnter(many, DragDropEffects.All), "refused");
                Check.True(!lit[^1] && bubble.Refused && bubble.Title == DropReader.TooMany, "and said so, muted");
                Check.Equal(DragDropEffects.None, module.Drop(many, DragDropEffects.All), "a drop anyway does nothing");
                Check.Equal(0, ws.Open.Count, "no task");

                // In a space: the tasks go there, and the bubble says so.
                var work = ws.AddSpace("Work");
                view.Show(work.Id);
                module.DragEnter(files, DragDropEffects.All);
                Check.Equal("In Work", bubble.Caption, "where they'll go");
                Check.Equal(DragDropEffects.Link, module.Drop(files, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move), "dropped");
                Check.Equal(2, view.Open.Count, "two tasks in Work");
                Check.True(view.Open.All(t => t.Space == work.Id && t.AttachmentTip != null), "each with its paperclip");
                Check.Equal("Added 2 tasks", toast.Shown[^1], "the panel's toast");
                Check.Equal("done", bubble.State, "the bubble");
                Check.Equal("Added 2 tasks", bubble.Title, "says the same");
                Check.True(bubble.OfferedUndo != null && toast.Offers(bubble.OfferedUndo), "with the toast's own Undo");
                Check.True(!lit[^1], "the orb back as it was");

                // Undo from the bubble: the tasks go, and their attachments once that Undo is spent.
                var added = view.Open.ToList();
                bubble.ClickUndo();
                Check.True(added.All(t => t.IsDeleted), "Undo took them back");
                Ui.Pump();
                Check.Equal("hidden", bubble.State, "the bubble gone with its Undo");
                Check.Equal(0, module.Store.Count, "their attachments let go");

                // A delete with its Undo on offer keeps the attachment; once it lapses, it goes.
                module.DragEnter(files, DragDropEffects.All);
                module.Drop(files, DragDropEffects.All);
                toast.Lapse();
                Ui.Pump();
                var task = view.Open[0];
                var undo = ws.Delete(task);
                toast.ShowToast("Task deleted", undo);
                module.Prune();
                Check.Equal(2, module.Store.Count, "kept while Undo is on offer");
                toast.Lapse();
                Ui.Pump();
                Check.Equal(1, module.Store.Count, "let go once it lapsed");
                Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        });
    }

    private static void SharedUndo()
    {
        Ui.Run(errors =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
                var view = new SpaceView(ws);
                var module = new DropModule(folder, ws, view, new DropContext { Probe = Launcher.Core.RealFileProbe.Instance, IsProgram = DesktopDrop.IsProgram });
                var toast = new SlotToast();
                var bubble = new Bubble();
                module.Attach(_ => { }, toast, () => bubble);
                int settled = 0;
                toast.UndoSettled += () => settled++;

                string a = Path.Combine(folder, "Clash report L3.pdf");
                File.WriteAllText(a, "x");
                var files = new DataObject(DataFormats.FileDrop, new[] { a });
                module.DragEnter(files, DragDropEffects.All);
                module.Drop(files, DragDropEffects.All);
                var dropped = view.Open[0];
                var dropUndo = bubble.OfferedUndo!;
                Check.True(toast.Offers(dropUndo) && toast.HasPendingUndo, "the drop's Undo is the toast's");

                // A snippet discarded in Notes: its toast takes the one slot, so the drop's Undo is past using.
                int snippetLapsed = 0, snippetUndone = 0;
                toast.Show("Snippet discarded", () => snippetUndone++, () => snippetLapsed++);
                Check.Equal(1, settled, "the drop's Undo settled once");
                Ui.Pump();
                Check.Equal("hidden", bubble.State, "the bubble's Undo went with it");
                Check.True(!toast.UndoIf(dropUndo) && !dropped.IsDeleted, "and can't undo the drop any more");
                Check.Equal(1, module.Store.Count, "the attachment stays: its task is still here");

                // The task deleted with Undo: the snippet's Undo lapses (its file would go), the attachment waits.
                var undo = ws.Delete(dropped);
                toast.ShowToast("Task deleted", undo);
                Check.Equal((1, 0), (snippetLapsed, snippetUndone), "the snippet's Undo lapsed once, unused");
                Check.Equal(2, settled, "settled again");
                Ui.Pump();
                Check.Equal(1, module.Store.Count, "kept while the delete's Undo is on offer");

                // Used: settled before it runs, and the attachment is kept with its task back.
                Check.True(toast.UndoIf(undo), "Undo, from the toast");
                Check.Equal(3, settled, "settled as it was used");
                Ui.Pump();
                Check.True(!dropped.IsDeleted && module.Store.Count == 1, "the task back, with its paperclip");

                // Deleted again, and this time the Undo lapses: the attachment goes.
                toast.ShowToast("Task deleted", ws.Delete(dropped));
                toast.Lapse();
                Check.Equal(4, settled, "settled when it lapsed");
                toast.Lapse();
                toast.ShowToast("Nothing to undo");
                Check.Equal(4, settled, "never without an Undo");
                Ui.Pump();
                Check.Equal(0, module.Store.Count, "let go once nothing can bring it back");
                Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        });
    }

    private static void OrbDragHover()
    {
        Ui.Run(errors =>
        {
            var orb = new OrbWindow();
            var bead = orb.Orb;
            var data = new DataObject(DataFormats.UnicodeText, "x");
            var seen = new List<bool>();
            orb.DragHoverChanged += seen.Add;
            orb.DropTarget = new Target();
            bead.RaiseEvent(DragArgs(DragDrop.DragEnterEvent, data, DragDropEffects.All, bead));
            bead.RaiseEvent(DragArgs(DragDrop.DragOverEvent, data, DragDropEffects.All, bead));
            bead.RaiseEvent(DragArgs(DragDrop.DragLeaveEvent, data, DragDropEffects.All, bead));
            bead.RaiseEvent(DragArgs(DragDrop.DragEnterEvent, data, DragDropEffects.All, bead));
            bead.RaiseEvent(DragArgs(DragDrop.DropEvent, data, DragDropEffects.All, bead));
            Check.Sequence([true, false, true, false], seen, "over on entering, away on leaving or letting go");
            orb.Close();
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ================= Drawings =================

    /// <summary>Light or dark, as ThemeManager sets it: the palette, the accent's shade, and the orb's materials.</summary>
    private static void InTheme(bool light, Action draw)
    {
        var resources = Application.Current.Resources;
        var palettes = resources.MergedDictionaries;
        var before = palettes[0];
        palettes[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tack;component/Themes/{(light ? "Light" : "Dark")}.xaml") };
        AccentPalette.Apply(resources, AccentPalette.Resolve("#60CDFF", light));
        var isLight = typeof(ThemeManager).GetProperty(nameof(ThemeManager.IsLight))!;
        bool was = ThemeManager.IsLight;
        isLight.SetValue(null, light);
        try { draw(); }
        finally
        {
            palettes[0] = before;
            isLight.SetValue(null, was);
        }
    }

    private static BitmapSource Draw(FrameworkElement e, double scale = 2)
    {
        if (e.Parent == null && VisualTreeHelper.GetParent(e) == null)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
        }
        e.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * scale), (int)Math.Ceiling(e.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(e);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("TACK_RENDERS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        png.Save(file);
    }

    private static Color Pixel(BitmapSource b, double x, double y, double scale = 2)
    {
        var px = new byte[4];
        b.CopyPixels(new Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static double Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static void OrbHighlight()
    {
        Ui.Run(errors =>
        {
            foreach (bool light in new[] { true, false })
            {
                InTheme(light, () =>
                {
                    string theme = light ? "light" : "dark";
                    var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
                    var desk = new SolidColorBrush(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x22, 0x28));
                    BitmapSource Bead(bool drop)
                    {
                        var orb = new OrbVisual { Count = 3, Progress = 0.4, IsDropTarget = drop, Width = 64, Height = 64 };
                        var scene = new Grid { Width = 64, Height = 64, Background = desk, Children = { orb } };
                        var b = Draw(scene);
                        Save(b, $"orb-{(drop ? "drop" : "plain")}-{theme}");
                        return b;
                    }
                    var plain = Bead(false);
                    var lit = Bead(true);
                    Check.True(Distance(Pixel(lit, 32, 32), accent) < 90, $"{theme}: a plus in the accent at the centre ({Pixel(lit, 32, 32)})");
                    // The rim: 1.5 wide, centred 19.25 from the middle.
                    Check.True(Distance(Pixel(lit, 32, 12.75), accent) < 120, $"{theme}: the rim in the accent ({Pixel(lit, 32, 12.75)})");
                    Check.True(Distance(Pixel(plain, 32, 12.75), accent) > Distance(Pixel(lit, 32, 12.75), accent) + 60, $"{theme}: not when it isn't a target");
                    var haloLit = Pixel(lit, 8.5, 32);
                    var haloPlain = Pixel(plain, 8.5, 32);
                    Check.True(Distance(haloLit, accent) < Distance(haloPlain, accent) - 20, $"{theme}: a soft halo outside ({haloLit} vs {haloPlain})");

                    // Not a target: exactly the bead 1.6.1 drew (the same drawing as a fresh one never lit).
                    var again = new OrbVisual { Count = 3, Progress = 0.4, Width = 64, Height = 64 };
                    again.IsDropTarget = true;
                    again.IsDropTarget = false;
                    var back = Draw(new Grid { Width = 64, Height = 64, Background = desk, Children = { again } });
                    var p1 = new byte[128 * 128 * 4];
                    var p2 = new byte[128 * 128 * 4];
                    plain.CopyPixels(p1, 128 * 4, 0);
                    back.CopyPixels(p2, 128 * 4, 0);
                    Check.True(p1.SequenceEqual(p2), $"{theme}: unlit again, not a pixel different");
                });
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void BubbleLayout()
    {
        Ui.Run(errors =>
        {
            foreach (bool light in new[] { true, false })
            {
                InTheme(light, () =>
                {
                    string theme = light ? "light" : "dark";
                    foreach (var (name, title, caption, undo, muted) in new (string, string, string?, bool, bool)[]
                    {
                        ("hint", "Drop to add a task", null, false, false),
                        ("hint-space", "Drop to add 3 tasks", "In Riverside Hospital BIM coordination, phase two", false, false),
                        ("refused", DropReader.TooMany, null, false, true),
                        ("done", "Added 3 tasks", "In Work", true, false),
                        ("done-one", "Added a task", "RE: Clash report L3 – level 3 east wing, ducts through the transfer beam", true, false),
                        ("done-note", "Added 3 tasks", "1 email wasn't kept: over 50 MB", true, false),
                    })
                    {
                        var window = new DropBubble();
                        window.Fill(title, caption, undo, muted);
                        var bubble = (Border)window.FindName("Bubble");
                        var button = (Button)window.FindName("UndoButton");
                        ((Panel)window.FindName("Root")).Children.Remove(bubble);
                        var scene = new Grid { Background = new SolidColorBrush(light ? Color.FromRgb(0xE8, 0xE8, 0xEA) : Color.FromRgb(0x30, 0x31, 0x36)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Children = { bubble } };
                        using (Ui.Host(scene))
                        {
                            double right = bubble.ActualWidth - bubble.Padding.Right - bubble.BorderThickness.Right;
                            Check.True(bubble.ActualWidth <= 300.5, $"{name}: at most 300 wide ({bubble.ActualWidth})");
                            foreach (var t in Ui.Descendants<TextBlock>(bubble).Where(t => t.IsVisible && t.Text.Length > 0))
                            {
                                var r = t.TransformToAncestor(bubble).TransformBounds(new Rect(t.RenderSize));
                                Check.True(r.Right <= right + 0.5, $"{name}: '{t.Text}' inside the bubble");
                                Check.True(t.ActualHeight < 22, $"{name}: '{t.Text}' on one line");
                            }
                            Check.Equal(undo, button.IsVisible, $"{name}: Undo {(undo ? "offered" : "not offered")}");
                            if (undo)
                            {
                                var u = button.TransformToAncestor(bubble).TransformBounds(new Rect(button.RenderSize));
                                Check.True(u.Right <= bubble.ActualWidth - bubble.BorderThickness.Right + 0.5 && u.Width > 30, $"{name}: Undo inside, in full ({u})");
                            }
                            Save(Draw(scene), $"bubble-{name}-{theme}");
                        }
                        window.Close();
                    }
                });
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Paperclips()
    {
        Ui.Run(errors =>
        {
            foreach (bool light in new[] { true, false })
            {
                InTheme(light, () =>
                {
                    string theme = light ? "light" : "dark";
                    var ws = new Workspace(new Settings { DoneExpanded = true }, new Hlc("desk0001"), WorkspaceData.Empty);
                    var finished = ws.AddTask("Send the federated model to the client");
                    ws.Complete(finished);
                    var plain = ws.AddTask("Call the structural engineer about the transfer beam");
                    var link = ws.AddTask("Riverside Hospital – Files");
                    var mail = ws.AddTask("RE: Clash report L3");
                    var now = ws.AddTask("Clash report L3", asNow: true);
                    now.AttachmentTip = "Open Clash report L3.pdf";
                    mail.AttachmentTip = "Open email";
                    link.AttachmentTip = "Open acc.autodesk.com";
                    finished.AttachmentTip = "Open Federated model.nwd";
                    var view = new SpaceView(ws);
                    var tasks = new TasksView();
                    tasks.Initialize(ws, view, null!);
                    var surface = new Border { Child = tasks, Width = 340, Padding = new Thickness(0, 12, 0, 0) };
                    surface.SetResourceReference(Border.BackgroundProperty, "SolidSurfaceBrush");
                    using (Ui.Host(surface))
                    {
                        var clips = Ui.Descendants<Button>(tasks).Where(b => b.IsVisible && (string)b.Content == "\uE723").ToList();
                        var names = clips.Select(AutomationProperties.GetName).OrderBy(n => n).ToList();
                        Check.Sequence(new[] { "Open acc.autodesk.com", "Open Clash report L3.pdf", "Open email", "Open Federated model.nwd" }.OrderBy(n => n).ToList(), names,
                            "a paperclip for each task with an attachment (focus card, list, Done), named for Narrator");
                        foreach (var clip in clips)
                        {
                            var r = clip.TransformToAncestor(tasks).TransformBounds(new Rect(clip.RenderSize));
                            Check.True(r.Right <= tasks.ActualWidth - 8 && r.Width >= 20, $"{AutomationProperties.GetName(clip)}: inside the row, big enough to click ({r})");
                            Check.Equal(AutomationProperties.GetName(clip), clip.ToolTip as string, "its tooltip says what it opens");
                        }

                        // A row without one is laid out exactly as it was: same height as a twin task that never had a paperclip.
                        Check.True(!Ui.Descendants<Button>(tasks).Any(b => b.IsVisible && b.DataContext == plain && (string)b.Content == "\uE723"), "none on a task without one");
                        Save(Draw(surface), $"tasks-paperclips-{theme}");

                        // The menu leads with what the paperclip opens.
                        var editor = Ui.Descendants<TextBox>(tasks).First(t => t.DataContext == mail);
                        var menu = editor.ContextMenu!;
                        menu.PlacementTarget = editor;
                        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                        var open = menu.Items.OfType<MenuItem>().First();
                        Check.True(open.Visibility == Visibility.Visible && (string)open.Header == "Open email" && open.InputGestureText == "Ctrl+O", "Open email (Ctrl+O) in its menu");
                        var other = Ui.Descendants<TextBox>(tasks).First(t => t.DataContext == plain);
                        var otherMenu = other.ContextMenu!;
                        otherMenu.PlacementTarget = other;
                        otherMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, otherMenu));
                        Check.Equal(Visibility.Collapsed, otherMenu.Items.OfType<MenuItem>().First().Visibility, "and not in a menu without one");

                        // Taken away (its task deleted for good): the paperclip goes, the row as before.
                        double before = RowHeight(tasks, mail);
                        mail.AttachmentTip = null;
                        Ui.Pump();
                        Check.True(!Ui.Descendants<Button>(tasks).Any(b => b.IsVisible && b.DataContext == mail && (string)b.Content == "\uE723"), "gone with its attachment");
                        Check.Equal(before, RowHeight(tasks, mail), "the row the same height");
                    }
                });
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static double RowHeight(TasksView tasks, TaskItem task) =>
        Ui.Descendants<Border>(tasks).First(b => b.Name == "Row" && b.DataContext == task).ActualHeight;
}
