#!/usr/bin/env python
"""
Build-diversity invariant checker for the FFT Living Weapons item rebalance.

Core rule: within a category, NO item may be strictly dominated by another. B dominates A iff B is >= A on
every "more is better" numeric axis it has (weapons: WP/evade/range; shields: physEv/magEv; head gear:
hp/mp), unlocks no later (tier), carries every identity rider A has (element / on-hit status / special
formula / EquipBonus rider), and is strictly better somewhere. Any rider B lacks, or an earlier tier,
immunizes A.

Usage: python tools/analyze.py [data/items.json] [--baseline]
Exit 1 if any 'proposed' item is strictly dominated.
"""
import csv, json, re, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib import flavor as flavor_mod   # module ref (not `from ... import`): the poach-notice gate
                                        # reads DORMANT_FORMULAS/POACH_NOTICE via getattr so a RED
                                        # run fails loudly on the missing NOTICE, never on ImportError
from lib.categories import WEAPON_CATS
from lib.flavor import (assemble_desc, card_signature_name, flavor_anchor,   # the exact rendered card text
                         rider_text, is_living, KILLS_SCAFFOLD, KILLS_SLOT_BODY_CHARS)
                                                   # + the house-voice prose each rider bakes onto its card
from lib.items import load_items, display_name
from lib.paths import ROOT, ITEMS as ITEMS_DEFAULT
from lib.riders import parse_rider   # rider prose -> claimed EquipBonus fields (for the payload gate)

ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else ITEMS_DEFAULT
CHECK_BASELINE = "--baseline" in sys.argv

# "more is better" numeric axes across all item types. default = neutral (range melee=1, rest 0).
# RANGE is a first-class fairness axis: a longer-reach weapon wins the range axis, so it is never dominated by a
# shorter one, and it can only dominate a shorter weapon if it also matches/beats it on WP/evade/riders
# (Range 2 via the Lunging flag strikes 2 tiles).
NUMERIC_AXES = {"wp": 0, "evade": 0, "range": 1, "physEv": 0, "magEv": 0, "hp": 0, "mp": 0}

# Cross-category dominance: items competing for the SAME equip slot can dominate each other, but only if the
# dominator is at least as broadly equippable. Access class per category + the "can dominate" relation (B may
# dominate A only if B's wearer-set superset-or-equals A's). 'universal' is wearable by every job in its slot.
SLOT_GROUP = {"Armor": "body", "Clothing": "body", "Robe": "body",
              "Helmet": "head", "Hat": "head", "HairAdornment": "head",
              "Shoes": "acc", "Armguard": "acc", "Ring": "acc", "Armlet": "acc", "Cloak": "acc", "Perfume": "acc",
              "Knife": "sidearm", "NinjaBlade": "sidearm"}
ACCESS = {"Armor": "armored", "Clothing": "universal", "Robe": "caster",
          "Helmet": "armored", "Hat": "universal", "HairAdornment": "female",
          "Shoes": "universal", "Armguard": "universal", "Ring": "universal", "Armlet": "universal",
          "Cloak": "universal", "Perfume": "female", "Knife": "knife", "NinjaBlade": "ninja"}
ACC_DOM = {"universal": {"universal", "armored", "caster", "female"}, "armored": {"armored"},
           "caster": {"caster"}, "female": {"female"},
           "knife": {"knife", "ninja"}, "ninja": {"ninja"}}  # knives equip on more jobs than ninja blades


def can_dominate_access(b, a):
    return ACCESS.get(a["category"]) in ACC_DOM.get(ACCESS.get(b["category"], ""), set())


def riders(s, normal_formulas):
    """Identity tokens that make an item non-dominated (a dominator must carry all of them)."""
    r = set()
    if s.get("element", "None") not in ("None", "", None):
        r.add("elem:" + s["element"])
    if s.get("onHit", "None") not in ("None", "", None):
        r.add("onhit:" + s["onHit"])
    if s.get("rider", "None") not in ("None", "", None):
        r.add("rider:" + s["rider"])
    if s.get("formula", 1) not in normal_formulas:
        r.add("formula:" + str(s["formula"]))
    return r


def dominates(bi, ai, key, normal_formulas):
    """Does item B strictly dominate item A?"""
    b, a = bi[key], ai[key]
    strict_axis = False
    for axis, default in NUMERIC_AXES.items():
        bv, av = b.get(axis, default), a.get(axis, default)
        if bv < av:
            return False
        if bv > av:
            strict_axis = True
    tb, ta = bi.get("tier", 0), ai.get("tier", 0)
    if tb > ta:
        return False  # B unlocks later -> A owns the availability niche
    ra, rb = riders(a, normal_formulas), riders(b, normal_formulas)
    if not ra.issubset(rb):
        return False  # A has a rider B lacks -> niche
    return strict_axis or (rb > ra) or (tb < ta)


