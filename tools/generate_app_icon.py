from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "app" / "Assets"
PNG_PATH = ASSET_DIR / "DiskCleanupAssistant.png"
ICO_PATH = ASSET_DIR / "DiskCleanupAssistant.ico"


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def render_icon(size: int = 1024) -> Image.Image:
    scale = size / 256.0
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pixels = tile.load()
    for y in range(size):
        t = y / max(1, size - 1)
        red = round(45 * (1 - t) + 17 * t)
        green = round(125 * (1 - t) + 89 * t)
        blue = round(210 * (1 - t) + 175 * t)
        for x in range(size):
            pixels[x, y] = (red, green, blue, 255)
    tile.putalpha(rounded_mask(size, round(52 * scale)))
    canvas.alpha_composite(tile)

    draw = ImageDraw.Draw(canvas)
    white = (255, 255, 255, 255)
    blue = (23, 105, 194, 255)
    mint = (117, 214, 201, 255)

    def box(values):
        return tuple(round(value * scale) for value in values)

    draw.rounded_rectangle(box((75, 87, 181, 216)), radius=round(18 * scale), fill=white)
    draw.rectangle(box((75, 91, 181, 118)), fill=white)
    draw.rounded_rectangle(box((66, 63, 190, 84)), radius=round(10 * scale), fill=white)
    draw.rounded_rectangle(box((103, 38, 153, 75)), radius=round(13 * scale), fill=white)
    draw.rectangle(box((121, 55, 135, 71)), fill=blue)

    line_width = max(1, round(13 * scale))
    draw.line(box((108, 114, 108, 181)), fill=blue, width=line_width)
    draw.line(box((148, 114, 148, 181)), fill=blue, width=line_width)

    sparkle = [box((197, 40)), box((203, 54)), box((218, 60)), box((203, 66)), box((197, 81)), box((191, 66)), box((176, 60)), box((191, 54))]
    draw.polygon(sparkle, fill=white)
    inner = [box((197, 47)), box((201, 56)), box((211, 60)), box((201, 64)), box((197, 74)), box((193, 64)), box((183, 60)), box((193, 56))]
    draw.polygon(inner, fill=mint)
    return canvas


def main() -> None:
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    icon = render_icon()
    preview = icon.resize((256, 256), Image.Resampling.LANCZOS)
    preview.save(PNG_PATH, "PNG")
    icon.save(
        ICO_PATH,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(ICO_PATH)


if __name__ == "__main__":
    main()
