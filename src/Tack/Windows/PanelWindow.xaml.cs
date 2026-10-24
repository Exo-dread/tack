using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Interop;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Windows;

/// <summary>
/// The expanded state: a compact acrylic panel with Tasks and Notes.
/// It knows nothing about the orb; AppShell decides where and when it appears.
/// </summary>
public partial class PanelWindow : Window, IFadingWindow
{
    private const int WM_SIZING = 0x0214;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const double HeaderHeight = 48;
    private const double SettingsMinHeight = 470; // settings never open in a cramped panel

    private readonly Workspace _ws;
    private readonly SpaceView _view;
    private readonly AppShell _shell;
    private readonly DispatcherTimer _toastTimer;
    private IntPtr _hwnd;
    private Action? _undo;
    private bool _settingsOpen;
    private bool _userResized;
    private bool _sizeChangePending;
    private GlideTiming? _sizeChangeInStep; // the pending request is a row folding away: glide in step with it
    private IReadOnlyList<SpaceTab> _stripTabs = [];
    private int _stripLimit = SpaceView.MaxListed; // spaces the strip draws ("More spaces" draws as many again)
    private bool _syncingStrip; // the strip is being brought in line with the space on show, not clicked
    private double _heldHeight; // > 0: Tasks keeps this height after a space switch, until the panel next opens

    /// <summary>
    /// Set by the shell: the panel grows and shrinks upward from its bottom edge (the orb is in the
    /// lower half of the screen), so a change of height moves the strip.
    /// </summary>
    internal bool AnchoredBottom { get; set; }

    /// <summary>Raised when the user starts dragging or resizing the panel.</summary>
    public event Action? SizeMoveStarted;

    /// <summary>Raised after the user finishes dragging or resizing the panel.</summary>
    public event Action? UserMovedOrResized;

    /// <summary>
    /// Raised when the panel would like a different size: tab switch, settings, or the task list grew or
    /// shrank. With a timing when the change is a row folding away: the panel's glide keeps in step with it.
    /// </summary>
    internal event Action<GlideTiming?>? PreferredSizeChanged;

