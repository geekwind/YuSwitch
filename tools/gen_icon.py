#!/usr/bin/env python3
"""Generate YuSwitch app icons.

- If <root>/logo.png exists (user-provided logo), use it.
- Otherwise generate an "YS" brand placeholder (indigo gradient rounded
  square + white monogram) so the icon pipeline always has an input.
- Outputs:
    <root>/icon.ico            multi-resolution (16..256) Windows icon
    <root>/wwwroot/favicon.png static browser-tab favicon (64x64)
    <root>/wwwroot/logo-512.png 512x512 master for the macOS .app / Photino icon

Run again after replacing logo.png to regenerate everything. Code that
consumes icon.ico (csproj <ApplicationIcon>, MainForm embedded resource)
needs no changes.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
LOGO = ROOT / "logo.png"
ICO = ROOT / "icon.ico"
FAVICON = ROOT / "wwwroot" / "favicon.png"
LOGO512 = ROOT / "wwwroot" / "logo-512.png"

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
FAVICON_SIZE = 64
MASTER_SIZE = 1024

FONT_CANDIDATES = [
    "C:/Windows/Fonts/segoeuib.ttf",   # Segoe UI Bold
    "C:/Windows/Fonts/arialbd.ttf",    # Arial Bold
    "C:/Windows/Fonts/msyhbd.ttc",     # Microsoft YaHei Bold
]


def _round_rect(size: int, radius: float) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=radius, fill=(255, 255, 255, 255)
    )
    return img


def generate_placeholder() -> Image.Image:
    """1024x1024 indigo (#4f46e5 -> #6366f1) diagonal gradient + white 'YS'."""
    size = MASTER_SIZE
    top, bottom = (79, 70, 229), (99, 102, 241)

    mask = _round_rect(size, radius=size * 0.18)
    gradient = Image.new("RGBA", (size, size))
    px = gradient.load()
    for y in range(size):
        t = y / (size - 1)
        r = round(top[0] + (bottom[0] - top[0]) * t)
        g = round(top[1] + (bottom[1] - top[1]) * t)
        b = round(top[2] + (bottom[2] - top[2]) * t)
        for x in range(size):
            px[x, y] = (r, g, b, 255)
    img = Image.composite(gradient, Image.new("RGBA", (size, size), (0, 0, 0, 0)), mask)

    draw = ImageDraw.Draw(img)
    font = None
    for path in FONT_CANDIDATES:
        try:
            font = ImageFont.truetype(path, int(size * 0.42))
            break
        except OSError:
            continue
    if font is None:
        font = ImageFont.load_default()
    text = "YS"
    bb = draw.textbbox((0, 0), text, font=font)
    w, h = bb[2] - bb[0], bb[3] - bb[1]
    draw.text(
        ((size - w) / 2 - bb[0], (size - h) / 2 - bb[1]),
        text,
        font=font,
        fill=(255, 255, 255, 255),
    )
    return img


def fit_square(img: Image.Image, size: int) -> Image.Image:
    """Center-fit img onto a size x size transparent canvas (no distortion)."""
    img = img.convert("RGBA")
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    w, h = img.size
    scale = min(size / w, size / h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    resized = img.resize((nw, nh), Image.LANCZOS)
    canvas.paste(resized, ((size - nw) // 2, (size - nh) // 2), resized)
    return canvas


def main() -> None:
    if LOGO.exists():
        print(f"Using user logo: {LOGO}")
        source = Image.open(LOGO).convert("RGBA")
    else:
        print("logo.png not found - generating 'YS' placeholder")
        source = generate_placeholder()
        source.save(LOGO)
        print(f"Wrote placeholder: {LOGO}")

    master = fit_square(source, ICO_SIZES[-1])
    # Pillow generates each requested size from the master image.
    master.save(ICO, sizes=[(s, s) for s in ICO_SIZES])
    print(f"Wrote {ICO} ({ICO_SIZES})")

    favicon = fit_square(source, FAVICON_SIZE)
    favicon.save(FAVICON)
    print(f"Wrote {FAVICON} ({FAVICON_SIZE}x{FAVICON_SIZE})")

    logo512 = fit_square(source, 512)
    logo512.save(LOGO512)
    print(f"Wrote {LOGO512} (512x512)")


if __name__ == "__main__":
    main()
