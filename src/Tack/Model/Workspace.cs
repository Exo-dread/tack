using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Tack.Model;

/// <summary>
/// Everything the user owns: tasks, the one task in focus ("Now"), note sheets and settings.
/// All mutations go through here so persistence and the orb stay in sync.
/// </summary>
public sealed class Workspace : Observable
{
    private TaskItem? _now;
    private NoteSheet _activeSheet;

    public Workspace(Settings settings)
    {
        Settings = settings;
        _activeSheet = new NoteSheet();
        Sheets.Add(_activeSheet);

        Open.CollectionChanged += OnTasksCollectionChanged;
        Done.CollectionChanged += OnTasksCollectionChanged;
        Sheets.CollectionChanged += OnSheetsCollectionChanged;
        Track(_activeSheet);
        Settings.PropertyChanged += (_, _) => MarkChanged();
    }

    public Settings Settings { get; }

    /// <summary>Open tasks in the user's order (excluding the task in focus).</summary>
    public ObservableCollection<TaskItem> Open { get; } = new();

    /// <summary>Completed tasks, most recent first.</summary>
    public ObservableCollection<TaskItem> Done { get; } = new();

    public ObservableCollection<NoteSheet> Sheets { get; } = new();

    /// <summary>The single task the user is working on right now, if any.</summary>
    public TaskItem? Now
    {
        get => _now;
        private set
        {
            if (_now != null) Untrack(_now);
            if (!Set(ref _now, value)) return;
            if (_now != null) Track(_now);
            OnPropertyChanged(nameof(HasNow));
            RaiseSummary();
            MarkChanged();
        }
    }

    public bool HasNow => _now != null;

    public NoteSheet ActiveSheet
    {
        get => _activeSheet;
        set
        {
            if (value == null || !Set(ref _activeSheet, value)) return;
            Settings.ActiveSheet = value.Id;
        }
    }

    // ---- Summary shown by the orb ----

    public int OpenCount => Open.Count + (_now is null ? 0 : 1);

    public int DoneToday => Done.Count(t => t.CompletedToday);

    public bool HasDone => Done.Count > 0;

    public bool HasOpen => OpenCount > 0;

    /// <summary>Share of today's work that is finished (0..1).</summary>
    public double Progress
    {
        get
        {
            int done = DoneToday, total = done + OpenCount;
            return total == 0 ? 0 : (double)done / total;
        }
    }

    /// <summary>What the user should be doing: the focus task, otherwise the top of the list.</summary>
    public TaskItem? FocusTask => _now ?? Open.FirstOrDefault();

    /// <summary>Raised whenever anything that is saved to disk changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when counts, progress or the focus task change.</summary>
    public event EventHandler? SummaryChanged;

    // ---- Task operations ----

    public TaskItem AddTask(string text, bool asNow = false)
    {
        var task = new TaskItem(text.Trim());
        if (asNow)
        {
            if (_now != null) Open.Insert(0, _now);
            Now = task;
        }
        else
        {
            Open.Insert(0, task);
        }
        return task;
    }

    public void Complete(TaskItem task)
    {
        task.IsChecking = false;
        if (task == _now) Now = null;
        else Open.Remove(task);
        task.CompletedAt = DateTimeOffset.Now;
        Done.Insert(0, task);
    }

    public void Reopen(TaskItem task)
    {
        if (!Done.Remove(task)) return;
        task.CompletedAt = null;
        Open.Insert(0, task);
    }

    /// <summary>Puts a task in focus. The previous focus task takes its place in the list.</summary>
    public void SetNow(TaskItem task)
    {
        if (task == _now) return;
        int index = Open.IndexOf(task);
        if (index < 0) return;
        var previous = _now;
        Open.RemoveAt(index);
        if (previous != null) Open.Insert(index, previous);
        Now = task;
    }

    public void ClearNow()
    {
        if (_now == null) return;
        var task = _now;
        Now = null;
        Open.Insert(0, task);
    }

