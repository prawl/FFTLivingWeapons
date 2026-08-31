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
        76: "Draw Out: Ashura",
        # The 2026-06-02 thematic may-cast pass (9433f22) gave seven bland weapons these
        # procs WITH hand-written card text; the 2026-06-07 generated-desc rewrite
        # (65b2a88) dropped the prose because this map never learned the ids, so the
        # procs shipped silently for eleven weeks (LW-320 audit find, owner-ruled
        # re-advertised 2026-08-25). Names from the vanilla ability table
        # (working/nxd_ability/ability.sqlite).
        # LW-322 budget release valve #1 (2026-08-25): short form -- the only card
        # carrying this proc IS Kiku-ichimonji, so "Draw Out" alone is unambiguous
        # there. The full name lives in the grid's onHit cell.
        83: "Draw Out",
        102: "Aurablast",
        139: "Rend Armor (breaking the target's armor)",
        141: "Rend Weapon (breaking the target's weapon)",
        213: "Leg Shot (Immobilize)",  # short form: the long clause blew Huntress's 205-char card budget
        249: "Fire Breath",
        # LW-331 / LW-352 (2026-08-27): the Mystic-style status spells the instrument and book
        # lines cast on hit. Names from the vanilla ability table (working/skeptic_throwability
        # .sqlite Ability-en: 37 Immobilize, 119 Intimidate "reduces the bravery of a distant
        # unit", 201 Charm, 234 Blind, 243 Confuse, 246 Sleep). Five cards printed NO mechanics
        # line while their rows cast these, the same silent-proc disease LW-320 cured elsewhere.
        37: "Immobilize",
        119: "Intimidate (lowering the target's Bravery)",
        201: "Charm",
        234: "Blind",
        243: "Confuse",
        246: "Sleep"}  # Formula-2 NON-elemental ability casts by opt id (elemental casts handled separately)

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

# Tiered elemental casts by ability id (decimal), so a card names the tier the row really
# casts: vanilla weapons carry Fire 16 / Thunder 20 / Blizzard 24, the Flail of Flame Fira 17.
TIERED_CAST = {16: "Fire", 17: "Fira", 18: "Firaga", 19: "Firaja",
               20: "Thunder", 21: "Thundara", 22: "Thundaga", 23: "Thundaja",
               24: "Blizzard", 25: "Blizzara", 26: "Blizzaga", 27: "Blizzaja"}
PROC_FLAVOR = {
    9: "Its glare leaves foes groping blind.", 10: "It smothers an enemy's voice mid-spell.",
    11: "A mark of doom rides every blow.", 12: "Its rhythm lulls the wary to sleep.",
    14: "It pins quarry fast where they stand.", 22: "A venom-slick edge that festers wounds.",
    55: "An arcane edge that unravels enchantments.",
    101: "It slicks the target in clinging oil.",
}


#: LW-352: the equip card's "Special Effect" badge (item.en.nxd UiStatusEffectId) is derived from
#: the weapon's damage formula, never inherited from the vanilla row: 1001 "Absorbs HP" only for
#: the two drain formulas, 1002 (the healing-staff badge) only for formula 7, 0 for everything
#: else. The Duskstring Harp (id 93) shipped the Bloodstring Harp's vanilla 1001 badge while its
#: row was a Blind cast (owner sighting 2026-08-27); this rule is what the bake writes and what
#: analyze.py's claim gate pins.
BADGE_ABSORB_HP = 1001
BADGE_HEALING = 1002
ABSORB_HP_FORMULAS = frozenset({6, 48})   # 6 = absorb HP dealt, 48 = Night Sword drain
ABSORB_MP_FORMULAS = frozenset({47})
HEAL_FORMULAS = frozenset({7})


def badge_for(it):
    """The UiStatusEffectId a weapon's card must carry, from its proposed formula (0 = no badge)."""
    if it.get("category") not in WEAPON_CATS:
        return None
    f = (it.get("proposed") or {}).get("formula", 1)
    if f in ABSORB_HP_FORMULAS:
        return BADGE_ABSORB_HP
    if f in HEAL_FORMULAS:
        return BADGE_HEALING
    return 0


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


