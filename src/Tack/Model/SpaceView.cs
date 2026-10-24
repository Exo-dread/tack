using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Tack.Sync;

namespace Tack.Model;

/// <summary>A space as the strip, menus and Settings list it: its id ("main" for Main) and the name shown.</summary>
public sealed record SpaceTab(string Id, string Name);

/// <summary>
/// What the panel and the orb show of one space, or of all of them: the space on show on this PC,
/// its open and finished tasks and its sheets (in the one global order, filtered), and the counts.
///
/// With no space besides Main, or in All, the lists are the workspace's own collections, so the
/// panel is exactly what it was before spaces. Only a space other than All with a second space
/// around gets lists of its own, kept in step with the workspace's.
/// </summary>
public sealed class SpaceView : Observable
{
    /// <summary>The value of <see cref="Shown"/> (and <see cref="Settings.SpaceOnShow"/>) for everything at once.</summary>
    public const string All = SpaceField.All;

    private readonly Workspace _ws;
    private readonly ObservableCollection<TaskItem> _open = new();
    private readonly ObservableCollection<TaskItem> _done = new();
    private readonly ObservableCollection<NoteSheet> _sheets = new();
    private readonly Dictionary<string, string> _lastSheet = new(); // the sheet each space showed last, this session
    private IReadOnlyList<SpaceTab> _tabs = [];
    private Dictionary<string, string> _names = new();
    private string _shown = SpaceField.Main;
    private bool _filtered;
    private NoteSheet? _activeSheet;