def check(items, key, normal_formulas):
    # Living-weapon tiers grow between battles (companion-driven), so a static
    # snapshot can't fairly judge them: the base is deliberately weak and the
    # upgrade rungs are unobtainable except via growth. Exempt from the gate
    # (both as dominatee and dominator) so they neither trip nor mask it.
    items = [it for it in items if not it.get("livingWeapon")]
    # Rows added by the mod (the Throwing/Bomb consumables, ids 122-127) have no
    # vanilla numbers, so a --baseline pass has nothing to judge them against.
    items = [it for it in items if key in it]
    by_cat = {}
    for it in items:
        by_cat.setdefault(it["category"], []).append(it)
    violations = []
    for group in by_cat.values():
        for a in group:
            doms = [b for b in group if b["id"] != a["id"] and dominates(b, a, key, normal_formulas)]
            if doms:
                violations.append((a, doms))
    return violations


def check_slots(items, normal_formulas):
    """Cross-category dominance within a shared equip slot, access-aware (a restricted item can't be dominated by
    a broader-access one's narrower sibling; a universal item dominates restricted ones it out-stats)."""
    items = [it for it in items if not it.get("livingWeapon")]  # see check(): living weapons are gate-exempt
    groups = {}
    for it in items:
        g = SLOT_GROUP.get(it["category"])
        if g:
            groups.setdefault(g, []).append(it)
    violations = []
    for grp in groups.values():
        for a in grp:
            doms = [b for b in grp if b["id"] != a["id"] and can_dominate_access(b, a)
                    and dominates(b, a, "proposed", normal_formulas)]
            if doms:
                violations.append((a, doms))
    return violations


FLAVOR_MAX = 90   # authored flavor lines must fit the equip card
P3DESC_MAX = 90   # the signature EFFECT line (the card header carries the name + the +N tier)
SIGNAME_MAX = 60  # the name in the "{name} (+{atTier})" card header. May carry a class-restriction
                  # note (e.g. "Shadow Blade (Squire, Gallant Knight, Knight only)"); the full header line
                  # (name + " (+3)") still fits within the ~90-char card width the flavor lines use.

def check_p3desc(items):
    """Each signature with a p3Desc must resolve a name (sigName, else displayLabel) for the card
    header '{name} (+{atTier})', and the p3Desc EFFECT line must fit the card. The card
    layout is: blank line, the header, the bare effect line (no name prefix, no +N suffix)."""
    bad = []
    for it in items:
        sig = it.get("signature")
        if not sig:
            continue
        p3 = sig.get("p3Desc")
        if not p3:
            continue
        name = card_signature_name(sig)   # the card's own resolution rule (lib/flavor.py)
        if not name or len(name) > SIGNAME_MAX or len(p3) > P3DESC_MAX:
            bad.append((it, p3))
    return bad


DESC_MAX = 205  # TOTAL assembled card description budget (chars). RECALIBRATED LIVE 2026-07-07
                # (owner eyewitness, LW-45): the prior 266 was far too loose. A third of the
                # catalog passed it yet still clipped off the bottom of the equip-card box on
                # screen. 205 is the observed boundary: Frostarc (204 assembled) fits, Gloomfang
                # (209) clips. Every over-cap item's flavor line (plus Umbral Rod's +3 prose) was
                # trimmed to fit. Chars are a proxy for the box's wrapped-line count; recalibrate
                # if the card UI ever changes. (History: 2026-07-05 calibrated 259 for the old
                # trailing "Kills:" scaffold, then 266 for the 2026-07-06 leading-scaffold move;
                # both proved too loose live.)


def check_desc_budget(items):
    """The FULL assembled card description (Kills scaffold + flavor + mechanics + range line +
    signature block, assemble_desc, the exact patch_names bake) must fit the equip card's box.
    Overflow pushes the bottom lines off the screen."""
    bad = []
    for it in items:
        if not it.get("name") or it.get("name") == "TBD":
            continue
        n = len(assemble_desc(it))
        if n > DESC_MAX:
            bad.append((it, n))
    return bad


#: The only damage-formula ids the game's Poach support ability is actually wired into
#: (owner-proven live 2026-08-12, LW-166: the formula matrix). Every OTHER formula a weapon
#: ships on must be in lib.flavor.DORMANT_FORMULAS -- classified poach-blind -- or the
#: classification gate below refuses the build.
VANILLA_FORMULAS = {1, 2, 3, 4, 6, 7}


def check_poach_notice(items):
    """LW-166: the game's Poach support silently cannot trigger through a weapon whose damage
    formula sits outside the vanilla handler set (owner-proven live 2026-08-12). Every
    weapon-category item's assembled card must carry lib.flavor.POACH_NOTICE EXACTLY WHEN its
    proposed.formula is in lib.flavor.DORMANT_FORMULAS -- never on a poach-capable weapon (a false
    warning is its own bug), never missing on a poach-blind one (the bug this gate exists for).
    Reads the two lib.flavor constants via getattr rather than a hard import so this check fails
    LOUDLY on the missing notice text itself, not on an ImportError, if flavor.py hasn't grown
    them yet."""
    dormant = getattr(flavor_mod, "DORMANT_FORMULAS", None)
    notice = getattr(flavor_mod, "POACH_NOTICE", None)
    if dormant is None or notice is None:
        return [({"id": 0, "name": "lib/flavor.py"},
                  ["DORMANT_FORMULAS/POACH_NOTICE missing from lib/flavor.py (LW-166)"])]
    bad = []
    for it in items:
        if it.get("category") not in WEAPON_CATS:
            continue
        formula = it.get("proposed", {}).get("formula")
        wants_notice = formula in dormant
        has_notice = notice in assemble_desc(it)
        if wants_notice and not has_notice:
            bad.append((it, [f"formula {formula} is dormant but the card is missing {notice!r}"]))
        elif has_notice and not wants_notice:
            bad.append((it, [f"formula {formula} is poach-capable but the card carries {notice!r} anyway"]))
    return bad


