"""Source colour variants and WCAG contrast (DESIGN.md §2.1.6, §11.5).

Run directly to print the variant table for every source in OnionLLM.Core and compare it with DESIGN.md:
    python tools/design/palette.py
"""
import colorsys
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCES_DIR = ROOT / "OnionLLM.Core" / "Sources"

LIGHT_BACKGROUNDS = ("#FFFFFF", "#F3F2F6")  # surface.1, surface.2 (light)
DARK_BACKGROUNDS = ("#16151C", "#26252E")   # surface.1, surface.3 (dark)
INITIAL_LIGHT = "#FFFFFF"
INITIAL_DARK = "#16151B"


def rgb(hex_color: str) -> tuple[float, float, float]:
    value = hex_color.lstrip("#")[-6:]
    return tuple(int(value[i:i + 2], 16) / 255 for i in (0, 2, 4))  # type: ignore[return-value]


def to_hex(color: tuple[float, float, float]) -> str:
    return "#" + "".join(f"{round(max(0.0, min(1.0, c)) * 255):02X}" for c in color)


def luminance(hex_color: str) -> float:
    def channel(c: float) -> float:
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

    r, g, b = (channel(c) for c in rgb(hex_color))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(a: str, b: str) -> float:
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def shift(hex_color: str, delta: float) -> str:
    h, l, s = colorsys.rgb_to_hls(*rgb(hex_color))
    return to_hex(colorsys.hls_to_rgb(h, max(0.0, min(1.0, l + delta)), s))


def adjust(hex_color: str, backgrounds: tuple[str, ...], step: float, minimum: float = 3.0) -> str:
    color = hex_color.upper()
    for _ in range(100):
        if all(contrast(color, bg) >= minimum for bg in backgrounds):
            return color
        color = shift(color, step)
    return color


def initial_for(background: str) -> str:
    return max((INITIAL_LIGHT, INITIAL_DARK), key=lambda text: contrast(text, background))


def avatar_for(background: str, text: str) -> str:
    step = -0.01 if text == INITIAL_LIGHT else 0.01
    color = background
    for _ in range(100):
        if contrast(text, color) >= 4.5:
            return color
        color = shift(color, step)
    return color


def variants(brand: str) -> dict[str, str]:
    light = adjust(brand, LIGHT_BACKGROUNDS, -0.01)
    dark = adjust(brand, DARK_BACKGROUNDS, 0.01)
    result = {"brand": brand.upper(), "light": light, "dark": dark}
    for theme, base in (("light", light), ("dark", dark)):
        text = initial_for(base)
        result[f"{theme}Avatar"] = avatar_for(base, text)
        result[f"{theme}OnAvatar"] = text
    return result


def core_sources() -> dict[str, str]:
    """Source id → brand colour, read from OnionLLM.Core/Sources/*Source.cs (the source of truth)."""
    found: dict[str, str] = {}
    for path in sorted(SOURCES_DIR.glob("*Source.cs")):
        text = path.read_text(encoding="utf-8")
        source_id = re.search(r'SourceId\s*=\s*"([^"]+)"', text)
        accent = re.search(r'AccentColor\s*=>\s*"(#[0-9A-Fa-f]{6})"', text)
        if source_id and accent:
            found[source_id.group(1)] = accent.group(1).upper()
    return found


if __name__ == "__main__":
    for source_id, brand in core_sources().items():
        v = variants(brand)
        print(f"{source_id:12} {brand} light {v['light']} dark {v['dark']} "
              f"avatar {v['lightAvatar']}/{v['darkAvatar']} initial {v['lightOnAvatar']}/{v['darkOnAvatar']}")
