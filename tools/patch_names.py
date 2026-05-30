#!/usr/bin/env python
"""
Batch-patch item.en.nxd names + descriptions from data/items.json.

For every renamed item it updates the Item-en row (Name / NameSingular / NamePlural / Name2 / Description),
then re-encodes the working sqlite to item.en.nxd via FF16Tools and drops it in the mod tree.

Description = one flavor line + one mechanics line (mechanics derived from the proposed stats, so the
player always sees what an item actually does: element, on-hit status, or EquipBonus rider).

Usage:
  python tools/patch_names.py            # patch all named items, re-encode, deploy nxd to mod tree
  python tools/patch_names.py --dry      # print the name/description that WOULD be written, no write
"""
import json, sys, subprocess, shutil, re
from pathlib import Path
import sqlite3

ROOT = Path(__file__).resolve().parent.parent
ITEMS = ROOT / "data" / "items.json"
SQLITE = ROOT / "working" / "pilot_item.sqlite"
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
MOD_NXD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd" / "item.en.nxd"
ENC_DIR = ROOT / "working" / "nxd_out"

WEAPON_CATS = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
               "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth"}
MELEE1_CATS = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff", "Flail", "Bag"}
CAT_NOUN = {"Knife": "knife", "NinjaBlade": "ninja blade", "Sword": "blade", "KnightSword": "knight's sword",
            "Katana": "katana", "Axe": "axe", "Rod": "rod", "Staff": "staff", "Flail": "flail", "Gun": "gun",
            "Crossbow": "crossbow", "Bow": "bow", "Instrument": "instrument", "Book": "tome", "Polearm": "spear",
            "Pole": "pole", "Bag": "bag", "Cloth": "cloth", "Shield": "shield", "Helmet": "helm", "Hat": "hat",
            "HairAdornment": "adornment", "Armor": "armor", "Clothing": "garb", "Robe": "robe", "Shoes": "boots",
            "Armguard": "gauntlet", "Ring": "ring", "Armlet": "armlet", "Cloak": "cloak", "Perfume": "perfume"}
PROC = {9: "Blind", 10: "Silence", 11: "Doom", 12: "Sleep", 14: "Immobilize", 22: "Poison", 101: "Oil"}

# thematic flavor clauses keyed by the item's defining trait (element > proc > rider > role)
ELEM_FLAVOR = {
    "Fire": "Forged in flame, it sears what it strikes.",
    "Ice": "A chill blade that bites like deep winter.",
    "Lightning": "It cracks with caged thunder.",
    "Wind": "Light as a gale and twice as quick.",
    "Earth": "Heavy with the weight of the mountains.",
    "Water": "It flows and strikes like the tide.",
    "Holy": "Blessed steel that burns the unclean.",
    "Dark": "Shadow clings to its edge.",
}
PROC_FLAVOR = {
    9: "Its glare leaves foes groping blind.", 10: "It smothers an enemy's voice mid-spell.",
    11: "A mark of doom rides every blow.", 12: "Its rhythm lulls the wary to sleep.",
    14: "It pins quarry fast where they stand.", 22: "A venom-slick edge that festers wounds.",
    101: "It slicks the target in clinging oil.",
}


def rider_text(rider):
    r = (rider or "None").strip()
    if r.lower() in ("none", ""):
        return ""
    out = []
    for m in re.finditer(r"(PA|MA|Speed|Move|Jump)\s*\+\s*(\d+)", r):
        nm = {"PA": "Physical Attack", "MA": "Magick Attack", "Speed": "Speed", "Move": "Move", "Jump": "Jump"}[m.group(1)]
        out.append(f"{nm} +{m.group(2)}.")
    for m in re.finditer(r"innate\s+([A-Za-z &,]+?)(?=$|,|\(| innate)", r):
        out.append("Grants " + m.group(1).strip().replace(" & ", " and ") + ".")
    for m in re.finditer(r"immune\s+([A-Za-z, ]+?)(?=$|\(|;)", r):
        st = re.sub(r"\b(Death|KO)\b", "instant death", m.group(1).strip(), flags=re.I)
        out.append("Wards against " + st + ".")
    for kind, verb in [("absorb", "Absorbs"), ("null", "Nullifies"), ("nullify", "Nullifies"),
                       ("halve", "Halves"), ("boost", "Strengthens")]:
        for m in re.finditer(kind + r"\s+([A-Za-z/, ]+?)(?=$|\(|;|,? \+)", r):
            els = m.group(1).strip().rstrip(".")
            out.append(f"{verb} {els} damage.")
    if "start invisible" in r.lower() or "start: invisible" in r.lower():
        out.append("Begins battle Transparent.")
    # de-dupe preserving order
    seen, ded = set(), []
    for o in out:
        if o not in seen:
            seen.add(o); ded.append(o)
    return " ".join(ded)


