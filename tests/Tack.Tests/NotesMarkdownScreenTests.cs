using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Sync;
using Tack.Views;
using Tack.Windows;

namespace Tack.Tests;

/// <summary>
/// Markdown in Notes on screen (docs/NOTES-MARKDOWN.md): the note formatted while you read it, the editor while you
/// write. Notes in a window far off screen, never activated; keys and text arrive as WPF's own events, raised on
/// the view (nothing is typed or clicked on this PC).
/// </summary>
internal static class NotesMarkdownScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("notes markdown on screen: a note shows formatted on opening, with the keyboard on it; empty, blank paper and over 200,000 characters stay the editor", FormattedOnOpen);
        yield return ("notes markdown on screen: a click opens the editor with the caret at the start of that line, the line kept where it was", ClickToEdit);
        yield return ("notes markdown on screen: typing a character, Enter or F2 on the formatted note opens the editor, and the character is typed", TypingFromReader);
        yield return ("notes markdown on screen: ticking a box changes exactly [ ] to [x] as one edit (saved, clocked); Ctrl+Z opens the editor and undoes it", TickAndUndo);
        yield return ("notes markdown on screen: the editor losing the keyboard, a sheet switch or a change from another device shows it formatted again", BackToReading);
        yield return ("notes markdown on screen: search opens the editor with the match selected; a new sheet and blank paper write at once", SelectionOpensEditor);
        yield return ("notes markdown on screen: Format notes off is 1.8.0: always the editor; on again, formatted", SettingOff);
        yield return ("notes markdown: Format notes is on by default, written to tack.json only when off, and never taken from an import", SettingInFile);
        yield return ("notes markdown on screen: Narrator hears headings as headings, list items and checkboxes by their text", Narrator);
        yield return ("notes markdown on screen: nothing in a formatted note opens anything; a link is styled text with its url as a tooltip", LinksOpenNothing);
        yield return ("notes markdown on screen: scrolling a long note makes only the rows in view, and keeps its place", Scrolling);
        yield return ("notes markdown on screen: a 20 KB note is laid out and drawn at 150% quickly, 200 KB within bounds", Quick);
    }

    // ---- A note to look at ----

    internal const string Sample = """
        # Level 3 coordination
        Clash run *Tuesday*, report to **MEP** by `Thu 12:00`. See [the BEP](https://example.com/bep).

        ## Today
        - [x] Federate the structural model
        - [ ] Check duct clearances on ~~L2~~ L3
        - [ ] Answer the architect about the stair core
          about the landing width

        1. Purge unused families
        2. Export the IFC
           - with base quantities
           - IFC4 reference view

        > Keep penetrations off the transfer beams.
        > - unless the engineer signs it off

        ```
        Navisworks: Clash Detective > Run all
        ```

        ---
        Plain line one
        Plain line two
        """;

    private sealed class Notes : IDisposable
    {
        private readonly IDisposable _window;

        public Notes(string text, Action<Settings>? settings = null)
        {
            var s = new Settings();
            settings?.Invoke(s);
            Ws = new Workspace(s, new Hlc("desk0001"), WorkspaceData.Empty);
            Ws.ActiveSheet.Text = NoteSheet.ForTextBox(text);
            View = new SpaceView(Ws);
            Page = new NotesView();
            Page.Initialize(Ws, View, null!, host: new Host());
            _window = Ui.Host(Page);
            Ui.Pump();
        }

        public Workspace Ws { get; }
        public SpaceView View { get; }
        public NotesView Page { get; }
        public NoteSheet Sheet => View.ActiveSheet!;
        public TextBox Editor => (TextBox)Page.FindName("Editor");
        public ScrollViewer Scroller => (ScrollViewer)Page.FindName("ReaderScroller");
        public NoteReader Reader => Page.ReaderPanel;
        public Window Window => Window.GetWindow(Page);

        public void Dispose() => _window.Dispose();

        /// <summary>Raises a key on the formatted note, as the keyboard would (PreviewKeyDown, then KeyDown).</summary>
        public void Key(Key key)
        {
            var source = PresentationSource.FromVisual(Reader);
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Reader.RaiseEvent(down);
            if (!down.Handled) Reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Ui.Pump();
        }

        /// <summary>Raises typed text on an element (the formatted note unless said), as the keyboard would.</summary>
        public void Text(string text, UIElement? on = null)
        {
            var target = on ?? Reader;
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text)) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            Ui.Pump();
        }

        public int RowOf(int offset)
        {
            int i = Reader.Rows.ToList().FindIndex(r => r.Line == offset);
            Check.True(i >= 0, $"the line at {offset} has its row");
            return i;
        }

        /// <summary>A point on the line starting at <paramref name="offset"/> in the formatted note (scrolled into view if it isn't).</summary>
        public Point PointOfLine(int offset)
        {
            int row = RowOf(offset);
            if (Reader.TextTopOf(row) == null) Reader.ScrollRowTo(row, 60);
            return new Point(60, Reader.TextTopOf(row)!.Value + 6);
        }

        public double TopOfLineInReader(int offset) => Reader.TranslatePoint(new Point(0, Reader.TextTopOf(RowOf(offset))!.Value), Page).Y;

        public double TopOfLineInEditor(int offset) => Editor.TranslatePoint(new Point(0, Editor.GetRectFromCharacterIndex(offset).Top), Page).Y;

        public CheckBox BoxOf(int row) => Reader.ViewOf(row)?.Box ?? throw new TestFailure($"row {row} has no checkbox in view");

        public List<int> TaskRows() => Enumerable.Range(0, Reader.Rows.Count).Where(i => Reader.Rows[i].Item?.Task != null).ToList();
    }

    private sealed class Host : INotesHost
    {
        public void ShowToast(string message, Action? undo = null, Action? lapsed = null) { }
        public void Announce(string text) { }
        public void ViewSnippet(string path, Snippets.Snippet snippet) { }
        public void EndUndo() { }
    }

    private static int LineOf(string text, string startsWith)
    {
        int at = text.StartsWith(startsWith, StringComparison.Ordinal) ? 0 : text.IndexOf("\n" + startsWith, StringComparison.Ordinal) + 1;
        Check.True(at > 0 || text.StartsWith(startsWith, StringComparison.Ordinal), $"'{startsWith}' is in the note");
        return at;
    }

    private static void Shown(Notes n, bool reading, string what)
    {
        Check.Equal(reading, n.Page.IsReading, $"{what}: {(reading ? "formatted" : "the editor")}");
        Check.Equal(reading ? Visibility.Visible : Visibility.Collapsed, n.Scroller.Visibility, $"{what}: the formatted note {(reading ? "shown" : "not there")}");
        Check.Equal(reading ? Visibility.Hidden : Visibility.Visible, n.Editor.Visibility, $"{what}: the editor {(reading ? "hidden" : "shown")}");
    }

    private static bool Focused(Notes n, IInputElement element) => ReferenceEquals(FocusManager.GetFocusedElement(n.Window), element);

    // ---- Reading and writing ----

    private static void FormattedOnOpen() => Ui.Run(errors =>
    {
        using (var n = new Notes(Sample))
        {
            Shown(n, true, "a note");
            var rows = n.Reader.Rows;
            Check.True(rows[0].Kind == RowKind.Heading && rows[0].Level == 1, "its heading");
            Check.Equal(FontWeights.SemiBold, n.Reader.ViewOf(0)!.Text.FontWeight, "in bold");
            Check.Equal(3, n.TaskRows().Count, "its checkboxes");
            Check.Equal(true, n.BoxOf(n.TaskRows()[0]).IsChecked, "the ticked one ticked");
            Check.True(rows.Any(r => r.Kind == RowKind.Code) && rows.Any(r => r.Kind == RowKind.Rule) && rows.Any(r => r.Quote == 1), "its code, rule and quote");
            n.Page.FocusDefault();
            Ui.Pump();
            Check.True(Focused(n, n.Reader), "opening Notes puts the focus on the formatted note, not in the editor");
            Shown(n, true, "after opening");
        }
        using (var n = new Notes("   \n\t\n"))
        {
            Shown(n, false, "an empty note");
            n.Page.FocusDefault();
            Ui.Pump();
            Check.True(Focused(n, n.Editor), "an empty note: the editor, with its hint");
        }
        using (var n = new Notes("x"))
        {
            n.View.Show(n.Ws.AddSpace("Blank").Id);
            Ui.Pump();
            Check.True(n.View.ActiveSheet == null, "blank paper");
            Shown(n, false, "blank paper");
        }
        // 200,000 characters of the note's own text (line breaks one each, as Tack Focus counts them).
        string limit = "# heading\n" + string.Concat(Enumerable.Repeat(new string('a', 99) + "\n", 1999)) + new string('a', 90);
        Check.Equal(Markdown.MaxFormatted, limit.Length, "(the case is 200,000 long)");
        using (var n = new Notes(limit))
            Shown(n, true, "200,000 characters (more in the editor's CR LF)");
        using (var n = new Notes(limit + "a"))
        {
            Shown(n, false, "200,001 characters");
            n.Page.FocusDefault();
            Ui.Pump();
            Shown(n, false, "200,001 characters, opened");
            Check.True(Focused(n, n.Editor), "the editor has the focus");
        }
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void ClickToEdit() => Ui.Run(errors =>
    {
        foreach (string target in new[] { "# Level", "- [ ] Answer", "  about the landing", "2. Export", "   - IFC4", "Plain line two", "Navisworks:", "> - unless", "---" })
        {
            using var n = new Notes(Sample);
            int line = LineOf(n.Editor.Text, target);
            var point = n.PointOfLine(line);
            double before = n.TopOfLineInReader(line);
            n.Page.ClickToEdit(point);
            Ui.Pump();
            Shown(n, false, target);
            Check.Equal(line, n.Editor.CaretIndex, $"{target}: the caret at the start of the line clicked");
            Check.Equal(0, n.Editor.SelectionLength, $"{target}: nothing selected");
            Check.True(Focused(n, n.Editor), $"{target}: the focus in the editor");
            double after = n.TopOfLineInEditor(line);
            Check.True(Math.Abs(after - before) <= 2 || after < before && n.Editor.VerticalOffset == 0, // the editor can't scroll above its top
                $"{target}: the line stays where it was ({before:F1} formatted, {after:F1} in the editor)");
        }

        // Scrolled: a long note, a line in the middle of the view stays where it was on screen.
        string longNote = string.Concat(Enumerable.Range(0, 120).Select(i => i % 10 == 0 ? $"## Part {i}\n\n" : $"- item {i}\n")) + "the end";
        using (var n = new Notes(longNote))
        {
            n.Reader.SetVerticalOffset(n.Reader.ExtentHeight / 2);
            Ui.Pump();
            int row = n.Reader.RowAt(new Point(60, n.Reader.ViewportHeight / 2));
            int line = n.Reader.Rows[row].Line;
            double before = n.TopOfLineInReader(line);
            n.Page.ClickToEdit(new Point(60, n.Reader.TextTopOf(row)!.Value + 6));
            Ui.Pump();
            Check.Equal(line, n.Editor.CaretIndex, "scrolled: the caret on the line clicked");
            double after = n.TopOfLineInEditor(line);
            Check.True(n.Editor.VerticalOffset > 0 && Math.Abs(after - before) <= 2, $"scrolled: the line stays put ({before:F1} formatted, {after:F1} in the editor)");

            // And back: the editor losing the keyboard shows the same line where it was.
            double editorTop = n.TopOfLineInEditor(line);
            Keyboard.ClearFocus();
            Ui.Pump();
            Shown(n, true, "back to reading");
            int top = n.Reader.RowAt(new Point(60, 1));
            Check.True(Math.Abs(n.Reader.Rows[top].Line - n.Reader.Rows[n.Reader.RowAt(n.Reader.TranslatePoint(new Point(0, 0), n.Reader))].Line) == 0, "(rows found)");
            double readerTop = n.TopOfLineInReader(n.Reader.Rows[n.Reader.RowOfLine(line)].Line);
            Check.True(Math.Abs(readerTop - editorTop) <= 30, $"back: the note scrolled to where the editor was ({editorTop:F1} in the editor, {readerTop:F1} formatted)");
        }
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void TypingFromReader() => Ui.Run(errors =>
    {
        using (var n = new Notes("# Title\nbody"))
        {
            n.Editor.CaretIndex = n.Editor.Text.Length; // where the caret was last time
            n.Text("!");
            Shown(n, false, "a character");
            Check.Equal("# Title\r\nbody!", n.Sheet.Text, "typed where the caret was");
            Check.True(Focused(n, n.Editor), "the focus in the editor");
        }
        using (var n = new Notes("- one"))
        {
            n.Editor.CaretIndex = n.Editor.Text.Length;
            n.Key(Key.Enter);
            Shown(n, false, "Enter");
            Check.Equal("- one\r\n- ", n.Sheet.Text, "Enter on a list line continues the list, as in the editor");
        }
        using (var n = new Notes("plain"))
        {
            n.Editor.CaretIndex = 2;
            n.Key(Key.F2);
            Shown(n, false, "F2");
            Check.Equal("plain", n.Sheet.Text, "F2 types nothing");
            Check.Equal(2, n.Editor.CaretIndex, "the caret where it was");
        }
        using (var n = new Notes("plain"))
        {
            n.Text("\u0001"); // Ctrl+A's character: not typing
            Shown(n, true, "a control character");
            n.Key(Key.Down);
            Shown(n, true, "an arrow scrolls, it doesn't write");
            Check.True(n.Page.ReaderKey(Key.V, ModifierKeys.Control) && !n.Page.IsReading, "Ctrl+V: the editor, to paste into");
        }
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void TickAndUndo() => Ui.Run(errors =>
    {
        using var n = new Notes(Sample + "\n" + string.Concat(Enumerable.Repeat("filler line\n", 40)));
        string before = n.Sheet.Text;
        var clk = n.Sheet.Clk.Text;
        int saves = 0;
        n.Ws.Changed += (_, _) => saves++;
        int row = n.TaskRows()[1];
        int at = n.Reader.Rows[row].Item!.At!.Value;
        Check.Equal(' ', before[at], "an open box");
        n.Reader.SetVerticalOffset(20);
        Ui.Pump();
        double scrolled = n.Reader.VerticalOffset;
        Check.True(scrolled > 10, "(scrolled a little)");
        n.Editor.Select(5, 3); // the editor's own selection, left as it was
        ((IToggleProvider)new CheckBoxAutomationPeer(n.BoxOf(row)).GetPattern(PatternInterface.Toggle)).Toggle();
        Ui.Pump();
        string after = n.Sheet.Text;
        Check.Equal(before[..at] + "x" + before[(at + 1)..], after, "exactly that [ ] is now [x]");
        Check.True(saves > 0 && n.Sheet.Clk.Text != clk, "saved and stamped, as typing is");
        Check.True(n.Editor.SelectionStart == 5 && n.Editor.SelectionLength == 3, "the editor's caret and selection as they were");
        Shown(n, true, "after a tick");
        Check.Equal(true, n.BoxOf(row).IsChecked, "shown ticked");
        Check.True(n.Reader.ViewOf(row)!.Text.TextDecorations == TextDecorations.Strikethrough, "its text struck through");
        Check.True(Math.Abs(n.Reader.VerticalOffset - scrolled) < 1, $"and scrolled as it was ({scrolled:F1}, now {n.Reader.VerticalOffset:F1})");

        // A click on a box ticks the other way too: [x] back to [ ].
        int first = n.TaskRows()[0];
        n.BoxOf(first).IsChecked = false;
        Ui.Pump();
        Check.Equal(' ', n.Sheet.Text[n.Reader.Rows[first].Item!.At!.Value], "[x] back to [ ]");

        Check.True(n.Page.ReaderKey(Key.Z, ModifierKeys.Control), "Ctrl+Z is Notes' own");
        Ui.Pump();
        Shown(n, false, "Ctrl+Z");
        Check.Equal(after, n.Sheet.Text, "Undo takes back the last tick");
        n.Editor.Undo();
        Ui.Pump();
        Check.Equal(before, n.Sheet.Text, "and the one before");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void BackToReading() => Ui.Run(errors =>
    {
        using var n = new Notes("# One\ntext");
        var one = n.Sheet;
        var two = n.Ws.AddSheet(text: "## Two");
        n.Ws.ActiveSheet = one;
        Ui.Pump();
        n.Page.ClickToEdit(n.PointOfLine(0));
        Ui.Pump();
        Shown(n, false, "clicked");

        // The editor loses the keyboard.
        Check.True(n.Editor.IsKeyboardFocused, "the editor has the keyboard");
        Keyboard.ClearFocus();
        Ui.Pump();
        Shown(n, true, "the editor lost the keyboard");

        // A sheet switch shows the other sheet formatted.
        n.Page.ClickToEdit(n.PointOfLine(0));
        Ui.Pump();
        n.Ws.ActiveSheet = two;
        Ui.Pump();
        Shown(n, true, "a sheet switch");
        Check.Equal("Two", n.Reader.Rows[0].Name, "the sheet switched to");

        // Another device changes it: read again; emptied: the editor.
        two.Text = "## Two\r\n- [ ] new";
        Ui.Pump();
        Check.Equal(1, n.TaskRows().Count, "read again after a change from elsewhere");
        two.Text = "";
        Ui.Pump();
        Shown(n, false, "emptied elsewhere");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void SelectionOpensEditor() => Ui.Run(errors =>
    {
        using (var n = new Notes("# Head\nfind the needle here"))
        {
            int needle = n.Sheet.Text.IndexOf("needle", StringComparison.Ordinal);
            n.Page.Reveal(n.Sheet, (needle, needle + 6));
            Ui.Pump();
            Shown(n, false, "search");
            Check.Equal(needle, n.Editor.SelectionStart, "the match selected");
            Check.Equal("needle", n.Editor.SelectedText, "all of it");
        }
        using (var n = new Notes("# Head"))
        {
            n.Ws.AddSheet(); // Ctrl+N's sheet: empty, so the editor
            Ui.Pump();
            Shown(n, false, "a new sheet");
        }
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void SettingOff() => Ui.Run(errors =>
    {
        using var n = new Notes(Sample, s => s.FormatNotes = false);
        Shown(n, false, "off");
        n.Page.FocusDefault();
        Ui.Pump();
        Check.True(Focused(n, n.Editor), "off: opening Notes puts the caret in the editor, as in 1.8.0");
        Keyboard.ClearFocus();
        Ui.Pump();
        Shown(n, false, "off, after the editor lost the keyboard");
        Check.Equal(0, n.Reader.Rows.Count, "off: nothing read or made");

        n.Ws.Settings.FormatNotes = true;
        Ui.Pump();
        Shown(n, true, "on again (not writing)");
        n.Ws.Settings.FormatNotes = false;
        Ui.Pump();
        Shown(n, false, "off again");
        Check.Equal(0, n.Reader.Rows.Count, "and its rows gone");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void SettingInFile()
    {
        static JsonElement Saved(Settings settings) => JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new StateDocument { Settings = settings },
            StateJsonContext.Default.StateDocument)).RootElement.GetProperty("settings");
        Check.True(new Settings().FormatNotes, "on by default");
        Check.True(!Saved(new Settings()).TryGetProperty("formatNotes", out _), "not written while on (tack.json, and the shared vectors, as before)");
        var off = Saved(new Settings { FormatNotes = false });
        Check.True(off.TryGetProperty("formatNotes", out var v) && v.ValueKind == JsonValueKind.False, "written as false when off");
        var back = JsonSerializer.Deserialize("{\"settings\":" + off.GetRawText() + "}", StateJsonContext.Default.StateDocument)!.Settings;
        Check.True(!back.FormatNotes, "read back off");
        var on = JsonSerializer.Deserialize("{\"settings\":{\"formatNotes\":null}}", StateJsonContext.Default.StateDocument)!.Settings;
        Check.True(on.FormatNotes, "null reads as on");

        // An import carries records only: a file saying it's off changes nothing here.
        var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
        string file = "{\"settings\":{\"formatNotes\":false,\"theme\":\"dark\"},\"tasks\":[{\"id\":\"t1\",\"text\":\"from elsewhere\",\"created\":\"2026-10-01T09:00:00Z\"}],\"sheets\":[]}";
        FileImport.Run(ws, Encoding.UTF8.GetBytes(file), null);
        Check.True(ws.Settings.FormatNotes, "an import never turns it off");
    }

    // ---- Narrator ----

    private static void Narrator() => Ui.Run(errors =>
    {
        using var n = new Notes(Sample);
        var peers = new List<AutomationPeer>();
        void Walk(AutomationPeer peer, int depth)
        {
            if (depth > 8) return;
            foreach (var child in peer.GetChildren() ?? [])
            {
                peers.Add(child);
                Walk(child, depth + 1);
            }
        }
        var root = UIElementAutomationPeer.CreatePeerForElement(n.Reader);
        Check.Equal("Note", root.GetName(), "the formatted note is named Note");
        Check.Equal(AutomationControlType.Document, root.GetAutomationControlType(), "a document");
        Walk(root, 0);
        var headings = peers.Where(p => p.GetHeadingLevel() != AutomationHeadingLevel.None).ToList();
        Check.True(headings.Any(h => h.GetName() == "Level 3 coordination" && h.GetHeadingLevel() == AutomationHeadingLevel.Level1),
            "the first heading, level 1, by its text: " + string.Join(", ", headings.Select(h => h.GetName())));
        Check.True(headings.Any(h => h.GetName() == "Today" && h.GetHeadingLevel() == AutomationHeadingLevel.Level2), "the second, level 2");
        var boxes = peers.Where(p => p.GetAutomationControlType() == AutomationControlType.CheckBox).ToList();
        Check.Sequence(new[] { "Federate the structural model", "Check duct clearances on L2 L3", "Answer the architect about the stair core about the landing width" },
            boxes.Select(b => b.GetName()).ToList(), "checkboxes named by their text");
        Check.Equal(ToggleState.On, ((IToggleProvider)boxes[0].GetPattern(PatternInterface.Toggle)).ToggleState, "the ticked one says so");
        var items = peers.Where(p => p.GetAutomationControlType() == AutomationControlType.ListItem).Select(p => p.GetName()).ToList();
        Check.True(items.Contains("Purge unused families") && items.Contains("with base quantities"), "list items by their text: " + string.Join(" | ", items));
        Check.True(peers.All(p => p.GetAutomationControlType() != AutomationControlType.Text || p.GetName().Length > 0 || p.GetClassName() == "Line"), "nothing nameless but blank lines");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void LinksOpenNothing() => Ui.Run(errors =>
    {
        using var n = new Notes("[open me](file:///C:/Windows/System32/calc.exe) [x](javascript:alert(1))");
        var all = new List<Inline>();
        void Walk(IEnumerable<Inline> inlines)
        {
            foreach (var i in inlines)
            {
                all.Add(i);
                if (i is Span s) Walk(s.Inlines);
            }
        }
        Walk(n.Reader.ViewOf(0)!.Text.Inlines);
        Check.True(!all.OfType<Hyperlink>().Any(), "no hyperlink anywhere");
        var links = all.OfType<Span>().Where(s => s.TextDecorations == TextDecorations.Underline).ToList();
        Check.Equal(2, links.Count, "two links, styled");
        Check.Equal("file:///C:/Windows/System32/calc.exe", links[0].ToolTip, "the url shown as text in its tooltip");
        n.Page.ClickToEdit(n.PointOfLine(0));
        Ui.Pump();
        Shown(n, false, "a click on a link: the editor, like any other line");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    private static void Scrolling() => Ui.Run(errors =>
    {
        using var n = new Notes(Long(60_000));
        int rows = n.Reader.Rows.Count;
        int made() => Ui.Descendants<RowView>(n.Reader).Count();
        Check.True(rows > 1000 && made() < 60, $"only the rows in view are made ({made()} of {rows})");
        n.Reader.SetVerticalOffset(n.Reader.ExtentHeight * 0.6);
        Ui.Pump();
        double at = n.Reader.VerticalOffset;
        Check.True(at > 0 && made() < 60, $"scrolled: still only those in view ({made()})");
        int top = n.Reader.RowAt(new Point(60, 0));
        n.Reader.LineDown();
        Ui.Pump();
        Check.True(n.Reader.VerticalOffset > at, "an arrow scrolls on");
        n.Reader.SetVerticalOffset(double.MaxValue);
        Ui.Pump();
        Check.True(n.Reader.RowAt(new Point(60, n.Reader.ViewportHeight - 2)) == rows - 1, "the end: the last row at the bottom");
        n.Reader.SetVerticalOffset(0);
        Ui.Pump();
        Check.Equal(0, n.Reader.RowAt(new Point(60, 1)), "the top again");
        Check.True(top > 0, "(it had moved)");
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    // ---- Cost ----

    /// <summary>A note of <paramref name="chars"/> characters, made of <see cref="Sample"/> and its variations.</summary>
    internal static string Long(int chars)
    {
        var sb = new StringBuilder();
        for (int i = 0; sb.Length < chars; i++) sb.Append(Sample.Replace("Level 3", $"Level {i}")).Append("\n\n");
        return sb.ToString(0, chars);
    }

    /// <summary>Shows the note formatted (parsed, its rows made, those in view laid out) and draws it at 150% in software; the best of a few rounds, in ms.</summary>
    private static (double Parse, double Show) Time(Notes n, int rounds = 5)
    {
        double parse = double.MaxValue, show = double.MaxValue;
        string text = n.Editor.Text;
        for (int r = 0; r < rounds; r++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Markdown.Parse(text);
            parse = Math.Min(parse, clock.Elapsed.TotalMilliseconds);
            n.Ws.Settings.FormatNotes = false;
            Ui.Pump();
            Keyboard.ClearFocus(); // not writing: on again, it shows formatted
            Ui.Pump();
            clock.Restart();
            n.Ws.Settings.FormatNotes = true;
            n.Window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(n.Page.ActualWidth * 1.5), (int)(n.Page.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(n.Page);
            show = Math.Min(show, clock.Elapsed.TotalMilliseconds);
            Check.True(n.Page.IsReading, "formatted");
        }
        return (parse, show);
    }

    private static void Quick() => Ui.Run(errors =>
    {
        using (var n = new Notes(Long(20_000)))
        {
            var (parse, show) = Time(n);
            // The budget is 16 ms on the desktop it was built on (--md-timing); a slow CI runner gets room.
            Check.True(show < 60, $"20 KB: shown in {show:F1} ms (parsed in {parse:F2})");
        }
        using (var n = new Notes(Long(Markdown.MaxFormatted)))
        {
            var (parse, show) = Time(n, rounds: 3);
            Check.True(show < 400, $"200 KB: shown in {show:F1} ms (parsed in {parse:F1})");
        }
        Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
    });

    /// <summary>
    /// --md-timing: not a test. What showing a note formatted costs at 150% in software, for 20 KB and 200 KB notes, and
    /// what the editor itself (a TextBox) and a FlowDocument cost for the same text, for comparison.
    /// </summary>
    public static int Measure()
    {
        int code = 0;
        Ui.Run(errors =>
        {
            foreach (int size in new[] { 20_000, Markdown.MaxFormatted })
            {
                using var n = new Notes(Long(size));
                Ui.Pump();
                var (parse, show) = Time(n, rounds: 10);
                Console.WriteLine($"{size / 1000} KB: parse {parse:F2} ms; shown formatted (parse, rows, layout of those in view, drawn at 150%) {show:F1} ms");
                {
                    // Where it goes: reading into rows, laying out those in view, drawing.
                    double read = double.MaxValue, lay = double.MaxValue, draw = double.MaxValue, empty = double.MaxValue;
                    for (int r = 0; r < 10; r++)
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var rows = NoteRows.Build(Markdown.Parse(n.Editor.Text));
                        n.Reader.SetRows(rows, keepPlace: false);
                        double t1 = clock.Elapsed.TotalMilliseconds;
                        n.Window.UpdateLayout();
                        double t2 = clock.Elapsed.TotalMilliseconds;
                        var bitmap = new RenderTargetBitmap((int)(n.Page.ActualWidth * 1.5), (int)(n.Page.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                        bitmap.Render(n.Page);
                        double t3 = clock.Elapsed.TotalMilliseconds;
                        read = Math.Min(read, t1);
                        lay = Math.Min(lay, t2 - t1);
                        draw = Math.Min(draw, t3 - t2);
                        clock.Restart();
                        var again = new RenderTargetBitmap((int)(n.Page.ActualWidth * 1.5), (int)(n.Page.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                        again.Render(n.Page);
                        empty = Math.Min(empty, clock.Elapsed.TotalMilliseconds);
                    }
                    Console.WriteLine($"  read into rows {read:F2} ms, layout {lay:F2} ms, draw {draw:F2} ms (the same page drawn again: {empty:F2} ms)");
                }

                string[] lines = n.Editor.Text.Split("\r\n");
                void Try(string what, Func<FrameworkElement> make)
                {
                    double best = double.MaxValue;
                    for (int r = 0; r < 4; r++)
                    {
                        var host = new Border { Width = 340, Height = 440 };
                        using var window = Ui.Host(host);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var el = make();
                        host.Child = el;
                        host.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)(340 * 1.5), (int)(440 * 1.5), 144, 144, PixelFormats.Pbgra32);
                        bitmap.Render(el);
                        best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
                    }
                    Console.WriteLine($"  for comparison, {what}: {best:F1} ms");
                }
                Try("the editor (a TextBox with the same text)", () => new TextBox { Text = n.Editor.Text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                Try("a FlowDocument, a plain paragraph a line", () =>
                {
                    var d = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, PagePadding = new Thickness(2, 0, 2, 0) };
                    foreach (var l in lines) d.Blocks.Add(new Paragraph(new Run(l)) { Margin = new Thickness(0) });
                    return new FlowDocumentScrollViewer { Document = d, IsToolBarVisible = false };
                });
            }
            if (errors.Count > 0)
            {
                Console.WriteLine(string.Join("\n", errors));
                code = 1;
            }
        });
        return code;
    }

    // ---- Pictures ----

    /// <summary>
    /// --render-notes folder: not a test. The panel on Notes, formatted (and the editor beside it), at 150%: light and
    /// dark, glass on and off, a few accents.
    /// </summary>
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        int result = 0;
        Ui.Run(errors =>
        {
            foreach (string theme in new[] { "light", "dark" })
            foreach (bool glass in new[] { true, false })
            foreach (var (accentName, accent) in new[] { ("system", "system"), ("amber", "#FFC567"), ("lilac", "#B9A4FF") })
            {
                if (!glass && accentName != "system") continue;
                string tag = $"{theme}-{(glass ? "glass" : "solid")}-{accentName}";
                using var shell = new PanelShell(s => { s.Theme = theme; s.Glass = glass; s.Accent = accent; }, ws => ws.ActiveSheet.Text = NoteSheet.ForTextBox(Sample));
                var panel = shell.Panel;
                panel.SelectTab("notes", animate: false);
                Ui.Pump();
                var notes = panel.NotesPage;
                Save(panel, Path.Combine(folder, $"notes-{tag}.png"), theme == "light", glass);
                if (accentName != "system") continue;
                // The same note in the editor, as a click on its first line opens it.
                notes.ClickToEdit(new Point(40, 10));
                Ui.Pump();
                Save(panel, Path.Combine(folder, $"notes-{tag}-editing.png"), theme == "light", glass);
            }
            if (errors.Count > 0)
            {
                Console.WriteLine(string.Join("\n", errors));
                result = 1;
            }
        });
        return result;
    }

    private static void Save(PanelWindow panel, string path, bool light, bool glass)
    {
        panel.UpdateLayout();
        const double scale = 1.5;
        var root = panel.Root;
        double w = root.ActualWidth, h = root.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // What glass would show through: a desktop, a little busy.
            dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xD8, 0xDE, 0xE8) : Color.FromRgb(0x20, 0x24, 0x2C)), null, new Rect(0, 0, w, h));
            if (glass)
                dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0x9F, 0xB8, 0xD8) : Color.FromRgb(0x2C, 0x3A, 0x52)), null, new Rect(w * 0.55, 0, w * 0.45, h));
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
        Console.WriteLine(path);
    }

    /// <summary>Tack's own panel in this process, off screen and never activated (as SpaceStripTests' shell).</summary>
    private sealed class PanelShell : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "tack-md-tests", Guid.NewGuid().ToString("N"));
        private readonly FolderSync _sync;
        private readonly StateStore _store;

        public PanelShell(Action<Settings> settings, Action<Workspace> seed)
        {
            Directory.CreateDirectory(_folder);
            var store = _store = new StateStore(_folder);
            Ws = store.Attach(new StateDocument { Settings = new Settings { Hotkey = "", Theme = "light" } });
            settings(Ws.Settings);
            seed(Ws);
            _sync = new FolderSync(Ws, store);
            App = new AppShell(Ws, store, _sync);
            new WindowInteropHelper(Panel).EnsureHandle();
            ThemeManager.Apply(Ws.Settings.Theme);
            AccentPalette.Apply(Application.Current.Resources, AccentPalette.Resolve(Ws.Settings.Accent, ThemeManager.IsLight));
            Panel.ApplyGlass(Ws.Settings.Glass);
            Panel.ShowActivated = false;
            Panel.Left = -32000;
            Panel.Top = -32000;
            Panel.Show();
            ScreenGeometry.Place(Panel.Handle, -32000, -32000, 510, 930);
            Ui.Pump();
        }

        public Workspace Ws { get; }
        public AppShell App { get; }
        public PanelWindow Panel => App.Panel;

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
}
