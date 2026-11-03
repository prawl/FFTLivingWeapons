#!/usr/bin/env python
"""Dodge report: every generic job's chance to be hit, vanilla against the mod.

A model of the game's PSX era evade and random gear rules, not a measurement. Each evade source
rolls in turn, so a hit chance is a product of survivals, never a sum. Enemy gear follows the
game's random equipment routine (highest level wins, a new highest level resets the list). Pure
functions over the committed snapshots, so it runs anywhere with no game file.

Usage. python tools/analyze_jobs.py [--levels 5,20,35] [--json] [--selftest]
Exit 1 when a gating check fails. The report itself never fails the build.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib import jobrules
from lib.items import load_items
from lib.paths import ROOT, MOD_TABLES, TABLE_DATA, VANILLA_TABLES

WEAPON_ROWS = ROOT / "data" / "vanilla_weapon_rows.json"
SLOTS = {"weapon": (1, 127), "shield": (128, 143), "accessory": (208, 239)}
DUAL_WIELD = 477
TWO_SWORDS = jobrules.ATTACK_FLAGS["TwoSwords"]
FORCED_TWO_HANDS = jobrules.ATTACK_FLAGS["ForcedTwoHands"]
GENERIC_IDS = range(74, 94)
FACINGS = ("front", "side", "back")
COLUMNS = ("front", "side", "back", "front_parry", "side_parry", "back_parry", "magic")
HEADER = "dodge report: a model of the game's PSX era evade and random gear rules, unverified live"
OUT_OF_MODEL = ("out of the model: story battle fixed loadouts, statuses, Abandon, Blade Grasp, "
                "weather, Charge, support abilities (Doublehand empties the off hand, Equip X widens "
                "the pick), the Dual Wield fallback to an unrestricted pick when no TwoSwords weapon "
                "qualifies")


def load_snapshot():
    return json.loads(VANILLA_TABLES.read_text(encoding="utf-8"))


def load_vanilla_weapons():
    """Vanilla weapon evade and attack flags by item id, from bytes 5 and 1 of each row."""
    rows = json.loads(WEAPON_ROWS.read_text(encoding="utf-8"))["rows"]
    out = {}
    for iid, hexrow in rows.items():
        b = bytes.fromhex(hexrow.replace(" ", ""))
        out[int(iid)] = {"evade": b[5], "flags": b[1]}
    return out


def mod_jobs(snapshot, mod_xml):
    """Equip list and class evade per job id, the mod rows laid over the vanilla snapshot."""
    jobs = {int(k): {"equip": list(v["equip"]), "cev": v["cev"]} for k, v in snapshot["jobs"].items()}
    for row in ET.fromstring(mod_xml).find("Entries").findall("Job"):
        jid = int(row.findtext("Id"))
        if jid not in jobs:
            continue
        equip = row.findtext("EquippableItems")
        if equip is not None:
            jobs[jid]["equip"] = [c.strip() for c in equip.split(",") if c.strip()]
        cev = row.findtext("CharacterEvasion")
        if cev is not None:
            jobs[jid]["cev"] = int(cev.strip())
    return jobs


def random_pick(snapshot, equip, slot, level, required_flag=0, flags=None):
    """Item ids the game's random equipment routine can land on for one slot."""
    lo, hi = SLOTS[slot]
    picked, highest = [], -1
    for iid in range(lo, hi + 1):
        it = snapshot["items"].get(str(iid))
        if it is None or it["rare"] or it["requiredLevel"] > level or it["category"] not in equip:
            continue
        if required_flag and (flags or {}).get(iid, 0) & required_flag != required_flag:
            continue
        if it["requiredLevel"] > highest:
            picked, highest = [iid], it["requiredLevel"]
        else:
            picked.append(iid)
    return picked


def slot_evade(ids, evade):
    """Mean evade over the picked ids, 0 for an empty list."""
    return sum(evade.get(i, 0) for i in ids) / len(ids) if ids else 0


def shield_share(right_ids, flags):
    """Share of right hand picks that leave the off hand free. An empty list leaves it free."""
    if not right_ids:
        return 1.0
    return sum(1 for i in right_ids if not flags.get(i, 0) & FORCED_TWO_HANDS) / len(right_ids)


