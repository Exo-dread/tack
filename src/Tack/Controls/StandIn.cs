using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tack.Controls;

/// <summary>
/// A picture that stands in for an element with a drop shadow while it slides or fades (the peek's bubble, the
/// undo toast). Tack draws in software, and software blurs a shadow anew for every frame it changes: about
/// half of each frame's work, enough to drop a 100 Hz display to 75 frames a second. The picture is taken once,
/// at the window's own DPI, so at rest it is the very same pixels; the live element takes over as soon as it
/// holds still, so text stays crisp and editable and nothing else changes.
/// </summary>
internal sealed class StandIn(FrameworkElement live, FrameworkElement area, Image picture)
{
    /// <summary>The picture, to animate in the live element's place.</summary>
    public Image Picture => picture;

    /// <summary>True while the picture stands in.</summary>
    public bool IsStanding => picture.Visibility == Visibility.Visible;

    /// <summary>Takes a fresh picture of the area (the live element with its shadow), as it looks now.</summary>
    public void Take()
    {
        Live(keep: false);
        area.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(area);
        int w = (int)Math.Round(area.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(area.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(area);
        bitmap.Freeze();
        picture.Source = bitmap;
    }

    /// <summary>
    /// The picture in, the live element out of sight: still there for the pointer (the toast's Undo can be
    /// clicked as it slides in), but drawn not at all, shadow included. Takes a picture if there's none.
    /// </summary>
    public void Stand()
    {
        if (picture.Source == null) Take();
        if (picture.Source == null) return;
        picture.Visibility = Visibility.Visible;
        Anim.Set(live, UIElement.OpacityProperty, 0);
    }

    /// <summary>The live element back, the picture hidden and at rest; let go of unless <paramref name="keep"/> (wanted again soon).</summary>
    public void Live(bool keep)
    {
        Anim.Set(live, UIElement.OpacityProperty, 1);
        picture.Visibility = Visibility.Collapsed;
        var (_, slide) = Anim.Transforms(picture);
        Anim.Set(slide, TranslateTransform.XProperty, 0);
        Anim.Set(slide, TranslateTransform.YProperty, 0);
        Anim.Set(picture, UIElement.OpacityProperty, 1);
        if (!keep) picture.Source = null;
    }
}
