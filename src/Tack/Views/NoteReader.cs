using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Tack.Model;

namespace Tack.Views;

/// <summary>What a row of the formatted note is.</summary>
internal enum RowKind { Text, Heading, Code, Rule }

/// <summary>What sits in a row's margin, before its text.</summary>
internal enum RowMarker { None, Bullet, Number, Box }

/// <summary>
/// One line of a note as the formatted view shows it: every row is exactly one line of the text (<see cref="Line"/> is where
/// it starts), so a click on a row is a click on that line. Blank lines show as the gap above the next row.
/// </summary>
internal sealed class NoteRow
{
    public int Line { get; init; }
    public RowKind Kind { get; init; }
    public IReadOnlyList<MdInline> Content { get; init; } = [];
    public string Code { get; init; } = "";
    public int Level { get; init; }
    /// <summary>How many quotes it's in.</summary>
    public int Quote { get; init; }
    /// <summary>Where its text starts (lists indent it), from the quotes' edge.</summary>
    public double Left { get; init; }
    public RowMarker Marker { get; init; }
    public double MarkerLeft { get; init; }
    public double MarkerWidth { get; init; }
    public int Number { get; init; }
    public int ListLevel { get; init; }
    /// <summary>The checkbox before an item's text in a numbered list (a bulleted one has it in the margin).</summary>
    public bool BoxInline { get; init; }
    /// <summary>The list item this row starts (its checkbox ticks it), or null.</summary>
    public MdItem? Item { get; init; }
    /// <summary>A line of a ticked item: struck through and muted.</summary>
    public bool Done { get; init; }
    /// <summary>Space above it: what separates blocks (and list items).</summary>
    public double Gap { get; init; }
    public bool CodeFirst { get; init; }
    public bool CodeLast { get; init; }

    /// <summary>The whole item's text for an item's first row, the row's own otherwise: what a screen reader names it by.</summary>
    public string Name { get; init; } = "";
}

/// <summary>
/// A note's Markdown tree (<see cref="Markdown"/>) as rows (<see cref="NoteRow"/>): one per line of the text, with the gaps,
/// indents, markers and quote bars that make the blocks.
/// </summary>
internal static class NoteRows
{
    internal const double BulletWidth = 18;
    internal const double BoxWidth = 22;
    internal const double QuoteIndent = 14;

    public static List<NoteRow> Build(IReadOnlyList<MdBlock> blocks)
    {
        var rows = new List<NoteRow>();
        AddBlocks(rows, blocks, quote: 0, left: 0, listLevel: 0, firstGap: 0);
        return rows;
    }

    private static double GapBefore(MdBlock b) => b switch
    {
        MdHeading { Level: 1 } => 14,
        MdHeading => 10,
        _ => 8,
    };

