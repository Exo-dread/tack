using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Tack.Sync;

namespace Tack.Model;

/// <summary>
/// Everything the user owns: tasks, the one task in focus ("Now"), note sheets and settings.
/// All mutations go through here so persistence and the orb stay in sync. Each one is also stamped
/// with the clock, step for step as Tack Focus stamps it (web/src/model/workspace.ts), so the two
/// apps produce the same records and can merge each other's changes.
/// </summary>
public sealed class Workspace : Observable, IImportTarget
{
    /// <summary>Deletions are remembered this long, so they can reach other devices, then forgotten.</summary>
    internal static readonly TimeSpan TombstoneAge = TimeSpan.FromDays(90);

    // Every task and sheet, deleted ones included, in the order they first appeared (as in Tack Focus).
    private readonly List<TaskItem> _allTasks = new();
    private readonly Dictionary<string, TaskItem> _tasks = new();
    private readonly List<NoteSheet> _allSheets = new();
    private readonly Dictionary<string, NoteSheet> _sheets = new();
    private readonly Func<DateTimeOffset> _wall;
    private readonly Func<string> _newId;
    private FocusData _focus = FocusData.None;
    private TaskItem? _now;
    private NoteSheet _activeSheet = null!;
    private bool _applying; // loading or merging: text that changes isn't a new edit

    /// <param name="wall">Local time, for dates (tests pass a fixed clock).</param>
    /// <param name="newId">Ids for new tasks and sheets (tests pass known ones).</param>
    internal Workspace(Settings settings, Hlc clock, WorkspaceData? data = null, Func<DateTimeOffset>? wall = null, Func<string>? newId = null)
    {
        Settings = settings;
        Clock = clock;
        _wall = wall ?? (() => DateTimeOffset.Now);
        _newId = newId ?? Ids.NewId;

        Open.CollectionChanged += OnCollectionChanged;
        Done.CollectionChanged += OnCollectionChanged;
        Sheets.CollectionChanged += (_, _) => MarkChanged();
        Load(data ?? WorkspaceData.Empty);
        Settings.PropertyChanged += (_, _) => MarkChanged();
    }

    public Settings Settings { get; }

    /// <summary>This device's clock.</summary>
    internal Hlc Clock { get; }

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
            if (!Set(ref _now, value)) return;
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

    /// <summary>Adds a task at the top of the list (or straight into focus).</summary>
    public TaskItem AddTask(string text, bool asNow = false)
    {
        string? top = TopOrder();
        string c = Clock.Now();
        var task = AddItem(new TaskData(_newId(), JsText.CleanTaskText(text), Today(), null,
            top != null ? OrderKey.Before(top) : OrderKey.Mid, false, TaskClocks.All(c)));
        if (asNow)
        {
            var previous = _now;
            if (previous != null)
            {
                SetOrder(previous, OrderKey.Before(task.Order));
                Open.Insert(0, previous);
            }
            SetFocus(task);
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
        if (task.IsDeleted || task.IsDone || !Owns(task)) return;
        string completed = Today();
        string stamp = Clock.Now(); // the completion is stamped before the focus change, as in Tack Focus
        if (task == _now) SetFocus(null);
        else Open.Remove(task);
        task.SetCompleted(completed);
        task.Clk = task.Clk with { Completed = stamp };
        InsertSorted(Done, task, CompareDone);
    }

    /// <summary>A finished task goes back to the top of the list.</summary>
    public void Reopen(TaskItem task)
    {
        if (task.IsDeleted || !task.IsDone || !Done.Contains(task)) return;
        string? top = TopOrder();
        Done.Remove(task);
        task.SetCompleted(null);
        Restamp(task, completed: true);
        SetOrder(task, top != null ? OrderKey.Before(top) : OrderKey.Mid);
        // Another device may have left it in focus; then it's the focus task again (as in Tack Focus).
        if (_focus.Id == task.Id && _now == null) Now = task;
        else Open.Insert(0, task);
    }

    /// <summary>Puts a task in focus. The previous focus task takes its place in the list.</summary>
    public void SetNow(TaskItem task)
    {
        if (task == _now) return;
        int index = Open.IndexOf(task);
        if (index < 0) return;
        var previous = _now;
        Open.RemoveAt(index);
        if (previous != null)
        {
            SetOrder(previous, OrderKey.At(Open.Select(t => t.Order).ToList(), index));
            InsertSorted(Open, previous, CompareOpen);
        }
        SetFocus(task);
    }

    /// <summary>Takes the focus task out of focus; it goes to the top of the list.</summary>
    public void ClearNow()
    {
        if (_now == null) return;
        var task = _now;
        string? top = TopOrder();
        SetOrder(task, top != null ? OrderKey.Before(top) : OrderKey.Mid);
        SetFocus(null);
        Open.Insert(0, task);
    }

    /// <summary>Deletes a task and returns an action that puts it back exactly where it was (and in focus, if it was).</summary>
    public Action Delete(TaskItem task)
    {
        if (task.IsDeleted || !Owns(task)) return () => { };
        bool wasNow = task == _now;
        Remove(task);
        Open.Remove(task);
        Done.Remove(task);

        return () =>
        {
            if (!Owns(task)) return; // forgotten since
            task.IsDeleted = false;
            Restamp(task, deleted: true);
            if (wasNow && !task.IsDone)
            {
                // Tack Focus puts the task back, then moves whatever took the focus meanwhile to the top.
                var current = _now;
                if (current != null && current != task)
                {
                    string top = Open.Count == 0 || CompareOpen(task, Open[0]) < 0 ? task.Order : Open[0].Order;
                    SetOrder(current, OrderKey.Before(top));
                }
                SetFocus(task);
                if (current != null && current != task) Open.Insert(0, current);
            }
            else
            {
                Place(task);
            }
        };
    }

    public Action ClearDone()
    {
        var removed = Done.ToList();
        if (removed.Count == 0) return () => { };
        foreach (var t in removed) Remove(t);
        Done.Clear();
        return () =>
        {
            foreach (var t in removed)
            {
                if (!Owns(t)) continue;
                t.IsDeleted = false;
                Restamp(t, deleted: true);
            }
            foreach (var t in removed) if (Owns(t)) Place(t);
        };
    }

    /// <summary>Moves an open task from one place in the list to another.</summary>
    public void MoveOpen(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Open.Count || to >= Open.Count) return;
        var task = Open[from];
        var others = Open.Where(t => t != task).ToList();
        SetOrder(task, OrderKey.At(others.Select(t => t.Order).ToList(), to));
        // Where the key sorts (the same as "to", unless two devices left equal keys behind).
        int target = others.Count(t => CompareOpen(t, task) < 0);
        if (target != from) Open.Move(from, target);
    }