#: LW-329 lane-hue slots, owner-read palette sitting 2026-08-25 (lw329_palette_map.json).
#: WP cyan 83 -> teal 93 (owner 2026-08-25 late: 83 hard to read on parchment; 93 read
#: "teal/sage (readable)" at the same sitting -- same family, darker, legible).
#: PA light red 30 -> prominent red 90 (owner sweep read 2026-08-25: "Prominent Red
#: (I like this over the one we're using)", banked in lw307_card_colors.json).
#: Owner tour rulings 2026-08-26: katanas take the teal 93 WP wore (steel 94 was too
#: close to MA blue at the icon rim, and 93 is the proven-readable teal); WP moves to
#: pink 82 (the sweep's one new readable family), pairing the new magenta glow.
LANE_COLOR_SLOT = {"Speed": "40", "PA": "90", "MA": "50", "HP": "81", "WP": "82",
                   "WP+Faith": "60", "PA+MA": "95", "PA+MA+Brave": "93"}


#: Owner ruling 2026-08-25 late: stat names spelled out in the game's own vocabulary
#: ("Magick", "Bravery"); HP stays HP (the game itself never spells it out); the pole
#: lane elides the shared word Attack to hold one wrapped line at the card's 33-char
#: wrap. A whole-phrase table, not a per-token join: the elision is not derivable
#: token-wise. The katana lane wraps to two lines by design; the three katana cards
#: paid a line elsewhere in the same ruling (flavor or signature prose).
GROWS_SPELLED = {"Speed": "Speed", "PA": "Physical Attack", "MA": "Magick Attack",
                 "HP": "HP", "WP": "Weapon Power",
                 "WP+Faith": "Weapon Power & Faith",
                 "PA+MA": "Physical & Magick Attack",
                 "PA+MA+Brave": "Physical Attack, Magick Attack & Bravery"}


def grows_phrase(grows):
    """'PA+MA+Brave' -> 'Physical Attack, Magick Attack & Bravery' (spelled-out ruling
    2026-08-25 late, superseding the same-day abbreviation wording)."""
    return GROWS_SPELLED[grows]


def grows_line(it):
    """The exact tagged 'Grows: <phrase>.' line for a living item -- the SINGLE composition
    point for that line's text (LW-332), called by both assemble_desc (the rendered card) and
    gen_living_weapon_meta.py (the baked "growsLine" meta field the runtime treats as a second
    Kills-counter anchor, LivingWeapon/Display/CardScanner.cs): the two can never independently
    drift apart because both read the same string out of here. LW-329's inline color tag wears
    the lane's hue (LANE_COLOR_SLOT); the phrase itself is grows_phrase's spelled-out wording."""
    return ("<color=" + LANE_COLOR_SLOT[it["grows"]] + ">Grows: "
            + grows_phrase(it["grows"]) + ".</color>")


def mechanics(it):
    s = it["proposed"]
    parts = []
    if it["category"] in WEAPON_CATS:
        el = s.get("element", "None")
        f = s.get("formula", 1)
        p = s.get("onHitAbilityId", 0) or 0
        if f == 67:  # CasMaxHP - CasCurHP: damage = the wielder's missing HP, ignores WP
            return "Damage equals the wielder's missing HP; deadliest near death."
        if f == 69:  # TarMaxHP - TarCurHP: damage = the TARGET's missing HP, ignores WP (an execute)
            return "Damage equals the target's missing HP; lethal against the wounded."
        if f == 4 and el not in ("None", None, ""):  # magic gun: attack IS the elemental spell; scales off FAITH (not MA), ignores armor
            spell = {"Lightning": "Thunder", "Fire": "Fire", "Ice": "Blizzard"}.get(el, el)
            parts.append(f"Fires as {spell} at no MP cost, scaling with Faith.")
        elif el not in ("None", None, ""):
            parts.append(f"Deals {el} damage.")
        if f == 99:
            # short form: the ", not Attack" clarifier cost Swiftedge a card line (LW-333)
            parts.append("Damage scales with Speed.")
        if f not in (2, 4):  # Formula 2/4 read the opt id as a spell cast, not a status
            if f == 45 and p in PROC:  # formula 0x2D = 100% status (confirmed in-game): always lands
                parts.append(f"Always inflicts {PROC[p]} on hit.")
            elif p == 55:
                parts.append("May strip the target's buffs on hit.")
            elif p == 95:
                parts.append("May Stop, petrify, or kill on hit.")
            elif p == 41:
                parts.append("May instantly kill on hit.")
            elif p in PROC:
                parts.append(f"May inflict {PROC[p]} on hit.")
        if f in (6, 47, 48):
            parts.append({6: "Absorbs HP dealt.", 47: "Absorbs MP dealt.", 48: "Night Sword: drains HP."}[f])
        if f == 2 and el not in ("None", None, ""):  # vanilla elemental spell-cast on hit
            # The cast is named by the row's ability id when it is one of the tiered elemental
            # spells (decimal ids, Fire 16 .. Firaja 19, Thunder 20 .. 23, Blizzard 24 .. 27,
            # the PSX order); the element only picks the name when the id is not one of those.
            # The restored Flail of Flame (68) carries 17 = Fira, not Fire (LW-351 stage 2).
            spell = TIERED_CAST.get(p) or {"Lightning": "Thunder", "Fire": "Fire", "Ice": "Blizzard"}.get(el, el)
            parts.append(f"May cast {spell} on hit.")
        if f == 2 and p == 147:  # Rush = knockback
            parts.append("May knock the target back a tile.")
        if f == 2 and p in CAST:  # non-elemental ability cast on hit (Sanguine / Ashura / Gravity)
            parts.append(f"May cast {CAST[p]} on hit.")
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


