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
