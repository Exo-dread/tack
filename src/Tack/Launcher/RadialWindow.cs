using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Tack.Controls;
using Tack.Interop;
using Tack.Launcher.Core;

namespace Tack.Launcher;

/// <summary>
/// The open launcher: shortcuts around the orb, a group's fan, the display picker and one label. Created when
/// the launcher opens and closed (not hidden) when it closes, so nothing of it lingers. It draws what the
/// <see cref="LauncherSession"/> says and forwards input to <see cref="LauncherModule"/>; all decisions are
/// made there. Everything is placed from the scene's physical-pixel layout, divided by the monitor's scale.
/// </summary>
internal sealed class RadialWindow : Window
{
    private const string Glyphs = "IconFont";

    private readonly LauncherModule _owner;
    private readonly LauncherSession _session;
    private readonly LauncherScene _scene;
    private readonly bool _animate;
    private readonly Canvas _canvas = new() { Focusable = true, FocusVisualStyle = null };
    private readonly Dictionary<NodeRef, FrameworkElement> _nodes = new();
    private readonly Border _label = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _labelText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
    private Border? _selector;
    private NodeRef? _selectorFor;
    private readonly List<Border> _tiles = new();
    private Hit _shownHighlight = Hit.Nothing;
    private int? _shownGroup;