def check_poach_formula_classified(items):
    """LW-166: every weapon's proposed.formula must be classified as either poach-capable
    (VANILLA_FORMULAS, the owner-proven-live set the game's Poach branch actually reads) or
    poach-blind (lib.flavor.DORMANT_FORMULAS). A formula in neither set is a future author who
    added a new damage formula without deciding which bucket it belongs in -- classify it in one
    of the two sets (and if poach-blind, check_poach_notice will demand the card say so)."""
    dormant = getattr(flavor_mod, "DORMANT_FORMULAS", set())
    known = VANILLA_FORMULAS | dormant
    bad = []
    for it in items:
        if it.get("category") not in WEAPON_CATS:
            continue
        formula = it.get("proposed", {}).get("formula")
        if formula not in known:
            bad.append((it, [f"formula {formula!r} is not classified poach-capable "
                              f"(VANILLA_FORMULAS) or poach-blind (lib.flavor.DORMANT_FORMULAS) -- "
                              f"LW-166: classify it before shipping"]))
    return bad


def check_dormant_poach_formulas_lockstep(items):
    """LW-167: pins LivingWeapon/Tuning.cs's DormantPoachFormulas array (the C# runtime's own
    poach-arming gate, LivingPoachPolicy.Decide's keystone) to lib.flavor.DORMANT_FORMULAS (the
    same set check_poach_notice/check_poach_formula_classified already gate the card text on) --
    mirrors check_kills_scaffold_lockstep's cross-language-pin mechanics: regex the C# constant's
    literal int list straight out of Tuning.cs, parse it, and diff against the Python set. A
    future author editing one side without the other is a silent double-fire (a weapon the
    runtime arms poaching on top of vanilla's own working Poach branch) or under-arm (a weapon
    that should be poachable through Living Poach never rolls) bug that no other gate catches --
    this makes the drift a red build instead."""
    tuning_path = ROOT / "LivingWeapon" / "Tuning.cs"
    try:
        text = tuning_path.read_text(encoding="utf-8")
    except OSError as ex:
        return [("<LivingWeapon/Tuning.cs>", f"could not read the file: {ex}")]

    m = re.search(r"DormantPoachFormulas\s*=\s*\{([^}]*)\}", text)
    if not m:
        return [("<LivingWeapon/Tuning.cs>", "DormantPoachFormulas = { ... } not found "
                 "(vacuous-pass guard: this gate never silently passes without reading it)")]
    try:
        csharp_set = {int(tok.strip()) for tok in m.group(1).split(",") if tok.strip()}
    except ValueError as ex:
        return [("<LivingWeapon/Tuning.cs>", f"could not parse DormantPoachFormulas as ints: {ex}")]

    python_set = set(getattr(flavor_mod, "DORMANT_FORMULAS", set()))
    if csharp_set != python_set:
        only_cs = sorted(csharp_set - python_set)
        only_py = sorted(python_set - csharp_set)
        detail = (f"Tuning.cs DormantPoachFormulas={sorted(csharp_set)} != "
                  f"lib/flavor.py DORMANT_FORMULAS={sorted(python_set)}")
        if only_cs:
            detail += f"; only in C#: {only_cs}"
        if only_py:
            detail += f"; only in Python: {only_py}"
        return [("<DormantPoachFormulas>", detail)]
    return []


def check_kills_scaffold_lockstep(items):
    """Pins the Python bake's Kills-scaffold body width to LivingWeapon/Signatures.cs's own
    KillsMeterSlotChars (11, the single source of truth for the width: see that constant's
    derivation comment). KILLS_SCAFFOLD's own body must be exactly KILLS_SLOT_BODY_CHARS chars,
    and every living weapon's baked description must actually lead with
    "KILLS_SCAFFOLD\\n\\n" (Kills line first, blank line after, body order preserved): a
    C#-side width change (or a baker regression that drops the prepend) shows up here instead of
    drifting silently out of sync with the shipped nxd."""
    bad = []
    scaffold_body_len = len(KILLS_SCAFFOLD) - len("Kills: ")
    if scaffold_body_len != KILLS_SLOT_BODY_CHARS:
        bad.append(("<KILLS_SCAFFOLD constant>", f"body is {scaffold_body_len} chars, expected {KILLS_SLOT_BODY_CHARS}"))
    prefix = KILLS_SCAFFOLD + "\n\n"
    for it in items:
        if not it.get("name") or it.get("name") == "TBD":
            continue
        if not is_living(it):
            continue
        d = assemble_desc(it)
        if not d.startswith(prefix):
            bad.append((it, f"does not lead with the Kills scaffold: {d[:40]!r}"))
    return bad


