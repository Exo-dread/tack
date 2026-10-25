using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Tack.Interop;
using Tack.Services;

namespace Tack.Snippets;

/// <summary>
/// A snippet at full size, in a plain window on the panel's display: one pixel of the snip to one pixel of
/// the screen, or scaled down to fit nine tenths of the display. Esc, a click on it, or clicking anywhere
/// else closes it. It's a window of its own (above the panel, never in the taskbar or Alt+Tab) so it can be
/// bigger than the panel, and it exists only while it's open.
/// </summary>
internal sealed class SnippetViewer : Window
{
    /// <summary>Room around the image, in DIPs.</summary>
    public const double Pad = 8;

    /// <summary>The share of the display's work area the viewer may take.</summary>
    public const double MaxShare = 0.9;

    private readonly long _openedAt = Environment.TickCount64;
    private bool _pressed;
    private bool _closing;

    private SnippetViewer(BitmapSource image, double scale, string name)
    {
        Title = "Snippet";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(FontFamilyProperty, "UiFont");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        // The image at its own pixels: its size in DIPs on this display, so nothing is resampled.
        Image = new Image
        {
            Source = image,
            Width = image.PixelWidth / scale,
            Height = image.PixelHeight / scale,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(Image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(Image, name);
        AutomationProperties.SetHelpText(Image, "Press Esc or click to close");
        var surface = new Border { Child = Image, Padding = new Thickness(Pad) };
        surface.SetResourceReference(Border.BackgroundProperty, "SolidSurfaceBrush");
        Content = surface;
        AutomationProperties.SetName(this, name);
    }

    /// <summary>The image on show.</summary>
    public Image Image { get; }

    /// <summary>Closed because something else was clicked (so an unpinned panel may fold away too).</summary>
    public bool ClosedByClickAway { get; private set; }

    /// <summary>
    /// Opens a snippet over the display <paramref name="near"/> is on, or returns null when its file can't be
    /// read (missing, damaged or too big).
    /// </summary>
    public static SnippetViewer? Open(Window owner, IntPtr near, string path, Snippet snippet)
    {
        var display = ScreenGeometry.FromWindow(near);
        var (rect, box) = Layout(display.Work, display.Scale);
        if (SnippetImage.Load(path, box) is not { } image) return null;

        // As big as the image (and a little room), centred on the work area; never smaller than a small card.
        int pad = display.Px(Pad);
        int width = Math.Max(image.PixelWidth + 2 * pad, display.Px(160));
        int height = Math.Max(image.PixelHeight + 2 * pad, display.Px(100));
        var placed = new Int32Rect(rect.X + (rect.Width - width) / 2, rect.Y + (rect.Height - height) / 2, width, height);

        var viewer = new SnippetViewer(image, display.Scale, $"Snippet pasted {snippet.PastedAt}") { Owner = owner };
        var hwnd = new WindowInteropHelper(viewer).EnsureHandle();
        ScreenGeometry.Place(hwnd, placed.X, placed.Y, placed.Width, placed.Height);
        viewer.Show();
        ScreenGeometry.PlaceExact(viewer, hwnd, placed);
        viewer.Activate();
        return viewer;
    }

    /// <summary>A viewer for <paramref name="image"/> on a display at <paramref name="scale"/>, not yet shown (tests show it off screen).</summary>
    internal static SnippetViewer Create(BitmapSource image, double scale, string name) => new(image, scale, name);

    /// <summary>The largest the viewer may be on a work area (centred), and the box the image is fitted into, in pixels.</summary>
    internal static (Int32Rect Bounds, (int Width, int Height) Box) Layout(Int32Rect work, double scale)
    {
        int width = (int)(work.Width * MaxShare), height = (int)(work.Height * MaxShare);
        var bounds = new Int32Rect(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
        int pad = (int)Math.Round(Pad * scale);
        return (bounds, (Math.Max(1, width - 2 * pad), Math.Max(1, height - 2 * pad)));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        WindowEffects.MakeToolWindow(hwnd);
        WindowEffects.StripWindowMenu(hwnd);
        WindowEffects.ExcludeFromPeek(hwnd);
        WindowEffects.ApplyPanelChrome(hwnd, glass: false, dark: !ThemeManager.IsLight);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            e.Handled = true;
            CloseSoon();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // The click that opened it doesn't close it, only one that starts here; nor does the rest of a double-click.
        if (Environment.TickCount64 - _openedAt > 500) _pressed = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_pressed) CloseSoon();
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_closing) return;
        ClosedByClickAway = true;
        CloseSoon();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Image.Source = null; // the full-size pixels go with the window
        base.OnClosed(e);
    }

    /// <summary>Closes once the event that asked for it is done (a window mustn't close inside its own activation change).</summary>
    public void CloseSoon()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

}