def hit_chance(a, rh, lh, c, facing):
    """Physical hit chance in percent. Side drops class evade, back drops class and both hands."""
    if facing != "front":
        c = 0
    if facing == "back":
        rh = lh = 0
    return round(100 * (1 - a / 100) * (1 - rh / 100) * (1 - lh / 100) * (1 - c / 100), 1)


def magic_hit(s, a):
    return round(100 * (1 - s / 100) * (1 - a / 100), 1)


def build_rules(snapshot, jobs, weapons, items_doc=None):
    """One side's lookups. Pass the items.json doc for the mod side, None for vanilla."""
    wev = {i: w["evade"] for i, w in weapons.items()}
    flags = {i: w["flags"] for i, w in weapons.items()}
    shield, acc = {}, {}
    for iid, it in snapshot["items"].items():
        n = int(iid)
        if SLOTS["shield"][0] <= n <= SLOTS["shield"][1]:
            row = snapshot["shields"].get(str(it["additionalDataId"]))
            if row:
                shield[n] = (row["physEv"], row["magEv"])
        elif SLOTS["accessory"][0] <= n <= SLOTS["accessory"][1]:
            row = snapshot["accessories"].get(str(it["additionalDataId"]))
            if row:
                acc[n] = (row["physEv"], row["magEv"])
    for it in (items_doc or {}).get("items", []):
        p, n = it.get("proposed", {}), it["id"]
        if SLOTS["shield"][0] <= n <= SLOTS["shield"][1] and "physEv" in p:
            shield[n] = (p["physEv"], p["magEv"])
        elif SLOTS["accessory"][0] <= n <= SLOTS["accessory"][1] and "physEv" in p:
            acc[n] = (p["physEv"], p["magEv"])
        elif SLOTS["weapon"][0] <= n <= SLOTS["weapon"][1]:
            if "evade" in p:
                wev[n] = p["evade"]
            if p.get("attackFlags"):
                flags[n] = jobrules.flag_byte(p["attackFlags"])
    return {"jobs": jobs, "wev": wev, "flags": flags, "shield": shield, "acc": acc}


def profile(rules, snapshot, jid, level):
    """Expected evade numbers and the item ids behind them for one job at one unit level."""
    job = rules["jobs"][jid]
    equip, flags = job["equip"], rules["flags"]
    dual = DUAL_WIELD in snapshot["jobs"][str(jid)]["innate"]
    need = TWO_SWORDS if dual else 0
    right = random_pick(snapshot, equip, "weapon", level, need, flags)
    if dual:
        left = random_pick(snapshot, equip, "weapon", level, need, flags)
        shield, share = [], 0.0
    else:
        left = []
        shield = random_pick(snapshot, equip, "shield", level)
        share = shield_share(right, flags)
    acc = random_pick(snapshot, equip, "accessory", level)
    phys = lambda t: {i: v[0] for i, v in t.items()}
    magic = lambda t: {i: v[1] for i, v in t.items()}
    return {
        "a": slot_evade(acc, phys(rules["acc"])), "amag": slot_evade(acc, magic(rules["acc"])),
        "c": job["cev"], "rh": slot_evade(right, rules["wev"]),
        "lh_shield": slot_evade(shield, phys(rules["shield"])) * share,
        "lh_weapon": slot_evade(left, rules["wev"]),
        "smag": slot_evade(shield, magic(rules["shield"])) * share, "share": round(share, 2),
        "ids": {"weapon": right, "offhand": left or shield, "accessory": acc},
    }


def chances(p):
    """Hit chances per facing without and with Parry, plus magic."""
    out = {}
    for facing in FACINGS:
        out[facing] = hit_chance(p["a"], 0, p["lh_shield"], p["c"], facing)
        out[facing + "_parry"] = hit_chance(p["a"], p["rh"], p["lh_shield"] + p["lh_weapon"], p["c"], facing)
    out["magic"] = magic_hit(p["smag"], p["amag"])
    return out


def band_rows(snapshot, sides, level):
    rows = []
    for jid in GENERIC_IDS:
        row = {"id": jid, "job": snapshot["jobs"][str(jid)]["name"]}
        for side, rules in sides.items():
            p = profile(rules, snapshot, jid, level)
            row[side] = chances(p)
            row[side]["share"] = p["share"]
            row[side]["ids"] = p["ids"]
        rows.append(row)
    return rows


