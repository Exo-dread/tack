using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Tack.Launcher.Core;
using Tack.Windows;

namespace Tack.Launcher;

/// <summary>
/// Settings → Launcher: an on/off switch and, when on, the six positions with a compact editor. Built in code
/// from the existing styles (Card, ToggleSwitch, Segment, IconButton, TextButton) so it looks like the rest of
/// Settings. Its content is only built once the launcher is turned on.
/// </summary>
internal sealed class LauncherSettingsView : UserControl
{
    private readonly LauncherModule _module;
    private readonly AppShell _shell;
    private readonly PanelWindow _panel;
    private readonly StackPanel _root = new();
    private readonly CheckBox _toggle;
    private readonly StackPanel _body = new();

    // The shortcut being edited (a copy): a primary position, or one of a group's shortcuts.
    private int _editSlot = -1;
    private Shortcut? _draft;
    private int _editChild = -1;
    private Shortcut? _childDraft;
    private bool _syncing;

    public LauncherSettingsView(LauncherModule module, AppShell shell, PanelWindow panel)
    {
        _module = module;
        _shell = shell;
        _panel = panel;
        Content = _root;

        var card = Card();
        var stack = new StackPanel();
        card.Child = stack;
        _toggle = new CheckBox { Content = "Radial launcher", Margin = new Thickness(0, 9, 0, 8) };
        _toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        _toggle.Checked += (_, _) => { if (!_syncing) _module.SetEnabled(true); };
        _toggle.Unchecked += (_, _) => { if (!_syncing) _module.SetEnabled(false); };
        stack.Children.Add(_toggle);
        stack.Children.Add(Hint("Shift+click the orb for your shortcuts, or Shift+drag to one and let go. Off, it uses nothing and the orb works as before."));
        stack.Children.Add(_body);
        _root.Children.Add(card);

        // Built when Settings is on screen (and kept current while it is), not at start-up.
        _syncing = true;
        _toggle.IsChecked = module.Enabled;
        _syncing = false;
        module.ConfigChanged += () => { if (IsVisible) Rebuild(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebuild(); };
    }

    // ================= Building =================

    private void Rebuild()
    {
        _syncing = true;
        _toggle.IsChecked = _module.Enabled;
        _syncing = false;
        _body.Children.Clear();
        if (!_module.Enabled) return;

        if (_draft != null) BuildEditor();
        else BuildSlots();
    }

    private void BuildSlots()
    {
        var config = _module.Config;
        _body.Children.Add(Divider());
        for (int i = 0; i < config.Slots.Count; i++)
        {
            int slot = i;
            var s = config.Slots[i];
            if (s == null)
            {
                _body.Children.Add(Row($"{slot + 1}", "Empty", null, null,
                    IconButton("", $"Add a shortcut at position {slot + 1}", () => StartEdit(slot, NewShortcut()))));
                continue;
            }
            _body.Children.Add(Row($"{slot + 1}", s.Label, Summary(s), ShortcutRulesProblem(s),
                IconButton("", $"Edit {s.Label}", () => StartEdit(slot, s.Clone())),
                IconButton("", $"Move {s.Label} up", () => Swap(slot, slot - 1), enabled: slot > 0),
                IconButton("", $"Move {s.Label} down", () => Swap(slot, slot + 1), enabled: slot < config.Slots.Count - 1),
                IconButton("", $"Remove {s.Label}", () => Remove(slot))));
        }
        _body.Children.Add(Divider());
        var motion = new CheckBox { Content = "Reduce motion", Margin = new Thickness(0, 9, 0, 8), IsChecked = _module.Config.ReduceMotion };
        motion.SetResourceReference(StyleProperty, "ToggleSwitch");
        motion.Checked += (_, _) => _module.Update(c => c.ReduceMotion = true);
        motion.Unchecked += (_, _) => _module.Update(c => c.ReduceMotion = false);
        _body.Children.Add(motion);
        _body.Children.Add(Hint("Positions 1–6 run clockwise from the top (top to bottom at a screen edge) and never move on their own. Windows' own animation setting is always respected."));
    }

    private static Shortcut NewShortcut() => new() { Kind = "app", Launch = "normal" };

    private string Summary(Shortcut s)
    {
        string where = s.LaunchValue switch
        {
            LaunchMode.Ask => " · asks for a display",
            LaunchMode.Preferred => " · opens on a remembered display",
            _ => "",
        };
        return s.KindValue switch
        {
            ShortcutKind.Group => $"Group · {s.Children?.Count ?? 0} shortcuts",
            ShortcutKind.Url => $"Website · {s.Target}",
            ShortcutKind.Folder => $"Folder · {s.Target}{where}",
            _ => $"App · {s.Target}{where}",
        };
    }

    private static string? ShortcutRulesProblem(Shortcut s) => s.IsGroup ? null : ShortcutRules.Problem(s, RealFileProbe.Instance);

    // ================= Slot actions =================

    private void Swap(int a, int b)
    {
        if (b < 0 || b >= LauncherConfig.SlotCount) return;
        _module.Update(c => (c.Slots[a], c.Slots[b]) = (c.Slots[b], c.Slots[a]));
    }

    private void Remove(int slot)
    {
        var removed = _module.Config.Slots[slot]?.Clone();
        if (removed == null) return;
        _module.Update(c => c.Slots[slot] = null);
        _panel.ShowToast($"Removed {removed.Label}", () => _module.Update(c => { if (c.Slots[slot] == null) c.Slots[slot] = removed; }));
    }

    private void StartEdit(int slot, Shortcut draft)
    {
        _editSlot = slot;
        _draft = draft;
        _editChild = -1;
        _childDraft = null;
        Rebuild();
    }

    private void EndEdit()
    {
        _editSlot = -1;
        _draft = null;
        _editChild = -1;
        _childDraft = null;
        Rebuild();
    }

    // ================= Editor =================

    private Shortcut Editing => _childDraft ?? _draft!;

    private void BuildEditor()
    {
        var s = Editing;
        bool child = _childDraft != null;
        _body.Children.Add(Divider());
        _body.Children.Add(Heading(child ? $"In {(_draft!.Label.Length > 0 ? _draft.Label : "the group")}" : $"Position {_editSlot + 1}"));

        // Name
        var name = Field(s.Label, "Name", text => s.Label = text);
        _body.Children.Add(Labelled("Name", name));

        // Type
        var types = new List<(string, string, string)> { ("app", "App", "A program or a shortcut to one"), ("folder", "Folder", "Opens in File Explorer"), ("url", "Website", "Opens in your browser") };
        if (!child) types.Add(("group", "Group", "Up to eight shortcuts behind one position"));
        _body.Children.Add(Labelled("Type", Segments(types, s.Kind, kind =>
        {
            s.Kind = kind;
            if (kind == "group") s.Children ??= new List<Shortcut>();
            if (kind is not ("app" or "folder")) { s.Launch = "normal"; s.Monitor = null; }
            Rebuild();
        })));

        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 6), Visibility = Visibility.Collapsed };
        problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        void Validate()
        {
            string? p = s.IsGroup ? null : ShortcutRules.Problem(Normalized(s), RealFileProbe.Instance);
            problem.Text = p ?? "";
            problem.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        }

