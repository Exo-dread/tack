using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Model;
using Tack.Sync;
using Tack.Windows;

namespace Tack.Views;

/// <summary>
/// Tasks: one task in focus, a quick-add box, the list, and a folded "Done" section.
/// Everything is editable in place; there are no dialogs. It shows the space on show
/// (<see cref="SpaceView"/>): with no spaces, everything, as it always has.
/// </summary>
public partial class TasksView : UserControl
{
    /// <summary>Grace period after ticking a task, during which a second click undoes it.</summary>
    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(650);

    private static readonly Regex ListMarker = new(@"^\s*(?:[-*+•·◦▪–]\s+|\d{1,3}[.)]\s+|\[[ xX]?\]\s*)", RegexOptions.Compiled);

    private readonly Dictionary<TaskItem, DispatcherTimer> _checking = new();
    private readonly HashSet<FrameworkElement> _folding = new(); // rows folding away right now
    private Workspace _ws = null!;
    private SpaceView _view = null!;
    private PanelWindow _panel = null!;
    private string? _editOriginal;
    private TaskItem? _dragItem;

    public TasksView()
    {
        InitializeComponent();
    }

    /// <summary>Raised whenever the list's natural height may have changed (rows added, removed, re-wrapped, folded).</summary>
    public event Action? ContentHeightChanged;

    /// <summary>Raised as a row starts folding away (ticked, deleted, moved to another space), with how long it takes (ms).</summary>
    public event Action<double>? RowFolding;

    /// <summary>
    /// How tall the page would like to be at <paramref name="width"/>, with nothing to scroll. A row that is
    /// folding away is already left out, so a panel that fits its list knows its final height from the
    /// first frame and shrinks once, in step with the row, instead of chasing it frame by frame.
    /// </summary>
    internal double FittedHeight(double width)
    {
        Measure(new Size(width, double.PositiveInfinity));
        double height = DesiredSize.Height;
        foreach (var row in _folding)
            if (row.IsVisible) height -= row.DesiredSize.Height;
        InvalidateMeasure(); // hand layout back to the window
        return height;
    }

    /// <summary>Folds a row away (its height to nothing, eased in), then runs <paramref name="completed"/>.</summary>
    private void Fold(FrameworkElement row, double ms, Action completed)
    {
        _folding.Add(row);
        RowFolding?.Invoke(ms);
        Anim.Collapse(row, ms, () =>
        {
            _folding.Remove(row);
            completed();
        });
    }

    internal void Initialize(Workspace ws, SpaceView view, PanelWindow panel)
    {
        _ws = ws;
        _view = view;
        _panel = panel;
        DataContext = view;
        DataObject.AddPastingHandler(AddBox, OnAddBoxPaste);

        ListStack.SizeChanged += (_, _) => ContentHeightChanged?.Invoke();
        NowCard.SizeChanged += (_, _) => ContentHeightChanged?.Invoke();
        NowCard.IsVisibleChanged += (_, _) => ContentHeightChanged?.Invoke();
    }

    public void FocusInput()
    {
        AddBox.Focus();
        Keyboard.Focus(AddBox);
    }

    private static TaskItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TaskItem;

    // ================= Quick add =================

    private void AddBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string text = AddBox.Text.Trim();
                if (text.Length > 0)
                {
                    bool asNow = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                    var task = _ws.AddTask(text, asNow, _view.NewItemSpace);
                    AddBox.Clear();
                    if (asNow) AnimateNowCard(); else AnimateIn(task);
                    Scroller.ScrollToTop();
                }
                e.Handled = true;
                break;

            case Key.Escape when AddBox.Text.Length > 0:
                AddBox.Clear();
                e.Handled = true;
                break;

            case Key.Down when _view.Open.Count > 0:
                FocusEditor(_view.Open[0], atEnd: true);
                e.Handled = true;
                break;

