"""Named-constant extraction from LivingWeapon/Offsets.cs, the probes' address source of truth.

LW-41: tools/probes/sentinel_probe.py shipped with hardcoded pre-1.5 sentinel addresses and fed
garbage sentinels (battleMode=0, slot9=0x1) into the LW-40 live incident, nearly misdirecting
the diagnosis. Probes now read the runtime's own Offsets.cs, so a re-anchor there re-anchors
every consuming probe in the same commit. Parsing is textual (a regex over
`public const long Name = 0x...;` and, since LW-387, `public static long Name => ImageBase.At(0x...);`),
deliberately dependency-free.

LW-387: the values returned are PREFERRED-BASE absolutes (image base 0x140000000), exactly the
documented pins. The Denuvo-free game build is ASLR-relocated, so a live probe must add the
module base itself (live base minus 0x140000000); teaching the probes that is a follow-up.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OFFSETS_CS = ROOT / "LivingWeapon" / "Offsets.cs"

_CONST = re.compile(
    r"(?:public|internal)\s+const\s+(?:long|int|uint|ulong)\s+(\w+)\s*=\s*(0x[0-9A-Fa-f_]+|\d+)\s*;")
# LW-387: image pins are rebased properties now, e.g. `public static long Slot0 => ImageBase.At(0x140782A30);`
_PIN = re.compile(
    r"(?:public|internal)\s+static\s+long\s+(\w+)\s*=>\s*ImageBase\.At\((0x[0-9A-Fa-f_]+)L?\)\s*;")


def _parse(text):
    out = {m.group(1): int(m.group(2).replace("_", ""), 0) for m in _CONST.finditer(text)}
    out.update({m.group(1): int(m.group(2).replace("_", ""), 0) for m in _PIN.finditer(text)})
    return out


def load(path=None):
    """Parse Offsets.cs into {name: int}. Raises on a missing file: a probe must never fall
    back to stale hardcoded numbers, that is the exact failure this module removes."""
    return _parse(Path(path or OFFSETS_CS).read_text(encoding="utf-8"))


def require(names, table=None):
    """Return [table[n] for n in names], raising with a clear message on any missing name
    (a renamed Offsets.cs constant fails loud here instead of feeding a wrong address)."""
    t = table if table is not None else load()
    missing = [n for n in names if n not in t]
    if missing:
        raise KeyError(f"Offsets.cs is missing expected constant(s): {', '.join(missing)}")
    return [t[n] for n in names]


def selftest():
    """Pure parser cases plus a shape check of the real file. No game required."""
    sample = (
        "public const long Slot0 = 0x140782A30;   // comment\n"
        "public const int BandSlots = 49;\n"
        "private const long NotPublic = 0x1;\n"
        "internal const long Internal = 0x2;\n"
        "public const long Under = 0x1_000;\n"
        "public static long Pinned => ImageBase.At(0x1411A7D10);   // comment\n"
        "internal static long PinnedL => ImageBase.At(0x14067E213L);\n"
        "public static long Derived => Pinned - 0x20;\n")
    t = _parse(sample)
    assert t["Slot0"] == 0x140782A30
    assert t["BandSlots"] == 49
    assert "NotPublic" not in t
    assert t["Internal"] == 0x2
    assert t["Under"] == 0x1000
    assert t["Pinned"] == 0x1411A7D10
    assert t["PinnedL"] == 0x14067E213
    assert "Derived" not in t

    real = load()
    for name in ("Slot0", "Slot9", "BattleMode", "EventId", "PauseFlag", "SubmenuFlag"):
        assert name in real, f"Offsets.cs no longer defines {name}"
        assert 0x140000000 <= real[name] < 0x150000000, (name, hex(real[name]))
    print(f"offsets selftest OK ({len(real)} constants parsed from {OFFSETS_CS.name})")


if __name__ == "__main__":
    selftest()
