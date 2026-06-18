# The Living Weapon Journey

Building the **Living Blade** — a sword that counts its own kills, displays the tally on itself, and
physically evolves as you fight with it. Prototyped in **FFTHandsFree** (a live companion DLL + Python),
data shipped in **FFTLivingWeapons** (pure-data mod). FFT: The Ivalice Chronicles (Denuvo, UE5, fixed base
`0x140000000`).

> Session journal, 2026-06-05. The MVP works END-TO-END live: kill with the blade equipped → counter ticks
> on the blade in-game → at 10 kills it auto-upgrades to "Living Blade+" in your equip slot. This doc
> records HOW, and every dead end we ruled out, so we don't re-walk them.

---

## TL;DR — what works, and the load-bearing facts

- **Kill attribution** — credit by the **CORPSE's team** (its static-array slot offset), NOT "who's active
  when the corpse is detected." That was the whole battle; see §1.
- **Growth = item-swap between pre-baked ids**, NOT altering one id in place. A weapon's stats are frozen at
  boot; the only runtime lever is *which* id is equipped. See §3 — this is THE answer to "why can't we just
  make one weapon stronger."
- **In-card live text** (the `Kills: NNN` counter) — the loaded description string IS reachable in plain
  memory (both UTF-8 and UTF-16) and overwriting it re-renders the menu. See §4.
- **Memory writes survive Denuvo** (plain WriteProcessMemory; it's HW-breakpoints/page-guards that crash).
- **In-battle unit stats lock at battle start** — roster/equip changes mid-battle take effect NEXT battle.

---

## 1. Kill attribution — the hard part (3 rounds)

The goal: when the player kills an enemy with the Living Blade equipped, log `killerTeam=0 killerWeapon=<id>`.
The tracker (`BattleTracker.cs`) polls the static battle array (`0x140893C00`, stride `0x200`) every 100ms.

**Round 0 — death detection.** Original detection was *transition-based* (catch the single poll where hp
crosses 0); a victim that MOVED or whose maxHp flickered right before dying re-inited the slot and swallowed
the crossing. FIX: **state-based** — a KO'd corpse persists at hp<=0 for several turns, so credit it exactly
once the first poll we see it dead (`TrackedSlot.DeadCredited`). Robust.

**Round 1 — weapon resolution race.** Attribution stamped "who's active" each poll, incl. transitional
between-turn junk (garbage nameId that matches no roster slot → weapon unresolved = -1). Corpses are spotted
1-2 polls AFTER the blow, by which point the snapshot was junk. FIX attempt: only refresh the snapshot on a
VALID read (player needs a resolved weapon; enemy needs sane stats). Worked ONCE (a lucky-timing Chirijiraden
kill) → we wrongly declared victory.

**Round 2 — the systematic failure.** Live testing: **4/4 deliberate player kills logged `team=1 weapon=-1`**
(credited to an ENEMY). Root cause: **the game rotates the active-unit pointer to the NEXT unit in the turn
order — almost always an enemy — the instant an action finishes**, but the corpse isn't detected until a poll
or two later. So "active at corpse time" is reliably the next enemy, never the killer.
  - Tried an **`acted==1` gate** (only refresh on a unit that has acted, to hold it across the rotation
    window). STILL failed on a PD-on-undead kill — long animation → the death registered so late an enemy had
    already acted by then.

**Round 2 FIX — corpse-team attribution (THE WINNER).** Stop asking "who's active." Use a signal that can't
lag: **the corpse's own team.** In the static array, player units sit at positive slot offsets (>=1), enemies
at <=0. So an **enemy slot dying => a player killed it** => credit `_lastPlayer*` (the last player who
completed an action with a resolved weapon). A player dying => enemy killed it => team 1, uncredited.
Detection lag is now irrelevant — the corpse's slot tells us the killer's team no matter how late we look.
  - Code: `BattleTracker.cs` fields `_lastPlayerNameId/Job/Weapon`, updated only when
    `acted==1 && team==0 && weapon>=0 && sane stats`; the `corpseIsEnemy = (s - ArraySlotsBack + 1) <= 0`
    block in `PollStaticArray`.
  - RESULT: every player kill now logs `killerTeam=0 killerWeapon=31`. Verified live 4/4 then 11/11.

**Known limitation (accepted):** counter/reaction kills happen on the ENEMY's turn (enemy is the active
unit), and over-credit edge cases (enemy-on-enemy, friendly fire, a stale last-player-actor for a DoT death)
can mis-credit. All rare; acceptable for a flavor counter.

