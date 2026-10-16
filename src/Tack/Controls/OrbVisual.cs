using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tack.Services;

namespace Tack.Controls;

/// <summary>
/// The collapsed orb: a small glass bead (dark or light, following the theme) whose ring fills as today's tasks get done,
/// with the number of open tasks at its centre. Drawn directly (no templates) so it stays cheap.
/// </summary>
internal sealed class OrbVisual : FrameworkElement
{
    public const double Diameter = 40;

    public static readonly DependencyProperty ProgressProperty = Register(nameof(Progress), 0.0);
    public static readonly DependencyProperty CountProperty = Register(nameof(Count), 0);
    public static readonly DependencyProperty AllDoneProperty = Register(nameof(AllDone), false);
    public static readonly DependencyProperty IsHotProperty = Register(nameof(IsHot), false);

    /// <summary>The bead's materials for one theme.</summary>
    private sealed record Look(Brush Shadow, Brush Body, Brush Sheen, Pen Rim, Pen RimHot, Pen Track, Brush Count);

    // Dark glass with a bright rim.
    private static readonly Look DarkLook = new(
        Shadow(0x60, 0x30),
        Body(Color.FromArgb(0xF4, 0x34, 0x35, 0x3C), Color.FromArgb(0xF4, 0x1A, 0x1B, 0x20)),
        Sheen(0x22),
        Rim(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
        Rim(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
        Track(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        Solid(Color.FromArgb(0xEE, 0xF3, 0xF4, 0xF6)));

    // Pale porcelain with a soft grey shadow and a fine dark rim, so it reads on bright documents.
    private static readonly Look LightLook = new(
        Shadow(0x3C, 0x1A),
        Body(Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xF8, 0xEA, 0xEB, 0xEF)),
        Sheen(0x80),
        Rim(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), Color.FromArgb(0x38, 0x00, 0x00, 0x00)),
        Rim(Color.FromArgb(0x30, 0x00, 0x00, 0x00), Color.FromArgb(0x58, 0x00, 0x00, 0x00)),
        Track(Color.FromArgb(0x17, 0x00, 0x00, 0x00)),
        Solid(Color.FromArgb(0xE6, 0x1B, 0x1B, 0x1F)));

    private static readonly Typeface CountFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public bool AllDone { get => (bool)GetValue(AllDoneProperty); set => SetValue(AllDoneProperty, value); }
    public bool IsHot { get => (bool)GetValue(IsHotProperty); set => SetValue(IsHotProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        const double r = Diameter / 2;
        var accent = (TryFindResource("AccentBrush") as Brush) ?? Brushes.DeepSkyBlue;
        var look = ThemeManager.IsLight ? LightLook : DarkLook;

        // Soft contact shadow, slightly below the bead.
        dc.DrawEllipse(look.Shadow, null, new Point(c.X, c.Y + 2.5), r + 7, r + 7);

        // Body, sheen and rim.
        dc.DrawEllipse(look.Body, null, c, r, r);
        dc.DrawEllipse(look.Sheen, null, c, r - 0.5, r - 0.5);
        dc.DrawEllipse(null, IsHot ? look.RimHot : look.Rim, c, r - 0.5, r - 0.5);

        // Progress ring: today's finished share of work.
        const double ringR = r - 5.5;
        dc.DrawEllipse(null, look.Track, c, ringR, ringR);
        double p = Math.Clamp(Progress, 0, 1);
        var ringPen = new Pen(accent, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.999 || AllDone) dc.DrawEllipse(null, ringPen, c, ringR, ringR);
        else if (p > 0.001) dc.DrawGeometry(null, ringPen, Arc(c, ringR, p));
        else if (Count > 0) dc.DrawEllipse(accent, null, new Point(c.X, c.Y - ringR), 1.5, 1.5); // where progress will start

        // Centre: open count, a tick when everything is done, or a quiet dot when empty.
        int count = Count;
        if (count > 0)
        {
            string text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
            double size = text.Length >= 3 ? 11 : 14;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CountFace, size,
                look.Count, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2 - 0.5));
        }
        else if (AllDone)
        {
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(c.X - 5, c.Y + 0.5), false, false);
                g.LineTo(new Point(c.X - 1.5, c.Y + 4), true, true);
                g.LineTo(new Point(c.X + 5.5, c.Y - 3.5), true, true);
            }
            tick.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
        }
        else
        {
            dc.DrawEllipse(accent, null, c, 3, 3);
        }
    }

    private static Geometry Arc(Point c, double radius, double fraction)
    {
        double angle = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - radius);
        var end = new Point(c.X + radius * Math.Sin(angle), c.Y - radius * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(OrbVisual),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush Shadow(byte inner, byte middle) => Freeze(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(inner, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(middle, 0, 0, 0), 0.62),
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0),
        },
    });

    private static Brush Body(Color top, Color bottom) => Freeze(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.28),
        GradientOrigin = new Point(0.5, 0.12),
        RadiusX = 0.85,
        RadiusY = 0.85,
        GradientStops = { new GradientStop(top, 0.0), new GradientStop(bottom, 1.0) },
    });

    private static Brush Sheen(byte alpha) => Freeze(new LinearGradientBrush(
        Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), new Point(0.5, 0), new Point(0.5, 0.55)));

    private static Pen Rim(Color top, Color bottom) => Freeze(new Pen(Freeze(new LinearGradientBrush(top, bottom, 90)), 1));

    private static Pen Track(Color color) => Freeze(new Pen(new SolidColorBrush(color), 2.2));

    private static Brush Solid(Color color) => Freeze(new SolidColorBrush(color));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