def check_flavor_length(items):
    """Authored flavor lines (flavorOverride) must stay <= FLAVOR_MAX chars. Items that carry a
    verbatim `desc` (restored vanilla originals for unchanged-name items) are exempt -- those are
    kept as the game shipped them."""
    bad = []
    for it in items:
        fo = it.get("flavorOverride")
        if fo and len(fo) > FLAVOR_MAX:
            bad.append((it, len(fo)))
    return bad


def check_unique_flavor(items):
    """Every named item's flavor line must be UNIQUE. The Living Weapon in-card counter anchors a
    weapon's Kills tally to its flavor line (the stable lead of its description); two items sharing
    a line make the counter show the wrong weapon's count. Identical flavor => identical description,
    so this also enforces 'no two items share a description'."""
    seen, violations = {}, []
    for it in items:
        name = it.get("name")
        if not name or name == "TBD":
            continue
        key = (flavor_anchor(it) or "").strip().lower()
        if not key:
            continue
        if key in seen:
            violations.append((it, seen[key]))
        else:
            seen[key] = it
    return violations


# Sentence-lead words and connectives in rider prose that carry no mechanic of their own;
# everything else in a rider sentence is a status/element/stat NAME the desc must mention.
_RIDER_NOISE = {"grants", "wards", "against", "begins", "battle", "with", "damage", "absorbs",
                "halves", "nullifies", "strengthens", "weak", "to", "takes", "extra", "boosts",
                "jp", "earned", "and", "all", "the", "a"}
# Accepted synonyms: vanilla prose conveys some mechanics under different names.
_RIDER_SYNONYMS = {"transparent": ("transparent", "invisib"),
                   "instant death": ("instant death", "ko"),
                   "status": ("status",)}


def check_rider_desc(items):
    """An item with a hand-written (verbatim) desc AND an equip-bonus rider must STATE every rider
    clause. NUMERIC bonuses must use the exact house voice ('Physical Attack +2') -- a bonus without
    its number is the live bug this gate exists for (the Genji set / Cursed Ring family, 2026-06-10).
    Prose clauses (innates, wards, elements) may use natural phrasing, but every status/element NAME
    in the clause must appear somewhere in the desc -- 'certain elemental magicks' does not tell the
    player it means Fire, Lightning, and Ice. flavorOverride items auto-append their mechanics and
    are safe by construction."""
    violations = []
    for it in items:
        rider = it.get("proposed", {}).get("rider")
        desc = it.get("desc")
        if not rider or not desc:
            continue
        prose = rider_text(rider)
        if not prose:
            continue
        low = desc.lower()
        missing = []
        for s in (x.strip() for x in re.split(r"(?<=\.)\s+", prose) if x.strip()):
            m = re.match(r"(Physical Attack|Magick Attack|Speed|Move|Jump) \+(\d+)\.", s)
            if m:
                if not re.search(re.escape(m.group(1)) + r"\s*\+" + m.group(2), desc, re.I):
                    missing.append(s)
                continue
            toks = [t for t in re.findall(r"[A-Za-z']+", s) if t.lower() not in _RIDER_NOISE]
            for tok in toks:
                accepted = _RIDER_SYNONYMS.get(tok.lower(), (tok.lower(),))
                if not any(a in low for a in accepted):
                    missing.append(s)
                    break
        if missing:
            violations.append((it, missing))
    return violations


_RIDER_STATS = ["PABonus", "MABonus", "SpeedBonus", "MoveBonus", "JumpBonus"]
_VANILLA_EB = None


def _eb_stats(eid, new_eb):
    """Numeric (PA/MA/Speed/Move/Jump) bonuses an equipBonusId actually grants. New rows (items.json
    _equipBonus) win over the committed vanilla extract. data/vanilla_equipbonus.json is a TRACKED
    extract of the gitignored working/ref/equipbonus.json decode (same gate-input pattern as
    data/vanilla_shop.json -- a missing file is a loud failure, never a silent pass). Regenerate after a
    vanilla re-decode with:
      python -c "import json,pathlib; r=json.loads(pathlib.Path('working/ref/equipbonus.json').read_text(encoding='utf-8')); F=['PABonus','MABonus','SpeedBonus','MoveBonus','JumpBonus']; pathlib.Path('data/vanilla_equipbonus.json').write_text(json.dumps({k:{f:v.get(f,0) for f in F} for k,v in sorted(r.items(), key=lambda kv:int(kv[0]))}, indent=1)+chr(10), encoding='utf-8')"
    """
    global _VANILLA_EB
    if _VANILLA_EB is None:
        p = ROOT / "data" / "vanilla_equipbonus.json"
        if not p.exists():
            raise SystemExit(f"GATE INPUT MISSING: {p} (vanilla EquipBonus reference; see _eb_stats)")
        _VANILLA_EB = json.loads(p.read_text(encoding="utf-8"))
    src = new_eb.get(str(eid), _VANILLA_EB.get(str(eid), {}))
    return {f: int(src.get(f, 0)) for f in _RIDER_STATS}


