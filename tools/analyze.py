#!/usr/bin/env python
"""
Build-diversity invariant checker for the FFT item overhaul.

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
from gen_living_weapon_meta import flavor_anchor, WEAPON_CATS   # the exact flavor line each item renders with
from patch_names import rider_text   # the house-voice prose each rider bakes onto its card

ROOT = Path(__file__).resolve().parent.parent
ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else ROOT / "data" / "items.json"
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
SIGNAME_MAX = 24  # the name in the "+{atTier} Ability -- {name}" card header, kept within card width

def check_p3desc(items):
    """Each signature with a p3Desc must resolve a name (sigName, else displayLabel) for the card
    header '+{atTier} Ability -- {name}', and the p3Desc EFFECT line must fit the card. The card
    layout is: blank line, the header, the bare effect line (no name prefix, no +N suffix)."""
    bad = []
    for it in items:
        sig = it.get("signature")
        if not sig:
            continue
        p3 = sig.get("p3Desc")
        if not p3:
            continue
        name = sig.get("sigName") or sig.get("displayLabel", "")
        if not name or len(name) > SIGNAME_MAX or len(p3) > P3DESC_MAX:
            bad.append((it, p3))
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


def check_grid_sync(items):
    """docs/living_weapon_grid.csv is the DESIGN SOURCE OF TRUTH for the living weapons and must
    never drift from items.json. Mechanically-checkable columns are enforced: every living weapon
    (weapon category, not noGrowth) has exactly one grid row, and the row's name / tier / WP /
    parry% match items.json. Prose columns (sigNote, onHit) and 'Verified Live?' are NOT checked --
    the verified flag is flipped by a human only."""
    grid_path = ROOT / "docs" / "living_weapon_grid.csv"
    violations = []
    if not grid_path.exists():
        return [({"id": 0, "name": "living_weapon_grid.csv"}, ["grid file missing"])]
    rows = {}
    for r in csv.DictReader(grid_path.open(encoding="utf-8-sig")):
        try:
            rid = int(r["id"])
        except (KeyError, ValueError):
            continue
        if rid in rows:
            violations.append(({"id": rid, "name": r.get("name")}, ["duplicate grid row"]))
        rows[rid] = r
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
        if probs:
            violations.append((it, probs))
    return violations


def fmt(it, key, nf):
    s = it[key]
    axes = " ".join(f"{ax}{s[ax]}" for ax in NUMERIC_AXES if ax in s)
    rid = riders(s, nf)
    extra = (" " + " ".join(sorted(rid))) if rid else ""
    name = it.get("name") if it.get("name") not in (None, "TBD") else it["vanillaName"]
    return f"id{it['id']:>3} {name:18} T{it.get('tier','?')} {axes}{extra}"


def main():
    doc = json.loads(ITEMS.read_text(encoding="utf-8"))
    nf = set(doc["_meta"].get("normalFormulaIds", [1]))
    items = doc["items"]
    print(f"=== {ITEMS.name}: {len(items)} items ===\n")
    for it in sorted(items, key=lambda x: x["id"]):
        print("  " + fmt(it, "proposed", nf))
    rc = 0
    for key, label in ([("baseline", "BASELINE"), ("proposed", "PROPOSED")] if CHECK_BASELINE else [("proposed", "PROPOSED")]):
        v = check(items, key, nf)
        print(f"\n--- {label} dominance check ---")
        if not v:
            print("  PASS: no item is strictly dominated. Build-diversity invariant holds.")
        else:
            for a, doms in v:
                an = a.get("name") if a.get("name") not in (None, "TBD") else a["vanillaName"]
                print(f"  DOMINATED: id{a['id']} {an}, beaten by {', '.join('id'+str(b['id']) for b in doms)}")
            if label == "PROPOSED":
                rc = 1
    sv = check_slots(items, nf)
    print("\n--- SLOT-WIDE dominance (cross-category, same equip slot, access-aware) ---")
    if not sv:
        print("  PASS: no item is dominated within its equip slot.")
    else:
        for a, doms in sv:
            an = a.get("name") if a.get("name") not in (None, "TBD") else a["vanillaName"]
            print(f"  DOMINATED id{a['id']} {an} ({a['category']}), beaten by "
                  + ", ".join(f"id{b['id']} {b.get('name')}({b['category']})" for b in doms))
        rc = 1

    fv = check_unique_flavor(items)
    print("\n--- DESCRIPTION UNIQUENESS (no two items share a flavor line) ---")
    if not fv:
        print("  PASS: every item's flavor line is unique.")
    else:
        for a, b in fv:
            print(f"  DUPLICATE id{a['id']} {a.get('name')} shares its flavor with id{b['id']} {b.get('name')}:")
            print(f"      {flavor_anchor(a)!r}")
        rc = 1

    fl = check_flavor_length(items)
    print(f"\n--- FLAVOR LENGTH (authored flavorOverride <= {FLAVOR_MAX} chars) ---")
    if not fl:
        print(f"  PASS: every authored flavor line is <= {FLAVOR_MAX} chars.")
    else:
        for a, n in fl:
            print(f"  TOO LONG id{a['id']} {a.get('name')} ({n} chars): {a['flavorOverride']!r}")
        rc = 1

    p3 = check_p3desc(items)
    print(f"\n--- P3 DESCRIPTION (signature effect <= {P3DESC_MAX} chars + a card-header name) ---")
    if not p3:
        print(f"  PASS: every p3Desc is valid.")
    else:
        for a, s in p3:
            print(f"  INVALID id{a['id']} {a.get('name')} (len={len(s)}): {s!r}")
        rc = 1

    rd = check_rider_desc(items)
    print("\n--- RIDER PROSE (verbatim descs state every equip-bonus clause) ---")
    if not rd:
        print("  PASS: every verbatim desc states its rider in the house voice.")
    else:
        for a, missing in rd:
            print(f"  UNSTATED id{a['id']} {a.get('name')}: desc is missing {missing}")
        rc = 1

    gs = check_grid_sync(items)
    print("\n--- GRID SYNC (docs/living_weapon_grid.csv matches items.json on id/name/tier/WP/parry) ---")
    if not gs:
        print("  PASS: the living weapon grid matches items.json.")
    else:
        for a, probs in gs:
            for prob in probs:
                print(f"  DRIFT id{a['id']} {a.get('name')}: {prob}")
        rc = 1

    sys.exit(rc)


if __name__ == "__main__":
    main()
