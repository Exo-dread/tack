using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tack.Controls;

/// <summary>
/// Small, consistent animation vocabulary. Every motion in Tack is short (≤ 240 ms),
/// eased, and runs on WPF's render thread; nothing animates while the app is idle.
/// </summary>
internal static class Anim
{
    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    public static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    static Anim()
    {
        ((Freezable)EaseOut).Freeze();
        ((Freezable)EaseIn).Freeze();
        ((Freezable)Spring).Freeze();
    }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    public static void To(IAnimatable target, DependencyProperty property, double to, double ms,
        IEasingFunction? ease = null, Action? completed = null, double? from = null)
    {
        var a = new DoubleAnimation { To = to, Duration = Ms(ms), EasingFunction = ease ?? EaseOut };
        if (from is { } f) a.From = f;
        if (completed != null) a.Completed += (_, _) => completed();
        target.BeginAnimation(property, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops any animation on the property and pins it to a value.</summary>
    public static void Set(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    public static void Fade(UIElement element, double to, double ms, Action? completed = null, double? from = null) =>
        To(element, UIElement.OpacityProperty, to, ms, EaseOut, completed, from);

    /// <summary>Ensures the element has a Scale+Translate transform group and returns its parts.</summary>
    public static (ScaleTransform Scale, TranslateTransform Translate) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup g && g.Children.Count == 2 &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);

        s = new ScaleTransform(1, 1);
        t = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }

    public static void Scale(UIElement element, double to, double ms, IEasingFunction? ease = null, double? from = null, Action? completed = null)
    {
        var (s, _) = Transforms(element);
        To(s, ScaleTransform.ScaleXProperty, to, ms, ease, completed, from);
        To(s, ScaleTransform.ScaleYProperty, to, ms, ease, null, from);
    }

    public static void Slide(UIElement element, double fromX, double fromY, double ms, IEasingFunction? ease = null)
    {
        var (_, t) = Transforms(element);
        if (fromX != 0) To(t, TranslateTransform.XProperty, 0, ms, ease, null, fromX);
        if (fromY != 0) To(t, TranslateTransform.YProperty, 0, ms, ease, null, fromY);
    }

    /// <summary>Animates an element's height to zero (and fades it), then runs the callback.</summary>
    public static void Collapse(FrameworkElement element, double ms, Action completed)
    {
        double h = element.ActualHeight;
        element.IsHitTestVisible = false;
        Fade(element, 0, ms * 0.7);
        var a = new DoubleAnimation(h, 0, Ms(ms)) { EasingFunction = EaseIn };
        a.Completed += (_, _) =>
        {
            // Run the callback (which usually removes the element) before restoring it,
            // all within one frame, so nothing flickers back into view.
            completed();
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.ClearValue(FrameworkElement.HeightProperty);
            element.IsHitTestVisible = true;
        };
        element.BeginAnimation(FrameworkElement.HeightProperty, a);
    }
}
