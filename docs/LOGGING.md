# Logging model

One page: the tier model, the module-prefix glossary, the lines a player is most likely to see
(with a plain gloss), and where the flight recorder (stage 2 of the logging overhaul) will slot in.

## Tier model

The runtime logs through a static facade, `ModLogger` (`LivingWeapon/ModLogger.cs`), backed by an
`ILogger` (`LivingWeapon/ILogger.cs`) -- the same shape as the sibling FFTColorCustomizer mod's
ModLogger/ILogger split, ported so both mods share one logging model. The production
implementation is `FileConsoleLogger` (`LivingWeapon/FileConsoleLogger.cs`).

Four tiers, `LogLevel` enum (low = more verbose): `Debug` (0), `Info` (1), `Warning` (2),
`Error` (3), `None` (4, silences everything).

**The two-sink rule (the one thing worth remembering):**

- The **file** (`livingweapon.log`, rotated per launch to `livingweapon.prev.log`, millisecond
  timestamps) gets **every** message, **Debug tier included, unconditionally** -- regardless of
  the configured `LogLevel`. Debug-tier file lines carry a `DBG ` tag right after the timestamp so
  they're easy to grep out. The evidence chain a live diagnosis needs is never thinner than
  before this overhaul.
- The **console** (the Reloaded window) only shows a message when its tier is at or above the
  configured `LogLevel`. Default `LogLevel` is `Info` -- so Debug-tier lines (per-tick diagnostics,
  the battle-event timeline, signature verdict dumps) are file-only by default and never spam the
  console.

**The user-facing knob:** `Config.VerboseLog` (Reloaded launcher, "Verbose Diagnostic Log",
default off). Setting it true sets `ModLogger.LogLevel = LogLevel.Debug` at startup (see
`Mod.cs`), so Debug-tier lines also reach the console. It never affects the file -- turning it
off does not lose any diagnostic detail, it only quiets the console.

**Deliberate Release-behavior change:** the dev-event timeline (`BattleLog`'s `ev:` lines --
per-tick HP/position diffs) used to be a DEV-only compile-time const (`Tuning.VerboseEvents`,
`false` in Release). It is now always captured to the file (`Engine.cs` constructs
`new BattleLog(verbose: true, sink: ModLogger.LogDebug)` unconditionally) -- the black-box
evidence chain wants the data in every build; the console stays quiet via the Debug tier.

## Module-prefix glossary

Every log line starts with a module tag. One line each on what that module is:

