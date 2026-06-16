# Live-memory probes (RE instruments)

External RPM/WPM scripts (run with `python` against the live `FFT_enhanced.exe`) used to
discover and verify the runtime's memory facts. Rescued from `%TEMP%\fft_probes\` on
2026-06-11 — they are instruments, not scratch: memories, journal docs, and the live ledger
cite them as evidence, and OS temp is allowed to delete its contents.

They read/write via Read/WriteProcessMemory only (fail-safe; cannot crash the game). Module
statics are readable externally despite Denuvo; engine *code* pages are not — that wall is
why the DLL is the only instrument for some experiments.

## The load-bearing instruments

| Probe | What it does / proved |
|---|---|
| `ct_probe.py` | watch/dump/hold of the battle structs (`dump`, `watch [s] [hz]`, `hold combat\|static\|cond ...`). Found the scheduler CT (combat base+0x41). |
| `sentinel_probe.py` | battle sentinels (slot0/slot9/battleMode/event) over time — proved the slot0 quit-stick (0xFF after QUIT, 0x66 after victory). |
| `poison_probe.py` | the **watchspan recipe** — diff a unit's full struct across a status application. Generalizes to mapping any status byte (found poison +0x48/0x80 + timer +0x4A). |
| `cripple_probe.py` | reaction-field suppression (combat +0x94 hold-zero) — Maim's proven primitive. |
| `noncharge_probe.py` | live grant of the Non-charge support (combat +0x98 OR-set bit 227) — twin of `cripple_probe`. Feasibility test for the 4th-staff "instant cast" signature: does the engine honor a live support grant at charge-time? **Run pending** (judge by cast behavior, not the menu). |
| `barrage_probe.py` | JobCommand record decode. **CAVEAT: its `msb` flag is whole-u16 order and WRONG** — the live layout is MSB-first *per byte* (see `docs/` + Barrage.Policy.cs). Kept for the record decode, not the bit math. |
| `barrage_undo.json` | restore bytes for the pre-runtime Barrage injection prototype (the shipped DLL now saves/restores itself). |
| `oracle_probe.py` | static-array enemy-identity capture validation (the EnemyOracle's filter). |
| `knockback_probe.py` | gx/gy position writes — proved the renderer desync that PARKED guaranteed-Knockback. |

## Session one-shots (kept for reference)

Everything else is a one-shot from a specific hunt (JobCommand scans, menu-region dumps,
learned-bit pokes, roster dumps, JP refunds, ...). They encode addresses and assumptions
from their day — **read before trusting, never run blind**: several WRITE live memory
(`set_learned*.py`, `support_poke.py`, `hp_poke.py`, `refund_jp.py`, `clear_stray.py`,
`aim_all_barrage.py`, `steal_only_barrage.py`, `hold_secondary.py`) and a few mutate repo
files (`merge_grid.py`, `merge_rods.py`, `add_dmg_col.py`, `fix_mojibake.py`).

New probes: start from `poison_probe.py`'s watchspan loop or `ct_probe.py`'s mode dispatch.
Add anything that produced a ledger fact to the table above.
