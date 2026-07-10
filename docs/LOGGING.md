# Logging model

STATUS: CONTRACT (logging + flight recorder reference; the verb table is test-gated)

One page: the line format, the tier model, the closed event-verb glossary, the launch header and
the per-battle match report (with worked examples), and where the flight recorder (the black box)
slots in. This is the post-facelift reference (2026-07-05); `livingweapon.prev.log`-era archives
predate it and keep the old shape (see "Reading old archives" below).

## Line format

Every line opens with `[Living Weapons]`. Both sinks always carry a millisecond timestamp
(`[HH:mm:ss.fff]`) and a level bracket (`[INFO]`/`[WARN]`/`[ERROR]`/`[DEBUG]`); the console
timestamp is load-bearing (it is what lets a player's bug-report console paste be joined back to
the matching `livingweapon.log` lines), never drop it. Beyond that, the FILE and CONSOLE shapes
diverge by design (the rendering split):

- **FILE, every line:** `[Living Weapons] [HH:mm:ss.fff] [LEVEL] [verb] description`: five
  tokens. The `[verb]` bracket names one of the closed event verbs below.
- **CONSOLE, Info tier:** `[Living Weapons] [HH:mm:ss.fff] [INFO] description`: four tokens, no
  verb. This is the match report a player actually reads, so its sentences are subject-first prose
  (see "Subject-first lexical fence" below) rather than a labeled event feed.
- **CONSOLE, Warning/Error tier:** `[Living Weapons] [HH:mm:ss.fff] [LEVEL] [verb] description`:
  five tokens, same as the file. A bug-report console paste needs the verb for triage.
- **CONSOLE, Debug tier (only when the console level is raised to Debug in Mod.cs):** five tokens, verb included; a Debug line
  reaching the console is a diagnostic request, not curated narrative.

**Full words on console lines** (no `cfg`/`dmg`/`abil`/`lvl` abbreviations), **names not ids**:
weapon and job names come from `LogNames`; numeric ids, hex addresses, offsets, and read-backs
live in parentheses on FILE lines only. The standard mechanism is the **two-line id pattern**
(`ModLogger.EventWithTrace`/`WarnWithTrace`): a clean console sentence plus a `[trace]` Debug
companion carrying the parenthesized ids. No " -- " separator and no em dash appear in any log
text (colon/semicolon/comma/parens instead); `LogContractTests` enforces this by source scan.

**Console dedup is a semantic key, not a rendered-string key:** the console suppresses a repeat
only when the SAME (level, verb, message) triple already appeared this battle (reset on both
battle edges via `ModLogger.NoteBattleEdge`). Two different verbs sharing the same Info-tier
sentence render identical console text (the verb is hidden there) but are still two distinct
events, and both reach the console. The FILE is never deduped.

**Subject-first lexical fence:** every Info/Warning console-eligible message (routed through
`ModLogger.Event`/`Warn`/`EventWithTrace`/`WarnWithTrace` or a `ScopedLogger`'s `Info`/`Warn`)
must read as a sentence, not a label. `LogContractTests` enforces a LEXICAL floor (the message
must open with an uppercase letter or an interpolation hole, and must not open with a bare
`Word:` leader such as `Armed:`/`Locked:`/`Granted:`), but full subjecthood beyond that lexical
check is a human review rule, not something a regex can certify. The two `#if LWDEV` dev
instruments (`ShowSpike.cs`, `FlavorSpike.cs`) are exempt from the fence only: the console is
that scaffolding's user interface and none of it ships in production.

## Tier model

The runtime logs through the static `ModLogger` facade (`LivingWeapon/ModLogger.cs`), backed by
an `ILogger` (`LivingWeapon/ILogger.cs`); production impl is `FileConsoleLogger`, test-only
swallow is `NullLogger`. Since the facelift the facade surface is TYPED: every call site uses
`ModLogger.Event(verb, msg)` / `Warn(verb, msg)` / `Error(verb, msg[, ex])` / `Debug(verb, msg)`,
the two-line helpers `EventWithTrace`/`WarnWithTrace`, or a `ScopedLogger` from
`ModLogger.For(verb, armed)`. The old free-form `Log`/`LogWarning`/`LogError`/`LogDebug` entry
points exist only inside the facade's own plumbing; `LogContractTests` fails the build if any
module calls them raw.

Four tiers, `LogLevel` enum (low = more verbose): `Debug` (0), `Info` (1), `Warning` (2),
`Error` (3), `None` (4, silences the console entirely).

**The two-sink rule (the one thing worth remembering):**

- The **file** (`livingweapon.log`, rotated per launch to `livingweapon.prev.log`) gets **every**
  message, **Debug tier included, unconditionally**, regardless of the configured `LogLevel`.
  The evidence chain a live diagnosis needs is never thinner than the console.
- The **console** (the Reloaded window) only shows a message when its tier is at or above the
  configured `LogLevel` (default `Info`), and dedups repeats per battle (see above).

**Grep-habit break (facelift):** the old file shape was
`HH:mm:ss.fff [FFTLivingWeapons] DBG message`. The `DBG ` tag is GONE (grep `"[DEBUG]"` now), the
`WARNING: `/`ERROR: ` tags are gone (grep `"[WARN]"`/`"[ERROR]"`), the tag is `[Living Weapons]`
(with a space), and the timestamp moved inside brackets AFTER the tag. `livingweapon.prev.log`
files written before the facelift keep the old shape.

**Tier meanings:** Info = the match report (battle bookends, kill credits with names and victim
identity, Marks, tier-ups, attribution corrections, signature moments, the startup header).
Warning = degraded but coping (locate-miss skips, write read-back misses, corrupt-file fallbacks,
unseen enemies, lost kills). Error = something broke; an Error-tier line ALSO arms the flight
recorder's FlushOnce trigger (see the flight section: only the FIRST error of a launch produces
an archive, deliberately). Debug = file-only evidence (heartbeats, gate churn, verdict dumps,
`[trace]` id companions, the event timeline).

