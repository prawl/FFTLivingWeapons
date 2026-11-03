# FFTModGates

One rulebook for every FFT mod. Each mod carries a copy of this folder, runs the same checks on
every build, and keeps only its own starting numbers. From the day a mod takes it, no number a
gate measures can get worse, and the copy itself cannot drift from what was published.

## What is in here

| Folder | What it holds | What a mod supplies |
| --- | --- | --- |
| `Logging/` | The logger every mod writes through (ILogger, ModLogger, FileConsoleLogger, NullLogger, ScopedLogger), compiled into the mod's own DLL as linked source | Its `LogVerb.cs` in the `ModGates.Logging` namespace |
| `Tests/` | The shared contract tests and the helpers they read the repo through, compiled into the mod's test project | Its `rules.json` and the baselines under `docs/health` |
| `tools/health.py` | The code health report, the ratchet, the floor and the per-file rows | Nothing beyond `rules.json` |
| `tools/manifest.py` | The fingerprint list, so the copy cannot drift | Nothing |
| `tools/style_scan.py` | The one second read of the comment and style rules, run after each edit by the shared setup's hook and by hand before `dotnet test`; the rules sit beside it in `style_rules.py` | Nothing beyond `rules.json` |
| `pipeline/gates.ps1` | The gate functions: manifest, format, unit tests, health, temp leftovers | Its own gates below the dot-source line |
| `Directory.Build.targets` | Warnings as errors and code style in the build | One import line |
| `.editorconfig` | The code style the build enforces | A byte-for-byte copy at its root |
| `MANIFEST.sha256` | A fingerprint per file, checked on every build | Nothing |

`Selftest/` holds a stand-in mod library and a stand-in test project, so the package is compiled
here exactly the way a mod compiles it and is held to the rules it enforces. `docs/merge/`
records where every rule from the five original copies went.

## How a mod takes it

Everything below is one commit. No gameplay code moves.

1. Vendor the folder as a subtree at a tagged version, so worktrees and fresh clones need nothing
   extra and the mod decides when it moves:

   ```
   git subtree add --prefix gates https://github.com/prawl/FFTModGates.git v0.1.1 --squash
   ```

2. Add `rules.json` at the repo root. Three readers parse it, the gate script, health.py and the
   shared tests, so every key is listed here once:

   | Key | Read by | Meaning |
   | --- | --- | --- |
   | `prod` | all three | The production code folder |
   | `tests` | health.py, tests | The test folder |
   | `pythonProd` | all three | Optional. Folders whose Python counts as production code, `tools/lib` included; the day a folder is first listed is its line in the sand |
   | `ticketPrefix` | gate script, tests | The ledger id prefix |
   | `projects` | gate script | Every csproj `dotnet format` verifies |
   | `testProjects` | gate script | Every csproj `dotnet test` runs |
   | `modConfig` | health.py | The JSON whose `ModVersion` names the release, or a plain file holding the version |
   | `devDefine` | health.py | Optional. The `#if` that compiles a dev spike out of release builds |
   | `tempPrefixes` | gate script | The temp folder names the repo's tests create |
   | `tempKeep` | gate script | Any fixed temp folder a test rebuilds every run |
   | `loggingDoc` | tests | Optional. The verb table, default `docs/LOGGING.md` |
   | `emDashScope` | tests | Optional. Folders and files where an em dash fails the style contract; left out, nothing is scanned, so data files with design notes stay out |

   ```json
   {
     "prod": "LivingWeapon/",
     "tests": "LivingWeapon.Tests/",
     "ticketPrefix": "LW",
     "projects": ["LivingWeapon/LivingWeapon.csproj", "LivingWeapon.Tests/LivingWeapon.Tests.csproj"],
     "testProjects": ["LivingWeapon.Tests/LivingWeapon.Tests.csproj"],
     "modConfig": "mod/ModConfig.json",
     "devDefine": "LWDEV",
     "tempPrefixes": ["lw_"],
     "tempKeep": []
   }
   ```

