"""Builds the README / profile images in docs/media from transparent wheel renders.

Render the wheels first (see make.sh), then: python compose.py <renders dir> <out dir>
"""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SRC, OUT = sys.argv[1], sys.argv[2]
F = "C:/Windows/Fonts/"
BG = (13, 16, 23)
INK = (232, 236, 243)
MUTED = (150, 158, 172)
ACCENT = (157, 214, 60)       # Aurora's highlight green
LINE = (88, 98, 116)


def font(name, size):
    return ImageFont.truetype(F + name, size)


H1, H2, BODY, SMALL = font("seguisb.ttf", 46), font("seguisb.ttf", 25), font("segoeui.ttf", 21), font("segoeui.ttf", 18)


def canvas(w, h, glow_at=None, glow_r=420, glow=(40, 60, 90)):
    """Flat dark background with a soft glow behind each subject (no wallpaper, no noise).
    glow_at is one (x, y) or a list of them."""
    img = Image.new("RGB", (w, h), BG)
    spots = [] if glow_at is None else glow_at if isinstance(glow_at, list) else [glow_at]
    if spots:
        g = Image.new("L", (w, h), 0)
        for x, y in spots:
            ImageDraw.Draw(g).ellipse((x - glow_r, y - glow_r, x + glow_r, y + glow_r), fill=255)
        g = g.filter(ImageFilter.GaussianBlur(glow_r * 0.45))
        img = Image.composite(Image.new("RGB", (w, h), glow), img, g.point(lambda v: v * 0.55))
    return img


def wheel(name, size=None):
    w = Image.open(f"{SRC}/{name}.png").convert("RGBA")
    return w.resize((size, size), Image.LANCZOS) if size else w


