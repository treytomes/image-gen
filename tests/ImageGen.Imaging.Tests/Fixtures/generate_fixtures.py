"""Generate PNG decode test fixtures for Png.Load.

Builds one PNG per scanline filter (0-4) for an 8x6 RGB image, plus a 5x4 RGBA
image, applying a single explicit filter type to every scanline. PNGs are written
by hand (stdlib zlib only) so the filter choice is exact and not left to an
encoder's heuristics.

Each file is then verified with Pillow (an independent PNG decoder): a fixture is
only trustworthy if a third-party decoder agrees it encodes the intended pixels.

Run from this folder:  python generate_fixtures.py   (requires Pillow)
"""

import os
import struct
import zlib

from PIL import Image  # independent oracle; used only to validate what we wrote


def _chunk(typ: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + typ
        + data
        + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF)
    )


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def encode(path: str, pixels: bytes, w: int, h: int, ch: int, filter_type: int) -> None:
    """Write a PNG applying one filter type to every scanline."""
    stride = w * ch
    raw = bytearray()
    prev = bytes(stride)
    for y in range(h):
        row = pixels[y * stride : (y + 1) * stride]
        out = bytearray([filter_type])
        for x in range(stride):
            a = row[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            v = row[x]
            if filter_type == 0:
                f = v
            elif filter_type == 1:
                f = (v - a) & 0xFF
            elif filter_type == 2:
                f = (v - b) & 0xFF
            elif filter_type == 3:
                f = (v - ((a + b) // 2)) & 0xFF
            elif filter_type == 4:
                f = (v - _paeth(a, b, c)) & 0xFF
            else:
                raise ValueError(filter_type)
            out.append(f)
        raw += out
        prev = row

    color_type = 6 if ch == 4 else 2
    ihdr = struct.pack(">IIBBBBB", w, h, 8, color_type, 0, 0, 0)
    png = (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", ihdr)
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _chunk(b"IEND", b"")
    )
    with open(path, "wb") as fh:
        fh.write(png)


def main() -> None:
    here = os.path.dirname(os.path.abspath(__file__))

    # 8x6 RGB: R=x*32, G=y*48, B=x*y*3
    w, h = 8, 6
    rgb = bytearray()
    for y in range(h):
        for x in range(w):
            rgb += bytes([(x * 32) & 0xFF, (y * 48) & 0xFF, (x * y * 3) & 0xFF])

    for ft in range(5):
        path = os.path.join(here, f"rgb_filter{ft}.png")
        encode(path, bytes(rgb), w, h, 3, ft)
        got = Image.open(path).convert("RGB").tobytes()
        assert got == bytes(rgb), f"Pillow disagrees on rgb_filter{ft}.png"
        print(f"rgb_filter{ft}.png  verified")

    # 5x4 RGBA: R=x*40, G=y*60, B=90, A=200
    w2, h2 = 5, 4
    rgba = bytearray()
    for y in range(h2):
        for x in range(w2):
            rgba += bytes([x * 40, y * 60, 90, 200])
    path = os.path.join(here, "rgba_filter0.png")
    encode(path, bytes(rgba), w2, h2, 4, 0)
    got = Image.open(path).tobytes()
    assert got == bytes(rgba), "Pillow disagrees on rgba_filter0.png"
    print("rgba_filter0.png  verified")


if __name__ == "__main__":
    main()