3. Compile the logger into the mod and the tests into the test project, as linked source:

   ```xml
   <ItemGroup>
     <Compile Include="..\gates\Logging\*.cs" Link="Gates\Logging\%(Filename)%(Extension)" />
   </ItemGroup>
   ```

   ```xml
   <ItemGroup>
     <Compile Include="..\gates\Tests\**\*.cs" Link="Gates\Tests\%(RecursiveDir)%(Filename)%(Extension)" />
   </ItemGroup>
   ```

   The traps the first mod hits, in order:

   - `LogVerb.cs` moves into the `ModGates.Logging` namespace, keeps a `Token()` extension
     giving each verb its bracket text, carries a `Trace` member, and has at least three
     members. `Selftest/Mod/LogVerb.cs` is the shape.
   - One file holds `global using ModGates.Logging;`.
   - The mod's assembly needs `[assembly: InternalsVisibleTo("<its test assembly>")]`. The logger
     types are internal.
   - `Mod.cs` calls `ModLogger.Init(modDir, "[Living Weapons]", "livingweapon")`. A mod with a
     flight recorder passes its flush as the `errorFlush` hook, and one that counts quiet faults
     passes `faultNote`.
   - The test project needs a module initializer that calls `ModLogger.UseNullLogger()` and
     turns off parallel test classes, since the facade is one static. `Selftest/Tests/TestSetup.cs`
     is the shape.
   - A csproj that sits at the repo root globs every `.cs` under it, including `gates/`. Add
     `<Compile Remove="gates\**" />` to that project.

4. Import the build settings from a root `Directory.Build.targets`:

   ```xml
   <Project>
     <Import Project="gates\Directory.Build.targets" />
   </Project>
   ```

   Then delete the same settings from the csproj files; the shared file owns them now. A repo
   still paying down warnings sets `ModGatesWarningsAsErrors` to false in that root file. The
   health gate then counts the build's warnings on every run and holds them to
   `docs/health/warnings.json`: the first run writes the record, a lower count moves it, a
   higher count is refused. Raising the record is the owner's call, with the ledger row named
   in its `card` field.

5. Load the gate functions at the top of `tools/pipeline.ps1`, keep the repo's own gates below,
   and run them inside the gates' own temp folder:

   ```powershell
   . "$PSScriptRoot\..\gates\pipeline\gates.ps1"
   ```

   ```powershell
   $gateTemp = Enter-GateTemp
   try {
       Invoke-ManifestGate -FailVerb DEPLOY
       Invoke-FormatGate -FailVerb DEPLOY
       Invoke-UnitTestGate -FailVerb DEPLOY
       Invoke-HealthGate -FailVerb DEPLOY
       Invoke-TempLeftoverGate -FailVerb DEPLOY -GateTemp $gateTemp
   }
   finally { Exit-GateTemp -State $gateTemp }
   ```

   The CI workflow sets `GATES_BASE_SHA` to the commit a push started from, so the health
   ratchet also judges against the memory on main, not only the last commit:

   ```yaml
   env:
     GATES_BASE_SHA: ${{ github.event_name == 'pull_request' && github.event.pull_request.base.sha || github.event.before }}
   ```

6. Copy `gates/.editorconfig` to the repo root. The style contract checks the two match.

7. Record today's numbers and commit them with the hookup, with
   `python gates/tools/health.py floor --write`. It writes `docs/health/floor.json`, the totals,
   and `docs/health/files.json`, one row per production file. From then on a build with no
   committed floor is refused, not warned about. Release records the repo wrote before that day
   stay in its trend and never set a bar; the first floor is the line in the sand. From then on
   the shared pre-commit (`install_git_hooks.sh <repo>` from the setup repo) runs the same
   command for every commit that touches production code and stages the two files, so a
   shrink, a new file or a gone file never reaches the ratchet unrecorded. A floor that would be
   worse refuses the commit with the row to paste, since widening stays the owner's call.

## The per-file rule

Every production file has a row in `docs/health/files.json`: comment lines, longest comment
block, code lines, silent catches and methods over 60 code lines. The health ratchet holds each
file to its row on every build.

- Comment lines and the longest block are frozen at the row. One more comment line fails.
- Code lines may grow up to 200, silent catches and long methods not at all. A file already over
  one of those lines on day one carries the debt as its row, and that row only shrinks.
