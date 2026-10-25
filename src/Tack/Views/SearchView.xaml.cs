using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Tack.Model;

namespace Tack.Views;

/// <summary>A line of a result: the text before the match, the match, and the text after it.</summary>
public sealed record Excerpt(string Before, string Match, string After)
{
    public static Excerpt Plain(string text) => new(text, "", "");

    public override string ToString() => Before + Match + After;
}

public enum SearchRowKind { Task, Done, Note, More }

/// <summary>One row of the results: a task, a finished task, a sheet, or "Show more" at the end of a group.</summary>
public sealed class SearchRow
{
    internal SearchRow(SearchRowKind kind, string group, Excerpt line, Excerpt? second, string meta, string name, SearchHit? hit, SearchKind? moreOf = null)
    {
        Kind = kind;
        Group = group;
        Line = line;
        Second = second;
        Meta = meta;
        Name = name;
        Hit = hit;
        MoreOf = moreOf;
    }

    public SearchRowKind Kind { get; }

    /// <summary>Its group's heading ("TASKS · 12"), which also groups the rows.</summary>
    public string Group { get; }

    public Excerpt Line { get; }

    /// <summary>A sheet's matching line, under its title (when the match isn't in the title).</summary>
    public Excerpt? Second { get; }

    public bool HasSecond => Second != null;

    /// <summary>Its space (while there are spaces), and "Now" for the task in focus.</summary>
    public string Meta { get; }

    /// <summary>What screen readers hear.</summary>
    public string Name { get; }

    internal SearchHit? Hit { get; }

    internal SearchKind? MoreOf { get; }

    public override string ToString() => Name;
}

/// <summary>
/// Search's results (Ctrl+F): what matches the query in the panel's header, from every space,
/// grouped Tasks, Done and Notes. The caret stays in the search box: arrows there choose a row,
/// Enter (or a click) opens it. Typing is followed a moment later (so a burst of keys is one
/// search), and each group draws its first rows, with "Show more" for the rest.
/// </summary>
public partial class SearchView : UserControl
{
    /// <summary>Rows a group draws at first; "Show more" draws this many again.</summary>
    internal const int FirstRows = 50;
    internal const int MoreRows = 200;

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(90);

    private readonly SearchIndex _index = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<SearchKind, int> _limits = new();
    private Workspace _ws = null!;
    private SpaceView _view = null!;
    private string _query = "";
    private string _shownQuery = "";
    private SearchResults _results = SearchResults.None;
    private List<SearchRow> _rows = [];
    private int _lead = 16; // characters a row shows before its match (see Cut), for the panel's width
    private double _width; // the width it was worked out for

