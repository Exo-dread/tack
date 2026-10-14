using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Interop;
using Tack.Model;

namespace Tack.Windows;

/// <summary>
/// The expanded state: a compact acrylic panel with Tasks and Notes.
/// It knows nothing about the orb; AppShell decides where and when it appears.
/// </summary>
public partial class PanelWindow : Window
{
    private const int WM_EXITSIZEMOVE = 0x0232;

    private readonly Workspace _ws;
    private readonly AppShell _shell;
    private readonly DispatcherTimer _toastTimer;
    private IntPtr _hwnd;
    private Action? _undo;
    private bool _settingsOpen;

    /// <summary>Raised after the user finishes dragging or resizing the panel.</summary>
    public event Action? UserMovedOrResized;

    internal PanelWindow(Workspace ws, AppShell shell)
    {
        _ws = ws;
        _shell = shell;
        InitializeComponent();
        DataContext = ws;

        Width = ws.Settings.PanelWidth;
        Height = ws.Settings.PanelHeight;

        TasksPage.Initialize(ws, this);
        NotesPage.Initialize(ws, this);
        SettingsPage.Initialize(ws, shell, this);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => HideToast();

        ws.SummaryChanged += (_, _) => UpdateBadge();
        ws.Settings.PropertyChanged += OnSettingChanged;
        UpdateBadge();
        UpdatePin();

        SelectTab(ws.Settings.Tab, animate: false);
        Loaded += (_, _) => MoveIndicator(animate: false);
    }

    public IntPtr Handle => _hwnd;

