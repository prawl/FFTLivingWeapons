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

## Denuvo-tail code sites: two patches had to be re-found, not rebased (LW-388, 2026-10-16)

Two of the extended inventory's byte patches (ExtendedSites.PostLoadSites) pointed into the removed
protector blob: 0x14F2EA40F (the damage-path hand resolver) and 0x14F45D315 (the damage-staging cap,
an `xor r15d,0x5E` that was Denuvo's obfuscation of the constant 6). Addresses in that range are not
image pins; `ImageBase.At` on them is meaningless. Both functions now live in plain .text:

| former (blob) | plain .text | evidence |
|---|---|---|
| 0x14F2EA40F `lea ecx,[rdx+6]` disp8 | 0x14027F002 | `ba ff 00 00 00` then `8d 4a 06`, two `call 0x1402B8C74` (ThunkWeaponStat) within 0x30 bytes; absent from the old exe's plain code (8 resolver sites on the new exe, 7 on the old) |
| 0x14F45D315 `xor r15d,0x5E` | 0x1402822A4 (`44 8d 75 06` at 0x1402822A1..A4, lea r14d,[rbp+6] after `bd ff 00 00 00` at 0x140282287; the first transcription said A5 and the verify round corrected it against the exe on disk) | the swing publisher at 0x140282234: stores combat +0x20 to 0x1407B0764 and +0x24 to 0x1407B0766, writes word 0x1407B0762 = 1, compares r14w against the id and substitutes the off-hand word above the cap, exactly the journal's Pub2 |

Method, for the next time a blob address needs a plain home: scan the new exe's code section for the
instruction SHAPE the journal recorded (the `mov r32,0xff` then `lea r32,[r32+6]` bound pair, or the
RIP-relative references to the globals the routine is known to write, resolved by
`site + 5 + imm + disp32`), then confirm by the neighbours (the thunk calls, the stores). Denuvo's
constant obfuscations (xor, add-sub pairs) have no plain equivalent; search for the plain constant or
the plain `lea` instead. Being plain .text, the re-found sites are readable at boot and become
BootSites; the post-load PendingPatch path keeps zero sites.

Second lesson from the same review: the near allocator searched its 2GB window from the bottom up,
which under ASLR lands 2GB below the image and out of rel32 reach of every patched field. It now
searches upward from the image first (LiveNearAllocator, LW-388).

## Accessor thunks became function entries: the first live pass of main failed on it (LW-388 R12, 2026-10-16)

In plain words: the extended inventory reaches ten tiny game routines (the per-item lookups for weapon
stats, validity, type, range, swing art and four siblings) by rewriting the first five bytes of each one
into a jump to a stub we own. On the Denuvo build those five bytes were ALREADY a jump (Denuvo had moved
the routine's body into its protected tail and left a jump behind), so the stub could simply forward to
the old destination. On the Denuvo-free build the body is back where it belongs: the five bytes are the
start of the routine itself. The redirect code checked for a jump, found a routine, and refused, which
stood the whole extended inventory down with one honest line
(`weapon-stat: thunk 0x7FF65FF18C74 is not an E9 jump (reads 4883EC2844)`). Correct behavior, wrong world.

Why the offline audit missed it: the audit compared ADDRESSES (section spans and pinned RVAs), and those
are unchanged. Nobody compared the BYTES at the ten thunk sites, which are the one place the two builds
legitimately differ (jump versus body). The 2026-10-16 byte diff that found this (16 bytes at all 311 image
literals in the runtime source, old exe versus new) shows them as the only real change: 286 windows
identical, 22 different, of which ten are these entries, two are the re-found Denuvo-tail sites, four are
unchanged sites whose window runs into a following entry, and six are comment citations.

The ten entries on the Denuvo-free exe (capstone, bytes on disk):

    0x1402B8C74 weapon-stat   48 83 EC 28 44 0F B7 C9     sub rsp,28h; movzx r9d,cx       (8 bytes)
    0x1402B8EBC validity      0F B7 C1 41 B8 01 00 00 00  movzx eax,cx; mov r8d,1         (9)
    0x1402B8EE8 type-probe    48 83 EC 28 44 8B CA        sub rsp,28h; mov r9d,edx        (7)
    0x1402B8BCC range-index   B8 FD 00 00 00              mov eax,0FDh                    (5)
    0x1402B8E60 sprite-pair   48 83 EC 28 E8 63 FD FF FF  sub rsp,28h; call range-index   (9, call rel32 at +4)
    0x1402B8C0C range-base    48 83 EC 28 E8 B7 FF FF FF  sub rsp,28h; call range-index   (9, call rel32 at +4)
    0x1402B8CD4/D3C/DA0/E04 siblings 1-4: 48 83 EC 28 44 0F B7 C9                         (8 each)

The old Denuvo-tail bodies (decoded from the old jumps: 0x14FE85562, 0x14FED2C3F, 0x14FEED4B0, 0x14FE66B2F,
0x14FEC80C0, 0x14FE6FF5D, 0x14FE97BF0, 0x14FEACE84, 0x14FEB8430, 0x14FEBE43B) carry the same opcode shapes
with Denuvo's constant obfuscation removed, so these are the same routines, not replacements. The
accessors call each other (weapon-stat and the siblings call range-base; range-base, type-probe and
sprite-pair call range-index) and every such call enters at the entry, so a hook at the entry sees the same
traffic the old thunk did.

The fix (R12): hook each entry the way SwingIdFallbackHook already hooks its 7-byte site. Verify the exact
expected bytes, plant a five-byte jump to the unchanged stub (NOP pad to the stolen length), and give the
stub's pass path a trampoline in the same page that re-runs the stolen instructions and jumps back to
entry + stolen length. The two entries whose stolen bytes contain `call rel32` get the call re-encoded as
`call [rip+2]; jmp +8; imm64` (the target is relocation-invariant, it is the live range-index entry). The
stubs' register footprint (rax and rcx only, the r11 lesson in ITEM_CAP_261_BREAK_JOURNEY.md) is unchanged;
the trampoline runs the original instructions, so the hooked routine clobbers exactly what the original
did. A managed Reloaded.Hooks detour stays off the table for the same r11 reason.

Live result (owner pass 2026-10-16 11:02): base 0x7FF65FC60000; "Extended inventory armed: 7 new
item(s) ..., 37 cap patches, 10 accessor redirects, swing-id fallback armed, 5 hooks ..., item count
lists relocated to 0x7FF663C00000; menu order charts relocated to 0x7FF663C10000"; main guard armed
59 s after launch; the Warbrand (id 264) listed, equipped, swung for weapon damage (999, 168, 144, 72),
claimed a credited kill, and its card read 2 of 5; scan_logs --require-battle --flight CLEAN.

Lesson for the runbook: a game update that removes or adds a protector changes the BYTES at every thunk
site while leaving every ADDRESS alone. Step 1 of PATCH_REANCHOR must include a byte diff at every code
site the mod writes or verifies, not only an RVA audit.

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
