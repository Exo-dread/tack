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
}

/// <summary>
/// Notes: a stack of scratch-paper sheets. Plain text, saved as you type,
/// with a couple of quiet helpers (list continuation, timestamps, line-to-task).
/// It shows the sheets of the space on show (<see cref="SpaceView"/>); a space without
/// any shows blank paper until you write on it. An image pasted onto a sheet becomes a
/// snippet: a thumbnail above its text, kept on this PC only (<see cref="SnippetStore"/>).
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

    /// <summary>Snippets are on: an image pasted here (or on Tasks) is kept.</summary>
    internal bool TakesImages => _snippets != null;

    /// <summary>What's on the clipboard. Tests hand in their own instead of touching this PC's clipboard.</summary>
    internal Func<IDataObject?> ReadClipboard { get; set; } = () =>
    {
        try { return Clipboard.GetDataObject(); }
        catch (Exception) { return null; } // another app has it open: the paste goes on as it would have
    };

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
        ShowSnippets();

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
        AutomationProperties.SetHelpText(thumb, "Opens it full size");
        thumb.Click += (_, _) => View(snippet);

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
