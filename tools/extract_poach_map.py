"""Extract the monster poach map from the game's Job sheet (LW-167 Living Poach).

Reads nxd/job.en.nxd AND nxd/poachitem.en.nxd out of the English data pac, decodes both with
FF16Tools, and writes LivingWeapon/poach.json: one entry per MONSTER JOB ID carrying the
carcass-store Keys and display names the runtime needs.

Ground truth (owner probe session 2026-08-12, LIVE_LEDGER row dated 2026-08-12): a live pass
falsified the old "species = victim job byte - 95" mapping -- a Black Chocobo read job byte 95
and was refused. Decoding the Job sheet directly (its generic layout labels the two columns
Unknown10/Unknown11) shows every Job row carries its own PoachItem common/rare Keys straight up:
monster jobs from Key 94 (Chocobo, keys 1/2) carry them nonzero, human jobs carry 0/0. So
MAP-MEMBERSHIP BY JOB ID is the entire monster gate now -- no arithmetic, no range check.

Hand-verified calibration (checked by eye against the PoachItem sheet, re-checked here every
run -- a mismatch means the sheet layout moved and this script must stop, not guess):
  Chocobo (94) -> 1/2, Black Chocobo (95) -> 3/4, Goblin (97) -> 7/8, Bomb (100) -> 25/26,
  Skeleton (109) -> 31/32, Ghoul (112) -> 13/14.

The Job sheet also repeats a handful of species' key pairs on higher, non-contiguous ALIAS job ids
(169-173 duplicate 103/97/98/100/94's exact pairs: Red Panther, Goblin, Black Goblin, Bomb,
Chocobo). Those alias rows are EMITTED as first-class map entries sharing their base job's pair.
They used to be folded away, which silently disabled Living Poach wherever the game fields them --
and it fields them in ORDINARY STORY BATTLES, not some NG+ corner: an offline ENTD decode
(2026-08-12) shows battle 384 (Sweegy Woods, chapter 1) fielding MainJob 169-172, with battles 389
and 400 fielding aliases too. Map membership by job id is the runtime's entire monster gate, so an
unmapped alias is a silent refusal (the LW-174 finding, from the ac43327 adversarial verify round).
Two job ids sharing one IDENTICAL pair is harmless: the store write is keyed by carcass key alone.
Carcass keys therefore stay unique across DISTINCT pairs (each of the store's 96 bytes has exactly
one owning pair) -- see the dedupe loop below. A genuine PARTIAL collision (a key reused across two
DIFFERENT pairs) is a real corruption signal and still fails loudly.

Rerun after a game patch (the pac path comes from tools/lib/paths.py) and commit the diff; the
json is the runtime's single source for the map.

Usage: python tools/extract_poach_map.py
"""
import json
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.nxd import PAC, unpack, decode_nxd_to_sqlite
from lib.paths import ROOT

WORK_JOB = ROOT / "working" / "nxd_job"
WORK_POACH = ROOT / "working" / "nxd_poachitem"
OUT = ROOT / "LivingWeapon" / "poach.json"

# (jobId, expectedCommonKey, expectedRareKey) -- hand-verified against the PoachItem sheet by the
# owner's 2026-08-12 probe. The Black Chocobo pair is the exact case that falsified the old
# species-arithmetic mapping live.
CALIBRATION = [
    (94, 1, 2),      # Chocobo
    (95, 3, 4),       # Black Chocobo -- the falsifying case
    (97, 7, 8),       # Goblin
    (100, 25, 26),    # Bomb
    (109, 31, 32),    # Skeleton
    (112, 13, 14),    # Ghoul
]