    internal PanelWindow(Workspace ws, SpaceView view, AppShell shell)
    {
        _ws = ws;
        _view = view;
        _shell = shell;
        InitializeComponent();
        DataContext = ws;

        bool notes = ws.Settings.Tab == "notes";
        Width = notes ? ws.Settings.NotesWidth : ws.Settings.TasksWidth;
        Height = notes ? ws.Settings.NotesHeight : ws.Settings.TasksHeight ?? 360;

        TasksPage.Initialize(ws, view, this);
        TasksPage.ContentHeightChanged += () => { if (IsTasksAutoSized) RequestPreferredSize(); };
        TasksPage.RowFolding += ms => { if (IsTasksAutoSized) RequestPreferredSize(new GlideTiming(WindowBoundsAnimator.Clock, ms, EaseIn: true)); };
        NotesPage.Initialize(ws, view, this);
        SettingsPage.Initialize(ws, view, shell, this);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => HideToast();

        view.SummaryChanged += (_, _) => UpdateBadge();
        view.PropertyChanged += OnViewChanged;
        ws.Settings.PropertyChanged += OnSettingChanged;
        UpdateBadge();
        UpdatePin();
        UpdateStrip();

        SelectTab(ws.Settings.Tab, animate: false);
        Loaded += (_, _) =>
        {
            MoveIndicator(animate: false);
            RevealShownSpace();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RevealShownSpace();
            else _heldHeight = 0; // folded away: it fits its list again next time
        };
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
        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                IsInSizeMove = true;
                _userResized = false;
                SizeMoveStarted?.Invoke();
                break;
            case WM_SIZING:
                _userResized = true;
                break;
            case WM_EXITSIZEMOVE:
                IsInSizeMove = false;
                if (_userResized)
                {
                    _heldHeight = 0;
                    RememberManualSize();
                }
                UserMovedOrResized?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Acrylic on Windows 11; an opaque surface otherwise or when the user turns glass off. A panel that's
    /// see-through without glass, or faded when idle, is clear instead: nothing behind it but what's on screen.
    /// </summary>
    public void ApplyGlass(bool glass)
    {
        if (_hwnd == IntPtr.Zero) return;
        bool clear = WantsClear(glass);
        // In this order (DWM drops the acrylic otherwise): no longer clear before the frame and glass come back;
        // clear only once they're gone.
        if (!clear && _clear) WindowEffects.SetClear(_hwnd, _clear = false);
        _glassActive = WindowEffects.ApplyPanelChrome(_hwnd, glass, dark: !ThemeManager.IsLight, clear);
        if (clear && !_clear) WindowEffects.SetClear(_hwnd, _clear = true);
        ApplyBackground();
    }

    private bool _glassActive;
    private bool _clear;
    private bool _faded;

    /// <summary>
    /// Nothing behind the panel: while faded when idle, or see-through without glass (Windows 11, where the
    /// slider is; blur-behind itself shows a window's own transparency since Windows 8).
    /// </summary>
    private bool WantsClear(bool glass) =>
        _faded || (WindowEffects.SupportsSystemBackdrop && !glass && _ws.Settings.PanelBackgroundSolid is < 1);

    /// <summary>
    /// The background under everything (Settings → Look → Panel background): the theme's tint over the glass,
    /// or its solid surface, made as solid as the setting says. Only this layer: text and controls never fade.
    /// At the default (no setting) it's the palette's own brush, exactly as it has always been.
    /// </summary>
    public void ApplyBackground()
    {
        string key = _glassActive ? "GlassTintBrush" : "SolidSurfaceBrush";
        double? opacity = _glassActive ? _ws.Settings.PanelBackgroundGlass : _ws.Settings.PanelBackgroundSolid;
        // Without the system backdrop (Windows 10) there's nothing behind the panel to show through.
        if (opacity is not double o || !WindowEffects.SupportsSystemBackdrop)
        {
            Surface.SetResourceReference(Border.BackgroundProperty, key);
            return;
        }
        var color = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Black;
        color.A = (byte)Math.Round(o * 255);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Surface.Background = brush;
    }

    /// <summary>Settings → Look → Panel background moved: a new tint, or (without glass, to or from solid) a new chrome too.</summary>
    public void BackgroundChanged()
    {
        if (WantsClear(_ws.Settings.Glass) != _clear) ApplyGlass(_ws.Settings.Glass);
        else ApplyBackground();
    }

    /// <summary>
    /// The background's opacity before you change it, with glass or without, in the current theme: the
    /// theme's tint (about half) over the glass, solid without. Where Settings → Look's slider starts.
    /// </summary>
    public double DefaultBackground(bool glass) =>
        glass && TryFindResource("GlassTintBrush") is SolidColorBrush tint ? Math.Round(tint.Color.A / 255.0, 2) : 1;

    /// <summary>
    /// Fade when idle: the whole panel at half opacity, see-through (as a solid panel at half strength: frosted
    /// glass can't be half there), and back. Back is always at once; the fade itself eases out unless
    /// <paramref name="animate"/> is false (Windows' animation effects are off).
    /// </summary>
    public void SetFaded(bool faded, bool animate)
    {
        if (faded == _faded) return;
        _faded = faded;
        if (faded)
        {
            ApplyGlass(_ws.Settings.Glass);
            if (animate) Anim.Fade(Root, IdleOpacity, 320, from: 1);
            else Anim.Set(Root, OpacityProperty, IdleOpacity);
        }
        else
        {
            Anim.Set(Root, OpacityProperty, 1);
            ApplyGlass(_ws.Settings.Glass);
        }
    }

    public bool IsFaded => _faded;

    /// <summary>True while nothing is drawn behind the panel (see-through without glass, or faded).</summary>
    internal bool IsClear => _clear;

    /// <summary>True while the frosted glass is drawn (Windows 11, glass on, not faded).</summary>
    internal bool IsGlassActive => _glassActive;

    /// <summary>How opaque the orb and a pinned panel are while faded when idle.</summary>
    public const double IdleOpacity = 0.5;

    // ---- Size: each tab remembers its own; Tasks fits its list until you resize it ----

    /// <summary>True while the user is dragging or resizing the panel.</summary>
    public bool IsInSizeMove { get; private set; }

    private bool IsTasksAutoSized => _ws.Settings.Tab == "tasks" && _ws.Settings.TasksHeight == null;

    /// <summary>The size (in DIPs) the current tab would like, given the tallest an auto-fitting panel may grow.</summary>
    public Size PreferredSize(double maxAutoHeight)
    {
        var s = _ws.Settings;
        var size = s.Tab == "notes"
            ? new Size(s.NotesWidth, s.NotesHeight)
            : new Size(s.TasksWidth, s.TasksHeight ?? (_heldHeight > 0 ? Math.Min(_heldHeight, Math.Max(Settings.MinHeight, maxAutoHeight)) : FitTasksHeight(s.TasksWidth, maxAutoHeight)));
        if (_settingsOpen && size.Height < SettingsMinHeight) size.Height = SettingsMinHeight;
        return size;
    }

    /// <summary>How tall the panel must be to show the whole task list at this width (clamped).</summary>
    private double FitTasksHeight(double width, double maxHeight)
    {
        double strip = Strip.Visibility == Visibility.Visible ? Strip.Height + Strip.Margin.Top + Strip.Margin.Bottom : 0;
        double natural = Math.Ceiling(HeaderHeight + strip + TasksPage.FittedHeight(width));
        return Math.Clamp(natural, Settings.MinHeight, Math.Max(Settings.MinHeight, maxHeight));
    }

    /// <summary>Dragging an edge pins the size of the tab you're on; automatic sizing stops for it.</summary>
    private void RememberManualSize()
    {
        var s = _ws.Settings;
        if (s.Tab == "notes")
        {
            s.NotesWidth = ActualWidth;
            s.NotesHeight = ActualHeight;
        }
        else
        {
            s.TasksWidth = ActualWidth;
            s.TasksHeight = ActualHeight;
        }
    }

    /// <summary>Double-clicking the header: Tasks fits its list again, Notes returns to its default size.</summary>
    public void ResetSize()
    {
        var s = _ws.Settings;
        if (s.Tab == "notes")
        {
            s.NotesWidth = Settings.DefaultWidth;
            s.NotesHeight = Settings.DefaultNotesHeight;
        }
        else
        {
            s.TasksWidth = Settings.DefaultWidth;
            s.TasksHeight = null;
        }
        _heldHeight = 0;
        RequestPreferredSize();
    }

    /// <summary>Coalesces bursts of changes (e.g. every frame of a row animation) into one request per layout pass.</summary>
    private void RequestPreferredSize(GlideTiming? inStep = null)
    {
        if (inStep != null) _sizeChangeInStep = inStep;
        if (_sizeChangePending) return;
        _sizeChangePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _sizeChangePending = false;
            var timing = _sizeChangeInStep;
            _sizeChangeInStep = null;
            PreferredSizeChanged?.Invoke(timing);
        });
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
        if (tab != _ws.Settings.Tab) _heldHeight = 0; // the panel takes the other tab's size anyway
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
        RequestPreferredSize();
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
        int n = _view.OpenCount;
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

