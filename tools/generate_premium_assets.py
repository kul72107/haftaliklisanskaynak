from __future__ import annotations

import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


PURPLE_DARK = (22, 9, 43)
PURPLE = (80, 38, 134)
PURPLE_2 = (112, 61, 184)
LAVENDER = (166, 132, 255)
EMERALD = (37, 211, 102)
BRONZE = (190, 130, 58)
INK = (24, 20, 33)
RED = (220, 78, 95)


def cover_resize(source: Path, dest: Path, size: tuple[int, int]) -> None:
    image = Image.open(source).convert("RGBA")
    target_w, target_h = size
    scale = max(target_w / image.width, target_h / image.height)
    resized = image.resize((math.ceil(image.width * scale), math.ceil(image.height * scale)), Image.Resampling.LANCZOS)
    left = (resized.width - target_w) // 2
    top = (resized.height - target_h) // 2
    resized.crop((left, top, left + target_w, top + target_h)).save(dest)


def fit_resize(source: Path, dest: Path, size: tuple[int, int]) -> None:
    image = Image.open(source).convert("RGBA")
    image.thumbnail(size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.alpha_composite(image, ((size[0] - image.width) // 2, (size[1] - image.height) // 2))
    canvas.save(dest)


def line_gradient(size: tuple[int, int], left: tuple[int, int, int], right: tuple[int, int, int], alpha: int = 255) -> Image.Image:
    w, h = size
    image = Image.new("RGBA", size)
    px = image.load()
    for x in range(w):
        t = x / max(1, w - 1)
        col = tuple(round(left[i] * (1 - t) + right[i] * t) for i in range(3)) + (alpha,)
        for y in range(h):
            px[x, y] = col
    return image


def add_soft_glow(image: Image.Image, box: tuple[int, int, int, int], color: tuple[int, int, int], alpha: int, blur: int) -> None:
    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(glow)
    draw.ellipse(box, fill=(*color, alpha))
    glow = glow.filter(ImageFilter.GaussianBlur(blur))
    image.alpha_composite(glow)


def draw_zip_box(draw: ImageDraw.ImageDraw, s: int, rect: tuple[int, int, int, int]) -> None:
    x0, y0, x1, y1 = [v * s for v in rect]
    draw.rounded_rectangle((x0, y0, x1, y1), radius=20 * s, fill=(*PURPLE, 242), outline=(*BRONZE, 210), width=3 * s)
    draw.rounded_rectangle((x0 + 13 * s, y0 + 12 * s, x1 - 13 * s, y1 - 14 * s), radius=14 * s, outline=(*LAVENDER, 92), width=2 * s)
    zx = (x0 + x1) // 2
    draw.line((zx, y0 + 12 * s, zx, y1 - 14 * s), fill=(*BRONZE, 245), width=4 * s)
    for i, y in enumerate(range(y0 + 18 * s, y1 - 24 * s, 13 * s)):
        x = zx - (7 if i % 2 == 0 else -1) * s
        draw.rounded_rectangle((x, y, x + 8 * s, y + 7 * s), radius=2 * s, fill=(*LAVENDER, 228))


def draw_cloud(draw: ImageDraw.ImageDraw, s: int, cx: int, cy: int, scale: float = 1.0, fill=(*LAVENDER, 220)) -> None:
    def p(v: float) -> int:
        return round(v * scale * s)

    x = cx * s
    y = cy * s
    draw.ellipse((x - p(42), y - p(14), x + p(4), y + p(32)), fill=fill)
    draw.ellipse((x - p(16), y - p(38), x + p(34), y + p(14)), fill=fill)
    draw.ellipse((x + p(18), y - p(22), x + p(62), y + p(26)), fill=fill)
    draw.rounded_rectangle((x - p(46), y + p(1), x + p(66), y + p(34)), radius=p(16), fill=fill)


def draw_icon(dest: Path, kind: str) -> None:
    s = 4
    n = 256
    image = Image.new("RGBA", (n * s, n * s), (0, 0, 0, 0))
    add_soft_glow(image, (26 * s, 30 * s, 230 * s, 234 * s), PURPLE_2, 130, 30 * s)
    add_soft_glow(image, (64 * s, 44 * s, 218 * s, 196 * s), EMERALD if kind == "backup-success" else LAVENDER, 42, 24 * s)
    draw = ImageDraw.Draw(image)
    draw.ellipse((38 * s, 38 * s, 218 * s, 218 * s), fill=(*PURPLE_DARK, 232), outline=(*LAVENDER, 120), width=3 * s)
    draw_zip_box(draw, s, (62, 78, 194, 180))

    if kind == "backup-success":
        draw.line((83 * s, 140 * s, 113 * s, 169 * s, 180 * s, 95 * s), fill=(*EMERALD, 255), width=13 * s, joint="curve")
    elif kind == "backup-warning":
        pts = [(128 * s, 55 * s), (196 * s, 180 * s), (60 * s, 180 * s)]
        draw.polygon(pts, fill=(*BRONZE, 245), outline=(*INK, 210))
        draw.rounded_rectangle((123 * s, 93 * s, 133 * s, 141 * s), radius=5 * s, fill=(*INK, 230))
        draw.ellipse((122 * s, 152 * s, 134 * s, 164 * s), fill=(*INK, 230))
    elif kind == "backup-failed":
        draw.line((88 * s, 91 * s, 169 * s, 172 * s), fill=(*RED, 255), width=13 * s)
        draw.line((169 * s, 91 * s, 88 * s, 172 * s), fill=(*RED, 255), width=13 * s)
    elif kind == "cloud-upload":
        draw_cloud(draw, s, 125, 101, 0.9, fill=(*LAVENDER, 235))
        draw.line((128 * s, 166 * s, 128 * s, 101 * s), fill=(*EMERALD, 250), width=10 * s)
        draw.polygon([(128 * s, 82 * s), (101 * s, 112 * s), (155 * s, 112 * s)], fill=(*EMERALD, 250))
    elif kind == "license-key":
        draw.ellipse((76 * s, 103 * s, 118 * s, 145 * s), outline=(*EMERALD, 255), width=9 * s)
        draw.line((116 * s, 124 * s, 184 * s, 124 * s), fill=(*EMERALD, 255), width=10 * s)
        draw.line((162 * s, 124 * s, 162 * s, 148 * s), fill=(*EMERALD, 255), width=8 * s)
        draw.line((184 * s, 124 * s, 184 * s, 143 * s), fill=(*EMERALD, 255), width=8 * s)
    elif kind == "encrypted-secrets":
        draw.rounded_rectangle((82 * s, 107 * s, 174 * s, 176 * s), radius=15 * s, fill=(*INK, 230), outline=(*BRONZE, 245), width=4 * s)
        draw.arc((94 * s, 62 * s, 162 * s, 132 * s), 185, 355, fill=(*BRONZE, 245), width=9 * s)
        draw.ellipse((121 * s, 136 * s, 135 * s, 150 * s), fill=(*EMERALD, 240))
        draw.rounded_rectangle((124 * s, 147 * s, 132 * s, 164 * s), radius=4 * s, fill=(*EMERALD, 240))
    elif kind == "schedule-clock":
        draw.ellipse((74 * s, 74 * s, 182 * s, 182 * s), fill=(*INK, 222), outline=(*BRONZE, 245), width=6 * s)
        draw.line((128 * s, 128 * s, 128 * s, 91 * s), fill=(*LAVENDER, 250), width=7 * s)
        draw.line((128 * s, 128 * s, 158 * s, 147 * s), fill=(*EMERALD, 250), width=7 * s)
        draw.ellipse((121 * s, 121 * s, 135 * s, 135 * s), fill=(*BRONZE, 255))
    elif kind == "zip-archive":
        draw.polygon([(86 * s, 53 * s), (156 * s, 53 * s), (188 * s, 86 * s), (188 * s, 184 * s), (86 * s, 184 * s)], fill=(*LAVENDER, 218), outline=(*BRONZE, 235))
        draw.polygon([(156 * s, 53 * s), (156 * s, 88 * s), (188 * s, 88 * s)], fill=(*PURPLE_2, 235))
        draw.line((126 * s, 62 * s, 126 * s, 177 * s), fill=(*INK, 220), width=4 * s)
        for y in range(69, 166, 14):
            draw.rectangle((120 * s, y * s, 132 * s, (y + 7) * s), fill=(*BRONZE, 255))

    image = image.resize((n, n), Image.Resampling.LANCZOS)
    image.save(dest)


def draw_empty_state(dest: Path, kind: str) -> None:
    w, h = 1200, 700
    image = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    add_soft_glow(image, (260, 100, 980, 620), PURPLE_2, 58, 70)
    draw.rounded_rectangle((345, 210, 855, 490), radius=46, fill=(*PURPLE_DARK, 54), outline=(*LAVENDER, 64), width=3)
    draw.rounded_rectangle((405, 260, 795, 450), radius=32, fill=(*PURPLE, 88), outline=(*BRONZE, 80), width=4)
    for x in range(575, 641, 16):
        draw.rounded_rectangle((x, 278, x + 8, 432), radius=3, fill=(*BRONZE, 128))

    if kind == "sources":
        for i in range(3):
            ox = 270 + i * 58
            oy = 184 + i * 28
            draw.rounded_rectangle((ox, oy, ox + 190, oy + 130), radius=18, fill=(*LAVENDER, 58), outline=(*LAVENDER, 80), width=3)
            draw.polygon([(ox + 118, oy), (ox + 190, oy + 66), (ox + 118, oy + 66)], fill=(*PURPLE_2, 66))
    elif kind == "targets":
        draw.ellipse((705, 190, 940, 425), fill=(*INK, 72), outline=(*BRONZE, 120), width=5)
        draw.ellipse((760, 245, 885, 370), fill=(*PURPLE_2, 92), outline=(*LAVENDER, 95), width=4)
        draw.line((820, 300, 820, 445), fill=(*EMERALD, 120), width=14)
        draw.polygon([(820, 474), (780, 430), (860, 430)], fill=(*EMERALD, 120))
    else:
        for y in (230, 285, 340, 395):
            draw.rounded_rectangle((305, y, 895, y + 24), radius=12, fill=(*LAVENDER, 54))
        draw.ellipse((244, 224, 282, 262), fill=(*EMERALD, 92))
        draw.ellipse((244, 279, 282, 317), fill=(*BRONZE, 92))
        draw.ellipse((244, 334, 282, 372), fill=(*LAVENDER, 92))

    image.save(dest)


def draw_divider(dest: Path) -> None:
    w, h = 1600, 80
    image = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    glow = line_gradient((w, 6), PURPLE_2, BRONZE, 190).filter(ImageFilter.GaussianBlur(5))
    image.alpha_composite(glow, (0, 36))
    draw = ImageDraw.Draw(image)
    for x in range(w):
        t = x / max(1, w - 1)
        alpha = round(40 + 180 * math.sin(math.pi * t))
        col = tuple(round(PURPLE_2[i] * (1 - t) + BRONZE[i] * t) for i in range(3)) + (alpha,)
        draw.point((x, 40), fill=col)
    image.save(dest)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pattern-src", required=True)
    parser.add_argument("--header-src", required=True)
    parser.add_argument("--sidebar-src", required=True)
    parser.add_argument("--icon-src", required=True)
    parser.add_argument("--assets-dir", default=r"src\ModernYedek.App\Assets")
    args = parser.parse_args()

    assets = Path(args.assets_dir)
    assets.mkdir(parents=True, exist_ok=True)

    cover_resize(Path(args.pattern_src), assets / "premium-pattern.png", (1024, 1024))
    cover_resize(Path(args.header_src), assets / "premium-header-banner.png", (2400, 520))
    cover_resize(Path(args.sidebar_src), assets / "premium-sidebar-texture.png", (700, 1600))
    fit_resize(Path(args.icon_src), assets / "myedek-icon-master.png", (1024, 1024))

    icon_master = Image.open(assets / "myedek-icon-master.png").convert("RGBA")
    icon_master.save(assets / "myedek.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    for kind in (
        "backup-success",
        "backup-warning",
        "backup-failed",
        "cloud-upload",
        "license-key",
        "encrypted-secrets",
        "schedule-clock",
        "zip-archive",
    ):
        draw_icon(assets / f"icon-{kind}.png", kind)

    draw_empty_state(assets / "empty-sources.png", "sources")
    draw_empty_state(assets / "empty-targets.png", "targets")
    draw_empty_state(assets / "empty-logs.png", "logs")
    draw_divider(assets / "premium-divider.png")


if __name__ == "__main__":
    main()