    // ---- Notes ----

    public NoteSheet AddSheet()
    {
        var sheet = AddSheetAtEnd();
        ActiveSheet = sheet;
        return sheet;
    }

    /// <summary>Removes a sheet (the last sheet is cleared instead). Returns an undo action.</summary>
    public Action DeleteSheet(NoteSheet sheet)
    {
        if (sheet.IsDeleted || !Owns(sheet)) return () => { };
        if (Sheets.Count <= 1)
        {
            string text = sheet.Text;
            sheet.Text = "";
            return () => sheet.Text = text;
        }

        int index = Sheets.IndexOf(sheet);
        SetSheetDeleted(sheet, true);
        Sheets.RemoveAt(index);
        if (ActiveSheet == sheet) ActiveSheet = Sheets[Math.Min(index, Sheets.Count - 1)];
        return () =>
        {
            if (!Owns(sheet)) return;
            SetSheetDeleted(sheet, false);
            if (!Sheets.Contains(sheet)) InsertSorted(Sheets, sheet, CompareSheets);
            ActiveSheet = sheet;
        };
    }

    // ---- Housekeeping ----

    /// <summary>Call at midnight (and when the panel opens): "done today" resets, and very old deletions are forgotten.</summary>
    public void RefreshDay()
    {
        if (PurgeTombstones() > 0) MarkChanged();
        RaiseSummary();
    }

    /// <summary>Removes tombstones older than <see cref="TombstoneAge"/> for good. Returns how many.</summary>
    internal int PurgeTombstones()
    {
        double cutoff = WallMs() - TombstoneAge.TotalMilliseconds;
        int removed = 0;
        foreach (var t in _allTasks.Where(t => t.IsDeleted && Stamp.Time(t.Clk.Deleted) < cutoff).ToList())
        {
            t.PropertyChanged -= OnTaskPropertyChanged;
            _allTasks.Remove(t);
            _tasks.Remove(t.Id);
            removed++;
        }
        foreach (var s in _allSheets.Where(s => s.IsDeleted && Stamp.Time(s.Clk.Deleted) < cutoff).ToList())
        {
            s.PropertyChanged -= OnSheetPropertyChanged;
            _allSheets.Remove(s);
            _sheets.Remove(s.Id);
            removed++;
        }
        return removed;
    }

    // ---- Records: loading, merging, restoring ----

