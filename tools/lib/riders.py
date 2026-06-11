#!/usr/bin/env python
"""
Turn an equip-bonus rider phrase into an EquipBonus field query.

A "rider" is the bonus text on a piece of armor or an accessory, e.g.
"PA+2, immune Silence" or "absorb Holy". parse_rider() reads that prose and
returns a dict of EquipBonus fields (PABonus, ImmuneStatus, AbsorbElements, ...)
that generate.py and patch_names.py use to find the row and build the tooltip. Returns None for "none"/empty.
"""
import re

ALL8 = "Dark, Holy, Water, Earth, Wind, Ice, Lightning, Fire"
SYN = {"death": "KO", "ko": "KO", "petrify": "Stone", "stone": "Stone", "don't act": "Disable",
       "dont act": "Disable", "don't move": "Immobilize", "transparent": "Invisible", "frog": "Toad"}
_KW = r"(innate|auto|immune|cancel|start|starting|initial|absorb|null|nullify|halve|boost|strong|weak)"
_FIELD = {"innate": "InnateStatus", "auto": "InnateStatus", "immune": "ImmuneStatus", "cancel": "ImmuneStatus",
          "start": "StartingStatus", "starting": "StartingStatus", "initial": "StartingStatus",
          "absorb": "AbsorbElements", "null": "NullifyElements", "nullify": "NullifyElements",
          "halve": "HalveElements", "boost": "StrongElements", "strong": "StrongElements", "weak": "WeakElements"}


def _names(val):
    val = re.sub(r"\(.*?\)", "", val)
    out = []
    for t in re.split(r",|/| and ", val):
        t = t.strip().rstrip(".")
        if not t:
            continue
        tl = t.lower()
        if tl in ("all", "all elements", "all element", "every element", "all elemental"):
            return ALL8.split(", ")
        out.append(SYN.get(tl, t[:1].upper() + t[1:]))
    return out


def parse_rider(s):
    s = (s or "").strip()
    if not s or s.lower() in ("none", "-", ""):
        return None
    q = {}
    low = s.lower()
    for kw, field in [("pa", "PABonus"), ("ma", "MABonus"), ("speed", "SpeedBonus"),
                      ("move", "MoveBonus"), ("jump", "JumpBonus")]:
        m = re.search(rf"\b{kw}\s*\+\s*(\d+)", low)
        if m:
            q[field] = int(m.group(1))
    if "boostjp" in low.replace(" ", ""):
        q["BoostJP"] = True
    for seg in re.split(r"(?=\b" + _KW + r"\b)", s, flags=re.I):
        m = re.match(r"\s*" + _KW + r"\s+(.+)", seg, flags=re.I | re.S)
        if not m:
            continue
        q.setdefault(_FIELD[m.group(1).lower()], []).extend(_names(m.group(2)))
    for k in list(q):
        if isinstance(q[k], list):
            q[k] = ", ".join(dict.fromkeys(q[k]))
    return q
