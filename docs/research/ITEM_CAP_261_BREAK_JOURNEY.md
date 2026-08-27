# The 261-Item-Cap Break Journey

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

Final Fantasy Tactics: The Ivalice Chronicles (FFT IVC, Denuvo-protected, UE5,
preferred base `0x140000000`, no ASLR). Goal: **display & equip items at ids ≥ 261**,
beyond the engine's hard 261-entry item catalog (ids 0–260).

> This doc was reconstructed 2026-06-04 after the original was lost (never
> committed). It merges the prior multi-session research (captured in the
> FFTHandsFree project memories `project_item_cap_break_session_2026_06_03`,
> `project_item_cap_layers_2026_06_03`,
> `project_item_cap_lua_wall_confirmed_2026_06_03`) with a fresh live
> investigation on 2026-06-04 driven through the FFTHandsFree bridge.

---

## TL;DR — current status

- **The reclaim win ships today:** ids **257/258/259** (Mirror Edge / Phoenix
  Crown / Wyrmscale Vest) and the buffed **260** (Cursed Ring) work fully —
  equip, render, survive save/reload — because slots **256–260 are real Akademy
  DLC entries** that exist in *every* boot-built table. That is the only road to
  "extra items" that works right now (5 reclaimable DLC slots, all used).
- **Going past 261 is blocked at the DISPLAY layer.** Data-layer cap-break is
  solved (catalog redirect, 8 cap sites lifted to 512, inventory write recipe).
  But the PartyMenu item list is gated by a **boot-built item registry / resolved
  cache** that only contains ids 0–260. Items at 261+ exist in inventory data but
  are filtered out of the rendered list.
- **Realistic effort to finish:** still a 2–3 day reverse-engineering project.
  The wall is finding + injecting into that boot-built registry.

---

## The cap is enforced in many layers (the map)

### Catalog tables (live addresses, byte-verified 2026-06-04)
- **Main block** `0x140808740` — 256 entries × 12 bytes (ids 0–255).
- **Extended block** `0x14067B470` — **5** entries × 12 bytes (ids 256–260).
- Past the extended block, `0x14067B4AC` onward is **adjacent table / vtable
  pointer data** — overwriting it crashes PartyMenu render.

Live dump of `0x14067B470` (ids 256–261), 2026-06-04:
```
256 @470: 0D 15 61 82 20 03 00 54 0A 00 01 00   Sword (Materia-Blade twin, addData 0x20)
257 @47C: 0D 0C 08 82 14 03 00 51 0A 00 01 00   Mirror Edge  (Sword, addData 0x14)  ← renders as "Akademy Blade"
258 @488: 03 5F 05 12 10 15 00 52 0A 00 01 00   Phoenix Crown (Hat 0x15)
259 @494: 0D B2 06 12 1F 18 00 53 0A 00 01 00   Wyrmscale Vest (Clothing 0x18)
260 @4A0: 0C 8F 33 0A 0B 1C 00 50 0A 00 01 00   Cursed Ring (Ring 0x1C)
261 @4AC: 3A 05 00 00 01 8E 01 FF 10 0A 00 00   ← NOT item data. Adjacent struct/vtable garbage.
                                                   TypeFlags byte (+3) = 0x00.  THE CATALOG ENDS AT 260.
```

### ITEM_COMMON_DATA struct (12 bytes, Pack=1)
```
+0x00 Palette          u8
+0x01 SpriteID         u8
+0x02 RequiredLevel    u8
+0x03 TypeFlags        u8  (Weapon=0x80, Shield=0x40, Armor=0x10, Accessory=0x08, Consumable=0x04, Rare=0x02)
+0x04 AdditionalDataId u8  (index into the category sub-table for weapon stats etc.)
+0x05 ItemCategory     u8  (Knife=0x01, Sword=0x03, Hat=0x15, Clothing=0x18, Ring=0x1C, ...)
+0x06 unused           u8
+0x07 EquipBonusId     u8
+0x08 Price            u16 LE
+0x0A ShopAvailability u8  (Chapter1_Start=0x01, ...)
+0x0B unused           u8
```

### The 8 cap sites (all lifted 261 → 512, live-verified patched this boot)
`05 01 00 00` (261) → `00 02 00 00` (512):

| Address | Role |
|---|---|
| `0x1402B9726` | equip-sanitizer |
| `0x1402833EC` | id-validator |
| `0x1402805A0` | inv-bound |
| `0x140281CA9` | catalog-search |
| `0x140282173` | category-search |
| `0x1402D1F5F` | inv-full-check |
| `0x140152CC5` | items-display-iter (lives in the UI list handler at `0x140152CC0`) |
| `0x140281D63` | dynamic-cap calc imm8 `lea r10d,[r11+6]`: 0x06 → 0x7F |

These are auto-armed at boot by the bridge's `CatalogRedirectHook.LiftCapSites`
and survive Denuvo. Confirmed live 2026-06-04: the display handler at
`0x140152CC3` reads `cmp esi, 0x200` (the lifted 512).

### Inventory count array (live-verified 2026-06-04)
- Base **`0x1411A17C0`**, indexed by id: `count[id] = byte[0x1411A17C0 + id]`.
  Verified via the display disasm `lea rcx,[rip+0x104eaee]` @ `0x140152CCB`
  → resolves to `0x1411A17C0`.
- id257 count @ `0x1411A18C1`; id261 count @ `0x1411A18C5`.
- Write recipe (sticks, no resync needed): `wv 0x1411A17C0+id <count>`
  (bridge `write_byte`).
- **Roster base** is `0x1411A18D0` (= base+0x110) — a *different* structure
  (unit data, slot stride 0x258). Most refs into this region are roster code,
  not inventory counts.

### LuaState (confirmed live 2026-06-04)
- Global at `0x143CD34F0` = heap ptr `0x418761A580`; `0x143CD3500` = `0x4177F02C00`.
  Sane, walkable, runtime-decrypted heap pointers.

---

## What's PROVEN DEAD (do not re-attempt)

- **Catalog relocation / in-place growth.** Modloader (`fftivc.utility.modloader`,
  Nenkai, open-source w/ local PDB) is 100% in-place overwrite — AOB-scans the two
  fixed blocks, wraps in `FixedArrayPtr` of native count (256 + 5), writes fields
  in place. `NumEntries=261` (FFTOItemDataManager.cs) is a guardrail against the
  vtable crash. **Zero** VirtualAlloc / relocate / repoint infra. A fork saves ~20
  lines of XML plumbing, none of the hard part.
- **No game-side pointer to flip.** Every qword pointer to either catalog base in
  memory is modloader-owned (`.NET FixedArrayPtr` objects) or an image copy.
  ZERO game-side pointers → the game computes catalog base inside Denuvo-encrypted
  backing functions. Can't repoint. (Earlier "0 pointers" was a scanner bug —
  `OpenProcess(VM_READ only)` → `VirtualQueryEx` returns 0; must use `0x0410`.)
- **Geometry is packed both ends** — no in-place room to grow either block.
- **Repurpose-unused-slots is EXHAUSTED.** Ids 256–260 are the only "DLC" slots;
  all 5 are used. No free vanilla slots.
- **`0x1405D0D78` is NOT the item-display gate.** (See ruled-out below.)
- **NXD name presence alone is insufficient** — a prior NXD `item.en.nxd` patch
  adding a `Moonblade` id=261 row did NOT make it render.

---

## What's BUILT (the bridge tooling, in FFTHandsFree)

All in `paxtrick.fft.handsfree` (deployed DLL is the cap-break build). Hooks
auto-arm at `HookHost.SetHooks` (pre-boot, before the engine's table build):

- **`CatalogRedirectHook`** (`Hooks/CatalogRedirectHook.cs`) — detours the catalog
  backing fn `0x1402EEC94`; serves ids 261–516 from a managed 256-slot buffer
  pre-seeded with Mirror-Edge clone bytes; lifts the 8 cap sites. Verbs:
  `catalog_status / catalog_set_entry / catalog_get_entry / catalog_clone_from`.
- **`PageGuardWatcher`** (`Watch/PageGuardWatcher.cs`) — VEH+PAGE_GUARD
  "find-what-accesses". Verbs: `write_watch_arm / write_watch_status /
  write_watch_release`. **⚠ See the crash warning below.**
- **`ItemLuaLookupHook`** (`Hooks/ItemLuaLookupHook.cs`) — detours `0x1405D0D78`,
  auto-armed inject ON. **Inert** (the inject never fires — see ruled-out).
- Memory verbs: `read_block` (Address hex no-`0x`, BlockSize→BlockData),
  `read_bytes`, `batch_read`, `search_bytes` (AoB), `write_byte` (`wv`),
  `write_address`.

Driving: `source ./fft.sh` then `boot` / `scan_inventory` / etc., or write raw
JSON to `…/FFTHandsFree/command.json` and read `response.json`.
Screenshots (ground truth): `./screenshot_crop.ps1`.

---

## 2026-06-04 live investigation — new findings

Driven through the bridge against a maxed test save (Ramza Lv99, 2.5M gil).

1. **The wall reproduces with the FULL hook stack active.** With cap→512,
   catalog-redirect serving 261, and lua-inject ON, I set `count[261]=7`,
   `count[262]=8` and opened PartyMenu → Inventory → Weapons. Screenshot: the same
   7 weapons (3 pages of ids ≤ 260). **ids 261/262 do not render.** So the wall is
   not fixed by any current data-layer lever.

2. **`0x1405D0D78` is DEFINITIVELY ruled out as the gate.** Live capture during a
   real weapons render: `totalCalls=411392, observed=0` (zero calls with an
   item-id key in [255,516]). The one capture was `key=49, a0=0x24`. And
   `0x1400259E4` (which feeds the call) just does `lea rax,[rip+…] → 0x143CD34E8`,
   then `mov rcx,[rax]` = `*(0x143CD34E8)` = **`0x24`** — a non-pointer. So
   `0x1405D0D78` is a generic `luaH_get(table=0x24, key=49)` called all over;
   the `0x140025E64 → 0x1405D0D78` static trace is a **confirmed red herring**
   (one of the "3 wrong gate" traces the prior session warned about).

3. **⚠ `PageGuardWatcher` CRASHES the game on hot pages.** Arming a guard on the
   inventory-count page (`0x1411A1000`) while the menu was rendering instantly
   killed `FFT_enhanced.exe` (Denuvo/VEH fault storm — the exact failure the
   watcher's own doc warns about). **The prior plan to "find-what-accesses the
   catalog/cache at boot" via the in-process page-guard is OFF THE TABLE.** Use a
   targeted Reloaded.Hooks logging detour or external Cheat Engine (kernel
   debugger evades Denuvo's usermode anti-debug) instead.

4. **Full map of inventory-count readers (safe static LEA/MOV scan, `scan_count_refs.ps1`).**
   15 functions reference the count base `0x1411A17C0`:
   `0x1400FFE7A, 0x1400FFF08, 0x140152CCB, 0x1401F36A3, 0x140205FAA, 0x140207F0E,`
   `0x140275BE4, 0x140275DDE, 0x14028068F, 0x1402CAA17, 0x1402CB2EF, 0x1402D1E71,`
   `0x140303B7A, 0x14030B27A, 0x14030B5A2, 0x1403914C1`.
   - **None has an unpatched fixed id-cap** (`cmp …,261/260`); the caps present
     are dynamic (`lea eax,[rcx-1]`, `lea r10d,[r11+2]`). → the wall is a
     **registry/validity check, not an iteration cap**. No quick cap-patch exists.
   - Several are **not** the render: `0x140205FAA`/`0x140207F0E` are a bulk
     `movups` struct memcpy (inventory-state snapshot/restore); `0x1400FFE7A` is
     init. `0x140152CC0` is *a* UI list handler but its per-item path went through
     the `0x1405D0D78` red herring and did not gate the weapons list.

5. **This boot's NXD has no id261 name** — `search_bytes` for "Moonblade"
   (UTF-16 and the name table) returns 0 matches; the deployed `item.en.nxd` is a
   clean build without the prior session's id261 row. Note: item names are NOT
   stored as plain UTF-8/UTF-16 (searching "Akademy Blade" both ways = 0 matches),
   so the UE5 text system can't be content-searched naively.

6. **4 more unpatched `cmp r32, 261` cap sites exist** (the prior 8-site lift only
   covered specific encodings; `scratch_search_imm32.ps1 -Imm 261` finds these):
   `0x140280643` (cmp ebx — inventory maintenance loop, zeroes invalid counts),
   `0x140284B54` (cmp edi), `0x140284EEE` (cmp ebx — counts items per category),
   `0x140392453` (cmp edi — indexes a sorted word-table). All iterate id 0..260.

7. **Catalog geometry — there is room for EXACTLY ONE more entry (id261).**
   Live dump of `0x14067B470`+: after the 5 entries (256–260), `0x14067B4AC` holds
   ~20 bytes of "adjacent table data" (`3A 05 00 00 01 8E 01 FF 10 0A 00 00 01 8E
   01 FF 06 0A 00 00`), then at **`0x14067B4C0`** an array of live 8-byte pointers
   (`0x140712EC0, 0x140712EE8, 0x140712F60, …`). id261's 12-byte slot (`0x14067B4AC`
   ..`0x14067B4B7`) fits **before** the pointer array; id262's would overlap the
   first pointer → crash. So a single extra catalog entry (id261) is physically
   placeable; id262+ are not, without relocation.

8. **THE DECISIVE EXPERIMENT (and result).** Live-patched, post-boot:
   id261's catalog slot `0x14067B4AC` = id257-clone bytes; the 3 display-subsystem
   caps `0x140284B56/0x140284EF0/0x140392455` lifted 261→**262** (surgical 1-byte
   `05→06`, to include only id261, not the pointer garbage); `count[261]=5`.
   Patches held (Denuvo did not revert). **Result: id261 STILL did not render** in
   the weapons inventory (verified across all pages by screenshot).
   → **This is the key finding:** giving id261 a valid catalog entry *after boot*
   is insufficient. The render is driven by the **boot-built resolved cache**,
   which was already built (from the 261-entry catalog) before the post-boot write.
   The boot resolver reads the catalog **directly** (not via the hooked dispatcher
   — `capBreakCalls=0`), so to feed it, **id261's direct slot `0x14067B4AC` must
   hold valid item bytes BEFORE boot resolution runs.** (All patches reverted; rig
   left clean.)

9. **PRE-BOOT seed test (rebuilt the hook + rebooted) — the prime hypothesis.**
   `CatalogRedirectHook.SeedDirectSlot261()` now writes id261's physical slot
   `0x14067B4AC` = id257-clone AND lifts the 3 display caps to 262, all at
   `HookHost.SetHooks` (pre-title, before boot resolution). Build clean; **game
   booted fine** (touching the adjacent-table bytes did not crash boot); log
   confirmed `Seeded id261 physical catalog slot`; the slot held the clone bytes
   through boot and the render (verified — not reverted). **Result: id261 STILL did
   not render** (whole weapons list scrolled top→bottom; no clone, no blank row).
   → **Conclusion:** a valid raw-catalog entry, even present before/through boot,
   is NOT what the menu registry is built from. The registry/resolved-cache draws
   from the **NXD/name layer or a separate Lua table**, not the 12-byte catalog
   block. This boot's NXD has no id261 row, and earlier NXD-only tests also failed
   → the still-untested combo is **NXD name row + pre-boot catalog entry together
   in one build**. Deployed state: `SeedDirectSlot261` is live in the bridge
   (uncommitted FFTHandsFree change) as a building block; harmless (boots clean).

10. **THE COMBO test (NXD name + pre-boot catalog seed together).** Added a
    Key=261 "Moonblade" row to the `Item-en` table (`tools/add_id261_name.py`,
    clone of id257), re-encoded `item.en.nxd` to **262 rows / 47126 bytes**,
    deployed, rebooted. Confirmed loaded: `search_bytes "Moonblade"` now returns
    **1 match** (was 0), and id257's displayed name flipped **"Akademy Blade" to
    "Mirror Edge"** — proving names are id-indexed and the new table is live.
    Set `count[261]=5`. **Result: id261 STILL does not render.** So even
    catalog-entry + name-row + count, all present at boot, is insufficient.

11. **Clean-inventory cross-check (zeroed all bag counts 0–260, left id261=5).**
    `count[261]=5` **persisted** (no master-store sync resets it — that theory is
    dead) yet id261 still did not appear. Note: the inventory list = **bag items
    (count array `0x1411A17C0`) + equipped items (unit slots)**; zeroing the bag
    leaves equipped weapons visible, so it doesn't fully isolate id261, but it
    confirms beyond doubt that a bag item at id261 with valid catalog+name+count is
    excluded by the registry. The 3 `cmp r32,260` sites are unrelated (jump-table
    dispatch, value switch, 260-char string-parse). **No imm32 261/260 loop cap is
    the inventory list-build** → the list iterates a boot-built registry whose size
    (261) comes from the item DATA load (`item.nxd`, modloader-capped at 256+5),
    which none of catalog-seed / NXD-row / count / cap-patches extends.

12. **Hardware-breakpoint tracer — built, deployed, CRASHED (Denuvo anti-debug).**
    Built `HardwareBreakpointWatcher` (DR0/DR7 data breakpoint + VEH, single-address
    so no page-storm; verbs `hwbp_arm/status/release`). Armed read/write on
    `count[257]` (`0x1411A18C1`) across 172 threads, opened the inventory — and the
    game **crashed the instant the breakpoint fired** (the render read count[257]).
    → **Both in-process dynamic-trace techniques are Denuvo-walled:** page-guard
    (fault storm) AND hardware breakpoint (anti-debug detects DR registers /
    intercepts the debug exception). The watcher is deployed but dormant (only arms
    on command); do NOT arm it. *(Possible future mitigation: set DR on only the
    single render thread instead of all 172, to dodge Denuvo's anti-debug threads —
    untested, risky.)*

13. **Function-hook probe campaign — built `calllog_*`, ran it, hit the
    entry-identification wall.** Built `GenericCallLogger` (8-arg passthrough
    Reloaded.Hooks detour, armable at any address via `calllog_arm/status/release`)
    — **confirmed Denuvo-safe** (hooks armed + fired without crashing, unlike the
    breakpoints). Probed the count-reader functions' entries (found via int3-padding
    scan) for "called during the weapons-inventory open": `0x140392320`,
    `0x140152808`, `0x1401F3144`, `0x140303B04`, `0x14030A9C4` all → **0 calls**
    (fresh full opens, not cached). The one apparent hit (`0x1402C8CDC`, calls=1,
    args 256/255) was a **false positive** — that address is a `jmp qword ptr
    [0x00D50298]` thunk, not a function entry. → **The blocker now is reliable
    function-boundary identification:** int3-scanning lands on thunks / wrong
    boundaries, and hooking a non-entry corrupts the frame. Doing this right needs a
    full disassembly-based call-target map of the code section to enumerate true
    function entries, then probe each. Tractable but a real chunk.

14. **The render's count access is deeply INDIRECT (the reason static enumeration
    fails).** Built a call-target function-entry map (`get_func_entries.py`,
    77k true entries from `E8` call targets) and re-probed every count-reader's
    TRUE enclosing-function entry with `calllog_*` on fresh opens: `0x140152808,
    0x1401F3394, 0x1402CA6E8, 0x1402CAF5C, 0x1402D1E4C, 0x14030B078, 0x1403914A8,
    0x140303B04, 0x140392320` — **ALL 0 calls** during the weapons-inventory open
    (fresh opens verified by screenshot to actually render). Yet `count[257]` at
    `0x1411A17C0` drives the display. And there is **no pointer-global holding
    `0x1411A17C0`** (search = 0 matches). Widening the lea/mov scan to the
    menu-state struct page (`0x1411A1000`–`0x1411A17C0`) returns **63 refs** to a
    dozen sub-bases (`0x1411A1050/1120/1240/1350/13D0/...`); the render reaches the
    counts as `[struct_sub_base + offset + id]`, so its function is one of dozens
    touching the menu-state struct — not cleanly identifiable statically. → The
    render is buried in indirect struct+offset access; enumerating it needs
    data-flow analysis (IDA/Ghidra), not lea-scanning.

