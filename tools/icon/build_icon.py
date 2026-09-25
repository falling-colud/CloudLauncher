"""Builds the app icon: a pixel cloud on a rust tile with stepped corners.

Each size is drawn on its own pixel grid (16, 20, 24 or 32 cells) and scaled up by a whole number,
so small sizes stay sharp instead of being shrunk from a big image.

    python tools/icon/build_icon.py

Writes CloudLauncher/Assets/appicon.ico, and the website's favicon.ico, img/logo.svg and
img/apple-touch-icon.png. Prints the inline SVGs the website's header and footer use (those
follow the page accent, so the tile and cloud are coloured by CSS). Needs Pillow.
"""
import io
import os
import struct

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WWW = os.path.join(ROOT, "CloudLauncher.Server", "wwwroot")

RUST = (0x60, 0x1B, 0x00)
RUST_LIGHT = (0x74, 0x24, 0x05)
RUST_DARK = (0x4A, 0x14, 0x00)
SHADOW = (0x33, 0x0E, 0x00)
CREAM = (0xF5, 0xF4, 0xEF)
SHADE = (0xD9, 0xD4, 0xC7)

# Cloud: three bumps on a flat base with rounded ends, in 0..1 coordinates (y down).
BUMPS = [(0.300, 0.590, 0.130), (0.480, 0.455, 0.165), (0.680, 0.520, 0.150)]
BASE = (0.17, 0.59, 0.83, 0.735)
BASE_R = 0.08
# Pixels cut from each corner row of the tile, per grid size.
CORNERS = {16: (2, 1), 20: (2, 1), 24: (2, 1, 1), 32: (3, 2, 1)}
# Icon size -> grid it is drawn on.
SIZES = {16: 16, 20: 20, 24: 24, 32: 32, 40: 20, 48: 24, 64: 32, 96: 32, 128: 32, 256: 32}


def in_cloud(u, v):
    if any((u - cx) ** 2 + (v - cy) ** 2 <= r * r for cx, cy, r in BUMPS):
        return True
    x0, y0, x1, y1 = BASE
    if x0 + BASE_R <= u <= x1 - BASE_R and y0 <= v <= y1:
        return True
    for cx in (x0 + BASE_R, x1 - BASE_R):
        cy = y1 - BASE_R
        if (u - cx) ** 2 + (v - cy) ** 2 <= BASE_R ** 2 or (abs(u - cx) <= BASE_R and y0 <= v <= cy):
            return True
    return False


def masks(n):
    tile = [[True] * n for _ in range(n)]
    for r, cut in enumerate(CORNERS[n]):
        for c in range(cut):
            for rr, cc in ((r, c), (r, n - 1 - c), (n - 1 - r, c), (n - 1 - r, n - 1 - c)):
                tile[rr][cc] = False
    cloud = [[in_cloud((c + 0.5) / n, (r + 0.5) / n) for c in range(n)] for r in range(n)]
    step = max(1, round(n / 16))
    top = [[False] * n for _ in range(n)]
    bottom = [[False] * n for _ in range(n)]
    shadow = [[False] * n for _ in range(n)]
    shade = [[False] * n for _ in range(n)]
    for r in range(n):
        for c in range(n):
            if not tile[r][c]:
                continue
            if cloud[r][c]:
                shade[r][c] = any(r + k >= n or not cloud[r + k][c] for k in range(1, step + 1))
                continue
            if r == 0 or not tile[r - 1][c]:
                top[r][c] = True
            elif r == n - 1 or not tile[r + 1][c]:
                bottom[r][c] = True
            shadow[r][c] = r >= step and c >= step and cloud[r - step][c - step]
    return tile, top, bottom, shadow, cloud, shade


def draw(n):
    tile, top, bottom, shadow, cloud, shade = masks(n)
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    px = img.load()
    for r in range(n):
        for c in range(n):
            if not tile[r][c]:
                continue
            color = RUST_LIGHT if top[r][c] else RUST_DARK if bottom[r][c] else RUST
            if shadow[r][c]:
                color = SHADOW
            if cloud[r][c]:
                color = SHADE if shade[r][c] else CREAM
            px[c, r] = color + (255,)
    return img


def write_ico(path, images):
    """32-bit BMP frames below 256 px and a PNG frame at 256, the layout Windows handles best."""
    blobs = []
    for im in images:
        size = im.size[0]
        if size >= 256:
            buf = io.BytesIO()
            im.save(buf, "PNG")
            blobs.append((size, buf.getvalue()))
            continue
        px = im.load()
        xor = b"".join(
            bytes((px[x, y][2], px[x, y][1], px[x, y][0], px[x, y][3]))
            for y in range(size - 1, -1, -1) for x in range(size))
        row_bytes = ((size + 31) // 32) * 4
        and_mask = bytearray()
        for y in range(size - 1, -1, -1):
            row = bytearray(row_bytes)
            for x in range(size):
                if px[x, y][3] == 0:
                    row[x // 8] |= 0x80 >> (x % 8)
            and_mask += row
        header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
        blobs.append((size, header + xor + bytes(and_mask)))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(blobs)))
    offset = 6 + 16 * len(blobs)
    for size, data in blobs:
        out.write(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in blobs:
        out.write(data)
    with open(path, "wb") as f:
        f.write(out.getvalue())


def path_data(mask):
    parts = []
    n = len(mask)
    for r in range(n):
        c = 0
        while c < n:
            if not mask[r][c]:
                c += 1
                continue
            end = c
            while end + 1 < n and mask[r][end + 1]:
                end += 1
            parts.append(f"M{c} {r}h{end - c + 1}v1h-{end - c + 1}z")
            c = end + 1
    return "".join(parts)


def svg(n, css_class=None):
    tile, top, bottom, shadow, cloud, shade = masks(n)
    attrs = f' class="{css_class}"' if css_class else ' xmlns="http://www.w3.org/2000/svg"'
    hidden = ' aria-hidden="true"' if css_class else ""
    return (f'<svg{attrs} viewBox="0 0 {n} {n}" shape-rendering="crispEdges"{hidden}>'
            f'<path class="logo-tile" fill="#601B00" d="{path_data(tile)}"/>'
            f'<path fill="#FFF" fill-opacity=".1" d="{path_data(top)}"/>'
            f'<path fill="#000" fill-opacity=".22" d="{path_data(bottom)}"/>'
            f'<path fill="#000" fill-opacity=".45" d="{path_data(shadow)}"/>'
            f'<path class="logo-cloud" fill="#F5F4EF" d="{path_data(cloud)}"/>'
            f'<path fill="#000" fill-opacity=".12" d="{path_data(shade)}"/>'
            f'</svg>')


def main():
    grids = {g: draw(g) for g in set(SIZES.values())}
    icons = {size: grids[g].resize((size, size), Image.NEAREST) for size, g in SIZES.items()}
    write_ico(os.path.join(ROOT, "CloudLauncher", "Assets", "appicon.ico"), [icons[s] for s in sorted(icons)])
    write_ico(os.path.join(WWW, "favicon.ico"), [icons[s] for s in (16, 32, 48, 64)])
    grids[32].resize((180, 180), Image.NEAREST).save(os.path.join(WWW, "img", "apple-touch-icon.png"))
    with open(os.path.join(WWW, "img", "logo.svg"), "w", encoding="utf-8", newline="\n") as f:
        f.write(svg(32))
    print("Header / banner logo (class logo or logo-big):")
    print(svg(32, "logo"))
    print()
    print("Footer logo (16 px):")
    print(svg(16, "logo"))


if __name__ == "__main__":
    main()
