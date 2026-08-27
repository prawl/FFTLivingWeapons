#!/usr/bin/env python
"""LW-346: bake the tiny texture-parts files an extended-inventory item's menu icon needs.

Plain language: a menu icon is TWO files per surface, the picture (`ei_<id>_uitx.tex`) and a
92/96-byte parts file (`.utexpt`) that names that picture's own path. An extended item ships
a byte copy of its iconSource donor's ALREADY-RECOLORED picture (the donor's shipped .tex in
the mod tree), the way it borrows the donor's swing art: it stays outside the LW-247 tint
programme (tools/recolor_icons.py skips `extended` rows, whose racks and pins are sized to the
vanilla-range weapons). Its own treatment is LW-346 item 11 polish. Vanilla ids ship their parts files inside
the game's pac; a new id has none, so the game's icon load gives up ("not found/empty" in the
modloader log) and the card shows a blank. Proven live on id 261 2026-08-26 (owner screenshot,
docs/research/ITEM_CAP_261_BREAK_JOURNEY.md "ICONS SOLVED"): copy any donor pair and byte-patch
the embedded path to the new id, same length, and both surfaces render.

Layout of a parts file (read from the donor pair unpacked 2026-08-27): a 32-byte header whose
field at +0x0C is a RELATIVE offset from that field to the trailing "TexturePart" string, then
the NUL-terminated path of the icon's own .tex, then "TexturePart\\0". Because the id is always
three digits (`ei_037` -> `ei_261`), the swap keeps every length and offset unchanged; this
script asserts that rather than trusting it.

Needs the game install + FF16Tools (tools/lib/paths.py); the donor pair is unpacked from
data/enhanced/0008.pac into the OS temp dir. Outputs land in the mod tree beside the .tex
files and are committed like the 468 icon textures.

Usage: python tools/bake_extended_icon_parts.py            # every `extended` row in items.json
       python tools/bake_extended_icon_parts.py --selftest  # the patch arithmetic, no game files
"""
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.items import load_items
from lib.nxd import unpack
from lib.paths import ROOT, STEAM_FFT

ICON_PAC = STEAM_FFT / "data" / "enhanced" / "0008.pac"
MOD_ICON = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "ui" / "ffto" / "icon"
SURFACES = (("equip_item", "ei"), ("equip_item_s", "ei_s"))
DONOR_ID = 37   # Chaos Blade: the pair proven live on id 261; any vanilla weapon's pair has the same layout


def parts_inner_path(sub, pfx, item_id):
    return f"ui/ffto/icon/{sub}/textureparts/{pfx}_{item_id:03d}_uitx.utexpt"


def tex_inner_path(sub, pfx, item_id):
    return f"ui/ffto/icon/{sub}/texture/{pfx}_{item_id:03d}_uitx.tex"


def patch_parts(donor_bytes, sub, pfx, donor_id, item_id):
    """Pure: the donor parts file with its embedded .tex path re-pointed at item_id. Refuses
    (ValueError) unless the donor path is found exactly once and the swap is length-neutral."""
    old = tex_inner_path(sub, pfx, donor_id).encode("ascii") + b"\0"
    new = tex_inner_path(sub, pfx, item_id).encode("ascii") + b"\0"
    if len(old) != len(new):
        raise ValueError(f"path length changed ({len(old)} -> {len(new)}); the offset field would go stale")
    if donor_bytes.count(old) != 1:
        raise ValueError(f"donor parts file does not embed {old!r} exactly once")
    out = donor_bytes.replace(old, new)
    if len(out) != len(donor_bytes) or not out.endswith(b"TexturePart\0"):
        raise ValueError("patched parts file lost its shape")
    return out


def main():
    if "--selftest" in sys.argv:
        return selftest()
    ext = [it for it in load_items()["items"] if it.get("extended")]
    if not ext:
        print("no extended-inventory rows in data/items.json; nothing to bake")
        return 0
    with tempfile.TemporaryDirectory(prefix="lw_icon_parts_") as td:
        tmp = Path(td)
        donors = {}
        for sub, pfx in SURFACES:
            donors[(sub, pfx)] = unpack(ICON_PAC, parts_inner_path(sub, pfx, DONOR_ID), tmp).read_bytes()
        for it in ext:
            icon_donor = it.get("iconSource") or it["extended"]["cloneDonor"]
            for sub, pfx in SURFACES:
                donor_tex = MOD_ICON / sub / "texture" / f"{pfx}_{icon_donor:03d}_uitx.tex"
                if not donor_tex.exists():
                    raise SystemExit(f"id {it['id']} ({it['name']}): icon donor {icon_donor} has no shipped "
                                     f"{donor_tex.name} in the mod tree (run tools/recolor_icons.py first)")
                tex = MOD_ICON / sub / "texture" / f"{pfx}_{it['id']:03d}_uitx.tex"
                tex.write_bytes(donor_tex.read_bytes())
                print(f"  wrote {tex.relative_to(ROOT)} ({tex.stat().st_size} bytes, byte copy of id {icon_donor})")
                out = MOD_ICON / sub / "textureparts" / f"{pfx}_{it['id']:03d}_uitx.utexpt"
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(patch_parts(donors[(sub, pfx)], sub, pfx, DONOR_ID, it["id"]))
                print(f"  wrote {out.relative_to(ROOT)} ({out.stat().st_size} bytes, from id {DONOR_ID})")
    return 0


def selftest():
    donor = (b"\x20\x00\x00\x00\x0c\x00\x00\x00\x01\x00\x00\x00\x44\x00\x00\x00" + b"\0" * 8
             + b"\x60\x00\x00\x00\xc0\x00\x00\x00"
             + b"ui/ffto/icon/equip_item/texture/ei_037_uitx.tex\0TexturePart\0")
    out = patch_parts(donor, "equip_item", "ei", 37, 261)
    assert len(out) == len(donor) == 92
    assert b"ei_261_uitx.tex\0TexturePart\0" in out and b"037" not in out
    assert out[:32] == donor[:32]
    try:
        patch_parts(donor, "equip_item", "ei", 37, 1000)
        raise AssertionError("a four-digit id must be refused")
    except ValueError:
        pass
    try:
        patch_parts(donor.replace(b"ei_037", b"ei_038"), "equip_item", "ei", 37, 261)
        raise AssertionError("a donor without the expected path must be refused")
    except ValueError:
        pass
    print("bake_extended_icon_parts selftest: 3/3 passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
