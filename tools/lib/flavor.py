"""Item description prose: the deterministic flavor/mechanics bake.

Description = one flavor line + one mechanics line, the mechanics derived from the proposed
stats (element, on-hit status, or EquipBonus rider). The flavor line is the STABLE part of a
weapon's description (it doesn't change as the blade levels up); the Living Weapon runtime
anchors each weapon's in-card Kills counter to it, so flavor_anchor() must mirror exactly
what patch_names.py bakes into item.en.nxd.

Moved out of patch_names.py (rider_text/mechanics/flavor/plural) and gen_living_weapon_meta.py
(flavor_anchor) so the pipeline -- analyze.py is the CI gate -- imports from a library, never
from a manual nxd deploy script.
"""
import re

from .categories import WEAPON_CATS
from .riders import parse_rider, ALL8

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
        if f == 69:  # TarMaxHP - TarCurHP: damage = the TARGET's missing HP, ignores WP -- an execute
            return "Deals damage equal to the TARGET's missing HP -- near-nothing against a fresh foe, lethal against a wounded one."
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
        af = s.get("attackFlags") or ""
        if "ForcedTwoHands" in af and "Arc" not in af:  # melee two-hander; bows (Arc) are obviously 2H, skip
            parts.append("Held in two hands only.")
        rt = rider_text(s.get("rider"))  # weapons can carry an EquipBonus rider too (Arcanum MA+2, Dragon Rod Reraise)
        if rt:
            parts.append(rt)
    else:
        # accessory evasion is shown on the equip card's stat panel already -- don't repeat it in the desc.
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
    # No element/proc flavor available -> pick from a varied pool, indexed by id so adjacent
    # items never share a line (kills the "Sturdy X of dependable make" on 60 items problem).
    noun = CAT_NOUN.get(it["category"], it["vanillaName"].lower())
    pick = lambda pool: pool[it["id"] % len(pool)]
    if it["category"] in WEAPON_CATS:
        ev = s.get("evade", 0) or 0
        wp = s.get("wp", 0) or 0
        tier = it.get("tier", 3) or 3
        if ev >= 40:
            return f"A {noun} made to never be where the blow lands."
        if ev >= 20:
            return pick([f"A light, quick {noun} that favors the nimble hand.",
                         f"A nimble {noun} that reads a blow before it lands.",
                         f"A whisper-light {noun} that rewards a quick wrist."])
        if tier <= 2:
            return pick([f"A plain, dependable {noun} for the early road.",
                         f"A humble {noun}, the first a recruit is trusted with.",
                         f"A workmanlike {noun} with no pretensions."])
        if wp >= 20:
            return pick([f"A brutal {noun} that trades finesse for sheer force.",
                         f"A heavy {noun} that ends an argument in one swing.",
                         f"A {noun} forged for raw, uncompromising power."])
        return pick([f"A finely-wrought {noun} of proven temper.",
                     f"A well-balanced {noun}, the smith's quiet pride.",
                     f"A keen {noun} that has earned its keep many times over.",
                     f"A trustworthy {noun} a veteran would not part with."])
    if it["category"] in ACC_CATS:
        return pick([f"A finely-made {noun} that lends a quiet edge.",
                     f"An understated {noun} prized by those who know its worth.",
                     f"A {noun} of subtle, lasting craft.",
                     f"A traveler's {noun}, light and never in the way.",
                     f"A well-kept {noun} with a faint trace of old magic."])
    # armor (HP/MP pieces): pools by tier band, varied by id
    tier = it.get("tier", 3) or 3
    band = ("late" if ((s.get("hp", 0) or 0) >= 120 or tier >= 6) else ("early" if tier <= 2 else "mid"))
    return pick({
        "early": [f"Honest {noun} for a soldier just starting out.",
                  f"Simple {noun}, all a green recruit can afford.",
                  f"Roughspun {noun} that turns aside a careless blow.",
                  f"Plain {noun} issued to the rank and file.",
                  f"Cheap but serviceable {noun} for the long first march."],
        "mid":   [f"Sturdy {noun} of dependable make.",
                  f"Field-tempered {noun} that has weathered hard marches.",
                  f"Well-wrought {noun} trusted by seasoned hands.",
                  f"Heavy {noun} built to outlast a long campaign.",
                  f"Reliable {noun}, neither cheap nor showy."],
        "late":  [f"Masterwork {noun}, the pride of an armory.",
                  f"Peerless {noun} fit for a champion.",
                  f"Flawless {noun}, a master smith's life work.",
                  f"Storied {noun} whispered of in old war tales.",
                  f"Resplendent {noun} worn only by the worthy."],
    }[band])


