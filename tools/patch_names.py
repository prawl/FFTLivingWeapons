#!/usr/bin/env python
"""
Batch-patch item.en.nxd names + descriptions from data/items.json.

For every renamed item it updates the Item-en row (Name / NameSingular / NamePlural / Name2 / Description),
then re-encodes the working sqlite to item.en.nxd via FF16Tools and drops it in the mod tree.

Description = one flavor line + one mechanics line, the mechanics derived from the proposed stats
(element, on-hit status, or EquipBonus rider).

Usage:
  python tools/patch_names.py            # patch all named items, re-encode, deploy nxd to mod tree
  python tools/patch_names.py --dry      # print the name/description that WOULD be written, no write
"""
import json, sys, subprocess, shutil, re
from pathlib import Path
import sqlite3
sys.path.insert(0, str(Path(__file__).resolve().parent))
from riders import parse_rider, ALL8

ROOT = Path(__file__).resolve().parent.parent
ITEMS = ROOT / "data" / "items.json"
SQLITE = ROOT / "working" / "pilot_item.sqlite"
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
MOD_NXD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd" / "item.en.nxd"
ENC_DIR = ROOT / "working" / "nxd_out"

WEAPON_CATS = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
               "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth"}
MELEE1_CATS = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff", "Flail", "Bag"}
ACC_CATS = {"Shoes", "Armguard", "Ring", "Armlet", "Cloak", "Perfume"}
CAT_NOUN = {"Knife": "knife", "NinjaBlade": "ninja blade", "Sword": "blade", "KnightSword": "knight's sword",
            "Katana": "katana", "Axe": "axe", "Rod": "rod", "Staff": "staff", "Flail": "flail", "Gun": "gun",
            "Crossbow": "crossbow", "Bow": "bow", "Instrument": "instrument", "Book": "tome", "Polearm": "spear",
            "Pole": "pole", "Bag": "bag", "Cloth": "cloth", "Shield": "shield", "Helmet": "helm", "Hat": "hat",
            "HairAdornment": "adornment", "Armor": "armor", "Clothing": "garb", "Robe": "robe", "Shoes": "boots",
            "Armguard": "gauntlet", "Ring": "ring", "Armlet": "armlet", "Cloak": "cloak", "Perfume": "perfume"}
PROC = {9: "Blind", 10: "Silence", 11: "Doom", 12: "Sleep", 13: "Don't Act", 14: "Immobilize",
        17: "Petrify", 18: "Slow", 22: "Poison", 23: "Confuse", 24: "Charm", 44: "Stop", 53: "Berserk",
        101: "Oil"}  # Formula-1 status procs (ItemOptions ids; in Formula 2/4 the same ids mean spells)
CAST = {42: "Gravity (damaging a share of the target's current HP)",
        45: "Sanguine Sword (draining the target's HP)",
        76: "Draw Out: Ashura"}  # Formula-2 NON-elemental ability casts by opt id (elemental casts handled separately)
# UiItemCategoryId = the equip-card weapon-TYPE label (item.en.nxd Item-en table). A repurposed weapon
# keeps its BASE slot's type id, so the card mislabels it (e.g. a KnightSword on the Giant's Axe slot
# reads "Axe"). Patch it to match the categoryOverride so the card reads right. Ids dumped from vanilla.
UICAT = {"Knife": 1, "NinjaBlade": 2, "Sword": 3, "KnightSword": 4, "Katana": 5, "Axe": 6,
         "Rod": 7, "Staff": 8, "Flail": 9, "Gun": 10, "Crossbow": 11, "Bow": 12, "Instrument": 13,
         "Book": 14, "Polearm": 15, "Pole": 16, "Bag": 17, "Cloth": 18}

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
    55: "An arcane edge that unravels enchantments.",
    101: "It slicks the target in clinging oil.",
}


def rider_text(rider):
    """Render an EquipBonus rider as prose via the structural parser (avoids regex overlap bugs)."""
    q = parse_rider(rider)
    if not q:
        return ""
    out = []
    for f, label in [("PABonus", "Physical Attack"), ("MABonus", "Magick Attack"),
                     ("SpeedBonus", "Speed"), ("MoveBonus", "Move"), ("JumpBonus", "Jump")]:
        if q.get(f):
            out.append(f"{label} +{q[f]}.")
    if q.get("InnateStatus"):
        out.append("Grants " + q["InnateStatus"].replace(" & ", " and ") + ".")
    if q.get("StartingStatus"):
        for st in q["StartingStatus"].split(", "):
            out.append("Begins battle Transparent." if st == "Invisible" else f"Begins battle with {st}.")
    if q.get("ImmuneStatus"):
        out.append("Wards against " + re.sub(r"\bKO\b", "instant death", q["ImmuneStatus"]) + ".")
    for f, verb in [("AbsorbElements", "Absorbs"), ("NullifyElements", "Nullifies"),
                    ("HalveElements", "Halves"), ("StrongElements", "Strengthens")]:
        if q.get(f):
            v = q[f]
            if set(v.split(", ")) == set(ALL8.split(", ")):
                v = "all elemental"
            out.append(f"{verb} {v} damage.")
    if q.get("WeakElements"):
        out.append(f"Weak to {q['WeakElements']} (takes extra damage).")
    if q.get("BoostJP"):
        out.append("Boosts JP earned.")
    return " ".join(out)


