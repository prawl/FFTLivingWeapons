# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, the same system as the mods with the MG id prefix. "Now" holds what is actively
being worked for the current release (hard cap 5, each entry carries Done means + Verify).
"Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this file only
through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The release ship
gate lives in docs/RELEASE_SCOPE.md.

## Now (release: 0.1)

- **[MG-7] Track which mods are on the shared rules so the paydown can start when all five are** (opened 2026-11-02) [QUEUED]
  - Done means: one place in this repo says which mod runs which rulebook version and since when,
    so anyone can see when the last mod is in and the paydown of the recorded debt can begin.
    Order after Treasure Master, on v0.1.0 since 2026-11-03: Living Weapons, Offensive Chemist,
    Color Customizer, and the private mod once its open branch lands. (Tech: a table in README.md
    with mod, version taken, date and hookup commit; each hookup updates its row in the same
    session.)
  - Verify: the table names Treasure Master at v0.1.0 with its hookup commit and gains a row with
    each later hookup; Check.ps1 passes with the table in place.
- **[MG-15] Keep the rulebook quiet when a mod's own tests load the gate script** (opened 2026-11-03) [BUILDING]
  - Done means: loading gates.ps1 prints nothing, and the line naming the rulebook version moves
    into the first gate, so a mod test that dot-sources its pipeline script and reads the output
    gets its answer and not the banner. Living Weapons has nine such tests. (Tech: the Write-Host
    moves from the file body into Invoke-ManifestGate; VERSION 0.1.1; tag v0.1.1.)
  - Verify: Check.ps1 green and its log still names the version from the manifest gate; the nine
    Living Weapons tests pass on the v0.1.1 subtree.
- **[MG-16] Let a mod with old health records start its floor from today's tree** (opened 2026-11-03) [BUILDING]
  - Done means: the first floor of a repo is its line in the sand even when release records from
    earlier months hold better numbers, so day one debt is recorded and not refused. Living
    Weapons' first floor was refused against its v2.1.0 record. (Tech: floor.json carries since,
    the first floor's date; since_adoption drops records older than it from the ratchet and the
    floor; the trend still reads every record.)
  - Verify: health.py --selftest covers a record older than the floor being history and a newer
    one still setting the bar; Living Weapons' floor --write succeeds on v0.1.1 and its ratchet
    runs green.

## Backlog

- [MG-5] 2026-11-02: Let the style contract's em dash ban take a per-repo scope list, since data
  files hold design notes with em dashes that are not game text.
- [MG-6] 2026-11-02: Have the shared setup's /gates status print the rulebook version a repo is
  on, so one screen shows both the session gates and the build gates.
- [MG-14] 2026-11-02: Have the pre-commit hook write the file rows, so nobody runs floor --write
  by hand and a shrink is never left for the ratchet to ask about. (Tech: the shared pre-commit in
  the setup repo runs health.py floor --write when rules.json is present and stages files.json.)

## Walled (blocked by engine / external)

- Nothing yet.

## Format (enforced by TodoContractTests)

- Sections, in this order and no others: Now (with the release name in the header), Backlog,
  Walled, Format.
- Now: at most 5 entries. Entry first line: `- **[MG-<n>] <title>** (opened YYYY-MM-DD) [STATUS]`
  where STATUS is QUEUED, BUILDING, AWAITING-LIVE, or BLOCKED(reason). Every entry carries a
  `- Done means:` and a `- Verify:` sub-bullet. Promote from Backlog by filling those in; if Now
  is at cap, demote something first.
- Backlog: entry first line `- [MG-<n>] YYYY-MM-DD: <one sentence>`; indented continuation lines
  are free. Capture new items here in the session they surface.
- ELI5-first prose: the first sentence of every entry, and the opening of every Done means /
  Verify, is plain language a non-programmer follows. Technical detail comes AFTER, in
  continuation lines or a "(Tech: ...)" tail, never instead of it.
- IDs are unique across this file and docs/CHANGELOG.md; never reuse a retired ID.
- Items exit ONLY by moving to docs/CHANGELOG.md when they ship or die: in the shipping commit
  itself, or in the immediately following commit when the exit row cites that commit's own hash.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE resolutions (flipping a row out of AWAITING-LIVE) are owner-only.
