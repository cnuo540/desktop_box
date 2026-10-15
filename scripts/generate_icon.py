"""Generate DesktopBox application icon (.ico).

Design: an isometric open box (the "DesktopBox") with a file being dropped in,
on a modern blue gradient rounded-square background. Rendered at 4x and
downsampled for smooth anti-aliased edges, then exported as a multi-size ICO.
"""
import os
from PIL import Image, ImageDraw

SS = 4  # supersampling factor


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def draw_base(S):
    """Blue gradient rounded-square app background."""
    top = (0x3B, 0x82, 0xF6)   # blue-500
    bottom = (0x1D, 0x4E, 0xD8)  # blue-700
    grad = Image.new("RGB", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(top, bottom, y / (S - 1)))

    mask = rounded_mask(S, int(S * 0.225))
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    base.paste(grad, (0, 0), mask)

    # subtle top-left highlight for depth
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-S * 0.35, -S * 0.55, S * 1.35, S * 0.55], fill=(255, 255, 255, 46))
    hl.putalpha(Image.composite(hl.split()[3], Image.new("L", (S, S), 0), mask))
    base = Image.alpha_composite(base, hl)
    return base


def draw_box(S):
    """Isometric open box: interior, two front faces, plus a file dropping in."""
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    cx = S * 0.50
    cy = S * 0.545          # center of the box top opening
    hw = S * 0.255          # half width of the diamond top
    rh = S * 0.128          # half height of the diamond top
    bh = S * 0.235          # body height

    N = (cx, cy - rh)
    E = (cx + hw, cy)
    Spt = (cx, cy + rh)
    W = (cx - hw, cy)
    Eb = (E[0], E[1] + bh)
    Sb = (Spt[0], Spt[1] + bh)
    Wb = (W[0], W[1] + bh)

    interior = (0xDB, 0xEA, 0xFE, 255)   # light blue-100 (inside of box)
    left = (0xFF, 0xFF, 0xFF, 255)       # white front-left face
    right = (0xD7, 0xE3, 0xF7, 255)      # shaded front-right face

    d.polygon([W, Spt, Sb, Wb], fill=left)
    d.polygon([Spt, E, Eb, Sb], fill=right)
    d.polygon([N, E, Spt, W], fill=interior)

    # soft inner shadow along the back of the opening
    d.polygon([N, E, Spt, W], outline=(0x93, 0xB4, 0xE8, 255), width=max(1, int(S * 0.008)))

    # ---- file hovering above the box, about to be dropped in ----
    fw = S * 0.115
    fh = S * 0.145
    fx = cx - fw / 2
    fy = cy - rh - S * 0.20
    page = (255, 255, 255, 255)
    accent = (0x25, 0x63, 0xEB, 255)
    d.rounded_rectangle([fx, fy, fx + fw, fy + fh], radius=S * 0.018, fill=page)
    # folded corner
    fold = S * 0.045
    d.polygon(
        [(fx + fw - fold, fy), (fx + fw, fy), (fx + fw, fy + fold)],
        fill=(0xBF, 0xDB, 0xFE, 255),
    )
    # two content lines
    for i, yfrac in enumerate((0.38, 0.58)):
        y = fy + fh * yfrac
        d.line(
            [(fx + fw * 0.22, y), (fx + fw * (0.78 if i == 0 else 0.62), y)],
            fill=accent,
            width=max(1, int(S * 0.014)),
        )

    return layer


def build(size):
    S = size * SS
    img = draw_base(S)
    img = Image.alpha_composite(img, draw_box(S))
    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    out_dir = os.path.join(os.path.dirname(__file__), "..", "src", "DesktopBox", "Resources")
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "DesktopBox.ico")

    images = [build(s) for s in sizes]
    images[-1].save(out_path, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    print(f"saved {out_path}")


if __name__ == "__main__":
    main()
