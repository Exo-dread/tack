"""Renders Tack's icon (the orb: a dark glass bead with a progress arc and a centre dot).

    python tools/make_icon.py

Writes src/Tack/Assets/Tack.ico (16-256 px) and docs/icon.png. Requires Pillow.
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ACCENT = (96, 205, 255)  # Windows 11 dark-mode accent blue
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling factor


def render(size: int) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    small = size <= 24
    pad = s * (0.04 if small else 0.08)
    box = [pad, pad, s - pad, s - pad]
    d = s - 2 * pad

    # Soft shadow (skipped at tiny sizes where it only muddies the edge).
    if not small:
        shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        sd = ImageDraw.Draw(shadow)
        off = d * 0.03
        sd.ellipse([box[0], box[1] + off, box[2], box[3] + off], fill=(0, 0, 0, 120))
        shadow = shadow.filter(ImageFilter.GaussianBlur(d * 0.04))
        img.alpha_composite(shadow)

    # Body: vertical gradient from a lifted charcoal to near-black.
    body = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    top, bottom = (58, 60, 68), (22, 23, 28)
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = min(max((y - box[1]) / d, 0), 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        gd.line([(0, y), (s, y)], fill=c + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse(box, fill=255)
    body.paste(grad, (0, 0), mask)
    img.alpha_composite(body)

    overlay = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)

    # Rim highlight.
    rim_w = max(SS, int(d * 0.025))
    draw.ellipse(box, outline=(255, 255, 255, 70), width=rim_w)

    # Track + progress arc (about two thirds done).
    ring_inset = d * (0.17 if small else 0.16)
    ring_box = [box[0] + ring_inset, box[1] + ring_inset, box[2] - ring_inset, box[3] - ring_inset]
    ring_w = int(d * (0.11 if small else 0.075))
    draw.ellipse(ring_box, outline=(255, 255, 255, 34), width=ring_w)
    draw.arc(ring_box, start=-90, end=-90 + 250, fill=ACCENT + (255,), width=ring_w)
    # Round caps for the arc.
    cx, cy = s / 2, s / 2
    r = (ring_box[2] - ring_box[0]) / 2 - ring_w / 2
    for ang in (-90, -90 + 250):
        a = math.radians(ang)
        px, py = cx + r * math.cos(a), cy + r * math.sin(a)
        draw.ellipse([px - ring_w / 2, py - ring_w / 2, px + ring_w / 2, py + ring_w / 2], fill=ACCENT + (255,))

    # Centre dot: the "tack".
    dot = d * (0.16 if small else 0.12)
    draw.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=ACCENT + (255,))

    img.alpha_composite(overlay)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    images = [render(sz) for sz in SIZES]
    ico = ROOT / "src" / "Tack" / "Assets" / "Tack.ico"
    ico.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(ico, format="ICO", sizes=[(sz, sz) for sz in SIZES], append_images=images[:-1])
    png = ROOT / "docs" / "icon.png"
    png.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(png)
    print(f"wrote {ico} and {png}")


if __name__ == "__main__":
    main()