    /// <summary>Copies of every record (deleted ones included) and the focus register, e.g. for saving.</summary>
    internal WorkspaceData ToData() =>
        new(_allTasks.Select(t => t.ToData()).ToList(), _allSheets.Select(s => s.ToData()).ToList(), _focus);

    /// <summary>
    /// Merges records from elsewhere (a file, or another device) into the workspace. Newer edits win
    /// field by field; nothing newer is lost. Returns whether anything changed.
    /// </summary>
    /// <param name="base">The clock both sides last agreed on: notes rewritten on both sides since are kept twice.</param>
    internal bool MergeFrom(WorkspaceData data, string? @base = null)
    {
        bool changed = false;
        _applying = true;
        try
        {
            foreach (var remote in data.Tasks)
            {
                Clock.Observe(remote.Clk.Max());
                _tasks.TryGetValue(remote.Id, out var local);
                var (value, c) = Merge.Task(local?.ToData(), remote);
                if (!c) continue;
                changed = true;
                if (local != null) local.Apply(value);
                else AddItem(value);
            }
            foreach (var remote in data.Sheets)
            {
                Clock.Observe(remote.Clk.Max());
                _sheets.TryGetValue(remote.Id, out var local);
                var (value, c, conflict) = Merge.Sheet(local?.ToData(), remote, @base);
                if (c)
                {
                    changed = true;
                    if (local != null) local.Apply(value);
                    else AddItem(value);
                }
                if (conflict != null && !_sheets.ContainsKey(conflict.Id))
                {
                    changed = true;
                    AddItem(conflict);
                }
            }
            Clock.Observe(data.Focus.Clk);
            var (focus, fc) = Merge.Focus(_focus, data.Focus);
            if (fc)
            {
                _focus = focus;
                changed = true;
            }
        }
        finally
        {
            _applying = false;
        }
        if (changed)
        {
            Rebuild();
            MarkChanged();
        }
        return changed;
    }

    /// <summary>
    /// Imports a tack.json (from Tack Focus, another PC or a backup) into the workspace.
    /// Returns whether anything changed. Throws <see cref="TackFileException"/> for files that aren't Tack files.
    /// </summary>
    /// <param name="lastModified">When the file was last saved (ms since the epoch), to settle edits made in both places.</param>
    internal bool Import(JsonElement doc, long? lastModified)
    {
        var (data, @base) = TackFile.Read(doc, this, lastModified, WallMs());
        return MergeFrom(data, @base);
    }

    /// <summary>
    /// Makes the workspace look exactly like <paramref name="data"/> (a snapshot taken earlier), as fresh
    /// edits: records that differ are rewritten, records that didn't exist then are deleted. Because
    /// the edits are new, they win over the state they replace everywhere it has spread. (Undo for Import.)
    /// </summary>
    internal void Restore(WorkspaceData data)
    {
        var tasks = new Dictionary<string, TaskData>();
        foreach (var t in data.Tasks) tasks[t.Id] = t;
        var sheets = new Dictionary<string, SheetData>();
        foreach (var s in data.Sheets) sheets[s.Id] = s;

        foreach (string id in _allTasks.Select(t => t.Id).Concat(data.Tasks.Select(t => t.Id)).Distinct().ToList())
        {
            tasks.TryGetValue(id, out var target);
            if (!_tasks.TryGetValue(id, out var item))
            {
                if (target is { Deleted: false }) AddItem(target with { Clk = TaskClocks.All(Clock.Now()) });
                continue;
            }
            var current = item.ToData();
            var want = target ?? current with { Deleted = true };
            if (current.Text != want.Text && want.Text.Length > 0) SetText(item, want.Text);
            if (current.Completed != want.Completed)
            {
                item.SetCompleted(want.Completed);
                Restamp(item, completed: true);
            }
            if (current.Order != want.Order) SetOrder(item, want.Order);
            if (current.Deleted != want.Deleted)
            {
                item.IsDeleted = want.Deleted;
                Restamp(item, deleted: true);
            }
        }

        foreach (string id in _allSheets.Select(s => s.Id).Concat(data.Sheets.Select(s => s.Id)).Distinct().ToList())
        {
            sheets.TryGetValue(id, out var target);
            if (!_sheets.TryGetValue(id, out var item))
            {
                if (target is { Deleted: false }) AddItem(target with { Clk = SheetClocks.All(Clock.Now()) });
                continue;
            }
            var current = item.ToData();
            var want = target ?? current with { Deleted = true };
            if (current.Deleted && !want.Deleted) SetSheetDeleted(item, false);
            if (current.Text != want.Text && !want.Deleted && !item.IsDeleted) item.Text = want.Text;
            if (!current.Deleted && want.Deleted) SetSheetDeleted(item, true);
        }

        if (_focus.Id != data.Focus.Id) _focus = new FocusData(data.Focus.Id, Clock.Now());
        Rebuild();
        MarkChanged();
    }