**The relevance gate (armed):** a signature module's Info lines reach the console only while its
weapon is EQUIPPED BY A DEPLOYED UNIT this battle; unarmed, `ScopedLogger` demotes them to Debug
(the file keeps everything). Signature modules gate on `Wielder.AnyDeployedMainHand`; the
attribution lines (pending corpses, kill expiries, coverage) gate on KillTracker's sticky
per-battle latch ("any tracked weapon resolved this battle"). Modules that deliberately tick OUT
of battle (Barrage, ShadowBlade, CharmLock's hold path, GunSlinger's roster prep) keep their
out-of-battle plumbing at Debug rather than inventing an out-of-battle gate; their player-visible
grant/proc edges are armed by construction (they only fire when a wielder exists).

**The event timeline** (`BattleLog`'s `event: damage/healing/move` lines, per-tick HP/position
diffs) is always captured to the file at Debug tier under the `[trace]` verb, in every build
flavor (a deliberate Release-behavior change from the original DEV-only const); the console stays
quiet via the Debug tier.

**Console verbosity:** fixed at Info. LW-52 removed the `VerboseLog` launcher knob and did not
replace it, so Debug lines no longer reach the console; the log FILE still records every line
including Debug, unconditionally, so no diagnostic evidence is lost. A dev who needs Debug on the
console raises `ModLogger.LogLevel` in Mod.cs.

## Event verbs (closed glossary)

The one source of truth for every log line's `[verb]` token. `LogContractTests` parses this
table and asserts it matches the `LogVerb` enum one-for-one; the doc and the code cannot drift.
The set is CLOSED: a new subsystem reuses one of these 18 verbs, or this table gets amended
deliberately; no ad-hoc per-module prefixes. The legacy column maps the pre-facelift prefixes so
`livingweapon.prev.log`-era archives stay readable.

