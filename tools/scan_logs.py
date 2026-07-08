#!/usr/bin/env python
"""
Verify-time log scanner (LW-54): fail a live-verify run when livingweapon.log shows runtime errors.

This is a VERIFY step, NOT a build gate. The build never runs the game, so at build time the log is
a stale runtime artifact (wiped on the next deploy) and build failures are already caught by
generate/analyze/test/compile. This tool exists for the OTHER half: after a deploy + an in-game
battle, scan the freshly written livingweapon.log and hard-fail (nonzero exit) if the runtime
reported trouble, so a live-verify recipe can key off the exit code instead of a human hand-grepping
the log every session (which is exactly what surfaced this need, 2026-07-07, across a whole Mushin
probing session).

WHAT COUNTS AS A FAILURE (exit 1):
  * any [ERROR] line in the newest livingweapon.log (every error site in the mod is a catch block or
    a genuine misconfiguration; none fire on a healthy run -- see docs/LOGGING.md "Tier meanings"),
  * a fingerprint-guard STAND-DOWN (LaunchGuard.cs: "...standing down to protect your save"): the mod
    switched itself off, so nothing it does this session is trustworthy -- reported distinctly from a
    generic error because the remedy is different (rebuild the fingerprint for the new game build),
  * NOT ARMED despite a battle: the log shows a battle ran but the guard never emitted its armed edge
    (LaunchGuard.cs: "...Living Weapons is armed") and never stood down -- writes stayed disabled, so
    the mod was silently inert through gameplay.

WARN lines never fail the scan (they are "degraded but coping" per docs/LOGGING.md: locate misses,
fresh-start save notices, readback misses); they are surfaced as context only.

Log location: the newest launch's log is <modDir>/livingweapon.log (rotated to livingweapon.prev.log
on the next launch -- see FileConsoleLogger.MakeFileSink). The deployed <modDir> is resolved exactly
like BuildLinked.ps1 does: $RELOADEDIIMODS/prawl.fft.livingweapons if that env var is set, else the
default Steam install path.

Usage:
  python tools/scan_logs.py                     # scan the deployed mod's livingweapon.log
  python tools/scan_logs.py path/to/livingweapon.log      # scan a specific file
  python tools/scan_logs.py --mod-dir "D:/Mods/prawl.fft.livingweapons"
  python tools/scan_logs.py --flight            # also inspect the newest flight/*.jsonl archive
  python tools/scan_logs.py --require-battle     # also fail if no battle ran in this log
  python tools/scan_logs.py --allow "prompt system"   # suppress a known-benign error substring (repeatable)
  python tools/scan_logs.py --selftest          # run the built-in tests; no game/files needed

Exit codes: 0 = clean, 1 = runtime failure(s) found, 2 = could not run (no log / bad args).
"""
import os
import re
import sys
from collections import namedtuple
from pathlib import Path