    private static void AddBlocks(List<NoteRow> rows, IReadOnlyList<MdBlock> blocks, int quote, double left, int listLevel, double firstGap)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            double gap = b == 0 ? firstGap : GapBefore(block);
            if (rows.Count == 0) gap = 0;
            switch (block)
            {
                case MdHeading h:
                    rows.Add(new NoteRow { Line = h.Line, Kind = RowKind.Heading, Level = h.Level, Content = h.Content, Quote = quote, Left = left, Gap = gap, Name = Plain(h.Content) });
                    break;
                case MdParagraph p:
                    foreach (var (line, content) in Lines(p.Content, p.Line))
                    {
                        rows.Add(new NoteRow { Line = line, Content = content, Quote = quote, Left = left, Gap = gap, Name = Plain(content) });
                        gap = 0;
                    }
                    break;
                case MdCodeBlock c:
                {
                    string[] lines = c.Text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        rows.Add(new NoteRow
                        {
                            Line = c.Lines.Count > i ? c.Lines[i] : c.Line, Kind = RowKind.Code, Code = lines[i], Quote = quote, Left = left,
                            Gap = i == 0 ? gap : 0, CodeFirst = i == 0, CodeLast = i == lines.Length - 1, Name = lines[i],
                        });
                    }
                    break;
                }
                case MdRule r:
                    rows.Add(new NoteRow { Line = r.Line, Kind = RowKind.Rule, Quote = quote, Left = left, Gap = gap });
                    break;
                case MdQuote q:
                    if (q.Blocks.Count == 0) rows.Add(new NoteRow { Line = q.Line, Quote = quote + 1, Left = left, Gap = gap });
                    else AddBlocks(rows, q.Blocks, quote + 1, left, listLevel, gap);
                    break;
                case MdList l:
                    AddList(rows, l, quote, left, listLevel + 1, gap);
                    break;
            }
        }
    }

    private static void AddList(List<NoteRow> rows, MdList list, int quote, double left, int level, double gap)
    {
        bool boxes = !list.Ordered && list.Items.Any(i => i.Task != null);
        double markerWidth = list.Ordered
            ? Math.Max(BulletWidth, 7.5 * (list.Start + list.Items.Count - 1).ToString(CultureInfo.InvariantCulture).Length + 9)
            : boxes ? BoxWidth : BulletWidth;
        double textLeft = left + markerWidth;
        int number = list.Start;
        for (int k = 0; k < list.Items.Count; k++)
        {
            var item = list.Items[k];
            bool done = item.Task == true;
            bool inline = list.Ordered && item.Task != null;
            var lines = Lines(item.Content, item.Line);
            for (int i = 0; i < lines.Count; i++)
            {
                rows.Add(new NoteRow
                {
                    Line = lines[i].Line,
                    Content = lines[i].Content,
                    Quote = quote,
                    Left = textLeft + (inline ? BoxWidth : 0),
                    Marker = i > 0 ? RowMarker.None : list.Ordered ? RowMarker.Number : item.Task != null ? RowMarker.Box : RowMarker.Bullet,
                    MarkerLeft = left,
                    MarkerWidth = markerWidth,
                    Number = number,
                    ListLevel = level,
                    BoxInline = i == 0 && inline,
                    Item = i == 0 ? item : null,
                    Done = done,
                    Gap = i > 0 ? 0 : k == 0 ? gap : 3,
                    Name = i == 0 ? Plain(item.Content) : Plain(lines[i].Content),
                });
            }
            number++;
            AddBlocks(rows, item.Sub, quote, textLeft + (inline ? BoxWidth : 0), level, firstGap: 3);
        }
    }

    /// <summary>Inline content split at its line breaks: each line with where it starts in the text.</summary>
    private static List<(int Line, IReadOnlyList<MdInline> Content)> Lines(IReadOnlyList<MdInline> content, int first)
    {
        var lines = new List<(int, IReadOnlyList<MdInline>)>();
        var current = new List<MdInline>();
        int line = first;
        foreach (var x in content)
        {
            if (x is MdBreak br)
            {
                lines.Add((line, current));
                current = new List<MdInline>();
                line = br.Line;
            }
            else current.Add(x);
        }
        lines.Add((line, current));
        return lines;
    }

    /// <summary>Plain text of inline content (a line break as a space): what a screen reader names a row by.</summary>
    public static string Plain(IReadOnlyList<MdInline> inlines)
    {
        var sb = new StringBuilder();
        void Add(IReadOnlyList<MdInline> xs)
        {
            foreach (var x in xs)
            {
                switch (x)
                {
                    case MdText t: sb.Append(t.Text); break;
                    case MdCode c: sb.Append(c.Text); break;
                    case MdStyle s: Add(s.Content); break;
                    case MdLink l: Add(l.Content); break;
                    case MdBreak: sb.Append(' '); break;
                }
            }
        }
        Add(inlines);
        return sb.ToString().Trim();
    }
}

/// <summary>
/// The formatted note: its rows (<see cref="NoteRow"/>) in a light panel that scrolls itself and makes only the rows in
/// view, so a 20 KB note shows as quickly as a short one (a FlowDocument laid every line of it out: 230-470 ms at 20 KB, in
/// software at 150%). Rows not yet seen have an estimated height until they are. Nothing in it opens anything: a link is
/// styled text with its url as a tooltip (synced text never becomes something to launch).
/// </summary>
internal sealed class NoteReader : FrameworkElement, IScrollInfo
{
    private const double Wheel = 48;
    private const double Arrow = 18;