def summary(rows):
    return {side: round(sum(r[side]["front"] for r in rows) / len(rows), 1) for side in ("vanilla", "mod")}


def format_band(level, rows):
    short = {"front": "fr", "side": "si", "back": "bk", "front_parry": "Pfr", "side_parry": "Psi",
             "back_parry": "Pbk", "magic": "mag"}
    head = "".join(f"{s + '.' + short[c]:<7}" for s in ("V", "M") for c in COLUMNS)
    lines = [f"--- unit level {level} (V vanilla, M mod, P with Parry) ---", f"{'job':<14}{head}shield share (mod)"]
    for r in rows:
        nums = "".join(f"{r[s][c]:<7}" for s in ("vanilla", "mod") for c in COLUMNS)
        lines.append(f"{r['job']:<14}{nums}{r['mod']['share']}")
    for r in rows:
        for s in ("vanilla", "mod"):
            i = r[s]["ids"]
            lines.append(f"  {r['job']} {s}: weapon {i['weapon']} offhand {i['offhand']} "
                         f"accessory {i['accessory']}")
    sm = summary(rows)
    lines.append(f"band {level}: mean front hit chance over {len(rows)} jobs, no Parry, "
                 f"vanilla {sm['vanilla']} mod {sm['mod']}")
    return "\n".join(lines)


def check_shield_baselines(items_doc, snapshot):
    """Problems for every shield whose items.json baseline differs from the snapshot."""
    bad = []
    for it in items_doc["items"]:
        if it.get("category") != "Shield":
            continue
        snap = snapshot["items"].get(str(it["id"]))
        row = snapshot["shields"].get(str(snap["additionalDataId"])) if snap else None
        base = it.get("baseline", {})
        if row and (base.get("physEv") != row["physEv"] or base.get("magEv") != row["magEv"]):
            bad.append(f"id {it['id']} {it.get('vanillaName')} baseline {base.get('physEv')}/{base.get('magEv')} "
                       f"but the game ships {row['physEv']}/{row['magEv']}")
    return bad


def check_snapshot_hashes(snapshot):
    """None when the game folder is absent, else the source files whose hash changed."""
    if not TABLE_DATA.exists():
        return None
    from snapshot_vanilla_tables import source_hashes
    have = source_hashes(TABLE_DATA)
    return [n for n, h in snapshot["_meta"]["sha256"].items() if have.get(n) != h]


def build_sides(snapshot):
    weapons = load_vanilla_weapons()
    vanilla = {int(k): {"equip": v["equip"], "cev": v["cev"]} for k, v in snapshot["jobs"].items()}
    mod = mod_jobs(snapshot, (MOD_TABLES / "JobData.xml").read_text(encoding="utf-8"))
    return {"vanilla": build_rules(snapshot, vanilla, weapons),
            "mod": build_rules(snapshot, mod, weapons, load_items())}


def run_gates(snapshot, emit):
    """Print each gating check and return 1 when any failed."""
    rc = 0
    emit("\n--- VANILLA TABLES SNAPSHOT ---")
    changed = check_snapshot_hashes(snapshot)
    if changed is None:
        emit("  vanilla tables: source not on this box, hash check skipped")
    elif changed:
        rc = 1
        emit("  FAIL: vanilla tables changed under the snapshot, rerun python tools\\snapshot_vanilla_tables.py "
             f"({', '.join(changed)})")
    else:
        emit("  PASS: the snapshot matches the game's table dump.")
    emit("\n--- SHIELD BASELINES ---")
    problems = check_shield_baselines(load_items(), snapshot)
    for p in problems:
        emit(f"  FAIL: {p}")
    rc = 1 if problems else rc
    if not problems:
        emit("  PASS: every shield baseline equals the snapshot.")
    emit("\n--- JOB RULE REASONS ---")
    bad = jobrules.missing_reasons(json.loads(jobrules.JOBS.read_text(encoding="utf-8")))
    for b in bad:
        emit(f"  FAIL: {b} has no reason")
    rc = 1 if bad else rc
    if not bad:
        emit("  PASS: every job departure carries a reason.")
    return rc