def check_rider_payload(items, new_eb):
    """The NUMERIC half of a rider's prose must match the EquipBonus row the item actually emits. The
    live bug this gate exists for: Blazing Staff (id 63) shipped rider 'MA+1' while its equipBonusId
    pointed at the Move+1 row -- the card advertised a stat the item never granted (and hid the one it
    did). Gate the stat bonuses (PA/MA/Speed/Move/Jump) BOTH ways: every stat the rider claims must be
    in the row, and every stat the row grants must be named by the rider. Element/status clauses are
    deliberately summarized ('absorb Ice' on a shield that also halves Fire) and parsed loosely, so they
    are left to check_rider_desc; only the unambiguous numeric stats are enforced here."""
    violations = []
    for it in items:
        p = it.get("proposed", {})
        rider, eid = p.get("rider"), p.get("equipBonusId")
        if not rider or rider in ("None", "-"):
            continue
        claimed = parse_rider(rider) or {}
        # eid None -> a zero row, so a numeric rider with no EquipBonus at all (advertises a stat nothing
        # grants) is caught too, not silently skipped.
        row = _eb_stats(eid, new_eb) if eid is not None else dict.fromkeys(_RIDER_STATS, 0)
        diffs = [f"{f.replace('Bonus', '')}: rider says +{claimed.get(f, 0)}, row grants +{row[f]}"
                 for f in _RIDER_STATS if claimed.get(f, 0) != row[f]]
        if diffs:
            violations.append((it, eid, diffs))
    return violations


def load_grid_rows(collect_dups=None):
    """docs/living_weapon_grid.csv -> {id: row}: THE loader all three grid gates share (LW-156;
    they used to carry three private copies of it). Returns None when the file is missing (each
    gate turns that into its own violation row). A duplicated id is last-write-wins, exactly as
    every private copy behaved; only check_grid_sync opts into duplicate DETECTION by passing
    collect_dups (a list receiving the repeat row before it wins) -- the two p3 gates
    deliberately keep judging the surviving row alone, so this loader must NOT quietly promote
    dup detection to all three."""
    grid_path = ROOT / "docs" / "living_weapon_grid.csv"
    if not grid_path.exists():
        return None
    rows = {}
    for r in csv.DictReader(grid_path.open(encoding="utf-8-sig")):
        try:
            rid = int(r["id"])
        except (KeyError, ValueError):
            continue
        if collect_dups is not None and rid in rows:
            collect_dups.append((rid, r))
        rows[rid] = r
    return rows


def check_grid_sync(items):
    """docs/living_weapon_grid.csv is the DESIGN SOURCE OF TRUTH for the living weapons and must
    never drift from items.json. Mechanically-checkable columns are enforced: every living weapon
    (weapon category, not noGrowth) has exactly one grid row, and the row's name / Prev Name
    (the vanilla weapon it was converted from) / tier / WP / parry% match items.json. Prose
    columns (sigNote, onHit) and 'Verified Live?' are NOT checked --
    the verified flag is flipped by a human only."""
    dups = []
    rows = load_grid_rows(collect_dups=dups)
    if rows is None:
        return [({"id": 0, "name": "living_weapon_grid.csv"}, ["grid file missing"])]
    violations = [({"id": rid, "name": r.get("name")}, ["duplicate grid row"]) for rid, r in dups]
    lw = {it["id"]: it for it in items
          if it.get("category") in WEAPON_CATS and not it.get("noGrowth")}
    for iid in sorted(set(lw) - set(rows)):
        violations.append((lw[iid], ["missing from the grid"]))
    for iid in sorted(set(rows) - set(lw)):
        violations.append(({"id": iid, "name": rows[iid].get("name")},
                           ["grid row has no living weapon in items.json"]))
    for iid in sorted(set(lw) & set(rows)):
        it, r, p, probs = lw[iid], rows[iid], lw[iid]["proposed"], []
        if (r.get("name") or "").strip() != it.get("name"):
            probs.append(f"name: grid {r.get('name')!r} != items {it.get('name')!r}")
        if (r.get("Prev Name") or "").strip() != (it.get("vanillaName") or ""):
            probs.append(f"Prev Name: grid {r.get('Prev Name')!r} != items vanillaName {it.get('vanillaName')!r}")
        want_type = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", it.get("category") or "")
        if (r.get("type") or "").strip() != want_type:
            probs.append(f"type: grid {r.get('type')!r} != items {want_type!r}")
        if (r.get("tier") or "").strip() != str(it.get("tier")):
            probs.append(f"tier: grid {r.get('tier')} != items {it.get('tier')}")
        if (r.get("WP") or "").strip() != str(p.get("wp", "")):
            probs.append(f"WP: grid {r.get('WP')} != items {p.get('wp')}")
        ev = (r.get("parry%") or "").strip().rstrip("%")
        if ev and ev != str(p.get("evade", 0)):
            probs.append(f"evade: grid {ev}% != items {p.get('evade')}%")
        probs += _check_obtain(it, r)
        if probs:
            violations.append((it, probs))
    return violations


