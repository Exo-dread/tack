using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
    private TextBlock? _caption;
    private NodeRef? _selectorFor;
    private readonly List<Border> _tiles = new();
    private Border? _recents;
    private TextBlock? _recentsCaption;
    private NodeRef? _recentsFor;
    private readonly List<Border> _rows = new();
    private Hit _shownHighlight = Hit.Nothing;
    private int? _shownGroup;
    private string? _announced;

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
        // A message (why a shortcut can't open, "Opening…") is read out as it appears.
        AutomationProperties.SetLiveSetting(_labelText, AutomationLiveSetting.Assertive);

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
        var g = new ShortcutVisual(() => _owner.OnInvoke(this, node))
        {
            Width = d, Height = d, RenderTransformOrigin = new Point(0.5, 0.5), Tag = node, Focusable = true, FocusVisualStyle = null,
        };

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
        ShortcutKind.Group => LauncherConfig.CountOf(e.Children?.Count ?? 0),
        ShortcutKind.Url => e.Target,
        _ => (_scene.MultipleDisplays && e.CanTargetDisplay
                ? $"{e.Target}\nRight-click or keep dragging outward to choose a display."
                : e.Target) +
            (_scene.RecentFilesOn && e.KindValue == ShortcutKind.App && WinPath.Extension(e.Target.Trim().Trim('"')) == ".exe"
                ? "\nRest on it, or press R, for its recent files." : ""),
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
        FollowHighlight();
    }

    /// <summary>
    /// With the keyboard on the launcher (click mode), the highlighted shortcut or display has the keyboard
    /// focus as well, so Narrator and other screen readers say which one it is. Never takes focus by itself.
    /// </summary>
    private void FollowHighlight()
    {
        if (!IsKeyboardFocusWithin) return;
        var h = _session.Highlight;
        FrameworkElement? target = h.Kind switch
        {
            HitKind.Item when _nodes.TryGetValue(h.Node, out var v) => v,
            HitKind.Display when h.Display >= 0 && h.Display < _tiles.Count => _tiles[h.Display],
            HitKind.File when h.Node.File < _rows.Count => _rows[h.Node.File],
            _ => null,
        };
        if (target != null) { if (!target.IsKeyboardFocused) target.Focus(); }
        else if (Keyboard.FocusedElement is UIElement { IsVisible: false }) _canvas.Focus(); // its group folded away
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

        if (_recentsFor != _session.Recents)
        {
            if (_recents != null) _canvas.Children.Remove(_recents);
            _recents = null;
            _recentsCaption = null;
            _rows.Clear();
            _recentsFor = _session.Recents;
            if (_recentsFor is { } owner) BuildRecents(owner);
            if (_shownHighlight.Kind == HitKind.File) _shownHighlight = Hit.Nothing; // its row went with the card
        }

        if (_selectorFor != _session.Selector)
        {
            if (_selector != null) _canvas.Children.Remove(_selector);
            _selector = null;
            _caption = null;
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
        // The app whose recent files are out keeps an accent ring, so it's clear whose files they are.
        if (_ownerShown != _session.Recents && _ownerShown is { } was && h != new Hit(HitKind.Item, was)) SetHot(new Hit(HitKind.Item, was), false);
        _ownerShown = _session.Recents;
        if (_ownerShown is { } app && h != new Hit(HitKind.Item, app) && _nodes.TryGetValue(app, out var v) && v is Grid g && g.Children[0] is Ellipse body)
        {
            body.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            body.StrokeThickness = 1.5;
        }
        ShowLabel();
        FollowHighlight();
    }

    private NodeRef? _ownerShown;

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
            case HitKind.File when hit.Node.Owner == _recentsFor && hit.Node.File < _rows.Count:
                StyleRow(hit.Node.File, hot);
                break;
        }
    }

    private void ShowLabel()
    {
        string? text = _session.Message;
        bool message = text != null;
        bool warning = message && _session.IsWarning;
        PxPoint anchor = _scene.Orb;
        var h = _session.Highlight;

        // The picker's own caption names its shortcut, and the display while one is highlighted, so no floating
        // label goes over the picker or the shortcut beside it.
        if (_caption != null && _session.Selector is { } open)
        {
            string what = _scene.Entry(open)?.Label ?? "";
            _caption.Text = h.Kind == HitKind.Display && h.Display >= 0 && h.Display < _scene.Displays.Count
                ? $"Open {what} on Display {MonitorMatch.Numbers(_scene.Displays)[h.Display]}{(_scene.Displays[h.Display].Primary ? " (main)" : "")}"
                : $"Open {what} on";
        }
        // The recent files' caption says which app's they are, or the folder of the one highlighted (its row has the name).
        if (_recentsCaption != null && _session.Recents is { } app)
        {
            var files = _scene.RecentsOf(app);
            _recentsCaption.Text = h.Kind == HitKind.File && h.Node.Owner == app && files != null && h.Node.File < files.Count
                ? RecentFiles.ShortFolder(files[h.Node.File].Folder)
                : $"Recent in {_scene.Entry(app)?.Label}";
        }
        if (!message && h.Kind == HitKind.Item && _session.Selector != h.Node && _scene.Entry(h.Node) is { } e)
        {
            text = e.Label;
            anchor = _scene.CenterOf(h.Node);
        }
        else if (message && h.Kind is HitKind.Item or HitKind.EmptySlot) anchor = _scene.CenterOf(h.Node);
        else if (message && h.Kind == HitKind.File) anchor = _scene.CenterOf(h.Node.Owner);

        if (string.IsNullOrEmpty(text))
        {
            _label.Visibility = Visibility.Collapsed;
            _announced = null;
            return;
        }
        _labelText.Text = text;
        // A message is read in full (wrapped); a shortcut's name stays on one line.
        _labelText.TextWrapping = message ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _labelText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "DangerBrush" : "TextPrimaryBrush");
        _label.Visibility = Visibility.Visible;
        // Measured now, for this text: without the invalidation, Measure keeps the size of the previous text
        // until the next layout pass, and the label would be placed for a label of another width.
        _label.InvalidateMeasure();
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;

        // On the far side of the shortcut from the orb (outward), or wherever near it covers least of the orb,
        // the shortcuts and the picker on show; under the orb when it's about the launcher as a whole.
        var a = Dip(anchor);
        var o = Dip(_scene.Orb);
        double orbR = LauncherMetrics.OrbDiameter / 2;
        var keepClear = new List<PxRect> { new(o.X - orbR, o.Y - orbR, 2 * orbR, 2 * orbR) };
        foreach (var v in _nodes.Values)
            if (v.Visibility == Visibility.Visible) keepClear.Add(new PxRect(Canvas.GetLeft(v), Canvas.GetTop(v), v.Width, v.Height));
        if (_selector != null) keepClear.Add(new PxRect(Canvas.GetLeft(_selector), Canvas.GetTop(_selector), _selector.Width, _selector.Height));
        if (_recents != null) keepClear.Add(new PxRect(Canvas.GetLeft(_recents), Canvas.GetTop(_recents), _recents.Width, _recents.Height));
        double gap = anchor == _scene.Orb ? orbR + 6 : LauncherMetrics.ItemDiameter / 2 + 4;
        var box = RadialLayout.LabelBox(new PxPoint(a.X, a.Y), new PxPoint(o.X, o.Y), size.Width, size.Height, gap, new PxRect(0, 0, Width, Height), keepClear);
        Canvas.SetLeft(_label, box.X);
        Canvas.SetTop(_label, box.Y);

        if (message && text != _announced)
        {
            _announced = text;
            if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                UIElementAutomationPeer.CreatePeerForElement(_labelText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        else if (!message) _announced = null;
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
        _caption = caption;
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
            int display = i;
            var tile = new TileVisual(() => _owner.OnInvokeDisplay(this, display))
            {
                Width = t.Width, Height = t.Height, CornerRadius = new CornerRadius(3), Child = number,
                Focusable = true, FocusVisualStyle = null,
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

    // ================= Recent files =================

    /// <summary>An app's recent files: a card of rows (icon, name, extension) beyond it, like the display picker.</summary>
    private void BuildRecents(NodeRef owner)
    {
        var files = _scene.RecentsOf(owner);
        if (files is not { Count: > 0 }) return;
        var box = Dip(_scene.RecentsBox(owner));
        var rows = _scene.RecentRows(owner);
        string app = _scene.Entry(owner)?.Label ?? "";

        var frame = new Canvas { Width = box.Width, Height = box.Height };
        var caption = new TextBlock { Text = $"Recent in {app}", FontSize = 11, MaxWidth = box.Width - 20, TextTrimming = TextTrimming.CharacterEllipsis };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _recentsCaption = caption;
        Canvas.SetLeft(caption, 10);
        Canvas.SetTop(caption, 5);
        frame.Children.Add(caption);

        for (int i = 0; i < files.Count && i < rows.Count; i++)
        {
            var file = files[i];
            var r = Dip(rows[i]);
            var line = new Grid { Margin = new Thickness(6, 0, 8, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (LauncherIcons.ForExtension(file.Extension) is { } icon)
                line.Children.Add(new Image { Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            var name = new TextBlock { Text = file.Stem, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(name, 1);
            line.Children.Add(name);
            var ext = new TextBlock { Text = file.Extension.ToLowerInvariant(), FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            ext.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            Grid.SetColumn(ext, 2);
            line.Children.Add(ext);

            var node = owner.WithFile(i);
            var row = new RowVisual(() => _owner.OnInvoke(this, node))
            {
                Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = line,
                Focusable = true, FocusVisualStyle = null, ToolTip = file.Path,
            };
            ToolTipService.SetInitialShowDelay(row, 700);
            AutomationProperties.SetName(row, file.Name);
            AutomationProperties.SetHelpText(row, $"Opens with {app}. {file.Folder}");
            Canvas.SetLeft(row, r.X - box.X);
            Canvas.SetTop(row, r.Y - box.Y);
            _rows.Add(row);
            StyleRow(i, false);
            frame.Children.Add(row);
        }

        _recents = new Border
        {
            Width = box.Width, Height = box.Height, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = frame,
        };
        _recents.SetResourceReference(Border.BackgroundProperty, "FlyoutBrush");
        _recents.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");
        AutomationProperties.SetName(_recents, $"Recent files for {app}");
        Canvas.SetLeft(_recents, box.X);
        Canvas.SetTop(_recents, box.Y);
        Panel.SetZIndex(_recents, 18); // under a display picker (20) and the label (30)
        _canvas.Children.Add(_recents);
        if (_animate)
        {
            // Out of its app, like a group's shortcuts: a short fade and a little travel.
            var from = Dip(_scene.CenterOf(owner));
            var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            Anim.Fade(_recents, 1, 120, from: 0);
            Anim.Slide(_recents, (from.X - center.X) * 0.15, (from.Y - center.Y) * 0.15, 160, Anim.EaseOut);
        }
    }

    private void StyleRow(int i, bool hot)
    {
        var row = _rows[i];
        if (hot) row.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        else row.Background = Brushes.Transparent; // still hit-testable, so the whole row is the target
        // The border is always there (clear when not lit), so lighting a row never moves its icon and name.
        if (hot) row.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else row.BorderBrush = Brushes.Transparent;
        row.BorderThickness = new Thickness(1);
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
        if (key == Key.F4 && (e.Key == Key.System || (Keyboard.Modifiers & ModifierKeys.Alt) != 0))
        {
            // Alt+F4 closes the launcher the launcher's way (all of it, focus handed back), not just its window.
            e.Handled = true;
            _owner.OnCloseRequested(this);
            return;
        }
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
            Key.R => (LauncherKey.Recents, 0),
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _owner.OnWindowClosed(this); // nothing to do when the launcher closed it itself
    }

    // ================= UI Automation =================

    /// <summary>
    /// A shortcut or a display tile as UI Automation (Narrator) sees it: a button with its name, what it opens
    /// (help text) and Invoke. A plain Grid or Border has no automation peer, so it wouldn't be seen at all.
    /// </summary>
    private sealed class PartPeer(FrameworkElement owner, string className, Action invoke) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => className;

        // Its icon, dots and number are part of it, not things of their own.
        protected override List<AutomationPeer>? GetChildrenCore() => null;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => invoke();
    }

    private sealed class ShortcutVisual(Action invoke) : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PartPeer(this, "LauncherShortcut", invoke);
    }

    private sealed class TileVisual(Action invoke) : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PartPeer(this, "LauncherDisplay", invoke);
    }

    private sealed class RowVisual(Action invoke) : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PartPeer(this, "LauncherRecentFile", invoke);
    }
}