# --- Deployed-install resolution (mirror BuildLinked.ps1's mods-folder logic) ---
MOD_ID = "prawl.fft.livingweapons"
DEFAULT_MODS_DIR = (r"C:\program files (x86)\steam\steamapps\common"
                    r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods")
LOG_NAME = "livingweapon.log"
PREV_LOG_NAME = "livingweapon.prev.log"

# --- Stable log-text landmarks (substrings, kept in sync with the C# source by comment) ---
# The exact wording lives in LivingWeapon/LaunchGuard.cs; matched on the most stable fragment so a
# copy edit to the surrounding sentence does not silently blind the scanner.
ARMED_MARK = "Living Weapons is armed"                 # LaunchGuard.ArmedEdge (INFO [startup])
STANDDOWN_MARK = "standing down to protect your save"  # LaunchGuard.StandDown (ERROR [startup])
STARTUP_MARK = "is starting inside"                    # Mod.cs launch header (INFO [startup])

# Findings carry a machine code (for the selftest / recipes) plus a human line.
Finding = namedtuple("Finding", ["severity", "code", "lineno", "text"])  # severity: "fail" | "warn"
ScanResult = namedtuple("ScanResult", [
    "findings", "n_lines", "errors_seen", "warns_seen",
    "startup", "armed", "stood_down", "battle",
])

# Current post-facelift file line: "[Living Weapons] [HH:mm:ss.fff] [LEVEL] ...".
_CUR_LEVEL = re.compile(r"^\[Living Weapons\]\s+\[[\d:.]+\]\s+\[([A-Z]+)\]")
# Legacy pre-facelift file line (livingweapon.prev.log-era): "HH:mm:ss.fff [FFTLivingWeapons] TAG ...".
_LEGACY_TAG = re.compile(r"\[FFTLivingWeapons\]\s+(ERROR:|WARNING:|DBG)")
_BOM = chr(0xFEFF)   # a leading UTF-8 byte-order mark; strip it before the anchored match


def line_level(line):
    """Return 'ERROR' | 'WARN' | 'INFO' | 'DEBUG' | None for one log line, spanning both the current
    and the legacy (livingweapon.prev.log) line shapes. None = not a recognizable mod log line."""
    line = line.lstrip(_BOM)   # a leading UTF-8 BOM would else defeat the anchored ^ match
    m = _CUR_LEVEL.match(line)
    if m:
        tok = m.group(1)
        return tok if tok in ("ERROR", "WARN", "INFO", "DEBUG") else None
    m = _LEGACY_TAG.search(line)
    if m:
        return {"ERROR:": "ERROR", "WARNING:": "WARN", "DBG": "DEBUG"}[m.group(1)]
    return None


def is_standdown(line):
    return STANDDOWN_MARK in line


def is_armed(line):
    return ARMED_MARK in line


def is_battle(line):
    # File lines carry the verb bracket; also match the console-shape sentence for robustness.
    return ("[battle-start]" in line or "[battle-end]" in line
            or "Battle started." in line or "Battle ended" in line)


def is_startup_header(line):
    return STARTUP_MARK in line


def scan_lines(lines, allow=(), require_battle=False):
    """The pure core: classify a log's lines and assemble findings. Operates on a list of text lines
    so the whole verdict is unit-testable with no real disk (see selftest). `allow` is a list of
    substrings that exempt an otherwise-failing ERROR line (a known-benign escape hatch)."""
    # Drop empty allow substrings: "" in line is ALWAYS True, so an empty --allow would silently
    # suppress every error (turning the escape hatch into a blanket kill switch). An empty entry is
    # meaningless, so treat it as a no-op rather than a footgun.
    allow = [s for s in allow if s]
    findings = []
    errors_seen = 0
    warns_seen = 0
    startup = armed = stood_down = battle = False

    for i, line in enumerate(lines, start=1):
        level = line_level(line)
        if is_startup_header(line):
            startup = True
        if is_armed(line):
            armed = True
        if is_battle(line):
            battle = True

        if is_standdown(line):
            # The stand-down is itself an ERROR line, but report it once and distinctly (its remedy
            # differs from a generic crash): do NOT also count it as a generic runtime error below.
            stood_down = True
            findings.append(Finding("fail", "stand-down", i,
                                    "fingerprint-guard stand-down: the mod switched itself OFF this "
                                    "session (writes disabled). Rebuild the fingerprint for the "
                                    "current game build. Line: " + line.strip()))
            continue

        if level == "ERROR":
            if any(sub in line for sub in allow):
                continue
            errors_seen += 1
            findings.append(Finding("fail", "runtime-error", i, "runtime error: " + line.strip()))
        elif level == "WARN":
            warns_seen += 1

    # Not-armed: a battle ran but the guard never armed and never stood down -> writes stayed off,
    # the mod was silently inert through gameplay. If it stood down, that finding already explains it.
    if battle and not armed and not stood_down:
        findings.append(Finding("fail", "not-armed", None,
                                "the log shows a battle but the mod never armed (no armed edge, no "
                                "stand-down): writes stayed disabled, so the mod was inert this run"))
    elif not battle and not armed and not stood_down:
        # No gameplay to judge against: cannot confirm the guard armed. Inconclusive, not a failure,
        # unless the caller demanded a battle.
        findings.append(Finding("warn", "inconclusive", None,
                                "no battle and no armed edge in this log: cannot confirm the mod "
                                "armed (a title-screen-only session looks like this)"))

    if require_battle and not battle:
        findings.append(Finding("fail", "no-battle", None,
                                "no battle ran in this log (--require-battle): the live-verify run "
                                "did not exercise a battle"))

    return ScanResult(findings=findings, n_lines=len(lines), errors_seen=errors_seen,
                      warns_seen=warns_seen, startup=startup, armed=armed,
                      stood_down=stood_down, battle=battle)


def exit_code(result):
    """0 if no fail-severity finding, else 1."""
    return 1 if any(f.severity == "fail" for f in result.findings) else 0


# --- File-system shell (thin; the logic above is pure and unit-tested via --selftest) ---

def resolve_mod_dir(explicit=None):
    if explicit:
        return Path(explicit)
    env = os.environ.get("RELOADEDIIMODS")
    base = Path(env) if env else Path(DEFAULT_MODS_DIR)
    return base / MOD_ID


def resolve_log_path(positional=None, mod_dir=None):
    """(path, note): the log file to scan, plus an optional human note. Prefers an explicit
    positional path; else <modDir>/livingweapon.log; falls back to livingweapon.prev.log with a
    note if the current log is absent. Returns (None, reason) if nothing is found."""
    if positional:
        p = Path(positional)
        return (p, None) if p.exists() else (None, f"no such log file: {p}")
    d = resolve_mod_dir(mod_dir)
    cur = d / LOG_NAME
    if cur.exists():
        return cur, None
    prev = d / PREV_LOG_NAME
    if prev.exists():
        return prev, f"{LOG_NAME} absent; scanning {PREV_LOG_NAME} (a prior launch) instead"
    return None, f"no {LOG_NAME} found in {d}"


def scan_flight_dir(flight_dir, log_mtime):
    """Inspect the newest flight_*.jsonl archive. An 'error'-triggered flush (filename
    ...__error.jsonl) means the flight recorder's first-error trigger fired. Returns a Finding
    (fail if the archive is at least as new as the log, else a warn that it may predate this run),
    or None. Flight files persist across launches and deploys, so recency matters."""
    d = Path(flight_dir)
    if not d.is_dir():
        return None
    files = sorted(d.glob("flight_*.jsonl"))  # names embed yyyyMMdd_HHmmss: lexical == chronological
    if not files:
        return None
    newest = files[-1]
    trigger = flight_trigger(newest.name)
    if trigger != "error":
        return Finding("warn", "flight-ok", None, f"newest flight archive: {newest.name} (trigger {trigger})")
    recent = newest.stat().st_mtime >= (log_mtime - 5) if log_mtime is not None else True
    if recent:
        return Finding("fail", "flight-error", None,
                       f"flight archive {newest.name} was flushed by the first-error trigger")
    return Finding("warn", "flight-error-stale", None,
                   f"a prior session's flight archive shows an error: {newest.name} (predates this log)")


def flight_trigger(filename):
    """flight_<yyyyMMdd_HHmmss>_<trigger>.jsonl -> '<trigger>' (or '' if unparseable)."""
    m = re.match(r"^flight_\d{8}_\d{6}_(.+)\.jsonl$", filename)
    return m.group(1) if m else ""


def report_lines(result, path, note, extra_findings=(), quiet=False):
    """Build the report as a list of strings (so the selftest can assert on it without capturing
    stdout). Returns (lines, findings). quiet=True suppresses ALL chatter except the [FAIL] lines:
    nothing at all on a clean scan, one line per failure otherwise, so a scripted caller relies on
    the exit code yet still gets a reason when it is nonzero."""
    findings = list(result.findings) + list(extra_findings)
    fails = [f for f in findings if f.severity == "fail"]
    warns = [f for f in findings if f.severity == "warn"]
    loc = lambda f: f" (line {f.lineno})" if f.lineno else ""
    if quiet:
        return [f"[FAIL] {f.text}{loc(f)}" for f in fails], findings
    yn = lambda b: "yes" if b else "no"
    lines = [f"Scanned {path} ({result.n_lines} lines)."]
    if note:
        lines.append(f"  note: {note}")
    lines.append(f"  startup header: {yn(result.startup)}   armed: {yn(result.armed)}   "
                 f"battle: {yn(result.battle)}   stand-down: {yn(result.stood_down)}")
    lines.append(f"  warnings: {result.warns_seen}   errors: {result.errors_seen}")
    if warns:
        lines.append("CONTEXT:")
        lines.extend(f"  [warn] {f.text}{loc(f)}" for f in warns)
    if fails:
        lines.append("FINDINGS:")
        lines.extend(f"  [FAIL] {f.text}{loc(f)}" for f in fails)
        lines.append(f"RESULT: FAILED ({len(fails)} finding(s))")
    else:
        lines.append("RESULT: CLEAN")
    return lines, findings


def render(result, path, note, extra_findings=(), quiet=False):
    """Print the report; returns the combined finding list (result + extras)."""
    lines, findings = report_lines(result, path, note, extra_findings, quiet)
    for ln in lines:
        print(ln)
    return findings


def parse_args(argv):
    opts = {"log": None, "mod_dir": None, "flight": False, "require_battle": False,
            "allow": [], "selftest": False, "quiet": False}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--selftest":
            opts["selftest"] = True
        elif a == "--flight":
            opts["flight"] = True
        elif a == "--require-battle":
            opts["require_battle"] = True
        elif a == "--quiet":
            opts["quiet"] = True
        elif a == "--mod-dir":
            i += 1
            if i >= len(argv):
                raise ValueError("--mod-dir requires a directory argument")
            opts["mod_dir"] = argv[i]
        elif a == "--allow":
            i += 1
            if i >= len(argv):
                raise ValueError("--allow requires a substring argument")
            opts["allow"].append(argv[i])
        elif a.startswith("--"):
            raise ValueError(f"unknown flag: {a}")
        else:
            if opts["log"] is not None:
                raise ValueError(f"unexpected extra argument: {a}")
            opts["log"] = a
        i += 1
    return opts


def main(argv):
    try:
        opts = parse_args(argv)
    except ValueError as e:
        print(str(e), file=sys.stderr)
        return 2
    if opts["selftest"]:
        return selftest()

    path, note = resolve_log_path(opts["log"], opts["mod_dir"])
    if path is None:
        print(f"scan_logs: {note}", file=sys.stderr)
        print("  (deploy + play a battle first, or pass a log path / --mod-dir)", file=sys.stderr)
        return 2
    try:
        # utf-8-sig transparently strips a leading BOM if some external editor re-saved the log
        # with one (the mod's own file sink writes UTF-8 without a BOM).
        text = Path(path).read_text(encoding="utf-8-sig", errors="replace")
    except OSError as e:
        print(f"scan_logs: could not read {path}: {e}", file=sys.stderr)
        return 2

    result = scan_lines(text.splitlines(), allow=opts["allow"], require_battle=opts["require_battle"])
    extra = []
    if opts["flight"]:
        try:
            log_mtime = Path(path).stat().st_mtime
        except OSError:
            log_mtime = None
        f = scan_flight_dir(Path(path).parent / "flight", log_mtime)
        if f is not None:
            extra.append(f)
    findings = render(result, path, note, extra, quiet=opts["quiet"])
    return 1 if any(f.severity == "fail" for f in findings) else 0


# ----------------------------------------------------------------------- selftest ----------------

def _hdr(dev=True):
    flavor = "development" if dev else "production"
    return [
        f"[Living Weapons] [19:10:00.001] [INFO] [startup] Living Weapons version 2.2.2 ({flavor} build) is starting inside fft_enhanced.exe.",
        "[Living Weapons] [19:10:00.010] [INFO] [config] Configuration loaded: VerboseLog=False BannerToasts=True LogLevel=Info (from ...\\Config.json)",
        "[Living Weapons] [19:10:00.020] [INFO] [save] The kill tally holds 63 lifetime kills across 12 weapons (kills.json, primary).",
        "[Living Weapons] [19:10:00.030] [INFO] [startup] The runtime loop has started.",
    ]


_ARMED = "[Living Weapons] [19:10:05.100] [INFO] [startup] The game build matches all memory landmarks; Living Weapons is armed (writes were held until now)."
_BATTLE = [
    "[Living Weapons] [19:11:00.000] [INFO] [battle-start] Battle started.",
    "[Living Weapons] [19:11:00.050] [DEBUG] [trace] battle-start sentinels (slot0=10 slot9=0 mode=3)",
    "[Living Weapons] [19:11:30.000] [INFO] [kill] Windrunner claims kill number 8, felling an undead foe at (7,6).",
    "[Living Weapons] [19:12:00.000] [INFO] [battle-end] Battle ended: 1 kills credited (Windrunner 1), 0 Marks earned, 0 tiers reached; 23 turns; the kill tally and legends are saved.",
]
_ERROR = "[Living Weapons] [19:11:15.000] [ERROR] [engine] One engine update was skipped; an internal error occurred: NullReferenceException at ..."
_STANDDOWN = "[Living Weapons] [19:10:05.100] [ERROR] [startup] The game build does not match this mod's memory landmarks (pe-build-key: TimeDateStamp 0x690C1269 != 0x6A0F86A9); Living Weapons is standing down to protect your save. The mod likely needs an update for a new game patch, or another installed mod has modified the job command tables."
_WARN = "[Living Weapons] [19:10:00.025] [WARN] [save] No kill tally on disk; starting fresh (kills.json will be created on the first save)."
_LEGACY_ERR = "19:11:15.000 [FFTLivingWeapons] ERROR: legacy failure from a prev.log-era archive"
_EXC_CONT = "[Living Weapons] [19:11:15.001] [ERROR]   InvalidOperationException: the set corrupted its state"


def _codes(result):
    return sorted(f.code for f in result.findings)


def selftest():
    """Built-in test suite (no game, no filesystem for the pure core). Prints PASS/FAIL per case;
    returns 0 iff every assertion holds. This is the tool's TDD gate, mirroring
    tools/probes/battle_cheats.py --selftest (no pytest in this repo)."""
    cases = []
    def check(name, cond):
        cases.append((name, bool(cond)))

    # 1. Healthy armed battle -> clean.
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE)
    check("healthy: clean", exit_code(r) == 0)
    check("healthy: armed+battle detected", r.armed and r.battle and r.startup and not r.stood_down)
    check("healthy: no findings", _codes(r) == [])

    # 2. A single [ERROR] line fails.
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE[:1] + [_ERROR] + _BATTLE[1:])
    check("error: fails", exit_code(r) == 1)
    check("error: one runtime-error finding", _codes(r) == ["runtime-error"])
    check("error: counter is 1", r.errors_seen == 1)

    # 3. Fingerprint-guard stand-down: reported distinctly, still fails; no double-count as generic error.
    r = scan_lines(_hdr() + [_STANDDOWN])
    check("standdown: fails", exit_code(r) == 1)
    check("standdown: reported as stand-down only", _codes(r) == ["stand-down"])
    check("standdown: stood_down flag set", r.stood_down and not r.armed)

    # 4. Not armed despite a battle -> fails.
    r = scan_lines(_hdr() + _BATTLE)
    check("not-armed: fails", exit_code(r) == 1)
    check("not-armed: not-armed finding", _codes(r) == ["not-armed"])

    # 5. Title-screen-only (no armed, no battle, no error) -> inconclusive, clean by default.
    r = scan_lines(_hdr())
    check("title-only: clean by default", exit_code(r) == 0)
    check("title-only: one warn (inconclusive)", [f.severity for f in r.findings] == ["warn"])
    # ...but --require-battle turns a battle-less log into a failure.
    r = scan_lines(_hdr(), require_battle=True)
    check("require-battle: no-battle fails", exit_code(r) == 1)
    check("require-battle: no-battle finding", "no-battle" in _codes(r))

    # 6. WARN lines never fail the scan.
    r = scan_lines(_hdr() + [_WARN, _ARMED] + _BATTLE)
    check("warn: clean", exit_code(r) == 0)
    check("warn: counted, not a fail", r.warns_seen == 1 and not any(f.severity == "fail" for f in r.findings))

    # 7. --allow suppresses a known-benign error substring.
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE + [_ERROR], allow=["an internal error occurred"])
    check("allow: suppressed error -> clean", exit_code(r) == 0)
    check("allow: error not counted", r.errors_seen == 0)

    # 8. Legacy prev.log ERROR shape is still caught.
    r = scan_lines(["19:10:00.001 [FFTLivingWeapons] DBG boot", _LEGACY_ERR])
    check("legacy: error caught", exit_code(r) == 1 and "runtime-error" in _codes(r))

    # 9. An exception continuation line counts as an error too.
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE + [_ERROR, _EXC_CONT])
    check("exc-continuation: both error lines counted", r.errors_seen == 2)

    # 10. Stand-down WITH a battle still reports only the stand-down (writes off, but Engine still ticks).
    r = scan_lines(_hdr() + [_STANDDOWN] + _BATTLE)
    check("standdown+battle: stand-down only, no not-armed", _codes(r) == ["stand-down"])

    # 11. Level parsing spans both shapes.
    check("level: current ERROR", line_level(_ERROR) == "ERROR")
    check("level: current WARN", line_level(_WARN) == "WARN")
    check("level: legacy ERROR", line_level(_LEGACY_ERR) == "ERROR")
    check("level: non-log line", line_level("just some text") is None)

    # 12. flight_trigger parsing (real recorder emits battle-exit/battle-start/error, per
    #     Engine.FlushBattleEnd/Start + FileConsoleLogger's Flight.RequestFlush("error")).
    check("flight: error trigger", flight_trigger("flight_20260707_191215_error.jsonl") == "error")
    check("flight: battle-exit trigger", flight_trigger("flight_20260707_192546_battle-exit.jsonl") == "battle-exit")
    check("flight: junk", flight_trigger("notaflight.txt") == "")

    # 13. --allow "" (empty substring) must NOT suppress every error (the blanket-kill footgun).
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE + [_ERROR], allow=[""])
    check("allow-empty: does not suppress", exit_code(r) == 1 and r.errors_seen == 1)
    # ...a non-empty allow still suppresses its target.
    r = scan_lines(_hdr() + [_ARMED] + _BATTLE + [_ERROR], allow=["an internal error occurred", ""])
    check("allow-mixed: real substring still suppresses", exit_code(r) == 0 and r.errors_seen == 0)

    # 14. A UTF-8 BOM on the first line still classifies that line's level (anchored-regex guard).
    check("bom: level parsed through BOM", line_level(_BOM + _ERROR) == "ERROR")
    r = scan_lines([_BOM + _ERROR])
    check("bom: first-line error caught", exit_code(r) == 1 and r.errors_seen == 1)

    # 15. --quiet: silent on a clean scan; only [FAIL] lines on failure; the machine signal (exit) holds.
    clean = scan_lines(_hdr() + [_ARMED] + _BATTLE)
    qlines, _ = report_lines(clean, "x.log", None, (), quiet=True)
    check("quiet: silent when clean", qlines == [])
    vlines, _ = report_lines(clean, "x.log", None, (), quiet=False)
    check("quiet: verbose still prints when clean", any("RESULT: CLEAN" in l for l in vlines))
    bad = scan_lines(_hdr() + [_ARMED] + _BATTLE + [_ERROR])
    qlines, _ = report_lines(bad, "x.log", None, (), quiet=True)
    check("quiet: only FAIL lines on failure", len(qlines) == 1 and qlines[0].startswith("[FAIL]"))

    passed = sum(1 for _, ok in cases if ok)
    for name, ok in cases:
        if not ok:
            print(f"  FAIL: {name}")
    print(f"scan_logs selftest: {passed}/{len(cases)} passed.")
    return 0 if passed == len(cases) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