def check_p3_grid_lockstep(items):
    """docs/living_weapon_grid.csv's '+3 ability' column must never drift from items.json's
    signature.p3Desc (owner requirement, LW-36 part 3): the CSV is the design source of truth
    (see check_grid_sync), so the effect text a human edits there and the text the card actually
    renders (baked from p3Desc, see lib/flavor.assemble_desc) must stay byte-identical. Every item
    whose signature carries a p3Desc must have a grid row whose '+3 ability' cell matches it
    exactly. A signature with NO p3Desc (id 32, Materia Blade/Ultima, whose curve is conveyed by
    the item's own desc, not a card-header effect line) must have an EMPTY cell, so a stray value
    can't creep in unnoticed."""
    rows = load_grid_rows()
    if rows is None:
        return [({"id": 0, "name": "living_weapon_grid.csv"}, ["grid file missing"])]
    violations = []
    for it in items:
        sig = it.get("signature")
        if not sig:
            continue
        p3, iid = sig.get("p3Desc"), it["id"]
        row = rows.get(iid)
        if row is None:
            if p3:
                violations.append((it, [f"+3 ability: no grid row for id{iid} (items.json has p3Desc)"]))
            continue
        cell = (row.get("+3 ability") or "").strip()
        if p3:
            if cell != p3:
                violations.append((it, [f"+3 ability: grid {cell!r} != items.json p3Desc {p3!r}"]))
        elif cell:
            violations.append((it, [f"+3 ability: grid has {cell!r} but items.json has no p3Desc"]))
    return violations


def check_p3_signame_grid(items):
    """docs/living_weapon_grid.csv's 'P3' column carries the +3 growth line PLUS the signature's
    card NAME (e.g. 'PA +30% + Puppeteer'), and that name must match the ability name the equip
    card actually shows. The card header is '{sigName else displayLabel} (+{atTier})' (see
    lib.flavor.assemble_desc), so for every signature that renders a card block (has a p3Desc) the
    card name -- any '(Knight jobs only)'-style restriction stripped -- must appear in the P3 cell.
    Companion to check_p3_grid_lockstep (which pins the EFFECT line): together they keep both halves
    of the card's signature block in lockstep with the design sheet. This gate exists for the
    stale-name drift it was added to catch: Galewind read 'Infatuation' long after the Puppeteer
    rework, and Yoichi read 'Fan-Splitter' after the signature shipped as Barrage. Signatures with
    no p3Desc render no card block (id 32, Materia Blade/Ultima) and are skipped, same as the card."""
    rows = load_grid_rows()
    if rows is None:
        return [({"id": 0, "name": "living_weapon_grid.csv"}, ["grid file missing"])]
    violations = []
    for it in items:
        sig = it.get("signature")
        if not sig or not sig.get("p3Desc"):
            continue
        name = card_signature_name(sig)   # the card's own resolution rule (lib/flavor.py)
        base = re.sub(r"\s*\([^)]*\)", "", name).strip()  # drop a trailing class-restriction note
        row = rows.get(it["id"])
        if row is None:
            violations.append((it, [f"P3: no grid row for id{it['id']}"]))
            continue
        cell = (row.get("P3") or "").strip()
        if base and base not in cell:
            violations.append((it, [f"P3: card name {base!r} not present in grid P3 {cell!r}"]))
    return violations


# Acquisition vocabulary for the grid's obtain column. Tokens may carry a parenthetical
# detail -- "Poach (Plague Horror)", "Move-Find (Midlight's Deep DELTA)" -- which the
# check strips before validating; the detail is the human-facing where/from-whom and the
# token is the contract. Shop is DERIVED (effective
# ShopAvailability: our shopOverride, else vanilla) and enforced both ways; the rest is
# design knowledge only a human holds, so any non-Shop token just has to be spelled from
# this set ("Poaching" rotting next to "Poach" is how sheets die). TBD marks the pending
# acquisition pass (the shop/poach treatment TODO) and is always accepted.
_OBTAIN_VOCAB = {"Shop", "Steal", "Poach", "Move-Find", "Join", "TBD"}
_SOLD = re.compile(r"Chapter|Start", re.I)
_VANILLA_REF = None


def _effective_sold(it):
    # Vanilla ShopAvailability lives in data/vanilla_shop.json -- a TRACKED extract of the
    # gitignored working/ref/itemdata.json decode, because this is a GATE input: reading the
    # untracked cache made the obtain check silently judge everything "not sold" on a fresh
    # checkout (CI failed with 87 phantom DRIFTs the first time main ran the gate). Missing
    # file = loud failure, never a silent pass. Regenerate after a vanilla re-decode with:
    #   python -c "import json,pathlib; r=json.loads(pathlib.Path('working/ref/itemdata.json').read_text(encoding='utf-8')); pathlib.Path('data/vanilla_shop.json').write_text(json.dumps({k:v.get('shop','') for k,v in sorted(r.items(), key=lambda kv:int(kv[0]))}, indent=1)+chr(10), encoding='utf-8')"
    global _VANILLA_REF
    if _VANILLA_REF is None:
        ref_path = ROOT / "data" / "vanilla_shop.json"
        if not ref_path.exists():
            raise SystemExit(f"GATE INPUT MISSING: {ref_path} (vanilla shop reference; see _effective_sold)")
        _VANILLA_REF = json.loads(ref_path.read_text(encoding="utf-8"))
    eff = it.get("proposed", {}).get("shopOverride") or _VANILLA_REF.get(str(it["id"]), "")
    return bool(_SOLD.search(eff or ""))


