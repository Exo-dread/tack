using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Windows;

namespace Tack.Views;

/// <summary>A handful of preferences. If it isn't here, Tack decided it so you don't have to.</summary>
public partial class SettingsView : UserControl
{
    private Workspace _ws = null!;
    private AppShell _shell = null!;
    private bool _capturing;

    public SettingsView()
    {
        InitializeComponent();
    }

    internal void Initialize(Workspace ws, AppShell shell, PanelWindow panel)
    {
        _ws = ws;
        _shell = shell;

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
        SyncPrivacy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DataIntro.Text = on
            ? "Everything is on this PC, and in your OneDrive while sync is on. No Tack account, no tracking."
            : "Everything stays on this PC. No account, no cloud, no tracking.";

        string? line = on ? st.Phase switch
        {
            SyncPhase.Syncing => "Syncing…",
            SyncPhase.Synced => st.LastSync > 0 ? $"Up to date · {Ago(st.LastSync)}" : "Up to date",
            SyncPhase.Waiting => st.Message ?? "Waiting for the sync folder.",
            SyncPhase.Error => st.Message ?? "Sync stopped.",
            _ => null,
        } : st.Message;
        if (on && st.Phase is SyncPhase.Synced or SyncPhase.Syncing && st.Peers.Count > 0)
            line += $"\nAlso syncing: {string.Join(", ", st.Peers.Select(p => $"{(p.Name.Length > 0 ? p.Name : "another device")} ({Ago(p.WrittenMs)})"))}.";
        foreach (var (device, problem) in on ? st.Problems : [])
        {
            string who = st.Peers.FirstOrDefault(p => p.Device == device)?.Name is { Length: > 0 } n ? n : "another device";
            line += problem == Tack.Sync.DeviceFileProblem.Newer
                ? $"\n{who} has a newer Tack. Update Tack here to keep syncing with it."
                : $"\nThe sync file from {who} couldn't be read. It'll be written again the next time that device syncs.";
        }
        SyncStatusText.Text = line ?? "";
        SyncStatusText.Visibility = string.IsNullOrEmpty(line) ? Visibility.Collapsed : Visibility.Visible;
        SyncStatusText.SetResourceReference(TextBlock.ForegroundProperty, on && st.Phase == SyncPhase.Error ? "DangerBrush" : "TextPrimaryBrush");
        SyncFolderText.Text = on ? _ws.Settings.SyncFolder : "";
        SyncFolderText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Ago(long ms)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
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