            case Key.Up when _view.Now != null:
                FocusTextBox(NowEditor, atEnd: true);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Pasting several lines adds each one as a task (bullets and numbering are stripped).</summary>
    private void OnAddBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => ListMarker.Replace(ListMarker.Replace(l, ""), "").Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count > DeviceFile.MaxPasteLines)
        {
            e.CancelCommand();
            _panel.ShowToast($"Paste up to {DeviceFile.MaxPasteLines:N0} lines at once");
            return;
        }
        // A line longer than any device reads from a sync file would stay on this PC alone.
        lines.RemoveAll(l => JsText.CleanTaskText(l).Length > DeviceFile.MaxTaskText);
        if (lines.Count <= 1)
        {
            if (text.Contains('\n'))
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, lines.FirstOrDefault() ?? "");
                e.DataObject = data;
            }
            return;
        }

        e.CancelCommand();
        var added = new List<TaskItem>();
        for (int i = lines.Count - 1; i >= 0; i--) added.Add(_ws.AddTask(lines[i], space: _view.NewItemSpace));
        Scroller.ScrollToTop();
        _panel.ShowToast($"Added {lines.Count} tasks", () => { foreach (var t in added) _ws.Delete(t); });
    }

    // ================= Completing =================

    // Checked/Unchecked (rather than Click) so keyboard, mouse and screen-reader toggles all work.
    private void Check_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton box || ItemOf(sender) is not { } task) return;
        if ((box.IsChecked == true) != task.IsChecking) ToggleCheck(task);
    }

    private void ToggleCheck(TaskItem task)
    {
        if (_checking.Remove(task, out var pending))
        {
            pending.Stop();
            task.IsChecking = false;
            if (task == _ws.Now) Anim.Fade(NowEditor, 1, 150);
            return;
        }

        task.IsChecking = true;
        if (task == _ws.Now) Anim.Fade(NowEditor, 0.45, 150);
        var timer = new DispatcherTimer { Interval = CheckDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_checking.Remove(task) || !task.IsChecking) return;
            if (Keyboard.FocusedElement is TextBox tb && tb.DataContext == task) FocusInput();
            var container = ContainerFor(task);
            if (container == null) _ws.Complete(task);
            else Fold(container, 200, () => _ws.Complete(task));
            Anim.Set(NowEditor, OpacityProperty, 1);
        };
        _checking[task] = timer;
        timer.Start();
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } task) return;
        _ws.Reopen(task);
        AnimateIn(task);
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        int n = _view.Done.Count;
        var undo = _view.ClearDone();
        _panel.ShowToast(n == 1 ? "Cleared 1 finished task" : $"Cleared {n} finished tasks", undo);
    }

    // ================= Focus ("Now") =================

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } task) MakeNow(task);
    }

    private void MakeNow(TaskItem task)
    {
        var previous = _ws.Now;
        _ws.SetNow(task);
        AnimateNowCard();
        if (previous != null) AnimateIn(previous);
    }

    private void Unfocus_Click(object sender, RoutedEventArgs e)
    {
        var task = _ws.Now;
        if (task == null) return;
        _ws.ClearNow();
        AnimateIn(task);
    }

    /// <summary>The line about the task in focus in another space: shows that space.</summary>
    private void NowLine_Click(object sender, RoutedEventArgs e)
    {
        if (_view.NowElsewhere is not { } now) return;
        _panel.ShowSpace(_view.SpaceOf(now));
        // The line goes with the switch: typing goes on in the add box, as after a click on the strip.
        _panel.FocusDefault();
    }

    // ================= Deleting =================

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } task) DeleteWithUndo(task);
    }

    private void DeleteWithUndo(TaskItem task, bool animate = true)
    {
        void Remove()
        {
            var undo = _ws.Delete(task);
            _panel.ShowToast("Task deleted", () =>
            {
                undo();
                if (task == _ws.Now) AnimateNowCard(); else AnimateIn(task);
            });
        }

        var container = animate ? ContainerFor(task) : null;
        if (container == null) Remove();
        else Fold(container, 180, Remove);
    }

    // ================= Context menu =================

    private static TaskItem? MenuItemTask(object sender) =>
        (((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as TaskItem;

    private void MenuFocus_Click(object sender, RoutedEventArgs e) { if (MenuItemTask(sender) is { } t) MakeNow(t); }
    private void MenuDone_Click(object sender, RoutedEventArgs e) { if (MenuItemTask(sender) is { } t) ToggleCheck(t); }
    private void MenuDelete_Click(object sender, RoutedEventArgs e) { if (MenuItemTask(sender) is { } t) DeleteWithUndo(t); }

    private void MenuTop_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTask(sender) is { } t) MoveWithFlip(_view.Open.IndexOf(t), 0);
    }

    // ================= Spaces: Move to (the menus, and Ctrl+M) =================

    /// <summary>"Move to" shows once there are spaces, listing them all; the task's own is greyed out.</summary>
    private void TaskMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || (menu.PlacementTarget as FrameworkElement)?.DataContext is not TaskItem task) return;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag is "MoveTo") is not { } moveTo) return;
        moveTo.Visibility = _view.HasSpaces ? Visibility.Visible : Visibility.Collapsed;
        moveTo.Items.Clear();
        if (_view.HasSpaces) FillMoveMenu(moveTo.Items, task);
    }

    private void FillMoveMenu(ItemCollection items, TaskItem task) =>
        SpaceMenu.Fill(items, _view.Tabs, _view.SpaceOf(task), space => MoveToSpace(task, space));

    /// <summary>Ctrl+M: the same list of spaces, under the task.</summary>
    private void OpenMoveMenu(TaskItem task, TextBox editor)
    {
        var menu = new ContextMenu { PlacementTarget = editor, Placement = PlacementMode.Bottom };
        System.Windows.Automation.AutomationProperties.SetName(menu, "Move to");
        FillMoveMenu(menu.Items, task);
        menu.Opened += (_, _) => menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsEnabled)?.Focus();
        menu.IsOpen = true;
    }

    /// <summary>Puts a task in another space; only its space changes. Undo puts it back.</summary>
    private void MoveToSpace(TaskItem task, string space)
    {
        string before = task.Space.Length > 0 ? task.Space : SpaceField.Main;
        bool leaves = !_view.IsAll && space != _view.Shown && _view.Open.Contains(task);
        void Move()
        {
            _ws.MoveTaskToSpace(task.Id, space);
            // Out of the list on show (anywhere but All): typing goes on in the add box.
            if (!_view.IsAll) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusInput);
            _panel.Announce($"Moved to {_view.NameOf(space)}");
            _panel.ShowToast($"Moved to {SpaceView.ToastName(_view.NameOf(space))}", () =>
            {
                _ws.MoveTaskToSpace(task.Id, before);
                if (task == _ws.Now) AnimateNowCard(); else AnimateIn(task);
            });
        }
        if (leaves && ContainerFor(task) is { } container) Fold(container, 180, Move);
        else Move();
    }

    // ================= Editing in place =================

    private void Editor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _editOriginal = ((TextBox)sender).Text;

    private void Editor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ItemOf(sender) is not { } task || task.IsChecking) return;
        string trimmed = task.Text.Trim();
        if (trimmed.Length > 0)
        {
            if (trimmed != task.Text) task.Text = trimmed;
            return;
        }
        // Emptied out: treat as a delete, but keep the old text for Undo.
        task.Text = string.IsNullOrWhiteSpace(_editOriginal) ? "Untitled task" : _editOriginal!;
        if (_ws.Open.Contains(task) || _ws.Now == task) DeleteWithUndo(task, animate: false);
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || ItemOf(sender) is not { } task) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool isNow = task == _ws.Now;

        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.Control:
                if (!isNow)
                {
                    MakeNow(task);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusTextBox(NowEditor, atEnd: true));
                }
                e.Handled = true;
                break;

            case Key.Enter:
                FocusInput();
                e.Handled = true;
                break;

            case Key.Escape:
                if (_editOriginal != null) tb.Text = _editOriginal;
                FocusInput();
                e.Handled = true;
                break;

            case Key.D when mods == ModifierKeys.Control:
                ToggleCheck(task);
                e.Handled = true;
                break;

            case Key.M when mods == ModifierKeys.Control && _view.HasSpaces:
                OpenMoveMenu(task, tb);
                e.Handled = true;
                break;

            case Key.Up when mods == ModifierKeys.Alt && !isNow:
            case Key.Down when mods == ModifierKeys.Alt && !isNow:
                int from = _view.Open.IndexOf(task);
                int to = key == Key.Up ? from - 1 : from + 1;
                if (to >= 0 && to < _view.Open.Count)
                {
                    int caret = tb.CaretIndex;
                    MoveWithFlip(from, to);
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        if (EditorFor(task) is { } ed) { ed.Focus(); ed.CaretIndex = Math.Min(caret, ed.Text.Length); }
                    });
                }
                e.Handled = true;
                break;

            case Key.Up when mods == ModifierKeys.None && OnFirstLine(tb):
                if (isNow) break;
                int i = _view.Open.IndexOf(task);
                if (i > 0) FocusEditor(_view.Open[i - 1], atEnd: true);
                else FocusInput();
                e.Handled = true;
                break;

            case Key.Down when mods == ModifierKeys.None && OnLastLine(tb):
                if (isNow) { FocusInput(); e.Handled = true; break; }
                int j = _view.Open.IndexOf(task);
                if (j >= 0 && j < _view.Open.Count - 1) FocusEditor(_view.Open[j + 1], atEnd: true);
                e.Handled = true;
                break;

            case Key.Back when tb.Text.Length == 0 && !isNow:
                int k = _view.Open.IndexOf(task);
                _editOriginal = null;
                DeleteWithUndo(task, animate: false);
                if (k > 0 && k - 1 < _view.Open.Count) FocusEditor(_view.Open[k - 1], atEnd: true);
                else FocusInput();
                e.Handled = true;
                break;
        }
    }

    private static bool OnFirstLine(TextBox tb) => tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) <= 0;

    private static bool OnLastLine(TextBox tb)
    {
        int line = tb.GetLineIndexFromCharacterIndex(tb.CaretIndex);
        return line < 0 || line >= tb.LineCount - 1;
    }

    private void FocusEditor(TaskItem task, bool atEnd)
    {
        if (EditorFor(task) is { } editor) FocusTextBox(editor, atEnd);
    }

    private static void FocusTextBox(TextBox tb, bool atEnd)
    {
        tb.Focus();
        tb.CaretIndex = atEnd ? tb.Text.Length : 0;
    }

    // ================= Drag to reorder =================

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } task) return;
        _dragItem = task;
        SetLifted(task, true);
        // Capture on the view itself: rows get re-parented as they move, which would drop a row's capture.
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragItem == null || !IsMouseCaptured) return;

        double y = e.GetPosition(OpenList).Y;
        int from = _view.Open.IndexOf(_dragItem), to = from;
        for (int i = 0; i < _view.Open.Count; i++)
        {
            if (i == from || ContainerAt(i) is not { } c) continue;
            double mid = c.TranslatePoint(new Point(0, 0), OpenList).Y + c.ActualHeight / 2;
            if (i < from && y < mid) { to = i; break; }
            if (i > from && y > mid) to = i;
        }
        if (to != from) MoveWithFlip(from, to);

        double sy = e.GetPosition(Scroller).Y;
        if (sy < 18) Scroller.LineUp();
        else if (sy > Scroller.ActualHeight - 18) Scroller.LineDown();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragItem != null) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragItem == null) return;
        SetLifted(_dragItem, false);
        _dragItem = null;
    }

    private void SetLifted(TaskItem task, bool lifted)
    {
        if (ContainerFor(task) is not ContentPresenter cp || cp.ContentTemplate.FindName("Row", cp) is not Border row) return;
        if (lifted)
        {
            row.Background = (Brush)FindResource("AccentFaintBrush");
            row.BorderBrush = (Brush)FindResource("AccentSoftBrush");
        }
        else
        {
            row.ClearValue(Border.BackgroundProperty);
            row.ClearValue(Border.BorderBrushProperty);
        }
    }

    /// <summary>Moves a task and lets the other rows glide into their new places (FLIP animation).</summary>
    private void MoveWithFlip(int from, int to)
    {
        if (from < 0 || to < 0 || from == to) return;
        var before = new Dictionary<TaskItem, double>();
        foreach (var t in _view.Open)
            if (ContainerFor(t) is { } c) before[t] = c.TranslatePoint(new Point(0, 0), OpenList).Y;

        _view.MoveOpen(from, to);
        OpenList.UpdateLayout();

        foreach (var t in _view.Open)
        {
            if (ContainerFor(t) is not { } c || !before.TryGetValue(t, out double oldY)) continue;
            double dy = oldY - c.TranslatePoint(new Point(0, 0), OpenList).Y;
            if (Math.Abs(dy) < 0.5) continue;
            var shift = new TranslateTransform(0, dy);
            c.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, Anim.Ms(170)) { EasingFunction = Anim.EaseOut });
        }
    }

    // ================= Search: going to a task =================

    /// <summary>
    /// Search opened this task: scrolled to once the list on show has it, lit up for a moment, and
    /// the caret at the end of its text (a finished task's row has no text box: the caret goes to
    /// the add box, as when the panel opens).
    /// </summary>
    internal void Reveal(TaskItem task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ContainerFor(task) is not { } container)
            {
                FocusInput();
                return;
            }
            if (container == NowCard) LightUp(NowCard, () =>
            {
                NowCard.SetResourceReference(Border.BackgroundProperty, "AccentFaintBrush");
                NowCard.SetResourceReference(Border.BorderBrushProperty, "AccentSoftBrush");
            });
            else if (RowOf(container) is { } row) LightUp(row, () =>
            {
                row.ClearValue(Border.BackgroundProperty);
                row.ClearValue(Border.BorderBrushProperty);
            });
            if (!task.IsDone && EditorFor(task) is { } editor) FocusTextBox(editor, atEnd: true);
            else FocusInput();
            // The whole row, with a little room around it (the caret's own scrolling shows only its text box).
            container.UpdateLayout();
            container.BringIntoView(new Rect(0, -12, container.ActualWidth, container.ActualHeight + 24));
        });
    }

    /// <summary>How long a task search went to stays lit before it fades (in 260 ms) back to how it was.</summary>
    internal static readonly TimeSpan LitFor = TimeSpan.FromMilliseconds(1100);

    /// <summary>The row's own border in a list's container.</summary>
    private static Border? RowOf(FrameworkElement container) =>
        container is ContentPresenter cp && cp.ContentTemplate?.FindName("Row", cp) is Border row ? row : null;

    /// <summary>Lights a row up in the accent for a moment, then fades it back (<paramref name="restore"/> puts its own look back).</summary>
    private static void LightUp(Border row, Action restore)
    {
        var fill = ((SolidColorBrush)row.FindResource("AccentSoftBrush")).Clone();
        var edge = ((SolidColorBrush)row.FindResource("AccentBorderBrush")).Clone();
        row.Background = fill;
        row.BorderBrush = edge;
        var hold = new DispatcherTimer { Interval = LitFor };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            var fade = new DoubleAnimation(0, Anim.Ms(260)) { EasingFunction = Anim.EaseOut };
            fade.Completed += (_, _) =>
            {
                // Unless something else has changed its look meanwhile (a drag lifting it, say).
                if (ReferenceEquals(row.Background, fill) && ReferenceEquals(row.BorderBrush, edge)) restore();
            };
            fill.BeginAnimation(Brush.OpacityProperty, fade);
            edge.BeginAnimation(Brush.OpacityProperty, fade);
        };
        hold.Start();
    }

    // ================= Containers and entrance animations =================

    private FrameworkElement? ContainerAt(int index) =>
        OpenList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private FrameworkElement? ContainerFor(TaskItem task)
    {
        if (task == _ws.Now) return NowCard;
        var list = task.IsDone && !task.IsChecking ? DoneList : OpenList;
        return list.ItemContainerGenerator.ContainerFromItem(task) as FrameworkElement;
    }

    private TextBox? EditorFor(TaskItem task)
    {
        if (task == _ws.Now) return NowEditor;
        if (OpenList.ItemContainerGenerator.ContainerFromItem(task) is not ContentPresenter cp) return null;
        cp.ApplyTemplate();
        return cp.ContentTemplate?.FindName("Editor", cp) as TextBox;
    }

    private void AnimateIn(TaskItem task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ContainerFor(task) is not { } c) return;
            Anim.Fade(c, 1, 180, from: 0);
            Anim.Slide(c, 0, -8, 240);
        });
    }

    private void AnimateNowCard()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Anim.Fade(NowCard, 1, 200, from: 0);
            Anim.Slide(NowCard, 0, 10, 260);
        });
    }
}