    private readonly VisualCollection _children;
    private readonly Dictionary<int, RowView> _views = new();
    private List<NoteRow> _rows = [];
    private double[] _heights = [];
    private bool[] _measured = [];
    private double[]? _tops; // where each row starts, from the heights (made again when they change)
    private double _width = double.NaN;
    private Size _viewport;
    private int _anchor; // the row at the top of the view, and how far into it the view starts
    private double _delta;
    private List<(int Row, double Y)> _shown = new();
    private (double, double, double) _told;

    public NoteReader()
    {
        _children = new VisualCollection(this);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        // Rows' text inherits this; only quotes, done items and markers set their own.
        SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
    }

    /// <summary>What every row is made with, looked up once each time the note is read.</summary>
    internal sealed record Look(FontFamily Ui, FontFamily Display, double Size, Style? Check);

    private Look? _look;

    private Look Fonts => _look ??= new Look(
        TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
        TryFindResource("DisplayFont") as FontFamily ?? TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"),
        TryFindResource("BodySize") is double size ? size : 13.5,
        TryFindResource("NoteCheck") as Style);

    /// <summary>A checkbox clicked (or toggled by a screen reader): the item it ticks.</summary>
    public event Action<NoteRow, CheckBox>? Ticked;

    /// <summary>The rows, in order.</summary>
    public IReadOnlyList<NoteRow> Rows => _rows;

    /// <summary>Shows these rows; with <paramref name="keepPlace"/>, scrolled as before (a tick, a change from another device).</summary>
    public void SetRows(List<NoteRow> rows, bool keepPlace)
    {
        foreach (var v in _views.Values) _children.Remove(v);
        _views.Clear();
        _shown.Clear();
        _rows = rows;
        _look = null;
        _heights = new double[rows.Count];
        _measured = new bool[rows.Count];
        _tops = null;
        for (int i = 0; i < rows.Count; i++) _heights[i] = Estimate(rows[i]);
        if (!keepPlace)
        {
            _anchor = 0;
            _delta = 0;
        }
        _anchor = Math.Clamp(_anchor, 0, Math.Max(0, rows.Count - 1));
        InvalidateMeasure();
    }

    /// <summary>The row view for row <paramref name="i"/>, if it's made (in view).</summary>
    internal RowView? ViewOf(int i) => _views.TryGetValue(i, out var v) ? v : null;

    /// <summary>The row under a point (this panel's coordinates), the nearest one in view if it's between rows.</summary>
    public int RowAt(Point at)
    {
        int best = -1;
        foreach (var (row, y) in _shown)
        {
            if (y <= at.Y || best < 0) best = row;
            else break;
        }
        return best;
    }

    /// <summary>Where row <paramref name="i"/>'s text starts, in this panel's coordinates, if it's in view.</summary>
    public double? TextTopOf(int i)
    {
        foreach (var (row, y) in _shown)
        {
            if (row == i) return y + _views[row].TextTop;
        }
        return null;
    }

    /// <summary>Scrolls so row <paramref name="i"/>'s text starts at <paramref name="y"/> (this panel's coordinates).</summary>
    public void ScrollRowTo(int i, double y)
    {
        if (_rows.Count == 0) return;
        _anchor = Math.Clamp(i, 0, _rows.Count - 1);
        EnsureMeasured(_anchor, _width);
        _delta = _views.TryGetValue(_anchor, out var v) ? v.TextTop - y : -y;
        InvalidateMeasure();
        UpdateLayout();
    }