def mechanics(it):
    s = it["proposed"]
    parts = []
    if it["category"] in WEAPON_CATS:
        el = s.get("element", "None")
        f = s.get("formula", 1)
        p = s.get("onHitAbilityId", 0) or 0
        if f == 67:  # CasMaxHP - CasCurHP: damage = the wielder's missing HP, ignores WP
            return "Deals damage equal to the wielder's missing HP. Harmless at full health, devastating near death."
        if f == 4 and el not in ("None", None, ""):  # magic gun: attack IS the elemental spell; scales off FAITH (not MA), ignores armor
            spell = {"Lightning": "Thunder", "Fire": "Fire", "Ice": "Blizzard"}.get(el, el)
            parts.append(f"Its attack strikes as {spell} at no MP cost; the magic damage scales with the wielder's Faith.")
        elif el not in ("None", None, ""):
            parts.append(f"Deals {el}-elemental damage.")
        if f == 99:
            parts.append("Damage scales with the wielder's Speed, not Physical Attack.")
        if f not in (2, 4):  # Formula 2/4 read the opt id as a spell cast, not a status
            if f == 45 and p in PROC:  # formula 0x2D = 100% status (confirmed in-game): always lands
                parts.append(f"Always inflicts {PROC[p]} on hit.")
            elif p == 55:
                parts.append("Has a chance to remove the target's buffs on hit.")
            elif p == 95:
                parts.append("Has a chance to Stop, petrify, or kill on hit.")
            elif p == 41:
                parts.append("Has a chance to instantly kill on hit.")
            elif p in PROC:
                parts.append(f"Has a chance to inflict {PROC[p]} on hit.")
        if f in (6, 47, 48):
            parts.append({6: "Absorbs HP dealt.", 47: "Absorbs MP dealt.", 48: "Night Sword: drains HP."}[f])
        if f == 2 and el not in ("None", None, ""):  # vanilla elemental spell-cast on hit
            spell = {"Lightning": "Thunder", "Fire": "Fire", "Ice": "Blizzard"}.get(el, el)
            parts.append(f"Has a chance to cast {spell} on hit.")
        if f == 2 and p == 147:  # Rush = knockback
            parts.append("Has a chance to knock the target back a tile on hit.")
        if f == 2 and p in CAST:  # non-elemental ability cast on hit (Sanguine / Ashura / Gravity)
            parts.append(f"Has a chance to cast {CAST[p]} on hit.")
        rng = s.get("range", 1) or 1
        if rng >= 2:  # any reach weapon states its range (per user request)
            parts.append(f"Strikes from up to {rng} tiles away.")
        af = s.get("attackFlags") or ""
        if "ForcedTwoHands" in af and "Arc" not in af:  # melee two-hander; bows (Arc) are obviously 2H, skip
            parts.append("Held in two hands only (no shield).")
        rt = rider_text(s.get("rider"))  # weapons can carry an EquipBonus rider too (Arcanum MA+2, Dragon Rod Reraise)
        if rt:
            parts.append(rt)
    else:
        if it["category"] in ACC_CATS:
            pe, me = s.get("physEv", 0) or 0, s.get("magEv", 0) or 0
            if pe and me and pe == me:
                parts.append(f"Grants {pe}% evasion.")
            elif pe and me:
                parts.append(f"Grants {pe}% physical and {me}% magick evasion.")
            elif pe:
                parts.append(f"Grants {pe}% physical evasion.")
            elif me:
                parts.append(f"Grants {me}% magick evasion.")
        rt = rider_text(s.get("rider"))
        if rt:
            parts.append(rt)
    return " ".join(parts)


def flavor(it):
    s = it["proposed"]
    if it["category"] in WEAPON_CATS:
        if s.get("formula") == 67:
            noun = CAT_NOUN.get(it["category"], it["vanillaName"].lower())
            art = "An" if noun[:1].lower() in "aeiou" else "A"
            return f"{art} {noun} that feeds on its wielder's pain."
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
            # flavorOverride lets an item keep a hand-written flavor line while STILL auto-appending its
            # mechanics (so the stats stay in sync if we retune them); plain flavor() is the fallback.
            fl, mech = (it.get("flavorOverride") or flavor(it)), mechanics(it)
            desc = fl + ("\n" + mech if mech else "")
        name = it["name"]
        if dry:
            if it["id"] >= 11:  # show the new ones
                print(f"id{it['id']:>3} {name}\n      {desc!r}")
            continue
        con.execute('UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
                    (name, name.lower(), plural(name), name, desc, it["id"]))
        cat_ov = it["proposed"].get("categoryOverride")  # fix the equip-card type label for repurposed weapons
        if cat_ov in UICAT:
            con.execute('UPDATE "Item-en" SET UiItemCategoryId=? WHERE Key=?', (UICAT[cat_ov], it["id"]))
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