def mechanics(it):
    s = it["proposed"]
    parts = []
    if it["category"] in WEAPON_CATS:
        el = s.get("element", "None")
        if el not in ("None", None, ""):
            parts.append(f"Deals {el}-elemental damage.")
        p = s.get("onHitAbilityId", 0) or 0
        if p in PROC:
            parts.append(f"May inflict {PROC[p]} on hit.")
        if s.get("formula") in (6, 47, 48):
            parts.append({6: "Absorbs HP dealt.", 47: "Absorbs MP dealt.", 48: "Night Sword: drains HP."}[s["formula"]])
        ev = s.get("evade", 0) or 0
        if ev >= 15:
            parts.append(f"Turns aside {ev}% of physical blows.")
        rng = s.get("range", 1) or 1
        if rng >= 2 and it["category"] in MELEE1_CATS:
            parts.append(f"Strikes from up to {rng} tiles away.")
    else:
        rt = rider_text(s.get("rider"))
        if rt:
            parts.append(rt)
    return " ".join(parts)


def flavor(it):
    s = it["proposed"]
    if it["category"] in WEAPON_CATS:
        el = s.get("element", "None")
        if el in ELEM_FLAVOR:
            return ELEM_FLAVOR[el]
        p = s.get("onHitAbilityId", 0) or 0
        if p in PROC_FLAVOR:
            return PROC_FLAVOR[p]
    noun = CAT_NOUN.get(it["category"], it["vanillaName"].lower())
    if it["category"] in WEAPON_CATS:
        ev = s.get("evade", 0) or 0
        wp = s.get("wp", 0) or 0
        tier = it.get("tier", 3) or 3
        if ev >= 40:
            return f"A {noun} made to never be where the blow lands."
        if ev >= 20:
            return f"A light, quick {noun} that favors the nimble hand."
        if tier <= 2:
            return f"A plain, dependable {noun} for the early road."
        if wp >= 20:
            return f"A brutal {noun} that trades finesse for sheer force."
        return f"A finely-wrought {noun} of proven temper."
    # armor / accessory plain piece
    tier = it.get("tier", 3) or 3
    if tier <= 2:
        return f"Honest {noun} for a soldier just starting out."
    if (s.get("hp", 0) or 0) >= 120 or (it.get("tier", 0) or 0) >= 6:
        return f"Masterwork {noun}, the pride of an armory."
    return f"Sturdy {noun} of dependable make."


def plural(name):
    low = name.lower()
    if low.endswith(("s", "x", "z", "ch", "sh")):
        return low + "es"
    return low + "s"


def main():
    dry = "--dry" in sys.argv
    doc = json.loads(ITEMS.read_text(encoding="utf-8"))
    named = [it for it in doc["items"] if it.get("name") and it["name"] != "TBD"]
    con = sqlite3.connect(SQLITE)
    n = 0
    for it in named:
        custom = it.get("desc")
        if custom:
            desc = custom
        else:
            fl, mech = flavor(it), mechanics(it)
            desc = fl + ("\n" + mech if mech else "")
        name = it["name"]
        if dry:
            if it["id"] >= 11:  # show the new ones
                print(f"id{it['id']:>3} {name}\n      {desc!r}")
            continue
        con.execute('UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
                    (name, name.lower(), plural(name), name, desc, it["id"]))
        n += 1
    if dry:
        con.close(); return
    con.commit(); con.close()
    print(f"Patched {n} rows in {SQLITE.name}. Re-encoding to nxd...")
    ENC_DIR.mkdir(parents=True, exist_ok=True)
    r = subprocess.run([str(FF16), "sqlite-to-nxd", "-i", str(SQLITE), "-o", str(ENC_DIR), "-g", "fft"],
                       capture_output=True, text=True)
    out_nxd = ENC_DIR / "item.en.nxd"
    if not out_nxd.exists():
        print("ENCODE FAILED:\n" + r.stdout + r.stderr)
        sys.exit(1)
    MOD_NXD.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy(out_nxd, MOD_NXD)
    print(f"Wrote {MOD_NXD} ({out_nxd.stat().st_size} bytes).")


if __name__ == "__main__":
    main()