    /// <summary>Deletes a task and returns an action that puts it back exactly where it was.</summary>
    public Action Delete(TaskItem task)
    {
        if (task == _now)
        {
            Now = null;
            return () => { if (_now != null) Open.Insert(0, _now); Now = task; };
        }

        var list = task.IsDone ? Done : Open;
        int index = list.IndexOf(task);
        if (index < 0) return () => { };
        list.RemoveAt(index);
        return () => list.Insert(Math.Min(index, list.Count), task);
    }

    public Action ClearDone()
    {
        var removed = Done.ToList();
        Done.Clear();
        return () => { foreach (var t in removed) Done.Add(t); };
    }

    public void MoveOpen(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Open.Count || to >= Open.Count) return;
        Open.Move(from, to);
    }

    // ---- Notes ----

    public NoteSheet AddSheet()
    {
        var sheet = new NoteSheet();
        Sheets.Add(sheet);
        ActiveSheet = sheet;
        return sheet;
    }

    /// <summary>Removes a sheet (the last sheet is cleared instead). Returns an undo action.</summary>
    public Action DeleteSheet(NoteSheet sheet)
    {
        if (Sheets.Count == 1)
        {
            string text = sheet.Text;
            sheet.Text = "";
            return () => sheet.Text = text;
        }

        int index = Sheets.IndexOf(sheet);
        Sheets.RemoveAt(index);
        if (ActiveSheet == sheet) ActiveSheet = Sheets[Math.Min(index, Sheets.Count - 1)];
        return () =>
        {
            Sheets.Insert(Math.Min(index, Sheets.Count), sheet);
            ActiveSheet = sheet;
        };
    }

    // ---- Loading ----

    /// <summary>Replaces the contents with data read from disk.</summary>
    public void Populate(TaskItem? now, IEnumerable<TaskItem> open, IEnumerable<TaskItem> done, IReadOnlyList<NoteSheet> sheets)
    {
        Open.Clear();
        Done.Clear();
        foreach (var t in open) Open.Add(t);
        foreach (var t in done.OrderByDescending(t => t.CompletedAt)) Done.Add(t);
        Now = now;

        if (sheets.Count > 0)
        {
            Sheets.Clear();
            foreach (var s in sheets) Sheets.Add(s);
            ActiveSheet = Sheets.FirstOrDefault(s => s.Id == Settings.ActiveSheet) ?? Sheets[0];
        }
        RaiseSummary();
    }

    /// <summary>Call at midnight: "done today" resets.</summary>
    public void RefreshDay() => RaiseSummary();

    // ---- Change tracking ----

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (TaskItem t in e.OldItems) Untrack(t);
        if (e.NewItems != null) foreach (TaskItem t in e.NewItems) Track(t);
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var t in (IEnumerable<TaskItem>)sender!) Track(t);
        RaiseSummary();
        MarkChanged();
    }

    private void OnSheetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (NoteSheet s in e.OldItems) s.PropertyChanged -= OnSheetPropertyChanged;
        if (e.NewItems != null) foreach (NoteSheet s in e.NewItems) Track(s);
        MarkChanged();
    }

    private void Track(TaskItem t)
    {
        t.PropertyChanged -= OnTaskPropertyChanged;
        t.PropertyChanged += OnTaskPropertyChanged;
    }

    private void Untrack(TaskItem t) => t.PropertyChanged -= OnTaskPropertyChanged;

    private void Track(NoteSheet s)
    {
        s.PropertyChanged -= OnSheetPropertyChanged;
        s.PropertyChanged += OnSheetPropertyChanged;
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskItem.Text) or nameof(TaskItem.CompletedAt))
        {
            if (sender == FocusTask) RaiseSummary();
            MarkChanged();
        }
    }

    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteSheet.Text)) MarkChanged();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(DoneToday));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(FocusTask));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(HasOpen));
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
