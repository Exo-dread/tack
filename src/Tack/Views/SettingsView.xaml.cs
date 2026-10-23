using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Sync;
using Tack.Windows;

namespace Tack.Views;

/// <summary>A handful of preferences. If it isn't here, Tack decided it so you don't have to.</summary>
public partial class SettingsView : UserControl
{
    private Workspace _ws = null!;
    private SpaceView _view = null!;
    private AppShell _shell = null!;
    private PanelWindow _panel = null!;
    private bool _capturing;
    private bool _rebuilding;
    private readonly Dictionary<UIElement, (string Id, string Part)> _spaceParts = new();

    public SettingsView()
    {
        InitializeComponent();
    }

    internal void Initialize(Workspace ws, SpaceView view, AppShell shell, PanelWindow panel)
    {
        _ws = ws;
        _view = view;
        _shell = shell;
        _panel = panel;
        view.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpaceView.Tabs)) BuildSpaceRows(); };
        BuildSpaceRows();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Tack {version?.Major}.{version?.Minor}.{version?.Build}";
        DataPath.Text = shell.DataFolder;

        if (!WindowEffects.SupportsSystemBackdrop)
        {
            GlassToggle.IsEnabled = false;
            GlassHint.Text = "Frosted glass needs Windows 11 (version 22H2 or later).";
        }

        // Swatches show the shade the current theme really uses, so rebuild them when it changes.
        ThemeManager.Changed += () =>
        {
            BuildSwatches();
            Refresh();
        };

        shell.Sync.StatusChanged += ShowSync;
        ShowSync();

        LauncherHost.Content = new Tack.Launcher.LauncherSettingsView(shell.Launcher, shell, panel);

        BuildSwatches();
        Refresh();
    }

    public void Refresh()
    {
        if (_ws == null) return;
        ShowHotkey();
        ShowSync();
        foreach (RadioButton choice in ThemeChoices.Children)
            choice.IsChecked = (string)choice.Tag == _ws.Settings.Theme;
        foreach (RadioButton swatch in Swatches.Children)
            swatch.IsChecked = string.Equals((string)swatch.Tag, _ws.Settings.Accent, StringComparison.OrdinalIgnoreCase);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_ws != null && sender is RadioButton { Tag: string theme }) _ws.Settings.Theme = theme;
    }

    // ---- Spaces ----

    /// <summary>
    /// A row per space, in their order: Main (its name), then each space (its name, up, down,
    /// delete). Rebuilt whenever spaces change; the control you were on keeps the focus, and a
    /// name being typed keeps what's typed so far.
    /// </summary>
    private void BuildSpaceRows()
    {
        (string Id, string Part)? focused = null;
        string? draft = null;
        int caret = 0;
        if (Keyboard.FocusedElement is UIElement f && _spaceParts.TryGetValue(f, out var part))
        {
            focused = part;
            if (f is TextBox box)
            {
                draft = box.Text;
                caret = box.CaretIndex;
            }
        }

        _rebuilding = true;
        try
        {
            SpaceRows.Children.Clear();
            _spaceParts.Clear();
            var tabs = _view.Tabs;
            UIElement? refocus = null;
            for (int i = 0; i < tabs.Count; i++)
            {
                int index = i;
                var (id, name) = tabs[i];
                bool main = id == SpaceField.Main;
                if (i > 0)
                {
                    var line = new Border { Height = 1, Margin = new Thickness(6, 0, 0, 0) };
                    line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
                    SpaceRows.Children.Add(line);
                }
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameBox = new TextBox
                {
                    Style = (Style)FindResource("SpaceNameBox"),
                    Text = focused == (id, "name") && draft != null ? draft : name,
                    Tag = main ? "Main" : "Untitled",
                    ToolTip = main ? "Main holds everything not put in a space. Rename it if you like." : null,
                };
                AutomationProperties.SetName(nameBox, $"Name of {name}");
                nameBox.PreviewKeyDown += (_, e) => SpaceName_PreviewKeyDown(id, nameBox, e);
                nameBox.LostKeyboardFocus += (_, _) => CommitSpaceName(id, nameBox);
                _spaceParts[nameBox] = (id, "name");
                row.Children.Add(nameBox);
                if (focused == (id, "name")) refocus = nameBox;

                if (!main)
                {
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(buttons, 1);
                    var up = SpaceButton("", $"Move {name} up", index > 0, () => _ws.MoveSpace(id, index - 1));
                    var down = SpaceButton("", $"Move {name} down", index < tabs.Count - 1, () => _ws.MoveSpace(id, index + 1));
                    var delete = SpaceButton("", $"Delete {name}", true, () => DeleteSpace(id, name));
                    _spaceParts[up] = (id, "up");
                    _spaceParts[down] = (id, "down");
                    _spaceParts[delete] = (id, "delete");
                    buttons.Children.Add(up);
                    buttons.Children.Add(down);
                    buttons.Children.Add(delete);
                    row.Children.Add(buttons);
                    // A move button that can't go further hands the focus to the other one.
                    if (focused is { } p && p.Id == id && p.Part is "up" or "down")
                        refocus = (p.Part == "up" ? up : down).IsEnabled ? (p.Part == "up" ? up : down) : p.Part == "up" ? down : up;
                }
                SpaceRows.Children.Add(row);
            }

            if (refocus != null)
            {
                refocus.Focus();
                if (refocus is TextBox box) box.CaretIndex = Math.Min(caret, box.Text.Length);
            }
            else if (focused != null)
            {
                NewSpaceButton.Focus(); // its row is gone
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private Button SpaceButton(string glyph, string name, bool enabled, Action click)
    {
        var button = new Button { Style = (Style)FindResource("SpaceButton"), Content = glyph, ToolTip = name, IsEnabled = enabled };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void SpaceName_PreviewKeyDown(string id, TextBox box, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitSpaceName(id, box);
                (Keyboard.FocusedElement as TextBox)?.SelectAll(); // the row may have been rebuilt
                e.Handled = true;
                break;
            case Key.Escape:
                // Esc puts back a name being changed; with nothing to put back, it leaves Settings as always.
                if (box.Text == _view.NameOf(id)) break;
                box.Text = _view.NameOf(id);
                box.SelectAll();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Keeps a new name (cleaned to one line, as every device keeps it). Main called "Main", or
    /// nothing, is simply Main again; another space can't be left without a name.
    /// </summary>
    private void CommitSpaceName(string id, TextBox box)
    {
        if (_rebuilding) return;
        string text = SpaceField.CleanName(box.Text);
        // The box shows the name as kept before the rename rebuilds the rows (which keep what's in it).
        if (id == SpaceField.Main)
        {
            var record = _ws.SpaceById(SpaceField.Main);
            string own = record is { Deleted: false } ? record.Name : "";
            string want = text == "Main" ? "" : text;
            box.Text = want.Length > 0 ? want : "Main";
            // Renaming never moves a space (Main's first name gives it a record without a place).
            if (want != own && (want.Length > 0 || record != null)) _ws.RenameSpace(SpaceField.Main, want);
        }
        else if (text.Length > 0 && _ws.SpaceById(id) is { Deleted: false } space)
        {
            box.Text = text;
            if (space.Name != text) _ws.RenameSpace(id, text);
        }
        else
        {
            box.Text = _view.NameOf(id);
        }
    }

    private void DeleteSpace(string id, string name)
    {
        var undo = _ws.DeleteSpace(id);
        _panel.ShowToast($"Deleted {SpaceView.ToastName(name)}", undo);
    }

    private void NewSpace_Click(object sender, RoutedEventArgs e)
    {
        NewSpaceButton.Visibility = Visibility.Collapsed;
        NewSpaceBox.Clear();
        NewSpaceBox.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NewSpaceBox.Focus());
    }

    private void NewSpaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string name = NewSpaceBox.Text;
                if (SpaceField.CleanName(name).Length > 0) _ws.AddSpace(name);
                EndNewSpace();
                e.Handled = true;
                break;
            case Key.Escape:
                EndNewSpace();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Clicking elsewhere keeps a name that was typed: the space is made (Esc is the way to drop it).</summary>
    private void NewSpaceBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (NewSpaceBox.Visibility != Visibility.Visible) return;
        NewSpaceBox.Visibility = Visibility.Collapsed;
        NewSpaceButton.Visibility = Visibility.Visible;
        string name = NewSpaceBox.Text;
        NewSpaceBox.Clear();
        if (SpaceField.CleanName(name).Length > 0) _ws.AddSpace(name);
    }

    private void EndNewSpace()
    {
        // The box goes first, so moving the focus away from it doesn't count as leaving a name behind.
        NewSpaceBox.Visibility = Visibility.Collapsed;
        NewSpaceBox.Clear();
        NewSpaceButton.Visibility = Visibility.Visible;
        NewSpaceButton.Focus();
    }

    // ---- Accent swatches ----

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var (name, value, _) in AccentPalette.Presets)
        {
            var color = AccentPalette.Resolve(value, ThemeManager.IsLight);
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(color),
                Tag = value,
                GroupName = "Accent",
                ToolTip = value == "system" ? "Windows accent colour" : name,
                Margin = new Thickness(1, 0, 0, 0),
            };
            if (value == "system")
            {
                // A small "A" marks the swatch that follows Windows.
                swatch.Content = new TextBlock
                {
                    Text = "A", FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = AccentPalette.PrefersWhiteText(color) ? Brushes.White : Brushes.Black,
                    IsHitTestVisible = false,
                };
            }
            swatch.Checked += (_, _) => _ws.Settings.Accent = value;
            Swatches.Children.Add(swatch);
        }
    }

    // ---- Shortcut recorder ----

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) { CancelHotkeyCapture(); return; }
        _capturing = true;
        _shell.SuspendHotkey();
        HotkeyButton.Tag = "capturing";
        HotkeyButton.Content = "Press keys…";
        SetStatus("Press a combination with Ctrl, Alt, Shift or Win. Esc cancels, Backspace turns it off.");
        HotkeyButton.Focus();
    }

    public void CancelHotkeyCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        HotkeyButton.Tag = null;
        _shell.ResumeHotkey();
        ShowHotkey();
        SetStatus(null);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.Escape && mods == ModifierKeys.None) { CancelHotkeyCapture(); return; }

        if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None)
        {
            Finish("", "Shortcut turned off.");
            return;
        }

        var hotkey = new Hotkey(mods, key);
        if (Hotkey.IsModifierKey(key))
        {
            HotkeyButton.Content = mods == ModifierKeys.None ? "Press keys…" : string.Join("+", ModifierNames(mods)) + "+…";
            return;
        }
        if (!hotkey.IsValid)
        {
            SetStatus("Include Ctrl, Alt or Win so it doesn't clash with typing.");
            return;
        }

        if (_shell.TryUseHotkey(hotkey)) Finish(hotkey.ToString(), null);
        else SetStatus($"{hotkey} is already used by another app. Try another.");
    }

    private void Finish(string hotkey, string? status)
    {
        _capturing = false;
        HotkeyButton.Tag = null;
        _ws.Settings.Hotkey = hotkey;
        _shell.ResumeHotkey();
        ShowHotkey();
        SetStatus(status);
    }

    private void ShowHotkey()
    {
        string text = _ws.Settings.Hotkey;
        HotkeyButton.Content = string.IsNullOrEmpty(text) ? "Off" : text;
        if (_capturing) return;
        SetStatus(!string.IsNullOrEmpty(text) && !_shell.HotkeyActive
            ? $"{text} is taken by another app, so it isn't active. Click to choose another."
            : null);
    }

    private void SetStatus(string? text)
    {
        HotkeyStatus.Text = text ?? "";
        HotkeyStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static IEnumerable<string> ModifierNames(ModifierKeys mods)
    {
        if (mods.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (mods.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (mods.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (mods.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    // ---- Sync ----

    private void ShowSync()
    {
        var sync = _shell.Sync;
        var st = sync.Status;
        bool on = sync.Enabled;
        SyncOnButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SyncChooseButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SyncNowButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncOffButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DataIntro.Text = on
            ? "Everything is on this PC, and in your OneDrive while sync is on. No Tack account, no tracking."
            : "Everything stays on this PC. No account, no cloud, no tracking.";

        // Tack sees the folder, not the cloud: "in step with the folder" is what it can vouch for.
        string? line = on ? st.Phase switch
        {
            SyncPhase.Syncing => "Syncing…",
            SyncPhase.Synced => st.LastSync > 0 ? $"In step with the sync folder, {At(st.LastSync)}." : "In step with the sync folder.",
            SyncPhase.Waiting => st.Message ?? "Waiting for the sync folder.",
            SyncPhase.Error => st.Message ?? "Sync stopped.",
            _ => null,
        } : st.Message;
        if (on && st.Phase is SyncPhase.Synced or SyncPhase.Syncing)
        {
            if (st.Peers.Count > 0)
                line += $"\nOther devices: {string.Join(", ", st.Peers.Select(p => $"{(p.Name.Length > 0 ? p.Name : "another device")} (last change {At(p.WrittenMs)})"))}.";
            line += "\nThe OneDrive app carries the files: if changes don't arrive, check it's running.";
        }
        foreach (var (device, problem) in on ? st.Problems : [])
        {
            string who = st.Peers.FirstOrDefault(p => p.Device == device)?.Name is { Length: > 0 } n ? n : "another device";
            line += problem == Tack.Sync.DeviceFileProblem.Newer
                ? $"\n{who} has a newer Tack. Update Tack here to keep syncing with it."
                : $"\nThe sync file from {who} couldn't be read. It'll be written again the next time that device syncs.";
        }
        if (SyncStatusText.Text != (line ?? ""))
        {
            SyncStatusText.Text = line ?? "";
            // Screen readers hear the new status (WPF announces a live region only when told).
            if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(SyncStatusText) is { } peer)
                peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
        SyncStatusText.Visibility = string.IsNullOrEmpty(line) ? Visibility.Collapsed : Visibility.Visible;
        SyncStatusText.SetResourceReference(TextBlock.ForegroundProperty, on && st.Phase == SyncPhase.Error ? "DangerBrush" : "TextPrimaryBrush");
        SyncFolderText.Text = on ? _ws.Settings.SyncFolder : "";
        SyncFolderText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"at 14:05", "yesterday at 14:05", "on 12 Oct at 14:05": a time, which (unlike "3 min ago") never goes stale.</summary>
    private static string At(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var today = DateTime.Today;
        string clock = t.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (t.Date == today) return $"at {clock}";
        if (t.Date == today.AddDays(-1)) return $"yesterday at {clock}";
        return $"on {t.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)} at {clock}";
    }

    private async void SyncOn_Click(object sender, RoutedEventArgs e)
    {
        SyncOnButton.IsEnabled = false;
        try { await _shell.Sync.TurnOnAsync(); }
        finally { SyncOnButton.IsEnabled = true; }
    }

    private async void SyncChoose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder to sync through",
            InitialDirectory = Environment.GetEnvironmentVariable("OneDrive") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (_shell.ShowDialog(dialog) != true) return;
        await _shell.Sync.TurnOnAsync(dialog.FolderName);
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e) => _shell.Sync.SyncNow();

    private void SyncOff_Click(object sender, RoutedEventArgs e) => _shell.Sync.TurnOff();

    // ---- Data and quit ----

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _shell.OpenDataFolder();

    private void Import_Click(object sender, RoutedEventArgs e) => _shell.ImportFile();

    private void Quit_Click(object sender, RoutedEventArgs e) => _shell.Quit();
}