- A file with no row is new and meets the full rule, under 200 code lines included. A rename is a
  new file.
- The parts of a `partial` type are summed under one row named `partial <Type>`, so splitting a
  file without a real seam changes nothing.
- Python under a folder `rules.json` lists in `pythonProd` gets the same row, keyed by its repo
  relative path, with a silent catch being an `except` whose body is only `pass` and a long
  method a `def` over 60 code lines. The day a folder is first listed, every file under it is
  debt rather than new, the line in the sand for that language. A repo that lists nothing sees
  no change, and a repo whose record holds no C# row at all is no longer refused for that alone.
  Listing a folder after the first floor moves the totals too, so that floor is the owner's,
  written with `floor --write --allow-worse --card <id>` citing the card that opted the folder in.
- When a file shrinks, or is new, or is gone, the row must follow before a build passes. The
  shared pre-commit writes it into the commit for you; by hand it is
  `python gates/tools/health.py floor --write`, committed with the change. The comment budget
  test reads the same rows, so `dotnet test` says the same thing earlier.
- Widening a row or a total is the owner's call. Every refusal ends with a Backlog row to paste
  that names the rule it wanted to bend and why, under the next free ledger id. The owner then
  runs `floor --write --allow-worse --card <id>` citing that row. The records remember the card,
  and the ratchet judges a widened key at HEAD alone, so the override survives CI's base ref. A
  pile of such rows against one rule says the rule is wrong, not the code.

## Every frozen number

A refusal from a test that freezes a count names its baseline and what moves it, so the fix and
the baseline land in one edit. The same list, in one place:

| Baseline | Who reads it | What moves it |
| --- | --- | --- |
| `docs/health/files.json` | the health ratchet, the comment budget test, the silent catch test | `python gates/tools/health.py floor --write` for a shrink, a new file or a gone file, which the shared pre-commit runs for you; `floor --write --allow-worse --card <id>` when the owner widens a row |
| `docs/health/floor.json` | the health ratchet's totals | the same command |
| `docs/health/style.json` | the style contract's doc width and comment width counts | hand written, `"docs": {"docs/X.md": n}` and `"comments": {"File.cs": n}`; no command |
| `docs/health/warnings.json` | the warning ratchet in the health gate | the gate writes a lower count; the owner raises it by hand and names the ledger row in `card` |
| a mod's own counts (proof claims and the like) | that mod's own tests | its own README says |

## Taking a newer rulebook

```
git subtree pull --prefix gates https://github.com/prawl/FFTModGates.git v0.2.0 --squash
```

The commit is the moment the mod takes the new rules. `gates/VERSION` says which, every build
prints it, `/gates` in a session shows it beside the session gates, and the manifest gate refuses
a build when any file under `gates/` differs from the published list. A fix to the rules goes
upstream here, never into a mod's copy.

## Who is on it

| Mod | Rulebook | Since | Hookup commit |
| --- | --- | --- | --- |
| Treasure Master | v0.1.0 | 2026-11-03 | b8287c9 |
| Living Weapons | v0.1.1 | 2026-11-03 | 22a290ff |
| Offensive Chemist | v0.1.1 | 2026-11-03 | bb6f60d |
| Color Customizer | v0.1.2 | 2026-11-03 | 379c1a12 |
| The private mod | not yet | | |

The paydown of the recorded debt starts when every row names a version.

## What stays in each mod

Tape tests, data pipeline gates, and the docs, readme, proof claim and pipeline manifest
contracts, because each one checks that mod's own files.

## Working on this repo

`.\Check.ps1` runs every gate here, under Windows PowerShell 5.1 and PowerShell 7 in CI. The
shared pre-commit rewrites `MANIFEST.sha256` on every commit here; without the shim, run
`python tools/manifest.py write` before committing, since a stale list fails the check. The
ledger is `docs/TODO.md` and `docs/CHANGELOG.md` with the MG id prefix.
`python tools/style_scan.py` answers the comment and style rules in a second, on the whole repo
or on the files named; the shared setup runs it after every edit, so a slip shows before the
gate does.
