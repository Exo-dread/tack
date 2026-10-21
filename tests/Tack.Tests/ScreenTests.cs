using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Model;
using Tack.Sync;
using Tack.Views;

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
