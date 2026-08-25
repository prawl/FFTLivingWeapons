#!/usr/bin/env python
"""LW-251: make the ex-flail weapons draw SWORD frames, by two u16 edits in the shape files.

THE BUG THIS FIXES. The rebalance retypes the four vanilla flails (67 Warbrand, 68 Bloodlash,
69 Climhazzard, 70 Sasori) into sword-family types. The TYPE override lands (equip class,
skills, swing motion), but the drawn frames still come from the weapon's GRAPHIC class, and
the sword motion's frame offsets are added to the FLAIL class's base frame: they run off the
end of the flail's small frame block into neighbouring frame records, and the renderer draws
the resulting garbage slice (owner screenshot tools/probes/lw251_warbrand_jacked.png).

THE MECHANISM. battle_wep1_shp.bin / battle_wep2_shp.bin (FFTPack files 63/64, read at every
battle load per the loader log; format identical to PSX WEP1/WEP2.SHP, sizes 5218/5436 match
the PSX originals byte for byte) open with a 0x44-byte header: u32 swim pointer, then 32 u16
ZERO FRAMES, one per weapon-graphic class. Every animation frame index is relative to the
acting weapon's class zero frame. Decoded from the files themselves (parser cross-checked
against the open-source TacticsTemplateG Shp.gd and the ffhacktics SHP notes):

    the index is the item TYPE (the equipment category enum), per the open-source
    unit_animation_manager.gd consumer: type 9 = Flail (base 86 in wep1 / 92 in wep2,
    SHARED with type 6 Axe), types 2,3,4,5,7,8 = the blade family (base 40 / 43, a
    46-frame block larger than any motion offset observed). Round 1 of this probe edited
    index 11 believing it the flail from its two-piece base frame; that is the CROSSBOW
    (bow body + bolt), and the live round proved it: the loader log showed both modded
    files served ("Accessing modded file 63/64") while the garbage persisted unchanged
    (tools/probes/lw251_warbrand_probe1.png). The surviving model: the renderer takes the
    base frame from the item's VANILLA type (a data source the table categoryOverride does
    not reach) and adds the NEW type's motion offsets; flail base 86 plus sword offsets
    up to ~45 overruns the 40-frame axe/flail block into neighbouring records, which is
    the garbage slice on screen.

THE EDIT (round 2): zero_frames[9] := the blade base (40 in wep1, 43 in wep2). Old-type
base + sword offsets then lands on native sword frames for every motion. Only the four
ex-flails are type 9; the Axe entry (6) keeps base 86 so the ex-axes are untouched. Pure render data: no item id, no damage path
(the damage-coupled levers were CWeapon and the render global, neither is touched here).
KNOWN TRADEOFF: all four ex-flails are type 9, so Bloodlash/Climhazzard/Sasori change
silhouette too. Their colours stay their own (the runtime paints per weapon).

USAGE:
  python lw251_wep_shape_probe.py build     # extract pristine, patch, verify, stage in %TEMP%
  python lw251_wep_shape_probe.py deploy    # build + copy both files into the INSTALLED mod
                                            # (game must be restarted; fftpack registration is
                                            # per launch even though the file is read per battle)
  python lw251_wep_shape_probe.py revert    # delete the two files from the installed mod
  python lw251_wep_shape_probe.py status    # what is deployed right now, with hashes

Verification in build: the patched file must differ from pristine at EXACTLY bytes
0x16-0x17 (4 + 9*2), value old base -> blade base, and nowhere else.
"""
import hashlib
import os
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lw251_wep_spr_forge as fg   # GAME/FF16TOOLS/SOURCE_PAC live there

