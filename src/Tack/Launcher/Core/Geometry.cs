namespace Tack.Launcher.Core;

// The launcher's geometry works in physical screen pixels (virtual-desktop coordinates, which
// can be negative), like ScreenGeometry. These types have no WPF dependency so the layout,
// hit testing and state machine can be tested on any machine.

internal readonly record struct PxPoint(double X, double Y)
{
    public double DistanceTo(PxPoint other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

    /// <summary>Angle from this point to another, in degrees: 0 = right, 90 = down (screen coordinates), range (-180, 180].</summary>
    public double AngleTo(PxPoint other) => Math.Atan2(other.Y - Y, other.X - X) * 180 / Math.PI;

    public PxPoint Offset(double angleDeg, double distance)
    {
        double a = angleDeg * Math.PI / 180;
        return new PxPoint(X + distance * Math.Cos(a), Y + distance * Math.Sin(a));
    }
}

internal readonly record struct PxRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PxPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PxRect Around(PxPoint c, double halfWidth, double halfHeight) =>
        new(c.X - halfWidth, c.Y - halfHeight, halfWidth * 2, halfHeight * 2);

    public bool Contains(PxPoint p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    /// <summary>True when the whole rectangle lies inside this one.</summary>
    public bool Contains(PxRect r) => r.X >= X - 0.01 && r.Y >= Y - 0.01 && r.Right <= Right + 0.01 && r.Bottom <= Bottom + 0.01;

    public PxRect Deflate(double by) => new(X + by, Y + by, Math.Max(0, Width - 2 * by), Math.Max(0, Height - 2 * by));

    public PxRect Union(PxRect r)
    {
        if (IsEmpty) return r;
        if (r.IsEmpty) return this;
        double x = Math.Min(X, r.X), y = Math.Min(Y, r.Y);
        return new PxRect(x, y, Math.Max(Right, r.Right) - x, Math.Max(Bottom, r.Bottom) - y);
    }

    public PxRect Intersect(PxRect r)
    {
        double x = Math.Max(X, r.X), y = Math.Max(Y, r.Y);
        double right = Math.Min(Right, r.Right), bottom = Math.Min(Bottom, r.Bottom);
        return right <= x || bottom <= y ? default : new PxRect(x, y, right - x, bottom - y);
    }

    public double OverlapArea(PxRect r)
    {
        var i = Intersect(r);
        return i.IsEmpty ? 0 : i.Width * i.Height;
    }

    /// <summary>Moves (never resizes, unless it can't fit) the rectangle so it lies inside <paramref name="area"/>.</summary>
    public PxRect ClampInside(PxRect area)
    {
        double w = Math.Min(Width, area.Width), h = Math.Min(Height, area.Height);
        double x = Math.Clamp(X, area.X, area.Right - w);
        double y = Math.Clamp(Y, area.Y, area.Bottom - h);
        return new PxRect(x, y, w, h);
    }

    /// <summary>Moves a point inside the rectangle.</summary>
    public PxPoint Clamp(PxPoint p) => new(Math.Clamp(p.X, X, Math.Max(X, Right)), Math.Clamp(p.Y, Y, Math.Max(Y, Bottom)));
}

/// <summary>A connected display, as Windows reports it (physical pixels).</summary>
/// <param name="Device">Windows' device name, e.g. \\.\DISPLAY1. Not guaranteed stable across reconnects.</param>
internal sealed record Display(string Device, PxRect Bounds, PxRect Work, double Scale, bool Primary);
