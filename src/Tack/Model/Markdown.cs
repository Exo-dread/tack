using System.Text;

namespace Tack.Model;

// The tree a note's Markdown reads as (docs/NOTES-MARKDOWN.md). Besides what the shared cases hold
// (tests/vectors/markdown.json), blocks, items and line breaks know where their line starts in the
// note's text, so a click on the formatted note can put the caret there.

/// <summary>A block of a note: where its first line starts in the text (<see cref="Line"/>).</summary>
public abstract record MdBlock(int Line);

/// <summary><c>#</c>, <c>##</c>, <c>###</c> (and deeper, as 3).</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content, int Line) : MdBlock(Line);

/// <summary>Consecutive lines, each line break kept (<see cref="MdBreak"/>).</summary>
public sealed record MdParagraph(IReadOnlyList<MdInline> Content, int Line) : MdBlock(Line);

/// <summary>A bulleted (<see cref="Ordered"/> false) or numbered list; numbered ones start at <see cref="Start"/>.</summary>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdItem> Items, int Line) : MdBlock(Line);

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks, int Line) : MdBlock(Line);

/// <summary>A fenced code block: its lines joined with "\n"; <see cref="Lines"/> are where each starts in the text.</summary>
public sealed record MdCodeBlock(string Text, IReadOnlyList<int> Lines, int Line) : MdBlock(Line);

public sealed record MdRule(int Line) : MdBlock(Line);

/// <summary>
/// A list item. <see cref="Task"/> is null unless it has a checkbox (true: ticked); <see cref="At"/> is then where the
/// character between its brackets is in the text. <see cref="Sub"/> holds what's nested in it (lists, and lines
/// that carry on the item after one).
/// </summary>
public sealed record MdItem(IReadOnlyList<MdInline> Content, bool? Task, int? At, IReadOnlyList<MdBlock> Sub, int Line);

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public enum MdStyleKind { Bold, Italic, Struck }