        if (!s.IsGroup)
        {
            string what = s.KindValue switch { ShortcutKind.Url => "Address", ShortcutKind.Folder => "Folder", _ => "Program" };
            var target = Field(s.Target, what, text => { s.Target = text; Validate(); });
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(target);
            if (s.KindValue != ShortcutKind.Url)
            {
                var browse = TextButton("Browse…", () => Browse(s));
                Grid.SetColumn(browse, 1);
                browse.Margin = new Thickness(4, 0, -8, 0);
                row.Children.Add(browse);
            }
            _body.Children.Add(Labelled(what, row));
            if (s.KindValue == ShortcutKind.Url) _body.Children.Add(Hint("Like https://github.com. Only web addresses (https:// or http://)."));

            if (s.KindValue == ShortcutKind.App)
            {
                _body.Children.Add(Labelled("Arguments (optional)", Field(s.Arguments ?? "", "Arguments", text => { s.Arguments = text; Validate(); })));
                _body.Children.Add(Hint("Passed to the program as they are, e.g. a folder for VS Code to open."));
            }
            _body.Children.Add(problem);
            Validate();

            if (s.CanTargetDisplay) _body.Children.Add(Labelled("Opens on", DisplayChoice(s)));
        }
        else
        {
            BuildChildren(s);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, -8, 8) };
        buttons.Children.Add(TextButton("Cancel", () =>
        {
            if (child) { _childDraft = null; _editChild = -1; Rebuild(); }
            else EndEdit();
        }));
        buttons.Children.Add(TextButton("Done", () => Commit(problem)));
        _body.Children.Add(buttons);
    }

    private void BuildChildren(Shortcut group)
    {
        var kids = group.Children ??= new List<Shortcut>();
        for (int i = 0; i < kids.Count; i++)
        {
            int at = i;
            var k = kids[i];
            _body.Children.Add(Row("", k.Label, Summary(k), ShortcutRulesProblem(k),
                IconButton("", $"Edit {k.Label}", () => { _editChild = at; _childDraft = k.Clone(); Rebuild(); }),
                IconButton("", $"Move {k.Label} up", () => { (kids[at - 1], kids[at]) = (kids[at], kids[at - 1]); Rebuild(); }, enabled: at > 0),
                IconButton("", $"Move {k.Label} down", () => { (kids[at + 1], kids[at]) = (kids[at], kids[at + 1]); Rebuild(); }, enabled: at < kids.Count - 1),
                IconButton("", $"Remove {k.Label}", () => { kids.RemoveAt(at); Rebuild(); })));
        }
        if (kids.Count < LauncherConfig.MaxChildren)
        {
            var add = TextButton("Add to group", () => { _editChild = kids.Count; _childDraft = NewShortcut(); Rebuild(); });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(-8, 2, 0, 0);
            _body.Children.Add(add);
        }
        else _body.Children.Add(Hint($"A group holds up to {LauncherConfig.MaxChildren} shortcuts."));
    }

    /// <summary>Normally / Ask each time / one of the connected displays (remembered for this shortcut).</summary>
    private FrameworkElement DisplayChoice(Shortcut s)
    {
        var displays = DisplayInfo.Current();
        int[] numbers = MonitorMatch.Numbers(displays);
        var options = new List<(string Key, string Text, string Tip)>
        {
            ("normal", "Normally", "Wherever Windows and the app put it"),
            ("ask", "Ask", "Show the display picker every time"),
        };
        var order = Enumerable.Range(0, displays.Count).OrderBy(i => numbers[i]).ToList();
        foreach (int i in order)
            options.Add(($"display:{i}", numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture), MonitorMatch.Describe(displays[i], numbers[i])));

        string current = s.LaunchValue switch
        {
            LaunchMode.Ask => "ask",
            LaunchMode.Preferred when s.Monitor != null && MonitorMatch.Find(s.Monitor, displays) is int d => $"display:{d}",
            LaunchMode.Preferred => "missing",
            _ => "normal",
        };
        if (current == "missing") options.Add(("missing", "Unplugged", "The remembered display isn't connected: this opens normally until it is"));

        var panel = new StackPanel();
        panel.Children.Add(Segments(options, current, key =>
        {
            if (key == "normal") { s.Launch = "normal"; s.Monitor = null; }
            else if (key == "ask") { s.Launch = "ask"; s.Monitor = null; }
            else if (key.StartsWith("display:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(8), out int i) && i < displays.Count)
            {
                s.Launch = "preferred";
                s.Monitor = MonitorRef.From(displays[i]);
            }
        }));
        if (displays.Count < 2) panel.Children.Add(Hint("Connect another display to choose where this opens."));
        else panel.Children.Add(Hint("Either way, Shift+drag past a shortcut (or right-click it) to pick a display just this once. Some apps put themselves back where they were; then Tack leaves them."));
        return panel;
    }

    private void Browse(Shortcut s)
    {
        if (s.KindValue == ShortcutKind.Folder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder" };
            if (_shell.ShowDialog(dialog) != true) return;
            s.Target = dialog.FolderName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a program",
                Filter = "Programs and shortcuts|*.exe;*.lnk;*.appref-ms;*.url;*.bat;*.cmd|All files|*.*",
                DereferenceLinks = false,
                CheckFileExists = true,
            };
            if (_shell.ShowDialog(dialog) != true) return;
            s.Target = dialog.FileName;
        }
        if (s.Label.Length == 0) s.Label = LauncherConfig.DefaultLabel(s);
        Rebuild();
    }

    /// <summary>The shortcut as it will be saved: a website gets its https://; the rest is cleaned by Normalize.</summary>
    private static Shortcut Normalized(Shortcut s)
    {
        var copy = s.Clone();
        if (copy.KindValue == ShortcutKind.Url && ShortcutRules.NormalizeUrl(copy.Target) is { } uri) copy.Target = uri.AbsoluteUri;
        return copy;
    }

    private void Commit(TextBlock problemText)
    {
        var s = Normalized(Editing);
        if (!s.IsGroup)
        {
            // A target that's well formed but not found right now (an unplugged drive) may still be saved;
            // anything malformed may not.
            if (ShortcutRules.Problem(s, AssumeExists.Instance) is { } malformed)
            {
                problemText.Text = malformed;
                problemText.Visibility = Visibility.Visible;
                return;
            }
        }
        if (s.Label.Trim().Length == 0) s.Label = LauncherConfig.DefaultLabel(s);

        if (_childDraft != null)
        {
            var kids = _draft!.Children ??= new List<Shortcut>();
            if (_editChild >= 0 && _editChild < kids.Count) kids[_editChild] = s;
            else if (kids.Count < LauncherConfig.MaxChildren) kids.Add(s);
            _childDraft = null;
            _editChild = -1;
            Rebuild();
            return;
        }

        int slot = _editSlot;
        _draft = null;
        _editSlot = -1;
        _module.Update(c => c.Slots[slot] = s); // rebuilds through ConfigChanged
    }

    private sealed class AssumeExists : IFileProbe
    {
        public static readonly AssumeExists Instance = new();
        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public string ExpandEnvironment(string path) => Environment.ExpandEnvironmentVariables(path);
    }

    // ================= Small builders (existing styles only) =================

    private static Border Card()
    {
        var b = new Border();
        b.SetResourceReference(StyleProperty, "Card");
        return b;
    }

    private static Border Divider()
    {
        var d = new Border { Height = 1 };
        d.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return d;
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, -2, 0, 10) };
        t.SetResourceReference(StyleProperty, "Hint");
        return t;
    }

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement content)
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) };
        l.SetResourceReference(StyleProperty, "Caption");
        p.Children.Add(l);
        p.Children.Add(content);
        return p;
    }

    /// <summary>A text field: the borderless GhostTextBox in a subtle rounded frame.</summary>
    private static Border Field(string text, string name, Action<string> changed)
    {
        var box = new TextBox { Text = text, TextWrapping = TextWrapping.NoWrap, FontSize = 12.5 };
        box.SetResourceReference(StyleProperty, "GhostTextBox");
        AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) => changed(box.Text);
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return frame;
    }

    private static Border Segments(IReadOnlyList<(string Key, string Text, string Tip)> options, string selected, Action<string> chosen)
    {
        var group = "launcher-" + Guid.NewGuid().ToString("N");
        var wrap = new WrapPanel();
        foreach (var (key, text, tip) in options)
        {
            var rb = new RadioButton { Content = text, GroupName = group, ToolTip = tip, IsChecked = key == selected };
            rb.SetResourceReference(StyleProperty, "Segment");
            AutomationProperties.SetName(rb, $"{text}: {tip}");
            rb.Checked += (_, _) => chosen(key);
            wrap.Children.Add(rb);
        }
        var frame = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = wrap };
        frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return frame;
    }

    private static Button TextButton(string text, Action click)
    {
        var b = new Button { Content = text };
        b.SetResourceReference(StyleProperty, "TextButton");
        b.Click += (_, _) => click();
        return b;
    }

    private static Button IconButton(string glyph, string name, Action click, bool enabled = true)
    {
        var b = new Button { Content = glyph, ToolTip = name, IsEnabled = enabled, Width = 26, Height = 26 };
        b.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>One shortcut: its position number, name, what it opens (or why it can't), and its buttons.</summary>
    private static Grid Row(string number, string title, string? detail, string? problem, params Button[] buttons)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var n = new TextBlock { Text = number, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        n.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        g.Children.Add(n);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, detail == null ? "TextTertiaryBrush" : "TextPrimaryBrush");
        text.Children.Add(t);
        if (problem != null || detail != null)
        {
            var d = new TextBlock { Text = problem ?? detail ?? "", TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = problem ?? detail };
            d.SetResourceReference(StyleProperty, "Caption");
            if (problem != null) d.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            text.Children.Add(d);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons) actions.Children.Add(b);
        Grid.SetColumn(actions, 2);
        g.Children.Add(actions);
        AutomationProperties.SetName(g, problem == null ? $"{title}. {detail}" : $"{title}. {problem}");
        return g;
    }
}
