using System.Windows;
using Tack.Windows;

namespace Tack.Tests;

/// <summary>
/// Opening and folding the panel (<see cref="Unfold"/>): where DWM is told to draw the panel at each scale, and the curves.
/// What it looks like on screen: tests/Tack.Tests -- --unfold; how smooth: -- --frames.
/// </summary>
internal static class UnfoldTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("unfold: whole, the panel is drawn exactly where it is; smaller, about the orb's centre, which never moves", ScalesAboutTheOrb);
        yield return ("unfold: an orb off the panel (its rectangle nudged onto the screen) is brought onto its edge: the panel never grows from outside itself", OriginKeptOnPanel);
        yield return ("unfold: the curves start and end where they should and never turn back", Curves);
    }

    private static void ScalesAboutTheOrb()
    {
        // The orb at the panel's top-right corner (it sits at the right edge, in the upper half).
        var rect = new Int32Rect(1600, 300, 510, 930);
        var place = new Unfold.Placement(rect, (1600 + 510 - 33, 300 + 33));
        var whole = place.At(1);
        Check.Equal((0, 0, 510, 930), (whole.Left, whole.Top, whole.Right, whole.Bottom), "drawn whole");
        foreach (double scale in new[] { Unfold.Small, 0.9, 0.97 })
        {
            var r = place.At(scale);
            Check.True(Math.Abs(r.Width - 510 * scale) <= 1 && Math.Abs(r.Height - 930 * scale) <= 1, $"at {scale}, {r.Width} x {r.Height}");
            Check.True(r.Left >= 0 && r.Top >= 0 && r.Right <= 510 && r.Bottom <= 930, $"at {scale}, inside the panel");
            // The orb's centre is at the same fraction of the panel at every scale.
            double fx = (place.OriginX - r.Left) / (double)r.Width, fy = (place.OriginY - r.Top) / (double)r.Height;
            Check.True(Math.Abs(fx - place.OriginX / 510.0) < 0.004 && Math.Abs(fy - place.OriginY / 930.0) < 0.004, $"at {scale}, scaled about the orb");
        }
    }

    private static void OriginKeptOnPanel()
    {
        var rect = new Int32Rect(100, 100, 400, 600);
        var place = new Unfold.Placement(rect, (40, 900));
        Check.Equal((0, 600), (place.OriginX, place.OriginY), "the orb's centre, brought onto the panel's corner");
        var r = place.At(Unfold.Small);
        Check.Equal((0, 600), (r.Left, r.Bottom), "the panel grows out of that corner");
    }

    private static void Curves()
    {
        Check.Equal((0.0, 1.0), (Unfold.EaseOut(0), Unfold.EaseOut(1)), "ease out's ends");
        Check.Equal((0.0, 1.0), (Unfold.EaseIn(0), Unfold.EaseIn(1)), "ease in's ends");
        double lastOut = 0, lastIn = 0;
        for (int i = 1; i <= 100; i++)
        {
            double t = i / 100.0;
            Check.True(Unfold.EaseOut(t) >= lastOut && Unfold.EaseIn(t) >= lastIn, $"monotone at {t}");
            lastOut = Unfold.EaseOut(t);
            lastIn = Unfold.EaseIn(t);
        }
        // Opening decelerates (most of the way early: it's there when you look); folding accelerates into the orb.
        Check.True(Unfold.EaseOut(0.5) > 0.85 && Unfold.EaseIn(0.5) < 0.15, "decelerating out, accelerating in");
        Check.True(Unfold.FoldMs < Unfold.OpenMs && Unfold.OpenMs <= 260, "a fold is quicker than an opening; both short");
    }
}