| Verb | Legacy prefix(es) | Level discipline |
|---|---|---|
| `startup` | bare startup lines, `DEV:` seed line, both Mod.cs hooks lines | Info; launch header + its hooks footer only. |
| `config` | `config:` | Info once per launch; Warning on read failure. |
| `battle-start` | `battle: started` | Info once per battle; sentinels move to a `[trace]` companion. |
| `battle-end` | `battle: ended` | Info once per battle; THE match-report summary line. |
| `kill` | `kill:` credit lines | Info, with wielder weapon NAME + victim identity where known; ids in parens, file-only. |
| `credit` | `turn:` acting-weapon attribution, `kill:` coverage check, attribution corrections | Info only for corrections and degraded coverage (Warning); routine per-turn credit is Debug. |
| `mark` | (new: Reliquary RecordDeed earn, previously toast-only) | Info, once per earn. |
| `tier` | tier crossings (previously buried in GRANT/toast payloads) | Reserved: no per-crossing line is emitted; a crossing surfaces via the `[battle-end]` summary and toast delivery instead. |
| `grant` | `GRANT`, `barrage:`/`shadow blade:` JobCommand grant + readback lines | Info when armed; a readback MISS stays Warning-worthy. |
| `signature` | `charm-lock:` `eagle-eye:` `ricochet:` `maim:` `kobu:` `iai:` `plague:` `life-sap:` `wyrmblood:` `renewal:` `rapture:` `font:` `feign-death:` `larceny:` `benediction:` `sanctuary:` `choir:` `ultima:` `afterimage:` `puppeteer:`* | Info only under the relevance gate (a deployed unit wields the weapon this battle); everything else Debug. |
| `toast` | `banner-toast:` `prompt-swap:` | Info for delivered/dropped; queue internals Debug. |
| `save` | `kill-tally:` `legend-store:` + the new load summaries | Info for load summaries; Warning for fallback/corrupt; Error for failed saves. |
| `display` | `display:` | Info once per launch: sweep generation 1 is the liveness canary; every later generation is Debug, file-only. |
| `growth` | `growth:` locate lines | Debug (file-only at default level); ambiguous-locate refusals are Warning. |
| `turn` | `turn:` per-unit turn-finished bookkeeping | Debug (file-only): demoted off console per the no-heartbeats rule. |
| `treasure` | `treasure:` `scholar-ring:` | Info only with the Ring armed (relevance gate); the Scholar's Ring grant keeps its one Info line per session. Module slated for removal; no further investment. |
| `engine` | `engine:` tick-loop internal errors | Error, console-deduped per battle. |
| `trace` | `ev:` `wielder-search:` `show-spike:` (dev), scan/dump evidence, id companions | Debug, file-only (the console is Info-only since LW-52). |

\* Puppeteer's lines adopted the `signature` verb with the facelift conversion; its live-verify
arc continues separately.

## The launch header

Six Info lines at every launch (seven in development builds), then the hooks footer once the
loader resolves controllers. File shape shown; the console drops the `[verb]` brackets:

```
[startup] Living Weapons version 2.2.2 (production build) is starting inside fft_enhanced.exe.
[config]  Configuration loaded: TreasureAlwaysOn=False LogLevel=Info (from ...\Config.json)
[save]    The kill tally holds 63 lifetime kills across 12 weapons (kills.json, primary).
[save]    The legends hold deeds for 4 weapons and 1 Marks (legends.json, primary).
[startup] Living Weapons is tracking 118 weapon types.
[startup] The runtime loop has started.
[startup] Connected to the game's rendering hooks; toast pop-ups can be delivered.
```

Facts worth knowing: the version is read fail-soft from the deployed `ModConfig.json`
("unknown" if unreadable); the flavor is the compiled `Tuning.BuildFlavor` const (never
`build_flavor.txt`, which is BuildLinked's deploy-guard marker and can be stale/absent); the two
`[save]` lines state their load SOURCE (`primary`/`backup`/`fresh`), and a backup or fresh load
also emits a Warning; dev builds add `[startup] Development build: every weapon's tally is
seeded...` after the tally load line. A missing hooks helper mod turns the footer into a Warning (toasts die, the mod
runs); the runtime-loop line is the liveness canary that closes the header.

## The match report

What a typical armed battle prints at the default LogLevel (console shape: no verb brackets on
Info lines). The design ceiling is roughly 15 console lines for a normal battle:

```
Battle started.
All 6 enemies are accounted for; kill credit will be reliable this battle.
Gloomfang bestows Concentration on its wielder.
Windrunner claims kill number 8, felling an undead foe at (7,6).
Kiyomori struck a braver enemy (its current brave 75); the wielder's brave rises 60 to 75.
Windrunner claims kill number 9, felling a caster at (4,9).
Crediting the unit that landed the finishing blow, wielding Windrunner, rather than the unit that acted most recently.
Windrunner earns the Mark of the Requiem.
Delivered the toast "Windrunner has earned its Mark: Requiem!".
Battle ended: 2 kills credited (Windrunner 2), 1 Mark earned (Windrunner the Requiem), 0 tiers reached; 23 turns; the kill tally and legends are saved.
```

The moving parts:

- **Kill credits** (`[kill]`) name the weapon and the victim's archetype in words (`a caster` /
  `a human` / `a monster` / `an undead foe` / `an enemy` when no snapshot was captured); the
  victim's nameId/job and the weapon id ride the `[trace]` companion in the file.
- **Attribution corrections** (`[credit]`) stay on the console: the first-kill fallback, the
  charged-attack (Jump/spellcast) credit, the finishing-blow-over-live-latch credit, and the
  actor-register overrides. Routine per-turn latching is Debug.
