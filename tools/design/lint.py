"""Design-system lint (DESIGN.md §10.1): no visual literals outside Resources/Styles/Tokens.*.

    python tools/design/lint.py            # list every violation, exit 1 if any
    python tools/design/lint.py --summary  # counts per file and rule (for the PR description)
"""
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "OnionLLM"
SKIP_DIRS = {"bin", "obj", "AppPackages", "BundleArtifacts", "Fonts", "Images", "AppIcon", "Splash", "Raw"}

RULES = {
    "hex colour": re.compile(r"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b"),
    "FontSize literal": re.compile(r'FontSize="\d'),
    "Margin literal": re.compile(r'Margin="-?\d'),
    "Padding literal": re.compile(r'Padding="\d'),
    "CornerRadius literal": re.compile(r'CornerRadius="\d'),
    "Spacing literal": re.compile(r'(?:Row|Column)?Spacing="\d'),
    "Width/Height literal": re.compile(r'(?:Width|Height)Request="\d'),
    "AppThemeBinding": re.compile(r"AppThemeBinding"),
    "platform font": re.compile(r"Segoe|Consolas|OpenSans|SF Symbols"),
    "DisplayAlert": re.compile(r"DisplayAlert"),
    "built-in ProgressBar": re.compile(r"<ProgressBar\b"),
}


def allowed(path: Path) -> bool:
    relative = path.relative_to(APP).as_posix()
    return relative.startswith("Resources/Styles/Tokens.") or relative == "Resources/Icons.cs"


def files() -> list[Path]:
    found = []
    for path in APP.rglob("*"):
        if path.suffix not in {".xaml", ".cs"} or any(part in SKIP_DIRS for part in path.relative_to(APP).parts):
            continue
        if path.relative_to(APP).parts[0] == "Platforms" and path.suffix == ".xaml":
            continue
        if not allowed(path):
            found.append(path)
    return sorted(found)


def main() -> int:
    summary = "--summary" in sys.argv
    violations: list[tuple[Path, int, str, str]] = []
    for path in files():
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("///"):
                continue
            for rule, pattern in RULES.items():
                if pattern.search(line):
                    violations.append((path, number, rule, stripped))

    if summary:
        per_file = Counter(path.relative_to(ROOT).as_posix() for path, *_ in violations)
        per_rule = Counter(rule for _, _, rule, _ in violations)
        for name, count in per_file.most_common():
            print(f"{count:5}  {name}")
        print()
        for name, count in per_rule.most_common():
            print(f"{count:5}  {name}")
    else:
        for path, number, rule, text in violations:
            print(f"{path.relative_to(ROOT).as_posix()}:{number}: {rule}: {text[:120]}")
    print(f"\n{len(violations)} violation(s)")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
