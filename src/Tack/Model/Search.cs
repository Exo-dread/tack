using System.Globalization;
using System.Text;

namespace Tack.Model;

/// <summary>
/// Search (Ctrl+F): what matches. Tack Focus has the same rule (web/src/model/search.ts), held to
/// the same cases (tests/shared/search.json).
///
/// A query matches any task, finished task or note containing it, anywhere in its text (all of a
/// note, not just its title), in every space, whatever its case or accents. Text and query are
/// both folded first, one character at a time:
/// <list type="bullet">
/// <item>a CR LF pair is one line break, and every line break, tab and other space is a plain space;</item>
/// <item>curly quotes are straight ones, dashes and the minus sign a hyphen, an ellipsis three dots;</item>
/// <item>soft hyphens and zero-width characters are left out;</item>
/// <item>ß is ss, æ ae and œ oe; ø ł đ ħ ŧ and ı are their plain letters;</item>
/// <item>anything else is decomposed (NFD), its accents (Mn) dropped, and lower-cased (ς as σ).</item>
/// </list>
/// The query then loses the spaces at either end. A match maps back to the text as typed
/// (<see cref="OriginalRange"/>), so a note can open with it selected.
/// </summary>
public static class Search
{
    private static readonly Dictionary<int, string> Special = BuildSpecial();
    private static readonly Dictionary<int, string> Memo = new();

    private static Dictionary<int, string> BuildSpecial()
    {
        var map = new Dictionary<int, string>();
        void Put(string to, params int[] codes)
        {
            foreach (int c in codes) map[c] = to;
        }
        Put(" ", 0x85, 0xA0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000);
        Put("", 0xAD, 0x200B, 0x200C, 0x200D, 0x2060, 0xFEFF);
        Put("'", 0x2018, 0x2019, 0x201A, 0x201B, 0x2032);
        Put("\"", 0x201C, 0x201D, 0x201E, 0x201F, 0x2033);
        Put("-", 0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015, 0x2212);
        Put("...", 0x2026);
        Put("ss", 0xDF, 0x1E9E);
        Put("ae", 0xE6, 0xC6);
        Put("oe", 0x153, 0x152);
        Put("o", 0xF8, 0xD8);
        Put("l", 0x142, 0x141);
        Put("d", 0x111, 0x110);
        Put("h", 0x127, 0x126);
        Put("t", 0x167, 0x166);
        Put("i", 0x131);
        return map;
    }

    /// <summary>An ASCII character folded: capitals lower-cased, tab and line breaks (and VT, FF) a space.</summary>
    private static char Ascii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c is >= '\t' and <= '\r' ? ' ' : c;

    /// <summary>One character that isn't ASCII (a code point; a lone surrogate stays as it is) folded.</summary>
    private static string FoldChar(int cp)
    {
        if (Memo.TryGetValue(cp, out string? folded)) return folded;
        folded = FoldOther(cp);
        if (Memo.Count < 65_536) Memo[cp] = folded;
        return folded;
    }