- **The owner-flagged no-credit ruling**: `The fallen undead foe... was slain by a player carrying
  no Living Weapon; the kill is deliberately left uncredited (actor resolved via ...)` names the
  victim AND how the actor was resolved (the acted-period latch / the actor register / a
  charged-action landing / an enemy-turn team read).
- **Unarmed battles are silent**: coverage lines, pending corpses, and kill expiries demote to
  Debug when no Living Weapon is fielded (the relevance gate), so a no-mod-weapons battle prints
  only the two bookends.
- **Warnings** that survive to the console (armed): a lost kill (`The killer of the enemy at
  battle slot N could not be determined...`), an unseen pre-battle enemy, a write read-back MISS,
  a corrupt save-file fallback.
- **The `[battle-end]` summary** is composed BEFORE the per-battle counters reset: kills per
  weapon (with lifetime-derived tier crossings), Marks earned, the optional `, N kills credited
  by fallback attribution` clause, and the turn count. Zero-kill form: `Battle ended: no kills
  were credited; N turns; the kill tally and legends are saved.`
- **Both battle edges** carry their raw sentinels (`slot0`/`slot9`/`mode`/`paused`/`event`) in
  `[trace]` file companions, not on the console.

## Flight recorder (the black box)

An always-on, cheap, structured capture of on-change runtime events, surviving deploys and
launches, so the FIRST live anomaly of a session is diagnosable after the fact even if nobody was
watching the console at the time.

**Shape:** `LivingWeapon/FlightRecorder.cs` is the testable INSTANCE core -- a bounded ring
(capacity 4096, oldest-dropped) of `(elapsedMs, type, payload)` records. Every dependency (the
monotonic clock, the wall-clock provider, the file writer, the retention lister/deleter) is
injected, so the whole ring/flush/retention contract is unit-tested with no real disk or clock
(`LivingWeapon.Tests/FlightRecorderTests.cs`). `LivingWeapon/Flight.cs` is a **static null-object
facade over it, mirroring `ModLogger`'s own swappable-`Instance` idiom**: every call site
(`Flight.Record`/`RequestFlush`/`DrainPending`/`FlushBattleEnd`) is a silent no-op until
`Flight.Init(modDir)` builds the real core (called once from `Mod.cs`, right after
`ModLogger.Init`). That is what lets every pre-existing test keep passing unmodified -- none of
them call `Flight.Init`, so every `Flight.*` call inside the production code they exercise does
nothing.

**The jsonl vocabulary is FROZEN and separate from the console verbs.** The record type strings
(`"ev"`, `"kill"`, `"turn"`, `"mode"`, `"toast"`, `"legend-store"`, `"census"`, `"victim"`,
`"mark-earned"`, `"deed-miss"`, `"guard"`, ...) are set at the tap sites and were deliberately NOT renamed
by the logging facelift: `tools/parse_flight.py --grep` filters and every existing archive depend
on them. The console `[verb]` glossary above is a different namespace; do not "align" them.

**What gets captured (on-change only -- never a per-tick state dump):** battle enter/exit edges
and battle-mode changes (Engine); the event timeline (BattleLog's `event: damage/healing/move`
lines, dual-emitted alongside the `[trace]` file sink under the flight type `"ev"`); turn-clock
rising/falling edges and per-unit turn credit (TurnTracker); the engine actor-pointer's ownership
transitions (ActorRegister); the acting-player weapon latch and every corpse credit/no-credit
verdict, including the pending edge (KillTracker); tier-up/milestone toast enqueue and drop
(BannerToast); toast delivery into the facing prompt (PromptSwap); the Reliquary deed taps
(`mark-earned`/`deed-miss`); and the fingerprint guard's own lifecycle (`"guard"`, LaunchGuard):
the armed edge and a stand-down's landmark diag, so a stand-down archive is self-contained
(LW-53). Deliberately **not** tapped: Puppeteer (a separate live-verify arc is in flight against
those exact lines) and Treasure Master / the chemist-grenade paths (both slated for eventual
removal -- no new investment there).

**Where files land:** `<modDir>/flight/flight_<yyyyMMdd_HHmmss>_<trigger>.jsonl` -- one compact
JSON object per line (Newtonsoft.Json; no hand-rolled escaping). The first line of every file is
a header object (`{"hdr": true, "wall": "...", "t": <elapsedMs>}`) carrying the wall-clock time
and the recorder's own elapsedMs at flush, so a file's records can be cross-referenced against
`livingweapon.log`'s `[HH:mm:ss.fff]` timestamps (millisecond precision was preserved through
the facelift for exactly this join). Every other line is `{"t": <elapsedMs>, "e": "<type>",
"d": "<payload>"}`.

