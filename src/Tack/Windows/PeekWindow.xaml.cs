using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tack.Controls;
using Tack.Interop;
using Tack.Model;

namespace Tack.Windows;

/// <summary>
/// Hovering the orb reveals what you're meant to be doing, without opening anything.
/// The bubble is click-through and never takes focus.
/// </summary>
public partial class PeekWindow : Window
{
    private const double Inset = 12;  // the Border's margin (room for its shadow), DIPs
    private const double Gap = 4;     // space between bead and bubble, DIPs
    private IntPtr _hwnd;
    private bool _wanted;

    public PeekWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(_hwnd, noActivate: true, clickThrough: true);
        WindowEffects.ExcludeFromPeek(_hwnd);
    }

    public void ShowFor(OrbWindow orb, SpaceView view, string hotkey)
    {
        _wanted = true;
        Fill(view, hotkey);

        var (cx, cy) = orb.Center;
        if (!IsVisible)
        {
            Opacity = 0;
            Left = -32000; // off-screen until measured
            Show();
        }
        // Move onto the orb's monitor first so WPF picks up the right DPI, then place precisely once measured.
        ScreenGeometry.Place(_hwnd, cx, cy);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Place(orb));
    }

    public void Dismiss()
    {
        _wanted = false;
        if (!IsVisible) return;
        StandIn.Stand();
        Anim.Fade(this, 0, 90, () =>
        {
            if (_wanted) return;
            Hide();
            StandIn.Live(keep: false);
        });
    }

    /// <summary>The bubble's picture, which stands in for it while it slides and fades (see <see cref="Controls.StandIn"/>).</summary>
    internal StandIn StandIn => _standIn ??= new StandIn(Bubble, Root, Picture);

    private StandIn? _standIn;

    private void Place(OrbWindow orb)
    {
        if (!_wanted) return;
        UpdateLayout();
        var (cx, cy) = orb.Center;
        var mon = ScreenGeometry.FromPoint(cx, cy);
        var me = ScreenGeometry.GetWindowRect(_hwnd);
        int half = orb.DiameterPx / 2;
        int inset = mon.Px(Inset), gap = mon.Px(Gap);

        bool toLeft = cx > mon.Work.X + mon.Work.Width / 2;
        int x = toLeft ? cx - half - gap - me.Width + inset : cx + half + gap - inset;
        int y = cy - me.Height / 2;
        var r = ScreenGeometry.Clamp(new Int32Rect(x, y, me.Width, me.Height), mon.Work, -inset);
        ScreenGeometry.Place(_hwnd, r.X, r.Y);

        StandIn.Take(); // what it says may have changed since it last showed
        StandIn.Stand();
        Anim.Fade(this, 1, 140);
        var (_, slide) = Anim.Transforms(Picture);
        Anim.To(slide, TranslateTransform.XProperty, 0, 180, from: toLeft ? 8 : -8, completed: () =>
        {
            // Still showing: the live bubble takes over (the picture is kept for the fade out).
            if (_wanted && StandIn.IsStanding) StandIn.Live(keep: true);
        });
    }

    /// <summary>What's on show: a space's (or everything's) focus task and counts, and, once there are spaces, which one.</summary>
    internal void Fill(SpaceView view, string hotkey)
    {
        // The bubble's room for a line of text; a long space name gives way (with an ellipsis).
        double room = Bubble.MaxWidth - Bubble.Padding.Left - Bubble.Padding.Right - Bubble.BorderThickness.Left - Bubble.BorderThickness.Right;
        Label.MaxWidth = Math.Max(0, room - Dot.Width - Dot.Margin.Left - Dot.Margin.Right);

        var focus = view.FocusTask;
        if (view.Now != null)
        {
            Label.Text = "NOW";
            TaskText.Text = view.Now.Text;
        }
        else if (view.NowElsewhere is { } now)
        {
            Label.Text = $"NOW IN {view.NameOf(view.SpaceOf(now)).ToUpper(System.Globalization.CultureInfo.CurrentCulture)}";
            TaskText.Text = now.Text;
        }
        else if (focus != null)
        {
            Label.Text = "UP NEXT";
            TaskText.Text = focus.Text;
        }
        else
        {
            Label.Text = "ALL CLEAR";
            TaskText.Text = view.DoneToday > 0 ? "Everything's done. Nice work." : "Nothing on your list.";
        }

        int open = view.OpenCount, done = view.DoneToday;
        var parts = new List<string>();
        if (open > 0) parts.Add($"{open} open");
        if (done > 0) parts.Add($"{done} done today");
        if (parts.Count == 0 && !string.IsNullOrEmpty(hotkey)) parts.Add($"Click or press {hotkey} to add a task");
        if (view.HasSpaces)
        {
            // "Work  ·  3 open  ·  2 done today": the counts keep their room, and the name takes
            // what's left (at least a third of the line), cut with an ellipsis when it's long.
            Summary.Text = "";
            Summary.Visibility = Visibility.Collapsed;
            SpaceName.Text = view.ShownName;
            SpaceCounts.Text = parts.Count > 0 ? "  ·  " + string.Join("  ·  ", parts) : "";
            var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
            SpaceCounts.MaxWidth = double.PositiveInfinity;
            SpaceCounts.Measure(any);
            SpaceName.MaxWidth = Math.Max(room - SpaceCounts.DesiredSize.Width, Math.Round(room / 3));
            SpaceName.Measure(any);
            SpaceCounts.MaxWidth = Math.Max(0, room - SpaceName.DesiredSize.Width);
            SpaceSummary.Visibility = Visibility.Visible;
            return;
        }
        SpaceSummary.Visibility = Visibility.Collapsed;
        Summary.Text = string.Join("  ·  ", parts);
        Summary.Visibility = Summary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
