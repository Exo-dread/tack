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
    // Spaces, deleted ones included: ids in the order they first appeared, and the records.
    private readonly List<string> _spaceIds = new();
    private readonly Dictionary<string, SpaceData> _spaces = new();
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
        KeepDeletions = () => Settings.SyncFolder != null;

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

    /// <summary>
    /// Adds a task at the top of the list (or straight into focus), in <paramref name="space"/> if
    /// that's a live space other than Main (its space stamped with the task's other clocks), else in Main.
    /// </summary>
    public TaskItem AddTask(string text, bool asNow = false, string? space = null)
    {
        string? top = TopOrder();
        string c = Clock.Now();
        string where = RealSpace(space);
        var task = AddItem(new TaskData(_newId(), JsText.CleanTaskText(text), Today(), null,
            top != null ? OrderKey.Before(top) : OrderKey.Mid, false, where, TaskClocks.All(c) with { Space = where.Length > 0 ? c : Stamp.Zero }));
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
            SetOrderAt(previous, Open.ToList(), index);
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

    public Action ClearDone() => ClearDone(null);

    /// <summary>Clears finished tasks: all of them, or only <paramref name="only"/> (what a space shows). Returns an undo action.</summary>
    internal Action ClearDone(IReadOnlyCollection<TaskItem>? only)
    {
        var removed = only == null ? Done.ToList() : Done.Where(only.Contains).ToList();
        if (removed.Count == 0) return () => { };
        foreach (var t in removed) Remove(t);
        if (only == null) Done.Clear();
        else foreach (var t in removed) Done.Remove(t);
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
        SetOrderAt(task, others, to);
        // Where the key sorts: "to", now that neighbours with equal keys have made room.
        int target = others.Count(t => CompareOpen(t, task) < 0);
        if (target != from) Open.Move(from, target);
    }

    /// <summary>
    /// Gives an open task the place at <paramref name="index"/> in <paramref name="list"/> (some of the open
    /// tasks, in order, without it: one space's), so only tasks of that list make room where keys tie.
    /// </summary>
    internal void MoveOpenAmong(TaskItem task, IReadOnlyList<TaskItem> list, int index)
    {
        if (!Open.Contains(task) || index < 0 || index > list.Count) return;
        SetOrderAt(task, list, index);
        var sorted = Open.ToList();
        sorted.Sort(CompareOpen);
        Reconcile(Open, sorted);
    }

    // ---- Notes ----

    /// <summary>
    /// A new sheet after the last one, in <paramref name="space"/> as <see cref="AddTask"/> puts tasks
    /// in one, and the one being written on. It may start with <paramref name="text"/> (the first
    /// keystrokes on a space's blank paper).
    /// </summary>
    public NoteSheet AddSheet(string? space = null, string text = "")
    {
        var sheet = AddSheetAtEnd(space, text);
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

    // ---- Spaces ----

    /// <summary>Raised when a space is added, renamed, moved or deleted, or a task or sheet moves to another space.</summary>
    public event EventHandler? SpacesChanged;

    /// <summary>
    /// Live spaces in their order (key, then id), Main's own record included once it has one. A
    /// record with All's id is never a space (<see cref="SpaceField.All"/>).
    /// </summary>
    internal IReadOnlyList<SpaceData> SpaceList =>
        _spaceIds.Select(id => _spaces[id]).Where(s => !s.Deleted && s.Id != SpaceField.All).OrderBy(s => s, Comparer<SpaceData>.Create((a, b) => CompareKeys(a.Order, a.Id, b.Order, b.Id))).ToList();

    /// <summary>
    /// The spaces as they're listed everywhere, Main included: by order key, then id. Main without a
    /// place of its own comes first: with no record (never renamed or moved) as <see cref="MainStandIn"/>,
    /// or as its record when it was only ever renamed (its order never stamped, so its key means nothing).
    /// </summary>
    internal IReadOnlyList<SpaceData> ShownSpaces
    {
        get
        {
            var live = SpaceList;
            if (MainHasPlace) return live;
            var main = live.FirstOrDefault(s => s.Id == SpaceField.Main) ?? MainStandIn;
            return [main, .. live.Where(s => s.Id != SpaceField.Main)];
        }
    }

    /// <summary>Main in <see cref="ShownSpaces"/> while it has no record: no name ("Main"), no key, no clocks.</summary>
    internal static readonly SpaceData MainStandIn = new(SpaceField.Main, "", "", false, new SpaceClocks(Stamp.Zero, Stamp.Zero, Stamp.Zero));

    /// <summary>
    /// Whether Main has a place among the spaces' keys: a live record whose order was stamped (Main was
    /// moved, here or on another device). A record made by renaming alone has none, so renaming never
    /// moves Main, on any device.
    /// </summary>
    private bool MainHasPlace => _spaces.TryGetValue(SpaceField.Main, out var main) && !main.Deleted && main.Clk.Order != Stamp.Zero;

    /// <summary>Any space with this id, deleted ones included.</summary>
    internal SpaceData? SpaceById(string id) => _spaces.GetValueOrDefault(id);

    /// <summary>
    /// How many records taking <paramref name="data"/> in would add (tasks, sheets and spaces this
    /// workspace has no record of, deleted or not): what an import or another device's file would add
    /// to this device's own sync file. Spaces are records like any other here: how many show is up to
    /// the screen (<see cref="SpaceView.MaxListed"/>).
    /// </summary>
    internal int WouldAdd(WorkspaceData data) =>
        data.Tasks.Select(t => t.Id).Distinct().Count(id => !_tasks.ContainsKey(id))
        + data.Sheets.Select(s => s.Id).Distinct().Count(id => !_sheets.ContainsKey(id))
        + data.Spaces.Select(s => s.Id).Distinct().Count(id => !_spaces.ContainsKey(id));

    /// <summary>Every task, sheet and space held, deleted ones included (as a device file counts them).</summary>
    internal int RecordCount => _tasks.Count + _sheets.Count + _spaces.Count;

    /// <summary>Every task held, deleted ones included.</summary>
    internal int TaskCount => _tasks.Count;

    /// <summary>Adds a space after the last one.</summary>
    internal SpaceData AddSpace(string name)
    {
        string? last = SpaceList.LastOrDefault()?.Order;
        string c = Clock.Now();
        var space = new SpaceData(_newId(), SpaceField.CleanName(name), last != null ? OrderKey.After(last) : OrderKey.Mid, false, SpaceClocks.All(c));
        PutSpace(space);
        MarkChanged();
        SpacesTouched();
        return space;
    }

    /// <summary>
    /// Moves a space to <paramref name="index"/> in <see cref="ShownSpaces"/> (Main included): it gets a
    /// key between its new neighbours, stamped on its order alone (neighbours that share a key with the
    /// one above make room too, as tasks do: <see cref="OrderKey.KeysAt"/>). Main has no key until it
    /// has a place: moving Main gives it one (making its record if it has none: name "" so it still
    /// shows as Main, order stamped, name and deleted Zero; a Main renamed but never moved keeps its
    /// name and has only its order stamped); a space going above a Main with no place first gives Main
    /// one the same way, at the top.
    /// </summary>
    internal void MoveSpace(string id, int index)
    {
        var shown = ShownSpaces;
        int from = -1;
        for (int i = 0; i < shown.Count; i++) if (shown[i].Id == id) from = i;
        if (from < 0) return;
        index = Math.Clamp(index, 0, shown.Count - 1);
        if (index == from) return;
        var others = shown.Where(s => s.Id != id).ToList();

        if (id != SpaceField.Main && !MainHasPlace)
        {
            // others[0] is Main, first whatever its key.
            if (index > 0)
            {
                // Main stays first whatever the keys: place among the spaces after it.
                others.RemoveAt(0);
                index--;
            }
            else
            {
                // Going above Main: Main needs a key first, ahead of everything else.
                if (_spaces.GetValueOrDefault(SpaceField.Main) is { Deleted: true }) return; // a deleted Main record (only a damaged file has one)
                others[0] = PlaceMain(OrderKey.Between(null, others.Count > 1 ? others[1].Order : null));
            }
        }

        var keys = OrderKey.KeysAt(others.Select(s => s.Order).ToList(), index);
        if (id == SpaceField.Main) PlaceMain(keys[0]);
        else PutSpace(_spaces[id] with { Order = keys[0], Clk = _spaces[id].Clk with { Order = Clock.Now() } });
        for (int i = 1; i < keys.Length; i++)
        {
            var s = _spaces[others[index + i - 1].Id];
            PutSpace(s with { Order = keys[i], Clk = s.Clk with { Order = Clock.Now() } });
        }
        MarkChanged();
        SpacesTouched();
    }

    /// <summary>
    /// Renames a space (not a deleted one; the same name changes nothing). Main has no record until
    /// it's first renamed; this makes it, with the name alone and no place (its order never stamped),
    /// so Main stays where it was, here and on every device the name reaches.
    /// Names are kept to 100 characters (<see cref="SpaceField.CleanName"/>).
    /// </summary>
    internal void RenameSpace(string id, string name)
    {
        string text = SpaceField.CleanName(name);
        if (!_spaces.TryGetValue(id, out var space))
        {
            if (id != SpaceField.Main) return;
            PutSpace(new SpaceData(SpaceField.Main, text, OrderKey.Mid, false, new SpaceClocks(Clock.Now(), Stamp.Zero, Stamp.Zero)));
            MarkChanged();
            SpacesTouched();
            return;
        }
        if (space.Deleted || space.Name == text) return;
        SetSpaceName(space, text);
    }

    /// <summary>
    /// Deletes a space (never Main). Its tasks and sheets keep their space and show in Main
    /// (<see cref="Spaces.Effective"/>), so Undo brings them all back with it.
    /// </summary>
    internal Action DeleteSpace(string id)
    {
        if (id == SpaceField.Main || !_spaces.TryGetValue(id, out var space) || space.Deleted) return () => { };
        SetSpaceDeleted(space, true);
        return () =>
        {
            if (_spaces.TryGetValue(id, out var s)) SetSpaceDeleted(s, false);
        };
    }

    /// <summary>Puts a task in a space ("main" for Main). Its place in the list and the focus stay as they are.</summary>
    internal void MoveTaskToSpace(string taskId, string spaceId)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || !Ids.IsValid(spaceId) || task.Space == spaceId) return;
        task.Space = spaceId;
        task.Clk = task.Clk with { Space = Clock.Now() };
        MarkChanged();
        SpacesTouched();
    }

    /// <summary>Puts a sheet in a space ("main" for Main). Nothing else about it changes.</summary>
    internal void MoveSheetToSpace(string sheetId, string spaceId)
    {
        if (!_sheets.TryGetValue(sheetId, out var sheet) || !Ids.IsValid(spaceId) || sheet.Space == spaceId) return;
        sheet.Space = spaceId;
        sheet.Clk = sheet.Clk with { Space = Clock.Now() };
        MarkChanged();
        SpacesTouched();
    }

    // ---- Housekeeping ----

    /// <summary>
    /// Call at midnight (and when the panel opens): "done today" resets, and very old deletions are
    /// forgotten, except while syncing: then they're kept for good, so an out-of-date copy on
    /// another device can never bring a deleted task back (see docs/SYNC.md).
    /// </summary>
    public void RefreshDay()
    {
        if (!KeepDeletions() && PurgeTombstones() > 0) MarkChanged();
        RaiseSummary();
    }

    /// <summary>While sync is on, deletions are never forgotten (tests can say otherwise).</summary>
    internal Func<bool> KeepDeletions { get; set; }

    /// <summary>
    /// Whether a sheet holds something kept on this PC alone (snippets), so it isn't blank even with no text.
    /// Nothing here syncs; it only keeps <see cref="LeaveWelcomeBehind"/> from taking such a sheet.
    /// </summary>
    internal Func<string, bool>? HoldsLocalContent { get; set; }

    /// <summary>
    /// Joining a sync folder your other devices already use: the welcome tasks you never touched
    /// (not edited, ticked or moved, in the list or to a space: all their clocks are still the one
    /// they were made with, here, and none for a space) and a blank sheet never written on (or put
    /// in a space) stay behind, instead of turning up on every device. A sheet stays if it would be
    /// the last one. (untouchedWelcome in Tack Focus.)
    /// </summary>
    internal void LeaveWelcomeBehind(Func<string, bool> isWelcomeTip)
    {
        bool Untouched(TaskItem t) =>
            isWelcomeTip(t.Text) && Stamp.Node(t.Clk.Text) == Clock.Node
            && t.Clk.Completed == t.Clk.Text && t.Clk.Order == t.Clk.Text && t.Clk.Deleted == t.Clk.Text && t.Clk.Space == Stamp.Zero;
        var tasks = _allTasks.Where(t => !t.IsDeleted && Untouched(t)).ToList();
        foreach (var t in tasks) Remove(t);
        // Only this PC's own blank sheets, never one that just arrived from another device.
        var blank = Sheets.Where(s => s.Text.Length == 0 && s.Clk.Text == s.Clk.Deleted && Stamp.Node(s.Clk.Deleted) == Clock.Node && s.Clk.Space == Stamp.Zero
            && HoldsLocalContent?.Invoke(s.Id) != true).ToList();
        if (blank.Count == Sheets.Count) blank = blank.Skip(1).ToList();
        foreach (var s in blank) SetSheetDeleted(s, true);
        if (tasks.Count > 0 || blank.Count > 0) Rebuild();
    }

    /// <summary>
    /// The welcome tasks' focus yields to any real one: when a new PC first syncs, the focus you
    /// set on another device wins over "Tick this circle...". (Same as Tack Focus's seedWelcome.)
    /// </summary>
    internal void YieldFocus()
    {
        _focus = _focus with { Clk = Stamp.Encode(1, 0, "") };
        MarkChanged();
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
        foreach (string id in _spaceIds.Where(id => _spaces[id] is { Deleted: true } s && Stamp.Time(s.Clk.Deleted) < cutoff).ToList())
        {
            _spaceIds.Remove(id);
            _spaces.Remove(id);
            removed++;
        }
        return removed;
    }

    // ---- Records: loading, merging, restoring ----

    /// <summary>Copies of every record (deleted ones included) and the focus register, e.g. for saving.</summary>
    internal WorkspaceData ToData() =>
        new(_allTasks.Select(t => t.ToData()).ToList(), _allSheets.Select(s => s.ToData()).ToList(), _focus, _spaceIds.Select(id => _spaces[id]).ToList());

    /// <summary>
    /// Merges records from elsewhere (a file, or another device) into the workspace. Newer edits win
    /// field by field; nothing newer is lost. Returns whether anything changed.
    /// </summary>
    /// <param name="base">The clock both sides last agreed on: notes rewritten on both sides since are kept twice.</param>
    /// <param name="concurrent">Instead of a base (sync): were these two texts of a note written without seeing each other?</param>
    internal bool MergeFrom(WorkspaceData data, string? @base = null, Func<SheetData, SheetData, bool>? concurrent = null)
    {
        bool changed = false;
        _applying = true;
        try
        {
            foreach (var record in data.Tasks)
            {
                var remote = SpaceField.Normal(record);
                Clock.Observe(remote.Clk.Max());
                _tasks.TryGetValue(remote.Id, out var local);
                var (value, c) = Merge.Task(local?.ToData(), remote);
                if (!c) continue;
                changed = true;
                if (local != null) local.Apply(value);
                else AddItem(value);
            }
            foreach (var record in data.Sheets)
            {
                var remote = SpaceField.Normal(record);
                Clock.Observe(remote.Clk.Max());
                _sheets.TryGetValue(remote.Id, out var local);
                var (value, c, conflict) = Merge.Sheet(local?.ToData(), remote, @base, concurrent);
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
            foreach (var remote in data.Spaces)
            {
                Clock.Observe(remote.Clk.Max());
                var (value, c) = Merge.Space(_spaces.GetValueOrDefault(remote.Id), remote);
                if (!c) continue;
                changed = true;
                PutSpace(value);
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
        // Never put anywhere ("") and moved back ("main") are both Main; going back there is a move to "main".
        var tasks = new Dictionary<string, TaskData>();
        foreach (var t in data.Tasks) tasks[t.Id] = SpaceField.Normal(t);
        var sheets = new Dictionary<string, SheetData>();
        foreach (var s in data.Sheets) sheets[s.Id] = SpaceField.Normal(s);

        foreach (string id in _allTasks.Select(t => t.Id).Concat(data.Tasks.Select(t => t.Id)).Distinct().ToList())
        {
            tasks.TryGetValue(id, out var target);
            if (!_tasks.TryGetValue(id, out var item))
            {
                if (target is { Deleted: false })
                {
                    string c = Clock.Now();
                    AddItem(target with { Clk = TaskClocks.All(c) with { Space = target.Space.Length > 0 ? c : Stamp.Zero } });
                }
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
            if (!SameSpace(current.Space, want.Space)) MoveTaskToSpace(id, SpaceOrMain(want.Space));
        }

        foreach (string id in _allSheets.Select(s => s.Id).Concat(data.Sheets.Select(s => s.Id)).Distinct().ToList())
        {
            sheets.TryGetValue(id, out var target);
            if (!_sheets.TryGetValue(id, out var item))
            {
                if (target is { Deleted: false })
                {
                    string c = Clock.Now();
                    AddItem(target with { Clk = SheetClocks.All(c) with { Space = target.Space.Length > 0 ? c : Stamp.Zero } });
                }
                continue;
            }
            var current = item.ToData();
            var want = target ?? current with { Deleted = true };
            if (current.Deleted && !want.Deleted) SetSheetDeleted(item, false);
            if (current.Text != want.Text && !want.Deleted && !item.IsDeleted) item.Text = NoteSheet.ForTextBox(want.Text);
            if (!current.Deleted && want.Deleted) SetSheetDeleted(item, true);
            if (!SameSpace(current.Space, want.Space)) MoveSheetToSpace(id, SpaceOrMain(want.Space));
        }

        // Names as they're kept now (a snapshot may hold one from before the limit).
        var spaces = new Dictionary<string, SpaceData>();
        foreach (var s in data.Spaces) spaces[s.Id] = s with { Name = SpaceField.CleanName(s.Name) };
        foreach (string id in _spaceIds.Concat(data.Spaces.Select(s => s.Id)).Distinct().ToList())
        {
            spaces.TryGetValue(id, out var target);
            if (!_spaces.TryGetValue(id, out var current))
            {
                if (target is { Deleted: false }) PutSpace(target with { Clk = SpaceClocks.All(Clock.Now()) });
                continue;
            }
            // Main is never deleted: before it was renamed, it had no name of its own.
            var want = target ?? (id == SpaceField.Main ? current with { Name = "" } : current with { Deleted = true });
            if (current.Name != want.Name && !want.Deleted) SetSpaceName(_spaces[id], want.Name);
            if (current.Order != want.Order && !want.Deleted)
            {
                var space = _spaces[id];
                PutSpace(space with { Order = want.Order, Clk = space.Clk with { Order = Clock.Now() } });
            }
            if (current.Deleted != want.Deleted && id != SpaceField.Main) SetSpaceDeleted(_spaces[id], want.Deleted);
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
            foreach (var record in data.Tasks)
            {
                var t = SpaceField.Normal(record);
                Clock.Observe(t.Clk.Max());
                if (_tasks.TryGetValue(t.Id, out var twin)) twin.Apply(Merge.Task(twin.ToData(), t).Value);
                else AddItem(t);
            }
            foreach (var record in data.Sheets)
            {
                var s = SpaceField.Normal(record);
                Clock.Observe(s.Clk.Max());
                if (_sheets.TryGetValue(s.Id, out var twin)) twin.Apply(Merge.Sheet(twin.ToData(), s).Value);
                else AddItem(s);
            }
            foreach (var s in data.Spaces)
            {
                Clock.Observe(s.Clk.Max());
                PutSpace(Merge.Space(_spaces.GetValueOrDefault(s.Id), s).Value);
            }
            Clock.Observe(data.Focus.Clk);
            _focus = data.Focus;
        }
        finally
        {
            _applying = false;
        }
        if (!KeepDeletions()) PurgeTombstones();
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
        SpacesTouched(); // merged or restored records may have moved, and spaces come and go
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

    /// <summary>
    /// Gives a task the place at <paramref name="index"/> in <paramref name="list"/> (open tasks in order, without it).
    /// Neighbours that share a key with the one above get new keys too (keeping their order), so the task really lands there.
    /// </summary>
    private void SetOrderAt(TaskItem task, IReadOnlyList<TaskItem> list, int index)
    {
        var keys = OrderKey.KeysAt(list.Select(t => t.Order).ToList(), index);
        SetOrder(task, keys[0]);
        for (int i = 1; i < keys.Length; i++) SetOrder(list[index + i - 1], keys[i]);
    }

    private void Restamp(TaskItem task, bool text = false, bool completed = false, bool order = false, bool deleted = false)
    {
        var c = task.Clk;
        string stamp = Clock.Now();
        task.Clk = c with { Text = text ? stamp : c.Text, Completed = completed ? stamp : c.Completed, Order = order ? stamp : c.Order, Deleted = deleted ? stamp : c.Deleted };
        MarkChanged();
    }

    private void SetSheetDeleted(NoteSheet sheet, bool deleted)
    {
        sheet.IsDeleted = deleted;
        sheet.Clk = sheet.Clk with { Deleted = Clock.Now() };
        MarkChanged();
    }

    /// <summary>
    /// Gives Main a place: its record's order, stamped (a name it was given stays). With no record yet,
    /// one is made: no name (it shows as "Main"), only the order stamped. Returns the record.
    /// </summary>
    private SpaceData PlaceMain(string order)
    {
        var main = _spaces.TryGetValue(SpaceField.Main, out var existing)
            ? existing with { Order = order, Clk = existing.Clk with { Order = Clock.Now() } }
            : new SpaceData(SpaceField.Main, "", order, false, new SpaceClocks(Stamp.Zero, Clock.Now(), Stamp.Zero));
        PutSpace(main);
        return main;
    }

    private void SetSpaceName(SpaceData space, string name)
    {
        PutSpace(space with { Name = name, Clk = space.Clk with { Name = Clock.Now() } });
        MarkChanged();
        SpacesTouched();
    }

    private void SetSpaceDeleted(SpaceData space, bool deleted)
    {
        PutSpace(space with { Deleted = deleted, Clk = space.Clk with { Deleted = Clock.Now() } });
        MarkChanged();
        SpacesTouched();
    }

    /// <summary>Adds or replaces a space record (they're immutable: the screen asks for them again on <see cref="SpacesChanged"/>).</summary>
    private void PutSpace(SpaceData space)
    {
        if (!_spaces.ContainsKey(space.Id)) _spaceIds.Add(space.Id);
        _spaces[space.Id] = space;
    }

    /// <summary>"" (never assigned) and "main" are both Main.</summary>
    private static bool SameSpace(string a, string b) => SpaceOrMain(a) == SpaceOrMain(b);

    private static string SpaceOrMain(string space) => space.Length == 0 ? SpaceField.Main : space;

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

    private NoteSheet AddSheetAtEnd(string? space = null, string text = "")
    {
        string? last = Sheets.Count > 0 ? Sheets[^1].Order : null;
        string c = Clock.Now();
        string where = RealSpace(space);
        var sheet = AddItem(new SheetData(_newId(), JsText.NoteText(text), Today(), last != null ? OrderKey.After(last) : OrderKey.Mid, false, where,
            SheetClocks.All(c) with { Space = where.Length > 0 ? c : Stamp.Zero }));
        Sheets.Add(sheet);
        return sheet;
    }

    /// <summary>What a new task or sheet records as its space: a live space other than Main, else "" (Main).</summary>
    private string RealSpace(string? space) =>
        space != null && Spaces.Effective(space, _spaces.Values) is var s && s != SpaceField.Main ? s : "";

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

    private void SpacesTouched() => SpacesChanged?.Invoke(this, EventArgs.Empty);

    // ---- What reading a file needs to know (see TackFile) ----

    Hlc IImportTarget.Clock => Clock;

    string? IImportTarget.FocusId => _focus.Id;

    string? IImportTarget.FirstOpenOrder => TopOrder();

    string? IImportTarget.LastSheetOrder => Sheets.Count > 0 ? Sheets[^1].Order : null;

    TaskData? IImportTarget.FindTask(string id) => _tasks.TryGetValue(id, out var t) ? t.ToData() : null;

    SheetData? IImportTarget.FindSheet(string id) => _sheets.TryGetValue(id, out var s) ? s.ToData() : null;

    int IImportTarget.RecordCount => RecordCount;

    int IImportTarget.WouldAdd(WorkspaceData data) => WouldAdd(data);
}
