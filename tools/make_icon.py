"""Renders Tack's logo: a dark glass orb inside a thin ring, a teal progress arc three quarters of
the way round, and a small teal dot at the centre (the tack).

    python tools/make_icon.py

Writes src/Tack/Assets/Tack.ico (16-256 px) and docs/icon.png. Requires Pillow. Tack Focus's
icons (web/tools/make_icons.py) are drawn by the same `logo()`.

The design is drawn, not traced from an image, so it stays sharp at every size. Small sizes get
a bolder version of the same mark (a thick arc and a bigger dot), which still reads at 16 px.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
TEAL = (88, 220, 230)  # the arc: Tack Focus's accent (#58DCE6)
DOT = (44, 200, 180)  # the tack: a deeper, greener teal
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling factor
ARC = 270  # degrees of progress shown, clockwise from 12 o'clock


def _radial(s: int, cx: float, cy: float, r: float, inner: tuple, outer: tuple) -> Image.Image:
    """A disc of radius r filled with a radial gradient (RGBA colours)."""
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    steps = max(24, int(r / 3))
    d = ImageDraw.Draw(img)
    for i in range(steps, 0, -1):
        t = i / steps
        c = tuple(int(inner[k] + (outer[k] - inner[k]) * t) for k in range(4))
        rr = r * t
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=c)
    return img


def _arc(draw: ImageDraw.ImageDraw, cx: float, cy: float, r: float, width: float, colour: tuple, sweep: float = ARC) -> None:
    """A progress arc from 12 o'clock, clockwise, with round ends."""
    box = [cx - r - width / 2, cy - r - width / 2, cx + r + width / 2, cy + r + width / 2]
    draw.arc(box, start=-90, end=-90 + sweep, fill=colour, width=max(1, int(width)))
    for ang in (-90, -90 + sweep):
        a = math.radians(ang)
        px, py = cx + r * math.cos(a), cy + r * math.sin(a)
        draw.ellipse([px - width / 2, py - width / 2, px + width / 2, py + width / 2], fill=colour)