public sealed record MdStyle(MdStyleKind Kind, IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdCode(string Text) : MdInline;

/// <summary><c>[text](url)</c>: shown as its text, never opened (the url is only ever shown as text).</summary>
public sealed record MdLink(IReadOnlyList<MdInline> Content, string Url) : MdInline;

/// <summary>A line break; <see cref="Line"/> is where the next line starts in the text.</summary>
public sealed record MdBreak(int Line) : MdInline;

/// <summary>
/// Reads a note's Markdown into <see cref="MdBlock"/>s: the same rules and results as Tack Focus
/// (web/src/model/markdown.ts), held to the shared cases in tests/vectors/markdown.json. Pure, and linear in
/// the text: no regular expressions, no backtracking. Every line is looked at a bounded number of times (quotes
/// nest at most <see cref="MaxQuoteDepth"/> deep, lists <see cref="MaxListDepth"/>); inline delimiters are matched
/// with CommonMark's delimiter stack and a floor per character, code spans find their closer through an index of
/// backtick runs, links theirs through a table of the next ')'. Inline formatting is read line by line.
/// </summary>
public static class Markdown
{
    /// <summary>Inline formatting (bold, italic, struck, links) nests at most this deep; deeper delimiters are plain text.</summary>
    public const int MaxInlineDepth = 8;

    /// <summary>Lists nest at most this deep: an item indented further joins the deepest list.</summary>
    public const int MaxListDepth = 8;

    /// <summary>Quotes nest at most this deep: past it, <c>&gt;</c> is plain text.</summary>
    public const int MaxQuoteDepth = 8;

    /// <summary>A note longer than this is never formatted (it stays the editor).</summary>
    public const int MaxFormatted = 200_000;

    public static IReadOnlyList<MdBlock> Parse(string text) => new BlockReader(text).Run();

    private static bool IsWs(char c) => c == ' ' || c == '\t';

    private static int TrimEnd(string s, int a, int b)
    {
        while (b > a && IsWs(s[b - 1])) b--;
        return b;
    }

    private static int TrimStart(string s, int a, int b)
    {
        while (a < b && IsWs(s[a])) a++;
        return a;
    }

    /// <summary>A line of the note, or of a quote's content: its text is [A, B); <see cref="Raw"/> is where the whole line starts.</summary>
    private readonly record struct Line(int A, int B, int Raw);

    private readonly record struct Marker(bool Ordered, int Start, int W, int TextCol, int TextP, bool? Task, int At);

    // Blocks being built: what the list reader adds to as it goes.
    private sealed class ItemBuilder(List<MdInline> c, bool? task, int? at, int line)
    {
        public readonly List<MdInline> C = c;
        public readonly List<object> Sub = new(); // MdBlock, ListBuilder or ParaBuilder
        public MdItem Build() => new(C, task, at, Sub.Select(Finish).ToList(), line);
    }

    private sealed class ListBuilder(bool ordered, int start, int line)
    {
        public readonly bool Ordered = ordered;
        public readonly List<ItemBuilder> Items = new();
        public MdList Build() => new(Ordered, Ordered ? start : 0, Items.Select(i => i.Build()).ToList(), line);
    }

    private sealed class ParaBuilder(List<MdInline> c, int line)
    {
        public readonly List<MdInline> C = c;
        public MdParagraph Build() => new(C, line);
    }

    private static MdBlock Finish(object b) => b switch
    {
        ListBuilder l => l.Build(),
        ParaBuilder p => p.Build(),
        _ => (MdBlock)b,
    };

    private sealed class BlockReader(string s)
    {
        private readonly string _s = s;

        public List<MdBlock> Run()
        {
            var lines = new List<Line>();
            int a = 0;
            for (int i = 0; i < _s.Length; i++)
            {
                char c = _s[i];
                if (c != '\n' && c != '\r') continue;
                lines.Add(new Line(a, i, a));
                if (c == '\r' && i + 1 < _s.Length && _s[i + 1] == '\n') i++;
                a = i + 1;
            }
            lines.Add(new Line(a, _s.Length, a));
            return Blocks(lines, 0);
        }

        /// <summary>The first non-space of a line, and its column (a tab counts 4).</summary>
        private (int P, int W) Indent(Line l)
        {
            int p = l.A, w = 0;
            while (p < l.B)
            {
                char c = _s[p];
                if (c == ' ') w += 1;
                else if (c == '\t') w += 4;
                else break;
                p++;
            }
            return (p, w);
        }

        private List<MdBlock> Blocks(List<Line> lines, int quoteDepth)
        {
            var output = new List<MdBlock>();
            int i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                var (p, _) = Indent(line);
                if (p == line.B)
                {
                    i++;
                    continue;
                }
                if (Fence(line, p) is { } fence)
                {
                    i = Code(lines, i, fence, output);
                    continue;
                }
                if (IsQuote(line, p, quoteDepth))
                {
                    var inner = new List<Line>();
                    while (i < lines.Count)
                    {
                        var l = lines[i];
                        int q = Indent(l).P;
                        if (!IsQuote(l, q, quoteDepth)) break;
                        int from = q + 1 < l.B && _s[q + 1] == ' ' ? q + 2 : q + 1;
                        inner.Add(new Line(from, l.B, l.Raw));
                        i++;
                    }
                    output.Add(new MdQuote(Blocks(inner, quoteDepth + 1), line.Raw));
                    continue;
                }
                if (Heading(line, p) is { } heading)
                {
                    output.Add(heading);
                    i++;
                    continue;
                }
                if (IsRule(line, p))
                {
                    output.Add(new MdRule(line.Raw));
                    i++;
                    continue;
                }
                if (Marker(line) != null)
                {
                    i = List(lines, i, output);
                    continue;
                }
                // A paragraph: every line until a blank one or another block, each line break kept.
                var c = new List<MdInline>();
                int count = 0;
                while (i < lines.Count)
                {
                    var l = lines[i];
                    int q = Indent(l).P;
                    if (q == l.B) break;
                    if (count > 0 && StartsBlock(l, q, quoteDepth)) break;
                    if (count > 0) c.Add(new MdBreak(l.Raw));
                    c.AddRange(Inline(q, TrimEnd(_s, q, l.B)));
                    count++;
                    i++;
                }
                output.Add(new MdParagraph(c, line.Raw));
            }
            return output;
        }

        private bool StartsBlock(Line l, int p, int quoteDepth) =>
            Fence(l, p) != null || IsQuote(l, p, quoteDepth) || Heading(l, p) != null || IsRule(l, p) || Marker(l) != null;

        private bool IsQuote(Line l, int p, int quoteDepth) => quoteDepth < MaxQuoteDepth && p < l.B && _s[p] == '>';

        /// <summary>A fence's character and length, if the line opens a code block.</summary>
        private (char Ch, int N)? Fence(Line l, int p)
        {
            if (p >= l.B) return null;
            char ch = _s[p];
            if (ch != '`' && ch != '~') return null;
            int q = p;
            while (q < l.B && _s[q] == ch) q++;
            int n = q - p;
            if (n < 3) return null;
            // A backtick fence's language word can't hold a backtick (``` a ``` is inline code).
            if (ch == '`')
            {
                for (int k = q; k < l.B; k++) if (_s[k] == '`') return null;
            }
            return (ch, n);
        }

        private int Code(List<Line> lines, int i, (char Ch, int N) fence, List<MdBlock> output)
        {
            int at = lines[i].Raw;
            var text = new StringBuilder();
            var starts = new List<int>();
            i++;
            while (i < lines.Count)
            {
                var l = lines[i];
                var (p, _) = Indent(l);
                int q = p;
                while (q < l.B && _s[q] == fence.Ch) q++;
                if (q - p >= fence.N && TrimEnd(_s, q, l.B) == q)
                {
                    i++;
                    break;
                }
                if (starts.Count > 0) text.Append('\n');
                text.Append(_s, l.A, l.B - l.A);
                starts.Add(l.Raw);
                i++;
            }
            output.Add(new MdCodeBlock(text.ToString(), starts, at));
            return i;
        }

        private MdHeading? Heading(Line l, int p)
        {
            int q = p;
            while (q < l.B && _s[q] == '#') q++;
            int n = q - p;
            if (n < 1 || n > 6 || q >= l.B || !IsWs(_s[q])) return null;
            int end = TrimEnd(_s, q, l.B);
            // A closing run of # goes, if a space comes before it (or it's all there is).
            int k = end;
            while (k > q && _s[k - 1] == '#') k--;
            if (k < end && (k == q || IsWs(_s[k - 1]))) end = TrimEnd(_s, q, k);
            int start = TrimStart(_s, q, end);
            return new MdHeading(Math.Min(n, 3), Inline(start, end), l.Raw);
        }

        /// <summary>3+ of the same -, * or _ with nothing else but spaces.</summary>
        private bool IsRule(Line l, int p)
        {
            if (p >= l.B) return false;
            char ch = _s[p];
            if (ch != '-' && ch != '*' && ch != '_') return false;
            int n = 0;
            for (int k = p; k < l.B; k++)
            {
                char c = _s[k];
                if (c == ch) n++;
                else if (!IsWs(c)) return false;
            }
            return n >= 3;
        }

        /// <summary>A list item's marker (and box, for a task), if the line is an item.</summary>
        private Marker? Marker(Line l)
        {
            var (p, w) = Indent(l);
            if (p >= l.B || IsRule(l, p)) return null;
            char c = _s[p];
            bool ordered;
            int start = 0, q;
            if (c == '-' || c == '*' || c == '+')
            {
                ordered = false;
                q = p + 1;
            }
            else if (c >= '0' && c <= '9')
            {
                q = p;
                while (q < l.B && q - p < 10 && _s[q] >= '0' && _s[q] <= '9') q++;
                int digits = q - p;
                if (digits > 9 || q >= l.B || (_s[q] != '.' && _s[q] != ')')) return null;
                ordered = true;
                for (int k = p; k < q; k++) start = start * 10 + (_s[k] - '0');
                q++;
            }
            else
            {
                return null;
            }
            if (q >= l.B || _s[q] != ' ') return null;
            int col = w + (q - p) + 1;
            int t = q + 1;
            while (t < l.B)
            {
                char ch = _s[t];
                if (ch == ' ') col += 1;
                else if (ch == '\t') col += 4;
                else break;
                t++;
            }
            bool? task = null;
            int at = -1, textP = t;
            if (t + 2 < l.B && _s[t] == '[' && _s[t + 2] == ']')
            {
                char box = _s[t + 1];
                int after = t + 3;
                if ((box == ' ' || box == 'x' || box == 'X') && (after == l.B || _s[after] == ' '))
                {
                    task = box != ' ';
                    at = t + 1;
                    textP = TrimStart(_s, after, l.B);
                }
            }
            return new Marker(ordered, start, w, col, textP, task, at);
        }

        private sealed class OpenList(ListBuilder list, int ind, int textCol)
        {
            public readonly ListBuilder List = list;
            public int Ind = ind;
            public int TextCol = textCol;
        }

        /// <summary>A list and everything nested in it, from lines[i] (an item). Returns the next line to read.</summary>
        private int List(List<Line> lines, int i, List<MdBlock> output)
        {
            var first = Marker(lines[i])!.Value;
            var stack = new List<OpenList>();
            ItemBuilder NewItem(Marker m, Line l) =>
                new(Inline(m.TextP, TrimEnd(_s, m.TextP, l.B)), m.Task, m.Task == null ? null : m.At, l.Raw);
            ListBuilder Open(Marker m, Line l)
            {
                var list = new ListBuilder(m.Ordered, m.Start, l.Raw);
                list.Items.Add(NewItem(m, l));
                stack.Add(new OpenList(list, m.W, m.TextCol));
                return list;
            }
            var top = Open(first, lines[i]);
            i++;
            while (i < lines.Count)
            {
                var l = lines[i];
                var (p, w) = Indent(l);
                if (p == l.B)
                {
                    i++; // a blank line ends nothing by itself
                    continue;
                }
                if (Marker(l) is { } m)
                {
                    // Down the open lists while the marker is 2+ columns right of that level's last item's.
                    int k = 0;
                    while (k + 1 < stack.Count && m.W >= stack[k].Ind + 2) k++;
                    bool deeper = m.W >= stack[k].Ind + 2 && k == stack.Count - 1;
                    if (deeper && stack.Count < MaxListDepth)
                    {
                        var parent = stack[k].List;
                        parent.Items[^1].Sub.Add(Open(m, l));
                        i++;
                        continue;
                    }
                    stack.RemoveRange(k + 1, stack.Count - k - 1);
                    var level = stack[k];
                    if (level.List.Ordered == m.Ordered)
                    {
                        level.List.Items.Add(NewItem(m, l));
                        level.Ind = m.W;
                        level.TextCol = m.TextCol;
                    }
                    else if (k == 0)
                    {
                        break; // another kind of list starts here
                    }
                    else
                    {
                        stack.RemoveAt(k);
                        var parent = stack[k - 1].List;
                        parent.Items[^1].Sub.Add(Open(m, l));
                    }
                    i++;
                    continue;
                }
                // Not an item: it carries on the deepest open item whose text it's indented to, or ends the list.
                int d = stack.Count - 1;
                while (d >= 0 && w < stack[d].TextCol) d--;
                if (d < 0) break;
                stack.RemoveRange(d + 1, stack.Count - d - 1);
                var item = stack[d].List.Items[^1];
                var content = Inline(p, TrimEnd(_s, p, l.B));
                if (item.Sub.Count == 0)
                {
                    item.C.Add(new MdBreak(l.Raw));
                    item.C.AddRange(content);
                }
                else if (item.Sub[^1] is ParaBuilder para)
                {
                    para.C.Add(new MdBreak(l.Raw));
                    para.C.AddRange(content);
                }
                else
                {
                    item.Sub.Add(new ParaBuilder(content, l.Raw));
                }
                i++;
            }
            output.Add(top.Build());
            return i;
        }

        private List<MdInline> Inline(int a, int b) => new InlineReader(_s, a, b).Run();
    }

    /// <summary>
    /// The inline content of one line: CommonMark's delimiter stack ("process emphasis", without the rule of 3),
    /// so each closer is matched once and a failed search never repeats, in time linear in the line.
    /// </summary>
    private sealed class InlineReader(string s, int a, int b)
    {
        private readonly string _s = s;
        private readonly int _a = a, _b = b;
        private Node? _head, _tail;
        private Delim? _delims; // top of the delimiter stack
        private Bracket? _brackets;
        private int _seq;
        private int _linkedBelow = -1; // brackets opened before this one can't make links (a link holds no link)
        private Dictionary<int, (List<int> At, int Next)>? _runs; // backtick runs by length, read forwards only
        private int[]? _parens; // for each index, the next ')' at or after it (-1: none)

        /// <summary>A piece of the line being read: text (a delimiter run's is its characters) or a finished element.</summary>
        private sealed class Node
        {
            public string? Text;
            public MdInline? Element;
            public int Depth; // how deep the formatting in it nests (text: 0)
            public Node? Prev, Next;
        }

        private sealed class Delim
        {
            public required Node Node;
            public char Ch;
            public int N;
            public bool Open, Close;
            public Delim? Prev, Next;
        }

        private sealed class Bracket
        {
            public required Node Node;
            public Delim? Bottom; // the delimiter on top when it opened: emphasis inside the link stops there
            public int Seq;
            public Bracket? Prev;
        }

        private static bool IsPunct(char c) => c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

        /// <summary>Letters and digits, for _ (snake_case stays plain): ASCII ones, and anything past ASCII but a space.</summary>
        private static bool IsWord(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') || (c >= 128 && c != ' ' && c != '　');

        public List<MdInline> Run()
        {
            int i = _a, textFrom = i;
            void Flush(int to)
            {
                if (to > textFrom) Append(_s[textFrom..to]);
            }
            while (i < _b)
            {
                char c = _s[i];
                if (c == '\\')
                {
                    // \ before ASCII punctuation: that character, literally.
                    if (i + 1 < _b && IsPunct(_s[i + 1]))
                    {
                        Flush(i);
                        Append(_s[i + 1].ToString());
                        i += 2;
                        textFrom = i;
                    }
                    else
                    {
                        i++;
                    }
                }
                else if (c == '`')
                {
                    Flush(i);
                    i = CodeSpan(i);
                    textFrom = i;
                }
                else if (c == '*' || c == '_' || c == '~')
                {
                    Flush(i);
                    i = DelimRun(i, c);
                    textFrom = i;
                }
                else if (c == '[')
                {
                    Flush(i);
                    var node = Append("[");
                    _brackets = new Bracket { Node = node, Bottom = _delims, Seq = _seq++, Prev = _brackets };
                    i++;
                    textFrom = i;
                }
                else if (c == ']')
                {
                    Flush(i);
                    i = CloseBracket(i);
                    textFrom = i;
                }
                else
                {
                    i++;
                }
            }
            Flush(_b);
            Emphasis(null);
            return Collect(_head, null);
        }

        private Node Append(string text) => Append(new Node { Text = text });

        private Node Append(Node node)
        {
            node.Prev = _tail;
            if (_tail != null) _tail.Next = node;
            else _head = node;
            _tail = node;
            return node;
        }

        private int CodeSpan(int i)
        {
            int q = i;
            while (q < _b && _s[q] == '`') q++;
            int n = q - i;
            int close = NextRun(n, q);
            if (close < 0)
            {
                Append(_s[i..q]);
                return q;
            }
            string content = _s[q..close];
            if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.AsSpan().IndexOfAnyExcept(' ') >= 0)
                content = content[1..^1];
            Append(new Node { Element = new MdCode(content) });
            return close + n;
        }

        /// <summary>Where the next run of exactly <paramref name="n"/> backticks starts at or after <paramref name="from"/> (-1: none).</summary>
        private int NextRun(int n, int from)
        {
            if (_runs == null)
            {
                _runs = new();
                int k = _a;
                while (k < _b)
                {
                    if (_s[k] != '`')
                    {
                        k++;
                        continue;
                    }
                    int e = k;
                    while (e < _b && _s[e] == '`') e++;
                    if (!_runs.TryGetValue(e - k, out var r)) r = (new List<int>(), 0);
                    r.At.Add(k);
                    _runs[e - k] = r;
                    k = e;
                }
            }
            if (!_runs.TryGetValue(n, out var run)) return -1;
            int next = run.Next;
            while (next < run.At.Count && run.At[next] < from) next++;
            _runs[n] = (run.At, next);
            return next < run.At.Count ? run.At[next] : -1;
        }

        private int DelimRun(int i, char c)
        {
            int q = i;
            while (q < _b && _s[q] == c) q++;
            int n = q - i;
            var node = Append(_s[i..q]);
            if (c == '~' && n != 2) return q;
            int before = i > _a ? _s[i - 1] : -1, after = q < _b ? _s[q] : -1;
            bool open = after >= 0 && !IsWs((char)after), close = before >= 0 && !IsWs((char)before);
            if (c == '_')
            {
                open &= !(before >= 0 && IsWord((char)before));
                close &= !(after >= 0 && IsWord((char)after));
            }
            if (open || close)
            {
                var d = new Delim { Node = node, Ch = c, N = n, Open = open, Close = close, Prev = _delims };
                if (_delims != null) _delims.Next = d;
                _delims = d;
            }
            return q;
        }

        private int CloseBracket(int i)
        {
            var opener = _brackets;
            if (opener == null)
            {
                Append("]");
                return i + 1;
            }
            _brackets = opener.Prev;
            if (opener.Seq < _linkedBelow || i + 1 >= _b || _s[i + 1] != '(')
            {
                Append("]");
                return i + 1;
            }
            int close = NextParen(i + 2);
            if (close < 0)
            {
                Append("]");
                return i + 1;
            }
            // Emphasis inside the link text first, then the link holds it.
            Emphasis(opener.Bottom);
            int depth = 0;
            for (var n = opener.Node.Next; n != null; n = n.Next) depth = Math.Max(depth, n.Depth);
            if (depth + 1 > MaxInlineDepth)
            {
                Append("]");
                return i + 1;
            }
            var link = new MdLink(Collect(opener.Node.Next, null), _s[TrimStart(_s, i + 2, close)..TrimEnd(_s, i + 2, close)]);
            // The '[' node becomes the link; everything after it is inside.
            opener.Node.Text = null;
            opener.Node.Element = link;
            opener.Node.Depth = depth + 1;
            opener.Node.Next = null;
            _tail = opener.Node;
            _linkedBelow = opener.Seq;
            return close + 1;
        }

        private int NextParen(int from)
        {
            if (_parens == null)
            {
                _parens = new int[_b - _a + 1];
                int next = -1;
                _parens[_b - _a] = -1;
                for (int k = _b - 1; k >= _a; k--)
                {
                    if (_s[k] == ')') next = k;
                    _parens[k - _a] = next;
                }
            }
            return from > _b ? -1 : _parens[from - _a];
        }

        private void RemoveDelim(Delim d)
        {
            if (d.Prev != null) d.Prev.Next = d.Next;
            if (d.Next != null) d.Next.Prev = d.Prev;
            else _delims = d.Prev;
        }

        private void RemoveNode(Node n)
        {
            if (n.Prev != null) n.Prev.Next = n.Next;
            else _head = n.Next;
            if (n.Next != null) n.Next.Prev = n.Prev;
            else _tail = n.Prev;
        }

        /// <summary>Matches the delimiters above <paramref name="bottom"/> (CommonMark's "process emphasis", without the rule of 3).</summary>
        private void Emphasis(Delim? bottom)
        {
            // The first delimiter above bottom.
            var closer = _delims;
            if (closer == bottom) return;
            while (closer != null && closer.Prev != bottom) closer = closer.Prev;
            Delim? floorStar = bottom, floorUnder = bottom, floorTilde = bottom;
            while (closer != null)
            {
                if (!closer.Close)
                {
                    closer = closer.Next;
                    continue;
                }
                var stop = closer.Ch == '*' ? floorStar : closer.Ch == '_' ? floorUnder : floorTilde;
                var opener = closer.Prev;
                while (opener != null && opener != bottom && opener != stop && !(opener.Ch == closer.Ch && opener.Open)) opener = opener.Prev;
                bool matched = false;
                if (opener != null && opener != bottom && opener != stop)
                {
                    int use = closer.Ch == '~' ? 2 : closer.N >= 2 && opener.N >= 2 ? 2 : 1;
                    int depth = 0;
                    for (var n = opener.Node.Next; n != null && n != closer.Node; n = n.Next) depth = Math.Max(depth, n.Depth);
                    if (depth + 1 <= MaxInlineDepth)
                    {
                        matched = true;
                        var inner = Collect(opener.Node.Next, closer.Node);
                        MdInline element = new MdStyle(closer.Ch == '~' ? MdStyleKind.Struck : use == 2 ? MdStyleKind.Bold : MdStyleKind.Italic, inner);
                        var node = new Node { Element = element, Depth = depth + 1, Prev = opener.Node, Next = closer.Node };
                        opener.Node.Next = node;
                        closer.Node.Prev = node;
                        // Delimiters between them are plain text now.
                        opener.Next = closer;
                        closer.Prev = opener;
                        opener.N -= use;
                        closer.N -= use;
                        opener.Node.Text = opener.Node.Text![use..];
                        closer.Node.Text = closer.Node.Text![use..];
                        if (opener.N == 0)
                        {
                            RemoveNode(opener.Node);
                            RemoveDelim(opener);
                        }
                        if (closer.N == 0)
                        {
                            var next = closer.Next;
                            RemoveNode(closer.Node);
                            RemoveDelim(closer);
                            closer = next;
                        }
                    }
                }
                if (!matched)
                {
                    if (closer!.Ch == '*') floorStar = closer.Prev;
                    else if (closer.Ch == '_') floorUnder = closer.Prev;
                    else floorTilde = closer.Prev;
                    var next = closer.Next;
                    if (!closer.Open) RemoveDelim(closer);
                    closer = next;
                }
            }
            // Whatever is left above bottom is plain text.
            while (_delims != null && _delims != bottom) RemoveDelim(_delims);
        }

        /// <summary>The nodes from <paramref name="from"/> up to (not including) <paramref name="to"/>, as inline content: adjacent text as one string.</summary>
        private static List<MdInline> Collect(Node? from, Node? to)
        {
            var output = new List<MdInline>();
            StringBuilder? text = null;
            for (var n = from; n != null && n != to; n = n.Next)
            {
                if (n.Text != null)
                {
                    (text ??= new StringBuilder()).Append(n.Text);
                    continue;
                }
                if (text is { Length: > 0 })
                {
                    output.Add(new MdText(text.ToString()));
                    text.Clear();
                }
                output.Add(n.Element!);
            }
            if (text is { Length: > 0 }) output.Add(new MdText(text.ToString()));
            return output;
        }
    }
}
