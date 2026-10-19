"""Renders Tack Focus's icons: Tack's logo (drawn by tools/make_icon.py, shared with Tack for
Windows), plus a full-bleed version for home screens and the shortcut glyphs.

    python tools/make_icons.py        (from web/)

Writes public/icons/*. Requires Pillow. The SVG favicon is written by hand alongside.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / "tools"))
from make_icon import TEAL, logo, tile  # noqa: E402  (the one logo, for both apps)

OUT = HERE.parent / "public" / "icons"
SS = 4  # supersampling


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
        d.line([(c - L, c), (c + L, c)], fill=TEAL + (255,), width=w)
        d.line([(c, c - L), (c, c + L)], fill=TEAL + (255,), width=w)
        for x, y in ((c - L, c), (c + L, c), (c, c - L), (c, c + L)):
            d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=TEAL + (255,))
    else:
        x0, y0, x1, y1 = s * 0.32, s * 0.26, s * 0.68, s * 0.74
        d.rounded_rectangle([x0, y0, x1, y1], radius=s * 0.05, outline=TEAL + (255,), width=w)
        for i, frac in enumerate((0.42, 0.53, 0.64)):
            y = s * frac
            d.line([(x0 + s * 0.08, y), (x1 - s * (0.08 if i < 2 else 0.16), y)], fill=TEAL + (255,), width=int(w * 0.8))
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    logo(192).save(OUT / "icon-192.png")
    logo(512).save(OUT / "icon-512.png")
    logo(32).save(OUT / "favicon-32.png")
    tile(512, scale=0.8).save(OUT / "maskable-512.png")  # the ring stays inside the safe zone (the inner 80%)
    tile(180, scale=0.9).save(OUT / "apple-touch-icon.png")  # iOS rounds the corners; nothing is cropped
    glyph_icon(96, "task").save(OUT / "shortcut-task-96.png")
    glyph_icon(96, "notes").save(OUT / "shortcut-notes-96.png")
    print(f"wrote icons to {OUT}")


if __name__ == "__main__":
    main()