    public bool IsSettingsOpen => _settingsOpen;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        WindowEffects.MakeToolWindow(_hwnd);
        WindowEffects.StripWindowMenu(_hwnd);
        WindowEffects.KeepFrameActive(_hwnd);
        WindowEffects.ExcludeFromPeek(_hwnd);
        ApplyGlass(_ws.Settings.Glass);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_EXITSIZEMOVE)
        {
            _ws.Settings.PanelWidth = ActualWidth;
            _ws.Settings.PanelHeight = ActualHeight;
            UserMovedOrResized?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>Acrylic on Windows 11; an opaque surface otherwise or when the user turns glass off.</summary>
    public void ApplyGlass(bool glass)
    {
        if (_hwnd == IntPtr.Zero) return;
        bool active = WindowEffects.ApplyPanelChrome(_hwnd, glass);
        Surface.Background = (Brush)FindResource(active ? "GlassTintBrush" : "SolidSurfaceBrush");
    }

    // ---- Showing and hiding ----

    /// <summary>Plays the entrance: content grows out of the corner the orb sits in.</summary>
    public void PlayEntrance(bool fromRight, bool fromBottom)
    {
        Chrome.RenderTransformOrigin = new Point(fromRight ? 1 : 0, fromBottom ? 1 : 0);
        Anim.Fade(Chrome, 1, 170, from: 0);
        Anim.Scale(Chrome, 1, 220, Anim.EaseOut, from: 0.96);
        FocusDefault();
    }

    public void PlayExit(Action completed)
    {
        Anim.Fade(Chrome, 0, 90);
        Anim.Scale(Chrome, 0.97, 100, Anim.EaseIn, completed: completed);
    }

    /// <summary>Puts the caret where you'd want to type: the add-task box or the current note.</summary>
    public void FocusDefault()
    {
        if (IsSettingsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_ws.Settings.Tab == "notes") NotesPage.FocusEditor();
            else TasksPage.FocusInput();
        });
    }

    // ---- Tabs ----

    public void SelectTab(string tab, bool animate = true)
    {
        CloseSettings(animate: false);
        _ws.Settings.Tab = tab;
        bool notes = tab == "notes";
        (notes ? NotesTab : TasksTab).IsChecked = true;

        UIElement show = notes ? NotesPage : TasksPage, hide = notes ? TasksPage : NotesPage;
        if (show.Visibility != Visibility.Visible)
        {
            hide.Visibility = Visibility.Collapsed;
            show.Visibility = Visibility.Visible;
            if (animate)
            {
                Anim.Fade(show, 1, 160, from: 0);
                Anim.Slide(show, notes ? 14 : -14, 0, 220);
            }
        }
        MoveIndicator(animate);
        if (animate) FocusDefault();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string tab = sender == NotesTab ? "notes" : "tasks";
        if (tab != _ws.Settings.Tab || IsSettingsOpen) SelectTab(tab);
    }

    private void MoveIndicator(bool animate)
    {
        if (!IsLoaded) return;
        UpdateLayout();
        var tab = _ws.Settings.Tab == "notes" ? NotesTab : TasksTab;
        double x = tab.TranslatePoint(new Point(tab.ActualWidth / 2 - TabIndicator.Width / 2, 0), Header).X;
        TabIndicator.Opacity = IsSettingsOpen ? 0 : 1;
        if (animate) Anim.To(TabIndicator, Canvas.LeftProperty, x, 220);
        else Anim.Set(TabIndicator, Canvas.LeftProperty, x);
    }

    private void UpdateBadge()
    {
        int n = _ws.OpenCount;
        TasksBadge.Text = n > 0 ? n.ToString() : "";
        if (IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveIndicator(animate: true));
    }

    // ---- Settings page ----

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (IsSettingsOpen) CloseSettings();
        else OpenSettings();
    }

    private FrameworkElement CurrentPage => _ws.Settings.Tab == "notes" ? NotesPage : TasksPage;

    public void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;

        // The panel is see-through, so the page underneath must step out of the way.
        var page = CurrentPage;
        Anim.Fade(page, 0, 90, () => { if (_settingsOpen) page.Visibility = Visibility.Hidden; });

        SettingsButton.Foreground = (Brush)FindResource("AccentBrush");
        SettingsPage.Visibility = Visibility.Visible;
        SettingsPage.Refresh();
        Anim.Fade(SettingsPage, 1, 160, from: 0);
        Anim.Slide(SettingsPage, 24, 0, 220);
        TabIndicator.Opacity = 0;
        TasksTab.IsChecked = NotesTab.IsChecked = false;
        SettingsPage.Focus();
    }

    public void CloseSettings(bool animate = true)
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        SettingsPage.CancelHotkeyCapture();
        SettingsButton.ClearValue(ForegroundProperty);

        var page = CurrentPage;
        page.Visibility = Visibility.Visible;
        if (animate)
        {
            Anim.Fade(SettingsPage, 0, 100, () => { if (!_settingsOpen) SettingsPage.Visibility = Visibility.Collapsed; });
            Anim.Fade(page, 1, 160, from: 0);
            Anim.Slide(page, -14, 0, 220);
        }
        else
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            Anim.Set(page, OpacityProperty, 1);
        }
        (_ws.Settings.Tab == "notes" ? NotesTab : TasksTab).IsChecked = true;
        MoveIndicator(animate);
        if (animate) FocusDefault();
    }

    // ---- Pin ----

    private void Pin_Changed(object sender, RoutedEventArgs e) => _ws.Settings.Pinned = PinButton.IsChecked == true;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.Pinned)) UpdatePin();
    }

    private void UpdatePin()
    {
        bool pinned = _ws.Settings.Pinned;
        PinButton.IsChecked = pinned;
        PinGlyph.Text = ((char)(pinned ? 0xE841 : 0xE718)).ToString(); // PinnedFill / Pin
        PinButton.ToolTip = pinned
            ? "Pinned: stays open over other apps. Click to let it fold away when you click elsewhere."
            : "Pin open over other apps";
    }

    // ---- Header ----

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => _shell.Collapse();

    // ---- Toast + undo ----

    public void ShowToast(string message, Action? undo = null)
    {
        _undo = undo;
        ToastText.Text = message;
        ToastUndo.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        Anim.Fade(Toast, 1, 140, from: 0);
        Anim.Slide(Toast, 0, 10, 220);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _undo = null;
        if (Toast.Visibility == Visibility.Visible)
            Anim.Fade(Toast, 0, 160, () => { if (_undo == null) Toast.Visibility = Visibility.Collapsed; });
    }

    public bool Undo()
    {
        if (_undo == null) return false;
        var undo = _undo;
        HideToast();
        undo();
        return true;
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e) => Undo();

    /// <summary>Alt+F4 and friends fold the panel away instead of destroying it.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shell.IsQuitting)
        {
            e.Cancel = true;
            _shell.Collapse();
        }
        base.OnClosing(e);
    }

    // ---- Keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1 or Key.NumPad1: SelectTab("tasks"); e.Handled = true; return;
                case Key.D2 or Key.NumPad2: SelectTab("notes"); e.Handled = true; return;
                case Key.OemComma: OpenSettings(); e.Handled = true; return;
                // Text boxes keep their own undo; once they have nothing left, Ctrl+Z undoes the last delete.
                case Key.Z when (Keyboard.FocusedElement is not TextBox box || !box.CanUndo) && Undo(): e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (IsSettingsOpen) CloseSettings();
            else _shell.Collapse();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