def _split_obtain(cell):
    """Token separator is '/' OUTSIDE parentheses only -- details may carry slashes
    ("Poach (Wild Boar, rare 1/8)"). Returns the tokens with details stripped."""
    parts, depth, cur = [], 0, []
    for ch in cell:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        elif ch == "/" and depth == 0:
            parts.append("".join(cur))
            cur = []
            continue
        cur.append(ch)
    parts.append("".join(cur))
    return [t for t in (re.sub(r"\s*\([^)]*\)", "", p).strip() for p in parts) if t]


def _check_obtain(it, row):
    cell = (row.get("obtain") or "").strip()
    if not cell:
        return ["obtain: empty (use TBD while the acquisition pass is pending)"]
    toks = _split_obtain(cell)
    probs = [f"obtain: unknown token {t!r} (vocabulary: {'/'.join(sorted(_OBTAIN_VOCAB))})"
             for t in toks if t not in _OBTAIN_VOCAB]
    sold = _effective_sold(it)
    if sold and "Shop" not in toks:
        probs.append("obtain: item is shop-sold but the cell does not say Shop")
    if not sold and "Shop" in toks:
        probs.append("obtain: cell says Shop but the item is not shop-sold")
    return probs


def fmt(it, key, nf):
    s = it[key]
    axes = " ".join(f"{ax}{s[ax]}" for ax in NUMERIC_AXES if ax in s)
    rid = riders(s, nf)
    extra = (" " + " ".join(sorted(rid))) if rid else ""
    return f"id{it['id']:>3} {display_name(it):18} T{it.get('tier','?')} {axes}{extra}"


def _fmt_dominance(v):
    for a, doms in v:
        print(f"  DOMINATED: id{a['id']} {display_name(a)}, beaten by {', '.join('id'+str(b['id']) for b in doms)}")


def _fmt_slots(v):
    for a, doms in v:
        print(f"  DOMINATED id{a['id']} {display_name(a)} ({a['category']}), beaten by "
              + ", ".join(f"id{b['id']} {b.get('name')}({b['category']})" for b in doms))


def _fmt_unique_flavor(v):
    for a, b in v:
        print(f"  DUPLICATE id{a['id']} {a.get('name')} shares its flavor with id{b['id']} {b.get('name')}:")
        print(f"      {flavor_anchor(a)!r}")


def _fmt_flavor_length(v):
    for a, n in v:
        print(f"  TOO LONG id{a['id']} {a.get('name')} ({n} chars): {a['flavorOverride']!r}")


def _fmt_p3desc(v):
    for a, s in v:
        print(f"  INVALID id{a['id']} {a.get('name')} (len={len(s)}): {s!r}")


def _fmt_desc_budget(v):
    for a, n in v:
        print(f"  OVERFLOW id{a['id']} {a.get('name')} ({n} chars, {n - DESC_MAX} over)")


def _fmt_dormant_poach_lockstep(v):
    # Findings are always a bare (name, detail) string pair -- there is no per-item finding here,
    # the whole check is a single file-vs-file comparison.
    for a, detail in v:
        print(f"  DRIFT {a}: {detail}")


def _fmt_kills_scaffold(v):
    # Findings are either an item dict (a living weapon whose baked desc drifted) or a bare
    # string (the <KILLS_SCAFFOLD constant> self-check, which has no item to name).
    for a, detail in v:
        name = a.get("name") if isinstance(a, dict) else a
        aid = a.get("id") if isinstance(a, dict) else "-"
        print(f"  DRIFT id{aid} {name}: {detail}")


def _fmt_rider_desc(v):
    for a, missing in v:
        print(f"  UNSTATED id{a['id']} {a.get('name')}: desc is missing {missing}")


def _fmt_rider_payload(v):
    for a, eid, diffs in v:
        print(f"  MISMATCH id{a['id']} {a.get('name')} (equipBonusId {eid}): " + "; ".join(diffs))


def _fmt_drift_probs(v):
    for a, probs in v:
        for prob in probs:
            print(f"  DRIFT id{a['id']} {a.get('name')}: {prob}")


