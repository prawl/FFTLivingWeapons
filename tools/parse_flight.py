"""Print a human-readable timeline from a flight-recorder .jsonl archive.

The flight recorder (LivingWeapon/FlightRecorder.cs) flushes an always-on bounded ring of
on-change events to <modDir>/flight/flight_<yyyyMMdd_HHmmss>_<trigger>.jsonl on the battle-exit
edge or the first ModLogger.LogError of a launch (see docs/LOGGING.md, "Flight recorder"). Each
file is JSONL: a header line ({"hdr": true, "wall": "...", "t": <elapsedMs>}) followed by one
compact JSON object per record ({"t": <elapsedMs>, "e": "<type>", "d": "<payload>"}).

This prints each record as "+N.NNNs [type] payload", relative to the header's elapsedMs anchor.
The header's wall-clock timestamp cross-references livingweapon.log's HH:mm:ss.fff lines --
elapsedMs is the recorder's own monotonic clock (Environment.TickCount64), not wall-clock, so add
the relative offset to the header's wall time to line a record up against a specific log line.

  python tools\\parse_flight.py flight_20260704_010101_battle-exit.jsonl
  python tools\\parse_flight.py flight_20260704_010101_battle-exit.jsonl --grep kill
"""
import json
import sys


def load_records(path):
    """Read one flight_*.jsonl file -> (header_dict, [record_dict, ...]). Raises ValueError on
    an empty file, a non-JSON line, or a missing/misplaced header (the header must be first)."""
    header = None
    records = []
    with open(path, "r", encoding="utf-8") as f:
        for lineno, raw in enumerate(f, start=1):
            line = raw.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError as e:
                raise ValueError(f"{path}:{lineno}: not valid JSON -- {e}") from e
            if header is None:
                if not obj.get("hdr"):
                    raise ValueError(f"{path}:{lineno}: expected the header line ({{'hdr': true, ...}}) first")
                header = obj
                continue
            records.append(obj)
    if header is None:
        raise ValueError(f"{path}: empty file -- no header line found")
    return header, records


def print_timeline(header, records, grep=None):
    t0 = header.get("t", 0)
    print(f"header: wall={header.get('wall', '?')} elapsedMs={t0}")
    if grep:
        print(f"(filtering to type == {grep!r})")
    shown = 0
    for rec in records:
        etype = rec.get("e", "")
        if grep and etype != grep:
            continue
        rel = (rec.get("t", t0) - t0) / 1000.0
        print(f"+{rel:8.3f}s [{etype}] {rec.get('d', '')}")
        shown += 1
    print(f"\n{shown}/{len(records)} record(s) shown.")


def parse_args(argv):
    """(paths, grep_type_or_None). Keeps the arg parsing dependency-free (no argparse needed for
    one optional flag) -- mirrors the other one-shot tools/ scripts' plain sys.argv handling."""
    grep = None
    positional = []
    skip_next = False
    for i, a in enumerate(argv):
        if skip_next:
            skip_next = False
            continue
        if a == "--grep":
            if i + 1 >= len(argv):
                raise ValueError("--grep requires a TYPE argument")
            grep = argv[i + 1]
            skip_next = True
            continue
        if a.startswith("--"):
            continue
        positional.append(a)
    return positional, grep


def main(argv):
    try:
        paths, grep = parse_args(argv)
    except ValueError as e:
        print(str(e), file=sys.stderr)
        return 2
    if not paths:
        print(__doc__)
        return 2
    header, records = load_records(paths[0])
    print_timeline(header, records, grep)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
