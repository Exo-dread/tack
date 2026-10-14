using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tack.Model;
using Tack.Windows;

namespace Tack.Views;

/// <summary>
/// Notes: a stack of scratch-paper sheets. Plain text, saved as you type,
/// with a couple of quiet helpers (list continuation, timestamps, line-to-task).
/// </summary>
public partial class NotesView : UserControl
{
    // "- ", "* ", "• ", "[ ] ", "[x] ", "1. " at the start of a line (with optional indentation).
    private static readonly Regex ListPrefix = new(@"^(\s*)(?:([-*•])|\[[ xX]\]|(\d{1,3})([.)]))\s+", RegexOptions.Compiled);

    private readonly Dictionary<NoteSheet, (int Caret, double Scroll)> _positions = new();
    private Workspace _ws = null!;
    private PanelWindow _panel = null!;
    private NoteSheet? _shown;

    public NotesView()
    {
        InitializeComponent();
    }

    internal void Initialize(Workspace ws, PanelWindow panel)
    {
        _ws = ws;
        _panel = panel;
        _shown = ws.ActiveSheet;
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
        _shown = _ws.ActiveSheet;
        SheetList.ScrollIntoView(_shown);

        // A sheet switch replaces the text wholesale; it must not become an undo step.
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
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
        _ws.AddSheet();
        FocusEditor();
    }

    private void DeleteSheet_Click(object sender, RoutedEventArgs e)
    {
        if ((((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext is not NoteSheet sheet) return;
        bool wasEmpty = sheet.IsEmpty;
        var undo = _ws.DeleteSheet(sheet);
        _positions.Remove(sheet);
        if (!wasEmpty) _panel.ShowToast("Sheet deleted", undo);
    }

    private void CycleSheet(int delta)
    {
        int n = _ws.Sheets.Count;
        if (n < 2) return;
        int i = _ws.Sheets.IndexOf(_ws.ActiveSheet);
        _ws.ActiveSheet = _ws.Sheets[((i + delta) % n + n) % n];
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
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return;

        var added = new List<TaskItem>();
        for (int i = lines.Count - 1; i >= 0; i--) added.Add(_ws.AddTask(lines[i]));
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
