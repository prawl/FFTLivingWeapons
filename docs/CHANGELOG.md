# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [MG-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 0.2 cycle

- [MG-5] SHIPPED 5a6096f 2026-11-03: A repo can now say where its em dash ban applies, so the
  ban reaches its code and docs without reaching data files whose design notes are not game
  text. A repo that lists nothing is not scanned, by the owner's ruling of 2026-11-03; this
  repo lists its docs, code, tools, pipeline, selftest, README and check script. (Tech:
  rules.json emDashScope, folders or files; a style contract test walks them, skips binaries
  and names each line; the scanner applies the same rule; the README key table gains the row.)
- [MG-25] SHIPPED 5a6096f 2026-11-03: A total or a row the owner widened under a card now stays
  widened after that card ships, instead of the ratchet going red on the next build against
  the main branch. Found the moment MG-17 shipped, when the base ref's old floor came back for
  three totals and the check went red. (Tech: valid_allowances in health.py reads the card
  from TODO.md and CHANGELOG.md at the ref; a selftest case pins that a SHIPPED row is a card.)
- [MG-18] SHIPPED ef3027b 2026-11-03: Shipping the last card in Now no longer fails the ledger
  test, so nobody has to invent a next card at every last exit. The owner ruled so on
  2026-11-03, once the reason behind the refusal turned out to be written nowhere. The cap of
  five and every other rule stay. (Tech: the NotEmpty assertion left TodoContractTests; the
  Format section says Now may stand empty; the global CLAUDE.md dropped its sentence; every
  repo on the rulebook takes the change at its next subtree pull.)
- [MG-6] SHIPPED ef3027b 2026-11-03: One screen now shows both the session gates and the build
  gates. The /gates table has a rulebook row with the version a repo carries, whether the
  pre-commit rows step and the style scan hook are live there, and the scan's last hit; the
  session start line names the version too. Its first live run showed Color Customizer with no
  shared pre-commit shim. (Tech: rulebook_version and rulebook in the shared setup's gates.py,
  reading gates/VERSION in a mod or VERSION in this repo; five tests in test_gates.py.)
- [MG-17] SHIPPED a1de979 2026-11-03: A mod written in Python can now be held to the same rules
  as a C# mod. A repo lists the folders in rules.json and every .py under them, tools/lib
  included, gets a row with the 200 line gate, the comment budget, the silent except and
  long def counts and the duplication ratchet; the comment and style contracts read them
  too. A repo that lists nothing sees no change, and one whose record holds no C# row is no
  longer refused for that alone. Found hooking up Offensive Chemist, whose ten tools files
  now measure from a scratch clone with gate.py as its one debt row. This repo opted in its
  own tools folder and widened three totals under this card, since the day a folder is first
  listed is its line in the sand. (Tech: rules.json pythonProd; health.py prod_root, row_name,
  function_lengths, SILENT_EXCEPT and fresh_folders; FileRows carries a Python flag; the
  docstring first sentence stands in for the summary; eleven selftest cases and three
  theories.)
- [MG-14] SHIPPED 625776e 2026-11-03: Nobody runs floor --write by hand any more. When a commit
  touches production code in a repo on the rulebook, the shared pre-commit writes the health
  file rows and stages them, so a shrink, a new file or a gone file never reaches the ratchet
  unrecorded. A floor that would be worse still refuses the commit with the row to paste. In
  this repo the same hook rewrites the fingerprint list on every commit. (Tech: health_rows.py
  behind health_rows.sh in the shared setup's git_hooks/pre-commit, after the cold read step;
  rows that did not move are put back so no commit carries a floor whose only change is its
  date; six tests through real commits and a stub health.py; the row shows on the /gates
  screen.)
- [MG-23] SHIPPED b6321a6 2026-11-03: When a test that freezes a count goes red, its message
  now says which file holds the number and what moves it, so the fix and the baseline land in
  one edit instead of three reruns. The README lists every frozen baseline in one table. From
  the second handoff feedback in the private mod, where three baselines went red one at a
  time after one patch. (Tech: FileRows.WriteHint and WidenHint name files.json and health.py
  floor --write; StyleContractTests.FreezeHint names style.json as hand written; the comment
  budget, silent catch, doc width and comment width messages carry them, pinned by a test
  that reads the builders.)