def logo(size: int, glow: bool = True, fill: float = 0.92) -> Image.Image:
    """The mark on a transparent background; `fill` is the share of the square the ring spans."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    c = s / 2
    R = s * fill / 2  # outer edge of the ring
    bold = size < 64  # taskbars, tabs, small lists: the same mark, heavier
    medium = 64 <= size < 128

    if bold:
        ring_r, ring_w = R * 0.80, R * 0.20
        sphere_r, dot_r = R * 0.62, R * 0.20
    elif medium:
        ring_r, ring_w = R * 0.88, R * 0.09
        sphere_r, dot_r = R * 0.76, R * 0.11
    else:
        ring_r, ring_w = R * 0.925, R * 0.032
        sphere_r, dot_r = R * 0.83, R * 0.07

    # Soft teal light around the arc (only where there's room for it).
    if glow and not bold:
        halo = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        _arc(ImageDraw.Draw(halo), c, c, ring_r, ring_w * (3.2 if medium else 4.5), TEAL + (150,))
        img.alpha_composite(halo.filter(ImageFilter.GaussianBlur(R * 0.06)))

    # The bezel: a dark glass disc with a faint lighter edge.
    bezel = _radial(s, c, c - R * 0.15, R * 1.1, (44, 48, 56, 255), (18, 20, 25, 255))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse([c - R, c - R, c + R, c + R], fill=255)
    disc = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    disc.paste(bezel, (0, 0), mask)
    img.alpha_composite(disc)

    ov = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(ov)
    edge = max(SS, R * 0.016)
    draw.ellipse([c - R, c - R, c + R, c + R], outline=(150, 160, 172, 90), width=int(edge))

    # The track and the progress arc.
    track = [c - ring_r - ring_w / 2, c - ring_r - ring_w / 2, c + ring_r + ring_w / 2, c + ring_r + ring_w / 2]
    draw.ellipse(track, outline=(110, 150, 160, 70), width=max(1, int(ring_w)))
    _arc(draw, c, c, ring_r, ring_w, TEAL + (255,))
    img.alpha_composite(ov)

    # The glass orb: near-black, a lighter rim, and a soft highlight across its top.
    if not bold:
        sphere = _radial(s, c, c + sphere_r * 0.25, sphere_r * 1.25, (15, 16, 20, 255), (44, 47, 55, 255))
        smask = Image.new("L", (s, s), 0)
        ImageDraw.Draw(smask).ellipse([c - sphere_r, c - sphere_r, c + sphere_r, c + sphere_r], fill=255)
        orb = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        orb.paste(sphere, (0, 0), smask)

        cap = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        cw, ch = sphere_r * 0.62, sphere_r * 0.40
        cy = c - sphere_r * 0.47
        cd = ImageDraw.Draw(cap)
        steps = 40
        for i in range(steps):
            t = i / steps  # top to bottom of the highlight: brighter at the top, fading out
            alpha = int(80 * (1 - t) ** 1.6)
            y0 = cy - ch + 2 * ch * t
            y1 = cy - ch + 2 * ch * (t + 1 / steps)
            cd.rectangle([c - cw, y0, c + cw, y1], fill=(210, 220, 235, alpha))
        cap_mask = Image.new("L", (s, s), 0)
        ImageDraw.Draw(cap_mask).ellipse([c - cw, cy - ch, c + cw, cy + ch], fill=255)
        highlight = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        highlight.paste(cap, (0, 0), cap_mask)
        orb.alpha_composite(highlight.filter(ImageFilter.GaussianBlur(sphere_r * 0.02)))

        rim = ImageDraw.Draw(orb)
        rim.ellipse([c - sphere_r, c - sphere_r, c + sphere_r, c + sphere_r], outline=(120, 128, 140, 110), width=max(SS, int(sphere_r * 0.018)))
        img.alpha_composite(orb)

    # The tack, with a little light of its own.
    if not bold:
        shine = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(shine).ellipse([c - dot_r * 2, c - dot_r * 2, c + dot_r * 2, c + dot_r * 2], fill=DOT + (70,))
        img.alpha_composite(shine.filter(ImageFilter.GaussianBlur(dot_r * 0.8)))
    ImageDraw.Draw(img).ellipse([c - dot_r, c - dot_r, c + dot_r, c + dot_r], fill=(DOT if not bold else TEAL) + (255,))

    return img.resize((size, size), Image.LANCZOS)


def tile(size: int, scale: float = 0.62, background: tuple = (13, 15, 20)) -> Image.Image:
    """The logo on an opaque square (home screens, store tiles), with a little light behind it."""
    s = size * SS
    img = Image.new("RGBA", (s, s), background + (255,))
    light = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    r = s * scale * 0.62
    ImageDraw.Draw(light).ellipse([s / 2 - r, s / 2 - r, s / 2 + r, s / 2 + r], fill=(40, 120, 135, 70))
    img.alpha_composite(light.filter(ImageFilter.GaussianBlur(s * 0.12)))
    mark = logo(max(128, int(size * scale)))
    mark = mark.resize((int(s * scale), int(s * scale)), Image.LANCZOS)
    img.alpha_composite(mark, ((s - mark.width) // 2, (s - mark.height) // 2))
    return img.resize((size, size), Image.LANCZOS).convert("RGB")


def main() -> None:
    images = [logo(sz) for sz in SIZES]
    ico = ROOT / "src" / "Tack" / "Assets" / "Tack.ico"
    ico.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(ico, format="ICO", sizes=[(sz, sz) for sz in SIZES], append_images=images[:-1])
    png = ROOT / "docs" / "icon.png"
    png.parent.mkdir(parents=True, exist_ok=True)
    logo(512).save(png)
    print(f"wrote {ico} and {png}")


if __name__ == "__main__":
    main()