    public RadialWindow(LauncherModule owner, LauncherSession session, bool animate)
    {
        _owner = owner;
        _session = session;
        _scene = session.Scene;
        _animate = animate;

        Title = "Tack launcher";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Not quite transparent, so clicks between shortcuts land here (and close the launcher) instead of
        // falling through to the app underneath.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Width = _scene.Extent.Width / _scene.Scale;
        Height = _scene.Extent.Height / _scene.Scale;
        SetResourceReference(FontFamilyProperty, "UiFont");
        AutomationProperties.SetName(this, "Tack launcher");

        _canvas.Width = Width;
        _canvas.Height = Height;
        var orb = Dip(_scene.Orb);
        _canvas.RenderTransformOrigin = new Point(orb.X / Math.Max(1, Width), orb.Y / Math.Max(1, Height));
        Content = _canvas;

        _label.SetResourceReference(Border.BackgroundProperty, "FlyoutBrush");
        _label.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");
        _label.BorderThickness = new Thickness(1);
        _label.Child = _labelText;
        Panel.SetZIndex(_label, 30);

        Build();
        _canvas.Children.Add(_label);
    }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(Handle); // no taskbar button, not in Alt+Tab
        WindowEffects.ExcludeFromPeek(Handle);
    }

    // ================= Building =================

    private Point Dip(PxPoint p) => new((p.X - _scene.Extent.X) / _scene.Scale, (p.Y - _scene.Extent.Y) / _scene.Scale);

    private Rect Dip(PxRect r) => new(Dip(new PxPoint(r.X, r.Y)), new Size(r.Width / _scene.Scale, r.Height / _scene.Scale));

    private void Build()
    {
        for (int i = 0; i < _scene.Slots.Count; i++)
        {
            var node = new NodeRef(i);
            Add(node, _scene.Slots[i] is null ? EmptySlot() : Item(node));
            if (_scene.Slots[i] is { IsGroup: true })
                foreach (var child in _scene.ChildrenOf(i))
                {
                    var v = Item(child);
                    v.Visibility = Visibility.Collapsed;
                    Add(child, v);
                }
        }
    }

    private void Add(NodeRef node, FrameworkElement visual)
    {
        var c = Dip(_scene.CenterOf(node));
        Canvas.SetLeft(visual, c.X - visual.Width / 2);
        Canvas.SetTop(visual, c.Y - visual.Height / 2);
        _nodes[node] = visual;
        _canvas.Children.Add(visual);
    }

    private static FrameworkElement EmptySlot()
    {
        var dot = new Ellipse { Width = 8, Height = 8, Opacity = 0.8, IsHitTestVisible = false };
        dot.SetResourceReference(Shape.FillProperty, "StrokeStrongBrush");
        var g = new Grid { Width = 8, Height = 8, IsHitTestVisible = false };
        g.Children.Add(dot);
        AutomationProperties.SetName(g, "Empty position");
        return g;
    }

    private FrameworkElement Item(NodeRef node)
    {
        var entry = _scene.Entry(node)!;
        string? problem = _scene.ProblemOf(node);
        double d = LauncherMetrics.ItemDiameter;
        var g = new Grid { Width = d, Height = d, RenderTransformOrigin = new Point(0.5, 0.5), Tag = node };

        var body = new Ellipse { StrokeThickness = 1 };
        body.SetResourceReference(Shape.FillProperty, "FlyoutBrush");
        body.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
        g.Children.Add(body);

        var icon = LauncherIcons.For(entry);
        if (icon != null)
            g.Children.Add(new Image { Source = icon, Width = 22, Height = 22, IsHitTestVisible = false });
        else
        {
            var glyph = new TextBlock
            {
                Text = LauncherIcons.Glyph(entry), FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, Glyphs);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            g.Children.Add(glyph);
        }

        if (entry.IsGroup)
        {
            // Three small dots under the icon: "there's more here".
            var dots = new TextBlock { Text = "•••", FontSize = 7, Margin = new Thickness(0, 0, 0, 3), IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            dots.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            g.Children.Add(dots);
        }
        if (problem != null)
        {
            var badge = new Ellipse { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), IsHitTestVisible = false };
            badge.SetResourceReference(Shape.FillProperty, "DangerBrush");
            g.Children.Add(badge);
        }

        string name = node.IsChild ? entry.Label : $"{node.Slot + 1}. {entry.Label}";
        AutomationProperties.SetName(g, name);
        AutomationProperties.SetHelpText(g, problem ?? Describe(entry));
        g.ToolTip = problem != null ? $"{entry.Label}\n{problem}" : $"{entry.Label}\n{Describe(entry)}";
        ToolTipService.SetInitialShowDelay(g, 700);
        return g;
    }

    private string Describe(Shortcut e) => e.KindValue switch
    {
        ShortcutKind.Group => $"{e.Children?.Count ?? 0} shortcuts",
        ShortcutKind.Url => e.Target,
        _ => _scene.MultipleDisplays && e.CanTargetDisplay
            ? $"{e.Target}\nRight-click or keep dragging outward to choose a display."
            : e.Target,
    };

    // ================= Showing state =================

    public void PlayOpen()
    {
        if (!_animate) return;
        // The whole launcher grows out of the orb: short, slightly springy, and it never blocks input.
        Anim.Fade(_canvas, 1, 120, from: 0);
        Anim.Scale(_canvas, 1, 180, Anim.Spring, from: 0.55);
    }

    public void PlayClose(Action completed)
    {
        IsHitTestVisible = false;
        if (!_animate) { completed(); return; }
        Anim.Fade(_canvas, 0, 110);
        Anim.Scale(_canvas, 0.7, 120, Anim.EaseIn, completed: completed);
    }

    public void TakeFocus()
    {
        Activate();
        _canvas.Focus();
        Keyboard.Focus(_canvas);
    }

    /// <summary>Brings the visuals in line with the session: highlight, open group, picker, label.</summary>
    public void Refresh()
    {
        if (_shownGroup != _session.OpenGroup)
        {
            if (_shownGroup is { } old) foreach (var c in _scene.ChildrenOf(old)) SetVisible(c, false);
            if (_session.OpenGroup is { } open) foreach (var c in _scene.ChildrenOf(open)) SetVisible(c, true, from: new NodeRef(open));
            _shownGroup = _session.OpenGroup;
        }

        if (_selectorFor != _session.Selector)
        {
            if (_selector != null) _canvas.Children.Remove(_selector);
            _selector = null;
            _tiles.Clear();
            _selectorFor = _session.Selector;
            if (_selectorFor is { } n) BuildSelector(n);
        }

        var h = _session.Highlight;
        if (h != _shownHighlight)
        {
            SetHot(_shownHighlight, false);
            SetHot(h, true);
            _shownHighlight = h;
        }
        ShowLabel();
    }

    private void SetVisible(NodeRef node, bool visible, NodeRef? from = null)
    {
        if (!_nodes.TryGetValue(node, out var v)) return;
        if (!visible) { v.Visibility = Visibility.Collapsed; return; }
        v.Visibility = Visibility.Visible;
        if (!_animate || from is not { } parent) return;
        var p = Dip(_scene.CenterOf(parent));
        var c = Dip(_scene.CenterOf(node));
        Anim.Fade(v, 1, 120, from: 0);
        Anim.Slide(v, p.X - c.X, p.Y - c.Y, 160, Anim.EaseOut);
    }

    private void SetHot(Hit hit, bool hot)
    {
        switch (hit.Kind)
        {
            case HitKind.Item when _nodes.TryGetValue(hit.Node, out var v) && v is Grid g && g.Children.Count > 0 && g.Children[0] is Ellipse body:
                body.SetResourceReference(Shape.FillProperty, hot ? "AccentSoftBrush" : "FlyoutBrush");
                body.SetResourceReference(Shape.StrokeProperty, hot ? "AccentBrush" : "StrokeStrongBrush");
                body.StrokeThickness = hot ? 1.5 : 1;
                double scale = hot ? 1.12 : 1.0;
                if (_animate) Anim.Scale(g, scale, 90);
                else
                {
                    var (s, _) = Anim.Transforms(g);
                    Anim.Set(s, ScaleTransform.ScaleXProperty, scale);
                    Anim.Set(s, ScaleTransform.ScaleYProperty, scale);
                }
                break;
            case HitKind.Display when hit.Display >= 0 && hit.Display < _tiles.Count:
                StyleTile(hit.Display, hot);
                break;
        }
    }

    private void ShowLabel()
    {
        string? text = _session.Message;
        bool warning = text != null;
        PxPoint anchor = _scene.Orb;
        var h = _session.Highlight;
        if (text == null && h.Kind == HitKind.Item && _scene.Entry(h.Node) is { } e)
        {
            text = e.Label;
            anchor = _scene.CenterOf(h.Node);
        }
        else if (text == null && h.Kind == HitKind.Display && h.Display < _scene.Displays.Count && _session.Selector is { } sel)
        {
            int number = MonitorMatch.Numbers(_scene.Displays)[h.Display];
            text = MonitorMatch.Describe(_scene.Displays[h.Display], number);
            anchor = _scene.SelectorFor(sel).Center;
        }
        else if (warning && h.Kind == HitKind.Item) anchor = _scene.CenterOf(h.Node);

        if (string.IsNullOrEmpty(text))
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _labelText.Text = text;
        _labelText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "DangerBrush" : "TextPrimaryBrush");
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;

        // Below the shortcut if there's room in the window, otherwise above it; always inside the window.
        var a = Dip(anchor);
        double half = LauncherMetrics.ItemDiameter / 2 + 4;
        double x = a.X - size.Width / 2;
        double y = a.Y + half;
        if (y + size.Height > Height) y = a.Y - half - size.Height;
        if (_selector != null && h.Kind == HitKind.Display) y = Canvas.GetTop(_selector) - size.Height - 4;
        Canvas.SetLeft(_label, Math.Clamp(x, 0, Math.Max(0, Width - size.Width)));
        Canvas.SetTop(_label, Math.Clamp(y, 0, Math.Max(0, Height - size.Height)));
        AutomationProperties.SetName(_label, text);
    }

    // ================= Display picker =================

    private void BuildSelector(NodeRef node)
    {
        var box = Dip(_scene.SelectorFor(node));
        var entry = _scene.Entry(node);
        int? preferred = entry != null ? _scene.PreferredDisplay(entry) : null;
        int here = _scene.OrbDisplay();
        int[] numbers = MonitorMatch.Numbers(_scene.Displays);

        var frame = new Canvas { Width = box.Width, Height = box.Height };
        var caption = new TextBlock { Text = $"Open {entry?.Label} on", FontSize = 11, MaxWidth = box.Width - 16, TextTrimming = TextTrimming.CharacterEllipsis };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Canvas.SetLeft(caption, 10);
        Canvas.SetTop(caption, 4);
        frame.Children.Add(caption);

        var tiles = _scene.TilesFor(node);
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = Dip(tiles[i]);
            var number = new TextBlock
            {
                Text = numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture) + (i == here ? " •" : ""),
                FontSize = Math.Clamp(t.Height * 0.38, 9, 14), FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var tile = new Border
            {
                Width = t.Width, Height = t.Height, CornerRadius = new CornerRadius(3), Child = number,
                Tag = preferred == i ? "preferred" : null,
                ToolTip = MonitorMatch.Describe(_scene.Displays[i], numbers[i]) + (preferred == i ? "\nRemembered for this shortcut" : "") + (i == here ? "\nTack is on this display" : ""),
            };
            AutomationProperties.SetName(tile, MonitorMatch.Describe(_scene.Displays[i], numbers[i]));
            Canvas.SetLeft(tile, t.X - box.X);
            Canvas.SetTop(tile, t.Y - box.Y);
            _tiles.Add(tile);
            StyleTile(i, false);
            frame.Children.Add(tile);
        }

        _selector = new Border
        {
            Width = box.Width, Height = box.Height, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = frame,
        };
        _selector.SetResourceReference(Border.BackgroundProperty, "FlyoutBrush");
        _selector.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");
        AutomationProperties.SetName(_selector, $"Choose a display for {entry?.Label}");
        Canvas.SetLeft(_selector, box.X);
        Canvas.SetTop(_selector, box.Y);
        Panel.SetZIndex(_selector, 20);
        _canvas.Children.Add(_selector);
        if (_animate) Anim.Fade(_selector, 1, 110, from: 0);

        // A highlight that was set before the tiles existed (keyboard): show it now.
        if (_session.Highlight.Kind == HitKind.Display) { _shownHighlight = Hit.Nothing; }
    }

    private void StyleTile(int i, bool hot)
    {
        var tile = _tiles[i];
        bool preferred = (tile.Tag as string) == "preferred";
        tile.SetResourceReference(Border.BackgroundProperty, hot ? "AccentSoftBrush" : "SubtleFillBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, hot ? "AccentBrush" : preferred ? "AccentBorderBrush" : "StrokeStrongBrush");
        tile.BorderThickness = new Thickness(hot || preferred ? 1.5 : 1);
    }

    // ================= Input (click mode; gestures arrive through the orb) =================

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        _owner.OnPointerMove(this);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        e.Handled = true;
        _owner.OnPointerDown(this);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        e.Handled = true;
        _owner.OnPointerUp(this);
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        e.Handled = true;
        _owner.OnSecondaryClick(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        (LauncherKey Key, int Digit)? mapped = key switch
        {
            Key.Escape => (LauncherKey.Escape, 0),
            Key.Right or Key.Down => (LauncherKey.Next, 0),
            Key.Left or Key.Up => (LauncherKey.Previous, 0),
            Key.Tab => (shift ? LauncherKey.Previous : LauncherKey.Next, 0),
            Key.Enter when shift => (LauncherKey.PickDisplay, 0),
            Key.Enter or Key.Space => (LauncherKey.Activate, 0),
            Key.M or Key.Apps => (LauncherKey.PickDisplay, 0),
            >= Key.D1 and <= Key.D6 => (LauncherKey.Digit, key - Key.D0),
            >= Key.NumPad1 and <= Key.NumPad6 => (LauncherKey.Digit, key - Key.NumPad0),
            _ => null,
        };
        if (mapped is not { } m) return;
        e.Handled = true;
        _owner.OnKey(this, m.Key, m.Digit);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _owner.OnDeactivated(this);
    }
}
