"""Draws the OpenOutlook application icon and writes it as a Windows .ico plus PNGs.

    python scripts/generate_icon.py

The icon is drawn at 1024 px and scaled down, so every size is sharp. Output goes to src/OpenOutlook.Desktop/Assets.
Design: a blue rounded square, a white envelope, and an open amber ring (the "O" of Open, left unclosed).
"""
import math
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "OpenOutlook.Desktop", "Assets")
S = 1024


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(size, radius, inset=0):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, size - 1 - inset, size - 1 - inset], radius=radius, fill=255)
    return mask


def draw():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # background: vertical blue gradient inside a rounded square
    top, bottom = (36, 150, 237), (10, 72, 160)
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(top, bottom, y / (S - 1)) + (255,))
    img.paste(grad, (0, 0), rounded_mask(S, 230, 24))

    # soft highlight along the top edge
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    for y in range(S // 2):
        hd.line([(0, y), (S, y)], fill=(255, 255, 255, int(46 * (1 - y / (S / 2)))))     # fades out smoothly: no hard edge
    img.alpha_composite(Image.composite(hl, Image.new("RGBA", (S, S), (0, 0, 0, 0)), rounded_mask(S, 230, 24)))

    d = ImageDraw.Draw(img)

    # envelope body (a soft shadow first)
    ex0, ey0, ex1, ey1 = 170, 290, 854, 744
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([ex0, ey0 + 26, ex1, ey1 + 26], radius=64, fill=(0, 30, 80, 90))
    from PIL import ImageFilter
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(22)))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([ex0, ey0, ex1, ey1], radius=64, fill=(255, 255, 255, 255))

    # the flap: a V from the top corners down to the middle, drawn as a pale triangle with a crisp edge
    mid = (S // 2, 560)
    d.polygon([(ex0 + 20, ey0 + 18), (ex1 - 20, ey0 + 18), mid], fill=(214, 232, 250, 255))
    d.line([(ex0 + 20, ey0 + 18), mid, (ex1 - 20, ey0 + 18)], fill=(120, 168, 222, 255), width=14, joint="curve")
    # lower folds
    d.line([(ex0 + 28, ey1 - 28), (S // 2 - 150, 560 + 70)], fill=(176, 206, 238, 255), width=12)
    d.line([(ex1 - 28, ey1 - 28), (S // 2 + 150, 560 + 70)], fill=(176, 206, 238, 255), width=12)

    # the open ring: a thick amber arc (about 300 degrees) in the lower right, overlapping the envelope corner
    cx, cy, r, w = 724, 704, 172, 70
    ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    rd = ImageDraw.Draw(ring)
    rd.ellipse([cx - r - 14, cy - r - 14, cx + r + 14, cy + r + 14], fill=(10, 72, 160, 255))     # a blue halo separates it from the envelope
    rd.arc([cx - r, cy - r, cx + r, cy + r], start=-35, end=265, fill=(255, 183, 0, 255), width=w)
    for angle in (-35, 265):                                                                      # round caps
        x = cx + (r - w / 2) * math.cos(math.radians(angle))
        y = cy + (r - w / 2) * math.sin(math.radians(angle))
        rd.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=(255, 183, 0, 255))
    img.alpha_composite(ring)
    return img


def main():
    os.makedirs(OUT, exist_ok=True)
    big = draw()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [big.resize((n, n), Image.LANCZOS) for n in sizes]
    ico = os.path.join(OUT, "openoutlook.ico")
    frames[-1].save(ico, format="ICO", sizes=[(n, n) for n in sizes], append_images=[])
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "openoutlook.png"))
    big.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, "openoutlook-512.png"))
    print("wrote", ico)


if __name__ == "__main__":
    main()