    private void Load(WorkspaceData data)
    {
        string? remembered = Settings.ActiveSheet;
        _applying = true;
        try
        {
            foreach (var t in data.Tasks)
            {
                Clock.Observe(t.Clk.Max());
                if (_tasks.TryGetValue(t.Id, out var twin)) twin.Apply(Merge.Task(twin.ToData(), t).Value);
                else AddItem(t);
            }
            foreach (var s in data.Sheets)
            {
                Clock.Observe(s.Clk.Max());
                if (_sheets.TryGetValue(s.Id, out var twin)) twin.Apply(Merge.Sheet(twin.ToData(), s).Value);
                else AddItem(s);
            }
            Clock.Observe(data.Focus.Clk);
            _focus = data.Focus;
        }
        finally
        {
            _applying = false;
        }
        PurgeTombstones();
        Rebuild();
        ActiveSheet = Sheets.FirstOrDefault(s => s.Id == remembered) ?? Sheets[0];
    }

    /// <summary>
    /// Brings the lists in line with the records (after loading or merging): open tasks by key, the
    /// focus register's task as Now, finished tasks most recent first, sheets by key. There is always
    /// at least one sheet to write on.
    /// </summary>
    private void Rebuild()
    {
        var now = _focus.Id != null && _tasks.TryGetValue(_focus.Id, out var f) && !f.IsDeleted && !f.IsDone ? f : null;
        var open = _allTasks.Where(t => !t.IsDeleted && !t.IsDone && t != now).ToList();
        open.Sort(CompareOpen);
        var done = _allTasks.Where(t => !t.IsDeleted && t.IsDone).ToList();
        done.Sort(CompareDone);
        var sheets = _allSheets.Where(s => !s.IsDeleted).ToList();
        sheets.Sort(CompareSheets);

        int activeIndex = Math.Max(0, Sheets.IndexOf(_activeSheet));
        Reconcile(Open, open);
        Reconcile(Done, done);
        Now = now;
        Reconcile(Sheets, sheets);
        if (Sheets.Count == 0) AddSheetAtEnd();
        if (_activeSheet == null || !Sheets.Contains(_activeSheet)) ActiveSheet = Sheets[Math.Min(activeIndex, Sheets.Count - 1)];
        RaiseSummary();
    }

    // ---- Stamping (the order of Clock.Now() calls matches Tack Focus, so clocks come out identical) ----

    private void SetText(TaskItem task, string text)
    {
        _applying = true;
        try { task.Text = text; } finally { _applying = false; }
        Restamp(task, text: true);
    }

    private void SetOrder(TaskItem task, string order)
    {
        task.Order = order;
        Restamp(task, order: true);
    }

    private void Restamp(TaskItem task, bool text = false, bool completed = false, bool order = false, bool deleted = false)
    {
        var c = task.Clk;
        string stamp = Clock.Now();
        task.Clk = new TaskClocks(text ? stamp : c.Text, completed ? stamp : c.Completed, order ? stamp : c.Order, deleted ? stamp : c.Deleted);
        MarkChanged();
    }

    private void SetSheetDeleted(NoteSheet sheet, bool deleted)
    {
        sheet.IsDeleted = deleted;
        sheet.Clk = sheet.Clk with { Deleted = Clock.Now() };
        MarkChanged();
    }

    /// <summary>The focus register: one task id (or none) with its own clock.</summary>
    private void SetFocus(TaskItem? task)
    {
        _focus = new FocusData(task?.Id, Clock.Now());
        Now = task;
        MarkChanged();
    }

    /// <summary>removeTask in Tack Focus: leave focus (if the register points here), then a tombstone.</summary>
    private void Remove(TaskItem task)
    {
        if (task.IsDeleted) return;
        if (_focus.Id == task.Id) SetFocus(null);
        task.IsDeleted = true;
        Restamp(task, deleted: true);
    }

    /// <summary>Puts a task that came back into the list it belongs in, where its key or date says.</summary>
    private void Place(TaskItem task)
    {
        if (task.IsDeleted || Open.Contains(task) || Done.Contains(task) || task == _now) return;
        if (task.IsDone) InsertSorted(Done, task, CompareDone);
        else if (_focus.Id == task.Id && _now == null) Now = task;
        else InsertSorted(Open, task, CompareOpen);
    }

