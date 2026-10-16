# FFT:IC 1.5.2 no-DRM build: Offset Audit Ledger

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

> Damage report for the Denuvo-free game build that reached this box 2026-10-15 (the publisher
> removed the protection in September 2026). Companions: docs/research/PORT_1.5.2_OFFSETS.md
> (the previous point release) and docs/PATCH_REANCHOR.md (the runbook). What is different about
> this one: nothing moved and the mod still broke, because the LOADER CONTRACT changed, not the
> addresses. The game was never launched to produce anything below.

## In one paragraph

The game lost its copy protection and the mod stopped counting kills. The program code is the
same compile as 1.5.2: every section starts at the same position, every table, flag and function
the mod points at sits at the same internal offset. What changed is that the unprotected program
is allowed to load at a random address each launch (ASLR), and the mod assumed one fixed address.
Its startup guard read an empty spot, treated that as "not ready yet", and waited forever. No
loud line, no message box, no writes. The fix is a base-relative address rule plus the new build
identity for the guard; no offset was re-found because none needed to be.

## Build identity

| build | TimeDateStamp | SizeOfImage | Steam buildid | exe stamped | exe SHA256 |
|---|---|---|---|---|---|
| 1.5.2 | 0x6A5EA53C | 0x18D78000 | 24304240 | 2026-07-20 22:46 UTC | 1FD5320D... |
| 1.5.2 no-DRM | 0x6A5E4A1B | 0x03F9E000 | 24304444 | 2026-07-20 16:17:31 UTC | 937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5 |

Installed on this box 2026-10-15 22:31 (Steam appmanifest LastUpdated 1792117890). exe size
12,158,720 (was 362,226,944). Backup: C:\Users\ptyRa\FFT_IC_backup_1.5.2-nodrm\. The compile
stamp is six and a half hours EARLIER than 1.5.2's: the protector re-stamped the wrapped build,
the unwrapped one carries the original compile time. The buildids are 204 apart.

## Image structure: the wrapper is gone, the contract changed

| field | 1.5.2 | no-DRM |
|---|---|---|
| sections | 12, protector-scrambled names | 7, standard names (.text .rdata .data .pdata _RDATA .rsrc .reloc) |
| ImageBase | 0x140000000 | 0x140000000 (preferred only) |
| DllCharacteristics | 0x8120 (no DYNAMIC_BASE) | 0x8160 (DYNAMIC_BASE + HIGH_ENTROPY_VA) |
| .reloc | absent | present, 0xFCF4 bytes |
| EntryPoint | 0x12C48AC0 (inside the protector blob) | 0x41FFDC (plain CRT entry) |

The first seven section VA spans are IDENTICAL between the builds: 0x1000 / 0x611000 / 0x77C000 /
0x3D25000 / 0x3D59000 / 0x3D5C000 / 0x3F8E000. The 349MB protector section and everything after it
are gone. Every address in the 0x15xxxxxxx range (Denuvo VM springboards) no longer exists; the
only one in this repo is a dev-only spike (StatusSpike), which re-anchors lazily per the runbook.

## Offline audit results (tools/probes/exe_reanchor_scan.py, tools/probes/rip_xref_reanchor.py)

Content re-find, old 1.5.2 backup against the new exe: Barrage.AbilityBase, DecoyActionTable,
InflictTable, FnSetTextString and the three BodyDoubleSpike code entries all UNCHANGED at their
pinned address. LiveActionTable reported AMBIGUOUS at 20-byte strides, which is the table's own
row repetition (a 256-byte window inside a 368x20 table matches at every row boundary nearby),
the same result the 1.5.2 pass saw; the decoy mirror one table-length away re-found unique.

RIP-relative cross-reference of the runtime globals: 15 unchanged, 0 moved, 4 unresolved for lack
of an xref in the old code (Slot0, ArrayBase, TerrainGrid, MenuCursor; all four were also
unresolved offline on 1.5.2 and verified by that live pass). Consensus counts: RosterBase 37/37,
ActorPtr 34/34, EventId 22/22, SubmenuFlag 18/18, InventoryCountBase 17/17, PauseFlag 14/14.

The data layer: the game's pac files kept their 2026-07-24 date; `python tools/audit_nxd_bakes.py`
exits 0 (item 1088 intended cells, ability 7, UIStatusEffect 5, zero drift). No LW-78 rebase needed.

## The symptom, explained

LaunchGuard reads the PE header at a fixed ModuleBase of 0x140000000. On the relocated image that
address is unmapped, so the landmark verdict is Unreadable, which by design retries silently (a
boot-window non-arm is normal). The mod therefore never arms, never counts, never paints, and never
stands down loudly. The AnchorScout re-find inventory never runs either, because it only starts
after a stand-down. Players saw "kill counts stopped" with no message. Saves were never at risk:
Mem.WritesEnabled stayed false the whole time.

## What the fix is (LW-387, hotfix/2.3.4 from v2.3.3)

One rule: every address the mod knows stays written as the documented preferred-base number, and
is resolved through ImageBase.At(pin), which adds the live main-module base minus 0x140000000.
The default base is the preferred one, so every existing test and every probe reads the same
numbers it always did. The guard learns the new PE key. A source tripwire test forbids new bare
image literals outside the allow-listed files. Live confirmation of the relocation itself is the
owner pass: the startup log names the resolved base.

## Standing caveats

- Treasure Master (present on this branch, removed later on main) stays disarmed because its
  baked LivingWeapon/treasure.json carries the 1.5 build key, not because of this change. Its
  tile addresses are preferred-base absolutes and are not rebased; do not re-key treasure.json on
  this build (owner direction: the module is slated for removal).

- This audit is evidence about ADDRESSES and blind to SEMANTICS (the 1.5.1 pause-flag lesson).
  The owner live pass owns the behavioral verdict, as always.
- The external python probes under tools/probes/ still address the game at the preferred base.
  With ASLR on they must add the live module base before any live use; that is a tooling
  follow-up, not part of the hotfix.
- Main (the 2.4.0 cycle) carries 146 pinned addresses plus the extended-inventory code patches
  and a near allocator pinned under the old fixed base. The same rule must be applied there as its
  own ledger item before 2.4.0 can ship on this build.