def main(argv=None):
    ap = argparse.ArgumentParser(description="Dodge report and its gating checks.")
    ap.add_argument("--levels", default="5,20,35")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    levels = [int(x) for x in args.levels.split(",")]
    snapshot = load_snapshot()
    bands = {lv: band_rows(snapshot, build_sides(snapshot), lv) for lv in levels}
    if args.json:
        doc = {"levels": levels, "bands": {str(lv): {"jobs": rows, "summary": summary(rows)}
                                           for lv, rows in bands.items()}}
        print(json.dumps(doc, indent=1))
        return run_gates(snapshot, lambda s: print(s.strip(), file=sys.stderr) if "FAIL" in s else None)
    print(HEADER)
    print(OUT_OF_MODEL)
    for lv in levels:
        print()
        print(format_band(lv, bands[lv]))
    return run_gates(snapshot, print)


def _fixture(rows, first=1):
    items = {str(first + i): {"category": c, "requiredLevel": lv, "rare": r, "additionalDataId": 0}
             for i, (c, lv, r) in enumerate(rows)}
    return {"items": items}


def _temp(prefix, suffix):
    return Path(tempfile.gettempdir()) / f"{prefix}{os.getpid()}{suffix}"


def _json_matches_table(level):
    """Run the script plain and with --json for one band and compare every number."""
    base = [sys.executable, str(Path(__file__).resolve()), "--levels", str(level)]
    plain = subprocess.run(base, capture_output=True, text=True).stdout
    doc = json.loads(subprocess.run(base + ["--json"], capture_output=True, text=True).stdout)
    band = doc["bands"][str(level)]
    block = plain.split(f"--- unit level {level}")[1].split("\n--- ")[0]
    row_re = re.compile(r"^(.+?)\s+((?:\d+\.\d\s+){14})(\S+)$")
    table = {}
    for line in block.splitlines():
        m = row_re.match(line)
        if m:
            table[m.group(1)] = [float(x) for x in m.group(2).split()] + [float(m.group(3))]
    if len(table) != len(band["jobs"]):
        return False
    for r in band["jobs"]:
        want = [r[s][c] for s in ("vanilla", "mod") for c in COLUMNS] + [r["mod"]["share"]]
        if table.get(r["job"]) != want:
            return False
    tail = re.search(r"vanilla ([\d.]+) mod ([\d.]+)$", block.strip(), re.M)
    return bool(tail) and [float(tail.group(1)), float(tail.group(2))] == [
        band["summary"]["vanilla"], band["summary"]["mod"]]