def main():
    doc = load_items(ITEMS)
    nf = set(doc["_meta"].get("normalFormulaIds", [1]))
    items = doc["items"]
    print(f"=== {ITEMS.name}: {len(items)} items ===\n")
    for it in sorted(items, key=lambda x: x["id"]):
        print("  " + fmt(it, "proposed", nf))

    # Registry of every gate stanza: (header, check_fn, pass_msg, format_failure, sets_rc).
    # sets_rc lets one entry opt OUT of failing the build (the --baseline dominance pass is
    # informational only; see the dominance loop below) without a special case in the runner.
    registry = []
    dom_runs = [("baseline", "BASELINE"), ("proposed", "PROPOSED")] if CHECK_BASELINE else [("proposed", "PROPOSED")]
    for key, label in dom_runs:
        registry.append(dict(
            header=f"{label} dominance check",
            check_fn=lambda key=key: check(items, key, nf),
            pass_msg="PASS: no item is strictly dominated. Build-diversity invariant holds.",
            format_failure=_fmt_dominance,
            sets_rc=(label == "PROPOSED"),   # --baseline is reference-only, never fails the gate
        ))
    registry += [
        dict(header="SLOT-WIDE dominance (cross-category, same equip slot, access-aware)",
             check_fn=lambda: check_slots(items, nf),
             pass_msg="PASS: no item is dominated within its equip slot.",
             format_failure=_fmt_slots, sets_rc=True),
        dict(header="DESCRIPTION UNIQUENESS (no two items share a flavor line)",
             check_fn=lambda: check_unique_flavor(items),
             pass_msg="PASS: every item's flavor line is unique.",
             format_failure=_fmt_unique_flavor, sets_rc=True),
        dict(header=f"FLAVOR LENGTH (authored flavorOverride <= {FLAVOR_MAX} chars)",
             check_fn=lambda: check_flavor_length(items),
             pass_msg=f"PASS: every authored flavor line is <= {FLAVOR_MAX} chars.",
             format_failure=_fmt_flavor_length, sets_rc=True),
        dict(header=f"P3 DESCRIPTION (signature effect <= {P3DESC_MAX} chars + a card-header name)",
             check_fn=lambda: check_p3desc(items),
             pass_msg="PASS: every p3Desc is valid.",
             format_failure=_fmt_p3desc, sets_rc=True),
        dict(header=f"DESC BUDGET (assembled card text <= {DESC_MAX} chars; overflow clips the box)",
             check_fn=lambda: check_desc_budget(items),
             pass_msg="PASS: every assembled description fits the card.",
             format_failure=_fmt_desc_budget, sets_rc=True),
        dict(header="POACH NOTICE (card carries \"No Poaching.\" exactly on dormant-formula weapons)",
             check_fn=lambda: check_poach_notice(items),
             pass_msg="PASS: every dormant-formula weapon's card carries the notice, and no other weapon does.",
             format_failure=_fmt_drift_probs, sets_rc=True),
        dict(header="POACH FORMULA CLASSIFIED (every weapon formula is poach-capable or poach-blind)",
             check_fn=lambda: check_poach_formula_classified(items),
             pass_msg="PASS: every weapon formula is classified poach-capable or poach-blind.",
             format_failure=_fmt_drift_probs, sets_rc=True),
        dict(header="DORMANT POACH FORMULAS LOCKSTEP (LivingWeapon/Tuning.cs == lib.flavor.DORMANT_FORMULAS)",
             check_fn=lambda: check_dormant_poach_formulas_lockstep(items),
             pass_msg="PASS: Tuning.cs's DormantPoachFormulas matches lib.flavor.DORMANT_FORMULAS exactly.",
             format_failure=_fmt_dormant_poach_lockstep, sets_rc=True),
        dict(header=f"KILLS SCAFFOLD LOCKSTEP (baked meter body == {KILLS_SLOT_BODY_CHARS} chars, Kills line first)",
             check_fn=lambda: check_kills_scaffold_lockstep(items),
             pass_msg=f"PASS: KILLS_SCAFFOLD is {KILLS_SLOT_BODY_CHARS} chars and every living weapon leads with it.",
             format_failure=_fmt_kills_scaffold, sets_rc=True),
        dict(header="RIDER PROSE (verbatim descs state every equip-bonus clause)",
             check_fn=lambda: check_rider_desc(items),
             pass_msg="PASS: every verbatim desc states its rider in the house voice.",
             format_failure=_fmt_rider_desc, sets_rc=True),
        dict(header="RIDER PAYLOAD (numeric rider stat matches the emitted EquipBonus row)",
             check_fn=lambda: check_rider_payload(items, doc.get("_equipBonus", {})),
             pass_msg="PASS: every numeric rider matches its EquipBonus row.",
             format_failure=_fmt_rider_payload, sets_rc=True),
        dict(header="GRID SYNC (docs/living_weapon_grid.csv matches items.json on id/name/tier/WP/parry)",
             check_fn=lambda: check_grid_sync(items),
             pass_msg="PASS: the living weapon grid matches items.json.",
             format_failure=_fmt_drift_probs, sets_rc=True),
        dict(header="P3 ABILITY GRID LOCKSTEP (items.json p3Desc == grid CSV '+3 ability')",
             check_fn=lambda: check_p3_grid_lockstep(items),
             pass_msg="PASS: every grid '+3 ability' cell matches items.json's signature p3Desc.",
             format_failure=_fmt_drift_probs, sets_rc=True),
        dict(header="P3 SIGNATURE NAME (grid CSV 'P3' cell carries the card's signature name)",
             check_fn=lambda: check_p3_signame_grid(items),
             pass_msg="PASS: every grid 'P3' cell names the ability the card shows.",
             format_failure=_fmt_drift_probs, sets_rc=True),
    ]

    rc = 0
    for gate in registry:
        v = gate["check_fn"]()
        print(f"\n--- {gate['header']} ---")
        if not v:
            print(f"  {gate['pass_msg']}")
        else:
            gate["format_failure"](v)
            if gate["sets_rc"]:
                rc = 1

    sys.exit(rc)


if __name__ == "__main__":
    main()
