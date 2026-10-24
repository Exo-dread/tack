using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Model;
using Tack.Sync;
using Tack.Views;
using Tack.Windows;

namespace Tack.Tests;

/// <summary>
/// The panel's own tabs, in this process: shown in a window far off screen that is never
/// activated (no input goes anywhere else), typed into the way the keyboard types (through the
/// editor's selection, inside its change block).
/// </summary>
internal static class ScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("notes on screen: typing on a space's blank paper makes its sheet and keeps every keystroke, with no error", TypingOnBlankPaper);
        yield return ("tasks on screen: the line to the task in focus in another space keeps room for the task beside a long space name", NowLineKeepsTheTask);
        yield return ("notes on screen: what's typed on a space's blank paper can be undone, all of it, on the sheet it made", UndoOnBlankPaper);
        yield return ("panel on screen: the toast keeps Undo inside the panel, however long its message", ToastKeepsUndo);
        yield return ("peek on screen: a long space name gives way to the counts and stays inside the bubble; with only Main, as before", PeekLongName);
        yield return ("menus on screen: Move to lists a thousand spaces at a time, the rest under More spaces, made when it opens", MoveMenuInPages);
    }

    private static void MoveMenuInPages()
    {
        Ui.Run(errors =>
        {
            var tabs = Enumerable.Range(0, SpaceView.MaxListed + 5).Select(i => new SpaceTab($"s{i}", $"space {i}")).ToList();
            var few = new ContextMenu();
            SpaceMenu.Fill(few.Items, tabs.Take(3).ToList(), "s1", _ => { });
            Check.Equal(3, few.Items.Count, "a few: each of them");
            Check.True(!((MenuItem)few.Items[1]).IsEnabled, "the current one greyed out");

            string? picked = null;
            var menu = new ContextMenu();
            SpaceMenu.Fill(menu.Items, tabs, "s0", id => picked = id);
            Check.Equal(SpaceView.MaxListed + 1, menu.Items.Count, "a thousand, and More spaces");
            var more = (MenuItem)menu.Items[^1];
            Check.Equal("More spaces, 5 more", System.Windows.Automation.AutomationProperties.GetName(more), "which says how many");
            Check.Equal(1, more.Items.Count, "made only when it opens");
            more.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, more));
            Check.Equal(5, more.Items.Count, "then the rest");
            var last = (MenuItem)more.Items[^1];
            Check.Equal($"space {SpaceView.MaxListed + 4}", System.Windows.Automation.AutomationProperties.GetName(last), "in order");
            last.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, last));
            Check.Equal($"s{SpaceView.MaxListed + 4}", picked, "and each can be picked");
            Check.Equal(0, errors.Count, "no error");
        });
    }

    private static void UndoOnBlankPaper()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.AddSpace("Work");
            var view = new SpaceView(ws);
            var notes = new NotesView();
            notes.Initialize(ws, view, null!);
            var editor = (TextBox)notes.FindName("Editor");
            using var window = Ui.Host(notes);

            // One keystroke, then Undo.
            var one = ws.AddSpace("UndoOne");
            view.Show(one.Id);
            Ui.Pump();
            Check.True(view.ActiveSheet == null && editor.Text.Length == 0 && !editor.CanUndo, "blank paper, nothing to undo");
            Ui.Type(editor, "F");
            var sheet = view.ActiveSheet;
            Check.True(sheet != null && sheet.Text == "F", "the first keystroke made the sheet");
            Check.True(editor.CanUndo, "and it can be undone");
            editor.Undo();
            Ui.Pump();
            Check.Equal("", editor.Text, "undone on the paper");
            Check.Equal("", sheet!.Text, "and on its sheet");
            Check.Equal(1, view.Sheets.Count, "no other sheet");

            // Several: Undo goes all the way back, not just to the first letter, and no further.
            var two = ws.AddSpace("BlankY");
            view.Show(two.Id);
            Ui.Pump();
            Check.True(!editor.CanUndo, "nothing to undo from the space before");
            Ui.Type(editor, "Hello world");
            var second = view.ActiveSheet;
            Check.True(second != null && second.Text == "Hello world" && second.Space == two.Id, "one sheet with all of it, in its space");
            for (int i = 0; i < 40 && editor.CanUndo; i++)
            {
                editor.Undo();
                Ui.Pump();
            }
            Check.Equal("", editor.Text, "all of it undone");
            Check.Equal("", second!.Text, "on its sheet too");
            Ui.Type(editor, "Hi");
            Check.Equal("Hi", second.Text, "typing goes on on the same sheet");
            Check.Equal(1, view.Sheets.Count, "still one sheet");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    /// <summary>The panel's own toast markup (PanelWindow.xaml), without its click handler, to lay out on its own.</summary>
    private static string ToastMarkup()
    {
        string xaml = File.ReadAllText(Path.Combine(Program.Root, "src", "Tack", "Windows", "PanelWindow.xaml"));
        var toast = Regex.Match(xaml, @"<Border x:Name=""Toast"".*?</Border>", RegexOptions.Singleline);
        Check.True(toast.Success, "the panel's toast is in PanelWindow.xaml");
        return Regex.Replace(toast.Value, @"\sClick=""[^""]*""", "").Replace(
            "<Border x:Name=\"Toast\"",
            "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Name=\"Toast\"");
    }

    private static void ToastKeepsUndo()
    {
        Ui.Run(errors =>
        {
            const string longName = "Coordination with the MEP and structural teams north tower L";
            string markup = ToastMarkup();
            foreach (double width in new[] { Settings.DefaultWidth, Settings.MinWidth })
            {
                foreach (string message in new[]
                {
                    "Task deleted",
                    $"Moved to {SpaceView.ToastName(longName)}",
                    $"Deleted {SpaceView.ToastName(longName)}",
                    $"Moved to {longName}",
                    "Added to tasks: Call the plumber about the l…",
                })
                {
                    var toast = (Border)XamlReader.Parse(markup);
                    var text = (TextBlock)toast.FindName("ToastText");
                    var undo = (Button)toast.FindName("ToastUndo");
                    text.Text = message;
                    toast.Visibility = Visibility.Visible;
                    var area = new Grid { Width = width, Height = 120, ClipToBounds = true };
                    area.Children.Add(toast);
                    using var window = Ui.Host(area);

                    string what = $"{width}: '{message}'";
                    var u = undo.TransformToAncestor(area).TransformBounds(new Rect(undo.RenderSize));
                    var t = text.TransformToAncestor(area).TransformBounds(new Rect(text.RenderSize));
                    Check.True(undo.IsVisible && u.Width > 20, $"{what}: Undo shows ({u})");
                    Check.True(u.Right <= width - toast.Margin.Right + 0.5, $"{what}: Undo inside the panel ({u.Right} of {width})");
                    Check.True(t.Right <= u.Left + 0.5, $"{what}: the message before it ({t.Right}, Undo at {u.Left})");
                    double needs = Natural(text);
                    if (needs + 120 < width) Check.True(text.ActualWidth >= needs - 0.5, $"{what}: a message with room shows in full ({text.ActualWidth} of {needs})");
                }
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void PeekLongName()
    {
        Ui.Run(errors =>
        {
            // Only Main: one line of counts, as before spaces.
            var alone = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            alone.AddTask("Check the clash report", asNow: true);
            alone.AddTask("Send the model");
            Peek(new SpaceView(alone), texts =>
            {
                var line = texts.Single(t => t.Text.Contains("open", StringComparison.Ordinal));
                Check.Equal("Summary", line.Name, "with only Main, the counts' own line");
                Check.Equal("2 open", line.Text, "as before");
            });

            const string name = "Riverside Hospital BIM coordination, phase two north tower";
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var far = ws.AddSpace(name);
            var dev = ws.AddSpace("Dev");
            ws.AddTask("Check the clash report", asNow: true, space: far.Id);
            ws.AddTask("Send the model", space: far.Id);
            ws.AddTask("Two", space: far.Id);
            ws.Complete(ws.AddTask("Done one", space: far.Id));
            var view = new SpaceView(ws);

            // Its own space on show: the counts in full after the name, which gives way.
            view.Show(far.Id);
            Peek(view, texts =>
            {
                var counts = texts.Single(t => t.Text.Contains("open", StringComparison.Ordinal));
                Check.True(counts.Text.Contains("3 open", StringComparison.Ordinal) && counts.Text.Contains("1 done today", StringComparison.Ordinal), $"the counts ({counts.Text})");
                Check.True(counts.ActualWidth >= Natural(counts) - 0.5, $"the counts in full ({counts.ActualWidth:F1} of {Natural(counts):F1})");
                Check.True(texts.Any(t => t.Text.StartsWith("Riverside", StringComparison.Ordinal)), "the space's name");
            });

            // Another space on show: "NOW IN …", cut at the bubble's edge.
            view.Show(dev.Id);
            Peek(view, texts => Check.True(texts.Any(t => t.Text.StartsWith("NOW IN RIVERSIDE", StringComparison.Ordinal)), "the label names the space"));

            // A short name shows in full.
            ws.RenameSpace(far.Id, "Work");
            view.Show(far.Id);
            Peek(view, texts =>
            {
                var shown = texts.Single(t => t.Text.StartsWith("Work", StringComparison.Ordinal));
                Check.True(shown.ActualWidth >= Natural(shown) - 0.5, "a short name in full");
            });
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    /// <summary>Fills a peek for <paramref name="view"/> and lays its bubble out; every line of text must stay inside it, then <paramref name="check"/> runs.</summary>
    private static void Peek(SpaceView view, Action<List<TextBlock>> check)
    {
        var peek = new PeekWindow();
        peek.Fill(view, "");
        var bubble = (Border)peek.FindName("Bubble");
        peek.Content = null;
        using (Ui.Host(new Grid { Children = { bubble }, HorizontalAlignment = HorizontalAlignment.Left }))
        {
            var texts = Ui.Descendants<TextBlock>(bubble).Where(t => t.IsVisible && t.Text.Length > 0).ToList();
            double right = bubble.ActualWidth - bubble.Padding.Right - bubble.BorderThickness.Right;
            foreach (var t in texts)
            {
                var r = t.TransformToAncestor(bubble).TransformBounds(new Rect(t.RenderSize));
                Check.True(r.Right <= right + 0.5, $"'{t.Text}' inside the bubble (to {r.Right:F1} of {right:F1})");
            }
            check(texts);
        }
        peek.Close();
    }

    /// <summary>The width a text block's text needs on one line.</summary>
    private static double Natural(TextBlock t)
    {
        var copy = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight, FontStyle = t.FontStyle, FontStretch = t.FontStretch };
        copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return copy.DesiredSize.Width;
    }

    private static void NowLineKeepsTheTask()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var far = ws.AddSpace("Riverside Hospital BIM coordination, phase two (St Mary's)");
            var work = ws.AddSpace("Work");
            const string task = "Check the clash report";
            ws.AddTask(task, asNow: true, space: far.Id);
            var view = new SpaceView(ws);
            view.Show(work.Id);
            var tasks = new TasksView();
            tasks.Initialize(ws, view, null!);
            using var window = Ui.Host(tasks);

            var line = (FrameworkElement)tasks.FindName("NowLine");
            Check.Equal(Visibility.Visible, line.Visibility, "the line shows");
            var blocks = Ui.Descendants<TextBlock>(line).ToList();
            var text = blocks.FirstOrDefault(b => b.Text == task);
            var name = blocks.FirstOrDefault(b => b.Text.StartsWith("Now in ", StringComparison.Ordinal));
            Check.True(text != null && name != null, "the space's name and the task, each in a text of its own");
            double width = ((FrameworkElement)VisualTreeHelper.GetParent(text!)).ActualWidth;
            Check.True(width > 100, $"laid out ({width})");
            Check.True(name!.ActualWidth <= width * 0.55 + 0.5, $"the name takes at most 55% ({name.ActualWidth} of {width})");
            Check.True(text!.ActualWidth >= width * 0.4, $"the task keeps the rest ({text.ActualWidth} of {width})");

            // A short name leaves the task all the room it needs.
            ws.RenameSpace(far.Id, "Dev");
            Ui.Pump();
            Check.True(name.ActualWidth < width * 0.3, $"a short name is short ({name.ActualWidth})");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void TypingOnBlankPaper()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.AddSpace("Work");
            var view = new SpaceView(ws);
            var notes = new NotesView();
            notes.Initialize(ws, view, null!);
            var editor = (TextBox)notes.FindName("Editor");
            var hint = (FrameworkElement)notes.FindName("Hint");
            using var window = Ui.Host(notes);

            // More than three within a minute: Tack closes after the fourth unhandled UI error.
            for (int n = 1; n <= 4; n++)
            {
                var space = ws.AddSpace($"Space {n}");
                view.Show(space.Id);
                Ui.Pump();
                Check.True(view.ActiveSheet == null && editor.Text.Length == 0, $"{n}: blank paper");

                Ui.Type(editor, "Plan\n- call the client");
                const string typed = "Plan\r\n- call the client"; // as the TextBox (and the sheet) have it
                var sheet = view.ActiveSheet;
                Check.True(sheet != null && view.Sheets.Count == 1, $"{n}: the first keystroke made one sheet");
                Check.Equal(space.Id, sheet!.Space, $"{n}: in the space on show");
                Check.Equal(typed, sheet.Text, $"{n}: with every keystroke");
                Check.Equal(typed, editor.Text, $"{n}: on the paper");
                Check.Equal(Visibility.Collapsed, hint.Visibility, $"{n}: the hint gone");
                Check.Equal(0, errors.Count, $"{n}: no error\n{string.Join("\n", errors)}");

                // Away and back: still all there, and the next keystrokes land too.
                view.Show(SpaceField.Main);
                Ui.Pump();
                view.Show(space.Id);
                Ui.Pump();
                Check.Equal(typed, editor.Text, $"{n}: back again");
                Ui.Type(editor, "!");
                Check.Equal(typed + "!", sheet.Text, $"{n}: typing on");
                Check.Equal(1, view.Sheets.Count, $"{n}: still one sheet");
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }
}

/// <summary>Helpers for tests of Tack's own controls: one WPF application with Tack's theme, windows off screen.</summary>
internal static class Ui
{
    private static Application? _app;

    /// <summary>Runs <paramref name="test"/> with Tack's theme loaded; errors the dispatcher meets are collected for it to check.</summary>
    public static void Run(Action<List<Exception>> test)
    {
        if (_app == null)
        {
            _app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Tack;component/Themes/Light.xaml") });
            _app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Tack;component/Themes/Theme.xaml") });
        }
        var errors = new List<Exception>();
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }
        _app.Dispatcher.UnhandledException += OnError;
        try
        {
            test(errors);
        }
        finally
        {
            _app.Dispatcher.UnhandledException -= OnError;
        }
    }

    /// <summary>Shows <paramref name="content"/> in a window far off screen, never activated; disposing closes it.</summary>
    public static IDisposable Host(FrameworkElement content)
    {
        var window = new Window
        {
            Content = content,
            Width = 360,
            Height = 480,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
        };
        window.Show();
        Pump();
        return new Closer(window);
    }

    /// <summary>Lets everything queued run: bindings, layout, deferred work.</summary>
    public static void Pump()
    {
        for (int i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Lets the dispatcher run, timers and the frames that drive animations too, until <paramref name="done"/> or <paramref name="timeoutMs"/>.</summary>
    public static void WaitUntil(Func<bool> done, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        poll.Tick += (_, _) =>
        {
            if (!done() && clock.ElapsedMilliseconds < timeoutMs) return;
            poll.Stop();
            frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Every element of type <typeparamref name="T"/> inside <paramref name="root"/>, in the visual tree.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>Types at the end of the box one character at a time, as the keyboard does ("\n" is Enter's line break).</summary>
    public static void Type(TextBox box, string text)
    {
        foreach (char ch in text)
        {
            box.CaretIndex = box.Text.Length;
            box.SelectedText = ch == '\n' ? Environment.NewLine : ch.ToString();
            box.CaretIndex = box.Text.Length;
            Pump();
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose()
        {
            window.Close();
            Pump();
        }
    }
}