    /// <summary>The last row starting at or before <paramref name="offset"/> in the text.</summary>
    public int RowOfLine(int offset)
    {
        int lo = 0, hi = _rows.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Line <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The row whose checkbox has the keyboard, if one does.</summary>
    public int FocusedBox() => _views.FirstOrDefault(v => v.Value.Box?.IsKeyboardFocused == true).Value is { } view ? view.Index : -1;

    // ---- Estimates and measuring ----

    private static double LineHeight(NoteRow r) => r.Kind switch
    {
        RowKind.Heading => r.Level == 1 ? 26 : r.Level == 2 ? 22 : 19,
        RowKind.Code => 17,
        RowKind.Rule => 1,
        _ => 18,
    };

    private double Estimate(NoteRow r)
    {
        double width = double.IsNaN(_width) ? 300 : Math.Max(40, _width - r.Left - r.Quote * NoteRows.QuoteIndent);
        int chars = r.Kind == RowKind.Code ? r.Code.Length : r.Name.Length;
        int lines = Math.Max(1, (int)Math.Ceiling(chars * 6.6 / width));
        double pad = r.Kind == RowKind.Code ? (r.CodeFirst ? 5 : 0) + (r.CodeLast ? 5 : 0) : r.Kind == RowKind.Rule ? 12 : 0;
        return r.Gap + lines * LineHeight(r) + pad;
    }

    private double[] Tops()
    {
        if (_tops != null) return _tops;
        var tops = new double[_rows.Count + 1];
        for (int i = 0; i < _rows.Count; i++) tops[i + 1] = tops[i] + _heights[i];
        return _tops = tops;
    }

    private RowView Realize(int i)
    {
        if (_views.TryGetValue(i, out var view)) return view;
        view = new RowView(_rows[i], i, Fonts, this);
        _views[i] = view;
        _children.Add(view);
        return view;
    }

    private void EnsureMeasured(int i, double width)
    {
        if (double.IsNaN(width)) return;
        var view = Realize(i);
        view.Measure(new Size(width, double.PositiveInfinity));
        double h = view.DesiredSize.Height;
        if (!_measured[i] || h != _heights[i])
        {
            _heights[i] = h;
            _measured[i] = true;
            _tops = null;
        }
    }

    internal void OnTicked(NoteRow row, CheckBox box) => Ticked?.Invoke(row, box);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 300 : available.Width;
        double height = double.IsInfinity(available.Height) ? 600 : available.Height;
        if (width != _width)
        {
            _width = width;
            Array.Clear(_measured);
            for (int i = 0; i < _rows.Count; i++) _heights[i] = Estimate(_rows[i]);
            _tops = null;
        }
        _viewport = new Size(width, height);
        var shown = new List<(int, double)>();
        if (_rows.Count > 0)
        {
            // Settle the row at the top: the view starts inside it.
            _anchor = Math.Clamp(_anchor, 0, _rows.Count - 1);
            EnsureMeasured(_anchor, width);
            while (_delta < 0 && _anchor > 0)
            {
                _anchor--;
                EnsureMeasured(_anchor, width);
                _delta += _heights[_anchor];
            }
            if (_delta < 0) _delta = 0;
            while (_anchor < _rows.Count - 1 && _delta >= _heights[_anchor])
            {
                _delta -= _heights[_anchor];
                _anchor++;
                EnsureMeasured(_anchor, width);
            }

            // Down from it until the view is full; at the end with room to spare, back up.
            double y = -_delta;
            int i = _anchor;
            for (; i < _rows.Count && y < height; i++)
            {
                EnsureMeasured(i, width);
                y += _heights[i];
            }
            if (i == _rows.Count && y < height)
            {
                double room = height - y;
                double above = _delta;
                while (room > 0 && (above > 0 || _anchor > 0))
                {
                    if (above > 0)
                    {
                        double take = Math.Min(above, room);
                        _delta -= take;
                        above -= take;
                        room -= take;
                        continue;
                    }
                    _anchor--;
                    EnsureMeasured(_anchor, width);
                    _delta = _heights[_anchor];
                    above = _delta;
                }
            }
            y = -_delta;
            for (i = _anchor; i < _rows.Count && y < height; i++)
            {
                shown.Add((i, y));
                y += _heights[i];
            }
        }
        _shown = shown;

        // Rows out of view go (their checkboxes with them, unless one has the keyboard).
        foreach (int k in _views.Keys.ToList())
        {
            if (shown.Exists(s => s.Item1 == k) || _views[k].IsKeyboardFocusWithin) continue;
            _children.Remove(_views[k]);
            _views.Remove(k);
        }
        // The scroll bar only hears of it when something it shows changed.
        var scroll = (ExtentHeight, VerticalOffset, height);
        if (scroll != _told)
        {
            _told = scroll;
            ScrollOwner?.InvalidateScrollInfo();
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var placed = new HashSet<int>();
        foreach (var (row, y) in _shown)
        {
            _views[row].Arrange(new Rect(0, y, final.Width, _heights[row]));
            placed.Add(row);
        }
        foreach (var (row, view) in _views)
        {
            if (!placed.Contains(row)) view.Arrange(new Rect(0, -10000, final.Width, view.DesiredSize.Height)); // kept for the keyboard, out of sight
        }
        return final;
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the gaps are the note too: a click there writes

    protected override AutomationPeer OnCreateAutomationPeer() => new ReaderPeer(this);

    // ---- IScrollInfo ----

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _viewport.Width;
    public double ExtentHeight => Tops()[^1];
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _rows.Count == 0 ? 0 : Tops()[_anchor] + _delta;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        if (_rows.Count == 0) return;
        var tops = Tops();
        offset = Math.Clamp(offset, 0, Math.Max(0, tops[^1] - _viewport.Height));
        int lo = 0, hi = _rows.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (tops[mid] <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        _anchor = found;
        _delta = offset - tops[found];
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }

    private void ScrollBy(double dy)
    {
        if (_rows.Count == 0) return;
        _delta += dy; // the next measure settles it within the note
        InvalidateMeasure();
    }

    public void LineUp() => ScrollBy(-Arrow);
    public void LineDown() => ScrollBy(Arrow);
    public void PageUp() => ScrollBy(-Math.Max(Arrow, _viewport.Height - Arrow));
    public void PageDown() => ScrollBy(Math.Max(Arrow, _viewport.Height - Arrow));
    public void MouseWheelUp() => ScrollBy(-Wheel);
    public void MouseWheelDown() => ScrollBy(Wheel);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>A checkbox tabbed to out of view: scrolled to it.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement e || !IsAncestorOf(visual)) return rectangle;
        var at = e.TransformToAncestor(this).TransformBounds(rectangle);
        if (at.Top < 0) ScrollBy(at.Top);
        else if (at.Bottom > _viewport.Height) ScrollBy(at.Bottom - _viewport.Height);
        return rectangle;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Up: LineUp(); break;
            case Key.Down: LineDown(); break;
            case Key.PageUp: PageUp(); break;
            case Key.PageDown: PageDown(); break;
            case Key.Home: SetVerticalOffset(0); break;
            case Key.End: SetVerticalOffset(double.MaxValue); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>The formatted note for a screen reader: a document whose children are the rows in view.</summary>
    private sealed class ReaderPeer(NoteReader owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

        protected override string GetClassNameCore() => "NoteReader";

        protected override List<AutomationPeer> GetChildrenCore() =>
            owner._shown.Select(s => UIElementAutomationPeer.CreatePeerForElement(owner._views[s.Row])).Where(p => p != null).ToList()!;
    }
}

/// <summary>One row on screen: its marker (bullet, number or checkbox), its text, and the quote bars and code shade it sits on.</summary>
internal sealed class RowView : FrameworkElement
{
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New");
    private readonly NoteRow _row;
    private readonly NoteReader _reader;
    private readonly TextBlock _text;
    private readonly string? _mark; // a bullet or a number, drawn in the margin
    private readonly Typeface _markFace;
    private readonly double _markSize;

