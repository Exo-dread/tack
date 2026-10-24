using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tack.Model;
using Tack.Sync;
using Tack.Windows;

namespace Tack.Views;

/// <summary>
/// Notes: a stack of scratch-paper sheets. Plain text, saved as you type,
/// with a couple of quiet helpers (list continuation, timestamps, line-to-task).
/// It shows the sheets of the space on show (<see cref="SpaceView"/>); a space without
/// any shows blank paper until you write on it.
/// </summary>
public partial class NotesView : UserControl
{
    // "- ", "* ", "• ", "[ ] ", "[x] ", "1. " at the start of a line (with optional indentation).
    private static readonly Regex ListPrefix = new(@"^(\s*)(?:([-*•])|\[[ xX]\]|(\d{1,3})([.)]))\s+", RegexOptions.Compiled);

    private readonly Dictionary<NoteSheet, (int Caret, double Scroll)> _positions = new();
    private Workspace _ws = null!;
    private SpaceView _view = null!;
    private PanelWindow _panel = null!;
    private NoteSheet? _shown;

    public NotesView()
    {
        InitializeComponent();
    }

    internal void Initialize(Workspace ws, SpaceView view, PanelWindow panel)
    {
        _ws = ws;
        _view = view;
        _panel = panel;
        DataContext = view;
        _shown = view.ActiveSheet;
    }

    public void FocusEditor()
    {
        Editor.Focus();
        Keyboard.Focus(Editor);
    }

    // ---- Sheets ----

    private void SheetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ws == null) return;
        if (_shown != null) _positions[_shown] = (Editor.CaretIndex, Editor.VerticalOffset);
        _shown = _view.ActiveSheet;
        if (_shown != null) SheetList.ScrollIntoView(_shown);

        // The sheet just made from what was typed on blank paper: the text on the paper is its
        // text, and what was typed stays undoable (the history held nothing else: landing on the
        // blank paper cleared it).
        bool madeOnPaper = _makingFromPaper;

        // A sheet switch replaces the text wholesale; it must not become an undo step.
        if (!madeOnPaper)
        {
            Editor.IsUndoEnabled = false;
            Editor.IsUndoEnabled = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // With spaces, a switch can land on blank paper, where Undo bringing back the text it
            // replaced would make a sheet of it: clear again, now that the new text is in.
            if (_view.HasSpaces && !madeOnPaper)
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
            if (IsVisible) FocusEditor();
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
    /// and now: the editor is still inside its change, where switching to the new sheet (which
    /// resets the undo history) isn't allowed, so the sheet is made as soon as the change is done
    /// (before the next keystroke is handled).
    /// </summary>
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
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
        bool wasEmpty = sheet.IsEmpty;
        var undo = _ws.DeleteSheet(sheet);
        _positions.Remove(sheet);
        if (!wasEmpty) _panel.ShowToast("Sheet deleted", undo);
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
        if (IsVisible) FocusEditor();
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
}
