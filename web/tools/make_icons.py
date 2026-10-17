"""Renders Tack Focus's icons: the orb from Tack for Windows (a dark glass bead, a progress arc and
a centre dot) in Tack Focus's teal, plus a full-bleed version for home screens.

    python tools/make_icons.py        (from web/)

Writes public/icons/*. Requires Pillow. The SVG favicon is written by hand alongside.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

OUT = Path(__file__).resolve().parent.parent / "public" / "icons"
ACCENT = (88, 220, 230)  # --accent (teal, dark theme)
SS = 4  # supersampling


def orb(size: int, shadow: bool = True, arc: float = 250) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    small = size <= 32
    pad = s * (0.04 if small else 0.08)
    box = [pad, pad, s - pad, s - pad]
    d = s - 2 * pad

    if shadow and not small:
        sh = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        off = d * 0.03
        ImageDraw.Draw(sh).ellipse([box[0], box[1] + off, box[2], box[3] + off], fill=(0, 0, 0, 120))
        img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(d * 0.04)))

    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    top, bottom = (60, 62, 71), (21, 22, 27)
    for y in range(s):
        t = min(max((y - box[1]) / d, 0), 1)
        gd.line([(0, y), (s, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse(box, fill=255)
    body = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    body.paste(grad, (0, 0), mask)
    img.alpha_composite(body)

    ov = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(ov)
    draw.ellipse(box, outline=(255, 255, 255, 70), width=max(SS, int(d * 0.025)))

    inset = d * (0.17 if small else 0.16)
    ring = [box[0] + inset, box[1] + inset, box[2] - inset, box[3] - inset]
    w = int(d * (0.11 if small else 0.075))
    draw.ellipse(ring, outline=(255, 255, 255, 34), width=w)
    draw.arc(ring, start=-90, end=-90 + arc, fill=ACCENT + (255,), width=w)
    cx = cy = s / 2
    r = (ring[2] - ring[0]) / 2 - w / 2
    for ang in (-90, -90 + arc):
        a = math.radians(ang)
        px, py = cx + r * math.cos(a), cy + r * math.sin(a)
        draw.ellipse([px - w / 2, py - w / 2, px + w / 2, py + w / 2], fill=ACCENT + (255,))
    dot = d * (0.16 if small else 0.12)
    draw.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=ACCENT + (255,))
    img.alpha_composite(ov)
    return img.resize((size, size), Image.LANCZOS)


def tile(size: int, scale: float = 0.62) -> Image.Image:
    """Opaque square for home screens: deep glass background, a soft teal glow, the orb."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (13, 15, 20, 255))
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-s * 0.3, -s * 0.45, s * 0.9, s * 0.55], fill=(36, 118, 140, 150))
    gd.ellipse([s * 0.45, s * 0.35, s * 1.35, s * 1.25], fill=(122, 46, 104, 110))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(s * 0.18)))
    o = orb(int(size * scale), shadow=True).resize((int(s * scale), int(s * scale)), Image.LANCZOS)
    img.alpha_composite(o, ((s - o.width) // 2, (s - o.height) // 2))
    return img.resize((size, size), Image.LANCZOS).convert("RGB")


def glyph_icon(size: int, kind: str) -> Image.Image:
    """Shortcut icons: a dark disc with a teal plus (new task) or note."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([0, 0, s - 1, s - 1], fill=(26, 28, 35, 255))
    w = int(s * 0.07)
    c = s / 2
    if kind == "task":
        L = s * 0.22
        d.line([(c - L, c), (c + L, c)], fill=ACCENT + (255,), width=w)
        d.line([(c, c - L), (c, c + L)], fill=ACCENT + (255,), width=w)
        for x, y in ((c - L, c), (c + L, c), (c, c - L), (c, c + L)):
            d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=ACCENT + (255,))
    else:
        x0, y0, x1, y1 = s * 0.32, s * 0.26, s * 0.68, s * 0.74
        d.rounded_rectangle([x0, y0, x1, y1], radius=s * 0.05, outline=ACCENT + (255,), width=w)
        for i, frac in enumerate((0.42, 0.53, 0.64)):
            y = s * frac
            d.line([(x0 + s * 0.08, y), (x1 - s * (0.08 if i < 2 else 0.16), y)], fill=ACCENT + (255,), width=int(w * 0.8))
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    orb(192).save(OUT / "icon-192.png")
    orb(512).save(OUT / "icon-512.png")
    orb(32, shadow=False).save(OUT / "favicon-32.png")
    tile(512, scale=0.56).save(OUT / "maskable-512.png")  # maskable safe zone is the inner 80%
    tile(180, scale=0.66).save(OUT / "apple-touch-icon.png")
    glyph_icon(96, "task").save(OUT / "shortcut-task-96.png")
    glyph_icon(96, "notes").save(OUT / "shortcut-notes-96.png")
    print(f"wrote icons to {OUT}")


if __name__ == "__main__":
    main()