---

## 2. The data layer — where the weapon lives

**Hard constraints (from the item-cap work, see ITEM_CAP_261_BREAK_JOURNEY.md):**
- **ZERO free item slots.** Ids 0-260 are all used (234 equippables + consumables + 5 DLC reclaims).
- **Ids > 260 CANNOT be equipped** — weapon stats live in an encrypted, 261-sized table; force-equipping a
  >260 id crashes. This is the genuine Denuvo floor.

**DLC slots (256-260) were a TRAP.** They're unsold-by-default (great for hidden tiers) and ARE within the
cap, so the idea was to host the Living Blade there. But they **did not render in the inventory** even with
counts set — Akademy DLC slots appear gated behind DLC ownership / the boot-built display registry. Abandoned.

**SHIPPED: real Sword slots (repurposed existing weapons).** Since there are no free slots, each tier eats an
existing sword:
- **id 31 (was Nagrarok)** → **"Living Blade"** base — WP 16, evade 12, no rider.
- **id 29 (was Rimebrand/Icebrand)** → **"Living Blade+"** — WP 18, evade 12, EquipBonus row 21 (+1 PA).
- Both `category: Sword` (swap-safe), flagged `"livingWeapon": true`.
- **Gate exemption:** `tools/analyze.py` skips `livingWeapon` items (a growing item breaks the static
  no-domination invariant). PA+1/+2 reuse existing EquipBonus rows 21/24 (no new rows needed).
- Names/descriptions: `tools/patch_names.py` (items.json `name`/`desc` → `Item-en` sqlite → FF16Tools
  `sqlite-to-nxd` → `item.en.nxd` → deploy). Verified in-game: WP 16/18 + correct names.

---

## 3. Growth model — WHY two ids (swap), not one (alter in place)

This is the question that keeps coming back: *"can't we just make the one existing sword get stronger,
instead of swapping between two ids?"* **No — and here's the mechanism, proven this session:**

A weapon's power (WP, PA rider) comes from its **catalog entry**, which the game reads **once at boot** into a
**resolved cache**. To change a weapon's stats at runtime you'd have to change that — and every path is walled:

- **(a) Edit the catalog live** → dead. The cap-break work proved post-boot catalog writes are *too late* (the
  resolved cache is already built). And `catalog_poke_test.py` showed the catalog isn't even a simple
  pokeable array — the obvious base reads as pointer noise; the real per-id catalog is reached through an
  **encrypted dispatcher** (`0x1402EEC94` → Denuvo blob). Can't find it, and writing it wouldn't propagate.
- **(b) Write the unit's resolved stats live** → dead. `pa_poke_test.py`: the battle-array PA fields (`+0x22`
  total, `+0x26` raw) are a **scratch copy** — `+0x22` recomputes every turn; writing `+0x26` did NOT move
  damage; exiting battle wipes both. The **roster has no PA byte at all** (PA is computed: RawPA × job-mult).
  There is no single number to poke that changes damage.
- **(c) Edit the catalog BETWEEN battles** → still dead. The resolved cache is built at BOOT, not per-battle.
  Battle stat-resolution reads that cache, not the catalog. So even between battles, a single id's stats are
  frozen until the next game restart.

**Therefore: a single id's power is FROZEN at its boot-resolved value. Nothing at runtime changes it.**

**The item-swap chain works precisely because it sidesteps all of that.** Every tier (id 31, id 29, …) is a
separate item whose stats were baked at boot like any other. Growth = the companion rewrites the wielder's
equip slot (`roster +0x14`) to point at a stronger, already-baked id. The engine re-resolves stats from the
equipped id at the next battle → the new (boot-baked) stats apply. We change *which id is equipped*, never an
id's stats. (The equip-slot write is the one mutable, proven lever, same as the description text and kill
count.)

