"""Loader for data/jobs.json, the hand edited list of every job departure from vanilla.

Why. Each equip category added or stripped, and the class evade floor, must say why it exists.
An empty or missing reason is a build failure, so a departure can never sit unexplained. The
loader keeps JSON key order, because the order of an add object is the order categories are
emitted in JobData.xml.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import ROOT

JOBS = ROOT / "data" / "jobs.json"


def missing_reasons(doc):
    """Labels such as 'Thief add Bow' for every departure whose reason is empty or absent."""
    bad = []
    groups = [(name, job) for name, job in doc["generic"].items()]
    groups += [(f"story {jid}", job) for jid, job in doc["story"].items()]
    for label, job in groups:
        for kind in ("add", "strip"):
            for cat, why in job.get(kind, {}).items():
                if not isinstance(why, str) or not why.strip():
                    bad.append(f"{label} {kind} {cat}")
    floor = doc["classEvadeFloor"].get("reason")
    if not isinstance(floor, str) or not floor.strip():
        bad.append("classEvadeFloor")
    return bad


def load(path=JOBS):
    """The parsed rules, or exit 1 naming each job and category that lacks a reason."""
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    bad = missing_reasons(doc)
    if bad:
        sys.exit("REFUSING: jobs.json departure without a reason: " + "; ".join(bad))
    return doc


# The attack flag byte of a weapon row. Same map the DLL packs with in ExtendedRecords.cs.
ATTACK_FLAGS = {"Striking": 128, "Lunging": 64, "Direct": 32, "Arc": 16,
                "TwoSwords": 8, "TwoHands": 4, "Throwable": 2, "ForcedTwoHands": 1}


def flag_byte(names):
    """The attack flag byte for a comma separated name list such as Arc, TwoHands."""
    return sum(0 if t.strip() == "None" else ATTACK_FLAGS[t.strip()]
               for t in str(names).split(",") if t.strip())


def flag_names(byte):
    """Names set in an attack flag byte, highest bit first."""
    return [n for n, bit in ATTACK_FLAGS.items() if byte & bit]
