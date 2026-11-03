# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, the same system as the mods with the MG id prefix. "Now" holds what is actively
being worked for the current release (hard cap 5, each entry carries Done means + Verify).
"Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this file only
through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The release ship
gate lives in docs/RELEASE_SCOPE.md.

## Now (release: 0.1)

- **[MG-11] Pin what a mod takes to a tagged version** (opened 2026-11-02) [QUEUED]
  - Done means: a mod vendors the rulebook at a tag, never at whatever main holds today, so two
    mods on one tag run the same rules and a change here reaches a mod only when it pulls the
    next tag. (Tech: tag v0.1.0 once Treasure Master runs green on it, the README's subtree lines
    name the tag; CI already runs Check.ps1 under both PowerShells since 6276a6f.)
  - Verify: a fresh Treasure Master clone takes the tag with git subtree add, builds, passes its
    own check, and gates/VERSION prints the number the tag carries.

## Backlog

- [MG-5] 2026-11-02: Let the style contract's em dash ban take a per-repo scope list, since data
  files hold design notes with em dashes that are not game text.
- [MG-6] 2026-11-02: Have the shared setup's /gates status print the rulebook version a repo is
  on, so one screen shows both the session gates and the build gates.
- [MG-7] 2026-11-02: Track which mods are on the shared rules, so the paydown can start when all
  five are. Order after this repo: Treasure Master, Living Weapons, Offensive Chemist, Color
  Customizer, and the private mod once its open branch lands. The per-file ratchet (MG-3) lands
  before the first hookup, so no mod migrates twice.
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
