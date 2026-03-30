"""Generate optiRAM app icon (.ico) — minimalistic, premium, Windows 11-style."""
from PIL import Image, ImageDraw
import os

SIZES = [16, 24, 32, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "optiRAM", "Resources", "app.ico")

# Design: Clean rounded-square with an abstract memory gauge.
# A vertical bar inside a rounded rectangle, partially filled — represents RAM usage.
# Single accent color palette. No skeuomorphic details.

ACCENT = (0, 120, 212, 255)       # Windows blue #0078D4
ACCENT_LIGHT = (96, 172, 232, 255)  # Lighter accent for the empty portion
BG = (32, 32, 32, 255)             # Dark background for the chip body


def draw_icon(size: int) -> Image.Image:
    """Draw a minimalistic memory gauge icon at the given pixel size."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size

    # ── Rounded-rectangle body ──
    pad = max(1, s // 16)
    r = max(2, s // 6)
    body = [pad, pad, s - pad - 1, s - pad - 1]
    d.rounded_rectangle(body, radius=r, fill=BG)

    # ── Inner gauge bar (vertical, partially filled) ──
    # The gauge represents ~65% fill to suggest active usage
    fill_ratio = 0.65

    bar_pad = max(3, s // 5)
    bar_left = bar_pad
    bar_right = s - bar_pad - 1
    bar_top = bar_pad
    bar_bottom = s - bar_pad - 1
    bar_height = bar_bottom - bar_top
    bar_r = max(1, s // 16)

    # Empty portion (top) — subtle lighter accent
    d.rounded_rectangle(
        [bar_left, bar_top, bar_right, bar_bottom],
        radius=bar_r,
        fill=(60, 60, 70, 255)
    )

    # Filled portion (bottom up) — bright accent
    fill_top = int(bar_top + bar_height * (1 - fill_ratio))
    if fill_top < bar_bottom:
        d.rounded_rectangle(
            [bar_left, fill_top, bar_right, bar_bottom],
            radius=bar_r,
            fill=ACCENT
        )

    # ── Horizontal notch lines across the gauge (memory segment markers) ──
    notch_count = 3
    notch_color = (255, 255, 255, 50)
    for i in range(1, notch_count + 1):
        ny = int(bar_top + bar_height * i / (notch_count + 1))
        line_inset = max(1, s // 32)
        d.line(
            [(bar_left + line_inset, ny), (bar_right - line_inset, ny)],
            fill=notch_color,
            width=max(1, s // 64)
        )

    # ── Subtle border on the body for definition ──
    d.rounded_rectangle(
        body, radius=r,
        outline=(80, 80, 90, 120),
        width=max(1, s // 64)
    )

    return img


def main():
    images = [draw_icon(sz) for sz in SIZES]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    # Save all sizes into one .ico — Pillow needs append_images for multi-size
    # Primary frame must be the largest — Pillow's ICO encoder requires this
    # for multi-size output. Windows reads all frames regardless of order.
    images[-1].save(
        OUT,
        format="ICO",
        append_images=images[:-1],
        sizes=[(sz, sz) for sz in SIZES],
    )
    print(f"Icon saved: {OUT}  ({os.path.getsize(OUT)} bytes, {len(SIZES)} sizes)")


if __name__ == "__main__":
    main()