**The player never sees "two weapons."** The swap is in-place on the same equip slot — the blade's name and
stats just change. The two ids are invisible plumbing. The ONLY real cost of two ids is **item-slot
scarcity** (each tier repurposes one existing sword) — which is the actual reason to wish for one id, but
it's a budget problem, not a mechanism problem.

**Rule of thumb:** anything baked at boot (stats) is frozen; anything we write live (equip-slot id,
description text, kill count, statuses) is mutable. Weapon power is in the frozen bucket → must be swapped.

---

## 4. The in-card live `Kills: NNN` display

The description is baked in `item.en.nxd` (static at boot). To show a LIVE count we overwrite the loaded
description **string in RAM**.
- `search_desc.py`: the loaded description IS reachable in plain memory — found "Living Blade" / "hungers" /
  "borrowed strength" in **both UTF-8 and UTF-16**, contiguous name+desc blocks (e.g. id31 ~`0x4D57B2DDxx`
  utf8 / `0x15AE54xxx` utf16). (The cap-break journal couldn't find item *names* this way; descriptions /
  our strings were findable.)
- `poke_test_desc.py`: overwriting "hungers"→"KILLERS" in those copies **re-rendered the in-game item card**.
  Proven.
- Implementation: bake a **fixed-width `Kills: 000`** placeholder into each tier's description (so the digit
  positions are stable, no length-change overflow), and the companion overwrites the 3 digits with the
  zero-padded count. Multiple copies exist (UTF-8 store + UTF-16 + render copies); overwrite all that match
  `Kills: ` + 3 digits.
- Addresses move per boot → must SEARCH each session (cache the hits, re-scan ~every 10s to catch
  freshly-rendered menu copies). The brute-force per-update scan (~3s over ~3.7GB) is why early updates
  "lagged"; caching the anchor addresses makes each write instant.

---

## 5. The companion (`FFTHandsFree/living_weapon_companion.py`)

One Python loop, ctypes against the live process:
1. Tails the BattleTracker kill log; counts kills where `team==0 && weapon in {31,29}` into one cumulative
   counter (maps all tier-ids to the logical weapon). Persists to `%TEMP%/living_weapon_blade.json`.
2. Writes `Kills:NNN` onto the blade (cached anchors + periodic re-scan).
3. Swaps the equip slot at thresholds: `LADDER = [(0,31),(10,29)]`; at 10 kills rewrites any roster `+0x14`
   holding a tier-id to the target tier. The stat change defers to next battle (engine locks stats at battle
   start) — which is the desired "no mid-fight stat swap" behavior, for free.

---

## 6. Probe scripts (in FFTHandsFree/) — reusable

- `pa_poke_test.py` — proves battle-array PA writes don't affect damage (ruled out direct stat write).
- `catalog_poke_test.py` — proves the catalog isn't a flat pokeable array (ruled out in-place catalog edit).
- `search_desc.py` — finds loaded description strings in memory (both encodings).
- `poke_test_desc.py` — proves overwriting a loaded description re-renders the menu.
- `give_living_blades.py` — sets inventory `count[id]` (`0x1411A17C0 + id`) to spawn items for testing.
- `update_kills_display.py` — standalone `Kills:NNN` writer.

---

## 7. Status & remaining polish

**WORKS END-TO-END (MVP).** Attribution, counter, live in-card display, auto tier-swap — all verified live.

Optional polish (none blocks the feature):
- Swap path is a few seconds slow (the display re-scan blocks it) — reorder the companion's `apply()` to
  swap-first.
- Battle-end gate the swap (currently fires on the kill; engine defers the effect anyway, so cosmetic).
- Hide id 29 from shops/drops so the +1 isn't separately obtainable.
- Move-Find / treasure placement for the base (id 31) so a real playthrough finds it.
- More tiers (+2/+3): each needs one more sacrificed sword id (no free slots).
- Port the finished mechanic into FFTLivingWeapons proper; then the rest of the Living Set
  (armor→hits→HP, ring→magic-dmg→MA).

