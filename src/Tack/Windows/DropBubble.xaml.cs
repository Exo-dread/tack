using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Drop;
using Tack.Interop;
using static Tack.Interop.NativeMethods;

namespace Tack.Windows;

/// <summary>
/// The bubble beside the orb for a drop: "Drop to add a task" while something Tack can take is dragged over
/// it, then "Added 3 tasks" with Undo once it's dropped. It never takes focus, so the app you dragged from
/// stays where you are; it lets clicks through until it has Undo to offer. Made the first time something is
/// dragged over the orb.
/// </summary>
public partial class DropBubble : Window, IDropBubble
{
    private const double Inset = 12; // the Border's margin (room for its shadow), DIPs
    private const double Gap = 6;    // space between bead and bubble, DIPs

    private readonly DispatcherTimer _hide = new();
    private IntPtr _hwnd;
    private bool _wanted;
    private Action? _undo;

    /// <summary>The orb it sits beside.</summary>
    internal OrbWindow? Orb { get; init; }

    public DropBubble()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Dismiss();
        };
    }

    /// <summary>Undo, clicked: the drop's own undo, handed back so it can be checked it's still the one on offer.</summary>
    public event Action<Action>? UndoRequested;

    /// <summary>The Undo on offer, while the bubble shows one.</summary>
    public Action? OfferedUndo => _wanted ? _undo : null;

    public bool IsShown => _wanted && IsVisible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(_hwnd, noActivate: true, clickThrough: true);
        WindowEffects.ExcludeFromPeek(_hwnd);
        HwndSource.FromHwnd(_hwnd)!.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        });
    }

    /// <summary>What it says: a title, an optional second line, and Undo or not. Muted for a drop that can't be made.</summary>
    internal void Fill(string title, string? caption, bool undo, bool muted)
    {
        Heading.Text = title;
        Heading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, muted ? "TextSecondaryBrush" : "TextPrimaryBrush");
        Dot.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        Caption.Text = caption ?? "";
        Caption.Margin = new Thickness(muted ? 0 : Dot.Width + Dot.Margin.Right, 2, 0, 0);
        Caption.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        Bubble.Padding = new Thickness(12, 7, undo ? 6 : 14, 8);
    }

    /// <summary>While something is dragged over the orb: what letting go would do (or why it can't).</summary>
    public void ShowHint(string title, string? caption, bool refused)
    {
        _hide.Stop();
        _undo = null;
        Fill(title, caption, undo: false, muted: refused);
        SetClickable(false);
        Present();
    }

    /// <summary>After a drop: what it added, with Undo, for as long as that Undo is on offer.</summary>
    public void ShowDone(string title, string? caption, Action undo, TimeSpan showFor)
    {
        _undo = undo;
        Fill(title, caption, undo: true, muted: false);
        SetClickable(true);
        Present();
        _hide.Interval = showFor;
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>A short word after a drop that added nothing.</summary>
    public void ShowNote(string title, TimeSpan showFor)
    {
        _undo = null;
        Fill(title, null, undo: false, muted: true);
        SetClickable(false);
        Present();
        _hide.Interval = showFor;
        _hide.Stop();
        _hide.Start();
    }

    public void Dismiss()
    {
        _hide.Stop();
        _wanted = false;
        _undo = null;
        SetClickable(false);
        if (!IsVisible) return;
        StandIn.Take(); // what it says now
        StandIn.Stand();
        Anim.Fade(this, 0, 90, () =>
        {
            if (_wanted) return;
            Hide();
            StandIn.Live(keep: false);
        });
    }

    /// <summary>The bubble's picture, which stands in for it while it slides and fades (see <see cref="Controls.StandIn"/>).</summary>
    internal Controls.StandIn StandIn => _standIn ??= new Controls.StandIn(Bubble, Root, Picture);

    private Controls.StandIn? _standIn;

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var undo = _undo;
        Dismiss();
        if (undo != null) UndoRequested?.Invoke(undo);
    }

    private void SetClickable(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = GetExStyle(_hwnd);
        long next = on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        if (next != ex) SetExStyle(_hwnd, next);
        IsHitTestVisible = on;
    }

    private void Present()
    {
        if (Orb is not { } orb) return;
        _wanted = true;
        var (cx, cy) = orb.Center;
        if (!IsVisible)
        {
            Opacity = 0;
            Left = -32000; // off-screen until measured
            Show();
            SetClickable(_undo != null);
        }
        // Onto the orb's monitor first so WPF picks up its DPI, then placed precisely once measured.
        ScreenGeometry.Place(_hwnd, cx, cy);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    private void Place()
    {
        if (!_wanted || Orb == null) return;
        UpdateLayout();
        var (cx, cy) = Orb.Center;
        var mon = ScreenGeometry.FromPoint(cx, cy);
        var me = ScreenGeometry.GetWindowRect(_hwnd);
        int half = (int)Math.Round(Orb.DiameterPx * 1.1) / 2; // the bead grows a little while it's a target
        int inset = mon.Px(Inset), gap = mon.Px(Gap);

        bool toLeft = cx > mon.Work.X + mon.Work.Width / 2;
        int x = toLeft ? cx - half - gap - me.Width + inset : cx + half + gap - inset;
        int y = cy - me.Height / 2;
        var r = ScreenGeometry.Clamp(new Int32Rect(x, y, me.Width, me.Height), mon.Work, -inset);
        ScreenGeometry.Place(_hwnd, r.X, r.Y);
        WindowEffects.BringToTopmost(_hwnd);

        if (Opacity < 0.5)
        {
            // Coming in: its picture slides and fades in, then the live bubble takes over (the picture kept for the fade out).
            StandIn.Take();
            StandIn.Stand();
            Anim.Fade(this, 1, 120);
            var (_, slide) = Anim.Transforms(Picture);
            Anim.To(slide, System.Windows.Media.TranslateTransform.XProperty, 0, 180, from: toLeft ? 8 : -8, completed: () =>
            {
                if (_wanted && StandIn.IsStanding) StandIn.Live(keep: true);
            });
        }
        else
        {
            // Already there (it now says something else, or was fading out): the live bubble, as it is now.
            if (StandIn.IsStanding) StandIn.Live(keep: true);
            Anim.Fade(this, 1, 120);
        }
    }
}
