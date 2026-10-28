using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Model;
using Tack.Snippets;
using Tack.Sync;
using Tack.Windows;

namespace Tack.Views;

/// <summary>What Notes asks of the panel it's in: toasts (whose Undo says when it lapses), announcements, the snippet viewer.</summary>
internal interface INotesHost
{
    /// <summary>A toast, with Undo when <paramref name="undo"/> is given; <paramref name="lapsed"/> runs once it can't be undone any more.</summary>
    void ShowToast(string message, Action? undo = null, Action? lapsed = null);

    void Announce(string text);

    void ViewSnippet(string path, Snippet snippet);

    /// <summary>The Undo on offer, if there is one, lapses now (a paste needs the room what it holds back).</summary>
    void EndUndo();
}

/// <summary>
/// Notes: a stack of scratch-paper sheets. Plain text, saved as you type,
/// with a couple of quiet helpers (list continuation, timestamps, line-to-task).
/// It shows the sheets of the space on show (<see cref="SpaceView"/>); a space without
/// any shows blank paper until you write on it. An image pasted onto a sheet becomes a
/// snippet: a thumbnail above its text, kept on this PC only (<see cref="SnippetStore"/>).
/// While you read a note (its editor doesn't have the keyboard) it shows formatted, its Markdown laid out
/// (<see cref="NoteDocument"/>, docs/NOTES-MARKDOWN.md); a click or a keystroke turns it back into the editor.
/// </summary>
public partial class NotesView : UserControl
{
    // "- ", "* ", "• ", "[ ] ", "[x] ", "1. " at the start of a line (with optional indentation).
    private static readonly Regex ListPrefix = new(@"^(\s*)(?:([-*•])|\[[ xX]\]|(\d{1,3})([.)]))\s+", RegexOptions.Compiled);

    private readonly Dictionary<NoteSheet, (int Caret, double Scroll)> _positions = new();
    private readonly Dictionary<string, BitmapSource> _thumbs = new(); // by snippet id
    private Workspace _ws = null!;
    private SpaceView _view = null!;
    private PanelWindow _panel = null!;
    private INotesHost? _host;
    private SnippetStore? _snippets;
    private NoteSheet? _shown;