    private TaskItem AddItem(TaskData data)
    {
        var task = new TaskItem(data);
        _allTasks.Add(task);
        _tasks[task.Id] = task;
        task.PropertyChanged += OnTaskPropertyChanged;
        return task;
    }

    private NoteSheet AddItem(SheetData data)
    {
        var sheet = new NoteSheet(data);
        _allSheets.Add(sheet);
        _sheets[sheet.Id] = sheet;
        sheet.PropertyChanged += OnSheetPropertyChanged;
        return sheet;
    }

    private NoteSheet AddSheetAtEnd()
    {
        string? last = Sheets.Count > 0 ? Sheets[^1].Order : null;
        string c = Clock.Now();
        var sheet = AddItem(new SheetData(_newId(), "", Today(), last != null ? OrderKey.After(last) : OrderKey.Mid, false, SheetClocks.All(c)));
        Sheets.Add(sheet);
        return sheet;
    }

    /// <summary>Any task with this id, deleted ones included.</summary>
    internal TaskItem? TaskById(string id) => _tasks.GetValueOrDefault(id);

    internal NoteSheet? SheetById(string id) => _sheets.GetValueOrDefault(id);

    private bool Owns(TaskItem task) => _tasks.TryGetValue(task.Id, out var t) && t == task;

    private bool Owns(NoteSheet sheet) => _sheets.TryGetValue(sheet.Id, out var s) && s == sheet;

    private string? TopOrder() => Open.Count > 0 ? Open[0].Order : null;

    /// <summary>Local time now, to the millisecond, as dates are stored (isoLocal in Tack Focus).</summary>
    private string Today() => IsoTime.Format(IsoTime.ToMilliseconds(_wall()));

    private long WallMs() => _wall().ToUnixTimeMilliseconds();

    // ---- Orders (byOrder in Tack Focus: key, then id) ----

    private static int CompareOpen(TaskItem a, TaskItem b) => CompareKeys(a.Order, a.Id, b.Order, b.Id);

    private static int CompareDone(TaskItem a, TaskItem b)
    {
        int c = b.CompletedMs.CompareTo(a.CompletedMs);
        return c != 0 ? c : CompareKeys(a.Order, a.Id, b.Order, b.Id);
    }

    private static int CompareSheets(NoteSheet a, NoteSheet b) => CompareKeys(a.Order, a.Id, b.Order, b.Id);

    private static int CompareKeys(string ao, string aid, string bo, string bid)
    {
        int c = string.CompareOrdinal(ao, bo);
        return c != 0 ? c : string.CompareOrdinal(aid, bid);
    }

    private static void InsertSorted<T>(ObservableCollection<T> list, T item, Comparison<T> compare)
    {
        int i = 0;
        while (i < list.Count && compare(list[i], item) <= 0) i++;
        list.Insert(i, item);
    }

    /// <summary>Turns a list into <paramref name="desired"/> with as few moves as possible, so rows animate sensibly.</summary>
    private static void Reconcile<T>(ObservableCollection<T> list, List<T> desired) where T : class
    {
        var keep = new HashSet<T>(desired);
        for (int i = list.Count - 1; i >= 0; i--)
            if (!keep.Contains(list[i])) list.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], desired[i])) continue;
            int j = list.IndexOf(desired[i]);
            if (j >= 0) list.Move(j, i);
            else list.Insert(i, desired[i]);
        }
    }

    // ---- Change tracking ----

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseSummary();
        MarkChanged();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskItem.Text) || sender is not TaskItem task) return;
        if (!_applying) task.Clk = task.Clk with { Text = Clock.Now() };
        if (task == FocusTask) RaiseSummary();
        MarkChanged();
    }

    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteSheet.Text) || sender is not NoteSheet sheet) return;
        if (!_applying)
        {
            sheet.SetUpdated(Today());
            sheet.Clk = sheet.Clk with { Text = Clock.Now() };
        }
        MarkChanged();
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

    // ---- What reading a file needs to know (see TackFile) ----

    Hlc IImportTarget.Clock => Clock;

    string? IImportTarget.FocusId => _focus.Id;

    string? IImportTarget.FirstOpenOrder => TopOrder();

    string? IImportTarget.LastSheetOrder => Sheets.Count > 0 ? Sheets[^1].Order : null;

    TaskData? IImportTarget.FindTask(string id) => _tasks.TryGetValue(id, out var t) ? t.ToData() : null;

    SheetData? IImportTarget.FindSheet(string id) => _sheets.TryGetValue(id, out var s) ? s.ToData() : null;
}