def paste_center(img, w, cx, cy):
    img.paste(w, (cx - w.width // 2, cy - w.height // 2), w)


def text_block(d, x, y, title, body, align="left", width=None):
    """Title + one line of detail. Returns the block's height."""
    tw = d.textlength(title, font=H2)
    bw = d.textlength(body, font=BODY)
    if align == "right":
        d.text((x - tw, y), title, font=H2, fill=INK)
        d.text((x - bw, y + 34), body, font=BODY, fill=MUTED)
    else:
        d.text((x, y), title, font=H2, fill=INK)
        d.text((x, y + 34), body, font=BODY, fill=MUTED)
    return 64


def callout(d, side, text_x, path, title, body):
    """A label joined to the wheel by a leader line. path: points from the anchor outwards (canvas px); the line
    then runs level to the text, so it never crosses a slice's icon or label."""
    ax, ay = path[0]
    ey = path[-1][1]
    text_block(d, text_x, ey - 18, title, body, align="right" if side == "left" else "left")
    end = text_x + 18 if side == "left" else text_x - 18
    d.line(path + [(end, ey)], fill=LINE, width=2, joint="curve")
    d.ellipse((ax - 7, ay - 7, ax + 7, ay + 7), fill=BG, outline=ACCENT, width=3)


def caption(d, cx, y, title, body):
    tw = d.textlength(title, font=H2)
    d.text((cx - tw / 2, y), title, font=H2, fill=INK)
    for i, line in enumerate(body.split("\n")):
        bw = d.textlength(line, font=BODY)
        d.text((cx - bw / 2, y + 36 + i * 28), line, font=BODY, fill=MUTED)


def footer(d, w, y, items):
    """A row of short facts separated by dots, centred."""
    s = "   ·   ".join(items)
    d.text(((w - d.textlength(s, font=BODY)) / 2, y), s, font=BODY, fill=MUTED)


import math


def placer(cx, cy, size):
    """Maps render coordinates (798 px, centre 399) to the canvas, and polar angles (0° = right, clockwise)."""
    k = size / 798
    def P(x, y): return (round(cx + (x - 399) * k), round(cy + (y - 399) * k))
    def A(deg, r): return P(399 + r * math.cos(math.radians(deg)), 399 + r * math.sin(math.radians(deg)))
    return P, A, k


RIM = 262   # the wheel's outer edge in render px

# ───────────────────────── 1. The features, pointed out ─────────────────────────

W, Hh = 1800, 920
cx, cy, WS = 900, 490, 820
img = canvas(W, Hh, glow_at=(cx, cy), glow_r=400, glow=(28, 44, 70))
d = ImageDraw.Draw(img)
paste_center(img, wheel("hero", WS), cx, cy)
P, A, k = placer(cx, cy, WS)

t = "Spoke"
d.text(((W - d.textlength(t, font=H1)) / 2, 30), t, font=H1, fill=INK)
sub = "A wheel of shortcuts for Windows. Press a key, point, done."
d.text(((W - d.textlength(sub, font=BODY)) / 2, 92), sub, font=BODY, fill=MUTED)

LX, RX = cx - round(RIM * k) - 110, cx + round(RIM * k) + 110
# Left: slices on the rim, then the hub and the number ring reached along the gaps between slices.
callout(d, "left", LX, [A(-135, 236), A(-135, RIM + 18)], "Apps, sites and folders", "Plus searches and text snippets")
callout(d, "left", LX, [A(157.5, 94), A(157.5, RIM + 6)], "The hub says what runs", "Name and detail of the slice you point at")
callout(d, "left", LX, [A(112.5, 104), A(112.5, RIM + 6)], "Keyboard too", "1–8, arrows and Enter pick a slice")
# Right.
callout(d, "right", RX, [A(-45, 236), A(-45, RIM + 18)], "Built-in tools", "Screenshot and colour picker")
callout(d, "right", RX, [A(0, 258)], "Image history", "Paste any image you copied before")
callout(d, "right", RX, [A(45, 236), A(45, RIM + 18)], "Files and text", "Convert, resize, PDF · case, JSON, Base64")
callout(d, "right", RX, [P(508, 698)], "Many wheels", "Each with its own shortcut · Tab switches")
footer(d, W, Hh - 58, ["Opens in about 10 ms", "Never steals focus", "Native C# + Direct2D", "Free and open source"])
img.save(f"{OUT}/features.png", optimize=True)

# ───────────────────────── 2. Simple wheel or extended ring ─────────────────────────

W2, H2h = 1800, 820
img = canvas(W2, H2h, glow_at=[(450, 350), (1350, 350)], glow_r=300, glow=(26, 40, 64))
d = ImageDraw.Draw(img)
for i, (name, title, body) in enumerate([
    ("simple", "Simple wheel", "2 to 12 slices of your own"),
    ("ring", "Extended wheel", "More options fan out as an outer ring,\nso the wheel stays in view"),
]):
    x = 450 + i * 900
    paste_center(img, wheel(name, 700), x, 350)
    caption(d, x, 700, title, body)
img.save(f"{OUT}/wheel-styles.png", optimize=True)

# ───────────────────────── 3. Themes: dark and light ─────────────────────────

themes = [("Aurora", False), ("Ember", False), ("Graphite", False), ("Onyx", False), ("Paper", True)]
CW, CH, GAP = 400, 460, 24
W3 = len(themes) * CW + (len(themes) + 1) * GAP
H3 = CH + 2 * GAP + 80
img = canvas(W3, H3)
d = ImageDraw.Draw(img)
for i, (name, light) in enumerate(themes):
    x0 = GAP + i * (CW + GAP)
    card = Image.new("RGB", (CW, CH), (231, 234, 240) if light else (22, 27, 37))
    mask = Image.new("L", (CW, CH), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, CW - 1, CH - 1), radius=22, fill=255)
    img.paste(card, (x0, GAP), mask)
    paste_center(img, wheel(f"t-{name}", 540), x0 + CW // 2, GAP + 205)
    label = name + (" (light)" if light else "")
    lw = d.textlength(label, font=H2)
    d.text((x0 + (CW - lw) / 2, GAP + CH - 62), label, font=H2, fill=(40, 46, 58) if light else INK)
footer(d, W3, GAP + CH + 30, ["Five themes, light and dark", "Or match Windows light / dark mode", "Windows accent or your own colour", "Sizes S to XL"])
img.save(f"{OUT}/themes.png", optimize=True)

# ───────────────────────── 4. Drop mode ─────────────────────────

W4, H4 = 1800, 900
cx, cy, WS4 = 900, 470, 780
img = canvas(W4, H4, glow_at=(cx, cy), glow_r=360, glow=(28, 44, 70))
d = ImageDraw.Draw(img)
paste_center(img, wheel("drop", WS4), cx, cy)
P, A, k = placer(cx, cy, WS4)
t = "Drop files on the wheel"
d.text(((W4 - d.textlength(t, font=H1)) / 2, 28), t, font=H1, fill=INK)
sub = "Drag files, keep holding the button, press the shortcut. The wheel opens under the pointer."
d.text(((W4 - d.textlength(sub, font=BODY)) / 2, 90), sub, font=BODY, fill=MUTED)
LX, RX = cx - round(RIM * k) - 110, cx + round(RIM * k) + 110
callout(d, "left", LX, [A(-130, 236), A(-130, RIM + 18)], "Compress for sharing", "JPEG at 75%, at most 4K")
callout(d, "left", LX, [A(190, 236), A(190, RIM + 6)], "Resize", "Hover it to pick 50%, 25% or a width")
callout(d, "left", LX, [A(130, 94), A(130, RIM + 6)], "Before → after", "The hub compares with the original")
callout(d, "right", RX, [A(-50, 236), A(-50, RIM + 18)], "Size before you convert", "Every format shows its estimated size")
callout(d, "right", RX, [A(-10, 262)], "Drop on a format", "Saved beside the original, never overwritten")
callout(d, "right", RX, [A(30, 236), A(30, RIM + 18)], "Honest about codecs", "Formats this PC can't write say so")
footer(d, W4, H4 - 58, ["PNG · JPEG · WebP · AVIF · HEIC · GIF · TIFF · BMP", "Batches too", "Nothing is uploaded"])
img.save(f"{OUT}/drop-mode.png", optimize=True)
print("ok")
