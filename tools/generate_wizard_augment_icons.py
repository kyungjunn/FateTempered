"""Original vector-style wizard augment icons. Run with Python + Pillow.

Renders seven 256px RGBA sprites, preserving existing Unity importer GUIDs.
No downloaded art, fonts or image-generation service required.
"""
from pathlib import Path
import hashlib
import math
import re
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'Assets/Images/AugmentIcons'
SCALE = 4


def render(name, theme, symbol):
    ink, accent, bright = theme
    size = 256 * SCALE
    image = Image.new('RGBA', (size, size))
    draw = ImageDraw.Draw(image)

    def polygon(points, color):
        draw.polygon([(int(x * SCALE), int(y * SCALE)) for x, y in points], fill=color)

    def line(points, color, width=3):
        draw.line([(int(x * SCALE), int(y * SCALE)) for x, y in points], fill=color,
                  width=int(width * SCALE), joint='curve')

    def ellipse(box, color, width=2):
        draw.ellipse(tuple(int(v * SCALE) for v in box), outline=color, width=int(width * SCALE))

    def filled_ellipse(box, color):
        draw.ellipse(tuple(int(v * SCALE) for v in box), fill=color)

    for y in range(256):
        factor = .7 + .3 * (1 - abs(y - 100) / 156)
        color = tuple(int(c * factor) for c in ink) + (255,)
        draw.line((0, y * SCALE, size, y * SCALE), fill=color, width=SCALE)
    mask = Image.new('L', image.size)
    ImageDraw.Draw(mask).rounded_rectangle(
        (4 * SCALE, 4 * SCALE, 252 * SCALE, 252 * SCALE), radius=32 * SCALE, fill=255)
    image.putalpha(mask)
    draw.rounded_rectangle((8 * SCALE, 8 * SCALE, 248 * SCALE, 248 * SCALE),
                           radius=28 * SCALE, outline=accent, width=3 * SCALE)
    draw.rounded_rectangle((14 * SCALE, 14 * SCALE, 242 * SCALE, 242 * SCALE),
                           radius=24 * SCALE, outline=(*accent[:3], 100), width=SCALE)
    ellipse((34, 34, 222, 222), (*accent[:3], 80), 1)
    for x, y in [(25, 25), (231, 25), (25, 231), (231, 231)]:
        polygon([(x, y - 4), (x + 4, y), (x, y + 4), (x - 4, y)], bright)

    def arrow(start, end, color, width=5):
        line([start, end], color, width)
        dx, dy = end[0] - start[0], end[1] - start[1]
        length = math.hypot(dx, dy)
        dx /= length
        dy /= length
        polygon([end,
                 (end[0] - dx * 14 - dy * 7, end[1] - dy * 14 + dx * 7),
                 (end[0] - dx * 14 + dy * 7, end[1] - dy * 14 - dx * 7)], color)

    def bolt(cx, cy, scale=1):
        pts = [(3, -60), (-31, 5), (-3, 5), (-20, 60), (36, -17), (7, -17), (24, -60)]
        polygon([(cx + x * scale, cy + y * scale) for x, y in pts], bright)

    def orb(cx, cy, radius, color):
        filled_ellipse((cx - radius, cy - radius, cx + radius, cy + radius), color)

    if symbol == 'pierce':
        polygon([(52, 168), (168, 52), (204, 88), (88, 204)], accent)
        polygon([(68, 164), (168, 64), (192, 88), (92, 188)], bright)
        line([(48, 176), (80, 208)], (*accent[:3], 180), 6)
        for i in range(3):
            y = 86 + i * 22
            line([(42, y), (92 - i * 8, y)], accent, 4)
        arrow((178, 70), (214, 48), bright, 4)
    elif symbol == 'resonance':
        orb(128, 128, 28, accent)
        orb(128, 128, 14, bright)
        ellipse((78, 78, 178, 178), accent, 4)
        ellipse((54, 54, 202, 202), (*accent[:3], 140), 3)
        for angle in [20, 140, 260]:
            r = math.radians(angle)
            sx, sy = 128 + 38 * math.cos(r), 128 + 38 * math.sin(r)
            ex, ey = 128 + 78 * math.cos(r), 128 + 78 * math.sin(r)
            arrow((sx, sy), (ex, ey), bright, 4)
    elif symbol == 'horizon':
        ellipse((56, 56, 200, 200), accent, 5)
        ellipse((84, 84, 172, 172), (*accent[:3], 160), 3)
        orb(128, 128, 22, accent)
        orb(128, 128, 10, bright)
        for angle in [0, 45, 90, 135, 180, 225, 270, 315]:
            r = math.radians(angle)
            inner = 46
            outer = 78
            line([(128 + inner * math.cos(r), 128 + inner * math.sin(r)),
                  (128 + outer * math.cos(r), 128 + outer * math.sin(r))], bright, 4)
    elif symbol == 'residual':
        ellipse((62, 62, 194, 194), (*accent[:3], 90), 3)
        ellipse((78, 78, 178, 178), accent, 4)
        for angle, radius in [(40, 18), (160, 14), (250, 12)]:
            r = math.radians(angle)
            cx, cy = 128 + 52 * math.cos(r), 128 + 52 * math.sin(r)
            orb(cx, cy, radius, accent)
        orb(128, 128, 20, bright)
        line([(86, 196), (128, 176), (170, 196)], (*accent[:3], 180), 4)
    elif symbol == 'dual':
        for cx in [96, 160]:
            ellipse((cx - 46, 82, cx + 46, 174), accent, 4)
            orb(cx, 128, 16, bright)
        line([(122, 128), (134, 128)], bright, 5)
        polygon([(128, 58), (136, 74), (128, 86), (120, 74)], bright)
        polygon([(128, 170), (136, 186), (128, 198), (120, 186)], accent)
    elif symbol == 'collapse':
        ellipse((58, 58, 198, 198), accent, 4)
        ellipse((86, 86, 170, 170), (*accent[:3], 80), 3)
        pts = [(128, 70), (154, 108), (196, 118), (164, 148), (174, 192),
               (128, 170), (82, 192), (92, 148), (60, 118), (102, 108)]
        polygon(pts, accent)
        inner = [(128, 96), (144, 118), (168, 124), (150, 142), (156, 166),
                 (128, 152), (100, 166), (106, 142), (88, 124), (112, 118)]
        polygon(inner, bright)
    elif symbol == 'overcharge':
        ellipse((70, 148, 186, 206), accent, 4)
        ellipse((94, 162, 162, 192), (*accent[:3], 160), 3)
        bolt(128, 108, 0.92)
        for angle in [200, 230, 310, 340]:
            r = math.radians(angle)
            line([(128 + 42 * math.cos(r), 176 + 18 * math.sin(r)),
                  (128 + 70 * math.cos(r), 176 + 38 * math.sin(r))], accent, 4)
        arrow((188, 86), (214, 62), bright, 4)

    image.resize((256, 256), Image.Resampling.LANCZOS).save(OUTPUT / (name + '.png'))


def main():
    core = ((32, 22, 58), (163, 116, 255, 255), (243, 238, 255, 255))
    field = ((24, 18, 46), (176, 132, 255, 255), (248, 242, 255, 255))
    surge = ((38, 20, 52), (196, 110, 255, 255), (255, 236, 250, 255))
    icons = [
        ('WizardBasicPierce', core, 'pierce'),
        ('WizardResonanceRadius', field, 'resonance'),
        ('WizardGravityRadius', core, 'horizon'),
        ('WizardResidualGravity', core, 'residual'),
        ('WizardExtraField', field, 'dual'),
        ('WizardFieldCollapse', surge, 'collapse'),
        ('WizardOvercharge', surge, 'overcharge'),
    ]
    template = (OUTPUT / 'NinjaHwaDunDamage.png.meta').read_text()
    for name, theme, symbol in icons:
        render(name, theme, symbol)
        meta = OUTPUT / (name + '.png.meta')
        if not meta.exists():
            guid = hashlib.md5(('PainAndGain/WizardAugment/' + name).encode()).hexdigest()
            meta.write_text(re.sub(r'^guid: .*$', 'guid: ' + guid, template, flags=re.M))
        print(name)


if __name__ == '__main__':
    main()