def main():
    WORK_JOB.mkdir(parents=True, exist_ok=True)
    WORK_POACH.mkdir(parents=True, exist_ok=True)

    job_nxd = unpack(PAC, "nxd/job.en.nxd", WORK_JOB)
    job_db = decode_nxd_to_sqlite([job_nxd], WORK_JOB, "job.sqlite")
    poach_nxd = unpack(PAC, "nxd/poachitem.en.nxd", WORK_POACH)
    poach_db = decode_nxd_to_sqlite([poach_nxd], WORK_POACH, "poachitem.sqlite")

    jcon = sqlite3.connect(str(job_db))
    job_rows = jcon.execute(
        "SELECT Key, Unknown10, Unknown11 FROM [Job-en] ORDER BY Key"
    ).fetchall()

    # Calibration self-test FIRST: if the sheet layout moved, stop here rather than build a
    # silently-wrong map.
    by_job = {int(k): (int(c), int(r)) for k, c, r in job_rows}
    for job_id, exp_common, exp_rare in CALIBRATION:
        got = by_job.get(job_id)
        if got != (exp_common, exp_rare):
            raise SystemExit(
                f"CALIBRATION FAILED: job {job_id} expected keys {exp_common}/{exp_rare}, "
                f"Job-en reports {got} -- the sheet layout changed, stop and re-derive."
            )

    pcon = sqlite3.connect(str(poach_db))
    poach_rows = pcon.execute(
        "SELECT Key, Unknown8, IsRare FROM [PoachItem-en] ORDER BY Key"
    ).fetchall()
    if len(poach_rows) != 96:
        raise SystemExit(f"expected 96 PoachItem rows, got {len(poach_rows)} -- sheet layout changed?")
    poach_by_key = {int(key): (name, int(is_rare)) for key, name, is_rare in poach_rows}

    jobs = {}
    key_owner = {}   # key -> (commonKey, rareKey) pair that first claimed it
    pair_owner = {}  # (commonKey, rareKey) pair -> the FIRST (lowest id) job that claimed it
    emitted_aliases = []   # (aliasJobId, baseJobId) pairs, for the summary print
    for job_id, common_key, rare_key in job_rows:
        job_id, common_key, rare_key = int(job_id), int(common_key), int(rare_key)
        if common_key == 0 or rare_key == 0:
            continue   # human job (or an unnamed/reserved slot) -- carries no poach keys

        for key in (common_key, rare_key):
            if not (1 <= key <= 96):
                raise SystemExit(f"job {job_id}: key {key} outside 1..96 -- sheet layout changed?")
        if common_key not in poach_by_key or rare_key not in poach_by_key:
            raise SystemExit(f"job {job_id}: key {common_key}/{rare_key} does not resolve in PoachItem-en")

        common_name, common_rare_flag = poach_by_key[common_key]
        rare_name, rare_rare_flag = poach_by_key[rare_key]
        if common_rare_flag != 0:
            raise SystemExit(f"job {job_id}: common key {common_key} has IsRare={common_rare_flag}, expected 0")
        if rare_rare_flag != 1:
            raise SystemExit(f"job {job_id}: rare key {rare_key} has IsRare={rare_rare_flag}, expected 1")

        pair = (common_key, rare_key)
        prior_common = key_owner.get(common_key)
        prior_rare = key_owner.get(rare_key)
        if prior_common is None and prior_rare is None:
            key_owner[common_key] = pair
            key_owner[rare_key] = pair
            pair_owner[pair] = job_id
            jobs[job_id] = {
                "common": {"key": common_key, "name": common_name},
                "rare": {"key": rare_key, "name": rare_name},
            }
        elif prior_common == pair and prior_rare == pair:
            # ALIAS row: a higher, non-contiguous job id carrying an earlier job's EXACT carcass
            # pair (169-173 clone 103/97/98/100/94). LW-174: emit it as a first-class entry sharing
            # that pair instead of folding it away -- the vanilla encounter table fields these ids
            # in ordinary story battles (battle 384 Sweegy Woods, MainJob 169-172), and the runtime
            # gates entirely on map membership by job id, so no entry meant a silent no-poach.
            # Sharing one pair is safe: the store write is keyed by carcass key alone. key_owner is
            # deliberately NOT touched here, so the global per-distinct-pair uniqueness self-check
            # below still measures exactly what it did before.
            base_job = pair_owner[pair]
            jobs[job_id] = {
                "common": {"key": common_key, "name": common_name},
                "rare": {"key": rare_key, "name": rare_name},
                "aliasOf": base_job,   # self-documenting only; the runtime DTO ignores unknown fields
            }
            emitted_aliases.append((job_id, base_job))
        else:
            raise SystemExit(
                f"job {job_id}: key pair {pair} partially collides with an existing entry "
                f"(common owner {prior_common}, rare owner {prior_rare}) -- real corruption, not an alias"
            )

    if not jobs:
        raise SystemExit("no monster jobs resolved -- Job-en layout changed?")

    seen_keys = [k for pair in {tuple(v) for v in key_owner.values()} for k in pair]
    if len(seen_keys) != len(set(seen_keys)):
        raise SystemExit("internal error: carcass keys are not globally unique after dedupe")

    out = {
        "_provenance": "generated by tools/extract_poach_map.py from nxd/job.en.nxd (Job-en's "
                       "Unknown10/Unknown11 columns carry each job's PoachItem common/rare Keys "
                       "directly) joined against nxd/poachitem.en.nxd; carcass store u8[96] at "
                       "0x1411A7A1B + (key - 1); the species = job - 95 arithmetic was falsified "
                       "live by a Black Chocobo at job 95, 2026-08-12 -- see LIVE_LEDGER row "
                       "2026-08-12; the Job sheet's alias rows 169-173 are emitted as first-class "
                       "entries sharing their base jobs' (103/97/98/100/94) carcass pairs because "
                       "the vanilla encounter table fields those ids in ordinary story battles "
                       "(LW-174)",
        "jobs": {str(j): jobs[j] for j in sorted(jobs)},
    }
    OUT.write_text(json.dumps(out, indent=1), encoding="utf-8")
    ids = sorted(jobs)
    print(f"wrote {OUT} ({len(jobs)} monster jobs, ids {ids[0]}..{ids[-1]})")
    if emitted_aliases:
        shared = ", ".join(f"{alias} -> {base}" for alias, base in emitted_aliases)
        print(f"  emitted {len(emitted_aliases)} alias job entries sharing a base job's carcass "
              f"pair: {shared}")


if __name__ == "__main__":
    main()