    public SpaceView(Workspace ws)
    {
        _ws = ws;
        ws.Open.CollectionChanged += (_, _) => SyncList(_open, ws.Open);
        ws.Done.CollectionChanged += (_, _) => SyncList(_done, ws.Done);
        ws.Sheets.CollectionChanged += OnSheetsChanged;
        ws.SpacesChanged += (_, _) => Refresh();
        ws.SummaryChanged += (_, _) => RaiseSummary();
        ws.PropertyChanged += OnWorkspaceChanged;
        ws.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.SpaceOnShow)) Refresh(); };
        Refresh();
    }

    public Settings Settings => _ws.Settings;

    // ---- Spaces ----

    /// <summary>Main and every live space, in the order they're listed everywhere.</summary>
    public IReadOnlyList<SpaceTab> Tabs => _tabs;

    /// <summary>
    /// Spaces one list draws at once (the strip, the Move to menus, Settings): "More spaces" draws as
    /// many again. Only a crafted file or import brings more than a few dozen, and every one is kept
    /// and synced: a limit on what a device takes in would stop devices that differ by a single space
    /// taking each other's files, so the screen is protected instead (MAX_SPACES_LISTED in Tack Focus).
    /// </summary>
    public const int MaxListed = 1_000;

    /// <summary>
    /// The spaces a list draws, with their places in <paramref name="tabs"/>: the first
    /// <paramref name="limit"/>, and <paramref name="keep"/> (the space on show, say) wherever it is;
    /// <c>More</c> is how many it leaves out (listed in Tack Focus).
    /// </summary>
    public static (IReadOnlyList<(SpaceTab Tab, int Index)> List, int More) Listed(IReadOnlyList<SpaceTab> tabs, int limit, string? keep = null)
    {
        var list = new List<(SpaceTab, int)>();
        for (int i = 0; i < tabs.Count; i++)
            if (i < limit || tabs[i].Id == keep) list.Add((tabs[i], i));
        return (list, tabs.Count - list.Count);
    }

    /// <summary>True once a second space exists: only then do the strip, the keys and the labels appear.</summary>
    public bool HasSpaces => _tabs.Count > 1;

    /// <summary>The space on show: <see cref="All"/>, "main" or a live space's id (a space that's gone shows Main).</summary>
    public string Shown => _shown;

    public bool IsAll => _shown == All;

    /// <summary>"All", or the name of the space on show.</summary>
    public string ShownName => IsAll ? "All" : NameOf(_shown);

    /// <summary>Shows a space ("main", a space id or <see cref="All"/>) on this PC, and remembers it.</summary>
    public void Show(string id) => Settings.SpaceOnShow = id == SpaceField.Main ? null : id;

    /// <summary>Alt+0 (<see cref="All"/>) and Alt+1..9 (the first nine spaces); null if there's none at that number.</summary>
    public string? SpaceForKey(int number) =>
        number == 0 ? All : number >= 1 && number <= _tabs.Count ? _tabs[number - 1].Id : null;

    /// <summary>The space a task, sheet or space id resolves to: its own if it's live, else Main.</summary>
    public string Effective(string space) =>
        space.Length > 0 && space != SpaceField.Main && space != All && _ws.SpaceById(space) is { Deleted: false } ? space : SpaceField.Main;

    public string SpaceOf(TaskItem task) => Effective(task.Space);

    public string SpaceOf(NoteSheet sheet) => Effective(sheet.Space);

    /// <summary>A space's name as it's shown: Main's own name if it has one, else "Main"; a space without a name is "Untitled".</summary>
    public string NameOf(string id) =>
        // Looked up, not searched for: All shows each task's space, and a crafted file may bring thousands.
        _names.TryGetValue(id, out string? name) ? name : id == SpaceField.Main ? MainName : "Untitled";

    private string MainName => _ws.SpaceById(SpaceField.Main) is { Deleted: false, Name.Length: > 0 } main ? main.Name : "Main";

    /// <summary>A space's name in a toast ("Moved to …", "Deleted …"): at most 24 characters, as Tack Focus shows it, so Undo keeps its room.</summary>
    public static string ToastName(string name) => Shorten(name, 24);

    /// <summary>At most <paramref name="max"/> characters, ending in "…" when cut (Tack Focus's <c>shorten</c>).</summary>
    public static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // never half a character
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>The space a new task or sheet goes in: the one on show (in All: Main).</summary>
    public string NewItemSpace => _filtered ? _shown : SpaceField.Main;

    // ---- Tasks ----

    /// <summary>Open tasks on show, in the user's order (excluding the task in focus).</summary>
    public ObservableCollection<TaskItem> Open => _filtered ? _open : _ws.Open;

    /// <summary>Finished tasks on show, most recent first.</summary>
    public ObservableCollection<TaskItem> Done => _filtered ? _done : _ws.Done;

    /// <summary>The task in focus, when it's in the space on show (or All shows it); see <see cref="NowElsewhere"/>.</summary>
    public TaskItem? Now => _ws.Now is { } now && InView(now) ? now : null;

    public bool HasNow => Now != null;

    /// <summary>The task in focus when it's in another space: the panel shows a line pointing to it.</summary>
    public TaskItem? NowElsewhere => _ws.Now is { } now && !InView(now) ? now : null;

    public bool HasNowElsewhere => NowElsewhere != null;

    /// <summary>"Now in Work: ", leading the line that points to the task in focus in another space.</summary>
    public string NowElsewhereLabel => NowElsewhere is { } now ? $"Now in {NameOf(SpaceOf(now))}: " : "";

    /// <summary>What screen readers hear for that line.</summary>
    public string NowLineName => NowElsewhere is { } now ? $"{NowElsewhereLabel}{now.Text}" : "";

    /// <summary>The space of the task in focus, shown on its card in All while there are spaces; else null.</summary>
    public string? NowLabel => HasSpaces && IsAll && _ws.Now is { } now ? NameOf(SpaceOf(now)) : null;

    /// <summary>The add box's placeholder: "Add to Work" in a space, as before ("Add a task") in All or without spaces.</summary>
    public string AddPlaceholder => _filtered ? $"Add to {ShownName}" : "Add a task";

    /// <summary>What screen readers hear for the add box: "Add a task to Work" in a space, else "Add a task".</summary>
    public string AddName => _filtered ? $"Add a task to {ShownName}" : "Add a task";

    /// <summary>
    /// Moves an open task from one place in the list on show to another. In a space the move is
    /// relative to the neighbours you see: it lands just above the task it's dropped on (moving up)
    /// or just below it (moving down), wherever tasks of other spaces sit, and only this task is
    /// stamped. Tasks in other spaces are never re-keyed: every place between its new neighbours
    /// looks the same here, so where keys tie (<see cref="OrderKey.KeysAt"/>) it takes the nearest
    /// of those places whose neighbours' keys differ; if they all tie, only this space's tasks make
    /// room (as Tack Focus does).
    /// </summary>
    public void MoveOpen(int from, int to)
    {
        if (!_filtered)
        {
            _ws.MoveOpen(from, to);
            return;
        }
        if (from == to || from < 0 || to < 0 || from >= _open.Count || to >= _open.Count) return;
        var task = _open[from];
        var others = _open.Where(t => t != task).ToList();
        var all = _ws.Open.Where(t => t != task).ToList();
        int lo = to > 0 ? all.IndexOf(others[to - 1]) + 1 : 0;
        int hi = to < others.Count ? all.IndexOf(others[to]) : all.Count;
        bool Free(int g) => g == 0 || g == all.Count || string.CompareOrdinal(all[g - 1].Order, all[g].Order) < 0;
        for (int k = 0; k <= hi - lo; k++)
        {
            int g = to > from ? lo + k : hi - k;
            if (!Free(g)) continue;
            _ws.MoveOpen(_ws.Open.IndexOf(task), g);
            return;
        }
        _ws.MoveOpenAmong(task, others, to);
    }

    /// <summary>Clears the finished tasks on show (all of them in All). Returns an undo action.</summary>
    public Action ClearDone() => _filtered ? _ws.ClearDone(_done.ToList()) : _ws.ClearDone();

    // ---- Notes ----

    /// <summary>Sheets on show, in order.</summary>
    public ObservableCollection<NoteSheet> Sheets => _filtered ? _sheets : _ws.Sheets;

    /// <summary>
    /// The sheet being written on. Null only in a space with no sheets: it shows blank paper, and
    /// the first keystroke makes its sheet (switching spaces never makes empty ones).
    /// </summary>
    public NoteSheet? ActiveSheet
    {
        get => _activeSheet;
        set
        {
            if (value != null) _ws.ActiveSheet = value;
        }
    }

    // ---- Summary (the orb, its peek and the Tasks badge) ----

    /// <summary>Open tasks on show, the task in focus included when it's here.</summary>
    public int OpenCount => _filtered ? _ws.Open.Count(InView) + (Now != null ? 1 : 0) : _ws.OpenCount;

    public int DoneToday => _filtered ? _ws.Done.Count(t => t.CompletedToday && InView(t)) : _ws.DoneToday;

    public bool HasOpen => OpenCount > 0;

    public bool HasDone => _filtered ? _ws.Done.Any(InView) : _ws.HasDone;

    /// <summary>Share of today's work on show that is finished (0..1).</summary>
    public double Progress
    {
        get
        {
            int done = DoneToday, total = done + OpenCount;
            return total == 0 ? 0 : (double)done / total;
        }
    }

    /// <summary>What to do next here: the task in focus, otherwise the top of the list.</summary>
    public TaskItem? FocusTask => _filtered ? Now ?? _ws.Open.FirstOrDefault(InView) : _ws.FocusTask;

    /// <summary>Raised when counts, progress or the focus task on show may have changed.</summary>
    public event EventHandler? SummaryChanged;

    // ---- Keeping in step ----

    private bool InView(TaskItem task) => !_filtered || SpaceOf(task) == _shown;

    private bool InView(NoteSheet sheet) => !_filtered || SpaceOf(sheet) == _shown;

    /// <summary>Everything again: the spaces, the space on show and the lists (after a space or the space on show changed).</summary>
    private void Refresh()
    {
        var tabs = _ws.ShownSpaces.Select(s => new SpaceTab(s.Id, s.Id == SpaceField.Main ? MainName : s.Name.Length > 0 ? s.Name : "Untitled")).ToList();
        bool tabsChanged = !tabs.SequenceEqual(_tabs);
        if (tabsChanged)
        {
            _tabs = tabs;
            _names = new Dictionary<string, string>();
            foreach (var tab in tabs) _names.TryAdd(tab.Id, tab.Name);
        }
        string? wanted = Settings.SpaceOnShow;
        string shown = wanted == All ? All : wanted != null && _ws.SpaceById(wanted) is { Deleted: false } ? wanted : SpaceField.Main;
        bool shownChanged = shown != _shown;
        _shown = shown;
        bool filtered = HasSpaces && !IsAll;
        bool switched = filtered != _filtered;
        _filtered = filtered;

        if (_filtered)
        {
            Reconcile(_open, _ws.Open.Where(InView).ToList());
            Reconcile(_done, _ws.Done.Where(InView).ToList());
            Reconcile(_sheets, _ws.Sheets.Where(InView).ToList());
        }
        // Lists of its own (again) or the workspace's: the screen swaps them before the old ones empty.
        if (switched)
        {
            OnPropertyChanged(nameof(Open));
            OnPropertyChanged(nameof(Done));
            OnPropertyChanged(nameof(Sheets));
        }
        if (!_filtered)
        {
            _open.Clear();
            _done.Clear();
            _sheets.Clear();
        }
        UpdateLabels();
        UpdateActiveSheet();

        if (tabsChanged)
        {
            OnPropertyChanged(nameof(Tabs));
            OnPropertyChanged(nameof(HasSpaces));
        }
        if (shownChanged)
        {
            OnPropertyChanged(nameof(Shown));
            OnPropertyChanged(nameof(IsAll));
        }
        OnPropertyChanged(nameof(ShownName));
        OnPropertyChanged(nameof(AddPlaceholder));
        OnPropertyChanged(nameof(AddName));
        OnPropertyChanged(nameof(NewItemSpace));
        RaiseNow();
        RaiseSummary();
    }

    private void SyncList(ObservableCollection<TaskItem> mine, ObservableCollection<TaskItem> source)
    {
        if (_filtered) Reconcile(mine, source.Where(InView).ToList());
        if (HasSpaces && IsAll) UpdateLabels();
    }

    private void OnSheetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_filtered) return;
        Reconcile(_sheets, _ws.Sheets.Where(InView).ToList());
        UpdateActiveSheet();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.Now):
                if (HasSpaces && IsAll) UpdateLabels();
                RaiseNow();
                break;
            case nameof(Workspace.ActiveSheet):
                UpdateActiveSheet();
                break;
        }
    }

    /// <summary>
    /// The sheet on show: the workspace's own while it's in this space; else the one this space
    /// showed last, or its first; or none (blank paper). It becomes the workspace's active sheet, so
    /// Tack opens on it next time.
    /// </summary>
    private void UpdateActiveSheet()
    {
        var active = _ws.ActiveSheet;
        NoteSheet? want = active;
        if (_filtered && (active == null || !_sheets.Contains(active)))
        {
            want = _lastSheet.TryGetValue(_shown, out string? id) && _sheets.FirstOrDefault(s => s.Id == id) is { } last ? last : _sheets.FirstOrDefault();
            if (want != null)
            {
                _ws.ActiveSheet = want; // comes back here through PropertyChanged
                if (_ws.ActiveSheet == want && _activeSheet == want) return;
            }
        }
        if (want != null && _filtered) _lastSheet[_shown] = want.Id;
        if (ReferenceEquals(_activeSheet, want)) return;
        _activeSheet = want;
        OnPropertyChanged(nameof(ActiveSheet));
    }

    /// <summary>Each task's space name, shown on its row in All while there are spaces.</summary>
    private void UpdateLabels()
    {
        bool show = HasSpaces && IsAll;
        foreach (var t in _ws.Open) t.SpaceLabel = show ? NameOf(SpaceOf(t)) : null;
        foreach (var t in _ws.Done) t.SpaceLabel = show ? NameOf(SpaceOf(t)) : null;
        if (_ws.Now is { } now) now.SpaceLabel = show ? NameOf(SpaceOf(now)) : null;
    }

    private void RaiseNow()
    {
        OnPropertyChanged(nameof(Now));
        OnPropertyChanged(nameof(HasNow));
        OnPropertyChanged(nameof(NowElsewhere));
        OnPropertyChanged(nameof(HasNowElsewhere));
        OnPropertyChanged(nameof(NowElsewhereLabel));
        OnPropertyChanged(nameof(NowLineName));
        OnPropertyChanged(nameof(NowLabel));
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(DoneToday));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(FocusTask));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(HasOpen));
        if (_ws.Now != null && !InView(_ws.Now)) OnPropertyChanged(nameof(NowLineName)); // its text may have changed
        SummaryChanged?.Invoke(this, EventArgs.Empty);
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
}