- [MG-22] SHIPPED 35e2059 2026-11-03: A session now sees a bad comment or a layout slip right
  after writing it, instead of seven minutes later when the full gate goes red. One Python
  script under tools answers the comment and style rules in a quarter second, on the whole
  repo or on the files named, and the shared setup runs it after every edit of a .cs, .py,
  .ps1 or .md file in a repo on the rulebook. The C# tests stay the verdict. From the first
  handoff feedback in the private mod, where the gate went red five times on rules a reader
  could not see in advance. (Tech: tools/style_scan.py with the rules in style_rules.py; a
  PostToolUse hook in the shared settings.json through style_scan.sh and style_scan_hook.py,
  exit 2 with the hits on stderr; six hook tests through a stub scanner.)
- [MG-21] SHIPPED 2c679b3 2026-11-03: Deleting unique code can no longer make the duplication
  floor refuse a build. The ratchet now holds the number of lines sitting in repeated blocks,
  not their share of all code, so a shrink with no new repeated line passes. Found swapping
  Color Customizer onto the shared logger, where removing five logger files raised the share
  from 3.13% to 3.19% and the owner had to widen under CC-50. (Tech: the snapshot gains
  duplicated_lines and RATCHET_ROWS judges it in place of duplication, which stays in the
  report; a floor without the key is silent on it until floor --write records it, as this
  repo's floor now does; two selftest cases.)

## 0.1 cycle

- [MG-20] SHIPPED 9ec036c 2026-11-03: Two production files with the same name no longer crash
  the image literal checks. Color Customizer has two StoryCharacterRegistry.cs and two
  ModInitializer.cs, and the allow list and pin table checks threw on the second of each. Now a
  name counts as still needing its entry when any file of that name does. (Tech: ToLookup by
  file name instead of ToDictionary in ImageLiteralContractTests; Color Customizer links the
  contract from v0.1.2 and its 182 shared tests pass.)
- [MG-19] SHIPPED 9ec036c 2026-11-03: A mod still paying its warnings down now has the count
  held so it can only fall. Turning the shared warnings as errors switch off arms a ratchet in
  the health gate that counts the build's compiler warnings and holds them to
  docs/health/warnings.json. The first run writes the record, a lower count moves it, and a
  higher count is refused with the confession row. Color Customizer's first run wrote 263 and a
  planted unused variable was refused. (Tech: Invoke-WarningRatchet inside Invoke-HealthGate,
  armed by ModGatesWarningsAsErrors false in the root Directory.Build.targets; a full rebuild of
  every non test project in rules.json, distinct warning lines; the record holds since, warnings
  and an optional card.)
- [MG-7] SHIPPED 17fee2d 2026-11-03: The README now says which mod runs which rulebook version
  since when, with its hookup commit, so anyone can see when the last mod is in and the paydown
  of the recorded debt can begin. Treasure Master is on v0.1.0 and Living Weapons on v0.1.1;
  Offensive Chemist, Color Customizer and the private mod are not yet. (Tech: the Who is on it
  table in README.md; each hookup updates its row in the same session.)
- [MG-16] SHIPPED 51d20d5 2026-11-03: A mod that already had months of release records can
  now take the rulebook without being told its code is worse than its best month. The first
  floor is the line in the sand, and older records feed the trend but never set a bar. Living
  Weapons' first floor was refused against its v2.1.0 record before this. (Tech: floor.json
  carries since, the first floor's date; since_adoption filters records in the ratchet and the
  floor; three new selftest checks.)
- [MG-15] SHIPPED a0b2da8 2026-11-03: Loading the gate script now prints nothing, and the first
  gate names the rulebook version instead, so a mod test that dot-sources its pipeline script
  and reads the output gets its answer and not the banner. Living Weapons has nine such tests.
  (Tech: the Write-Host moved from the body of gates.ps1 into Invoke-ManifestGate; VERSION
  0.1.1; tag v0.1.1 at 51d20d5.)
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