**NET — the honest limit.** Every data-side and cap-side lever is exhausted. The two
clean dynamic-trace techniques (page-guard, HW breakpoint) both crash under Denuvo.
Function-entry hooks ARE Denuvo-safe and `calllog_*` works — but using them to find
the render requires building a function-boundary map first (int3-scan isn't
reliable). So two real finish lines remain:
  (a) **External Cheat Engine (kernel/DBVM debugger)** — "find what accesses
      `0x1411A18C1`" during a weapons-inventory open. Evades Denuvo's anti-debug,
      ~5 minutes, hands over the exact render loop. (Can't be driven by an in-VM
      agent — needs a human at CE.)
  (b) **Finish the in-process hook campaign** — build a call-target/function-entry
      map, probe each true entry with `calllog_*`, find the render, read its size
      cap, patch it. Denuvo-safe but multi-hour.
Both then yield the render loop → its size source (the native 261 item count) → the
fix. The data path (pre-boot catalog seed + 262-row NXD name) is already in place,
so once the iteration is extended, id261 should light up.

---

## The wall, stated precisely

The PartyMenu item list iterates owned items (`count[id] > 0`) and, per item,
consults a **boot-built item registry / resolved cache** that contains entries
only for ids 0–260 (built from the 261-entry catalog before our hooks' data could
matter — the boot build reads the catalog *directly*, NOT via the hooked
dispatcher: `catalog_status` shows `capBreakCalls=0`). id261+ have a count but no
registry entry → filtered out of the list. The render reads **resolved/cached**
data, not the live catalog (proven earlier: poking a live catalog entry didn't
change the rendered stat). So the fix must operate on that resolved layer.

---

## Next-session roadmap (sharpened by the 2026-06-04 experiment)

The post-boot experiment proved the fix must land **before boot resolution**.
The single highest-value next step:

1. **Pre-boot direct-catalog write (the prime hypothesis).** Extend
   `CatalogRedirectHook.Install` (which already runs pre-boot at `HookHost.SetHooks`,
   before the engine's table build) to ALSO write **id261's direct catalog slot
   `0x14067B4AC`** = id257-clone bytes (the `meSeed`), so the boot resolver builds a
   resolved-cache entry for id261. Wrap defensively (try/catch, log, non-blocking)
   so a failure can't brick boot. Also add the 3 display caps to the patch list as
   **single-byte 261→262** lifts (`0x140284B56/0x140284EF0/0x140392455` → `0x06`;
   262 includes only id261, avoiding the pointer array at `0x14067B4C0`).
   `dotnet build` + `RunTests.sh` green → deploy → **reboot** → set `count[261]`,
   open inventory, screenshot. If id261 renders (even blank-named) → the resolved
   cache was the gate and the data path is solved.
2. **If it renders blank-named:** add id261's name (`tools/patch_names.py` →
   `item.en.nxd`, redeploy, reboot). Earlier sessions showed name-present alone is
   insufficient, but name + pre-boot catalog entry together is the untested combo.
3. **If it still doesn't render:** the resolved cache is built from a path that
   ignores even the direct catalog slot. Then find the real render/list-build loop
   among the count-readers (disassemble; NOT a bulk copy) and instrument it with a
   **Reloaded.Hooks logging detour** (NOT a page-guard — it crashes) or external
   **Cheat Engine** kernel debugger to learn the resolved-cache address + format,
   then inject id261 directly.
4. **Equip back-resolution** (separate wall): even when id261 items show in the
   equip picker, selecting one writes canonical id257 to the slot. Untested since
   the NXD name was added — revisit after display works.

---

## Operational notes (for driving the rig)

- Game exe `FFT_enhanced.exe`; launch via `boot` (Reloaded-II `--launch`).
- Bridge **screen detection can desync** (e.g. reports `Battle` while the
  screenshot shows WorldMap after abandoning a battle). Screenshots are ground
  truth; `reset_state_machine` / entering a menu re-syncs detection. State-gated
  helpers (`scan_inventory`) refuse on the stale state — drive raw keys instead
  (disable with `strict 0`, or use the screen's `validPaths` via `execute_action`).
- WorldMap → `Escape` → PartyMenuUnits → `OpenInventory` (validPath) /
  `E` (raw) → PartyMenuInventory (renders the weapon list).
- **Never** page-guard a hot page. Prefer disassembly + hooks.

---

## The equip wall (2026-06-04) — id261 renders but cannot be equipped

After id261 ("Moonblade") renders in the inventory with a **custom name + custom
description** (item.en.nxd extended to 262 rows) and a real type, the remaining
goal was *equippability*. It is blocked, and the block was traced precisely.

### What works
- **Inventory display (Items tab):** plain path. Name/desc from the nxd, category
  via the CategoryCloneHook (0x140284F3C → clone a source id), count from the
  count array. Fully functional.
- **Catalog accessor `0x1402B4910`** (the one plain ref to the catalog disp
  `+0x808740`): `id<256 → base + id*12 + 0x808740` (main block); `id>=256 →
  base + id*12 + 0x67A870` (extended block). For id261 this yields **0x14067B4AC
  — exactly our seed. No 261 cap.** 20+ plain callers. So id261's 12-byte catalog
  metadata is fully reachable in plain code.

### What is blocked
- **Equip selector:** built by encrypted code (`0x14DF626C2`) gated by the
  encrypted type-checker thunk `0x1402B4BB4`. Hard-caps id<261. id261 is the
  first id past it. Hooking the thunk crashes the party menu.
- **Stat resolver `0x140280898`** (plain; memsets a 0x280 struct then fills from
  src1=rsi/src2=rdi). It reads **rsi offsets up to 0x41 and rdi up to 0x1d** —
  far past a 12-byte catalog entry. So weapon **stats (Atk/range) live in a
  separate, wider per-item table, NOT the catalog.** The resolver has **0 plain
  callers** (scan_callers) — only reached via encryption, so that wider table's
  base/stride is computed inside encrypted code and cannot be found statically.
- **Force-equip test:** wrote id261 (0x0105) directly into a unit's heap weapon
  field (CE changed-value scan: equip Mirror Edge=257, scan 257, swap, "changed
  value", swap back). The write succeeded; **re-rendering crashed** — the recompute
  resolved id261's stats out of bounds in that 261-sized table. id257 (same cloned
  bytes, different id) equips fine → it is purely the id being past the table end.

### Static-analysis tooling added (in FFTHandsFree/)
- `scan_imm261.py` — byte-scans `.reloc` (Denuvo MISLABELS the real plain-code
  section as `.reloc`, 0x140001000–0x14060C000; `.text` is a 64KB stub; `.xtext`
  is the 292MB encrypted blob) for cmp/mov with imm 261/260. 188 hits: mostly
  count-array loops (`for id=0;id<261` over 0x1411A17C0) and the 3 known display
  caps (0x140284B56 / 0x140284EEE / 0x140392453). **None is the stats-table bound.**
- `scan_callers.py` — finds E8/E9 callers of a VA (the old get_callers.py was
  broken — returned the same list for any target).
- `scan_refs.py` — rip-relative lea/mov refs into an address range.

### The only remaining lead (needs CE — a human at the keyboard)
**Crash-probe:** with CE's debugger set to break on exceptions, force-equip id261
and read the faulting instruction + the address it dereferenced. That address is
`stats_table_base + 261*stride` — the table that's hidden behind the encryption.
If there is free space past it (or it can be repointed), seed id261's slot from a
real sword and force-equip succeeds → equippable. If it is exactly sized and its
base is referenced only from encrypted code, that is the true Denuvo floor.

**Verdict so far:** items past 261 *exist and display* (won, scalable). *Equipping*
them is gated by an encrypted-indexed, 261-sized stat table — the one inch that is
genuinely behind the DRM. Confirmed from three directions (selector cap, resolver
trace, force-equip crash).

---

## 2026-06-26 session — fresh-eyes re-attack (resume here)

Re-opened with a stronger model + a 4-agent mining workflow. Two new things, then where it parked.

**A. The battle-attack stat path is BOUNDS-SAFE (new live result).** Writing id261 into the live
*combat* struct `CWeapon` (+0x20, e.g. `0x141855D00` that session) and attacking did **NOT crash** —
the unit just goes **unarmed (fist attack)**. So the engine bounds-checks the BATTLE damage path
(id>=261 -> no weapon, graceful). The journal's force-equip CRASH is therefore specifically the
**EQUIP/STAT-SCREEN resolver** (`0x140280898` family), NOT the battle path. => The crash-probe must be
triggered from the **formation/equip menu**, not in battle: set roster rHand **`0x1411A7D24`** (slot0
+0x14, STABLE/no-ASLR) = 261, freeze, open Ramza's equip screen -> that re-render is the OOB read.

**B. Tooling to find the table without a crash is blocked tonight by WINDOWS, not Denuvo.** The clean
zero-crash move is CE "find what addresses this instruction accesses" on the resolver's `[rsi+disp]`
read while equipping two known ids -> back out `baseW`/`strideW` algebraically. That needs CE's
KERNEL/DBVM debugger (the journal proved usermode HW-breakpoints + page-guard crash under Denuvo). But
CE's `dbk64.sys` is on Windows' **vulnerable-driver blocklist** (Memory Integrity / Core Isolation), so
the kernel driver won't load. **Resume gate: Windows Security -> Device Security -> Core Isolation ->
Memory Integrity OFF -> reboot.** Then the access-trace is ~5 clean minutes. (VEH usermode debugger
loads without the driver and may survive Denuvo for a one-shot exception catch — untried-to-completion.)

**C. Premise correction confirmed (do not re-hunt display).** Per the equip-wall section above, the
DISPLAY wall is already SOLVED and lives PARKED on FFTHandsFree branch `parked-research` (de-armed
2026-06-10 because the broad cap-lifts corrupted IC "+" treasure items — Materia Blade+ got a gun's
range). The display stack: pre-boot catalog seed `SeedDirectSlot261` @`0x14067B4AC`, count-getter cap
`0x14028067B` (`0x103`->`0x203`), 3 display caps `0x140284B56/EF0` + `0x140392455` (261->262),
`CategoryCloneHook` `0x140284F3C`, 262-row `item.en.nxd`. **Strategic shortcut: a CONSUMABLE id261
never touches the equip stat resolver — it likely works with display-only. Test a consumable before
assuming equip must be cracked.**

**THE PLAN (ranked, from the workflow synthesis):**
1. **Access-trace `0x140280898`'s `[rsi+disp]` + `[rdi+disp]` reads** (needs Memory Integrity off ->
   kernel/DBVM CE). Equip two known ids, record the two accessed addresses, solve
   `strideW=(addr_k-addr257)/(k-257)`, `baseW=addr257-disp-257*strideW`; same for the narrow table
   via rdi. Then READ-ONLY dump `baseW+0..260*strideW` (confirm clean array) and `baseW+261*strideW`
   (free vs live -> decides seed-in-place vs repoint).
2. **#1b fallback (no debugger):** RPM AOB-scan writable heap for a known weapon's distinctive
   stat-record bytes; walk by candidate strides (0x42/0x48/0x50) until it aligns as an id-indexed array.
3. **#2 decisive test:** copy a real sword's full entry into `baseW+261*strideW` (and the narrow
   table), arm the display stack, force-equip id261 in the *formation* menu, re-render. No crash + real
   stats = equip SOLVED + scalable by repointing W/N to managed buffers. (BACK UP THE SAVE first; never
   save while id261 is equipped.)
4. **De-collateralize + ship display** (track A): scope every cap/category/Lua patch to fire ONLY for
   ids 261-516, never the "+" treasure id range; re-verify Materia Blade+ keeps its range.

PROVEN-DEAD (do not retry): page-guard, hardware breakpoints (both Denuvo-crash), catalog relocation /
NumEntries bump, `0x1405D0D78`, NXD-name-only, pre-boot-catalog-seed-alone, in-place catalog growth,
master-store-sync-reset, re-hunting the boot-built DISPLAY registry, and CWeapon-in-battle (graceful, no
crash). Full synthesis: this session's workflow `item-cap-registry-attack`.