FLAIL_CLASS = 9
ZF_OFF = 4 + FLAIL_CLASS * 2          # 0x16: the one u16 this probe may change per file
FILES = {
    "battle_wep1_shp.bin": {"size": 5218, "flail_base": 86, "blade_base": 40},
    "battle_wep2_shp.bin": {"size": 5436, "flail_base": 92, "blade_base": 43},
}
INSTALL_DIR = pathlib.Path(fg.GAME) / "Reloaded" / "Mods" / "prawl.fft.livingweapons" / \
    "FFTIVC" / "data" / "enhanced" / "fftpack" / "unit"


def extract_pristine(name, wd):
    inner = f"fftpack/unit/{name}"
    out = os.path.join(wd, "unpack")
    subprocess.run([fg.FF16TOOLS, "unpack", "-i", fg.SOURCE_PAC, "-f", inner, "-o", out,
                    "-g", "fft"], capture_output=True)
    p = pathlib.Path(out, *inner.split("/"))
    if not p.is_file():
        sys.exit(f"unpack produced no {p}")
    raw = p.read_bytes()
    want = FILES[name]["size"]
    if len(raw) != want:
        sys.exit(f"{name} is {len(raw)} bytes, expected {want}; format changed, STOP")
    return raw


def build(wd):
    staged = {}
    for name, spec in FILES.items():
        raw = bytearray(extract_pristine(name, wd))
        old = struct.unpack_from("<H", raw, ZF_OFF)[0]
        if old != spec["flail_base"]:
            sys.exit(f"{name}: zero_frames[9] reads {old}, expected {spec['flail_base']}; "
                     "the file is not the layout this probe was written against, STOP")
        struct.pack_into("<H", raw, ZF_OFF, spec["blade_base"])
        pristine = extract_pristine(name, wd)
        diff = [i for i, (a, b) in enumerate(zip(pristine, raw)) if a != b]
        if not all(ZF_OFF <= i <= ZF_OFF + 1 for i in diff) or not diff:
            sys.exit(f"{name}: patched bytes {diff} are not exactly the type-9 zero frame, STOP")
        dst = pathlib.Path(wd, name)
        dst.write_bytes(raw)
        staged[name] = dst
        print(f"  {name}: zero_frames[9] {old} -> {spec['blade_base']}, "
              f"{len(diff)} byte(s) changed, staged")
    return staged


def cmd_deploy():
    wd = tempfile.mkdtemp(prefix="lw251_shp_patch_")
    staged = build(wd)
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    for name, src in staged.items():
        shutil.copy2(src, INSTALL_DIR / name)
        print(f"  deployed {INSTALL_DIR / name}")
    print("RESTART the game to register the files, then any battle: the four ex-flails")
    print("(Warbrand, Bloodlash, Climhazzard, Sasori) should swing a proper blade.")


def cmd_revert():
    for name in FILES:
        p = INSTALL_DIR / name
        if p.is_file():
            p.unlink()
            print(f"  removed {p}")
        else:
            print(f"  not present: {p}")
    print("restart the game to take effect")


def cmd_status():
    wd = tempfile.mkdtemp(prefix="lw251_shp_status_")
    for name, spec in FILES.items():
        p = INSTALL_DIR / name
        if not p.is_file():
            print(f"  {name}: NOT deployed")
            continue
        raw = p.read_bytes()
        zf = struct.unpack_from("<H", raw, ZF_OFF)[0]
        pristine = extract_pristine(name, wd)
        n = sum(1 for a, b in zip(pristine, raw) if a != b)
        print(f"  {name}: deployed, zero_frames[9]={zf} "
              f"({'blade' if zf == spec['blade_base'] else 'flail' if zf == spec['flail_base'] else 'UNEXPECTED'}), "
              f"{n} byte(s) differ from pristine, md5 {hashlib.md5(raw).hexdigest()[:8]}")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "build":
        build(tempfile.mkdtemp(prefix="lw251_shp_build_"))
    elif mode == "deploy":
        cmd_deploy()
    elif mode == "revert":
        cmd_revert()
    elif mode == "status":
        cmd_status()
    else:
        print(__doc__)