#: Width (chars) of the equip-card meter-body slot painted after "Kills: ": the widest
#: production tier-progress body under the shipped kill-tier thresholds {5,10,15} (2026-08-11
#: retune) is 11 chars ("14/15 to +3" / "10/15 to +3", every tier-2 body from 10 through 14, all
#: exactly 11). Mirrors LivingWeapon's Signatures.KillsMeterSlotChars byte-for-byte; analyze.py's
#: lockstep check pins the two together so a C#-side width change can't silently drift out of
#: sync with the baked nxd.
KILLS_SLOT_BODY_CHARS = 11

#: The unpainted equip-card Kills line, baked as the FIRST line of every living weapon's
#: description: "Kills: " + a NEUTRAL dashed body ("-/- to +", 8 chars, LW-311) padded to
#: KILLS_SLOT_BODY_CHARS (11) with 3 trailing spaces = 18 chars total. Dashes, not "0/5",
#: because the baked text is also what shows FOREVER when the runtime is absent or stood down,
#: and for the first seconds after a cold boot before the paint lands: a literal zero there is
#: a lie about a player's real count, while a dash is honest in every state. (A "loading"
#: notice was considered and rejected: it lies harder when the runtime is absent, and angle
#: brackets are live markup in this engine's text renderer.) The DLL repaints this slot in
#: place (CardSites.PaintSiteWithResult) with the real tier-progress meter, VERBATIM the same
#: format AttackCardTail.ComposeHead renders on the Attack card; ByteScan.MeterSlotDigits
#: admits the leading dash by name, and KillsSlotWidthContractTests pins this literal against
#: that validator cross-language.
KILLS_SCAFFOLD = "Kills: -/- to +   "


#: LW-166: the game's Poach support ability is only wired into the vanilla damage-formula
#: handlers {1, 2, 3, 4, 6, 7} (owner-proven live 2026-08-12, the LW-166 formula matrix); a
#: weapon riding any OTHER (dormant/repurposed) formula silently cannot be poached through
#: STOCK Poach, no matter how the fight goes. LW-167 armed a runtime cure (Living Poach, see
#: LivingWeapon/Tuning.cs DormantPoachFormulas) that makes these weapons poachable again, so the
#: card-text warning LW-166 baked (removed here) would now be false; the owner chose clean cards
#: + a FAQ entry over a replacement line. This set stays the classification source of truth:
#: analyze.py's check_poach_formula_classified still refuses any weapon formula that is neither
#: in this set nor the vanilla poach-capable set, and check_dormant_poach_formulas_lockstep still
#: pins it to the C# runtime's own poach-arming gate.
DORMANT_FORMULAS = {45, 46, 47, 48, 67, 69, 99}


def card_signature_name(sig):
    """The name the equip card shows for a signature block: curated sigName, falling back to
    displayLabel. THE resolution rule (LW-156), shared by the bake (assemble_desc below) and
    analyze.py's two name gates (check_p3desc, check_p3_signame_grid), so a gate can never pin a
    different name than the card actually renders."""
    return sig.get("sigName") or sig.get("displayLabel", "")