    public RowView(NoteRow row, int index, NoteReader.Look look, NoteReader reader)
    {
        _row = row;
        _reader = reader;
        Index = index;
        var ui = look.Ui;
        double size = look.Size;

        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = ui, FontSize = size };
        if (row.Quote > 0 || row.Kind == RowKind.Rule) SetResourceReference(BarBrushProperty, "StrokeStrongBrush");
        if (row.Kind == RowKind.Code) SetResourceReference(FillBrushProperty, "CodeFillBrush");
        switch (row.Kind)
        {
            case RowKind.Heading:
                _text.FontWeight = FontWeights.SemiBold;
                if (row.Level < 3) _text.FontFamily = look.Display;
                _text.FontSize = row.Level switch { 1 => size + 6, 2 => size + 3, _ => size + 0.5 };
                Inlines(_text.Inlines, row.Content, Mono, size);
                break;
            case RowKind.Code:
                _text.FontFamily = Mono;
                _text.FontSize = size - 1;
                _text.Text = row.Code;
                break;
            case RowKind.Text:
                Inlines(_text.Inlines, row.Content, Mono, size);
                break;
        }
        if (row.Quote > 0) _text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        if (row.Done)
        {
            _text.TextDecorations = TextDecorations.Strikethrough;
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        }
        if (row.Kind != RowKind.Rule) AddVisualChild(_text);

