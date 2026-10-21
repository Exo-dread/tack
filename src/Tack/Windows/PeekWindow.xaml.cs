using System.Windows;
using System.Windows.Interop;
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
        Anim.Fade(this, 0, 90, () => { if (!_wanted) Hide(); });
    }

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

        Anim.Fade(this, 1, 140);
        Anim.Slide(Bubble, toLeft ? 8 : -8, 0, 180);
    }

    /// <summary>What's on show: a space's (or everything's) focus task and counts, and, once there are spaces, which one.</summary>
    private void Fill(SpaceView view, string hotkey)
    {
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
        if (view.HasSpaces) parts.Insert(0, view.ShownName); // "Work  ·  3 open  ·  2 done today"
        Summary.Text = string.Join("  ·  ", parts);
        Summary.Visibility = Summary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