| Prefix | Module |
|---|---|
| `battle:` | Battle enter/exit edges (Engine's BattleState). |
| `turn:` | Per-unit turn-completion / acting-weapon attribution (TurnTracker, KillTracker). |
| `kill:` | Kill credit and the enemy-identity coverage check (KillTracker, EnemyOracle). |
| `growth:` | Per-slot combat-struct locate for stat growth (GrowthEngine.Locate). |
| `GRANT` | A weapon's signature ability/support being granted or released (GrowthEngine.Signatures). |
| `ultima:` / `afterimage:` | Ultima Weapon's HP-scaled PA, Swiftedge's Speed ramp. |
| `display:` | The equip-card Kills-counter/suffix memory-sweep painter. |
| `config:` | The one-time startup echo of the resolved Config values. |
| `charm-lock:` | Galewind +3 -- held, unbreakable Charm. |
| `barrage:` | Yoichi Bow +3 -- JobCommand-injected Barrage. |
| `shadow blade:` | Sanguine Sword +3 -- JobCommand-injected Shadow Blade. |
| `eagle-eye:` | Eclipsebolt +3 -- hastened enemy Doom. |
| `ricochet:` | Stormarc +3 -- chain-lightning bounce. |
| `maim:` | Huntress +3 -- reaction suppression on hit. |
| `kobu:` | Kiyomori +3 -- one-shot brave match. |
| `iai:` | Ame-no-Murakumo +3 -- opening-turn Speed hold. |
| `plague:` | Venombolt +3 -- permanent, harder-ticking poison. |
| `life-sap:` | Umbral Rod +3 -- kill-triggered HP heal. |
| `wyrmblood:` / `renewal:` | Dragon Rod / Mending Staff +3 -- turn-edge regen splash/aura. |
| `rapture:` | Rod of Faith +3 -- low-HP emergency teleport window. |
| `font:` | Wellspring/Umbral Rod's Spiritual Font -- move-triggered HP/MP restore. |
| `feign-death:` | Wrathblade +3 -- played-dead corpse, auto-revive. |
| `larceny:` | Arcanum +3 -- steal/dispel a buff off the struck foe. |
| `benediction:` | Sanctus Staff +3 -- boosted ally heals. |
| `sanctuary:` | Staff of the Magi +3 -- fallen allies held from crystallizing. |
| `choir:` | Warlock's Staff +3 -- adjacent instant-cast aura. |
| `puppeteer:` / `puppeteer gate:` / `puppeteer-diag:` | Galewind +3 -- dominate a struck enemy (live-verify arc still in flight; its lines were carved out of this overhaul and land with that arc's own commit). |
| `scholar-ring:` | Auto-grants the Scholar's Ring so Treasure Master always has a gate item. |
| `treasure:` | Treasure Master -- auto-marks Move-Find treasure tiles. |
| `prompt-swap:` | Delivers tier-up toasts by swapping the Wait-state facing prompt's text. |
| `banner-toast:` | The tier-up toast queue (feeds prompt-swap). |
| `wielder-search:` | DEV-pulse-only diagnostic dump when a signature can't find its wielder (inert in normal play). |
| `ev:` | The dev battle-event timeline (BattleLog) -- per-tick HP/position diffs, Debug tier. |
| `show-spike:` | LWDEV-only callout-RE instrument (dev builds only, not shipped in Release). |

## The ~15 most player-visible lines

What they mean, and when they signal a real problem.

1. **`Living Weapon starting up ...`** -- the mod loaded. Missing entirely = the DLL never ran.
2. **`config: TreasureAlwaysOn=... VerboseLog=... LogLevel=...`** -- the resolved settings for this
   session. First thing to check in any bug report.
3. **`battle: started (...)`** / **`battle: ended -- saving kill tally, ...`** -- the battle
   enter/exit edges. Missing `ended`, or two in a row with no `started` between, means the battle
   boundary detection is stuck -- kill tallies may not save.
4. **`turn: a unit finished its turn (#N this battle) [...]`** -- per-turn bookkeeping. Volume-heavy
   but harmless; only worth reading when diagnosing a specific mis-credit.
5. **`kill: <Weapon> earns kill #N (enemy fell at x,y)`** -- the actual kill credit. This is the one
   line to watch when verifying a weapon's tally is climbing correctly.
6. **`kill: all N enemies accounted for -- kill credit will be reliable this battle`** -- healthy
   coverage; kills this battle can be trusted. **`kill: only M/N accounted for so far ...`**
   repeating past the first few seconds of battle is the tell that something is wrong (see the
   paired `WARN` line above it for which enemy).
7. **`GRANT <Weapon> -> <Ability> ... readback=SET`** -- a weapon's signature ability successfully
   armed this battle. `readback=MISS` means the write failed -- the signature is NOT active despite
   the log line existing; report it. `WARN build-time-only support` means the ability can never
   work live -- a design bug, not a runtime failure.
8. **`display: memory sweep #1 finished -- maintaining N card-text spots`** -- the equip-card Kills
   counter painter is working. This is the per-launch canary; if it never appears, kill counters
   won't paint on any card. (Later sweep generations are Debug-tier -- turn on VerboseLog to watch
   them too.)
9. **`<signature> ACTIVE -- ...`** / **`... inactive`** (charm-lock, barrage, eagle-eye, ricochet,
   maim, plague, life-sap, wyrmblood, renewal, rapture, font, feign-death, benediction, sanctuary,
   choir) -- a +3 weapon's signature turning on/off as it's equipped/unequipped or crosses its kill
   tier. The expected "is my weapon's gift live" check.
10. **`scholar-ring: granted (you had none)`** -- once per session; explains a Scholar's Ring
    appearing in inventory unprompted (Treasure Master's gate item).
11. **`treasure: no Scholar's Ring equipped -- module idle ...`** -- once per battle; Treasure
    Master's discoverability hint when nobody in the party carries the ring.
12. **`treasure: map N ... armed -- M tile(s)`** -- Treasure Master found and marked the map's
    Move-Find tiles.
13. **`banner-toast: queue at cap (N) -- dropped stale toast ...`** -- rare; a tier-up toast was
    dropped because too many piled up unseen. Explains a "missing" toast.
14. **`prompt-swap: delivered "..." (holder=0x...)`** -- a tier-up toast actually rendered in the
    facing-prompt slot. The player-visible confirmation the toast pipeline fired.
15. **`startup failed -- Living Weapon will not run: ...`** / any `ERROR:` line -- always worth
    reading; these are the blanket-KEEP Log.Error sites and never demoted.

## Flight recorder (placeholder -- stage 2)

A future stage adds `LivingWeapon/FlightRecorder.cs`: an always-on, bounded in-memory ring of
on-change events (battle edges, turn/kill credit, actor-pointer transitions, signature verdicts,
toast delivery), flushed to `<modDir>/flight/flight_<timestamp>_<trigger>.jsonl` on a battle-exit
edge or the first `ModLogger.LogError` of a launch. Purpose: the FIRST live anomaly of a session
is diagnosable after the fact even if nobody was watching the console at the time. Not yet built;
this section is a placeholder so LOGGING.md doesn't need a structural rewrite when it lands.