**Flush triggers:** (a) the battle-ENTER edge (`Flight.FlushBattleStart()`) and (b) the
battle-EXIT edge (`Flight.FlushBattleEnd()`, called beside `KillTally.Save()`); both flush
synchronously on Engine's own loop thread, and neither is hooked to `ResetBattleState()` (which
fires on both enter and exit). The enter-edge flush was added live 2026-07-04: three straight
sessions produced no archives at all because each ended in a process kill (the kill-and-deploy
cycle) before any exit edge fired, so the NEXT battle's enter edge is the reliable moment the
prior battle's tail can still be saved. (c) the first Error-tier line of a launch: both the
legacy `LogError` path and the typed `ModLogger.Error(verb, ...)` path arm it. An Error never
flushes synchronously -- it only raises a pending flag (`Flight.RequestFlush("error")`); the
actual serialize+write+retention-prune runs later from `Flight.DrainPending()`, called once per
Engine tick. This matters because `PromptSwapHook.Detour` logs errors on the game's own
`SetTextString` thread before forwarding -- a synchronous flush there would stall the game's own
prompt commit. (d) a dedicated `"standdown"` trigger (`Flight.RequestFlush("standdown")`,
requested LAST inside `LaunchGuard.StandDown`, LW-53): a fingerprint-guard stand-down happens
before any battle edge is reachable (`Engine.Tick`'s pre-arm early return holds every other
module off) and the launch's error FlushOnce token can already be burnt by an earlier unrelated
error (a Mod.cs hooks failure, an Engine.cs tick-loop catch) whose flush drained an empty ring;
a trigger other than `"error"` bypasses that latch entirely and the archive lands as
`flight_*_standdown.jsonl`.

**The error trigger is FlushOnce, a documented divergence from "ERROR = broke + flight flush":**
only the very first error of a launch ever produces a flight file, however many Error lines
follow; an error storm must not prune the 20-file retention into uselessness. Two invariants
the facelift deliberately preserved: `FlightRecorder.Flush` NEVER calls back into `ModLogger`
(the recursion guard: any "archived N records" notice must be emitted by Engine AFTER
`Flight.FlushBattleEnd()` returns, never from inside the flush), and `Flight.RequestFlush("error")`
stays inside `FileConsoleLogger`'s Error paths, flag-only and thread-safe.

**Retention:** after every flush, files beyond the 20 newest are deleted (oldest-first).
`BuildLinked.ps1`'s clean step preserves `<modDir>/flight/` wholesale (added to the
`Remove-Item -Exclude` list, the same treatment as `kills.json`) -- no TEMP round-trip needed
since it is just excluded outright. `Publish.ps1` needs no change: it stages the release zip from
the repo tree and never reads a deployed install's `flight/` folder.

**Two accepted loss modes (v1, by design):**
1. **Chained story battles can share one file.** If the exit edge never fires between two battles
   (a scripted battle->battle transition with no clean world-map interstitial), both battles'
   records land in the same flush file instead of two. Diagnosable (the header timestamp still
   anchors everything), just not split as cleanly as usual.
2. **A hard process death loses the in-memory ring.** The mod's `CanUnload()` returns `false` and
   there is no Reloaded unload hook wired, so nothing flushes on process exit -- there is no
   periodic partial flush in v1. Only records already written by a prior flush (battle-enter,
   battle-exit, or first-error) survive a kill; whatever was recorded since the last flush is gone.
   The battle-ENTER flush narrows this: within a session, every battle but the last-before-kill is
   archived at the following battle's enter edge, so a session that ends in the usual kill-and-deploy
   no longer loses everything -- only the final battle's tail (recorded after its own enter flush,
   with no later enter/exit edge to catch it) is lost.

**Reading a flight file:** `tools/parse_flight.py <path> [--grep TYPE]` prints a plain-text
timeline (`+N.NNNs [type] payload`, relative to the header's elapsedMs anchor), optionally
filtered to one event type (the FROZEN jsonl types above, not the console verbs). Standalone
script, no deploy-script imports.

## Reading old archives

`livingweapon.prev.log` files and console pastes from before the facelift use the old line shape
(`HH:mm:ss.fff [FFTLivingWeapons] DBG message`, module prefixes like `kill:`/`charm-lock:`); the
legacy column in the verb table above maps them onto today's verbs. The flight jsonl shape never
changed.
