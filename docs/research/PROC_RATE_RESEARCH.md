# Weapon spell-cast proc rate (the "19%") — cracked the mechanic, not yet the address

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

**Question:** can we change the 19% chance that a "May Cast: X" weapon (formula 2) fires its
spell on hit, and set it to whatever we want?

**Answer so far:** YES in principle — it's a *single byte* in the engine — but that byte lives in
native machine code, not in any data table, so flipping it needs a one-time reverse-engineering
pass to find its address. After that the FFTHandsFree bridge can patch it live (and we could bake a
permanent patch). No disassembler is installed on this box yet, so the address-find is a
next-session job. What IS nailed down is the exact lever.

## The mechanic (confirmed)

The 19% is **canonical PSX FFT**, inherited verbatim by the Ivalice Chronicles port:

> Weapons that can cast a spell (Icebrand, Holy Lance, …) have a **19% chance** to cast it on hit.
> The roll is skipped if the strike misses or is blocked.

And the "19%" is not literally 19 — it's a roll **out of 256**, the way most FFT chances work:

```
50 / 256 = 0.1953…  →  floor(19.53%) = "19%"
```

So the engine does, per qualifying hit:

```
roll = Rand() & 0xFF        ; 0..255
if (roll < 0x32)            ; 0x32 = 50
    cast the weapon's OptionsAbilityId spell on the struck tile
```

**The lever is the one immediate byte `0x32` (decimal 50)** in that compare. Change it, change the
rate. This is the same family as the documented "Separate Status caps at 25%" — note `25% = 64/256
= 0x40`, a *different* constant from the cast's `0x32`. So status-cap and cast-rate are two separate
bytes; we want the cast one (`0x32`).

### Rate → byte conversion (`threshold = round(rate% × 256 / 100)`)

| Want | Byte | Actual |
|------|------|--------|
| ~100% (always) | `0xFF` (255) | 99.6% |
| 75% | `0xC0` (192) | 75.0% |
| 50% | `0x80` (128) | 50.0% |
| 30% | `0x4D` (77) | 30.1% |
| 25% | `0x40` (64) | 25.0% |
| **19% (current)** | **`0x32` (50)** | 19.5% |

(True 100% would need editing the compare itself — the roll maxes at 255, so `0xFF` is the
practical ceiling.)

## Why it's not a data edit (quadruple-confirmed)

The pure-DATA overhaul cannot touch this number:

1. Scanned all ~30 modloader `TableData` tables — the only rate-ish field is
   `AbilityData.ChanceToLearn` (JP learning, irrelevant).
2. The FFHacktics **Weapon Secondary Data** struct (`0x808740`, 8 bytes) is
   Range/AttackFlags/Formula/WP/Evade/Element/CastSpellID — **no proc-rate byte**.
3. The mechanic is documented as engine hit-rate logic (the 25%/100% caps), computed in code.
4. The remaining `Unused` weapon byte reads 255 on every weapon — not a hidden knob.

The 19% is a hardcoded constant in the **weapon-strike formula routine**, inside
`FFT_enhanced.exe` (native x64, module base `0x140000000`, ~343 MB). That's why we need RE, not a
table edit.

## The tools we already have

- **Patch gun:** FFTHandsFree bridge `wv <addr> <value>` (write_byte). Once we know the address,
  `wv "0x14XXXXXXX" 128` sets the cast rate to 50%. The exe loads at its preferred base
  `0x140000000` with no ASLR shuffle observed, so the offset should be stable launch-to-launch →
  a permanent patch is feasible (apply on load).
- **Scanner:** the bridge ships `Reloaded.Memory.Sigscan` + a `SearchBytes` AoB verb — fine for
  patterns, but a blind hunt for `0x32` across 343 MB returns far too many hits without the
  routine's surrounding context. Not enough on its own.

## The crack-it recipe (next session — needs a debugger/disassembler)

The community already uses **Cheat Engine** on this exact game (Nexus/FearLess have IVC CE tables),
so CE is the path of least resistance:

1. Attach Cheat Engine (or x64dbg) to `FFT_enhanced.exe`.
2. Equip a known cast-weapon (the **Cleaver — "May Cast: Rush"** — is already equipped, and a
   battle is staged as the live anchor).
3. Locate the equipped weapon's **CastSpellID** byte in RAM (AoB the weapon's secondary record:
   `Formula = 0x02` + the Rush ability id; or scan the unit's equipment block).
4. **"Find out what accesses this address"** on that byte → swing the Cleaver a few times. The
   instruction CE catches is *inside the proc routine*.
5. In the disassembly around it, find the `and …, 0xFF` followed by `cmp …, 0x32` (bytes `3C 32`
   for `cmp al,0x32`, or `83 F8 32` for `cmp eax,0x32`, etc.). That `0x32` is the target.
6. Record the **module offset** (address − `0x140000000`).
7. Patch and test live: `wv "0x140000000+offset" 128` (50%) → swing the Cleaver, confirm Rush fires
   ~half the time. Then settle on a final rate.

Alternative anchor if step 3/4 is fiddly: the FFT RNG is a known routine; breakpoint the RNG call,
swing, and walk the return into the `& 0xFF` / `cmp 0x32` site.

## Status

- ✅ Mechanic, math, and exact lever cracked: `0x32` (50/256) in the weapon-strike routine.
- ✅ Patch + scan infrastructure confirmed ready (bridge `wv` / `SearchBytes`).
- ⛔ **2026-06-02: the address-find is Denuvo-walled.** FFT_enhanced.exe is Denuvo-protected:
  on-disk `.xtext` (279 MB exec section) is ENCRYPTED (entropy 7.95/8, disassembles to garbage);
  live RAM decrypts (entropy ~5.1–5.6) but is MUTATED/obfuscated — constants built via
  `mov;xor`/MBA chains, opaque predicates, register juggling. **No literal `0x32` immediate exists**
  to scan-for or `wv`-patch (the threshold is computed at runtime). So BOTH static capstone-on-disk
  AND a live bridge AoB-for-`cmp 0x32` are dead ends — confirmed by sampling. The bridge can't set a
  hardware breakpoint, so it can't do the only thing that would work: runtime-trace the proc branch.
- 🔭 Remaining paths (all hard, all carry Denuvo anti-tamper risk):
  (a) extend the FFTHandsFree mod (it runs IN-PROCESS) with a "find-what-accesses" handler —
  HW debug-register breakpoint or PAGE_GUARD + vectored-exception handler on a cast-weapon's
  CastSpellID byte — to catch the proc routine's RIP when you swing; then NOP/invert the obfuscated
  conditional BRANCH (not edit a constant). C# change + mod reload + Denuvo anti-debug may fight it.
  (b) Cheat Engine / x64dbg runtime "find what accesses" (user-driven GUI), same idea, mature tools.
  (c) accept 19% and design around it (formula-45 already gives 100% STATUS; cast-weapons work at 19%).
- ⛔ Out of scope for the shippable pure-DATA mod (it's a live/binary hack). Pure-data ceiling
  without it stays: 25% on-hit STATUS, or formula-45 (2D) **100%** on-hit status, or formula-4
  100% elemental magic. Full spell-CASTS stay 19% until this byte is patched.

Sources: FFHacktics wiki (Formulas), AeroStar Battle Mechanics Guide, multiple FFT community
references — all confirm 19% = 50/256 weapon spell-cast proc.