    public SearchView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = Pause };
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>A row was opened (Enter or a click), with the query it was found by.</summary>
    internal event Action<SearchHit, string>? Opened;

    /// <summary>What a search found, for screen readers ("12 results").</summary>
    internal event Action<string>? Counted;

    /// <summary>The count shown in the search box ("12"; "" before anything is typed).</summary>
    internal event Action<string>? CountChanged;

    /// <summary>The row chosen, for screen readers (the caret stays in the box).</summary>
    internal event Action<string>? Chosen;

    internal void Initialize(Workspace ws, SpaceView view)
    {
        _ws = ws;
        _view = view;
        ShowHint();
    }

    /// <summary>The rows on show, in order (headings aside).</summary>
    internal IReadOnlyList<SearchRow> Rows => _rows;

    internal SearchRow? Current => Results.SelectedItem as SearchRow;

    internal SearchResults LastResults => _results;

    /// <summary>The query in the box changed: searched a moment later.</summary>
    internal void SetQuery(string query)
    {
        _query = query;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Searches now for what's in the box, if it hasn't been yet (Enter mustn't open a stale row).</summary>
    internal void Flush()
    {
        _timer.Stop();
        if (_query == _shownQuery && _results != SearchResults.None) return;
        _shownQuery = _query;
        _limits.Clear();
        _results = _ws == null ? SearchResults.None : _index.Find(_ws, _query);
        Draw(select: 0);
        string count = _results.Total == 0 ? "No results" : _results.Total == 1 ? "1 result" : $"{_results.Total:N0} results";
        CountChanged?.Invoke(_results.Query.Length == 0 ? "" : _results.Total.ToString("N0"));
        if (_results.Query.Length > 0) Counted?.Invoke(count);
    }

    /// <summary>Searches again for the same query (something changed while the results were open).</summary>
    internal void Refresh()
    {
        _shownQuery = "\0";
        Flush();
    }

    /// <summary>Search closed: the results go (the box keeps its query for next time).</summary>
    internal void Clear()
    {
        _timer.Stop();
        _results = SearchResults.None;
        _shownQuery = "";
        _rows = [];
        Results.ItemsSource = null;
        ShowHint();
    }

    /// <summary>Up and Down in the box: the row before or after (staying at either end).</summary>
    internal void Move(int delta)
    {
        Flush();
        if (_rows.Count == 0) return;
        int i = Current is { } row ? _rows.IndexOf(row) : -1;
        Select(Math.Clamp(i < 0 ? (delta > 0 ? 0 : _rows.Count - 1) : i + delta, 0, _rows.Count - 1));
    }

    /// <summary>Enter in the box: opens the row chosen (the first, if none is), or draws more for "Show more".</summary>
    internal bool OpenCurrent()
    {
        Flush();
        var row = Current ?? _rows.FirstOrDefault();
        if (row == null) return false;
        Open(row);
        return true;
    }

    private void Open(SearchRow row)
    {
        if (row.MoreOf is { } kind)
        {
            int at = _rows.IndexOf(row);
            _limits[kind] = Limit(kind) + MoreRows;
            Draw(select: at);
            return;
        }
        if (row.Hit is { } hit) Opened?.Invoke(hit, _shownQuery);
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SearchRow row })
        {
            Results.SelectedItem = row;
            Open(row);
            e.Handled = true;
        }
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        Results.SelectedItem = row;
        Results.ScrollIntoView(row);
        Chosen?.Invoke(row.Name);
    }

    private int Limit(SearchKind kind) => _limits.TryGetValue(kind, out int n) ? n : FirstRows;

    // ---- Opening ----

    /// <summary>Whether what was found is still there to open (another device may have deleted it meanwhile).</summary>
    internal static bool IsLive(Workspace ws, object item) => item switch
    {
        TaskItem t => ws.Now == t || ws.Open.Contains(t) || ws.Done.Contains(t),
        NoteSheet s => ws.Sheets.Contains(s),
        _ => false,
    };

    /// <summary>
    /// Where opening what was found goes: the space to show (null when the one on show shows it
    /// already: All, or its own), its tab, and whether Done unfolds (for a finished task).
    /// </summary>
    internal static (string? Space, string Tab, bool OpenDone) Plan(SpaceView view, Settings settings, object item)
    {
        string space = item is NoteSheet s ? view.SpaceOf(s) : view.SpaceOf((TaskItem)item);
        string? show = view.HasSpaces && !view.IsAll && view.Shown != space ? space : null;
        return item is NoteSheet ? (show, "notes", false) : (show, "tasks", ((TaskItem)item).IsDone && !settings.DoneExpanded);
    }

    // ---- Drawing ----

    private void Draw(int select)
    {
        // About as many characters before a match as leave it in sight (less beside a caption: see Lead).
        double width = ActualWidth > 0 ? ActualWidth : (Parent as FrameworkElement)?.ActualWidth ?? 0;
        _width = width;
        _lead = width > 0 ? Math.Clamp((int)((width - 140) / 7), 12, 60) : 16;
        var rows = new List<SearchRow>();
        AddGroup(rows, SearchKind.Task, "TASKS", _results.Tasks);
        AddGroup(rows, SearchKind.Done, "DONE", _results.Done);
        AddGroup(rows, SearchKind.Note, "NOTES", _results.Notes);
        _rows = rows;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchRow.Group)));
        Results.ItemsSource = view;
        ShowHint();
        if (rows.Count > 0) Select(Math.Min(select, rows.Count - 1));
    }

    private void AddGroup(List<SearchRow> rows, SearchKind kind, string title, List<SearchHit> hits)
    {
        if (hits.Count == 0) return;
        string group = $"{title} · {hits.Count:N0}";
        int limit = Limit(kind);
        foreach (var hit in hits.Take(limit)) rows.Add(RowFor(hit, group));
        int left = hits.Count - limit;
        if (left > 0)
        {
            string text = left <= MoreRows ? $"Show {left:N0} more" : $"Show {MoreRows:N0} more ({left:N0} left)";
            rows.Add(new SearchRow(SearchRowKind.More, group, Excerpt.Plain(text), null, "", text, null, kind));
        }
    }

    private SearchRow RowFor(SearchHit hit, string group)
    {
        string text = hit.Text;
        var (start, end) = Search.OriginalRange(text, hit.At, hit.At + _results.Query.Length);
        string space = _view.HasSpaces ? _view.NameOf(hit.Item is NoteSheet s ? _view.SpaceOf(s) : _view.SpaceOf((TaskItem)hit.Item)) : "";
        string Joined(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
        string In(string name) => space.Length > 0 ? $"{name}, in {space}" : name;

        switch (hit.Kind)
        {
            case SearchKind.Task:
            {
                bool now = hit.Item == _ws.Now;
                string meta = Joined(now ? "Now" : "", space);
                return new SearchRow(SearchRowKind.Task, group, Cut(text, start, end, Lead(meta)), null, meta, In(now ? $"In focus: {Spoken(text)}" : Spoken(text)), hit);
            }
            case SearchKind.Done:
            {
                return new SearchRow(SearchRowKind.Done, group, Cut(text, start, end, Lead(space)), null, space, In($"Done: {Spoken(text)}"), hit);
            }
            default:
            {
                var (titleStart, titleEnd) = TitleLine(text);
                var match = Cut(text, start, end, Lead(space));
                if (start >= titleStart && start < Math.Max(titleEnd, titleStart + 1))
                    return new SearchRow(SearchRowKind.Note, group, match, null, space, In($"Note: {match}"), hit);
                string title = Title(text, titleStart, titleEnd);
                return new SearchRow(SearchRowKind.Note, group, Excerpt.Plain(title), match, space, In($"Note {title}: {match}"), hit);
            }
        }
    }

    /// <summary>
    /// Characters a row shows before its match when a caption ("Now · Work", a space) sits beside its line: the
    /// caption's width (about 6.5 DIP a character, up to its 96) comes off the room first, so the match stays in
    /// sight even at the panel's narrowest.
    /// </summary>
    private int Lead(string meta)
    {
        if (meta.Length == 0 || _width <= 0) return _lead;
        double caption = Math.Min(96, meta.Length * 6.5 + 8);
        return Math.Clamp((int)((_width - 140 - caption) / 7), 6, _lead);
    }

    /// <summary>A task's text as screen readers hear it: all of it, unless it's very long.</summary>
    private static string Spoken(string text) => SpaceView.Shorten(text, 300);

    /// <summary>Where a sheet's title is: its first line with anything on it.</summary>
    private static (int Start, int End) TitleLine(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int end = text.IndexOfAny(['\r', '\n'], i);
            if (end < 0) end = text.Length;
            if (Title(text, i, end).Length > 0) return (i, end);
            i = end + 1;
        }
        return (0, 0);
    }

    /// <summary>A line as a title: without the marks a heading or a list starts with.</summary>
    private static string Title(string text, int start, int end) =>
        text[start..end].Trim().TrimStart('#', '-', '*', '•', ' ').Trim();

    /// <summary>
    /// The match on its line, with a little of the line before it (from a word's start, so the match
    /// stays in sight in a narrow panel) and the rest after it; a cut is marked "…".
    /// </summary>
    internal static Excerpt Cut(string text, int start, int end, int lead = 16, int tail = 160)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int lineEnd = text.IndexOfAny(['\r', '\n'], end);
        if (lineEnd < 0) lineEnd = text.Length;
        while (lineStart < start && text[lineStart] is ' ' or '\t') lineStart++;
        // A few characters more than the lead aren't worth a cut.
        int from = start - lineStart > lead + 4 ? start - lead : lineStart;
        if (from > lineStart)
        {
            int space = text.IndexOf(' ', from, start - from);
            if (space >= 0) from = space + 1;
        }
        if (from > 0 && from < text.Length && char.IsLowSurrogate(text[from])) from--;
        int to = Math.Min(lineEnd, end + tail);
        if (to < text.Length && to > 0 && char.IsHighSurrogate(text[to - 1])) to++;
        return new Excerpt((from > lineStart ? "…" : "") + Flat(text[from..start]), Flat(text[start..end]), Flat(text[end..to]) + (to < lineEnd ? "…" : ""));
    }

    private static string Flat(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private void ShowHint()
    {
        if (_ws == null) return;
        bool searched = _results.Query.Length > 0;
        bool empty = searched && _results.Total == 0;
        Hint.Visibility = !searched || empty ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = searched && !empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            HintTitle.Text = $"Nothing matches “{SpaceView.Shorten(_shownQuery.Trim(), 40)}”";
            HintText.Text = "Search looks at every word of every task and note, whatever its case or accents.";
        }
        else if (!searched)
        {
            HintTitle.Text = "Search tasks and notes";
            HintText.Text = (_view.HasSpaces ? "Open and finished tasks and notes, in every space." : "Open and finished tasks, and every note.")
                + " ↑ ↓ to choose, Enter to open, Esc to close.";
        }
    }
}