    private static string FoldOther(int cp)
    {
        if (Special.TryGetValue(cp, out string? special)) return special;
        if (cp is >= 0xD800 and <= 0xDFFF) return ((char)cp).ToString();
        string ch = char.ConvertFromUtf32(cp), parts;
        try
        {
            parts = ch.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            parts = ch; // a code point Windows can't normalise: as it is (lower-cased)
        }
        var sb = new StringBuilder(2);
        foreach (var part in parts.EnumerateRunes())
        {
            if (part.Value < 0x80)
            {
                sb.Append(Ascii((char)part.Value));
                continue;
            }
            if (Rune.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark) continue;
            if (Special.TryGetValue(part.Value, out string? s))
            {
                sb.Append(s);
                continue;
            }
            var lower = Rune.ToLowerInvariant(part);
            sb.Append(lower.Value == 0x3C2 ? "σ" : lower.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Printable ASCII without capitals folds to itself: most text, at no cost.</summary>
    private static bool IsPlain(string text)
    {
        foreach (char c in text)
            if (c is < ' ' or > '~' or (>= 'A' and <= 'Z')) return false;
        return true;
    }

    /// <summary>The text as search compares it (see above).</summary>
    public static string Fold(string text)
    {
        if (IsPlain(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                sb.Append(' ');
                i += 2;
            }
            else if (c < 0x80)
            {
                sb.Append(Ascii(c));
                i++;
            }
            else
            {
                int n = Width(text, i);
                sb.Append(FoldChar(n == 2 ? char.ConvertToUtf32(c, text[i + 1]) : c));
                i += n;
            }
        }
        return sb.ToString();
    }

    /// <summary>Code units in the character at <paramref name="i"/>: 2 for a surrogate pair, else 1.</summary>
    private static int Width(string text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;

    /// <summary>A query as search looks for it: folded, without the spaces at either end ("" finds nothing).</summary>
    public static string FoldQuery(string query) => Fold(query).Trim(' ');

    /// <summary>Where a query (as <see cref="FoldQuery"/> gives it) first matches in folded text, or -1.</summary>
    public static int MatchAt(string folded, string query) =>
        query.Length == 0 ? -1 : folded.IndexOf(query, StringComparison.Ordinal);

    /// <summary>
    /// The part of <paramref name="text"/> (as typed) that folds to [start, end) of its folded text:
    /// whole characters, with the accents that follow the last one. Folding is done again only up to the match.
    /// </summary>
    public static (int Start, int End) OriginalRange(string text, int start, int end)
    {
        if (!Resizes(text)) return (Math.Min(start, text.Length), Math.Min(end, text.Length));
        int at = 0; // folded length so far
        int from = -1;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            int n = 1, len = 1;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                n = 2;
            }
            else if (c >= 0x80)
            {
                n = Width(text, i);
                len = FoldChar(n == 2 ? char.ConvertToUtf32(c, text[i + 1]) : c).Length;
            }
            if (len > 0)
            {
                if (from >= 0 && at >= end) return (from, i);
                if (from < 0 && at + len > start) from = i;
            }
            at += len;
            i += n;
        }
        return (from < 0 ? text.Length : from, text.Length);
    }

    /// <summary>What can make folded text longer or shorter than the text: anything not ASCII, and CR LF.</summary>
    private static bool Resizes(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x80 || (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')) return true;
        }
        return false;
    }

    /// <summary>Where in <paramref name="text"/> (as typed) a query first matches, or null.</summary>
    public static (int Start, int End)? FindIn(string text, string query)
    {
        string q = FoldQuery(query);
        int at = MatchAt(Fold(text), q);
        return at < 0 ? null : OriginalRange(text, at, at + q.Length);
    }
}

public enum SearchKind { Task, Done, Note }

/// <summary>One task or sheet that matches: <see cref="At"/> is where (in its folded text) the query first does.</summary>
public sealed record SearchHit(SearchKind Kind, object Item, int At)
{
    public TaskItem? Task => Item as TaskItem;

    public NoteSheet? Sheet => Item as NoteSheet;

    /// <summary>The text searched: a task's, or a sheet's (with the TextBox's line breaks).</summary>
    public string Text => Item is NoteSheet s ? s.Text : ((TaskItem)Item).Text;
}

public sealed class SearchResults
{
    public static readonly SearchResults None = new("");

    public SearchResults(string query) => Query = query;

    /// <summary>The query as matched (folded); "" when there's nothing to look for.</summary>
    public string Query { get; }

    /// <summary>The task in focus, then the open list in its order.</summary>
    public List<SearchHit> Tasks { get; } = new();

    /// <summary>Finished tasks, most recent first.</summary>
    public List<SearchHit> Done { get; } = new();

    /// <summary>Sheets, in order.</summary>
    public List<SearchHit> Notes { get; } = new();

    public int Total => Tasks.Count + Done.Count + Notes.Count;
}

/// <summary>
/// Searches a workspace: every space, open and finished tasks and notes. Folded text is kept per
/// task and sheet until its text changes, so typing a query (even into ten thousand tasks and
/// hundreds of long notes) only compares.
/// </summary>
public sealed class SearchIndex
{
    private Dictionary<object, (string Text, string Folded)> _folded = new(ReferenceEqualityComparer.Instance);

    public SearchResults Find(Workspace ws, string raw)
    {
        string query = Search.FoldQuery(raw);
        var results = new SearchResults(query);
        if (query.Length == 0) return results;
        var kept = new Dictionary<object, (string Text, string Folded)>(_folded.Count, ReferenceEqualityComparer.Instance);
        void Look(SearchKind kind, object item, string text, List<SearchHit> into)
        {
            if (!_folded.TryGetValue(item, out var entry) || !string.Equals(entry.Text, text, StringComparison.Ordinal))
                entry = (text, Search.Fold(text));
            kept[item] = entry;
            int at = Search.MatchAt(entry.Folded, query);
            if (at >= 0) into.Add(new SearchHit(kind, item, at));
        }
        if (ws.Now is { } now) Look(SearchKind.Task, now, now.Text, results.Tasks);
        foreach (var t in ws.Open) Look(SearchKind.Task, t, t.Text, results.Tasks);
        foreach (var t in ws.Done) Look(SearchKind.Done, t, t.Text, results.Done);
        foreach (var s in ws.Sheets) Look(SearchKind.Note, s, s.Text, results.Notes);
        // What's gone (deleted, or never asked about again) is let go.
        _folded = kept;
        return results;
    }
}
