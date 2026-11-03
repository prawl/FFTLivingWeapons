# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [MG-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 0.1 cycle

- [MG-11] SHIPPED 3da7098 2026-11-03: The rulebook now has a version a mod takes by name, v0.1.0,
  so two mods on one tag run the same rules and a change here reaches a mod only when it pulls
  the next tag. Treasure Master took it first and runs green on it locally and in CI, and
  gates/VERSION prints 0.1.0 in its build log. (Tech: tag v0.1.0 on 3da7098, pushed; the README's
  subtree lines name the tag; Treasure Master hookup commit b8287c9, CI run 41582500395.)
- [MG-13] SHIPPED 3a96d67 2026-11-02: A refused gate now ends with the Backlog row to paste, under
  the next free ledger id, naming the rule it wanted to bend and why, so the paper trail of rules
  that got in the way piles up in one place. The owner widens a rule with floor --write
  --allow-worse --card <id>, which must be a row of docs/TODO.md; the floor and files records store
  the card, and the ratchet judges a widened key at HEAD alone so the override survives CI's base
  ref. (Tech: health.py confession, has_card, next_id, valid_allowances, shield, floor_faults;
  Get-Confession in gates.ps1 for the manifest gate.)
- [MG-3] SHIPPED fe4af4c 2026-11-02: Every production file now has its own row of comment lines,
  longest block, code lines, silent catches and long methods in docs/health/files.json, and the
  health ratchet holds the file to it on every build. Comments and the block are frozen, code may
  grow to 200 lines, debt only shrinks, a new or renamed file meets the full rule, partial parts
  count as one file, and a shrunk row must be committed. (Tech: health.py floor --write writes the
  rows, ratchet judges them against HEAD and the base ref, FileRows.cs feeds the comment budget and
  silent catch tests, comments.json and silentCatchAllowlist are gone.)
- [MG-4] SHIPPED eeea715 2026-11-02: The style contract checks the repo's line ending file against
  the shared one, the same way as the style file, so line endings stop being a judgment call. (Tech:
  StyleContractTests, the byte for byte check over .editorconfig and .gitattributes.)
- [MG-2] SHIPPED eeea715 2026-11-02: The five copies of each contract test are one copy each, losing
  no rule and no example. Tests/ holds the ledger, log, image literal, comment budget, comment,
  silent catch, style and health wiring contracts reading rules.json, and the self test proves all
  172 original facts and 299 examples landed in a shared fact, a local one, or a declared new one.
  (Tech: docs/merge/originals.json is the denominator, docs/merge/mapping.json the placement,
  Selftest/Tests/MergeLedgerTests.cs the proof.)
- [MG-8] SHIPPED 6276a6f 2026-11-02: A mod cannot quietly edit its copy of the rulebook. Every file
  has a fingerprint in MANIFEST.sha256, the manifest gate runs first on every build and refuses a
  copy that differs, and this repo's check refuses a stale list. (Tech: tools/manifest.py write and
  verify, Invoke-ManifestGate, CRLF folded before hashing.)
- [MG-9] SHIPPED 6276a6f 2026-11-02: The health ratchet cannot be dodged by deleting or editing its
  memory. The bar is read from the last commit and from a base ref, never the working copy, and a
  repo with no committed memory is refused. (Tech: load_records and load_floor take a git ref,
  ratchet --base, GATES_BASE_SHA from the workflow.)
- [MG-10] SHIPPED 6276a6f 2026-11-02: The self test builds the rulebook the way a real mod does, a
  library holding the logger and a separate test project seeing it through InternalsVisibleTo, and
  the README names every trap the first mod hits. (Tech: Selftest/Mod and Selftest/Tests.)
- [MG-12] SHIPPED 6276a6f 2026-11-02: The build settings say why they force what they force, and the
  README lists every rules.json key with who reads it.
- [MG-1] SHIPPED 304b739 2026-11-02: One rulebook every mod can take, seeded from the best copy of
  each rule. The shared logger, health.py, the gate functions, the build settings and the style
  file live here with no mod's name inside, and the repo checks itself with its own gates.
  (Tech: Logging/ with fault note and error flush hooks, tools/health.py reading rules.json,
  pipeline/gates.ps1, Directory.Build.targets, Selftest/ with 50 tests; 13 of 13 health numbers
  identical on the newest mod's tree.)