    public NotesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the view up. Without <paramref name="snippets"/>, pasting an image does nothing, as before
    /// snippets; <paramref name="host"/> is the panel unless a test stands in for it.
    /// </summary>
    internal void Initialize(Workspace ws, SpaceView view, PanelWindow panel, SnippetStore? snippets = null, INotesHost? host = null)
    {
        _ws = ws;
        _view = view;
        _panel = panel;
        _host = host ?? panel;
        DataContext = view;
        _shown = view.ActiveSheet;
        InitializeReader();

        if (snippets == null) return;
        _snippets = snippets;
        snippets.Changed += sheet => { if (sheet == _view.ActiveSheet?.Id) ShowSnippets(); };
        // Ctrl+V and Shift+Insert are caught in Editor_PreviewKeyDown; this is the menu's Paste.
        CommandManager.AddPreviewExecutedHandler(Editor, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Paste && PasteImage()) e.Handled = true;
        });
        IsVisibleChanged += (_, _) => { if (!IsVisible) KeepThumbsOf(_view.ActiveSheet?.Id); };
        ShowSnippets();
    }

    /// <summary>What isn't about snippets: the formatted view, set up whether or not snippets are on.</summary>
    private void InitializeReader()
    {
        _ws.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.FormatNotes)) FormatChanged(); };
        Reader.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(Reader_MouseLeftButtonUp), handledEventsToo: true);
        Reader.ContextMenuOpening += (_, e) => _menuAt = e.CursorLeft >= 0 ? new Point(e.CursorLeft, e.CursorTop) : null;
        Reader.Ticked += Tick;
        if (CanRead(_view.ActiveSheet)) ShowReader();
    }

    /// <summary>Snippets are on: an image pasted here (or on Tasks) is kept.</summary>
    internal bool TakesImages => _snippets != null;

    /// <summary>What's on the clipboard. Tests hand in their own instead of touching this PC's clipboard.</summary>
    internal Func<IDataObject?> ReadClipboard { get; set; } = () =>
    {
        try { return Clipboard.GetDataObject(); }
        catch (Exception) { return null; } // another app has it open: the paste goes on as it would have
    };

    /// <summary>The keyboard to the editor (shown, if the note was formatted), its caret where it was.</summary>
    public void FocusEditor()
    {
        ShowEditor();
        Editor.Focus();
        Keyboard.Focus(Editor);
    }

    /// <summary>
    /// Opening Notes, a sheet switch: the note formatted if it can be (the keyboard on it, so typing still writes),
    /// otherwise the editor.
    /// </summary>
    public void FocusDefault()
    {
        if (!CanRead(_view.ActiveSheet))
        {
            FocusEditor();
            return;
        }
        ShowReader();
        Reader.Focus();
        Keyboard.Focus(Reader);
    }

    // ---- Sheets ----

    private void SheetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ws == null) return;
        if (_shown != null) _positions[_shown] = (Editor.CaretIndex, Editor.VerticalOffset);
        _shown = _view.ActiveSheet;
        if (_shown != null) SheetList.ScrollIntoView(_shown);
        ShowSnippets();

        // The sheet just made from what was typed on blank paper: the text on the paper is its
        // text, and what was typed stays undoable (the history held nothing else: landing on the
        // blank paper cleared it).
        bool madeOnPaper = _makingFromPaper;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // A sheet switch replaces the text wholesale; it must not become an undo step (on blank
            // paper, Undo bringing back the text it replaced would make a sheet of it). Cleared here,
            // once the new text is in, and not as the switch happens: that can be inside one of the
            // editor's own changes (a keystroke and whatever it sets off), where the TextBox throws
            // rather than touch its history. WPF runs nothing queued until the change ends, and this
            // still runs before the next keystroke.
            if (!madeOnPaper)
            {
                Editor.IsUndoEnabled = false;
                Editor.IsUndoEnabled = true;
            }
            if (_shown != null && _positions.TryGetValue(_shown, out var p))
            {
                Editor.CaretIndex = Math.Min(p.Caret, Editor.Text.Length);
                Editor.ScrollToVerticalOffset(p.Scroll);
            }
            else
            {
                Editor.CaretIndex = Editor.Text.Length;
            }
            // The note on show is formatted (unless it was just written on blank paper, where typing goes on).
            if (madeOnPaper)
            {
                if (IsVisible) FocusEditor();
            }
            else if (IsVisible) FocusDefault();
            else if (CanRead(_view.ActiveSheet)) ShowReader();
            else ShowEditor();
        });
    }

    private void AddSheet_Click(object sender, RoutedEventArgs e) => AddSheet();

    private void AddSheet()
    {
        _ws.AddSheet(_view.NewItemSpace);
        FocusEditor();
    }

    /// <summary>
    /// The first keystroke on a space's blank paper makes its sheet, with what was typed. Not here
    /// and now: the editor is still inside its change, where switching to the new sheet lost
    /// keystrokes, so the sheet is made as soon as the change is done (before the next keystroke is
    /// handled).
    /// </summary>
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_reading && !_ticking) RenderWhenShown();
        if (_ws == null || _makingSheet || _view.ActiveSheet != null || Editor.Text.Length == 0) return;
        _makingSheet = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _makingSheet = false;
            if (_view.ActiveSheet != null || Editor.Text.Length == 0) return;
            _makingFromPaper = true;
            try
            {
                _ws.AddSheet(_view.NewItemSpace, Editor.Text);
            }
            finally
            {
                _makingFromPaper = false;
            }
        });
    }

    private bool _makingSheet;

    /// <summary>True while the blank paper's sheet is being made (and switched to).</summary>
    private bool _makingFromPaper;

    private void DeleteSheet_Click(object sender, RoutedEventArgs e)
    {
        if ((((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext is not NoteSheet sheet) return;
        DeleteSheet(sheet);
    }

    /// <summary>
    /// Deletes a sheet (the last one is cleared instead), and its snippets with it: their files go once
    /// Undo lapses, and Undo brings them back with the sheet.
    /// </summary>
    internal void DeleteSheet(NoteSheet sheet)
    {
        var held = _snippets?.Of(sheet.Id) ?? [];
        bool wasEmpty = sheet.IsEmpty && held.Count == 0;
        var undo = _ws.DeleteSheet(sheet);
        _positions.Remove(sheet);
        _snippets?.Detach(held);
        if (wasEmpty) return;
        if (held.Count == 0)
        {
            _host?.ShowToast("Sheet deleted", undo);
            return;
        }
        _host?.ShowToast("Sheet deleted", () =>
        {
            undo();
            _snippets?.Attach(held);
        }, () => Forget(held));
    }

    /// <summary>"Move to" shows once there are spaces, listing them all; the sheet's own is greyed out.</summary>
    private void SheetMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || (menu.PlacementTarget as FrameworkElement)?.DataContext is not NoteSheet sheet) return;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag is "MoveTo") is not { } moveTo) return;
        moveTo.Visibility = _view.HasSpaces ? Visibility.Visible : Visibility.Collapsed;
        moveTo.Items.Clear();
        if (!_view.HasSpaces) return;
        SpaceMenu.Fill(moveTo.Items, _view.Tabs, _view.SpaceOf(sheet), space => MoveToSpace(sheet, space));
    }

    /// <summary>Puts a sheet in another space; only its space changes. Undo puts it back.</summary>
    private void MoveToSpace(NoteSheet sheet, string space)
    {
        string before = sheet.Space.Length > 0 ? sheet.Space : SpaceField.Main;
        _ws.MoveSheetToSpace(sheet.Id, space);
        _panel.Announce($"Moved to {_view.NameOf(space)}");
        _panel.ShowToast($"Moved to {SpaceView.ToastName(_view.NameOf(space))}", () =>
        {
            _ws.MoveSheetToSpace(sheet.Id, before);
            if (_view.Sheets.Contains(sheet)) _ws.ActiveSheet = sheet;
        });
        if (IsVisible) FocusDefault();
    }

    private void CycleSheet(int delta)
    {
        var sheets = _view.Sheets;
        int n = sheets.Count;
        if (n < 2 || _view.ActiveSheet is not { } active) return;
        int i = sheets.IndexOf(active);
        _ws.ActiveSheet = sheets[((i + delta) % n + n) % n];
    }

    // ---- Search: going to a sheet ----

    /// <summary>
    /// Search opened this sheet: the caret in it, with the match selected and scrolled to about a
    /// third of the way down the paper (after the sheet switch has put its text and caret in).
    /// </summary>
    internal void Reveal(NoteSheet sheet, (int Start, int End)? match)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_view.ActiveSheet != sheet) return;
            FocusEditor();
            if (match is not { } m) return;
            int start = Math.Min(m.Start, Editor.Text.Length), end = Math.Clamp(m.End, start, Editor.Text.Length);
            Editor.Select(start, end - start);
            int line = Editor.GetLineIndexFromCharacterIndex(start);
            if (line < 0) return;
            Editor.ScrollToLine(line);
            Editor.UpdateLayout();
            var at = Editor.GetRectFromCharacterIndex(start);
            if (!at.IsEmpty && Editor.ViewportHeight > 0)
                Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + at.Top - Editor.ViewportHeight / 3));
        });
    }

    // ---- Editor helpers ----

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.Control:
                SendLineToTasks();
                e.Handled = true;
                break;

            case Key.Enter when mods == ModifierKeys.None:
                e.Handled = ContinueList();
                break;

            case Key.F5:
                Insert(DateTime.Now.ToString("g", CultureInfo.CurrentCulture));
                e.Handled = true;
                break;

            case Key.N when mods == ModifierKeys.Control:
                AddSheet();
                e.Handled = true;
                break;

            case Key.Tab when mods == ModifierKeys.Control:
            case Key.PageDown when mods == ModifierKeys.Control:
                CycleSheet(1);
                e.Handled = true;
                break;

            case Key.Tab when mods == (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.PageUp when mods == ModifierKeys.Control:
                CycleSheet(-1);
                e.Handled = true;
                break;

            // An image becomes a snippet; anything with text in it pastes as it always has.
            case Key.V when mods == ModifierKeys.Control:
            case Key.Insert when mods == ModifierKeys.Shift:
                if (PasteImage()) e.Handled = true;
                break;
        }
    }

    private (int Start, string Text) CurrentLine()
    {
        string text = Editor.Text;
        int caret = Math.Min(Editor.CaretIndex, text.Length);
        int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        int end = text.IndexOf('\n', caret);
        if (end < 0) end = text.Length;
        return (start, text[start..end].TrimEnd('\r'));
    }

    /// <summary>Ctrl+Enter: the selection (or current line) becomes a task. The note keeps its text.</summary>
    private void SendLineToTasks()
    {
        string source = Editor.SelectionLength > 0 ? Editor.SelectedText : CurrentLine().Text;
        var lines = source.Replace("\r", "").Split('\n')
            .Select(l => ListPrefix.Replace(l, "").Trim())
            .Where(l => l.Length > 0 && JsText.CleanTaskText(l).Length <= DeviceFile.MaxTaskText)
            .ToList();
        if (lines.Count == 0) return;
        if (lines.Count > DeviceFile.MaxPasteLines)
        {
            _panel.ShowToast($"Make up to {DeviceFile.MaxPasteLines:N0} tasks at once");
            return;
        }

        // The tasks go in the sheet's own space.
        string space = _view.ActiveSheet is { } sheet ? _view.SpaceOf(sheet) : _view.NewItemSpace;
        var added = new List<TaskItem>();
        for (int i = lines.Count - 1; i >= 0; i--) added.Add(_ws.AddTask(lines[i], space: space));
        string label = lines.Count == 1 ? $"Added to tasks: {Shorten(lines[0])}" : $"Added {lines.Count} tasks";
        _panel.ShowToast(label, () => { foreach (var t in added) _ws.Delete(t); });
    }

    /// <summary>Enter on a list line starts the next item; Enter on an empty item ends the list.</summary>
    private bool ContinueList()
    {
        if (Editor.SelectionLength > 0) return false;
        var (start, line) = CurrentLine();
        var m = ListPrefix.Match(line);
        if (!m.Success) return false;

        // Only continue when the caret is at the end of the line.
        if (Editor.CaretIndex != start + line.Length) return false;

        if (line.Length == m.Length)
        {
            // Empty item: remove the marker and stop the list.
            Editor.Select(start, line.Length);
            Editor.SelectedText = "";
            Editor.CaretIndex = start;
            return true;
        }

        string indent = m.Groups[1].Value;
        string marker;
        if (m.Groups[2].Success) marker = m.Groups[2].Value + " ";
        else if (m.Groups[3].Success) marker = (int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + 1) + m.Groups[4].Value + " ";
        else marker = "[ ] ";
        Insert(Environment.NewLine + indent + marker);
        return true;
    }

    private void Insert(string text)
    {
        Editor.SelectedText = text;
        Editor.CaretIndex = Editor.SelectionStart + Editor.SelectionLength;
        Editor.SelectionLength = 0;
    }

    private static string Shorten(string s) => s.Length > 28 ? s[..27].TrimEnd() + "…" : s;

    // ---- Reading: the note formatted ----

    private bool _reading;
    private string? _renderedText;
    private bool _renderQueued;
    private bool _swapping;
    private bool _ticking;
    private (Point At, int Clicks)? _down;
    private Point? _menuAt;

    /// <summary>The note on show is formatted (the editor is hidden).</summary>
    internal bool IsReading => _reading;

    /// <summary>The formatted note (for tests).</summary>
    internal NoteReader ReaderPanel => Reader;

    /// <summary>
    /// Formatted, unless it's off, there's no sheet (blank paper), it's empty, or longer than Tack Focus would format
    /// (counted as the note's own text, a line break one character, not the editor's CR LF).
    /// </summary>
    private bool CanRead(NoteSheet? sheet)
    {
        if (!_ws.Settings.FormatNotes || sheet == null || sheet.IsEmpty) return false;
        string text = sheet.Text;
        if (text.Length <= Markdown.MaxFormatted) return true;
        int breaks = text.AsSpan().Count("\r\n");
        return text.Length - breaks <= Markdown.MaxFormatted;
    }

    private bool IsStale => _renderedText == null || !string.Equals(_renderedText, Editor.Text, StringComparison.Ordinal);

    /// <summary>Shows the note formatted, where the editor was; the keyboard follows if the editor had it.</summary>
    private void ShowReader()
    {
        if (!CanRead(_view.ActiveSheet))
        {
            ShowEditor();
            return;
        }
        if (_reading)
        {
            if (IsStale) RenderWhenShown();
            return;
        }
        var top = EditorTopLine();
        _reading = true;
        _swapping = true;
        try
        {
            bool hadKeyboard = Editor.IsKeyboardFocusWithin;
            ReaderScroller.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Hidden; // still laid out: back in an instant, scrolled as it was
            if (hadKeyboard)
            {
                Reader.Focus();
                Keyboard.Focus(Reader);
            }
            // Coming back to the panel (after another app had the keyboard) puts it here, not on the hidden editor.
            if (FocusManager.GetFocusScope(Editor) is { } scope && ReferenceEquals(FocusManager.GetFocusedElement(scope), Editor))
                FocusManager.SetFocusedElement(scope, Reader);
        }
        finally
        {
            _swapping = false;
        }
        Render(keepPlace: false);
        if (top is { } t && IsVisible) Reader.ScrollRowTo(Reader.RowOfLine(t.Offset), TranslatePoint(new Point(0, t.Y), Reader).Y);
    }

    /// <summary>Shows the editor; the keyboard follows if the formatted note had it. The rows go (they're made again next time).</summary>
    private void ShowEditor()
    {
        if (!_reading)
        {
            if (Editor.Visibility != Visibility.Visible) Editor.Visibility = Visibility.Visible;
            return;
        }
        _reading = false;
        _swapping = true;
        try
        {
            bool hadKeyboard = ReaderScroller.IsKeyboardFocusWithin;
            Editor.Visibility = Visibility.Visible;
            ReaderScroller.Visibility = Visibility.Collapsed;
            Reader.SetRows([], keepPlace: false);
            _renderedText = null;
            if (hadKeyboard)
            {
                Editor.Focus();
                Keyboard.Focus(Editor);
            }
        }
        finally
        {
            _swapping = false;
        }
    }

    /// <summary>
    /// Reads the note into rows: once each time it's shown, and when it changes while shown (a sync, a tick). Only the
    /// rows in view are laid out (<see cref="NoteReader"/>).
    /// </summary>
    private void Render(bool keepPlace)
    {
        int focusedBox = Reader.FocusedBox();
        string text = Editor.Text;
        Reader.SetRows(NoteRows.Build(Markdown.Parse(text)), keepPlace);
        _renderedText = text;
        if (focusedBox < 0) return;
        Reader.UpdateLayout();
        if (Reader.ViewOf(focusedBox)?.Box is { } box) box.Focus();
        else Reader.Focus();
    }

    /// <summary>The note changed while formatted: read again once things settle (once, however many changes).</summary>
    private void RenderWhenShown()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _renderQueued = false;
            if (!_reading || !IsStale) return;
            if (!CanRead(_view.ActiveSheet)) ShowEditor();
            else Render(keepPlace: true);
        });
    }

    private void FormatChanged()
    {
        if (!_ws.Settings.FormatNotes) ShowEditor();
        else if (!_reading && !Editor.IsKeyboardFocusWithin) ShowReader();
    }

    /// <summary>The editor lost the keyboard (a click or Tab away, the panel folding): the note shows formatted.</summary>
    private void Editor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_swapping || _reading || Editor.ContextMenu?.IsOpen == true) return;
        // Not while it's only gone to the editor's own menu: checked again once the focus has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_reading || Editor.IsKeyboardFocusWithin || Editor.ContextMenu?.IsOpen == true) return;
            if (CanRead(_view.ActiveSheet)) ShowReader();
        });
    }

    // ---- Reading: back to writing ----

    /// <summary>
    /// Opens the editor with the caret at <paramref name="offset"/> (the start of a line of the text), scrolled so
    /// that line stays where it was on screen (<paramref name="y"/>, in this view's coordinates).
    /// </summary>
    private void EditAt(int offset, double? y)
    {
        offset = Math.Clamp(offset, 0, Editor.Text.Length);
        ShowEditor();
        Editor.Select(offset, 0);
        if (y is double want && Editor.IsVisible)
        {
            // Lines out of view are only estimated until they're laid out: another look or two settles it.
            for (int pass = 0; pass < 3; pass++)
            {
                Editor.UpdateLayout();
                var rect = Editor.GetRectFromCharacterIndex(offset);
                if (rect.IsEmpty) break;
                double top = Editor.TranslatePoint(new Point(0, rect.Top), this).Y;
                double to = Math.Max(0, Editor.VerticalOffset + top - want);
                if (Math.Abs(to - Editor.VerticalOffset) < 0.5) break;
                Editor.ScrollToVerticalOffset(to);
            }
        }
        Editor.Focus();
        Keyboard.Focus(Editor);
    }

    /// <summary>A keystroke on the formatted note: the editor, its caret where it was.</summary>
    private void Edit() => FocusEditor();

    /// <summary>The line at the top of the editor: where it starts in the text, and where that is on screen.</summary>
    private (int Offset, double Y)? EditorTopLine()
    {
        if (!Editor.IsVisible || Editor.Text.Length == 0) return null;
        int visual = Editor.GetFirstVisibleLineIndex();
        if (visual < 0) return null;
        int index = Editor.GetCharacterIndexFromLineIndex(visual);
        if (index < 0) return null;
        string text = Editor.Text;
        int start = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        var rect = Editor.GetRectFromCharacterIndex(start);
        if (rect.IsEmpty) rect = Editor.GetRectFromCharacterIndex(index);
        if (rect.IsEmpty) return null;
        return (start, Editor.TranslatePoint(new Point(0, rect.Top), this).Y);
    }

    private static bool Inside<T>(object? source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is T) return true;
        }
        return false;
    }

    private void Reader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A checkbox ticks; anything else is a click to write (unless the pointer moves away before it's let go).
        _down = Inside<CheckBox>(e.OriginalSource) ? null : (e.GetPosition(Reader), e.ClickCount);
    }

    private void Reader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_down is not { } down || !_reading) return;
        _down = null;
        var up = e.GetPosition(Reader);
        if (down.Clicks > 1
            || Math.Abs(up.X - down.At.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(up.Y - down.At.Y) > SystemParameters.MinimumVerticalDragDistance)
            return;
        e.Handled = true;
        ClickToEdit(down.At);
    }

    /// <summary>A click at <paramref name="at"/> (the formatted note's coordinates): the editor, the caret at the start of that line.</summary>
    internal void ClickToEdit(Point at)
    {
        if (!_reading) return;
        int row = Reader.RowAt(at);
        if (row < 0)
        {
            FocusEditor();
            return;
        }
        double? top = Reader.TextTopOf(row);
        EditAt(Reader.Rows[row].Line, top is double y ? Reader.TranslatePoint(new Point(0, y), this).Y : null);
    }

    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_menuAt is { } at) ClickToEdit(at);
        else Edit();
    }

    private void Reader_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Space on a checkbox ticks it; control characters (Ctrl+letter) aren't typing.
        if (Inside<CheckBox>(e.OriginalSource) || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        TypeFromReader(e.Text);
    }

    /// <summary>A character typed on the formatted note: the editor opens and it's typed there.</summary>
    internal void TypeFromReader(string text)
    {
        Edit();
        Insert(text);
    }

    private void Reader_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ReaderKey(e.Key, Keyboard.Modifiers, Inside<CheckBox>(e.OriginalSource))) e.Handled = true;
    }

    /// <summary>A key on the formatted note (or a checkbox in it): true when it was one of Notes' own.</summary>
    internal bool ReaderKey(Key key, ModifierKeys mods, bool onBox = false)
    {
        switch (key)
        {
            case Key.F2 when mods == ModifierKeys.None:
                Edit();
                return true;

            case Key.Enter when mods == ModifierKeys.None && !onBox:
                Edit();
                if (!ContinueList()) Insert(Environment.NewLine);
                return true;

            case Key.Z when mods == ModifierKeys.Control:
                Edit();
                if (Editor.CanUndo) Editor.Undo();
                return true;

            case Key.Y when mods == ModifierKeys.Control:
                Edit();
                if (Editor.CanRedo) Editor.Redo();
                return true;

            case Key.V when mods == ModifierKeys.Control:
            case Key.Insert when mods == ModifierKeys.Shift:
                if (!PasteImage())
                {
                    Edit();
                    Editor.Paste();
                }
                return true;

            case Key.N when mods == ModifierKeys.Control:
                AddSheet();
                return true;

            case Key.Tab when mods == ModifierKeys.Control:
            case Key.PageDown when mods == ModifierKeys.Control:
                CycleSheet(1);
                return true;

            case Key.Tab when mods == (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.PageUp when mods == ModifierKeys.Control:
                CycleSheet(-1);
                return true;
        }
        return false;
    }

    /// <summary>
    /// A checkbox clicked: exactly that "[ ]" becomes "[x]" (or back) in the text, as one edit in the editor (so Undo,
    /// saving and the sync clocks all take it as typing). The caret stays where it was.
    /// </summary>
    private void Tick(NoteRow row, CheckBox box)
    {
        if (row.Item?.At is not int at) return;
        string text = Editor.Text;
        if (!_reading || at < 1 || at + 1 >= text.Length || text[at - 1] != '[' || text[at + 1] != ']' || text[at] is not (' ' or 'x' or 'X')
            || !string.Equals(text, _renderedText, StringComparison.Ordinal))
        {
            Render(keepPlace: true); // the note changed under it: show it as it is now
            return;
        }
        bool ticked = text[at] != ' ';
        if (box.IsChecked == ticked) return; // already as the text says
        int start = Editor.SelectionStart, length = Editor.SelectionLength;
        _ticking = true;
        try
        {
            Editor.Select(at, 1);
            Editor.SelectedText = ticked ? " " : "x";
            Editor.Select(start, length);
        }
        finally
        {
            _ticking = false;
        }
        RenderWhenShown(); // once this click is done: the box it came from goes
    }

    // ---- Snippets ----

    /// <summary>The strip's height (and its thumbnails'), in DIPs.</summary>
    internal const double ThumbHeight = 52;

    /// <summary>Ctrl+V (or Paste) with an image on the clipboard: true when it was one, kept or not.</summary>
    internal bool PasteImage() => _snippets != null && Paste(ReadClipboard());

    /// <summary>A paste in Notes: an image goes on the sheet as a snippet (true); anything else is left to the text box (false).</summary>
    internal bool Paste(IDataObject? data)
    {
        if (_snippets == null) return false;
        var found = SnippetImage.FromPaste(data);
        if (!found.IsImage) return false;
        Keep(found, fromTasks: false);
        return true;
    }

    /// <summary>
    /// Keeps pasted images on the sheet being written on (on blank paper, a new sheet). Pasted here they just
    /// appear; pasted on Tasks (which comes here to show them) a toast says where they went, with Undo. An
    /// image that can't be kept says why.
    /// </summary>
    internal void Keep(PastedImages found, bool fromTasks)
    {
        if (_snippets is not { } store) return;
        string? problem = Explain(found.Problem);
        var added = new List<Snippet>();
        if (found.Images.Count > 0)
        {
            var sheet = _view.ActiveSheet ?? _ws.AddSheet(_view.NewItemSpace);
            foreach (var png in found.Images)
            {
                var (snippet, refusal) = store.Add(sheet.Id, png, DateTimeOffset.Now);
                if (refusal == SnippetRefusal.Held)
                {
                    // The room is held by snippets an Undo could still put back: the paste is the newer
                    // action, so that Undo lapses (its files go) and the paste has the room, as with any toast.
                    _host?.EndUndo();
                    (snippet, refusal) = store.Add(sheet.Id, png, DateTimeOffset.Now);
                }
                if (snippet == null)
                {
                    problem = Explain(refusal);
                    break;
                }
                if (png.Thumb != null) _thumbs[snippet.Id] = png.Thumb;
                added.Add(snippet);
            }
            ShowSnippets();
        }
        if (added.Count > 0)
        {
            RevealSnippet(added[^1]);
            string what = added.Count == 1 ? "Snippet" : $"{added.Count} snippets";
            _host?.Announce($"{what} added");
            if (problem == null && fromTasks)
            {
                // Undo is the paste undone: nothing to bring back, so the files go then and there.
                _host?.ShowToast($"{what} added to Notes", () =>
                {
                    store.Detach(added);
                    Forget(added);
                });
                return;
            }
        }
        if (problem != null) _host?.ShowToast(problem);
    }

    private static string? Explain(PasteProblem problem) => problem switch
    {
        PasteProblem.TooBig => "That image is too big to paste",
        PasteProblem.Unreadable => "Couldn't read that image",
        PasteProblem.NotOnThisPc => "Only images on this PC's own drives can be pasted",
        _ => null,
    };

    private static string? Explain(SnippetRefusal refusal) => refusal switch
    {
        SnippetRefusal.Full => $"Notes keeps up to {SnippetStore.MaxCount} snippets: discard one to make room",
        SnippetRefusal.NoRoom => $"Notes keeps up to {SnippetStore.MaxTotalBytes / (1024 * 1024)} MB of snippets: discard one to make room",
        SnippetRefusal.CantSave => "Couldn't keep that image",
        SnippetRefusal.Held => $"Notes keeps up to {SnippetStore.MaxCount} snippets: discard one to make room",
        _ => null,
    };

    /// <summary>The strip shows the sheet's snippets, oldest first; with none it isn't there at all.</summary>
    private void ShowSnippets()
    {
        if (_snippets == null) return;
        var list = _snippets.Of(_view.ActiveSheet?.Id);
        SnippetStrip.Children.Clear();
        SnippetScroller.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < list.Count; i++) SnippetStrip.Children.Add(Cell(list[i], i, list.Count));
    }

    /// <summary>A thumbnail (click to view, right-click for a menu) with its × in the corner.</summary>
    private Grid Cell(Snippet snippet, int index, int count)
    {
        string time = snippet.PastedAt;
        var thumb = new Button
        {
            Style = (Style)FindResource("SnippetThumb"),
            Width = Math.Clamp(ThumbHeight * snippet.Width / Math.Max(1, snippet.Height), 40, 120),
            ToolTip = $"Pasted {time} · click to view",
            ContextMenu = (ContextMenu)FindResource("SnippetMenu"),
            Tag = snippet,
        };
        if (Thumb(snippet) is { } image)
        {
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            brush.Freeze();
            thumb.Background = brush;
        }
        else
        {
            thumb.SetResourceReference(BackgroundProperty, "SubtleFillBrush"); // its file is damaged or gone
        }
        AutomationProperties.SetName(thumb, count == 1 ? $"Snippet, pasted {time}" : $"Snippet {index + 1} of {count}, pasted {time}");
        AutomationProperties.SetHelpText(thumb, "Enter opens it full size, Delete discards it");
        thumb.Click += (_, _) => View(snippet);
        // A click views it and leaves the focus where it was (the text, usually), as before the strip took keys.
        thumb.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (thumb.IsKeyboardFocused) return;
            thumb.Focusable = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => thumb.Focusable = true);
        };
        thumb.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            int at = SnippetStrip.Children.IndexOf((UIElement)thumb.Parent);
            Discard(snippet);
            // The focus stays in the strip, on the next one along (or the one before), else goes to the text.
            if (SnippetStrip.Children.Count > 0)
                (SnippetStrip.Children[Math.Min(at, SnippetStrip.Children.Count - 1)] as Grid)?.Children.OfType<Button>().FirstOrDefault()?.Focus();
            else FocusDefault();
        };

        // Hidden until the pointer is over the thumbnail, but always there for screen readers.
        var discard = new Button
        {
            Style = (Style)FindResource("SnippetDiscard"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 3, 0),
            ToolTip = "Discard",
            Opacity = 0,
            IsHitTestVisible = false,
            Tag = snippet,
        };
        AutomationProperties.SetName(discard, "Discard snippet");
        discard.Click += (_, _) => Discard(snippet);

        var cell = new Grid { Margin = new Thickness(0, 0, 8, 0), Tag = snippet, Children = { thumb, discard } };
        cell.MouseEnter += (_, _) => ShowDiscard(discard, true);
        cell.MouseLeave += (_, _) => ShowDiscard(discard, false);
        return cell;
    }

    private static void ShowDiscard(Button discard, bool show)
    {
        discard.IsHitTestVisible = show;
        Anim.Fade(discard, show ? 1 : 0, show ? 120 : 90);
    }

    /// <summary>A snippet's thumbnail: made when it was pasted, otherwise read from its file (and kept while it's on show).</summary>
    private BitmapSource? Thumb(Snippet snippet)
    {
        if (_thumbs.TryGetValue(snippet.Id, out var thumb)) return thumb;
        if (_snippets == null || SnippetImage.Load(_snippets.PathOf(snippet)) is not { } loaded) return null;
        _thumbs[snippet.Id] = loaded;
        return loaded;
    }

    /// <summary>Folded away: only the thumbnails on show stay in memory.</summary>
    private void KeepThumbsOf(string? sheetId)
    {
        var keep = (_snippets?.Of(sheetId) ?? []).Select(s => s.Id).ToHashSet();
        foreach (string id in _thumbs.Keys.Where(id => !keep.Contains(id)).ToList()) _thumbs.Remove(id);
    }

    private void RevealSnippet(Snippet snippet) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            SnippetStrip.Children.OfType<FrameworkElement>().FirstOrDefault(c => Equals(c.Tag, snippet))?.BringIntoView());

    private void View(Snippet snippet)
    {
        if (_snippets != null) _host?.ViewSnippet(_snippets.PathOf(snippet), snippet);
    }

    /// <summary>× on a snippet: off the sheet at once; its file goes once Undo lapses.</summary>
    internal void Discard(Snippet snippet)
    {
        if (_snippets is not { } store) return;
        store.Detach([snippet]);
        _host?.Announce("Snippet discarded");
        _host?.ShowToast("Snippet discarded", () => store.Attach([snippet]), () => Forget([snippet]));
    }

    private void Forget(IReadOnlyCollection<Snippet> snippets)
    {
        _snippets?.Forget(snippets);
        foreach (var s in snippets) _thumbs.Remove(s.Id);
    }

    private static Snippet? SnippetOf(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement { Tag: Snippet s } ? s : null;

    private void ViewSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetOf(sender) is { } s) View(s);
    }

    private void DiscardSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetOf(sender) is { } s) Discard(s);
    }

    /// <summary>The mouse wheel scrolls the strip sideways (it has nothing to scroll up and down).</summary>
    private void SnippetScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SnippetScroller.ScrollableWidth <= 0) return;
        SnippetScroller.ScrollToHorizontalOffset(SnippetScroller.HorizontalOffset - e.Delta * 0.4);
        e.Handled = true;
    }
}