---

## 8. Scaling to ALL weapons (the real vision) — needs in-place growth

The actual goal: **every weapon** grows as you kill with it, not just one blade. This kills the swap
approach dead: 121 weapons × N tiers >> the 261-id ceiling, and ids > 260 can't be equipped. So **"all
weapons" REQUIRES in-place growth (no per-tier ids).** That makes cracking in-place worth real RE.

**Ruled out (the easy in-place targets):**
- Static-array PA write (`+0x22`/`+0x26`, 0x140893C00) — a scratch copy; writing it did NOT move damage.
- Live catalog edit — resolved cache built at boot; catalog behind an encrypted dispatcher.

**NOT ruled out — two real paths (both cap-break-scale RE):**
- **Path A — find the REAL resolved-stat write target.** The static array was the wrong copy. The value the
  damage formula actually reads (resolved effective PA, likely in the **heap combat unit struct** or the
  roster's undecoded **raw-stat block**) is UNTESTED. If found+writable: the companion sets each unit's
  attack = base + (their currently-equipped weapon's kill bonus) between battles. Per-weapon, no ids.
- **Path B — HOOK the damage-calc function [recommended].** Reloaded.Hooks function detours are Denuvo-SAFE
  (proven by the cap-break work — `CatalogRedirectHook`, `calllog_*` fire without crashing). Hook the damage
  formula, read the attacker's equipped weapon id + that weapon's kill count (the in-process BattleTracker
  already has both), add a bonus at calc time. **ONE hook covers ALL weapons** — no ids, no stat-writes,
  no per-tier data. Cleanest fit for the vision.

**The cost (honest):** both need finding the right code/data, and the two clean dynamic-trace techniques
(page-guard, HW breakpoint) CRASH under Denuvo (see ITEM_CAP journal). So it's disassembly + targeted
function-hook probing (we have `get_func_entries.py` 77k-entry map + `calllog_*`) or external Cheat Engine
(kernel debugger evades Denuvo). Multi-session. But it's a known-shape problem, not a wall.

**Recommendation:** Path B (damage hook) is the target architecture for "all weapons grow." Find the damage
function, hook it, add `bonus(weaponId, killCount)`. Path A is a useful fallback / stepping stone.

### Research progress (2026-06-05 session 1)
- Tooling confirmed live: bridge memory verbs + `GenericCallLogger` (`calllog_arm/status/release`, the
  Denuvo-safe detour) all work. `strict 0` needed before each dev verb (strict mode re-asserts on screen
  changes).
- **Stat resolver `0x140280898` confirmed a valid entry** (prologue `push rsp; push r14; push r15;
  sub rsp,0x20; mov rbx,rdx; mov rdi,r8; mov rsi,rcx; ...` — takes 3 pointer args in rcx/rdx/r8, builds the
  0x280 struct). Hooked it; no crash.
- **BLOCKED catching it:** it's **battle-SETUP-only** (`calls=0` after an in-battle Attack), AND the hook
  **releases on the world→battle load transition** (armed at the world map → "NOT ARMED" once in battle;
  re-arming in-battle works but the setup resolve already ran). In-battle menu transitions do NOT release it.
  To catch the setup resolve we'd need the hook to persist through battle load (a `HookHost` lifecycle fix)
  or a re-trigger (`battle_retry`, untested — also a reload).
- The per-attack DAMAGE function (Path B) needs candidates we don't have; "find-what-writes-HP" is the way
  to get them, and that **crashes in-process under Denuvo** (page-guard/HW-bp). → **Same wall as the
  cap-break hunt. The clean finish is external Cheat Engine** (kernel debugger evades usermode anti-debug).

### THE CHEAT ENGINE RECIPE (fast path to the damage function — needs a human at CE)
1. CE → attach to `FFT_enhanced`, in a battle.
2. Scan an enemy's current HP (4-byte: scan value → damage it → next-scan the lower value → repeat to 1 addr).
3. Right-click the HP address → **"Find out what writes to this address."**
4. Attack that enemy → CE logs the instruction that writes HP = the damage-**apply** site.
5. Note its address; the enclosing function is damage-apply. Trace 1-2 calls up the stack for the damage
   **calc** (PA × Br/100 × WP).  (Path A variant: scan a unit's PA value / "find what accesses" the resolved
   struct → the resolved-stat write target.)
6. Hand the function address back → we `calllog_arm` it to confirm it fires once per attack, then add
   `bonus(weaponId, killCount)` in a Reloaded.Hooks detour. That hook = "all weapons grow," done.

### Research session 2 (2026-06-05) — CE find-what-writes, traced the HP chain
CE find-what-writes on an enemy's HP (2-byte) walked a **copy chain**, not the damage:
1. HP heap copy `0x...A7C` ← `movups [rcx+20],xmm1` @ `0x14028D2D7` — a bulk 16-byte struct memcpy
   (propagation, fired per-frame).
2. ← **`SetHP` setter @ `FFT_enhanced.exe+E418C` (`0x14000E418C`)**: at `E41A5` `mov esi, r8d` → HP arrives
   as the **3rd argument (r8d)**; the fn stores HP/maxHp into a **heap battle-state container** (base
   ~`0x4366357948`, unit HP at `+0x10060 + r10` unit-offset). So damage is computed by the **caller** and
   passed in.
3. `scan_callers(0x14000E418C)` = **0 plain callers** → the caller is **encrypted / vtable-dispatched**
   (same wall as the cap-break render-loop hunt). Static caller-tracing is blocked.

**CONCLUSION: the damage calc sits behind encryption/indirection.** The in-process + static-scan toolchain
runs out here, exactly as the cap-break journal predicted for this class.

**RESUMPTION OPTIONS (fresh session):**
- **(a) Path A via the heap container `0x4366357948` [most promising, CE-doable].** This is NOT the static
  array we ruled out. Find a unit's PA/WP field in it (HP is at the unit sub-struct `+0x60` region; PA/WP at
  other offsets), write it boosted-by-kills, and test if damage changes. If the damage formula reads stats
  from this container, that's in-place growth — no damage-fn needed.
- **(b) CE breakpoint on `SetHP` (`E418C`) + read the call stack** to name the caller. Caveat: SetHP looks
  periodic (write fired ~8×/trace), so filter to the damage call (the one where HP actually drops).
- **(c) Ghidra/IDA data-flow** on the plain `.reloc` code (`0x140001000`–`0x14060C000`) to map the damage
  path. The heavy-but-definitive route.

### Research session 3 (2026-06-05) — Path A CONCLUSIVELY BLOCKED
Found the heap unit struct in the battle container (`0x4366367A1C`, the `SetHP` target): decodes to a real
unit-info struct — name `"Ramza"` at +0x14, **HP +0x60, maxHP +0x62, MP +0x64, Brave +0x6A (97),
Faith +0x6C (75)**, level, speed. **But NO WP and NO resolved PA** — those are *computed at damage time*
(PA = raw×job×equip; WP straight from the weapon catalog), not stored fields.
- **TEST:** dropped Brave 97→10 here (Brave is a real formula input: `dmg = PA × Brave/100 × WP`). Write
  held. Attacked → **damage UNCHANGED.** So this struct's Brave is a DISPLAY/SYNC copy too, not what the
  formula reads.
- **VERDICT:** combined with the earlier static-array PA write (also no effect), **EVERY writable stat copy
  we can find is downstream display/sync** — the damage formula reads its inputs from a source we can't
  locate or poke. **In-place stat-write growth is DEAD via memory-poking.**
- **The only remaining route to true "all weapons grow"** is **(c) Ghidra/IDA**: statically map the damage
  calc in the plain code, find+hook it, add `bonus(weaponId, killCount)`. Serious multi-day RE.
- **Pragmatic alternative that works TODAY:** the swap approach (proven) scales to a **curated set** of
  living weapons (one per class / the iconic ones) within the ~free-slot budget — not all 121, but real.
