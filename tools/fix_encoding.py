"""One-off repair for double-encoded (mojibake) text in the published markdown files.

The corruption came from UTF-8 bytes being decoded as CP1252 and written back as
UTF-8, which turns an em dash into "â€”". This reverses that, then flattens
anything still non-ASCII to a plain equivalent.

ASCII-only is a deliberate constraint, not laziness: every non-ASCII character
committed here is one that has already been silently corrupted at least once, and
a GitHub page that renders "â€"" instead of "-" is worse than one that renders
"--". It also keeps diffs readable.
"""

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Characters that survived the reversal and need an explicit ASCII equivalent.
SUBSTITUTIONS = {
    "\u2264": "<=",  # <=
    "\u2265": ">=",  # >=
    "\u2014": "-",  # em dash
    "\u2013": "-",  # en dash
    "\u2012": "-",  # figure dash
    "\u2192": "->",  # right arrow
    "\u00d7": "x",  # multiplication sign
    "\u00b7": "*",  # middle dot
    "\u2022": "*",  # bullet
    "\u2026": "...",  # ellipsis
    "\u2018": "'",  # left single quote
    "\u2019": "'",  # right single quote
    "\u201c": '"',  # left double quote
    "\u201d": '"',  # right double quote
    "\u2500": "-",  # box drawing horizontal
    "\u2502": "|",  # box drawing vertical
    "\u250c": "+",  # box drawing down and right
    "\u2510": "+",  # box drawing down and left
    "\u2514": "+",  # box drawing up and right
    "\u2518": "+",  # box drawing up and left
    "\u251c": "+",  # box drawing vertical and right
    "\u2524": "+",  # box drawing vertical and left
    "\u2534": "+",  # box drawing up and horizontal
    "\u252c": "+",  # box drawing down and horizontal
    "\u256d": "+",  # rounded top left
    "\u256e": "+",  # rounded top right
    "\u2570": "+",  # rounded bottom left
    "\u256f": "+",  # rounded bottom right
    "\u2500\u2500": "--",
    "\u00a0": " ",
}


def reverse_mojibake(text: str) -> tuple[str, bool]:
    """Undo a CP1252 mis-decode of UTF-8 bytes, if that is what happened."""
    try:
        return text.encode("cp1252").decode("utf-8"), True
    except (UnicodeEncodeError, UnicodeDecodeError):
        return text, False


def flatten(text: str) -> str:
    for bad, good in SUBSTITUTIONS.items():
        text = text.replace(bad, good)
    # Anything left is decoration we can live without.
    return "".join(ch if ord(ch) < 128 else "" for ch in text)


def main() -> int:
    targets = sorted(
        p
        for p in ROOT.rglob("*.md")
        if ".git" not in p.parts and not any(part in {"bin", "obj"} for part in p.parts)
    )

    for path in targets:
        original = path.read_text(encoding="utf-8")
        repaired, reversed_ok = reverse_mojibake(original)
        repaired = flatten(repaired)

        if repaired == original:
            print(f"clean   {path.relative_to(ROOT)}")
            continue

        path.write_text(repaired, encoding="utf-8", newline="\n")
        note = "reversed+flattened" if reversed_ok else "flattened"
        print(f"fixed   {path.relative_to(ROOT)}  ({note})")

    # Verify nothing non-ASCII survives anywhere in the published markdown.
    failures = []
    for path in targets:
        text = path.read_text(encoding="utf-8")
        offenders = {ch for ch in text if ord(ch) >= 128}
        if offenders:
            failures.append((path, offenders))

    if failures:
        print("\nSTILL NON-ASCII:", file=sys.stderr)
        for path, offenders in failures:
            print(f"  {path.relative_to(ROOT)}: {offenders}", file=sys.stderr)
        return 1

    print(f"\nAll {len(targets)} markdown files are pure ASCII.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
