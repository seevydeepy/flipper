from pathlib import Path

from PIL import Image, ImageDraw


def rounded_rect(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def make_icon(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = size // 16
    rounded_rect(draw, (pad, pad, size - pad, size - pad), size // 6, (27, 58, 75, 255))

    page = (
        int(size * 0.22),
        int(size * 0.18),
        int(size * 0.78),
        int(size * 0.84),
    )
    peek = (
        int(size * 0.30),
        int(size * 0.14),
        int(size * 0.82),
        int(size * 0.78),
    )
    rounded_rect(draw, peek, size // 18, (214, 196, 168, 255))
    rounded_rect(draw, page, size // 18, (246, 241, 232, 255))

    fold = [
        (int(size * 0.58), int(size * 0.18)),
        (int(size * 0.78), int(size * 0.18)),
        (int(size * 0.78), int(size * 0.38)),
    ]
    draw.polygon(fold, fill=(214, 196, 168, 255))
    draw.line(fold[:2] + [fold[2]], fill=(184, 163, 132, 255), width=max(1, size // 64))

    left = int(size * 0.30)
    right = int(size * 0.70)
    for i in range(4):
        y = int(size * (0.42 + i * 0.08))
        draw.line((left, y, right, y), fill=(27, 58, 75, 220), width=max(1, size // 48))
    note_r = max(2, size // 28)
    draw.ellipse((int(size * 0.36) - note_r, int(size * 0.50) - note_r, int(size * 0.36) + note_r, int(size * 0.50) + note_r), fill=(27, 58, 75, 255))
    draw.ellipse((int(size * 0.54) - note_r, int(size * 0.58) - note_r, int(size * 0.54) + note_r, int(size * 0.58) + note_r), fill=(27, 58, 75, 255))
    return img


def main():
    dest = Path(r"F:\code\Agent_worktrees\flipper-reader-polish\src\Flipper.App\Assets\AppIcon.ico")
    dest.parent.mkdir(parents=True, exist_ok=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [make_icon(size) for size in sizes]
    images[-1].save(dest, format="ICO", sizes=[(size, size) for size in sizes], append_images=images[:-1])
    print(dest, dest.stat().st_size)


if __name__ == "__main__":
    main()
