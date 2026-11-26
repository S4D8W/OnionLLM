"""Generates the Windows tray icons (DESIGN.md §7.3): the onion mark in a monochrome variant matching the taskbar,
without the centre dot at small sizes, plus a status dot overlay (bottom right) for warning / danger.

    python tools/design/gen_tray.py

Writes OnionLLM/Resources/Raw/tray/tray-<taskbar>-<state>.ico (taskbar: light | dark, state: ok | warning | danger),
each with 16, 20, 24 and 32 px images (PNG-in-ICO). Pure Python (zlib + struct), no external packages. Like the other
generators this is a place where colour literals are allowed: glyph colours follow the taskbar, dot colours are the
light-theme status tokens (§2.1.4).
"""
import math
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "OnionLLM" / "Resources" / "Raw" / "tray"

GLYPH = {"light": (0x1C, 0x1B, 0x22), "dark": (0xFF, 0xFF, 0xFF)}  # light taskbar → dark glyph and vice versa
DOT = {"warning": (0xD9, 0x7A, 0x06), "danger": (0xD9, 0x3A, 0x3A)}
GAP_DEGREES = 40
SIZES = (16, 20, 24, 32)
SUPERSAMPLE = 6


def coverage(size: int, state: str) -> list[list[tuple[float, tuple[int, int, int]]]]:
    """Per-pixel (alpha, colour) with supersampling. Grid 24: stroke 2, radii 11/7/3 (inner arc dropped below 20 px)."""
    unit = size / 24
    centre = size / 2
    half_gap = math.radians(GAP_DEGREES / 2)
    radii = (11, 7, 3) if size >= 20 else (11, 6.5)
    stroke = max(2 * unit, 1.6)
    dot_radius = size * 0.2
    dot_centre = (size - dot_radius - 0.5, size - dot_radius - 0.5)
    pixels = []
    for y in range(size):
        row = []
        for x in range(size):
            hits_glyph = hits_dot = hits_ring = 0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    px = x + (sx + 0.5) / SUPERSAMPLE
                    py = y + (sy + 0.5) / SUPERSAMPLE
                    if state != "ok":
                        d = math.hypot(px - dot_centre[0], py - dot_centre[1])
                        if d <= dot_radius:
                            hits_dot += 1
                            continue
                        if d <= dot_radius + 1.0:
                            hits_ring += 1  # transparent cut-out ring so the dot reads on any glyph
                            continue
                    dx, dy = px - centre, py - centre
                    angle = math.atan2(dx, -dy)  # 0 = up
                    if abs(angle) < half_gap:
                        continue
                    distance = math.hypot(dx, dy)
                    if any(abs(distance - r * unit) <= stroke / 2 for r in radii):
                        hits_glyph += 1
            total = SUPERSAMPLE * SUPERSAMPLE
            if hits_dot:
                row.append((hits_dot / total, DOT[state]))
            else:
                row.append((hits_glyph / total, None))
        pixels.append(row)
    return pixels


def png(size: int, taskbar: str, state: str) -> bytes:
    raw = bytearray()
    for row in coverage(size, state):
        raw.append(0)
        for alpha, colour in row:
            r, g, b = colour or GLYPH[taskbar]
            raw += bytes((r, g, b, round(alpha * 255)))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def ico(images: list[tuple[int, bytes]]) -> bytes:
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    data = b""
    for size, image in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset + len(data))
        data += image
    return out + entries + data


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for taskbar in ("light", "dark"):
        for state in ("ok", "warning", "danger"):
            images = [(size, png(size, taskbar, state)) for size in SIZES]
            (OUT / f"tray-{taskbar}-{state}.ico").write_bytes(ico(images))
    print("tray icons ->", OUT)


if __name__ == "__main__":
    main()
