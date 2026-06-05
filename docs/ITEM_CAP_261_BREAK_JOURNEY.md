# The 261-Item-Cap Break Journey

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
JSON to `…/FFTHandsFree/claude_bridge/command.json` and read `response.json`.
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