        if (row.Item is { Task: bool ticked } && (row.Marker == RowMarker.Box || row.BoxInline))
        {
            var box = new CheckBox { IsChecked = ticked, Style = look.Check, Cursor = Cursors.Hand };
            AutomationProperties.SetName(box, row.Name.Length > 0 ? row.Name : "Checkbox");
            // Checked and Unchecked, not Click: a screen reader's Toggle changes it without a click.
            box.Checked += (_, _) => _reader.OnTicked(_row, box);
            box.Unchecked += (_, _) => _reader.OnTicked(_row, box);
            Box = box;
            AddVisualChild(box);
        }
        // Bullets and numbers are drawn, not elements of their own: the rows in view are all there is to lay out.
        _markFace = new Typeface(ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _markSize = size;
        if (row.Marker is RowMarker.Bullet or RowMarker.Number)
        {
            _mark = row.Marker == RowMarker.Number ? row.Number.ToString(CultureInfo.InvariantCulture) + "." : row.ListLevel switch { 1 => "•", 2 => "◦", _ => "▪" };
            SetResourceReference(MarkBrushProperty, "TextSecondaryBrush");
        }
    }

    public static readonly DependencyProperty MarkBrushProperty = DependencyProperty.Register(nameof(MarkBrush), typeof(Brush), typeof(RowView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bullets and numbers (TextSecondaryBrush).</summary>
    public Brush? MarkBrush { get => (Brush?)GetValue(MarkBrushProperty); set => SetValue(MarkBrushProperty, value); }

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(RowView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(RowView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Quote bars and rules (StrokeStrongBrush, so a theme change redraws them).</summary>
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    /// <summary>Code's shade (CodeFillBrush).</summary>
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public int Index { get; }

    public NoteRow Row => _row;

    public CheckBox? Box { get; }

    public TextBlock Text => _text;

    /// <summary>Where its text starts, from its own top (below its gap, and a code block's padding).</summary>
    public double TextTop => _row.Gap + (_row.Kind == RowKind.Code && _row.CodeFirst ? 5 : 0);

    private double QuoteLeft => _row.Quote * NoteRows.QuoteIndent;

    private double TextLeft => QuoteLeft + _row.Left + (_row.Kind == RowKind.Code ? 8 : 0);

    protected override int VisualChildrenCount => (_row.Kind == RowKind.Rule ? 0 : 1) + (Box != null ? 1 : 0);

    protected override Visual GetVisualChild(int index) => index == 0 && _row.Kind != RowKind.Rule ? _text : Box!;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 300 : available.Width;
        double textWidth = Math.Max(20, width - TextLeft - (_row.Kind == RowKind.Code ? 8 : 0));
        double h;
        if (_row.Kind == RowKind.Rule)
        {
            h = 13;
        }
        else
        {
            _text.Measure(new Size(textWidth, double.PositiveInfinity));
            h = _text.DesiredSize.Height;
            if (_row.Kind == RowKind.Code) h += (_row.CodeFirst ? 5 : 0) + (_row.CodeLast ? 5 : 0);
        }
        Box?.Measure(new Size(14, 14));
        return new Size(width, _row.Gap + h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double top = TextTop;
        double left = TextLeft;
        if (_row.Kind != RowKind.Rule) _text.Arrange(new Rect(left, top, Math.Max(0, final.Width - left - (_row.Kind == RowKind.Code ? 8 : 0)), _text.DesiredSize.Height));
        // The first line's middle, for the marker and box.
        double line = _row.Kind == RowKind.Heading ? _text.DesiredSize.Height : Math.Min(_text.DesiredSize.Height, 18);
        if (_row.Kind == RowKind.Text && _text.DesiredSize.Height > 18) line = 18;
        double middle = top + line / 2;
        double markerLeft = QuoteLeft + _row.MarkerLeft;
        if (Box != null)
        {
            double x = _row.BoxInline ? QuoteLeft + _row.Left - NoteRows.BoxWidth : markerLeft;
            Box.Arrange(new Rect(x, Math.Round(middle - 7), 14, 14));
        }
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double h = RenderSize.Height, w = RenderSize.Width;
        // Quote bars, one for each quote it's in; carried through the gap when the row above is in that quote too.
        if (_row.Quote > 0)
        {
            var bar = BarBrush;
            for (int q = 0; q < _row.Quote; q++)
            {
                bool continued = Index > 0 && _reader.Rows[Index - 1].Quote > q;
                double from = continued ? 0 : _row.Gap;
                dc.DrawRectangle(bar, null, new Rect(q * NoteRows.QuoteIndent, from, 3, Math.Max(0, h - from)));
            }
        }
        if (_row.Kind == RowKind.Code)
        {
            var fill = FillBrush;
            double left = QuoteLeft + _row.Left;
            dc.DrawRectangle(fill, null, new Rect(left, _row.Gap, Math.Max(0, w - left), Math.Max(0, h - _row.Gap)));
        }
        if (_row.Kind == RowKind.Rule)
        {
            var line = BarBrush;
            double left = QuoteLeft + _row.Left;
            dc.DrawRectangle(line, null, new Rect(left, _row.Gap + 6, Math.Max(0, w - left), 1));
        }
        if (_mark != null)
        {
            var text = new FormattedText(_mark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _markFace, _markSize, MarkBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double left = QuoteLeft + _row.MarkerLeft;
            // A number ends a little before the text; a bullet starts the margin. Both on the text's first line.
            double x = _row.Marker == RowMarker.Number ? left + _row.MarkerWidth - 6 - text.WidthIncludingTrailingWhitespace : left + 3;
            dc.DrawText(text, new Point(x, TextTop));
        }
    }

    // ---- Inline formatting ----

    internal static void Inlines(InlineCollection into, IReadOnlyList<MdInline> inlines, FontFamily mono, double size)
    {
        foreach (var x in inlines) into.Add(Inline(x, mono, size));
    }

    private static Inline Inline(MdInline x, FontFamily mono, double size)
    {
        switch (x)
        {
            case MdText t:
                return new Run(t.Text);
            case MdCode c:
            {
                var run = new Run(c.Text) { FontFamily = mono, FontSize = size - 1 };
                run.SetResourceReference(TextElement.BackgroundProperty, "CodeFillBrush");
                return run;
            }
            case MdStyle s:
            {
                var span = new Span();
                switch (s.Kind)
                {
                    case MdStyleKind.Bold: span.FontWeight = FontWeights.SemiBold; break;
                    case MdStyleKind.Italic: span.FontStyle = FontStyles.Italic; break;
                    default: span.TextDecorations = TextDecorations.Strikethrough; break;
                }
                Inlines(span.Inlines, s.Content, mono, size);
                return span;
            }
            case MdLink l:
            {
                // Looks like a link, opens nothing: the url is only ever shown, as plain text.
                var span = new Span { TextDecorations = TextDecorations.Underline, ToolTip = l.Url.Length > 0 ? l.Url : null };
                span.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                Inlines(span.Inlines, l.Content, mono, size);
                return span;
            }
            default:
                return new Run();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    /// <summary>A heading (with its level), a list item, or a line of text, named by its text; a checkbox inside, if it has one.</summary>
    private sealed class RowPeer(RowView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            owner._row.Item != null ? AutomationControlType.ListItem : AutomationControlType.Text;

        protected override string GetClassNameCore() => owner._row.Kind == RowKind.Heading ? "Heading" : owner._row.Item != null ? "ListItem" : "Line";

        protected override string GetNameCore() => owner._row.Name;

        protected override AutomationHeadingLevel GetHeadingLevelCore() => owner._row.Kind != RowKind.Heading ? AutomationHeadingLevel.None : owner._row.Level switch
        {
            1 => AutomationHeadingLevel.Level1,
            2 => AutomationHeadingLevel.Level2,
            _ => AutomationHeadingLevel.Level3,
        };

        protected override List<AutomationPeer>? GetChildrenCore() =>
            owner.Box is { } box && UIElementAutomationPeer.CreatePeerForElement(box) is { } peer ? [peer] : null;
    }
}