        SettingsButton.SetResourceReference(ForegroundProperty, "AccentBrush");
        SettingsPage.Visibility = Visibility.Visible;
        SettingsPage.Refresh();
        Anim.Fade(SettingsPage, 1, 160, from: 0);
        Anim.Slide(SettingsPage, 24, 0, 220);
        TabIndicator.Opacity = 0;
        TasksTab.IsChecked = NotesTab.IsChecked = false;
        UpdateStrip();
        SettingsPage.Focus();
        RequestPreferredSize();
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
        UpdateStrip();
        MoveIndicator(animate);
        RequestPreferredSize();
        if (animate) FocusDefault();
    }

    // ---- Spaces strip ----

    /// <summary>
    /// Shows a space (or All) in the panel and the orb, and remembers it on this PC. What counts is
    /// the choice remembered, not what's on show: while a space that's gone shows Main, choosing Main
    /// is a choice of its own (kept when the space comes back), as in Tack Focus.
    /// </summary>
    public void ShowSpace(string id)
    {
        if ((id == SpaceField.Main ? null : id) == _ws.Settings.SpaceOnShow) return;
        // A Tasks panel that fits its list and grows up from its bottom edge would move the strip
        // out from under the mouse: it keeps its height until it next opens (the list scrolls).
        if (IsVisible && AnchoredBottom && IsTasksAutoSized && !_settingsOpen && _heldHeight <= 0) _heldHeight = ActualHeight;
        _view.Show(id);
        Announce($"Showing {_view.ShownName}");
    }

    /// <summary>Tells screen readers something that changed out of their sight (a space switched by a key, a task moved).</summary>
    internal void Announce(string text)
    {
        if ((UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this)) is { } peer)
            peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "Tack.Spaces");
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SpaceView.Tabs):
                UpdateStrip();
                break;
            case nameof(SpaceView.Shown):
                _syncingStrip = true;
                try
                {
                    foreach (var tab in StripTabs.Children.OfType<RadioButton>())
                    {
                        bool on = (string)tab.Tag == _view.Shown;
                        if (tab.IsChecked != on) tab.IsChecked = on;
                        tab.IsTabStop = on; // Tab stops at the space on show; arrows move along
                    }
                }
                finally
                {
                    _syncingStrip = false;
                }
                RevealShownSpace();
                break;
        }
    }

    /// <summary>Scrolls the strip so the space on show is in sight (after it's built, and whenever the panel opens).</summary>
    private void RevealShownSpace()
    {
        if (Strip.Visibility != Visibility.Visible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Clear of the fades at either edge too.
            if (StripTabs.Children.OfType<RadioButton>().FirstOrDefault(t => t.IsChecked == true) is { } tab)
                tab.BringIntoView(new Rect(-StripFade, 0, tab.ActualWidth + 2 * StripFade, tab.ActualHeight));
            UpdateStripFade();
        });
    }

    private const double StripFade = 20;

    /// <summary>The strip fades out at an edge with more spaces beyond it, so it's plain there's more.</summary>
    private void UpdateStripFade()
    {
        double width = StripScroller.ActualWidth;
        bool left = StripScroller.HorizontalOffset > 0.5;
        bool right = StripScroller.HorizontalOffset < StripScroller.ScrollableWidth - 0.5;
        if (width <= 0 || (!left && !right))
        {
            StripScroller.OpacityMask = null;
            return;
        }
        double f = Math.Min(0.3, StripFade / width);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, f));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        StripScroller.OpacityMask = mask;
    }

    private void StripScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateStripFade();

    /// <summary>The mouse wheel scrolls the strip sideways (it has nothing to scroll up and down).</summary>
    private void StripScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (StripScroller.ScrollableWidth <= 0) return;
        StripScroller.ScrollToHorizontalOffset(StripScroller.HorizontalOffset - e.Delta * 0.4);
        e.Handled = true;
    }

    /// <summary>The strip shows once a second space exists (not over Settings): All, then each space, then +.</summary>
    private void UpdateStrip()
    {
        var visibility = _view.HasSpaces && !_settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (Strip.Visibility != visibility)
        {
            Strip.Visibility = visibility;
            RequestPreferredSize();
            RevealShownSpace();
        }
        if (!_view.HasSpaces || _stripTabs.SequenceEqual(_view.Tabs)) return;
        _stripTabs = _view.Tabs;

        StripTabs.Children.Clear();
        var entries = new List<SpaceTab> { new(SpaceView.All, "All") };
        // So many at a time (only a crafted file or import makes more), and always the one on show.
        var (listed, more) = SpaceView.Listed(_view.Tabs, _stripLimit, _view.Shown);
        entries.AddRange(listed.Select(l => l.Tab));
        for (int i = 0; i < entries.Count; i++)
        {
            var (id, name) = entries[i];
            if (i > 0)
            {
                var dot = new Ellipse { Width = 2.5, Height = 2.5, Margin = new Thickness(1, 1, 1, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, "TextTertiaryBrush");
                StripTabs.Children.Add(dot);
            }
            var tab = new RadioButton
            {
                Style = (Style)FindResource("SpaceTab"),
                GroupName = "Spaces",
                Tag = id,
                Content = new TextBlock { Text = name, MaxWidth = 96, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = id == _view.Shown,
                IsTabStop = id == _view.Shown,
                ToolTip = i <= 9 ? $"{name} (Alt+{i})" : name,
            };
            AutomationProperties.SetName(tab, name);
            if (i <= 9) AutomationProperties.SetAcceleratorKey(tab, $"Alt+{i}");
            tab.Checked += SpaceTab_Checked;
            tab.Click += SpaceTab_Click;
            StripTabs.Children.Add(tab);
        }
        if (more > 0)
        {
            string left = $"{more:N0} more";
            var button = new Button { Style = (Style)FindResource("TextButton"), Content = "More spaces…", FontSize = 12, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 0, 0), ToolTip = left };
            AutomationProperties.SetName(button, $"More spaces, {left}");
            button.Click += MoreSpaces_Click;
            StripTabs.Children.Add(button);
        }
        RevealShownSpace();
    }

    /// <summary>"More spaces" at the end of the strip: as many again, and the first of them takes the focus.</summary>
    private void MoreSpaces_Click(object sender, RoutedEventArgs e)
    {
        int first = _stripLimit;
        _stripLimit += SpaceView.MaxListed;
        _stripTabs = [];
        UpdateStrip();
        if (first < _view.Tabs.Count && StripTabs.Children.OfType<RadioButton>().FirstOrDefault(t => (string)t.Tag == _view.Tabs[first].Id) is { } tab)
        {
            tab.Focus();
            tab.BringIntoView();
        }
    }

    private void SpaceTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingStrip || sender is not RadioButton { Tag: string id }) return;
        bool byMouse = InputManager.Current.MostRecentInputDevice is MouseDevice;
        ShowSpace(id);
        if (byMouse) FocusDefault();
    }

    /// <summary>
    /// A click on the space already checked: Main, standing in for a space that's gone, is chosen
    /// for itself then, and kept when that space comes back (a click that checks a tab has been
    /// handled by <see cref="SpaceTab_Checked"/> already, so this changes nothing more).
    /// </summary>
    private void SpaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id }) ShowSpace(id);
    }

    /// <summary>Arrow keys move along the strip and show what they land on, as in a tab list.</summary>
    private void StripTabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var tabs = StripTabs.Children.OfType<RadioButton>().ToList();
        int i = Keyboard.FocusedElement is RadioButton focused ? tabs.IndexOf(focused) : -1;
        if (i < 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        int next = e.Key switch
        {
            Key.Left or Key.Up => (i + tabs.Count - 1) % tabs.Count,
            Key.Right or Key.Down => (i + 1) % tabs.Count,
            Key.Home => 0,
            Key.End => tabs.Count - 1,
            _ => -1,
        };
        if (next < 0) return;
        tabs[next].Focus();
        tabs[next].IsChecked = true;
        e.Handled = true;
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
                EndNewSpace();
                if (SpaceField.CleanName(name).Length > 0) ShowSpace(_ws.AddSpace(name).Id);
                FocusDefault();
                e.Handled = true;
                break;
            case Key.Escape:
                EndNewSpace();
                FocusDefault();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Clicking elsewhere keeps a name that was typed: the space is made (Esc is the way to drop it).
    /// The space on show stays as it is, since the click was about something else.
    /// </summary>
    private void NewSpaceBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (NewSpaceBox.Visibility != Visibility.Visible) return;
        string name = NewSpaceBox.Text;
        EndNewSpace();
        if (SpaceField.CleanName(name).Length > 0) _ws.AddSpace(name);
    }

    private void EndNewSpace()
    {
        if (NewSpaceBox.Visibility != Visibility.Visible) return;
        NewSpaceBox.Visibility = Visibility.Collapsed;
        NewSpaceButton.Visibility = Visibility.Visible;
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
        if (e.ClickCount == 2)
        {
            ResetSize();
            e.Handled = true;
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => _shell.Collapse();

    // ---- Toast + undo ----

    public void ShowToast(string message, Action? undo = null)
    {
        _undo = undo;
        ToastText.Text = message;
        ToastUndo.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        _toastUp = true;
        // A picture of it slides and fades in; the live toast takes over once it's still.
        ToastStandIn.Take();
        ToastStandIn.Stand();
        Anim.Fade(ToastPicture, 1, 140, from: 0);
        var (_, slide) = Anim.Transforms(ToastPicture);
        Anim.To(slide, TranslateTransform.YProperty, 0, 220, from: 10, completed: () =>
        {
            if (_toastUp && ToastStandIn.IsStanding) ToastStandIn.Live(keep: true); // kept for the fade out
        });
        _toastTimer.Stop();
        _toastTimer.Start();
        ToastChanged?.Invoke();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _undo = null;
        if (!_toastUp) return;
        ToastStandIn.Stand();
        Anim.Fade(ToastPicture, 0, 160, () =>
        {
            if (_undo != null) return;
            _toastUp = false;
            ToastStandIn.Live(keep: false);
            Toast.Visibility = Visibility.Collapsed;
            ToastChanged?.Invoke();
        });
    }

    private bool _toastUp;

    /// <summary>The toast's picture, which stands in for it while it slides and fades (see <see cref="StandIn"/>).</summary>
    internal StandIn ToastStandIn => _toastStandIn ??= new StandIn(Toast, ToastHost, ToastPicture);

    private StandIn? _toastStandIn;

    /// <summary>True while the undo toast is up (the panel doesn't fade when idle meanwhile).</summary>
    public bool IsToastShown => _toastUp;

    /// <summary>Raised when the toast comes up or has gone.</summary>
    public event Action? ToastChanged;

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
        // Alt+0 shows All, Alt+1..9 the first nine spaces, wherever the caret is (a draft stays as it is).
        // Only once there are spaces: until then Alt+digits do what they always did.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System && e.SystemKey is >= Key.D0 and <= Key.D9
            && _view.HasSpaces && !_settingsOpen)
        {
            if (_view.SpaceForKey(e.SystemKey - Key.D0) is { } id) ShowSpace(id);
            e.Handled = true;
            return;
        }
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