def flavor_anchor(it):
    """The exact flavor line that leads this weapon's rendered description -- mirrors
    patch_names: a custom `desc` uses its first line; otherwise flavorOverride or flavor()."""
    custom = it.get("desc")
    if custom:
        return custom.split("\n", 1)[0]
    return it.get("flavorOverride") or flavor(it)


def is_living(it):
    """True when this item carries the Living Weapon card scaffold (the 2-char name-suffix slot
    and the trailing "Kills: 0" line). The SAME predicate patch_names.py bakes with and
    gen_living_weapon_meta.py selects on -- keep all three in lockstep."""
    eff_cat = (it.get("proposed") or {}).get("categoryOverride") or it.get("category")
    return eff_cat in WEAPON_CATS and not it.get("noGrowth")


def assemble_desc(it, scaffold=True):
    """The COMPLETE rendered card description, byte-for-byte what patch_names.py bakes into
    item.en.nxd: flavor line (+ generated mechanics), the uniform range sentence, the
    "+{atTier} Ability" signature block, and the Living Weapon "Kills: 0" scaffold. Extracted
    here so analyze.py's desc-budget gate and the baker CANNOT drift -- the same lockstep
    contract flavor_anchor carries for the first line. `scaffold` mirrors patch_names'
    SCAFFOLD_LIVING switch."""
    custom = it.get("desc")
    if custom:
        desc = custom
    else:
        # flavorOverride keeps a hand-written flavor line while STILL auto-appending mechanics
        # (so the stats stay in sync if retuned); plain flavor() is the fallback.
        fl = it.get("flavorOverride") or flavor(it)
        mech = mechanics(it)
        desc = fl + ("\n" + mech if mech else "")
    # reach line appended uniformly (custom + generated) so every ranged weapon phrases range identically
    rng = (it.get("proposed") or {}).get("range", 1) or 1
    if it.get("category") in WEAPON_CATS and rng >= 2:
        desc = desc.rstrip()
        if desc and not desc.endswith((".", "!", "?")):
            desc += "."
        desc += f" Strikes from up to {rng} tiles away."
    if scaffold and is_living(it):
        # p3Desc goes BEFORE the Kills scaffolding so gameplay prose stays grouped above the
        # tracker lines (the Kills anchor keys on the flavor line + literal prefixes). Header
        # names the ability via sigName (curated flavor name) falling back to displayLabel.
        sig = it.get("signature")
        p3 = sig.get("p3Desc") if sig else None
        if p3:
            sname = sig.get("sigName") or sig.get("displayLabel", "")
            at = sig.get("atTier", 3)
            header = f"+{at} Ability — {sname}" if sname else f"+{at} Ability"
            desc = desc.rstrip() + f"\n\n{header}\n{p3}"
        # "Kills: 0   " (digit + 3 spaces) as the LAST line -- the DLL paints left-aligned
        # digits into this fixed 4-char slot; literal MUST stay in lockstep with ByteScan.KillsDigits.
        desc = desc.rstrip() + "\n\nKills: 0   "
    return desc


def plural(name):
    low = name.lower()
    if low.endswith(("s", "x", "z", "ch", "sh")):
        return low + "es"
    return low + "s"