def assemble_desc(it, scaffold=True):
    """The COMPLETE rendered card description, byte-for-byte what patch_names.py bakes into
    item.en.nxd: the Living Weapon Kills-meter scaffold FIRST (owner decision 2026-07-06, moved
    off the last line so the counter reads before the flavor prose), then the colored Grows line
    (LW-332, moved up from the body bottom -- CardScanner now anchors the Kills counter to it
    too, so the wider gap that move opens no longer risks a mispaint), then the flavor line
    (+ generated mechanics), the uniform range sentence, and the "+{atTier} Ability" signature
    block. Extracted here so analyze.py's desc-budget gate and the baker CANNOT drift: the same
    lockstep contract flavor_anchor carries for the (now third) flavor line.
    `scaffold` stays a parameter for callers that need an unscaffolded render; the bake itself
    is unconditional (patch_names' never-False SCAFFOLD_LIVING knob was deleted in LW-155). An
    unscaffolded living render still carries the Grows line, as its own first line (there is no
    Kills line for it to sit under)."""
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
        desc += f" Reaches {rng} tiles."
    # LW-322 (owner ruling 2026-08-25): every living weapon's card says in plain words what it
    # grows. Unconditional on `scaffold` (body text, not scaffold) so an unscaffolded render
    # still carries it. Source is items.json's "grows", already GROWS LOCKSTEP-gated to the grid.
    # LW-329: the WHOLE line wears its lane's hue via an inline color tag the card renderer
    # consumes (LIVE_LEDGER [inline-color-markup-in-ui-text], card surface half); grows_line()
    # is the single composition point (shared with gen_living_weapon_meta.py's baked
    # "growsLine" meta field, which the runtime treats as a second Kills-counter anchor --
    # LivingWeapon/Display/CardScanner.cs). LW-332: the line moved UP from the body bottom to
    # directly under the Kills line (this composed value, `gl`, is placed below; this early
    # rstrip/period-ensure step is UNCHANGED from before that move -- it keeps every other baked
    # byte identical -- even though its result no longer has Grows glued onto it right here).
    gl = grows_line(it) if is_living(it) else None
    if gl is not None:
        desc = desc.rstrip()
        if desc and not desc.endswith((".", "!", "?")):
            desc += "."
        if not scaffold:
            # No Kills scaffold to sit under: the line is simply the body's FIRST line.
            desc = gl + "\n" + desc
    # LW-166 baked a "No Poaching." clause here for dormant-formula weapons; LW-167 armed a
    # runtime cure (Living Poach) that makes them poachable again, so the clause would now be
    # false. Removed (owner chose clean cards + a FAQ entry over a replacement line) -- see
    # DORMANT_FORMULAS above for why the set itself stays.
    if scaffold and is_living(it):
        # p3Desc stays grouped with the gameplay prose (BELOW the flavor line, same relative
        # order as before the Kills-scaffold move). Header names the ability via sigName
        # (curated flavor name) falling back to displayLabel.
        sig = it.get("signature")
        p3 = sig.get("p3Desc") if sig else None
        if p3:
            sname = card_signature_name(sig)
            at = sig.get("atTier", 3)
            header = f"{sname} (+{at})" if sname else f"(+{at})"
            desc = desc.rstrip() + f"\n\n{header}\n{p3}"
        # Kills line FIRST, then the Grows line (LW-332: moved here from the body bottom so the
        # owner's growth lane reads right under the counter -- CardScanner now anchors the Kills
        # counter to Grows too, so the wider gap this opens no longer risks a mispaint), then the
        # rest of the body. The DLL paints the tier-progress meter into the
        # KILLS_SLOT_BODY_CHARS-wide body slot in place; the literal prefix MUST stay in
        # lockstep with ByteScan.MeterSlotDigits.
        # Blank line after Kills removed 2026-08-25 (LW-333): the box clips by wrapped
        # LINES and the blank cost every card one of nine; density matches the owner's
        # own layout sketch.
        desc = KILLS_SCAFFOLD + "\n" + gl + "\n" + desc.lstrip("\n")
    return desc


def plural(name):
    low = name.lower()
    if low.endswith(("s", "x", "z", "ch", "sh")):
        return low + "es"
    return low + "s"