**D. CRITICAL: the journal's CODE addresses are STALE for the current build (confirmed live 2026-06-26).**
Aligned disasm (anchored on `int3` function boundaries via RPM+capstone) shows the function at
`0x140280200` (which CONTAINS the journal's resolver address `0x140280898`) has NONE of the
`[rsi+0x41]`/`[rdi+0x1d]` record reads the stat resolver must have -- so `0x140280898` is NOT the resolver
in this boot. The game was patched since 2026-06-04; CODE got recompiled and shifted. DATA addresses are
still good (roster `0x1411A7D10`, count array, CWeapon `+0x20` all worked live tonight) -- code and data
rebase independently across a patch. **Implication: every CODE address in this doc (resolver
`0x140280898`, the cap sites `0x140284B56/EF0`/`0x140392455`, count-getter `0x14028067B`, selector
`0x1402B4BB4`, `SeedDirectSlot261` target `0x14067B4AC`) must be RE-LOCATED in the current build before any
of the equip plan runs.** Also: id261 is gracefully rejected (-> "nothing"/fist) in BOTH the battle and
equip paths when the cap-break display stack is NOT armed, so the crash-probe can't fire without first
arming the (parked, address-stale) display stack. NEXT SESSION must start by re-basing: enable kernel CE
(Memory Integrity OFF -> reboot), then `find what accesses` the count array / catalog from a live menu to
re-anchor the code, OR signature-scan for the resolver (its `0x280`-struct memset = `mov reg,0x280` near
`[rsi+disp]`/`[rdi+disp]` reads; the live 6 MB RPM scan timed out tonight -- do it in CE or chunk smaller).

**E. DATA TABLES RE-BASED for the current build (live-verified 2026-06-26, RPM, no debugger).** The three
core weapon-DATA tables are found and the catalog->stat->count chain checks out on Vagabond/Warbrand/
Defender/Cleaver (WP/formula/evade/onHit all match items.json):
  - **ItemData / catalog = `0x14080EA90`** (was pre-1.5 `0x140808740`; +0x6350). 12-byte records, ids
    0-255 (256-entry MAIN block; id254/255 empty). Record layout: byte0 palette, 1 sprite, 2 reqlvl,
    3 TypeFlags(0x80=Weapon), **4 = SecondTableId**, **5 = ItemType** (03=Sword 04=KnSword ...), 8-9 price.
    At `0x14080EA90 + 256*12 = 0x14080F690` the main block ENDS and ItemWeaponData begins (NOT an extended
    catalog -- the 256-260 extended block is a separate table, still UNLOCATED this build; pre-1.5 was
    `0x14067A870 + id*12`, but +0x6350 -> `0x140680BC0` is WRONG (lands in a pointer array), so the
    0x14067xxxx region shifted by a DIFFERENT delta -- re-find by content).
  - **ItemWeaponData (weapon stats) = `0x14080F690`**, **8-byte rows, indexed by SecondTableId**:
    [Range, AttackFlags, Formula, 0xFF, **WP(Power)**, Evade, Element, OptionsAbilityId]. Only **~128 rows**
    (0-127); row 128+ is the shield/armor/item secondary tables. **SecondTableId == item id for weapons**
    (id67 Warbrand -> WPN[67] WP=15 f=1; id20 Cleaver -> WPN[20] f=2 opt=147 Knockback).
  - **Count array = `0x1411A7C00`** (= RosterBase 0x1411A7D10 - 0x110; was pre-1.5 `0x1411A17C0`).
    1 byte per id; give-all save reads 99 across ids 0-260.

**REFRAMED EQUIP HYPOTHESIS (test next):** the force-equip crash was id261 indexing ItemWeaponData OOB
(`[261*8]` past the 128-row block) because it had no catalog entry -> garbage SecondTableId. A new id261
weapon given a catalog entry whose **SecondTableId points at an existing weapon row (0-127, e.g. 67 to
clone Warbrand's stats)** should resolve IN BOUNDS -> valid stats, no crash, possibly equippable. The
journal's "separate 261-sized wide table, stride 0x42" may be a mis-read of the resolver's 0x280 OUTPUT
struct, not a real id-indexed source table -- ItemWeaponData (8-byte, SecondTableId-indexed, <128) is the
real weapon stat source. DECISIVE TEST: once the extended catalog / an id261 catalog slot is located, write
id261 = {valid weapon TypeFlags, SecondTableId=67, ItemType=Sword}, set count, and try to equip -- if no
crash, the equip wall falls via SecondTableId reuse, no encrypted-table-cracking needed.

STILL TO RE-LOCATE: the extended catalog (256-260 + id261 headroom slot), the display cap-sites + selector
+ CategoryCloneHook (CODE -- non-uniform shift), and confirm whether a separate id-indexed wide table even
exists. But the DATA write recipe for a new weapon is now: catalog `0x14080EA90+id*12`, stats
`0x14080F690+secondId*8`, count `0x1411A7C00+id`.

**F. CATALOG ACCESSOR FULLY DECODED via CE "find what accesses" (2026-06-26, VEH+HW-bp WORKED -- no crash;
so the usermode HW breakpoint survived Denuvo this time, unlike the in-process `hwbp_*` watcher).** The
inventory read of `0x14080EA90` traced to outer accessor `0x1402E1698` (masks the id with **`0x1FF`=511**,
`and dx,r11w`, then `call 0x1402B8CB8`), which calls the record-address function:
```
0x1402B8CB8  catalog record-address(ecx=id) -> rax:
  movzx eax,cx ; mov edx,0x100 ; cmp cx,dx          ; id vs 256
  lea rcx,[rip-0x2b8cca]  => 0x140000000            ; module base
  lea rax,[rax+rax*2]                               ; id*3
  jae .ext                                          ; id>=256 -> extended
  lea rax,[rax*4 + 0x80ea90]    ; MAIN: 0x14080EA90 + id*12     (instr @0x1402B8CD0)
  jmp .end
.ext: lea rax,[rax*4 + 0x67f910]  ; EXTENDED: 0x14067F910 + id*12 (instr @0x1402B8CDA)
.end: add rax,rcx ; ret
```
=> **MAIN catalog base `0x14080EA90`** (confirmed), **EXTENDED catalog base `0x14067F910`** (id261 slot =
`0x14068054C`). The accessor has **NO upper cap** -- it returns a valid address for any id (mask 0x1FF
allows 0-511). Extended block verified live: ids 256-260 are the real "+" item records (id256 Sword
2nd=32 / id257 Sword 2nd=20=Cleaver's WPN row / id258 Hat / id259 Clothing / id260 Ring) -- **they REUSE
existing in-bounds (<128) ItemWeaponData rows via SecondTableId**, which is the proof-by-example that a new
id261 weapon with 2nd=67 (Warbrand's row) resolves valid stats with NO OOB crash. BUT id261's physical slot
`0x14068054C` already holds a LIVE pointer array (`0x140718540...`) -- only 256-260 fit. **To add id261+,
patch the extended-branch immediate at `0x1402B8CDA` (the `+0x67f910` in `lea rax,[rax*4+0x67f910]`) to a
roomy allocated buffer, copy the 256-260 records there, and write new records past 260.** That single
code patch + a buffer relocates the extended catalog and lifts the physical cap to 511. Re-find the
display count-getter cap (journal `0x14028067B`, clamped count[id>=259]->0) and selector the same way
(CE find-what-accesses the count array / a "+" item's slot). The HW-bp-via-VEH working means kernel CE may
not even be needed for the rest.

**G. COUNT-GETTER cap RE-LOCATED (CE find-what-accesses count[257]=`0x1411A7D01`, 2026-06-26).** The
count get/set function (entry `0x140284870`) is:
```
0x140284870 mov eax,0x3ff ; 0x140284875 mov r8d,0x103(259) ; and cx,ax ; lea eax,[rcx-1]
0x140284881 cmp ax,r8w ; ja .skip            ; id-1 > 259  =>  id > 260 rejected (count stuck 0)
0x14028488B lea r9,[rip+0xf2336e] => 0x1411A7C00   ; count base CONFIRMED again
0x140284894 movzx ecx,[r8+r9] ; ... ; 0x14028489E mov [r8+r9],al
```
**CAP = `mov r8d,0x103` at `0x140284875`; lift by patching the imm byte at `0x140284878` (`01`->`02` =>
0x203=515).** That is the journal's stale `0x14028067B`, re-based. (mask is 0x3FF=1023 here, so 0x203 is
safe.)

**THE WRITE PLAN to land a weapon at id261 (have everything but the extended-block headroom):** (1)
VirtualAllocEx a >=3 KB buffer; copy the live 256-260 records (`0x14067F910+id*12`) into it at the same
id*12 offsets; patch the EXTENDED `lea` immediate at `0x1402B8CDA` from `0x67f910` to (buffer -
0x140000000) so the accessor reads the buffer for id>=256 -- verify 256-260 still resolve. (2) Write id261
catalog in the buffer: clone a real sword record (type=Sword 0x03, TypeFlags weapon 0x80/0x82,
**SecondTableId=67** to reuse Warbrand's in-bounds ItemWeaponData row). (3) patch `0x140284878` 01->02 +
set count[261]=1. (4) force-equip id261 (write CWeapon combat+0x20 or roster+0x14). Expect Warbrand stats,
no crash (vs the bare-261 graceful-fist) = weapon-at-261 PROVEN. Inventory DISPLAY (the list-builder cap +
3 display caps, journal `0x140284B56/EF0`+`0x140392455`) is a SEPARATE re-base, only needed to SHOW it in
the bag; force-equip needs none of it.

**H. LIVE WRITE EXECUTED + THE REGISTRY GATES *EVERYTHING* (2026-06-26).** Drove the full live write:
VirtualAllocEx'd a buffer @ a free in-range address, copied ids 256-260 into it, patched the extended
`lea` (`0x1402B8CDE`) to relocate the extended catalog to the buffer (256-260 verified resolving from it,
NO crash -- the catalog accessor relocation is clean and live), wrote **id261 = a valid Sword,
SecondTableId=67 (Warbrand WP15, in-bounds), TypeFlags Rare|Weapon** into the buffer, lifted the count cap
(`0x140284878` 01->02), set count[261]=1, and lifted the 4 display iteration caps (`0x14028479C /
0x140284841 / 0x140288D52 / 0x1402890EC`, 0x05->0x06). **RESULTS: (a) inventory STILL shows no id261**
(weapons + items + equip tabs) -- the list is the BOOT-BUILT registry, not a live count scan, so live
cap-lifts can't inject into it (journal findings 1-13 re-confirmed). **(b) force-equip CWeapon=261 in
battle = EMPTY SLOT / no weapon** (same graceful-reject as bare-261 before the catalog existed) -- so the
BATTLE equip+render path ALSO consults the boot-built registry, NOT our (now-valid) catalog chain. =>
**The boot-built registry gates BOTH display AND equip; a valid catalog entry written post-boot is
invisible to both.** This is the genuine wall, and it is fundamentally NOT live-pokeable: it is assembled
at BOOT from the item-data load. The catalog accessor (`0x1402B8CB8`) we cracked is read by the INVENTORY
DETAIL/lookup path but is NOT the registry the list/equip iterate. Live poking is EXHAUSTED for id261.
**THE ONLY PATH is pre-boot seeding** (the journal's eventual win, the parked-research stack): write
id261's catalog/stats/registry-source BEFORE boot resolution runs, so the boot build includes it. We have
now fully re-based that stack for the current build; it is a BUILD + REBOOT, not a live poke. All live
patches this session are runtime-only and revert on reboot; nothing saved.

## CURRENT-BUILD ADDRESS TABLE (re-based 2026-06-26 -- the journal's pre-1.5 addresses are ALL stale)
| thing | current addr | note |
|---|---|---|
| Catalog (ItemData) MAIN | `0x14080EA90` | 12B/rec, ids 0-255; byte4=SecondTableId byte5=type |
| Catalog EXTENDED base | `0x14067F910 + id*12` (id>=256) | repoint via `lea` imm @ `0x1402B8CDE` |
| Catalog accessor fn | `0x1402B8CB8` | no cap, mask 0x1FF=511; outer `0x1402E1698` |
| ItemWeaponData (stats) | `0x14080F690 + secondId*8` | <128 rows; +4=WP, +2=formula, +0=range, +1=flags, +5=evade, +6=elem, +7=optAbility |
| Count array | `0x1411A7C00 + id` | =RosterBase-0x110 |
| Count-getter cap | `mov r8d,0x103` @ `0x140284875` (imm byte `0x140284878`) | id<=260 gate |
| Display iteration caps | `cmp r32,261` @ `0x14028479B / 0x14028483F / 0x140288D50 / 0x1402890EA` | + maybe a 5th in 0x140392xxx |
| Roster | `0x1411A7D10` stride 0x258; +0x14 rHand | (was current already) |
| Combat CWeapon | combat +0x20 | drives battle stat/render; rejects 261 (registry) |
| **BOOT-BUILT REGISTRY** | **UNLOCATED** | gates display+equip; built at boot; the true wall; pre-boot seed only |

## 2026-06-26 (session 2) -- EQUIP gate-map: the wall is the encrypted weapon-stat accessor

Fresh-eyes re-attack on EQUIP (display deferred). CE find-what-accesses + RPM-capstone disasm (both
Denuvo-safe this build -- usermode HW/data BPs work). **KEY REFRAME: section H's "boot-built registry
gates equip" was WRONG.** The equip rejection is a CHAIN of plain, locatable gates plus one encrypted
accessor -- not the registry. Walked it live via the crash-probe (CE break-on-AV).

DATA RIG (live, reverts on reboot; fully re-derivable):
- Extended catalog RELOCATED to a VirtualAllocEx buffer. Buffer MUST be within +-2GB of 0x140000000
  (signed lea disp); find a MEM_FREE region via VirtualQueryEx (NULL alloc lands low/out-of-range; ALL_ACCESS
  OpenProcess is Denuvo-denied -- reuse the limited VM_OPERATION handle). Patch extended lea disp32
  @ 0x1402B8CDE = buf-0x140000000. Copy ids 256-260, write id261 = {TypeFlags 0x82, SecondTableId 0x43=67
  (Warbrand), ItemType 0x03=Sword} at buf+261*12.
- Count cap @ 0x140284878: 0x01->0x02 (mov r8d,0x103->0x203). count[261]=1 @ 0x1411A7C00+261.
- 4 display caps (imm byte 0x05->0x06 = 261->262): 0x14028479C, 0x140284841, 0x140288D52, 0x1402890EC.
- Force-equip: roster slot0 rHand @ 0x1411A7D24 = 261. **WARNING: a save captured id261 on Ramza this
  session -- un-equip a real weapon + re-save to clean it.**

EQUIP GATES for id261 (a weapon):
1. PLAIN CLAMP @ 0x140284c80: `mov edx,0xff; lea ecx,[rdx+6]`(=261); `cmp cx,ax; ja keep`. rHand>=261 ->
   fallback slot [rdi+0x16] (= section H's bogus "registry rejects equip"). Lift: disp8 @ 0x140284c82
   0x06->0x07 (cap 262; covers both weapon slots, cx reused).
2. **THE WALL -- encrypted weapon-stat accessor @ 0x1402B8CE8** (thunk: jmp 0x1500df9f8). Equip
   category/stat resolver @ 0x140F0719-0x140F0732: main accessor 0x1402B8CB8(id)->catalog;
   `cmp [rax+03],bl; jnl skip` (TypeFlags&0x80=weapon -> fall through); call 0x1402B8CE8(id);
   `mov cl,[rax+04]`. id261 -> 0x1402B8CE8 returns NULL -> AV at 0x140F0732 (rax=0). Returns NULL on
   EVERY call (hand-fixing RAX in CE re-faults next call). MAIN accessor (plain, relocatable) resolves
   id261 fine; the WEAPON accessor (encrypted) is the gate. FIX (concrete, untested): HOOK 0x1402B8CE8
   entry, ids 261-516 -> OriginalFunction(cloneWeaponId) (CategoryCloneHook pattern; id arg in ECX).
   Risk: journal warned hooking thunks can crash the menu (0x1402B4BB4 pre-1.5) -- verify.
3. Context: equip-block reader 0x1405C9E98 (plain, copies the 14-byte roster equip block raw, no clamp);
   per-item validity check 0x1402b8f30 (encrypted thunk jmp 0x1501484e0) in a 5-slot loop zeroing
   invalid slots.

DISPLAY (deferred, mapped): CategoryCloneHook hooks the category getter (pre-1.5 0x140284F3C, MOVED in
1.5 -- re-find). list-build calls it per id; 261-516 -> clone id19 category -> Weapons tab. Works LIVE.
Live data-table poke (ItemCategoryToDataType[13] 9->5 @ 0x14067fbac) did NOTHING -- the live getter reads
the boot resolved-cache via an encrypted path; only the function-return override (hook) works.

ROUTING TABLES (data behind the category system; modloader-moddable; do NOT gate display alone):
ItemIdRangeToCategory @ 0x1406804e0 (14 u16: 0,122,128,144,172,208,240,256,258,258,258,259,260,261),
ItemCategoryToDataType @ 0x14067fb78 ([0,0,1,2,2,3,4,5,5,6,7,7,8,9]), DataTypeToItemIdRange @ 0x14067fb38
([0,2,3,5,6,7,9,10,12,13]). id261 -> cat13 -> dt9 -> terminal range. Decompiled modloader (Nenkai,
fftivc.utility.modloader v4.1.3, ilspy): FFTOItemDataManager hardcodes main=256/extended=5;
ItemWeaponData=128 rows by SecondTableId; per-table AOB scans + ChangeProtection(RWX).

NET: equip is a SERIES of gates, not one wall. So far: caps (patch), clamp (patch), weapon accessor
(hook). Encrypted pieces (0x1402B8CE8, 0x1402b8f30) are THUNKS -- hookable at their plain entries.
Likely MORE gates downstream; find by build->deploy->crash-probe iterate. NEXT: /build the FFTHandsFree
1.5 cap-break stack (rig + clamp patch + 0x1402B8CE8 clone hook + category-getter clone hook), scoped to
261-516, deploy, iterate.

## 2026-06-26 (session 2 cont.) -- LIVE-VERIFY: the equip "floor" is a FIXABLE thunk-hook bug

Built the cap-break stack (FFTHandsFree branch `capbreak-equip`, via the /build pipeline: AccessorCloneHook
+ CapBreakEquipHook coordinator + IBytePatcher seam + capbreak_arm/status/disarm verbs; 4803 unit tests
green, off-by-default, scoped 261-511). Deployed (BuildLinked) + live-verified via the bridge. Results:

- **THUNK HOOK INSTALLS CLEAN (the #1 risk -- CLEARED).** `capbreak_arm` installs the catalog hook
  (0x1402B8CB8) + weapon hook (0x1402B8CE8) without crashing. Both report active.
- **BUT arming crashes a NORMAL party/formation render** -- even on a CLEAN save. Roster scanned: every
  unit's equip ids are <256 (or 255=empty); NO high-id item anywhere. So it is NOT save pollution. Bisected:
  reverted clamp + count-cap + count[261] (hooks-only) and it STILL crashed -> the **HOOKS** cause it, not
  the byte patches.
- **ROOT CAUSE (the reframe).** The weapon accessor `0x1402B8CE8` is a **5-byte `jmp rel32` THUNK**
  (`e9 0b 6d e2 0f` -> 0x1500df9f8). Reloaded.Hooks installs a **7-byte `jmp [abs]`** (detour is >2GB away)
  and pads to 13 bytes (`ff242568011605` + six `90`), so it **overwrites 8 bytes PAST the 5-byte thunk**
  (0x1402B8CED-CF4) -- clobbering adjacent code/data the resolvers use (and/or breaking the relocated-thunk
  passthrough). Confirmed by reading the installed bytes. By contrast the **CATALOG hook is CLEAN**: 0x1402B8CB8
  is a real ~0x2E-byte function, so the 7-byte jmp overwrote only its own (relocated) first 8 bytes; the body
  at +0x08 is intact, zero collateral. So it is SPECIFICALLY the 5-byte thunk that is unsafe to hook this way.
- **=> EQUIP IS NOT A DRM FLOOR.** It's a hook-METHOD bug. The encrypted weapon accessor returns NULL for
  id261 (internal id-cap); cloning `ecx = id -> 67` BEFORE the accessor (so its cap sees 67<261) resolves it.
  The clone only needs a NON-CLOBBERING hook.
- **THE FIX: a 5-byte `jmp rel32` redirect to a near stub** (within +-2GB), occupying EXACTLY the 5-byte
  thunk (zero clobber). Allocate stub S (VirtualAllocEx in range, like the catalog buffer); write S =
  `mov eax,ecx; and eax,0x3FF; cmp eax,261; jb pass; cmp eax,511; ja pass; mov ecx,67; pass: jmp 0x1500df9f8`;
  then rewrite `0x1402B8CE8` = `e9 <rel32 to S>` (5 bytes only). Handles clone (261-511) + correct passthrough
  via the explicit `jmp` to the original target. Same treatment for any other type-accessor thunk that a
  high-id item routes through (shield `0x1402B8D48` = `jmp 0x1500f1080`, etc.).
- **SECONDARY BUG: `capbreak_disarm` does NOT restore the hook bytes.** Reloaded's `IHook.Disable()` routes
  the detour to passthrough but LEAVES the jmp in place -- so the weapon-thunk clobber of 0x1402B8CED PERSISTS
  until process reboot. The coordinator must MANUALLY restore the saved entry bytes on disarm (extend the
  IBytePatcher save/restore to cover the hook entry bytes, not just the clamp/count patches).
- **CATALOG HOOK IS SAFE => DISPLAY is the lower-risk win** (catalog relocation/hook + the plain
  category-getter hook; no 5-byte-thunk weapon accessor involved).
- Game RESTORED CLEAN after testing (both accessor entries manually rewritten to original via RPM, since
  disarm won't). Save is clean.

**NEXT:** (1) rebuild `AccessorCloneHook` to use the 5-byte near-stub redirect for THUNK targets (detect a
`jmp rel32` entry; Reloaded.Hooks `CreateHook` is unsafe on <14-byte thunks when the detour is far). (2) Fix
disarm to restore entry bytes. (3) Redeploy + render-test (open party + equip screen with id261). If id261
equips, the equip wall falls -- then sweep for any further type-accessor thunks (shield/armor/accessory) and
the encrypted validity check 0x1402b8f30 the same way. Confidence on equip just went UP (from ~15% floor to a
concrete fixable mechanism).

## 2026-06-26 (session 2 cont.) -- DISPLAY path: clone id, the render wall, and the category-getter hunt

Pivoted from equip (thunk-walled) to DISPLAY (the catalog accessor is the safe, clean hook -- see line above:
"CATALOG HOOK IS SAFE => DISPLAY is the lower-risk win"). Drove the full live write and pinned the display facts:

- **DISPLAY clone id = 37 (Chaos Blade, KnightSword), NOT 67.** The clone id a high item resolves to must be a
  weapon TYPE the wielder's job can equip, or the menu renders "Nothing Equipped" even though the id is held in
  the slot. Proven live on Ramza (Gallant Knight):
  - Writing **id67 (plain Sword)** to Ramza's hand STUCK in memory but showed **"Nothing Equipped"** -- a
    Gallant Knight cannot equip a plain Sword.
  - Writing **id37 (Chaos Blade, KnightSword)** RENDERED as Chaos Blade.
  - => the display rig clones id261 -> id37 (the equip rig's clone-67 is for the weapon-stat path, a different
    concern; 67's in-bounds ItemWeaponData row is fine for STATS but wrong for the equip-type RENDER).

- **THE RENDER WALL, confirmed the cleanest possible way.** id261 **held in the slot** with a **valid relocated
  catalog (clone of 37) + valid weapon stats** STILL shows **"Nothing Equipped"** in BOTH the Status screen and
  the equipment editor. Registered items (Genji Helm/Armor, Titan Grips) render; id261 does not. So catalog +
  stats are NOT sufficient: the display is gated by the boot-built **registry / category / NAME layer**. The
  count-cap + count[261]=1 + the 4 display iteration caps + the catalog relocation were ALL live this run and
  inventory STILL showed no id261 -- i.e. **the CATEGORY GETTER is the missing linchpin**, not the caps.
  Live-poke alone (no category-getter hook, no NAME row) is EXHAUSTED for display.

- **The category/registry resolver cannot be found by static scanning.** A full code-section scan for
  rip-relative refs to the routing tables / catalog found ZERO refs (computed/encrypted addressing); only the
  count array `0x1411A7C00` was reachable by grep. So the 1.5 category getter must be found by CANDIDATE-TESTING
  live (or CE find-what-accesses), not by disasm grep. The pre-1.5 getter was `0x140284F3C` (plain code: id in
  CX, mask 0x3FF, returns a small category-group code) -- STALE for 1.5.
  **1.5 candidates** (the `mov eax,0x3ff` id-mask functions in `0x140283000`-`0x14028D000`):
  `0x140285036, 0x140286d26, 0x140287464, 0x14028752d, 0x1402875dc, 0x14028875f, 0x140288ded, 0x14028b20b`.
  **EXCLUDE** the count getter `0x140284870`. The right one is whichever makes id261 appear in the weapons tab.

- **BUILD DECISION (display catalog provision).** Two clean mechanisms exist for feeding id261's 12-byte catalog
  entry: (a) RELOCATION (VirtualAllocEx buffer + repoint the extended-catalog lea @ `0x1402B8CDE`) or (b) REUSE
  the proven catalog-accessor hook `0x1402B8CB8` with cloneId=37. Chose **(b)** -- simpler, reuses the tested
  `AccessorCloneHook`, and avoids the lea-disp32 formula (the journal records it two conflicting ways). Relocation
  stays the documented fallback IF live testing shows the accessor hook does not reach the boot registry.

- **RESIDUAL WALL (unchanged).** Even with category + catalog + a NAME (a 262-row `item.en.nxd`), the boot-built
  registry SIZE (261) may still gate the render; the pre-1.5 category hook reportedly bypassed the category check
  and rendered, but that is UNVERIFIED for 1.5. If size is the wall, the fix moves to pre-boot seeding (findings
  9-13) -- the boot-built registry is still UN-LOCATED. The display rig is buildable + unit-testable regardless;
  the in-game render is a manual live step (arm the rig, `capdisplay_setcat` each candidate, open the weapons tab).

**NEXT (display):** build a `CapBreakDisplayHook` rig (FFTHandsFree, branch `capbreak-equip`) parallel to the
equip rig -- catalog hook (0x1402B8CB8 -> clone 37) + a SETTABLE category-getter hook (`capdisplay_setcat <addr>`,
no address baked in) + the count-cap/4-display-caps/count[261] byte patches, off-by-default + reversible, scoped
261-511. Then live-test each category candidate above.

## 2026-06-26 (session 2 cont.) -- DISPLAY rig BUILT + deployed; live test hit two crash walls

Built `CapBreakDisplayHook` (FFTHandsFree `capbreak-equip`, via the /build pipeline: catalog hook 0x1402B8CB8 ->
clone 37 + settable category hook via `capdisplay_setcat <addr>` + 6 byte patches with a `ClaimablePatch`
claim-if-unpatched seam + S2 mutual-exclusion guard vs the equip rig; 4853 unit tests green, off-by-default,
reversible, scoped 261-511). Deployed (BuildLinked). Live test via the bridge found the rig is NOT live-viable
as built -- two distinct crash sources, neither yet worked around:

- **CRASH SOURCE A -- equip selector (the known equip wall).** Arming + opening a unit's equip screen -> weapon
  picker AVs. This is the documented equip wall: the equip path resolves the weapon's stats via the encrypted
  thunk `0x1402B8CE8`, which returns NULL for id261 -> AV. The display rig DELIBERATELY does not handle that thunk
  (display does not need it). So the unit equip/weapon-picker screen is OFF-LIMITS for the display rig -- it is an
  equip concern, not a display one.
- **CRASH SOURCE B -- party/roster render (BLOCKS the inventory path).** Arming (catalog hook + the 6 byte
  patches, category UNSET) then opening the party menu crashes -- BEFORE reaching the inventory. The journey's
  intended display screen is `PartyMenuInventory`, reached ONLY via `WorldMap -> Escape -> PartyMenuUnits ->
  OpenInventory` (op-notes line ~373); you MUST pass through `PartyMenuUnits` (renders the roster/units), and that
  render is what dies. This matches **finding #1's COUNT-CAP ROSTER CRASH**: lifting the count-getter cap
  `0x140284878` (01->02) makes a unit scan run past real entries -> `movzx r10d,[r8+0x56]` @ `0x1402858D4` with
  r8=0xFF (empty slot). The rig bundles the count-cap un-toggleably, so it fires on ANY roster render. => the
  count-cap patch is the prime suspect; it gates the very path to the inventory. (Finding H's earlier "no crash
  on inventory" likely reached the inventory without re-triggering the unit scan, or via a different nav.)

- **CATEGORY-CANDIDATE TRIAGE (disasm of all 8, read-only, no writes).** Only TWO of the 8 are SINGLE-ARG and thus
  compatible with the rig's 1-arg `AccessorCloneHook` (delegate `nint(nint rcx)`, which forwards ONLY rcx):
  - `0x140285036` -- a ROSTER/UNIT function (indexes roster base `0x1411A7D10` with a `0x258`/600-byte unit
    stride; takes a unit index in edi). NOT the category getter. This is what crashed when set as candidate 1.
  - `0x14028752d` -- the SELL-PRICE getter (masks id in cx, calls catalog accessor 0x1402B8CB8, returns
    `word[rax+8] >> 1` = price/2, min 1). NOT the category getter.
  The other SIX (`0x140286d26, 0x140287464, 0x14028875f, 0x140288ded, 0x14028b20b`, and the multi-call
  `0x1402875dc`) are MULTI-ARG (read rdx/r8/r9 -- e.g. `0x1402875dc` reads a per-id byte table at rip+0xf20102
  and ADDS edx, with a hard `cmp cx,0x105` (261) bound that returns 0 for id>=261; `0x14028b20b` compares TWO
  ids). A 1-arg detour would call them with garbage in rdx/r8/r9 -> crash/garbage. NONE cleanly returns a small
  0-4 category-group code. **=> the 8-candidate heuristic list does NOT contain a clean, rig-compatible 1.5
  category getter.** CE find-what-accesses is the real method (as predicted) -- and the true getter may be
  multi-arg, needing a multi-arg hook variant, not the current 1-arg `AccessorCloneHook`.

**STATUS:** instrument BUILT + verified (logic green); live path BLOCKED by crash source B (count-cap roster
overrun) before display can even be evaluated. No persistent damage (reboot reverts; never saved with id261
equipped). Code is UNCOMMITTED (verify-live-before-commit -- and it does not pass live yet).

**NEXT (deliberate, not a blind crash-walk):**
1. **Per-component arm verbs** (catalog-only / count[261]-only / count-cap-only / display-caps-only) so the
   party-render crash is bisectable WITHOUT guessing -- confirm `0x140284878` is the killer, then find whether
   id261 can be made "owned" WITHOUT lifting that getter cap (e.g. does the inventory list-builder read the count
   array `0x1411A7C00+id` directly vs via the capped getter? if direct, drop the count-cap patch entirely).
2. **CE find-what-accesses** for the true 1.5 category getter: breakpoint/FWA on a KNOWN weapon's catalog record
   while opening `PartyMenuInventory`, to catch the function the list-builder calls to categorize items. Note its
   arg count (single vs multi) -- drives whether the rig needs a multi-arg hook.
3. The settable category hook (`capdisplay_setcat`) is ready to test the address the moment CE finds it.

## 2026-06-26 (session 3) -- EQUIP re-attack: the "DRM floor" was a hook-method bug; the real fix BUILT + deployed

Premise correction that reframes the whole equip wall: the encrypted weapon-stat accessor `0x1402B8CE8` is a
**5-byte `jmp rel32` THUNK** (`e9 0b 6d e2 0f` -> 0x1500DF9F8). Reloaded.Hooks (`CreateHook`) installs a
~13-byte `jmp [abs]`+pad detour over it because the real detour is >2GB away -- clobbering **8 bytes past** the
5-byte thunk and corrupting adjacent code the resolvers use -> the party/formation render crash that every prior
session read as a Denuvo floor. **It was never a floor; it was the hook METHOD.** The encrypted accessor returns
NULL for id261 (internal id-cap); cloning `ECX = id -> a low in-bounds id` BEFORE the accessor resolves it. We
only needed a hook that occupies EXACTLY the 5 thunk bytes.

**THE FIX (built this session, FFTHandsFree branch `capbreak-equip`, UNCOMMITTED, deployed):** a 5-byte
`jmp rel32` redirect into a hand-written near stub (VirtualAlloc'd within +-2GB so the rel32 reaches), occupying
exactly the thunk (zero clobber). Stub: `mov eax,ecx; and eax,0x3FF; cmp eax,261; jb pass; cmp eax,511; ja pass;
mov ecx,cloneId; pass: jmp qword[rip+0]; dq 0x1500DF9F8` -- 40 bytes, byte-exact-tested (the load-bearing test;
non-vacuity proven empirically by the verifier). The stub->target jmp is rip-relative-indirect (range-free); only
thunk->stub needs +-2GB. Restore rewrites the saved 5 bytes (fixes the old secondary bug where Reloaded's
`Disable()` left the clobbering jmp in place) and leaks the stub page (UAF avoidance). Auto-detected at Install
(byte0==0xE9 -> stub path; else the catalog accessor's clean Reloaded path, unchanged).

Two INDEPENDENT settable clone ids (the resolver calls both accessors with the raw item id, so they don't have to
match): **catalog `0x1402B8CB8` -> 37** (Chaos Blade KnightSword = a render TYPE Ramza the Gallant Knight can
equip) + **weapon-stat `0x1402B8CE8` -> 67** (Warbrand, PROVEN in-bounds ItemWeaponData row). New
`capbreak_arm_equip` arms {catalog hook + weapon thunk redirect + clamp `0x140284c82` 06->07} and DELIBERATELY
OMITS the count-cap `0x140284878` + count[261] -- those caused the section-H/finding-#1 roster-render crash B,
and a force-equip (direct roster write) needs neither. `capbreak_setclone <catalogId> <weaponId>` retargets both
while disarmed. Off by default; deployed via BuildLinked. Suite 4853 -> 4892 pass / 2 skip / 0 fail (+39).

**LIVE TEST PENDING (game was off this session).** Sequence: boot -> confirm `0x1402B8CE8` still
`e9 0b 6d e2 0f` (re-base guard) -> `capbreak_arm_equip` -> open a NORMAL (id<261) equip screen (no crash = the
clobber bug is DEAD, the headline) -> force-write id261 (u16 `05 01`) to Ramza rHand `0x1411A7D24` -> open
Status/equip + attack in battle. Renders+damages = equip wall (and maybe the equipped-render wall, since that
render also consults this NULL-returning thunk) falls. "Nothing Equipped"/fist = thunk fixed but a downstream
gate remains (journal predicted "more gates; iterate via crash-probe"). Crash = a NEW gate past the thunk -> log
the faulting address. Result goes here + in LIVE_LEDGER once run. Pre-commit TODO: extract the 302-line
`CapBreakEquipHook` byte-patch seam.

**LIVE RESULT (session 3 cont.): weapon thunk VINDICATED; the catalog HOOK clobbers r11 (new root cause).**
Armed the minimal equip rig; opened a NORMAL equip screen -> crash. CE (VEH debugger -- usermode, no kernel
driver, survived Denuvo) caught the AV. The weapon thunk fix is PROVEN GOOD: read live, 0x1402B8CE8 = the
5-byte jmp to our stub @ a VirtualAlloc'd page; the stub is byte-exact (mov eax,ecx; and 0x3ff; cmp/jb/cmp/ja
261/511; mov ecx,67; jmp [rip]->0x1500DF9F8); the adjacent code at 0x1402B8CED is UNTOUCHED (the old clobber
is gone). The crash is a DIFFERENT bug:
```
+F0B06A2  call 0x1402B8CB8            ; catalog accessor -- our Reloaded.Hooks MANAGED detour
+F0B06A7  test [rax+3],8             ; rax (record) fine
+F0B06AD  movzx ecx, word [r11+0x1e] ; reads id from r11 -- r11 GARBAGE = 0x7FFC2CB628D8 (.NET GC-heap band)
+F0B06B2  call 0x1402B8E14           ; accessory thunk (jmp 0x1500F41A1), fed the garbage id
+F0B06B7  mov cl,[rax]               ; NULL -> crash
```
The equip-stat resolver keeps r11 (its struct pointer) LIVE across the catalog call. The ORIGINAL catalog
accessor (0x1402B8CB8, a tiny native leaf: movzx/mov/cmp/lea/lea/jae/lea/add/ret) never touches r11, so the
compiler trusted it. **Our Reloaded.Hooks managed detour clobbers the volatile r11** -> every [r11+..] read
after the first catalog call is garbage -> the resolver feeds a junk id to the next type-accessor (here the
accessory thunk 0x1402B8E14) -> NULL -> AV. => the session-2 "CATALOG HOOK IS SAFE" claim was WRONG: it was
never run through a resolver that relies on r11. (The crash hit the ACCESSORY accessor only incidentally --
because the garbage-fed slot happened to test as an accessory; it is NOT that id261 is an accessory.)

**FIX (building): drop the catalog HOOK; RELOCATE the extended catalog.** Patch the accessor's extended-branch
disp32 @ **0x1402B8CDE** (verified live this session: `lea rax,[rax*4 + 0x67F910]` @ 0x1402B8CDA, disp32 at
+4) to point at a VirtualAlloc'd buffer holding ids 256-260 (copied from 0x14067F910+id*12) + id261 = clone
of id37's MAIN record (0x14080EA90+37*12 = 0x14080EC4C). new_disp32 = buf - 0x140000000 (signed, buffer
within +-2GB via INearAllocator). The accessor then runs 100% NATIVE -> no managed detour -> r11 (all regs)
preserved -> normal screens work AND id261 resolves. Keep the weapon thunk stub (register-safe). This is the
journal's documented relocation fallback, and section H already proved it live on this build (256-260
resolved from the buffer, no crash). Two hook-method bugs found+fixed (weapon clobber-by-Reloaded-13-byte,
catalog clobber-by-managed-r11); NEITHER is a Denuvo floor. CE method that worked: external CE VEH debugger,
"let it crash + read RIP" (no breakpoint -- software BP at the thunk fights our arm-write; HW BP via the
breakpoint-list pane is the alternative).

## 2026-06-26 (session 3 FINAL) -- WALL DOWN: id261 EQUIPS, DISPLAYS as "Moonblade", and HITS for 323 (LIVE)

**The equip wall is broken.** id261 (past the engine's hard 261 cap) now equips on a real unit, renders with
its NXD name, and deals real weapon damage in battle. Live-verified end to end this session.

THE FOUR GATES (all beaten; NONE a Denuvo floor -- each was a hook-method / register bug):
1. **Catalog** -- the Reloaded managed detour on 0x1402B8CB8 clobbered volatile r11 (live r11 came back a .NET
   GC-heap ptr). FIXED by RELOCATION (ExtendedCatalogRelocator): patch the extended-branch disp32 @
   0x1402B8CDE to an 8 KB VirtualAlloc buffer holding ids 256-260 + id261 = clone of id37's MAIN record. The
   accessor runs 100% native -> no clobber. Live: normal equip renders clean armed.
2. **Weapon-stat accessor** 0x1402B8CE8 -- a 5-byte jmp thunk; Reloaded's 13-byte detour clobbered 8 adjacent
   bytes. FIXED by a 5-byte near-stub redirect (ThunkRedirect) cloning ids 261-511 -> 67 (Warbrand row, in
   bounds). Live: byte-exact stub verified in-process; adjacent code untouched.
3. **Validity check** 0x1402B8F30 (`jmp 0x1501484E0`, a 5-slot loop that zeros "invalid" slots) -- zeroed
   id261 back to 255 even after gates 1-2. FIXED by the SAME near-stub redirect cloning 261-511 -> 37, so the
   check sees a valid id and leaves the slot. Live: id261 STOPPED zeroing, stuck in the roster.
4. **Equip clamp** 0x140284c82 -- byte patch 06->07 (keep id 261, not bounce to fallback).

LIVE RESULTS (Gallant Knight Ramza, roster rHand 0x1411A7D24 = 0x0105):
- Formation/equip screen: weapon slot shows **"Moonblade"** (id261's item.en.nxd name), persists (no zeroing).
- Battle CONSTRUCTION keeps id261: combat struct CWeapon (+0x20) at 0x141855D00 = 0x0105 = 261 (NOT dropped
  / NOT truncated; AI slots = 0x00FF for contrast). combat_scan.py is the live tool (base 0x141853CE0,
  stride 0x200, weapon +0x20).
- Battle DAMAGE: Ramza's basic attack hits for **323** = PA 22 x Warbrand WP 15 (~330). The weapon-stat thunk
  (261->67) drives battle damage -- a fully functional weapon. (Section A's earlier "fist" was pre-hook.)

REMAINING GAP (cosmetic, NOW CRACKED): the in-battle weapon **MODEL** does not draw for id261 (Ramza swings
empty-handed). Investigated live and **the old "asset-walled" verdict is OVERTURNED**:
- find-what-accesses on combat `CWeapon` (`0x141855D00`) during idle battle caught ONLY the status-menu
  readers `0x140360383` (`movzx eax,[rdi+0x20]`) + `0x140360396` (`+0x22`) -- they fire ONLY on status-menu
  open. NOTHING reads CWeapon per-frame. => the battlefield swing model is **baked at unit CONSTRUCTION**,
  not live-read.
- The status/menu model preview DOES live-read CWeapon: writing `CWeapon`=67 made the status screen show
  Warbrand. (So the menu/preview model is live-redirectable; the battlefield swing is the baked one.)
- **DECISIVE PROOF:** set roster rHand `0x1411A7D24`=37 (Chaos Blade, a real-model weapon) → entered a
  battle → **Ramza swung a VISIBLE Chaos Blade on the field.** `0501`=261 → empty. So the battlefield model
  bakes from the equip-slot id AT CONSTRUCTION -- present a real-model id at build time and the unit swings it.
=> The cosmetic is REDIRECTABLE, not asset-walled. The fix is a battle-entry **slot-swap loop** (roster
261→37 for the construction window, →261 in menus so saves/names stay "Moonblade") -- the same background-loop
pattern the project already runs (puppet-hold/stat-hold) -- OR hook the (Denuvo-indirect) construction read
directly. NOT yet built. **NET: every layer of the cap (equip / stats / validity / menu display / battlefield
model) now has a proven live answer. The "weapon art is walled" ledger row is overturned.**

PRODUCTIONIZATION (engineering, not RE -- all walls mapped + beaten): (1) move the rig from the FFTHandsFree
research bridge into the shipping mod `prawl.fft.livingweapons`; (2) build the model slot-swap loop (or the
construction-read hook); (3) extend ItemWeaponData for distinct stats if >128 distinct weapons wanted
(same relocation technique). ~250 new ids reachable (261-511, catalog mask 0x1FF), ~120 trivially (the equip
clamp disp8 tops out ~382 before re-encoding).

THE RIG (FFTHandsFree branch `capbreak-equip`, UNCOMMITTED, off-by-default, reversible): verbs
`capbreak_arm_equip` (relocator + weapon thunk + validity thunk + clamp; NO count-cap), `capbreak_setclone
<catalogId> <weaponId>` (default 37/67), `capbreak_status`, `capbreak_disarm`. Suite 4853 -> 4920 pass / 2
skip / 0 fail. Built the full way (plan -> adversarial plan review -> TDD implement -> independent verify) across
three increments (thunk fix, relocation, validity hook). New files: ThunkRedirect.cs, INearAllocator.cs,
ExtendedCatalogRelocator.cs (+ AccessorCloneHook thunk auto-detect). All addresses re-based + re-confirmed
live this build. CE not needed for the wins (RPM-capstone disasm + the bridge sufficed; CE VEH only used once
to catch the r11 AV).

PRE-COMMIT TODO: extract the CapBreakEquipHook byte-patch seam (now ~340 lines); flip LIVE_LEDGER rows to
PROVEN (Patrick); then stage the commits green-gated. The "boot-built registry gates everything" thesis
(findings 1-13 / section H) is now DEFINITIVELY RETIRED for equip: it was a chain of hookable plain/thunk
gates + one register-clobber, not a registry. ~121 weapons / 240 items / 128 ItemWeaponData rows -- new ids
past 261 reuse a stat row via SecondTableId (Moonblade -> 67).

## 2026-08-26: the 1.5.2 re-anchor. Every gate found again; disk checking is blind here; a live read settled it

Resuming the arc (ledger row LW-346). Every rig address above was found on the 1.5.0 binary and
the game recompiled twice since (1.5.1, 1.5.2), so step zero was proving where the gates live now.

**Offline exe diffing CANNOT answer for these sites.** The gate sites sit in Denuvo-processed
ranges: the bytes on disk at those addresses match the running image in NO build, including the
very build the rig was live-verified on (the rig's own oldbytes are absent from that build's exe
on disk). The catalog DATA tables are plaintext on disk (and identical 1.5.1 to 1.5.2); the code
sites are not. Probe: tools/probes/lw346_capbreak_reanchor.py (offline, both backups vs current).

**Live read-only pass (game at the world map, plain RPM from outside, zero writes):** probes
tools/probes/lw346_capbreak_live_bytecheck.py then lw346_capbreak_live_confirm.py. All ten sites
drifted, but as two INTACT block slides with internal spacing byte-identical: the equip-resolver
and display-cap region moved -0x78 (resolver pair spacing 0x40B preserved) and the catalog
accessor cluster moved -0x74 (lea to weapon thunk 0xE, lea to validity thunk 0x256, both
preserved). Full instruction shapes re-read at every new site, each with a shifted-address
negative control; the catalog lea matched with its disp32 intact.

Old to new (1.5.2, PE key 0x6A5EA53C):
- equip clamp byte: 0x140284C82 to 0x140284C0A
- count-getter cap byte: 0x140284878 to 0x140284800
- display caps 1-4: 0x14028479C/0x140284841/0x140288D52/0x1402890EC to
  0x140284724/0x1402847C9/0x140288CDA/0x140289074 (all cmp imm32 0x105 shapes confirmed)
- catalog accessor entry: 0x1402B8CB8 to 0x1402B8C44 (id<256 branch shape confirmed)
- catalog extended-branch disp32: 0x1402B8CDE to 0x1402B8C6A (disp32 value 0x0067F910 unchanged)
- weapon-stat thunk: 0x1402B8CE8 to 0x1402B8C74 (E9, target now 0x14FE85562)
- validity thunk: 0x1402B8F30 to 0x1402B8EBC (E9, target now 0x14FED2C3F)

UNCHANGED: extended catalog base 0x14067F910, main catalog base 0x14080EA90, count array
0x1411A7C00 (64/64 first bytes read as plausible inventory counts live). Thunk TARGETS moved but
the rig decodes them live at install, so only site constants needed updating.

Wired into FFTHandsFree branch capbreak-equip 2026-08-26: commit 7ffa5cb (constants, comments,
tests), plus bb92fe1 fixing a pre-existing strict-mode roster red (seven DoAction/randmod verbs
were registered without roster entries). Suite 4934 pass / 0 fail. Note the earlier "THE RIG ...
UNCOMMITTED" line in this journal is stale: the branch was committed and pushed before this
session, and now carries the re-anchor on top.

NEXT: the owner-driven live probe on 1.5.2: enable the HandsFree mod, capbreak_status (read-only
byte check through the rig's own eyes), then capbreak_arm_equip and re-prove the Moonblade equip
chain (clone ids 37/67). The ledger rows [id261-equips-displays-moonblade],
[id261-functional-in-battle], [id261-model-construction-slot-swap] still await the owner's PROVEN
flips from 2026-06-26.

### Same day, the live re-prove on 1.5.2: PASSED first try

Owner at the game, world map. Deployed the re-anchored rig (HandsFree bb92fe1+7ffa5cb),
capbreak_arm_equip armed clean on the first attempt (every byte guard matched at the new
addresses; catalog buffer 0x136F60000). Wrote roster slot0 rHand 0x1411A7D24 = 261, owner opened
the equip screen: **id261 HELD the slot, no crash, rendered as "Moonblade" with its own
description ("A pale crescent blade that drinks the moonlight"), WP 15 (the id67 Warbrand stat
clone), category Sword, and the Living Weapon Kills line painted.** Only the menu icon is blank
(no art; the battlefield model remains the unbuilt construction slot-swap). Restored rHand=43,
disarmed, verified released. The rig's call counters read 0 by design (the hooks run as pure
native stubs); the proof is the screen itself, since unhooked id261 cannot survive the validity
zeroing loop nor resolve stats without the NULL-deref crash.

WHY THE NAME SHOWED: the shipped mod/FFTIVC/data/enhanced/nxd/item.en.nxd on main has carried the
prototype's 262nd row (Moonblade) since the June work, unnoticed and harmless (row 261 is
unreachable without the cap-break; live since then with zero player reports). So name, description,
and the Kills anchor all come free once the id resolves. Also observed: three installed mods ship
item.en.nxd (livingweapons, offensivechemist, wotlequipmentreplacer) and ours won the table merge.

STATE AFTER THIS PASS: the four-gate equip chain is re-proven end to end on current retail 1.5.2.
The three LIVE_LEDGER rows ([id261-equips-displays-moonblade], [id261-functional-in-battle],
[id261-model-construction-slot-swap]) now have a second dated confirmation for the first row's
scope; PROVEN flips remain owner-only. Unbuilt work is unchanged: construction slot-swap loop,
rig port out of the research bridge, >128 distinct stat rows, and the standalone framework
question (owner to name it).

### Same evening, two more unknowns fall: custom ICONS proven, custom STATS free in V1

**ICONS SOLVED (owner-witnessed, screenshot 18.11).** The blank icon was a missing SECOND file.
An item's menu icon is a pair per surface: the pixels (`ui/ffto/icon/equip_item/texture/
ei_<id>_uitx.tex`, filename IS the item id; small-list variant under `equip_item_s/` as
`ei_s_<id>`) plus a tiny parts file (`.../textureparts/ei_<id>_uitx.utexpt`, ~0x5C bytes: a
header and the embedded path string of its own .tex). Shipping only the .tex registers fine but
the game's icon load asks for the .utexpt and gives up ("not found/empty" in the modloader log,
which is what cracked it, per the serve-vs-registration rule). Recipe for a NEW id: copy any
donor pair, byte-patch the embedded `ei_037` style path to the new id (same length), ship all
four files. Proven live on id 261: both surfaces rendered the donor art. Other mods already ship
new-id .tex + .utexpt pairs (face portraits), so the modloader path was never in doubt. Donor
.utexpt extraction: FF16Tools `unpack -i data/enhanced/0008.pac -g fft -f <path>`.

**STAT READS FUNNEL THROUGH THE HOOKED ACCESSOR (owner-read WP flip).** With id 261 equipped,
swapping the rig's weapon clone row 67 (Warbrand, WP 15) to 37 (Chaos Blade, WP 28) flipped the
equip card's WP to 28 on reopen. Combined with June's 323-damage battle hit through the same
hook, every consumer of weapon stats resolves through the accessor we own. Consequence for the
extended-items system: V1 hands out FULLY CUSTOM stats by returning a pointer to an LW-authored
row buffer; no ItemWeaponData relocation and no donor-clone limitation for stats.

Remaining unknowns after tonight: boot-registry inventory-list visibility (arm-at-boot, the
make-or-break; probe needs a marker-file-gated auto-arm build of the HandsFree rig and a
backed-up save, minding the 2026-06-10 auto-arm scar and checking the "+" items 256-260),
nxd merge semantics, the construction slot-swap loop on 1.5.2, and the acquisition tables
(shop/poach/Move-Find, phase 3 by design). Probe litter: the four ei_261 icon files live in the
DEPLOYED livingweapons mod folder only (not the repo); the next BuildLinked sweep removes them.

### Late evening: the registry hunt narrows the wall to one builder; four hypotheses retired

Built the marker-gated BOOT-ARM path (FFTHandsFree f3ba480 + 1777eb0: arm the rig during mod
load, before the game boots, with experimental "patch 0xADDR OLD NEW" lines readable from the
marker file so a hunt round is a text edit plus relaunch). Boot-armed runs confirmed working
(rig armed pre-boot, all byte guards green, "+" items 256-260 audited clean pre/post). RESULT:
id261 still absent from the party inventory and the equip picker. Retired tonight, each with
its evidence:
- The six known registry patches applied PRE-BOOT are not sufficient. (Round 1.)
- Three loop-bound candidates from the live imm scan (0x140397121 incl. the journal's
  0x140392xxx hint, 0x14039678F, 0x1402D679B) patched at boot: no effect. (Round 2; probe
  tools/probes/lw346_registry_cap_scan.py enumerates every instruction-shaped 261/260/262
  immediate in .code.)
- ItemData.xml row 261: SILENTLY DROPPED. The modloader log proves XML table merge has no
  added-row path, while nex/nxd tables DO ("Processing 1 added rows for nex table 'item.en'"
  is our Moonblade name row loading). Data-side minting cannot ride the XML tables.
- The 1.5 category system is RANGE TABLES, not the pre-1.5 plain getter: the modloader itself
  logs "Found ItemDataTypeToItemIdRangeData @ 0x14067FB38" and "Found ItemIdRangeToCategoryData
  @ 0x1406804E0" (which is why the June-26 8-candidate getter hunt found nothing the right
  shape). The category fence (final boundary u16 261 at 0x1406804FA) widened to 262 live: no
  effect on an already-open session (boot-time effect untested). Dump probe:
  tools/probes/lw346_idrange_tables_dump.py.
- The June-26 "category candidate" 0x1402875dc descendant (0x140287563 on 1.5.2) is an
  adjust-per-id-byte-with-clamp routine over a SECOND per-id array at 0x1411A76FF (id bound
  imm at 0x14028756F); that array reads all zeros on a 99-of-everything save, so it is not the
  owned-count bag either.

TWO PRECISION FACTS FOR THE NEXT SESSION: (1) the equip picker's E-badge walk is a registry
scan (equipping id261 then opening the picker shows NO badge and the cursor defaults to id257,
the first extended id); (2) the return-to-bag path is registry-gated too: swapping the equipped
id261 for a real weapon makes the Moonblade EVAPORATE (nowhere to land, dropped silently,
hand restored clean). NEXT: the June-4 documented method, owner + Cheat Engine
find-what-accesses while the picker builds (usermode VEH survives Denuvo on this exe), with
the E-badge compare or a known item's Equipped/Held count as the tripwire. All probe writes
reverted this session; the boot-arm marker removed; residual in-process patches die at exit
(owner quits without saving).

### Night: the June-4 recipe fully ported; the list-builder accepts id 261; a post-build clean-up throws it out

Plain language: for the first time on 1.5.2 the party inventory made room for the Moonblade
(112 rows instead of 111) and then crashed on that last row, because a clean-up pass that runs
after the list is built deleted the Moonblade again without telling the menu. The old walls are
down; one more sits behind them.

What changed tonight (all read-only RPM plus the marker-gated boot-arm, no Cheat Engine):
- Nenkai's modloader source settled the 1.5 category model: item id -> `ItemIdRangeToCategoryData`
  (14 u16 starts at 0x1406804E0) -> category -> `ItemCategoryToDataTypeData` (14 u32 at
  0x14067FB78) -> data type; id 261 = category 13 = DataType 9, a non-weapon type. All three
  tables are sparse-XML moddable and patched pre-boot. Two data-only rounds were NULLS
  (category 13 -> DataType 5 weapon2; fence 261 -> 262 making 261 an accessory): the list does
  not consult these tables at build time, it asks the plain category getter.
- The June-4 category getter on 1.5.2 is 0x1402890C0 (pre-1.5 0x140284F3C + 0x4184, the same
  slide as the bag/equipped/total getters and display caps 3/4; entry int3-padded, shape
  byte-exact). The party-inventory list builder (0x140288B94, loop cap = display cap 3) calls
  it right after total-owned 0x140287530. The third June-4 cap is 0x140397121 (builder
  0x140396FEC); no round had carried it.
- FFTHandsFree boot-arm marker gained `cathook 0xADDR CLONEID` (AccessorCloneHook at boot, ids
  261-511 -> 37). Live: hook armed, builder A returned 112 rows, the UI row table's row 111 read
  0x3FF (a masked 0xFFFF terminator), the word buffer 0x141811470 held 111 entries with a
  double terminator, and the detail panel crashed at 0x14029FC5B (catalog accessor fed id
  0x3FF -> unmapped pointer). Three identical crashes, dumps parsed for the register context.
- The deletion mechanism: seven plain-code passes call the validity thunk 0x1402B8EBC per list
  word and compact rejects in place (sorter 0x140285B10 and siblings, listed in handoff.md).
  The validity routine behind the rig's stub (0x14FED2C3F, decrypted in memory) is a pure id
  range check (1..253 or 256..260) that should accept 261 as the clone 37; which pass rejected
  it is the open question. Next: patch every compaction branch to "keep" (behaviour-preserving
  for vanilla ids) and bisect.
Rig change uncommitted on capbreak-equip pending that run. The address table that section
leaned on is banked in the next section (handoff.md is disposable).

### 2026-08-27 early: the clean-up-pass theory is retired before a relaunch; the real gate is the default-order rebuild

Plain language: the night plan was to force every "throw this id out" branch to "keep" and
relaunch. Reading the running game first (no patches, no debugger) showed those branches can
never throw the Moonblade out, so that relaunch would have proven nothing. The pass that
actually drops it is the one that puts the list into the game's default display order: it
rebuilds the list from a fixed per-tab table of item ids, and any id the table does not know
(261) is silently left behind. The table is filled at load time, not baked into the exe.

Facts, all from read-only RPM on the round-3 boot (probes in tools/probes: compaction_audit,
xref_scan, live_disasm, inventory_snapshot):
- The validity routine behind the thunk 0x1402B8EBC (rig stub 0x114AC0000, 261..511 -> 37,
  original 0x14FED2C3F) is `valid = (id-1) <= 252 || (id-256) <= 4`, result in EAX (= r8d = 1);
  both hidden constants re-read live (0x154F773F4, 0x14EC34A0C). Every compaction pass (sorter
  0x140285B10 and the five siblings at 0x140286265 / 0x1402879C3 / 0x140287B23 / 0x140288175 /
  0x1402882E2) masks the word with 0x3ff and calls that thunk, so 261 is KEPT by all of them.
  The proposed branch bytes (75 25, 75 23, 0F 85 x4) all matched live; the patch is simply
  aimed at a gate that is not closed.
- Party-inventory pipeline: dispatcher 0x14036C3EC (dl==0) -> builder A 0x140288B94 with
  cx=-1, dx=-1 (no internal sort), group 5 for the weapons tab, r9 = the global word buffer
  0x141811470, 6th arg 0 (count source = total-owned 0x140287530). A's return count is passed
  to 0x14036B18C and never read there. 0x14036B18C reads the menu's sort mode (0x14019879C,
  panel +0xB18/+0xB1C; -1 = default) and calls either 0x140285DF0 (default order) or
  0x140286228 (compaction + swap-only bubble sort via comparator 0x14028B180); the WIDGET COUNT
  is that call's return (0x1402A3230(panel+0x58, count, ...)); rows come from 0x14036B2D0 ->
  0x14028B65C, row id = word & 0x3ff (a 0xFFFF terminator row therefore reads 0x3FF, the
  crash id).
- 0x140285DF0 (default order): counts the order table until a 0x00FF word or a word >= 0x105
  (imm byte 0x140285E2D), then for each table id copies the matching list word into a stack
  temp, memcpys the temp back over the list, returns the matched count. Ids absent from the
  table are dropped. The live buffer order (257, 1, 2, 4, ..., 32, 256, 33, ...) equals the
  weapons order table exactly, which is how the path was identified.
- Order tables: pointer array 0x14067F498 (tab 0 weapons -> 0x1407B2550, 1 -> 0x1407B2678,
  2 -> 0x1407B24B0, 3 -> 0x1407B2500, 4 -> 0x1407B26B8). The weapons table holds 127 ids, no
  261, sentinel 0x00FF at index 127 (0x1407B264E), zero padding after. Section .xpdata (RW
  initialized data); on disk those bytes are FF, so the table is RUNTIME-FILLED (a
  struct-to-globals copier at 0x1403271B3.. writes it). A boot-time byte patch of the table
  can be overwritten by a later load; patch after the save loads, or hook the routine.
- List-length bounds (read 2026-08-26 on the round-3 boot, same probe): builder A stops
  appending at 145 entries (`81 FE 91 00 00 00` cmp esi,0x91 / jge at 0x140288CC1..0x140288CC7,
  right after the word store and `add r13,2`), so every list it produces holds at most 145 words
  plus the 0xFFFF terminator; the Acquired list scan in 0x140286228 stops at 0x92 words
  (`cmp edx,0x92` at 0x140286318). The rig's OrderRebuildHook reads a 160-word window on the
  strength of those two bounds.
- Entry and terminator of 0x140285DF0 (read 2026-08-26 on the round-3 boot with
  tools/probes/lw346_live_disasm.py, capstone over RPM): the routine begins
  `48 89 5C 24 08` mov [rsp+8],rbx / `48 89 6C 24 18` mov [rsp+18h],rbp / `48 89 74 24 20`
  mov [rsp+20h],rsi / `57` push rdi / `48 81 EC 40 02 00 00` sub rsp,240h (a 0x240-byte
  stack temp, security cookie at [rsp+230h]). Its tail stores bp (= 0xFFFF) into the temp at
  index count (`66 89 6C 54 20` at 0x140285E94), then memcpys count*2+2 bytes back over the
  list (`lea edx,[rbx*2+2]` at 0x140285E99, call 0x1405C9D80 at 0x140285EA8) and returns ebx.
  So the output is always a subset of the input words, in table order, 0xFFFF-terminated. The
  rig's OrderRebuildHook pins those first ten prologue bytes as its stale-address guard.
- More hardcoded 0x105 guards on list paths: 0x1402862F7 (mode-8 move-to-front scan inside
  0x140286228) and 0x140285EE7 (`lea r10d,[r11+6]` in the delete-from-list routine
  0x140285ED8). Builder A's own tail (bl==0 path, 0x140288D5D) also calls the default-order
  rebuild.
- Still open: why the widget showed 112 rows against a 111-entry buffer (the default-order
  path returns the matched count, which would be 111). The 0xFFFF at [112] and [266] are
  consistent with leftovers from earlier, longer builds rather than proof of one deletion.
  Next look (zero patches, current boot): open Items -> Weapons, run
  `python tools/probes/lw346_inventory_snapshot.py --rows` (buffer, sort modes, widget, row
  table); then switch the sort to a non-default mode and re-run: 0x140286228 should keep 261.


### 2026-08-26 ~20:55: WALL DOWN on 1.5.2. The Moonblade is LISTED in the party inventory with a working detail panel

Plain language: with three bytes poked into the running game (no relaunch), the Moonblade
showed up as the last row of the Weapons tab, and moving the cursor onto it drew the full
item card: sword icon, "Sword" category, name, attack 15, evade 0%, the custom description,
and the "Kills: 0" line. No crash. Owner-observed (screenshot
tools/probes/lw346_moonblade_listed_152.png).

Recipe (round-3 boot: rig boot-armed by the marker incl. `cathook 0x1402890C0 37`, then from
outside via tools/probes/lw346_order_table_poke.py --apply while the game sat at the menu):
- weapons order table [127] `0x1407B264E`: 00FF (end marker) -> 0105 (id 261)
- weapons order table [128] `0x1407B2650`: 0000 (padding) -> 00FF (end marker)
- default-order scan guard `0x140285E2D`: 05 -> 06 (`mov eax,0x105` -> `0x106`)
Then close and re-open the Items screen. Snapshot after re-open: word buffer 112 entries with
261 last, row table 112 rows in the same order (row 111 = 261). Count and rows agree, which is
also the first direct evidence that the earlier 112-vs-111 disagreement came from the dropped
entry and not from a separate count source.

Why the table poke must happen AFTER load: the table bytes are FF on disk and get filled by a
load-time copier (0x1403271B3..), so a boot-time marker patch would be overwritten or refuse
its old-byte guard. The code byte can ride the marker (`patch 0x140285E2D 05 06`); the table
needs a post-load write (outside poke now; a hook on 0x140285DF0 that appends unmatched ids
is the durable form).

Still to check on this boot: detail-panel pages 2/3, the All tab, the equip picker E-badge
and the swap-off-hand landing (both registry-gated per the LATE handoff).

Follow-ups on the same boot (owner eyes, ~20:57): detail pages 2/3 render (Attribute Bonuses
none, Equipment Effects "Permanent Regen" and the Eligible Jobs grid come from the clone-37
Chaos Blade data behind the thunks; Can Dual Wield / Can Wield Two-handed shown). Sorting the
list keeps the Moonblade (the comparator path 0x140286228 never drops it, as read). There is
no "All" tab in the party Inventory screen. The UNIT equip picker did NOT list it on the first
unit tried: that picker is builder A with 6th arg 3, which (a) takes its count from the
per-id bag array 0x1411A7C00 (the rig's CountArrayBase, [261] = 1 live, cap imm 0x140284800
already widened) and (b) EXCLUDES any item the can-equip check 0x1402886D0(id, unit job,
unit+0x73) rejects (result -1 -> not appended), and that check reads the item record byte +5
through the catalog accessor, i.e. the Moonblade currently carries Chaos Blade's knight-sword
eligibility (page 3 highlights only the Knight). So the picker null is expected for a
non-Knight unit; a Knight-job unit is the discriminating retry.

Unit equip picker, resolved the same night (owner eyes): the picker only listed the
Moonblade after the owner sorted the picker once, and then it EQUIPPED and UNEQUIPPED
cleanly (the LATE handoff's "evaporates on swap-off" no longer reproduces on this boot). Cause,
read live: the picker has its own display-order table set (pointer array 0x14067FA90; weapons
at 0x141874540, reverse-id order ending ", 67, 256, 257"), consumed by the same default-order
rebuild 0x140285DF0 at 0x14033695A, and the game REGENERATES that table from the last
inventory sort. Before the sort it lacked 261 (dropped); after the sort it read
"..., 67, 256, 257, 261" and, with the 0x140285E2D guard widened, the rebuild kept 261. So a
durable fix must cover both tables (or hook the rebuild to append unmatched ids). Also
answered: id 261's item.en row has UiItemCategoryId 3 (Sword) and SortOrder 216 (a tie with
Akademy Blade; the oneoff meant 217), sane values; the sort keys were never the gate, the
id-260 caps on the table generators are.

Owner-spotted regression to chase later (LW-347): with the Moonblade equipped, Ramza's
off-hand empties and refuses a shield, unlike a real Chaos Blade. The rig's 261 record is
"04 16 62 82 25 04 00 05 0A 00 14 00" vs Chaos Blade "04 16 62 82 25 04 00 00 0A 00 14 00":
byte +7 is the only difference (05 vs 00) and is the first suspect.

### 2026-08-26 21:15: the "Acquired" sort crashes the same way, and it names the last list gate

Plain language: sorting the inventory by Acquired crashed the game at the same spot as the
night-3 crashes (owner report; dump FFT_enhanced.exe.20664.dmp parsed with
tools/probes/lw346_minidump_context.py: read of 0x114AA2FF7, rax = rig catalog + 0x3FF*12, rsi
= 0x3FF). Mechanism, from the code already read: mode 8 of 0x140286228 rebuilds the list
with 0x140285DF0 against the ACQUIRED order list (0x141874726, most recent first, 0x00FF end
marker, at most 0x92 words) and DISCARDS that rebuild's return, returning its own compaction
count instead. The acquired list never holds 261 because its maintainer 0x140286160 walks ids
1..260 (`lea edi,[rbp+6]` with ebp = 0xFF, imm byte 0x140286187), so the rebuild drops 261,
the count stays one too high, and the last row reads the 0xFFFF terminator (0x3FF) again. This
is the same shape as the night-3 crash and very likely its actual cause (the owner's sort
setting), which the "clean-up pass" theory had misread.

Fix lines added to the boot-arm marker for the next boot (code bytes, old-byte guarded):
`patch 0x140286187 06 07` (acquired-list maintainer walks ids 1..261 so the game enrols 261
itself), `patch 0x1402862F7 05 06` (mode-8 move-to-front scan guard), `patch 0x140285EE7 06 07`
(delete-from-list scan guard, lea r10d,[r11+6]). tools/probes/lw346_order_table_poke.py also
prepends 261 to the acquired list post-load (guarded, restorable) in case the maintainer only
runs on acquisition events. The durable form stays the same: hook 0x140285DF0 to append
whatever it dropped, which makes the ignored return value harmless.

### 2026-08-27: the owner's done-checklist, and what the data side already allows (offline read)

Plain language: the owner listed everything that has to be true before a new weapon counts as
done (banked in docs/TODO.md under LW-346). Three of the "how does it enter the world" paths
were checked against the modloader's source (fftivc.utility.modloader 1.7.2) without the game:
- Move-Find Item: `MapTrapFormationData` (128 maps, 4 slots, `RareItemId`/`CommonItemId` are
  u16) is moddable by sparse XML and already parsed by tools/lib/treasure.py, so a tile can
  name id 261 as data; whether the pickup code accepts it is a live question (the count-array
  adder at 0x1402847F8 takes ids up to 0x203, so the write half looks free).
- Poach: the poach rewards live in `poachitem.en.nxd` (per-cell moddable, ADDED rows load) keyed
  from the Job sheet (tools/extract_poach_map.py), so a new row can name 261 as data.
- Shop: `ItemShopsData` is a per-ITEM table of `ShopFlags` with exactly 256 entries (ids
  0..255), so ids 256+ have no shop row at all in the modloader's model; shop stock for 261
  needs the in-process XML path (an extended shop-flags array plus the shop list builder B's
  sibling getter cap at 0x140287570, already widened by the marker) and a live read of how the
  shop list consults the flags. The "+" items 256-260 are not sold anywhere, which fits.
Everything else on the checklist is live work on the next boot.

### 2026-08-26 22:15-22:50: the battle gate on 1.5.2, found with three CE captures, and beaten live

Plain language: equipping the Moonblade and starting a battle left Ramza empty-handed, and
afterwards the weapon was gone from the bag too. The owner ran Cheat Engine's "find what
writes" three times while I read the code each capture pointed at, and the chain came out:
the game copies a unit from the roster into a staging block, then block-copies that into the
battle object; the roster-to-staging step only keeps a hand's item if its id is below 261,
otherwise it silently substitutes the empty alternate word. One byte widened, the Moonblade
survives into battle (owner eyes, 22:50).

The chain (read live; addresses on 1.5.2):
- Roster slot 0 (0x1411A7D10, rHand u16 at +0x14 = 0x1411A7D24, lHand +0x16, head/body/acc
  at +0x0E/+0x10/+0x12). CE writers of rHand: 0x140285834 (`mov [rbx+14],0x00FF00FF`) and
  0x140285857 (`mov [rbx+14],r10w`) inside 0x140285744, the UNIT-OBJECT -> ROSTER sync (it
  clears both hands, then re-writes each from the unit object's +0x54/+0x56, routing shields
  (catalog byte +5 == 0x13) to lHand). Not a validator: it wrote 261 back (watcher
  tools/probes/lw346_equip_watch.py, 22:29:31.984 -> .986).
- Battle objects live in a static pool (0x1437415A0.., 0x280 bytes each; pointer table
  0x141800F50, count word 0x1437414A8). Ramza's this boot: 0x143742220, later 0x1437424A0.
  CE writers of object+0x54 (weapon): only the CRT memset (0x1405CA419, 0x280 bytes) and the
  CRT memcpy (0x1405C9F9E, 0x560 bytes from the staging block 0x143C47390 in .data).
- CE writers of the staging block's helm word (0x143C47448): the memset and 0x14036032E
  (`mov [rbx+58],ax`) inside the roster-to-staging converter. Its weapon path at 0x1403602EF:
  `movzx eax,[rdi+20]; mov ecx,0x105; cmp ax,0xFF; je alt; cmp cx,ax; ja keep; alt: movzx
  eax,[rdi+22]` then `mov [rbx+54],ax`; the same shape for the off-hand (+0x24/+0x26 ->
  +0x56); then a 5-slot loop calling the validity thunk 0x1402B8EBC (hooked, 261 -> 37) and
  zeroing rejects. THE GATE: the imm byte 0x1403602F4 (05). Widened live 05 -> 06 (WPM from
  outside, verified) and added to the marker; after it Ramza's battle object read +0x54 =
  0x0105 and the owner saw the Moonblade survive into battle.
- Why it "vanished from the bag" before the fix: equipping moves the item out of the bag
  array (0x1411A7C00[261] 1 -> 0), the converter dropped it from the battle object, and the
  unit-object -> roster sync then wrote the empty hand back over the roster; the return-to-bag
  path 0x14030CEC0 (`mov eax,0x105` imm 0x14030CED3) and the adjust-count path 0x140101058
  (`mov eax,0x104` imm 0x140101071) both skip ids past 260, so nothing put it back. Both caps
  are on the marker now (untested live). The bag count was restored from outside twice
  tonight (1, then 99 on request).
- Also learned: the battle-start party snapshot/restore (0x140279E88 / 0x140279C8B) copies
  the unit block (0x7E90 bytes) and the bag array as 261 bytes (ids 0..260): count[261] is
  neither saved nor restored by it; a save/load round trip therefore loses the Moonblade's
  count unless the rig re-seeds it after load (the boot-arm seeds it only at launch).
- Remaining battle gap, unchanged from June: with 261 in hand Ramza attacks bare-fisted on the
  field (the swing model/animation bakes from the id at construction; no model for 261).
  Damage source to be confirmed by the owner's next hit (weapon-stat thunk clone 67 => WP 15).

Damage control, 22:55 (owner, same Knight at 100%, no Attack Boost): bare fist 323, Moonblade
equipped 323. So on 1.5.2 the field damage IGNORES the Moonblade: the attack resolves as an
unarmed strike even though the battle object carries id 261 (the earlier 426 was Attack Boost).
June's "323 = PA 22 x WP 15" reading was most likely this same fist number. NEXT GATE on the
battle path: the damage/attack-type code's weapon-stat fetch for the attacker's hand (the
battle object's +0x54 through a lookup that is not the hooked thunk, or a capped lookup like
the converter's). CE "find what accesses" on the battle object's weapon word during an attack
is the direct route; the answer decides whether the swing model and the damage share one gate.

### 2026-08-26 23:05: the OrderRebuildHook live pass PASSES on all three list paths

Plain language: the durable fix for the menu lists (one hook that puts back whatever the
display-order rebuild threw out) went into the research rig tonight, built through four
implement/verify rounds, deployed, and passed its live pass with the owner: with both order
tables reset to vanilla from outside, the Moonblade was the last Weapons row, the Acquired
sort no longer crashes, and the unit picker lists it without a sort. Log lines:
`re-appended 1 id(s) [261] (list 0x141811470, table 0x1407B2550, count 111 -> 112)`,
`re-appended 1 id(s) [261] (list 0x141811470, table 0x141874540, count 24 -> 25)` (the
picker), and on the Acquired sort `re-appended 112 id(s) [...] (table 0x141874726, count 0
-> 112)`: the acquired-order table matched NOTHING, so that sort now keeps the previous order
instead of crashing; the acquired list's real format still needs a read (its first words are
not what the mode-8 scan expects). Owner also confirmed: the shield-in-off-hand removal
(LW-347) persists with the hook, as expected (different gate).

Acquired-list postscript (23:10): the list at 0x141874726 now begins `0105 0104 0103 0102 0101
0023 ...` (261 first, enrolled by the widened maintainer cap 0x140286187), and the mode-8
rebuild's table scan stops at the first word >= 0x105, so with the scan guard 0x140285E2D at
its vanilla 05 (which --restore had just put back) the scan saw zero entries and the hook
re-appended the whole list in its previous order. With the guard at 06 (the marker's boot
value) the Acquired sort should be a real acquisition-order sort; guard re-poked to 06 live.
The mode-8 count bug is now harmless either way because the hook keeps the list intact.

### 2026-08-26 23:10: LW-347 (shield evicts the Moonblade) traced to the item TYPE probe

Plain language: putting a shield in the off-hand threw the Moonblade out because the game
asked "is the thing in the other hand a weapon?" and, for id 261, the answer came back "no"
(the game's range tables file 261 under a non-weapon type), so it treated the pair like two
shields and cleared the weapon. The rig's catalog record byte +7 (05 vs Chaos Blade's 00) is
NOT the cause: byte +7 is the equip-bonus row id (vanilla Chaos Blade = 5, Regen; the main
table's 37 reads 0 only because the rebalance XML rewrote it after the rig cloned the vanilla
bytes at boot). Chain (CE writers of Ramza's unit object +0x54 during the shield equip:
0x14028864C `mov [r11+r10*2+54],r13w`): equip-apply 0x140288590(unit, slot, item) calls the
compatibility check 0x140288788; when that returns anything but 1 for a hand slot, the OTHER
hand is emptied (count returned to the bag via 0x1402847F8) before the new item is written.
0x140288788: can-equip 0x1402886D0, then `0x1402B8EE8(item, 0)` (weapon type?) and
`0x1402B8EE8(otherHand & 0x3ff, 0)`, with the weapon-stat thunk 0x1402B8C74 supplying byte +1
bits (0x08 two-handed, 0x01 dual-wield) for whichever is a weapon; for a shield in hand and a
NON-weapon in the other hand it returns 0 (conflict). 0x1402B8EE8 is a 5-byte jmp thunk
(-> 0x14FEED4B0), so the existing thunk-path clone hook covers it: marker line
`cathook 0x1402B8EE8 37` (the near stub remaps ecx only; rdx = dataType survives). Added for
the next boot; live effect pending the owner's relaunch. Every other consumer of the probe
(comparator sort, builder C, the picker's is-weapon tests, the category getter's five probes)
now sees 261 as a knight sword too, which is the intended identity.

23:20, owner eyes after the relaunch: with `cathook 0x1402B8EE8 37` armed (thunk path, stub at
0x114AD0000) a shield in the off-hand no longer evicts the Moonblade. LW-347 is explained and
beaten in the rig; battle re-check with the shield on is next.

### 2026-08-26 23:15-23:35: field DAMAGE is now weapon-driven; Regen (equip bonus) still not applied

Plain language: with the Moonblade in hand the attack was doing bare-fist damage. One CE
"find what accesses" on Ramza's combat weapon word during a hit listed nine readers; four of
them are copies of the same "keep the hand only if the id is below 261" check (two in plain
code, two in the copy-protected region) and one is an inline copy of the validity range
check. All widened live (plain ones on the marker; the copy-protected pair via
tools/probes/lw346_battle_caps_poke.py, post-load, and those bytes HELD in memory). A fresh
battle from the world map then hit 396 at 125 percent compatibility = 317 base against the
249 fist, i.e. PA x WP with the Warbrand-clone WP 15. The punch animation remains (the
model gap). The owner could not bisect which copy mattered; all six stay.
Sites: 0x140226FDD `lea ecx,[rdx+6]` (byte 0x140226FDF 06->07); 0x14028024D `mov r8d,0x105`
(byte 0x14028024F 05->06); 0x1401ED93C.. inline `(id-1)<=0xFC || (id-0x100)<=4` on both hands
(bytes 0x1401ED95B and 0x1401ED982 04->05); copy-protected 0x14F2EA40D `lea ecx,[rdx+6]`
(byte 0x14F2EA40F 06->07, right before two weapon-stat thunk calls) and 0x14F45D312
`xor r15d,0x5E` (byte 0x14F45D315 5E->5F; r15 = 0x58^0x5E = 6 feeds `lea r14d,[rbp+r15]` with
ebp = 0x2F + 0xD0 = 0xFF, the 0x105 cap on the damage-staging globals). A battle RESTART
reuses the snapshot built before the pokes; only a fresh entry re-runs construction.
Still open: the Moonblade's Permanent Regen (equip-bonus row 5 in record byte +7) shows on
the status page but never ticks; the status applier reaches the record through yet another
path. Record 261 lives at 0x114AA0C3C this boot (ext buffer 0x114AA0000), byte +7 at
0x114AA0C43.

Regen, 23:35 (owner screenshots): the in-battle "Equipment Effects" page lists Permanent Regen
for the Moonblade, the "Current Status Effects" window shows Protect and Shell (armor) but no
Regen, and HP does not tick. CE accesses on the record's bonus byte 0x114AA0C43 across a
battle start: only the equipment-effects accumulator 0x1400FA7AC (30x; catalog(item) ->
record byte +7 -> EquipBonus row (stride 0x1A at 0x14080FEA0) OR-ed into dest+0x18..) and the
card reader 0x14029F726. Plain-code callers of the accumulator: 0x140295C37, 0x1402A0EDA,
0x1402A0F40 (page/card paths). So the field applier either resolves the hand through yet
another capped copy BEFORE calling the accumulator, or lives in the copy-protected region.
NEXT (one capture): CE "find what accesses" on the roster rHand 0x1411A7D24 armed on the
formation screen through the first battle turn, then read the construction-time readers for
the cap idiom.

### 2026-08-27 00:00-00:50: the punch is a "not a weapon" verdict, and the art is a two-byte pair the old probes read one item off (static read of the live process, owner away, nothing poked)

Plain language: with the owner away this was a read-only pass over the running game. The
question was why Ramza still punches with the Moonblade. The answer has two halves. First,
when an attack starts the game asks a small helper "which family of item is id X?", and for
261 the helper says "none" (its list of family boundaries ends at 261), so the attack code
publishes "no weapon in hand" and draws nothing. Second, the picture drawn in the hand comes
from a two-byte record per item (which drawing in the weapon art sheet, which color set),
reached through another helper that also answers "none" for 261. Both helpers are the
five-byte jump thunks the research rig already knows how to clone, so the fix is two (plus a
few sibling) marker lines, staged for the next boot and NOT yet tested live. A bonus: the
two-byte table is at 0x140785CF0, and June's probes used 0x140785CF2 as the base, one item
off, which is why writing it "changed nothing" and why the CE watch on it saw zero hits.
The save side was read too: the save file stores exactly 261 bag counts, packed against
the next array, so the new weapon's count has to live in a mod-owned sidecar.

Method: tools/probes/lw346_live_disasm.py and lw346_xref_scan.py over RPM of the 23:10
boot (pid 43392, marker v1 armed, the two copy-protected damage caps still poked), plus
three small read-only helpers written tonight. Every address below was read this way; none
was observed changing behavior yet.

The retired lead. June's chain "0x1401ED93C reads both hands, 0x1401EDC50 resolves the
model" is half right: 0x1401ED910(unit) reads combat +0x20 / +0x24, range-checks the id
(the marker widened it to 261), and calls 0x1401EDC50(0, id), but that callee is a two-hop
nex read: table #45 = *(0x143CDA2E8) is item.en (262 rows on this install because the
shipped item.en.nxd carries the Moonblade row; id range [0, 261]), field +0x24 is
UiItemCategoryId; table #38 = *(0x143CDA6F0) is UIItemCategory (35 rows), field +0x18 is an
is-weapon flag. tools/probes/lw346_swing_model_tables.py replays it: ids 37, 67, 257 and 261
all resolve (261 -> category 3 -> 1), id 260 -> category 28 -> 0. So this is a "does the hand
hold a weapon" test that 261 already PASSES; it is not the model, and the FF16Tools ffto
layouts (Item.layout, UIItemCategory.layout) name both fields. No nex table has a weapon
graphic column; the art is engine data (below).

Pub2 on 1.5.2 is 0x14F45D298 and the render global did not move. It stores combat +0x20 to
0x1407B0764 and +0x24 to 0x1407B0766, sets word 0x1407B0762 = 1, and clamps with the
0x105 cap the marker widened at 0x14F45D315 (r14 = 0xFF + (0x58 ^ 0x5F) = 0x106): ids at or
above the cap get the off-hand word substituted and 0xFF written to the off-hand global. Its
tail branches on the unit type byte (+0x1A1 through the class table at 0x140680010): type 2
(monsters) publishes +0x1A8 instead. Readers of 0x1407B0764: plain 0x140282057 (a
draw-request filler that copies the DERIVED id, see below, into its struct at +0x20),
0x140307DFC (a publish wrapper), 0x1403099B0 (the attack-animation setup); copy-protected
0x1505F4700 (a status bit table at cluster +0x51, not the id) and 0x1506E80C0 (a second
publisher of the derived global for the non-player entry path; its obfuscated constants do
not decode statically, the same shape as the plain one).

THE PUNCH. 0x1403099B0 picks the weapon id (r9w) from the cluster (0x1407B0764, or
0x1407B0766 when the flag byte says off-hand, or the thrown item for action type 0x13), then:
`ecx = id; call 0x1402B8BCC` (range-index accessor, below); index -1 falls to a "not a
weapon" path; otherwise `[0x14067FB78 + idx*4]` (range -> data type) must be 0 (weapons). The
verdict is folded into a mask and the routine stores `word 0x1407B077A = id & mask` at
0x140309B75: the DERIVED weapon id, 0 for "no weapon". Everything downstream (the
weapon-stat thunk call right after it, whose 8-byte row is copied to the damage staging at
0x1407B07A8; the draw-request filler; 14 plain and 6 copy-protected readers) consumes
0x1407B077A. For id 261 the range index is -1, so 0x1407B077A = 0: the unit attacks
unarmed on screen. Damage still came out weapon-driven tonight because that path was fed by
a different copy of the hand (the 23:15 sites), which is consistent with the two-global
split.

The accessor family (all five-byte E9 thunks at 0x1402B8Bxx-0x1402B8Fxx into the
copy-protected region; ecx = item id; the rig's cathook line clones any of them):
- 0x1402B8BCC -> 0x14FE66B2F range INDEX: passes ids <= 0xFD or 0x100..0x104, then walks the
  13-word fence table 0x1406804E2..0x1406804FC ([0,122,128,144,172,208,240,256,258,258,258,
  259,260,261]) and returns the index of the first fence above the id; -1 otherwise. So 261
  and up have NO range. Callers: the attack setup, the seven bodies below, the type probe.
- 0x1402B8C0C -> 0x14FE6FF5D range BASE (fence word for the id's data type).
- 0x1402B8C74 (weapon stats, cloned to 67), 0x1402B8EBC (validity, cloned to 37),
  0x1402B8EE8 (type probe, cloned to 37): the three the rig already redirects.
- 0x1402B8CD4 -> 0x14FE97BF0, 0x1402B8D3C -> 0x14FEACE84, 0x1402B8DA0 -> 0x14FEB8430: two-byte
  rows keyed by (id - range base), two table bases each selected by obfuscated data-type
  constants; meaning unread. 0x1402B8E04 -> 0x14FEBE43B: three-byte rows for data types 4
  and 9 (tables 0x14067277C / 0x14080FB70).
- 0x1402B8E60 -> 0x14FEC80C0 the SPRITE/PALETTE PAIR: for data type 0 (weapons) returns
  0x140785CF0 + id*2 (raw id, not id minus base), for two other types 0x140672B54 + id*2,
  NULL when the range index is -1. Byte 0 = palette selector, byte 1 = graphic index:
  id 37 Chaos Blade 50 0C (0x0C = the FFHacktics knight-sword slot), id 33 Defender 80 0C,
  id 35 Excalibur 80 0E, id 27 Materia Blade D0 06, id 67 Warbrand F0 03 (its old axe art),
  id 1 and 19 E0 00, ids 256-260 00 00. Exactly two callers: the plain sprite composer
  0x14026BC60 (`ecx = word [unit+0x150]; call; graphic = [pair+1] or 0`) and the
  copy-protected CLUT loader at 0x14E92B95C (`palette = [pair+0]`, high nibble times an
  obfuscated stride into a table at 0x140D35750, then the low nibble). June's
  weapon_sprite_probe/writetest and the 2026-08-19 LW-289 lever list all used 0x140785CF2
  as the base, i.e. every write and every watch landed on id+1; the "vestigial table"
  verdict rests on that. This is a LEAD for the palette-assignment wall, not a result.
- 0x1402B8F18 -> 0x14FEEE3C0 is an ability-id remap (66 callers), not an item accessor.

Staged, not run (owner decides):
- M0, no relaunch, any vanilla weapon: `python tools/probes/lw346_sprite_pair_poke.py --apply
  <id>` writes the Chaos Blade pair (50 0C) over that weapon's record (undo file written,
  `--restore <id>` puts it back), then one attack. Knight-sword art on the swing = the pair
  table drives the HD art and the LW-289 wall reopens; unchanged art on the very next swing
  but changed after a fresh battle = read at construction only; unchanged after both = the
  table really is vestigial and the base fix was not the reason.
- M1, one relaunch: tools/probes/lw346_capbreak_bootarm.marker.v2.txt adds seven cathook
  lines (0x1402B8BCC, 0x1402B8E60, 0x1402B8C0C, 0x1402B8CD4, 0x1402B8D3C, 0x1402B8DA0,
  0x1402B8E04, all -> 37) to the banked v1. Expected: 0x1407B077A = 261 during the attack,
  a Chaos Blade in the hand, damage unchanged at 317 base (the stat thunk keeps its 67
  clone). The sibling clones exist so no body ever indexes "261 minus base" into a
  128-row table; the cost is that the Moonblade answers as a Chaos Blade to those
  unread tables, the same identity its catalog record already borrows.

The save gate, read from the serializer. The game save struct holds the bag as exactly
0x105 bytes at +0x83A8 (copy loop 0x14021926C.., 40 movups plus a 5-byte tail) immediately
followed at +0x84AD by a second 261-byte per-item array copied from 0x1411A7700; the restore
copies at 0x14021B1D5 and 0x14021E1D1 (which zeroes count[0] first) mirror it. There is no
slack to widen: count[261] is not in the save and cannot be without moving the next field.
Design consequence for LW-346 item 6: the mod owns an extended-bag sidecar keyed to the save
and re-seeds count[261+] after every load (and the roster hand ids need nothing, they are
u16 in the unit block the same copies carry). Other bag readers seen and not yet read:
a count getter with a 0x105 cap at 0x14015415B (surface unknown), a bag WRITER at
0x1402060F4 that fills counts from a table (new-game or reward seeding), and a loop at
0x14031FD40 bounded by 0x17F over the catalog accessor. tools/probes/lw346_saveload_check.py
prints hand, count, both order tables and the acquired list in one go for the round-trip
test; tonight's live state: rHand 261, count 99, 261 in both tables and first in Acquired.

### 2026-08-27 00:50-01:00: the sprite/palette pair is LIVE, both bytes, per swing (owner eyes)

Plain language: the owner ran the M0 test in a battle with Save the Queen. First swing after
the record was rewritten to Warbrand's drawing with the Chaos Blade's palette (50 03): a purple
Warbrand. Second swing after rewriting it to Warbrand's own record (F0 03): the same shape in
Warbrand's normal steel. Restored to F0 0C afterwards. So the two-byte record picks both the
drawing and the palette, and the game reads it on every swing, no reload needed. June's and
LW-289's "vestigial table" verdict is an artifact of the one-item-off base; ledger row
[weapon-sprite-pair-drives-swing-art] added (Uncertain, owner flip pending), the walled row
carries an overturn pointer, LW-349 records the result and the shape of the color build.

For the Moonblade this settles the art half: the accessor 0x1402B8E60 returning NULL for 261
is why nothing is drawn, and cloning it to 37 (marker v2) should put a knight sword in the
hand; the "not a weapon" verdict (0x1402B8BCC) is the other half. M1 (one relaunch with
tools/probes/lw346_capbreak_bootarm.marker.v2.txt) is the next experiment. Screenshots:
tools/probes/lw349_sprite_pair_graphic_swap_34.png, lw349_sprite_pair_palette_swap_34.png.

### 2026-08-27 01:00-01:40: M1 relaunch, the Moonblade swings a blade (owner eyes), with two loose ends

Plain language: the owner relaunched with marker v2 (the seven extra clone lines) and, after
the two post-load damage pokes, attacked with the Moonblade. The first several swings were
still bare-fisted; then, with nothing changed in between, a blade appeared and stayed on
every swing after that (screenshots tools/probes/lw346_moonblade_swing_blade_1.png, _2.png).
Damage on a chocobo at 125 percent: 272 = PA 21 x WP 15 x Brave 69 percent, so the weapon
now also uses the knight-sword Brave formula (the earlier 396 was PA x WP without it), the
identity a Chaos Blade clone should have.

Boot log: `BOOT-ARMED: equip-minimal rig + 19 registry patches + 9 clone hook(s) + 1 order
hook(s)`; every cathook installed on the thunk path (stub for 0x1402B8BCC at 0x114AE0000:
`mov eax,ecx; and eax,0x3ff; cmp eax,0x105; jb; cmp eax,0x1ff; ja; mov ecx,0x25; jmp [rip]`,
so only ecx is remapped, r9 survives).

The fist swings were chased before the blade appeared, and the chase produced facts worth
keeping even though the trigger is still unknown:
- tools/probes/lw346_render_cluster_watch.py (0.5 ms poll of the cluster 0x1407B0760.. and a
  unit's action block) showed, for every Moonblade swing fist or blade alike: pub2 publishes
  main 261 / off 255 with the flag word 0x0001, then ~180 ms later the derived word
  0x1407B077A becomes 242 (0xF2), the flag's high byte 0x1407B0763 goes 1, and the 8-byte
  weapon-stat staging at 0x1407B07A8.. fills; at action end the derived word returns to 0.
  So 242 is the game's own value for this action, not the fist's cause.
- The 242 rides in the action block: combat struct 0x141855CE0 (June's CWeapon base; hands at
  +0x20/+0x24), block at +0x1A0 = `10 06 b9 01 00 00 00 00 f2 00 05 10 05 00 ...` during the
  swing (+1 type 6, +2 word 441, +8 = 242), and `10 01 00 ...` at rest (the wrapper
  0x140307DFC re-publishes a neutral type-1 block). CE "find what writes" on +0x1A8 (owner):
  CRT memcpy 0x1405C9EB1 from a stack local (RDX 0x12ECDFBA0, 0x14 bytes); stack return
  0x145DD86DD = inside the copy-protected memcpy wrapper behind the thunk 0x14002630C, next
  return 0x140281970 = the class-8 branch of the plain action-commit routine 0x140281488
  (copies its rcx input to a local, dispatches on the class table 0x140680010[type], and
  memcpys the local to rdx = combat+0x1A0; class 8 copies it unchanged). Its plain callers:
  0x14020AC3E, 0x14030D389 (the player attack builder 0x14030D2D4: memcpy of the 0x14-byte
  static template 0x14186AFAC, then +0 = [unit+0x1BC], +0xA = 5, +0xC/+0xE/+0x10 = target
  coordinates, and the hands copied from statics 0x14186AFA2/0x14186AFA4 to unit +0x20/+0x24,
  June's "pub3"); copy-protected 0x14DB3278D, 0x14F3B75A0. The template 0x14186AFAC is
  filled by copy-protected 0x1508B2DC0 (memcpy from its caller's action, then publishes the
  unit's hands to 0x14186AFA2/A4 and sets byte 0x14186AFA1 = 1); the plain writers
  0x14030D941 / 0x14030DA90 are whole-state save/restore block copies (template +8 mirrors
  state struct +0xD4). Whatever computes the 242 sits above that in protected code; not
  chased further because the blade appeared.
- The derived publisher 0x1506E80C0 (copy-protected) takes the action block as rsi and for
  this class publishes word [rsi+8] (the 242) to 0x1407B077A; 0x1403099B0 is the plain
  publisher for the wrapper's neutral re-publish. Both write the same global.
- June's CWeapon address 0x141855D00 (= 0x141855CE0 + 0x20) is unchanged on 1.5.2; unit 1's
  struct (0x1418564E0, stride 0x800) mirrors Ramza's hands.

Working guess for the fist-then-blade: the knight-sword sprite sheet page or its CLUT for
graphic 0x0C was not resident until something loaded it (another unit or the status page),
and the composer drew nothing until then. Untested; noted on the ledger row
[capbreak-swing-art-via-accessor-clones] (Uncertain). What is settled for the LW port: the
model fix is `cathook 0x1402B8BCC` + `cathook 0x1402B8E60` (donor id per item), with the
sibling accessors cloned so no body indexes past its tables. LW-346 item 4's model clause is
covered by the rig; Regen stays parked.

### 2026-08-27 01:50-02:00: the save round trip (owner eyes): hand, lists and Acquired survive; the bag count does not

Plain language: with the Moonblade equipped and two spare copies in the bag, the owner saved,
quit to the title screen and loaded: the weapon was still in Ramza's hand, both menu order
tables and the Acquired list still knew it, and the bag still said 2. That last one was RAM
remembering, not the save: after quitting the game entirely and loading the same save, the
bag said 1, which is the research rig's boot seed, so the save file never held the count and
the load neither restores nor wipes it. Exactly what the serializer read predicted (the save
copies 261 counts, no more). The hand id and the lists ride in the save natively. LW-346 item
6 is therefore one sidecar away (LW-348); ledger row [capbreak-save-roundtrip-1-5-2].

### 2026-08-27 02:05-02:15: shop sell and Optimize (owner eyes)

Plain language: in a town shop the Moonblade appeared in the Sell list of weapons at 5 gil
(half the price byte in its catalog record, which is the Chaos Blade clone's 10), selling one
worked and the bag count went from 1 to 0 (the adjust-count cap the marker widened at
0x140101071 is doing its job). Optimize on Ramza's equipment screen replaced the Moonblade
with the Chaos Blade, which is what a WP ranking would do (the Moonblade's stats are the
Warbrand clone's WP 15 against the Chaos Blade's 28), so whether Optimize even considers id
261 is not settled by this; it needs a bag where the Moonblade is the strongest knight sword.
Buy lists are a known gap (no shop-flags row past id 255) and were not opened. LW-346 item 3
progress: sell = yes; shop stock, poach, Move-Find, spoils, steal, Rend Weapon, crystal, Throw,
in-battle Equip Change still unchecked.

### 2026-08-27 02:20-02:30: no Chronicle item list; uninstall is a clean loss (owner eyes)

Plain language: the Chronicle has no screen that lists individual equipment, so the "Chronicle
item collection" clause of LW-346 item 9 has nothing to check. Then the uninstall test: with
the boot marker renamed away, a cold relaunch loaded the save that had the Moonblade in hand
and one in the bag: no crash, the hand came up empty, the item was gone from the Inventory,
the Acquired list had been rebuilt without it. One hazard to document: the two display-order
tables are saved with the game and still held 261 as their last entry; the vanilla rebuild
stops at the first id it does not know, so a table where a sort had pushed 261 into the
middle would hide the later entries from that list's default order until one sort. Ledger
row [capbreak-uninstall-is-clean-loss]. The marker was renamed back afterwards (v2 stays the
next-launch state).

### 2026-08-27 02:45-03:15: an enemy cannot carry the new weapon through the encounter table (owner eyes, one read short)

Plain language: the encounter table (ENTD) was edited so that the Knight in slot 3 of Monastery
Vaults, Fourth Level (battle 435) holds the Moonblade. Two launches were spent learning that the
arena mod which ships that table is not enabled in Reloaded at all (its listing in the app
config is stale; the loader log names 12 mods and it is not one), so the edited table was moved
into the LivingWeapons mod folder for the test launch (merged: the log shows `(435,3,0)
RightHand changed`) and removed again afterwards. In the battle the Knight spawned with helmet
and shield but an EMPTY right hand; no unit in the pool held 261. The likely cause is format,
not a cap: the classic ENTD record stores item ids as single bytes (the FF16Tools layout notes
the override ints are "cast to byte" when patched), so 261 becomes 5 (a dagger a Knight cannot
hold) or nothing. The owner's status-screen read (empty versus Argent Dirk) was not taken
before clocking out. Design consequence for LW-346 items 3 and 9: enemies carrying a new id,
and therefore steal / Rend / spoils of it, need a RUNTIME loadout write after enemy
construction (the mod already knows the construction edge from the Body Double arc), not a
table row; the same limit applies to the DLC "+" ids 256-260, which fits their absence from
any encounter. Also learned: Reloaded's loaded-mod set is the log's "Loading N Mod(s)" list,
not the EnabledMods array in AppConfig.json; check the log before trusting a mod's presence.

### 2026-08-27 03:20: the port blueprint, written for the next session's /build (nothing here is new evidence)

Plain language: everything the research rig does for the new weapon, listed as the pieces the
LivingWeapons mod itself would have to carry, so the next session can plan the port (LW-346
items 7 and 8) without re-reading tonight's journal. Each piece names its live proof.

1. Boot-time byte patches (plain code, safe before the game runs; all on marker v2, every one
   owner-observed 2026-08-26/27): 0x140397121, 0x140287570, 0x140285E2D, 0x140286187,
   0x1402862F7, 0x140285EE7, 0x14030CED3, 0x140101071, 0x1403602F4, 0x140226FDF, 0x14028024F,
   0x1401ED95B, 0x1401ED982, plus the rig's own six (count-getter cap, four display caps,
   count[261] seed) and the equip clamp 0x140284C82 (06 -> 07). Port shape: one table of
   (address, old, new) with old-byte verification and full rollback on any mismatch, behind
   LaunchGuard (the PE key already gates the mod), and a PATCH_REANCHOR entry per site.
2. Post-load byte patches in the copy-protected region (pages are not decrypted at boot):
   0x14F2EA40F 06 -> 07 and 0x14F45D315 5E -> 5F (damage path). Port shape: the same table
   applied by the tick loop once the page reads its expected old byte (poll until readable and
   vanilla, then write, then verify), never at boot.
3. Thunk clones (five-byte E9 thunks redirected to a near stub that remaps ecx for ids
   261..511 and jumps to the original; the rig's ThunkRedirectInstaller): weapon stats
   0x1402B8C74 -> per-item donor (67 tonight), validity 0x1402B8EBC -> 37, type probe
   0x1402B8EE8 -> 37, range index 0x1402B8BCC -> 37, sprite/palette pair 0x1402B8E60 -> 37,
   range base 0x1402B8C0C, siblings 0x1402B8CD4 / 0x1402B8D3C / 0x1402B8DA0 / 0x1402B8E04 ->
   37. Port shape: a native stub per thunk allocated within +/-2 GB (VirtualAlloc near the
   image), the stub maps id -> donor through a small table so each new id can name its own
   stats donor and art donor; the plain category getter 0x1402890C0 needs the function-entry
   (Reloaded.Hooks) form the rig uses.
4. Extended catalog: the accessor 0x1402B8C44's extended branch (ids >= 256) already indexes
   a relocated 8 KB buffer (disp32 patched at the June site); the LW mod owns that buffer and
   fills one 12-byte record per new id (donor record copied, then byte 5 type, byte 7 equip
   bonus row, bytes 8-9 price edited from data). Live proof: the Moonblade's card, shield
   compatibility, sell price 5 (record price 10).
5. Order-rebuild hook on 0x140285DF0 (re-append what the display-order rebuild dropped;
   HandsFree OrderRebuildHook b1abd77, four partials plus tests). Port as-is.
6. Sidecar for bag counts of ids 261+ (LW-348): written on change, replayed after every load;
   the save never carries them (proven tonight, cold boot).
7. Data layer (item 7): one Nenkai-mimic XML per table for ids 261+ generated from
   data/items.json (name, description, icon pair, stats donor, art donor, price, type,
   equip-bonus row); item.en.nxd already accepts added rows (the Moonblade row ships today);
   icons are the per-id .tex plus .utexpt pair proven 2026-08-26.
8. Known limits to state in the docs: enemies cannot carry a new id through the encounter
   table (one-byte ids), shops cannot stock it (no shop-flags row past 255), Optimize ranks
   by WP so a weaker new weapon will be swapped out, Regen (equip-bonus row) is not applied
   on the field yet, and an uninstall loses the item cleanly but a saved order table may need
   one sort.