def selftest():
    """Regression cases for the report maths and the gates. Prints the pass count, returns an exit code."""
    cases = []

    def check(name, cond):
        cases.append((name, bool(cond)))

    sw = ["Sword"]
    check("T1 front is the product, not the sum", hit_chance(0, 0, 24, 25, "front") == 57.0)
    check("T1 side drops class evade", hit_chance(0, 0, 24, 25, "side") == 76.0)
    check("T1 back leaves only the accessory", hit_chance(0, 0, 24, 25, "back") == 100.0)
    check("T2 weapon evade multiplies in", hit_chance(0, 10, 24, 25, "front") == 51.3)
    check("T3 reset then append", random_pick(
        _fixture([("Sword", 1, False), ("Sword", 3, False), ("Sword", 6, False), ("Sword", 2, False)]),
        sw, "weapon", 5) == [2, 4])
    check("T3b equal level appends",
          random_pick(_fixture([("Sword", 3, False), ("Sword", 3, False)]), sw, "weapon", 5) == [1, 2])
    check("T3c a rare item never resets",
          random_pick(_fixture([("Sword", 1, False), ("Sword", 4, True)]), sw, "weapon", 5) == [1])
    check("T3d an unequippable item never resets",
          random_pick(_fixture([("Sword", 1, False), ("Bow", 4, False)]), sw, "weapon", 5) == [1])
    check("T3e empty list reads 0", slot_evade([], {1: 9}) == 0)
    check("T4 rare is excluded", random_pick(_fixture([("Sword", 1, True)]), sw, "weapon", 5) == [])
    check("T5b dual wield drops a weapon without TwoSwords",
          random_pick(_fixture([("Knife", 1, False), ("Knife", 1, False)]), ["Knife"], "weapon", 5,
                      TWO_SWORDS, {1: TWO_SWORDS, 2: 0}) == [1])
    check("T5c all forced gives share 0", shield_share([1, 2], {1: 1, 2: 1}) == 0.0)
    check("T5c half forced gives half", shield_share([1, 2], {1: 1, 2: 0}) == 0.5)
    check("T6 no Shield category gives an empty shield list",
          random_pick(_fixture([("Shield", 1, False)], 128), sw, "shield", 5) == [])
    check("T7 magic ignores facing", magic_hit(3, 10) == 87.3)

    full = load_snapshot()
    wp = load_vanilla_weapons()
    vjobs = {int(k): {"equip": v["equip"], "cev": v["cev"]} for k, v in full["jobs"].items()}
    vr = build_rules(full, vjobs, wp)
    committed_xml = (MOD_TABLES / "JobData.xml").read_text(encoding="utf-8")
    mod_ninja_jobs = mod_jobs(full, committed_xml)
    mr = build_rules(full, mod_ninja_jobs, wp, load_items())
    ninja = profile(mr, full, 89, 35)
    check("T5 a dual wield job with Shield in its equip list still wears none and holds a second weapon",
          "Shield" in mod_ninja_jobs[89]["equip"] and DUAL_WIELD in full["jobs"]["89"]["innate"]
          and ninja["lh_shield"] == 0 and ninja["ids"]["offhand"]
          and all(i < 128 for i in ninja["ids"]["offhand"]))
    xml = ("<JobTable><Entries><Job><Id>1</Id><EquippableItems>Sword</EquippableItems></Job>"
           "<Job><Id>2</Id><CharacterEvasion>9</CharacterEvasion></Job></Entries></JobTable>")
    mj = mod_jobs(full, xml)
    check("T6b overlay keeps vanilla where the mod row is silent",
          mj[1] == {"equip": ["Sword"], "cev": full["jobs"]["1"]["cev"]}
          and mj[2] == {"equip": full["jobs"]["2"]["equip"], "cev": 9}
          and mj[3] == vjobs[3])
    check("T6c flag map decodes the vanilla Bow",
          set(jobrules.flag_names(wp[83]["flags"])) == {"Arc", "TwoHands", "ForcedTwoHands"})
    check("T7b json and table numbers agree value for value", _json_matches_table(20))

    bad_doc = {"items": [{"id": 129, "category": "Shield", "vanillaName": "Buckler",
                          "baseline": {"physEv": 99, "magEv": 0}}]}
    problems = check_shield_baselines(bad_doc, full)
    check("T8 a wrong shield baseline is named", len(problems) == 1 and "id 129" in problems[0])

    tmp = _temp("lw_jobs_", ".json")
    try:
        doc = json.loads(jobrules.JOBS.read_text(encoding="utf-8"))
        doc["generic"]["Thief"]["add"]["Bow"] = ""
        tmp.write_text(json.dumps(doc), encoding="utf-8")
        try:
            jobrules.load(tmp)
            refused = False
        except SystemExit as e:
            refused = "Thief add Bow" in str(e.code)
        check("T9 an empty reason is refused and named", refused)
    finally:
        tmp.unlink(missing_ok=True)

    copy = _temp("lw_jobxml_", ".xml")
    try:
        committed = (MOD_TABLES / "JobData.xml").read_bytes()
        cmd = [sys.executable, str(ROOT / "tools" / "make_jobequip.py"), "--verify", "--out", str(copy)]
        copy.write_bytes(committed)
        first = subprocess.run(cmd, capture_output=True).returncode
        copy.write_bytes(committed.replace(b"<Id>", b"<Ix>", 1))
        second = subprocess.run(cmd, capture_output=True).returncode
        check("T10 verify passes a clean copy and fails a changed one", first == 0 and second == 1)
    finally:
        copy.unlink(missing_ok=True)

    failed = [n for n, ok in cases if not ok]
    for n in failed:
        print(f"  FAIL: {n}")
    print(f"analyze_jobs selftest: {len(cases) - len(failed)}/{len(cases)} passed.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
