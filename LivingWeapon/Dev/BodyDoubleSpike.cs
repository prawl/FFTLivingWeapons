#if LWDEV
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// DEV-only spike. Duplicate the hovered unit as a decoy "Body Double" by cold-calling the engine's
/// render-node builder, then binding the node and stamping the donor's identity. F5 spawns, Ctrl+F5 despawns.
/// Every data lever is walled (see the history block in the class), so it drives the engine's own paths.
/// Flow. Copy the donor's combat struct into a vacant slot, enroll it in the AI registry while hidden, build
/// and bind the node, stamp identity from the donor, hold CT at 0, then lower the node from the sky.
/// Throwaway save only. Notes on safety, usage and the Canary 1-9 history sit at the top of the class.
/// </summary>
internal sealed class BodyDoubleSpike
{
    // History (live runs 2026-07-10 unless dated otherwise).
    //
    // Why not data. The override nxd cannot author AlwaysPresent (Present column masked to 0xC), the
    // facing/elevation gate is dormant, and sprite/job is not the cause (Frank rebuilt as a Monk, a loaded
    // sheet, still stayed hidden). Frank (battle 435, combat slot 7, job +0x03 = 0x4E) is a vanilla
    // hidden-reserve unit +0x01 = 0xFF, +0x1B5 = 0, sceneNodeIdx[model] = -1. FFHacktics Event 0x47
    // AddGhostUnit registers the sprite so the engine builds a node; this spike cold-calls the builder
    // 0x14026EBEC on PATH A instead, cribbing a drawn sibling's scene-load entry so the sprite-id lookup
    // 0x14E90A5CB resolves rather than self-aborting and freeing the node. It is the reverse of despawn
    // 0x14023BFB0 (the per-unit remove).
    //
    // Canary 1. The build alone drew a Monk for Frank, but as a shallow clone riding the sibling's identity
    // and animation. Wrong tile, not a listed unit, and the real Monks went black while it froze mid-walk.
    // Canary 2. Scene-bind body (per-slot writes 0x1401D517D..0x1401D51B6) right after the build:
    // node+0x150 = combat ptr, combat+0x01 = slot idx (clears the 0xFF gate), node+0x11 = sprite-resource
    // index, node+0x230/0x238 = own animation objects (1-arg cold calls 0x14E8CBF78/0x14E8F2CA0), and
    // combat+0x1B5 = 1. The node+0x270 sub-block (0x1401D51BD..0x1401D5224) is skipped. It consumes the edx
    // that 0x14E8F2CA0 returns beside rax, which C# cannot capture, and the builder already leaves
    // node+0x270 workable. Result. A real listed party-guest Monk, but his turn never completed and the
    // real Monks blacked out when animating. Canary 4 fixed the turn and shrank the black-out to a reddish
    // tint from a garbage +0x05 faction byte.
    // Canary 3. Decoy CT-hold. The scheduler grants a turn at CT >= 100 (combat +0x41, band +0x25,
    // Offsets.ACtSlam, the byte ExtraTurn writes to grant turns). The clone has no action state, so its
    // turn stalled the queue; CT is zeroed at bind and held at 0 every tick, and F5 then toggles the hold.
    // Canary 3 also dumps the sibling's and the new node (0x548 bytes each) for the black-Monk diff.
    // Canary 4. Stamps cribbed from a live player donor (combat slots 16..20): +0x1BE = 01 real-unit
    // marker (writer 0x140456444); +0x191/+0x192 roster-identity backref (FE = unassigned, which crashed
    // turn arrival at 0x14018D102, a null controller singleton, factory object id 0x151); +0x05/+0x06/+0x1EE
    // faction/tint pair plus its mirror. The pair also routes names. Ramza's pair renamed the clone
    // "Ramza". With the stamps Kerrich took turns, moved, attacked and died into a chest, no crashes.
    // AI freeze. The enemy AI idles while a walk-visible, unenrolled clone exists. Ruled out. Count
    // corruption (Canary 5's refresh 0x140284FE4 is a destructive bulk rebuild, count 46 plus ~38 phantoms;
    // never call it) and duplicate identity. CE trace (execution BPs work, data watchpoints do not):
    // dispatch 0x140213C7A runs, but subject-select 0x14020F660 takes its cache-hit path (0x14020F684 je
    // 0x14020FABB, latch [0x140C6B307] == 0) to the epilogue 0x14020FB75 having selected nothing, and the
    // count loop 0x14020F769 never runs. The latch is armed at 0x1402D3EC3. Deeper tracing was stopped.
    // Canary 6 (Shift+F5) enrolls the clone in two steps, see the constants below.
    // Canary 7 (player-slot pivot): every byte lever poked live (count, identity, agency, the 6a/6b
    // enroll) left the AI frozen, and only removing the clone from the walk-visible set un-sticks it (gate
    // FF, or the death conversion; a KO'd corpse still freezes). An extra walk-visible unit in the enemy
    // region (slot 7, past the 7 deployed enemies 0-6) is the trigger, so the clone moves into a vacant
    // player slot (16..20; vacant = the deploy loop's empty stamp, loop 0x140270D5C). The template's combat
    // struct is copied there, +0x1BC is fixed to the host's model id, and the same build and bind run
    // against the host. Recon (workflow wf_5c071993-d4b): the builder derives its stamp index from arg8
    // +0x1BC with an inclusive cmp al,0x14 (0x14026EDBD) and writes SecondTable[model] (0x14026EDC3)
    // before the SceneNodeIdx stamp (0x14026EDEA); the bind is a flat 21-slot loop (bound cmp r11d,0x15 at
    // 0x1401D523B) that assumes model == slot. The ENTD lever stays walled. No team column, Present
    // hard-masked to 0xC engine-side (OverrideEntryData.layout), and the UnitId hi-bit proxy was falsified
    // 2026-06-21 in the FFTMultiplayer team spike.
    // Death-watch. Removing (crystal-popping) the clone game-overed battle 435 twice, once with Ramza's
    // identity pair and once with a generic's (live re-point, write-verified), so the defeat check is not
    // identity-keyed and the cause is unknown (suspects. Removal bookkeeping for an uncounted unit,
    // battle-specific defeat conditions). Once the composed Dead bit sets, the crystal counter is pinned
    // at 3 (the Sanctuary counter-pin) so the removal edge never fires. KO'd is survivable.
    // Canary 8 (duplicator, 2026-07-10): F5 duplicates the hovered unit, with no pre-hidden ENTD units and
    // no per-map data; Frank the template is retired. The live donor supplies a loaded sheet, a cribbable
    // scene-load entry, a clonable battle-keyed registry object and identity stamps. Order matters:
    //   (1) copy the donor's 0x200 combat struct into a vacant same-region slot (+0x1BC = host slot,
    //       gate/membership FF, CT 0, logic tile = a free tile next to the donor, since co-tile spawns soft-lock);
    //   (2) registry enroll while still hidden (clone the donor's object, re-key +0x2C to the host slot,
    //       append at table[count], bump the count word then byte), so the clone is AI-resolvable before it is visible;
    //   (3) cold-call the builder with a1/a2/a3 = the destination tile (they land in the node's tile key
    //       +0x88/89/8A, the AI's tile lookup; cribbing the sibling's tile here froze Canary 7's binds), arg8 = host;
    //   (4) stage-2 bind plus identity stamps from the donor (+0x191/2 pair, faction trio, real-unit marker);
    //   (5) flourish. Node world X/Y by formula (28*tile+14), Z parked at -600 and stepped +12 per tick to
    //       the donor's altitude. The decoy CT-hold still engages at bind; F5 again releases it.
    //
    // Safety (an engine AV is uncatchable, so prevention is the only defense):
    //  - TargetReady needs Mem.Readable plus a prologue landmark on 0x14026EBEC. The builder self-aborts and
    //    frees the node if arg5 (sprite-id) does not resolve to a loaded handle.
    //  - Fire only while paused (PauseFlag == 1): the builder front-inserts into the render list [0x140D3A410]
    //    the renderer thread walks. A residual cross-thread window remains and is accepted for a single key press.
    //  - Genuine inLive frame, foreground window, writes enabled (fingerprint guard armed). g_mode
    //    (0x142FF9E2C) must read 0 (PATH A allocate) and the render-mgr singleton (*0x140CE3250) must be non-null.
    //
    // Usage. Any battle, throwaway save. Open the menu of the unit to duplicate (the hover picks the donor),
    // press F5 once, unpause and look up. The clone descends onto a free tile beside the donor. Watch whether
    // the enemy AI keeps acting. F5 again releases the CT hold; Ctrl+F5 despawns (hover, or the ghost orphan).
    // Shift+F5 is the staged 6a/6b enroll. Press 1 = 6a, press 2 = 6b, press 3 = refused (fully enrolled).
    // Run spawn_probe aicensus (external, read-only) before and after for the count and registry picture.
    private const long FnNodeBuild  = 0x14026EBEC;   // render-node builder (10-arg Win64; PATH A allocates + stamps)
    private const long SceneTable   = 0x141856728;   // scene-load entry table, stride 0x20 (idx 0..6 populated)
    private const long UnitsBase    = 0x141853CE0;   // BattleUnitsBase, stride 0x200
    private const long SceneNodeIdx = 0x140822DAC;   // int32[] indexed by combat +0x1BC model id (-1 = no node)
    private const long GModeAddr    = 0x142FF9E2C;   // 0 = PATH A (allocate + stamp); 1 = PATH B (reuse)
    private const long RenderMgrPtr = 0x140CE3250;   // arg10 = *this (render-mgr singleton)

    // Stage-2 bind constants (scene-bind body 0x1401D517D..): turn the built node into the host's own unit.
    private const long NodePool     = 0x140D30030;   // render-node pool base; node N = pool + sceneNodeIdx*0x548
    private const long SecondTable  = 0x140822D58;   // per-slot sprite-resource index (si = byte[+slot*4])
    private const long AnimBase     = 0x140D005A0;   // animByte = byte[AnimBase + si*0x32D6 + 1]
    private const long FnAnimA      = 0x14E8CBF78;   // 1-arg (ecx=animByte), bounded lookup -> node+0x230
    private const long FnAnimB      = 0x14E8F2CA0;   // 1-arg (ecx=animByte), bounded lookup -> node+0x238

    // Canary 6 (AI enroll). Step 6a cold-calls the per-slot AI-data allocator 0x140274F30(rcx=combat slot):
    // it reads the slot's +0x02 (0xFF = unassigned), takes the allocate branch (free-finder 0x140275248),
    // fills the entry 0x1411A7D10 + id*0x258, stamps entry+1 = id and returns the id in eax (-1 on failure)
    // without writing combat+0x02, so the caller writes the id there. 6a worked mechanically (row filled)
    // but did not un-freeze alone (2026-07-10). It leaves the count byte 0x140D407BB alone; 6b builds the AI
    // object (+0x2C is a copy of +0x02) the way the single-object paths 0x1402BDA00 / 0x140285A9A do.
    private const long FnEnroll     = 0x140274F30;   // per-slot AI-data allocator/filler; rcx=combat slot, returns id in eax (-1 fail)
    private const long AiCountByte  = 0x140D407BB;   // AI-object count byte (the walk bound; 6b bumps it by exactly 1)
    private const long AiRegistry   = 0x141800F50;   // 56 qword slots -> per-unit AI objects (6b appends one entry)
    private const int  AiRegistrySlots = 0x38;
    private const int  AiObjMatchOff   = 0x2C;       // word an AI object matches vs combat +0x02

    // Canary 6b (object build), lifted from the engine's single-object sequence 0x140285A96..0x140285AB3:
    // scratch fill 0x140275CE8 (stores r8d to the mode global 0x1437414A0, so we pass its current value back;
    // returns -1 on a bounds or vacant-row fail), then populator 0x140284A80(scratch, obj, row), which
    // memsets the 0x280 object itself and sets +0x2C = row id (0x140284B8E). 6b appends the object at
    // registry[count] and bumps the count word 0x1437414A8 and the count byte by 1. The id-map 0x143741530
    // write is skipped (semantics unclear; add it as 6c only if needed).
    // Canary 9 (coexistence fix, 2026-07-10 on a slot-19 clone): one byte turned a clone that drew but
    // crashed into a working AI fighter. The AI-turn orchestrator's gate 0x151086AE8
    // (`cmp byte [0x141873038+slot], 0x10`) reads a per-slot AI-roster index that the registry, node and
    // combat enroll never touch. Real units hold 0x00..0x07; an unenrolled clone holds 0xFF, so the arm
    // path skips it, the AI-subject pointer [0x141872EA0] never arms and the facing code 0x150E74A5D
    // derefs null (the auto-battle crash). Writing the host slot the next free index fixes it; membership
    // +0x02 and node id 0 did not matter. Data-only, no cold call.
    private const long AiRosterTable  = 0x141873038; // per-slot AI-roster index; 0xFF = not enrolled
    private const byte AiRosterCap    = 0x10;         // the orchestrator's gate constant (indices are < this)

    private const long FnScratchFill  = 0x140275CE8; // thunk -> 0x14EF1F570; (rcx=&scratch, edx=rowId, r8d=mode) -> eax (-1 fail)
    private const long FnObjPopulate  = 0x140284A80; // (rcx=&scratch, rdx=objPtr, r8=rowPtr); memsets + fills the object
    private const long AiObjPool      = 0x1437415A0; // AI objects, stride 0x280 (56 capacity)
    private const int  AiObjStride    = 0x280;
    private const long AiDataArray    = 0x1411A7D10; // AI-data rows, stride 0x258 (54 capacity)
    private const int  AiDataStride   = 0x258;
    private const long FillModeGlobal = 0x1437414A0; // the fill's r8d lands here; read + pass back = no-op
    private const long AiObjCountWord = 0x1437414A8; // the builder's own object count (the byte caches its al)
    private const int  ScratchBytes   = 0x300;       // the engine call site uses a 0x200 stack window; padded

    private const int EntryStride   = 0x20;
    private const int Slots         = 21;            // combat slots covered by BattleUnitsBase
    private const long FreshLabel   = 0x86;          // arg7 >= 16 forces the find-first-free (fresh alloc) branch

    // Descent flourish. Park the new node's world Z in the sky and step it to the donor's altitude each
    // tick. Idle nodes are unowned, so the animation is uncontested. World X/Y = 28*tile + 14.
    private const int WorldXOff     = 0x4C;           // node u16 world X
    private const int WorldZOff     = 0x4E;           // node u16 (signed) world Z; -12*height, Float adds one unit
    private const int WorldYOff     = 0x50;           // node u16 world Y
    private const short SkyZ        = -600;
    private const int DescentStep   = 12;             // one height unit per ~33ms tick

    // Decoy hold. CT = combat +0x41 (band +0x25, Offsets.ACtSlam, the byte ExtraTurn slams to 100 to grant a
    // turn). Write-only. Reads are unreliable (rod-pass CT trap), so the hold never reads it back.
    private const int CtOffset      = 0x41;
    private const int NodeSize      = 0x548;          // render-node stride (pool 0x140D30030)
    private static readonly int[] SummarySlots = { 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20 };
    private static readonly byte[] ZeroCt = { 0 };

    // Canary 7 constants. Host vacancy is the deploy loop's empty stamp (gate +0x01 = FF at 0x140270FC5 and
    // present +0x1B5 = FF at 0x140270FBE, workflow decode 2026-07-10). Death-watch. Composed Dead = combat
    // +0x61 bit 0x20; crystal counter = combat +0x07, pinned at 3 so the removal edge never fires.
    private const int HostLo        = 16;
    private const int HostHi        = 20;
    private const int PresentOff    = 0x1B5;
    private const int DeadStatusOff = 0x61;
    private const byte DeadBit      = 0x20;
    private const int CrystalCounterOff = 0x07;
    private static readonly byte[] CounterPin = { 3 };

    // Builder prologue (code_patch read 2026-07-10): mov [rsp+0x20],rbx; push rbp/rsi/rdi/r12/r13/r14/r15.
    private static readonly byte[] Prologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x20, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57 };

    // Both anim lookups open with movzx eax,cx; lea rcx,[rip+table] (disasm 2026-07-10); enough with Mem.Readable to reject a patched routine.
    private static readonly byte[] AnimPrologue = { 0x0F, 0xB7, 0xC1, 0x48, 0x8D, 0x0D };

    // Raw exe bytes (ic_disasm 2026-07-10): FnEnroll opens mov [rsp+0x10],rbx; mov [rsp+0x18],rbp; push rsi/rdi/r12/r14.
    private static readonly byte[] EnrollPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56 };

    // 6b landmarks (ic_disasm 2026-07-10): FnScratchFill is a fixed .xcode thunk (jmp rel32 to 0x14EF1F570);
    // FnObjPopulate opens mov rax,rsp; mov [rax+8],rbx; mov [rax+0x10],rbp; mov [rax+0x18],rsi.
    private static readonly byte[] ScratchFillThunk =
        { 0xE9, 0x83, 0x98, 0xCA, 0x0E };
    private static readonly byte[] ObjPopulatePrologue =
        { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48 };

    // Ctrl+F5 despawn (agent a0239384, 2026-07-10). The ledger's "cold-call 0x14023BFB0(ecx=slot)" was wrong:
    // that address is mid-instruction in the event interpreter's opcode-0x3D wait loop (fn 0x14023A8E0, a
    // coroutine). The mechanism is data-only. Write mode 2 into the node's +0x12C flag word
    // ((old & ~0x30) | 0x20 = "remove me") and the per-frame node sweeper 0x14026E20C does the rest:
    // combat+0x01 = 0xFF (0x14026E2D3), combat+0x1B5 = 0x80 (0x14026E2DB), node+0x12C |= 0x30 done-mark
    // (0x14026E2B3), then leaf teardown 0x14026E16C (clears the element in-use dword and the conditional
    // model-loaded byte, unlinks the node from [0x140D3A410]). Vanilla uses the same primitive for crystal
    // and chest conversion (callers 0x1401F0A47/0x1401F25A8) and scripted removal. The sweeper is gated on
    // not-paused ([0x140C6B1C8] != 1), so the mark lands while paused and the removal completes on unpause;
    // Tick watches for the done-mark and a timeout restores the saved +0x12C. Target. The hovered unit
    // (condensed cursor fingerprint, any team), else the unique orphan (gate FF but still back-linked via
    // +0x148, a ghost-statue sprite). sceneNodeIdx goes stale after vanilla removals; the next bind's dedup
    // sweep 0x14026EDD2 scrubs it.
    private const long NodeListHead   = 0x140D3A410;  // render-node singly-linked list head
    private const int  NodeIdOff      = 0x08;         // node id byte (list lookups match this)
    private const int  NodeCombatOff  = 0x148;        // node -> combat back-pointer (builder-written)
    private const int  NodeModeOff    = 0x12C;        // flag word; bits 0x30 = removal mode/done
    private const byte NodeModeRemove = 0x20;         // mode 2 = "remove me" (sweeper consumes)
    // The per-id byte table 0x140C6CFE0 (stride 9) is the "engine engaged with this unit" marker, not a busy gate; see FireDespawn.
    private const long SpecialNodeId  = 0x140CF873C;  // dword. The current actor's node id; never remove it
    private const int  NodeWalkMax    = 64;           // list-walk bound, same as spawn_probe
    // Sweeper mode-dispatch landmark at 0x14026E28B. Confirms the +0x12C semantics before any write (bytes read 2026-07-10).
    private const long SweeperDispatch = 0x14026E28B;
    private static readonly byte[] SweeperDispatchBytes =
        { 0x8B, 0xCA, 0xC1, 0xE9, 0x04, 0x83, 0xE1, 0x03, 0x83, 0xE9, 0x01, 0x74, 0x5C, 0x83, 0xF9, 0x01 };
    private const int TurnFlagOff = 0x1B8;            // PSX turn flag (frame +0x1B8): nonzero = turn open
    private const int DespawnTimeoutTicks = 300;      // ~10s at 33ms. Sweeper never fired -> revert

    // Win64. Args 1-4 in rcx/rdx/r8/r9, args 5-10 on the stack at [rsp+0x20..0x48]. Widths <= 8 bytes take
    // a full slot; the callee reads cl/dl/r8b/r9w and the word/dword stack fields.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long NodeBuildFn(long a1, long a2, long a3, long a4, long a5,
                                      long a6, long a7, long a8, long a9, long a10);

    // Anim object fetch. Ecx = animByte, returns rax. 0x14E8F2CA0 also returns edx, which C# cannot capture (see the Canary 2 history).
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long AnimFn(int animByte);

    // Canary 6 enroller. Rcx = combat slot ptr; edx is ignored for Kerrich (spriteset 0x80 > 0x7f skips the
    // edx branch at 0x140274F58). Returns the AI-data id in eax, -1 on failure.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnrollFn(long rcx, int edx);

    // 6b pair. Scratch fill (-1 on bounds or vacant-row fail) and the object populator (memsets, then fills the 0x280 object).
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ScratchFillFn(long scratch, int rowId, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ObjPopulateFn(long scratch, long obj, long row);


    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private const int VkF5 = 0x74;   // reveal + hold toggle. F6 is ProvokeSpike's; ShowSpike also claims F5/F8 but is not wired in Engine (the old F6 spikes are deleted).
    private const int VkShift = 0x10; // Shift+F5 = staged enroll. ShowSpike.cs's key doc records F3/F7/F9 as eaten by the LL hook and F8 as stopped-registering on this box, so F5 with a Shift gate avoids untested keys.
    private const int VkCtrl = 0x11;  // Ctrl+F5 = clean despawn; same scarce-key reasoning as Shift.

    private readonly IGameMemory _mem;
    private readonly string _saveDir;   // update-safe (Reloaded\User\Mods): survives deploys AND log rotation
    private bool _f5Was;
    private int _hbTick;
    private bool _announced;
    private bool _bound;            // stage-2 bind completed this battle; F5 now toggles the decoy hold
    private bool _decoyHold;        // per-tick CT pin active (Frank's turn never arrives)
    private bool _stamped;          // Canary 4 stamps landed this battle; hold release is refused without them
    private bool _boundThisBattle;  // witness. BindStage2 ran since the last battle edge; the re-arm path requires it so a mispressed F5 never pins a real unit's CT
    private long _hostCombat;       // Canary 7. The claimed vacant player slot's combat base (0 = none yet)
    private byte _hostModel;        // the host's own slot index == its model id (+0x1BC fixup, +0x01 at bind)
    private bool _deathPinned;      // death-watch latch. The crystal-counter pin engaged (one loud log)
    private long _despawnNode;      // Ctrl+F5 pending removal. The mode-2-marked node (0 = none)
    private long _despawnCombat;    // its combat base (completion check + host-state cleanup)
    private byte _despawnOldMode;   // the +0x12C byte before the mark (restored on sweeper timeout)
    private int _despawnTicks;      // ticks since the mark (timeout counter)
    private long _donorCombat;      // Canary 8. The duplicated unit (identity stamps + registry clone source)
    private long _descendNode;      // descent flourish. The node being lowered (0 = none)
    private short _descendTarget;   // the donor's altitude the descent lands at
    private int _holdHb;

    public BodyDoubleSpike(IGameMemory mem, string saveDir)
    {
        _mem = mem;
        _saveDir = saveDir;
    }

    /// <summary>In-battle loop tick. Heartbeat, the F5 edge (reveal before the bind, hold toggle after) and the
    /// per-tick CT pin. The cold call runs on the loop thread (as in ShowSpike/StatusSpike), paused frames only.
    /// <paramref name="inLive"/> is the caller's genuine-live predicate.</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Debug(LogVerb.Trace, "body-double: armed (dev, CANARY 8 THE DUPLICATOR). Any battle, THROWAWAY SAVE. Open the menu of the unit to duplicate (hover = donor), then F5: copy + registry enroll + node build + bind, and the clone descends from the heavens beside the donor. Ctrl+F5 despawns. Watch the enemy AI.");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"body-double: alive (writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (Pressed(VkF5, ref _f5Was))
        {
            // Ctrl+F5 is checked first so Ctrl+Shift never reaches the enroll. Shift+F5 is the Canary 6 enroll,
            // which replaced the dead Canary 5 refresh (0x140284FE4).
            if ((GetAsyncKeyState(VkCtrl) & 0x8000) != 0)
            {
                if (inLive) FireDespawn();
                else ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 ignored (not a settled live frame; try again in a paused unit menu).");
            }
            else if ((GetAsyncKeyState(VkShift) & 0x8000) != 0)
            {
                if (inLive) FireEnroll();
                else ModLogger.Event(LogVerb.Trace, "body-double: SHIFT+F5 ignored (not a settled live frame; try again in a paused unit menu).");
            }
            else if (_bound) ToggleHold();
            else if (inLive) Fire();
            else ModLogger.Event(LogVerb.Trace, "body-double: F5 ignored (not a settled live frame; browse/enemy frames read mode 1, try again in a paused unit menu).");
        }
        // The hold does not gate on raw inLive. This site is only reached while the debounced battle is in, and
        // raw inLive dips false on every mode-1 frame, which is when CT accrues. Teardown is ResetBattle
        // (Engine.ResetBattleState). The write target is a fixed static slot, so the wider gate adds no
        // wrong-address risk (TreasureMaster pre-gate lesson).
        if (_decoyHold) HoldDecoy();
        if (_boundThisBattle) DeathWatch();
        if (_despawnNode != 0) DespawnWatch();
        if (_descendNode != 0) DescendStep();
    }

    /// <summary>Step the clone's world Z toward the donor's altitude, one height unit per tick. The clone's
    /// first real move or turn-open re-stamps Z anyway.</summary>
    private void DescendStep()
    {
        if (!Mem.WritesEnabled) { _descendNode = 0; return; }
        short z = unchecked((short)_mem.U16(_descendNode + WorldZOff));
        if (z >= _descendTarget)
        {
            _descendNode = 0;
            ModLogger.Event(LogVerb.Trace, $"body-double: the clone has touched down (Z {_descendTarget}).");
            return;
        }
        z = (short)Math.Min(z + DescentStep, (int)_descendTarget);
        _mem.WriteBytes(_descendNode + WorldZOff, BitConverter.GetBytes((ushort)z));
    }

    /// <summary>Debounced battle-edge teardown, called from Engine.ResetBattleState (both edges, as for
    /// Larceny). A raw mid-battle inLive dip must not land here.</summary>
    public void ResetBattle()
    {
        if (!_bound && !_decoyHold && !_boundThisBattle && _hostCombat == 0
            && _despawnNode == 0 && _descendNode == 0) return;
        _bound = false;
        _decoyHold = false;
        _boundThisBattle = false;
        _stamped = false;
        _hostCombat = 0;
        _hostModel = 0;
        _deathPinned = false;
        _despawnNode = 0;   // the scene teardown owns every node at a battle edge; never revert across it
        _donorCombat = 0;
        _descendNode = 0;
        ModLogger.Event(LogVerb.Trace, "body-double: battle edge; host claim + bind + decoy hold cleared (a new battle needs a fresh F5).");
    }

    /// <summary>Post-bind F5. Flip the decoy hold. Release is refused unless the Canary 4 stamps landed this
    /// battle, since an unstamped unit crashes on turn arrival (null controller id, 0x14018D102).</summary>
    private void ToggleHold()
    {
        if (_decoyHold && !_stamped)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: hold release REFUSED: the completeness stamps were skipped (no player donor), and an unassigned controller id crashes on turn arrival. Kerrich stays a decoy.");
            return;
        }
        _decoyHold = !_decoyHold;
        ModLogger.Event(LogVerb.Trace, _decoyHold
            ? "body-double: decoy CT-hold RE-ENGAGED (Kerrich takes no further turns)."
            : "body-double: decoy CT-hold RELEASED: Kerrich's turns go LIVE as a controllable unit (stamps verified this battle).");
    }

    /// <summary>Canary 3 decoy pin. Hold the double's CT at 0 every tick. Disengages if combat +0x01 no longer
    /// reads the model id stage 2 stamped.</summary>
    private void HoldDecoy()
    {
        if (!Mem.WritesEnabled || _hostCombat == 0) return;
        byte gate = _mem.U8(_hostCombat + 0x01);
        if (gate != _hostModel)
        {
            _bound = false;
            _decoyHold = false;
            ModLogger.Event(LogVerb.Trace, $"body-double: host +0x01 reads {gate:X2}, not the bound {_hostModel:X2}; the engine tore the bind down. Decoy hold disengaged.");
            return;
        }
        _mem.WriteBytes(_hostCombat + CtOffset, ZeroCt);
        if (++_holdHb % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, "body-double: decoy hold alive (CT pinned at 0; the queue should flow around the double)");
    }

    /// <summary>Death-watch (Canary 7): while the double's composed Dead bit is set, pin its crystal counter
    /// at 3 so the corpse never converts (see the history block). Revival releases the pin.</summary>
    private void DeathWatch()
    {
        if (!Mem.WritesEnabled || _hostCombat == 0) return;
        if ((_mem.U8(_hostCombat + DeadStatusOff) & DeadBit) == 0)
        {
            if (_deathPinned)
            {
                _deathPinned = false;
                ModLogger.Event(LogVerb.Trace, "body-double: double no longer reads Dead (revived?); crystal-counter pin released.");
            }
            return;
        }
        _mem.WriteBytes(_hostCombat + CrystalCounterOff, CounterPin);
        if (!_deathPinned)
        {
            _deathPinned = true;
            ModLogger.Event(LogVerb.Trace, "body-double: double is DOWN; crystal counter pinned at 3 so the corpse never converts (the removal edge game-overed battle 435 twice, 2026-07-10). Watch whether the enemy AI keeps acting over the corpse.");
        }
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was && GameIsForeground();   // never fire the cold call while alt-tabbed
        was = down;
        return pressed;
    }

    private static bool GameIsForeground()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == GetCurrentProcessId();
    }

    /// <summary>Verify the builder is mapped and still carries its prologue; its address lies beyond the
    /// image's always-mapped span.</summary>
    private bool TargetReady()
    {
        if (!Mem.Readable(FnNodeBuild, Prologue.Length))
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: builder 0x{FnNodeBuild:X} is not mapped this launch; refusing the cold call.");
            return false;
        }
        if (!_mem.TryReadBytes(FnNodeBuild, Prologue.Length, out var got))
        {
            ModLogger.Error(LogVerb.Trace, "body-double: could not read the builder prologue; refusing the cold call.");
            return false;
        }
        for (int i = 0; i < Prologue.Length; i++)
            if (got[i] != Prologue[i])
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: builder 0x{FnNodeBuild:X} prologue mismatch (game patched or wrong address); refusing.");
                return false;
            }
        return true;
    }

    /// <summary>Same check as TargetReady for the two anim lookups.</summary>
    private bool AnimTargetsReady()
    {
        foreach (long fn in new[] { FnAnimA, FnAnimB })
        {
            if (!Mem.Readable(fn, AnimPrologue.Length) || !_mem.TryReadBytes(fn, AnimPrologue.Length, out var got))
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: anim target 0x{fn:X} not readable this launch; skipping the stage-2 bind.");
                return false;
            }
            for (int i = 0; i < AnimPrologue.Length; i++)
                if (got[i] != AnimPrologue[i])
                {
                    ModLogger.Error(LogVerb.Trace, $"body-double: anim target 0x{fn:X} prologue mismatch; skipping the stage-2 bind.");
                    return false;
                }
        }
        return true;
    }

    /// <summary>Same check as TargetReady for the enroller.</summary>
    private bool EnrollTargetReady()
    {
        if (!Mem.Readable(FnEnroll, EnrollPrologue.Length) || !_mem.TryReadBytes(FnEnroll, EnrollPrologue.Length, out var got))
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: enroller 0x{FnEnroll:X} not readable this launch; refusing the Canary 6 cold call.");
            return false;
        }
        for (int i = 0; i < EnrollPrologue.Length; i++)
            if (got[i] != EnrollPrologue[i])
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: enroller 0x{FnEnroll:X} prologue mismatch (game patched or wrong address); refusing the Canary 6 cold call.");
                return false;
            }
        return true;
    }

    /// <summary>Registry membership by the planner's own key. The resolve loop 0x1402078F9 starts from the lea
    /// at 0x1402078ED = 0x141853CE1 (UnitsBase+1), so it skips the hide gate +0x01 and matches combat +0x02
    /// against registry object word +0x2C (disasm 2026-07-10; the lea at 0x14020793E uses base +0). Read-only;
    /// a garbage registry pointer just misses.</summary>
    private bool HostRegistered(out int regIdx)
    {
        regIdx = -1;
        if (_hostCombat == 0) return false;
        if (_mem.U8(_hostCombat + 0x01) == 0xFF) return false;   // planner's own skip. Hidden/unbound
        byte key = _mem.U8(_hostCombat + 0x02);                  // the match key vs registry word +0x2C
        for (int i = 0; i < AiRegistrySlots; i++)
        {
            ulong obj = _mem.U64(AiRegistry + i * 8L);
            if (obj == 0 || !Mem.Readable((long)obj + AiObjMatchOff, 2)) continue;
            if (_mem.U16((long)obj + AiObjMatchOff) == key)
            {
                regIdx = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>Canary 6 (Shift+F5), steps 6a and 6b. Cold-calls the AI-data allocator 0x140274F30 for the host
    /// slot and writes the returned id to combat +0x02; a second press builds the AI object (6b). Needs a
    /// paused unit menu (never rebuild AI state mid-planner) and a bind this battle. Refuses when already
    /// enrolled (+0x02 != 0xFF). Throwaway save. This is a cold call into engine allocation code.</summary>
    private void FireEnroll()
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: SHIFT+F5 ignored; the fingerprint guard has not armed yet.");
            return;
        }
        if (!_bound)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: SHIFT+F5 refused: no Kerrich bind this battle (the Canary 6 enroll is the AI-freeze fix, not a general lever).");
            return;
        }
        if (Mem.U8(Offsets.PauseFlag) != 1)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: SHIFT+F5 needs a paused unit menu (the turn-boundary rule: never enroll mid-planner).");
            return;
        }
        byte before02 = _mem.U8(_hostCombat + 0x02);
        if (before02 != 0xFF)
        {
            // 6a already ran (the AI-data row exists; row 0x2E read 80 2E 4E.. parallel to a real enemy's 80 01 57..). On to 6b.
            if (HostRegistered(out int already))
            {
                ModLogger.Event(LogVerb.Trace, $"body-double: SHIFT+F5 refused: the double is FULLY enrolled (id {before02:X2}, registry object idx {already}). Nothing left to build; watch the AI.");
                return;
            }
            FireObjectBuild(before02);
            return;
        }
        if (!EnrollTargetReady()) return;

        byte countBefore = _mem.U8(AiCountByte);
        int id;
        try
        {
            var enroll = Marshal.GetDelegateForFunctionPointer<EnrollFn>(unchecked((nint)FnEnroll));
            id = enroll(_hostCombat, 0);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: the Canary 6 enroll cold call threw (managed): {ex.Message}");
            return;
        }
        if (id < 0 || id > 0x35)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: enroller returned id {id} (out of the 0..0x35 AI-data range = allocation failed); +0x02 left untouched.");
            return;
        }
        _mem.WriteBytes(_hostCombat + 0x02, new[] { (byte)id });
        byte after02 = _mem.U8(_hostCombat + 0x02);
        bool isMember = HostRegistered(out int regIdx);
        ModLogger.Event(LogVerb.Trace,
            $"body-double: CANARY 6a enroll: allocated AI-data id {id}; combat+0x02 {before02:X2} -> {after02:X2}; count byte {countBefore} (untouched); registry-object match {(isMember ? $"idx {regIdx}" : "none yet")}. Unpause and watch the enemy AI; still frozen = press SHIFT+F5 again for 6b (the object build). LIVE 2026-07-10: 6a alone did NOT un-freeze, so expect to need 6b.");
    }

    /// <summary>Canary 6b. Build the AI object (see the constants block). Needs 6a to have run (combat +0x02 is
    /// a real id whose AI-data row has the id stamp at entry+1) and no registry object matching yet. Any
    /// precondition miss is a logged refusal. Two cold calls into engine code, throwaway save.</summary>
    private void FireObjectBuild(byte id)
    {
        if (id > 0x35)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: 6b refused: Kerrich +0x02 reads {id:X2}, outside the 0..0x35 AI-data range (not a 6a-issued id).");
            return;
        }
        long row = AiDataArray + (long)id * AiDataStride;
        byte rowId = _mem.U8(row + 1);
        if (rowId != id)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: 6b refused: AI-data row {id:X2} entry+1 reads {rowId:X2}, not the enroller's id stamp; the row does not look 6a-allocated.");
            return;
        }
        byte count = _mem.U8(AiCountByte);
        if (count >= AiRegistrySlots)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: 6b refused: count byte reads {count}, no free registry slot below the 0x{AiRegistrySlots:X} cap (corrupted count? run spawn_probe aicensus).");
            return;
        }
        long obj = AiObjPool + (long)count * AiObjStride;
        if (!Mem.Readable(obj, AiObjStride))
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: 6b refused: pool object slot {count} (0x{obj:X}) is not readable; not calling the populator at it.");
            return;
        }
        foreach (var (fn, want, name) in new[]
        {
            (FnScratchFill, ScratchFillThunk, "scratch fill"),
            (FnObjPopulate, ObjPopulatePrologue, "object populator"),
        })
        {
            if (!Mem.Readable(fn, want.Length) || !_mem.TryReadBytes(fn, want.Length, out var got))
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: 6b {name} 0x{fn:X} not readable this launch; refusing.");
                return;
            }
            for (int i = 0; i < want.Length; i++)
                if (got[i] != want[i])
                {
                    ModLogger.Error(LogVerb.Trace, $"body-double: 6b {name} 0x{fn:X} landmark mismatch (game patched or wrong address); refusing.");
                    return;
                }
        }

        int mode = (int)_mem.U32(FillModeGlobal);   // the fill stores r8d here; pass it back = no-op
        var pin = GCHandle.Alloc(new byte[ScratchBytes], GCHandleType.Pinned);
        try
        {
            long scratch = pin.AddrOfPinnedObject();
            int fillRet;
            try
            {
                var fill = Marshal.GetDelegateForFunctionPointer<ScratchFillFn>(unchecked((nint)FnScratchFill));
                fillRet = fill(scratch, id, mode);
            }
            catch (Exception ex)
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: the 6b scratch fill threw (managed): {ex.Message}");
                return;
            }
            if (fillRet < 0)
            {
                ModLogger.Event(LogVerb.Trace, $"body-double: 6b scratch fill returned {fillRet} (bounds/vacant-row fail) for row {id:X2}; object NOT built.");
                return;
            }
            try
            {
                var pop = Marshal.GetDelegateForFunctionPointer<ObjPopulateFn>(unchecked((nint)FnObjPopulate));
                pop(scratch, obj, row);
            }
            catch (Exception ex)
            {
                ModLogger.Error(LogVerb.Trace, $"body-double: the 6b object populator threw (managed): {ex.Message}; registry and counts left untouched.");
                return;
            }
        }
        finally
        {
            pin.Free();
        }

        ushort objKey = _mem.U16(obj + AiObjMatchOff);
        _mem.WriteBytes(AiRegistry + count * 8L, BitConverter.GetBytes(obj));
        ushort wordCount = _mem.U16(AiObjCountWord);
        _mem.WriteBytes(AiObjCountWord, BitConverter.GetBytes((ushort)(wordCount + 1)));
        _mem.WriteBytes(AiCountByte, new[] { (byte)(count + 1) });
        bool member = HostRegistered(out int regIdx);
        ModLogger.Event(LogVerb.Trace,
            $"body-double: CANARY 6b object build: pool slot {count} (0x{obj:X}) populated for row {id:X2}, object +0x2C reads {objKey:X4}; registry[{count}] appended, count word {wordCount} -> {wordCount + 1}, count byte {count} -> {_mem.U8(AiCountByte)}; membership {(member ? $"CONFIRMED idx {regIdx}" : "STILL ABSENT (key mismatch, expected the row id)")}. Unpause and watch the enemy AI.");
    }

    /// <summary>Ctrl+F5 clean despawn. Resolve a target, then mode-2 mark its render node (see the Ctrl+F5
    /// block above) so the unit and its node go together; a gate-FF poke alone strands a ghost-statue sprite.
    /// Refuses on. No unambiguous target, Ramza (slot 16; removing him is the defeat condition), an open turn
    /// (the AI-subject pointer crash decode, 2026-07-10), the reserved node id, a removal already in flight,
    /// or a bad sweeper-dispatch landmark. One-way once the sweeper runs.</summary>
    private void FireDespawn()
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 ignored; the fingerprint guard has not armed yet.");
            return;
        }
        if (Mem.U8(Offsets.PauseFlag) != 1)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 needs a paused unit menu (the despawn mutates the render list; pause idles the pipeline).");
            return;
        }
        if (_despawnNode != 0)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 refused: a despawn is already pending; unpause and let the sweeper run.");
            return;
        }
        if (!Mem.Readable(SweeperDispatch, SweeperDispatchBytes.Length)
            || !_mem.TryReadBytes(SweeperDispatch, SweeperDispatchBytes.Length, out var disp))
        {
            ModLogger.Error(LogVerb.Trace, "body-double: the sweeper dispatch landmark is unreadable this launch; refusing the despawn mark.");
            return;
        }
        for (int i = 0; i < SweeperDispatchBytes.Length; i++)
            if (disp[i] != SweeperDispatchBytes[i])
            {
                ModLogger.Error(LogVerb.Trace, "body-double: sweeper dispatch landmark mismatch (game patched?); the +0x12C mode semantics are unverified. Refusing.");
                return;
            }
        if (!ResolveDespawnTarget(out long node, out long combat, out int slot, out string how)) return;
        if (slot == 16)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 refused: slot 16 is Ramza; removing the story lead IS the defeat condition.");
            return;
        }
        if (_mem.U8(combat + TurnFlagOff) != 0)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: slot {slot}'s turn is OPEN (+0x1B8 nonzero); never remove the acting unit. Let the turn end first.");
            return;
        }
        byte id = _mem.U8(node + NodeIdOff);
        if (id == (byte)_mem.U32(SpecialNodeId))
        {
            // The special-id global is the current actor's node id (decoded 2026-07-10).
            ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: node id {id} is the current actor ([0x140CF873C]); never remove the acting unit.");
            return;
        }
        // No gate-byte refusal 0x140C6CFE0+id*9 is the "engine engaged with this unit" marker (menu open or
        // hover), which the aiming flow sets on the target. Refusing on it blocked every hover-resolved
        // despawn (hit 2026-07-10), and a pull with the marker at 01 completed fine.
        byte mode = _mem.U8(node + NodeModeOff);
        if ((mode & 0x30) != 0)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: node +0x12C reads {mode:X2}; a removal is already in flight on it.");
            return;
        }
        _despawnOldMode = mode;
        _mem.WriteBytes(node + NodeModeOff, new[] { (byte)((mode & ~0x30) | NodeModeRemove) });
        _despawnNode = node;
        _despawnCombat = combat;
        _despawnTicks = 0;
        ModLogger.Event(LogVerb.Trace,
            $"body-double: DESPAWN mode-2 marked on slot {slot} (node 0x{node:X}, id {id}, resolved by {how}). ONE-WAY once the sweeper runs. UNPAUSE: the sweeper is pump-gated and completes the removal on its own frame.");
    }

    /// <summary>Watch a pending mark. The sweeper 0x14026E20C stamps combat+0x01 = FF and node+0x12C |= 0x30
    /// when done. A timeout restores the saved mode byte so no half-marked node outlives the attempt.</summary>
    private void DespawnWatch()
    {
        byte gate = _mem.U8(_despawnCombat + 0x01);
        byte mode = _mem.U8(_despawnNode + NodeModeOff);
        if (gate == 0xFF && (mode & 0x30) == 0x30)
        {
            ModLogger.Event(LogVerb.Trace,
                $"body-double: DESPAWN COMPLETED by the engine sweeper (gate FF, node mode {mode:X2}); the unit and its sprite are gone.");
            if (_despawnCombat == _hostCombat)
            {
                _bound = false;
                _decoyHold = false;
                _stamped = false;
                ModLogger.Event(LogVerb.Trace, "body-double: the despawned unit was the double's host; bind state cleared (the host claim holds until the battle edge so F5 cannot re-copy the freed slot).");
            }
            _despawnNode = 0;
            return;
        }
        if (++_despawnTicks >= DespawnTimeoutTicks)
        {
            if (Mem.WritesEnabled) _mem.WriteBytes(_despawnNode + NodeModeOff, new[] { _despawnOldMode });
            ModLogger.Event(LogVerb.Trace,
                $"body-double: despawn TIMED OUT (~10s; sweeper never processed the mark; node mode reads {mode:X2}); the saved +0x12C byte was restored. Was the game left paused?");
            _despawnNode = 0;
        }
    }

    /// <summary>Despawn target resolution, fail-closed on ambiguity. The node comes from one bounded walk of
    /// the render-node list (head [0x140D3A410], node+0x148 = combat back-pointer); sceneNodeIdx goes stale
    /// after removals so it is not trusted. Tier 1. The hovered unit, by the condensed cursor struct's
    /// (level,hp,maxHp) fingerprint matched uniquely against visible slots. Tier 2. The unique orphan (combat
    /// gate FF, node still alive). Twin fingerprints or several orphans refuse.</summary>
    private bool ResolveDespawnTarget(out long node, out long combat, out int slot, out string how)
    {
        node = 0;
        combat = 0;
        slot = -1;
        how = "";
        var linked = new List<(long Node, long Combat, int Slot)>(NodeWalkMax);
        ulong cur = _mem.U64(NodeListHead);
        for (int i = 0; i < NodeWalkMax && cur != 0 && Mem.Readable((long)cur, NodeCombatOff + 8); i++)
        {
            long c = (long)_mem.U64((long)cur + NodeCombatOff);
            long off = c - UnitsBase;
            if (off >= 0 && off < Slots * 0x200 && off % 0x200 == 0)
                linked.Add(((long)cur, c, (int)(off / 0x200)));
            cur = _mem.U64((long)cur);
        }
        if (linked.Count == 0)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: CTRL+F5 refused: the render-node list walk found no unit nodes (not in a rendered battle?).");
            return false;
        }

        if (Mem.Readable(Offsets.TurnQueue, Offsets.TqMaxHp + 2))
        {
            ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
            ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
            ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
            if (maxHp > 0 && maxHp < 60000 && level >= 1 && level <= 99)
            {
                int matches = 0;
                long mCombat = 0;
                int mSlot = -1;
                for (int s = 0; s < Slots; s++)
                {
                    long c = UnitsBase + s * 0x200L;
                    if (_mem.U8(c + 0x01) == 0xFF) continue;   // hidden units are not hoverable
                    long e = c + 0x1C;
                    if (_mem.U16(e + Offsets.AMaxHp) != maxHp) continue;
                    if (_mem.U16(e + Offsets.AHp) != hp) continue;
                    if (_mem.U8(e + Offsets.ALevel) != level) continue;
                    matches++;
                    mCombat = c;
                    mSlot = s;
                }
                if (matches > 1)
                {
                    ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: the hovered fingerprint matches {matches} slots (twins); hover a uniquely-statted unit.");
                    return false;
                }
                if (matches == 1)
                {
                    foreach (var l in linked)
                        if (l.Combat == mCombat)
                        {
                            node = l.Node;
                            combat = mCombat;
                            slot = mSlot;
                            how = $"hover fingerprint lvl {level} hp {hp}/{maxHp}";
                            return true;
                        }
                    ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: hovered slot {mSlot} has no render node in the list (nothing to remove).");
                    return false;
                }
            }
        }

        // No hover answer. The unique ghost-statue orphan (node alive, combat gate FF).
        var orphans = new List<(long Node, long Combat, int Slot)>(2);
        foreach (var l in linked)
            if (_mem.U8(l.Combat + 0x01) == 0xFF)
                orphans.Add(l);
        if (orphans.Count == 1)
        {
            node = orphans[0].Node;
            combat = orphans[0].Combat;
            slot = orphans[0].Slot;
            how = "unique ghost-statue orphan (gate FF, node still linked)";
            return true;
        }
        ModLogger.Event(LogVerb.Trace, orphans.Count == 0
            ? "body-double: CTRL+F5 refused: nothing hovered and no ghost-statue orphan found; open the target unit's menu first."
            : $"body-double: CTRL+F5 refused: {orphans.Count} ghost-statue orphans (slots {string.Join(", ", orphans.ConvertAll(o => o.Slot))}); hover cannot pick between hidden units. Un-hide all but one first.");
        return false;
    }

    /// <summary>A drawn player unit (combat slots 16..20) to crib the Canary 4 stamps from +0x01 != 0xFF and a
    /// non-zero job (unreadable memory reads 0, which the job gate refuses). A +0x02 == 01 gate was wrong,
    /// that byte is dynamic turn state (Kenrick read 01, Ramza 00, which skipped the stamps 2026-07-10). The
    /// host slot is excluded, and generics 17..20 come before Ramza (slot 16) because the identity pair
    /// routes the name. Returns the combat base, or -1.</summary>
    private long FindPlayerDonor(long excludeCombat)
    {
        foreach (int slot in new[] { 17, 18, 19, 20, 16 })
        {
            long combat = UnitsBase + slot * 0x200L;
            if (combat == excludeCombat) continue;
            if (_mem.U8(combat + 0x01) == 0xFF) continue;
            if (_mem.U8(combat + 0x03) == 0) continue;   // a vacant slot's job byte (also the fail-safe refusal)
            return combat;
        }
        return -1;
    }

    /// <summary>Find a vacant slot in the donor's region (players 16..20, enemies 8..15). Vacant = gate FF and
    /// present FF; fully zeroed slots win over deploy-staging residue (a deployed generic once left a hidden
    /// stat ghost at slot 20). Returns the combat base and slot, or -1.</summary>
    private long FindVacantHost(int donorSlot, out int hostSlot)
    {
        int lo = donorSlot >= HostLo ? HostLo : 8;
        int hi = donorSlot >= HostLo ? HostHi : 15;
        long residue = -1;
        int residueSlot = -1;
        for (int slot = hi; slot >= lo; slot--)
        {
            long combat = UnitsBase + slot * 0x200L;
            if (_mem.U8(combat + 0x01) != 0xFF) continue;
            if (_mem.U8(combat + PresentOff) != 0xFF) continue;
            if (_mem.U8(combat + 0x03) == 0 && _mem.U8(combat + 0x29) == 0)
            {
                hostSlot = slot;
                return combat;   // zeroed. The preferred host
            }
            if (residue < 0)
            {
                residue = combat;
                residueSlot = slot;
            }
        }
        hostSlot = residueSlot;
        return residue;
    }

    /// <summary>The hovered unit is the donor. The condensed cursor struct fingerprint matched uniquely against
    /// visible slots. Fails closed on twins and non-hover states.</summary>
    private bool ResolveDonor(out long combat, out int slot)
    {
        combat = 0;
        slot = -1;
        if (!Mem.Readable(Offsets.TurnQueue, Offsets.TqMaxHp + 2))
        {
            ModLogger.Event(LogVerb.Trace, "body-double: F5 refused: the cursor struct is unreadable (hover the unit to duplicate first).");
            return false;
        }
        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 60000 || level < 1 || level > 99)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: F5 refused: no plausible hovered unit (open the donor's menu first).");
            return false;
        }
        int matches = 0;
        for (int s = 0; s < Slots; s++)
        {
            long c = UnitsBase + s * 0x200L;
            if (_mem.U8(c + 0x01) == 0xFF) continue;
            long e = c + 0x1C;
            if (_mem.U16(e + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(e + Offsets.AHp) != hp) continue;
            if (_mem.U8(e + Offsets.ALevel) != level) continue;
            matches++;
            combat = c;
            slot = s;
        }
        if (matches != 1)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: F5 refused: the hovered fingerprint matched {matches} slots (need exactly 1).");
            combat = 0;
            slot = -1;
            return false;
        }
        return true;
    }

    /// <summary>A free tile next to the donor. Neighbors first, then range 2 on the four axes. Co-tile spawns
    /// soft-lock targeting and movement. Walkability is not checked, so a wall pick is cosmetic.</summary>
    private bool FindFreeTileNear(long donor, out byte tx, out byte ty)
    {
        int dx = _mem.U8(donor + 0x4F);
        int dy = _mem.U8(donor + 0x50);
        var candidates = new[]
        {
            (dx + 1, dy), (dx - 1, dy), (dx, dy + 1), (dx, dy - 1),
            (dx + 1, dy + 1), (dx - 1, dy - 1), (dx + 1, dy - 1), (dx - 1, dy + 1),
            (dx + 2, dy), (dx - 2, dy), (dx, dy + 2), (dx, dy - 2),
        };
        foreach (var (cx, cy) in candidates)
        {
            if (cx < 0 || cy < 0 || cx > 29 || cy > 29) continue;
            bool taken = false;
            for (int s = 0; s < Slots && !taken; s++)
            {
                long c = UnitsBase + s * 0x200L;
                if (_mem.U8(c + 0x01) == 0xFF) continue;
                if (_mem.U8(c + 0x4F) == cx && _mem.U8(c + 0x50) == cy) taken = true;
            }
            if (!taken)
            {
                tx = (byte)cx;
                ty = (byte)cy;
                return true;
            }
        }
        tx = ty = 0;
        return false;
    }

    /// <summary>The donor's scene-load entry (combat ptr at entry +0x10). The donor is drawn, so its sheet is
    /// loaded and the cribbed sprite-id resolves.</summary>
    private long FindSceneEntryOf(long combat)
    {
        for (int idx = 0; idx < 8; idx++)
        {
            long entry = SceneTable + idx * EntryStride;
            if ((long)_mem.U64(entry + 0x10) == combat) return entry;
        }
        return -1;
    }

    /// <summary>Copy the donor's 0x200 combat struct into the host and fix the per-slot fields +0x1BC = host
    /// slot (the builder's stamp index and the bind loop's model == slot rule), gate and membership FF and CT 0
    /// to keep it inert until the bind, logic tile = the free destination.</summary>
    private bool CopyDonorToHost(long donor, long host, int hostSlot, byte tx, byte ty)
    {
        if (!_mem.TryReadBytes(donor, 0x200, out var body))
        {
            ModLogger.Error(LogVerb.Trace, "body-double: could not read the donor's combat struct; refusing the copy.");
            return false;
        }
        _mem.WriteBytes(host, body);
        _mem.WriteBytes(host + 0x1BC, new[] { (byte)hostSlot });
        _mem.WriteBytes(host + 0x01, new byte[] { 0xFF });
        _mem.WriteBytes(host + 0x02, new byte[] { 0xFF });
        _mem.WriteBytes(host + CtOffset, ZeroCt);
        _mem.WriteBytes(host + 0x4F, new[] { tx });
        _mem.WriteBytes(host + 0x50, new[] { ty });
        ModLogger.Event(LogVerb.Trace,
            $"body-double: CANARY 8 copy: donor 0x{donor:X} -> slot {hostSlot} (0x{host:X}) at free tile ({tx},{ty}); gate/membership FF, CT 0.");
        return true;
    }

    /// <summary>The freeze cure (shown by the resurrection arc): clone the donor's battle-keyed registry object,
    /// re-key +0x2C to the host slot, append at table[count], bump the count word then the byte so the walk
    /// never sees a half entry. Runs while the copy is hidden, so the clone is AI-resolvable before it is visible.</summary>
    private bool RegistryEnrollHost(int donorSlot, int hostSlot)
    {
        byte count = _mem.U8(AiCountByte);
        if (count >= AiRegistrySlots)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: registry count {count} at capacity; cannot enroll the clone.");
            return false;
        }
        long src = 0;
        var used = new HashSet<long>();
        for (int i = 0; i < count; i++)
        {
            long p = (long)_mem.U64(AiRegistry + i * 8L);
            if (p == 0) continue;
            used.Add(p);
            if (Mem.Readable(p + AiObjMatchOff, 2) && _mem.U16(p + AiObjMatchOff) == donorSlot) src = p;
        }
        if (src == 0)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: the donor (slot {donorSlot}) has no battle-keyed registry object to clone; refusing.");
            return false;
        }
        long dst = 0;
        for (int k = 40; k < 54; k++)
        {
            long cand = AiObjPool + (long)k * AiObjStride;
            if (!used.Contains(cand)) { dst = cand; break; }
        }
        if (dst == 0 || !_mem.TryReadBytes(src, AiObjStride, out var obj))
        {
            ModLogger.Event(LogVerb.Trace, "body-double: no free registry pool slot / unreadable donor object; refusing the enroll.");
            return false;
        }
        _mem.WriteBytes(dst, obj);
        _mem.WriteBytes(dst + AiObjMatchOff, BitConverter.GetBytes((ushort)hostSlot));
        _mem.WriteBytes(AiRegistry + count * 8L, BitConverter.GetBytes(dst));
        _mem.WriteBytes(AiObjCountWord, BitConverter.GetBytes((ushort)(_mem.U16(AiObjCountWord) + 1)));
        _mem.WriteBytes(AiCountByte, new[] { (byte)(count + 1) });

        // Canary 9. Give the host the next free AI-roster index (the count of slots already below the gate
        // cap; real units are 0..N-1 sequential).
        int nextIdx = 0;
        for (int s = 0; s < Slots; s++)
            if (_mem.U8(AiRosterTable + s) < AiRosterCap) nextIdx++;
        if (nextIdx >= AiRosterCap)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: AI-roster table is full ({nextIdx} indices); the clone cannot become an AI subject. Registry enroll stands, but expect the auto-battle crash.");
            return false;
        }
        _mem.WriteBytes(AiRosterTable + hostSlot, new[] { (byte)nextIdx });
        ModLogger.Event(LogVerb.Trace,
            $"body-double: registry enroll: donor obj cloned -> 0x{dst:X} keyed slot {hostSlot}, table[{count}], count -> {count + 1}; AI-roster index 0x141873038+{hostSlot} = {nextIdx:X2}. The clone is a FULL AI subject (the coexistence fix, live-proven).");
        return true;
    }

    private void Fire()
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: F5 ignored; the fingerprint guard has not armed yet.");
            return;
        }
        if (Mem.U8(Offsets.PauseFlag) != 1)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: F5 needs a paused menu (open a unit's menu first) to narrow the render-thread race.");
            return;
        }

        // Canary 7. A host is already claimed this battle. Recovery paths only; re-copying over a live bind
        // would stomp the engine's state on that slot.
        if (_hostCombat != 0)
        {
            byte hGate = _mem.U8(_hostCombat + 0x01);
            int hNode = (int)_mem.U32(SceneNodeIdx + _hostModel * 4);

            // Recovery re-arm. The double is bound (gate reads the host model, node exists) but the hold is off.
            // Re-pin without touching the engine so a transient teardown never strands a stall-capable double.
            // The host claim is the witness here, since only this spike's copy sets _hostCombat.
            if (hGate == _hostModel && hNode >= 0)
            {
                _mem.WriteBytes(_hostCombat + CtOffset, ZeroCt);
                _bound = true;
                _decoyHold = true;
                ModLogger.Event(LogVerb.Trace, "body-double: the player-slot double is already bound; decoy CT-hold re-armed on the existing bind.");
                return;
            }
            // A previous F5 built the node but stage 2 aborted. Bind the existing node instead of building
            // again, which would leak a second node under the same label.
            if (hGate == 0xFF && hNode >= 0)
            {
                long e = _donorCombat != 0 ? FindSceneEntryOf(_donorCombat) : -1;
                if (e < 0)
                {
                    ModLogger.Event(LogVerb.Trace, "body-double: no donor scene entry to finish the aborted stage-2; refusing.");
                    return;
                }
                ModLogger.Event(LogVerb.Trace,
                    $"body-double: sceneNodeIdx[{_hostModel}] = {hNode} is already stamped with the host still hidden (an aborted stage-2); re-binding the existing node, no second build.");
                BindStage2(hNode, _hostModel, e);
                return;
            }
            if (hGate != 0xFF)
            {
                ModLogger.Event(LogVerb.Trace, $"body-double: host +0x01 reads {hGate:X2} (neither hidden nor the bound {_hostModel:X2}); wrong state. Refusing.");
                return;
            }
            // hGate == 0xFF && hNode < 0. The copy landed but no node yet; fall through to the build.
        }
        else
        {
            // Canary 8. Duplicate the hovered unit. The donor supplies body bytes, scene entry, registry object and identity; order matters (history block).
            if (!ResolveDonor(out long donor, out int donorSlot)) return;
            long host = FindVacantHost(donorSlot, out int hostSlot);
            if (host < 0)
            {
                ModLogger.Event(LogVerb.Trace, $"body-double: no vacant slot in the donor's region (donor slot {donorSlot}); the clone needs one. Refusing.");
                return;
            }
            if (!FindFreeTileNear(donor, out byte tx, out byte ty))
            {
                ModLogger.Event(LogVerb.Trace, "body-double: no free tile adjacent to the donor (co-tile spawns are the proven soft-lock); refusing.");
                return;
            }
            if (!CopyDonorToHost(donor, host, hostSlot, tx, ty)) return;
            _hostCombat = host;
            _hostModel = (byte)hostSlot;
            _donorCombat = donor;
            if (!RegistryEnrollHost(donorSlot, hostSlot))
            {
                ModLogger.Event(LogVerb.Trace, "body-double: registry enroll failed; the copy stays hidden (an un-enrolled reveal is the proven freeze). F5 again retries the build once the registry has room.");
                return;
            }
        }

        uint gmode = _mem.U32(GModeAddr);
        if (gmode != 0)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: g_mode = {gmode} (not the allocate path 0); refusing.");
            return;
        }
        ulong renderMgr = _mem.U64(RenderMgrPtr);
        if (renderMgr == 0)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: the render-mgr singleton is null (not in a rendered battle); refusing.");
            return;
        }

        long entry = FindSceneEntryOf(_donorCombat);
        if (entry < 0)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: the donor has no scene-load entry to crib (not an init-built unit?); refusing.");
            return;
        }

        if (!TargetReady()) return;
        if (!_mem.TryReadBytes(entry, EntryStride, out var b))
        {
            ModLogger.Error(LogVerb.Trace, "body-double: could not read the donor's scene-load entry; refusing.");
            return;
        }

        long nodeIdxAddr = SceneNodeIdx + _hostModel * 4;
        int before = (int)_mem.U32(nodeIdxAddr);

        // Crib the donor's node args except a1/a2/a3 = the host's own tile (they land in node tile key
        // +0x88/89/8A, the AI's tile lookup; cribbing the donor's tile froze Canary 7's binds). arg8 = host,
        // arg7 = fresh label, arg10 = render-mgr. The builder stamps SceneNodeIdx[arg8 +0x1BC] and
        // SecondTable[model] itself.
        long a1 = _mem.U8(_hostCombat + 0x4F);           // dest tile x
        long a2 = _mem.U8(_hostCombat + 0x50);           // dest tile y
        long a3 = (byte)(_mem.U8(_hostCombat + 0x51) >> 7);   // layer bit
        long a4 = (ushort)(b[0x04] | (b[0x05] << 8));
        long a5 = (ushort)(b[0x06] | (b[0x07] << 8));   // sprite-id (the donor's own loaded sheet)
        long a6 = (ushort)(b[0x08] | (b[0x09] << 8));
        long a7 = FreshLabel;                            // force a fresh alloc, do not match the donor's node
        long a8 = _hostCombat;                           // bind the new node to the host
        long a9 = (uint)(b[0x18] | (b[0x19] << 8) | (b[0x1A] << 16) | (b[0x1B] << 24));
        long a10 = (long)renderMgr;

        ModLogger.Event(LogVerb.Trace,
            $"body-double: cribbing the donor's entry 0x{entry:X} (spriteId {a5:X4}); dest tile ({a1},{a2}), host model {_hostModel}, sceneNodeIdx[{_hostModel}] = {before}; COLD-CALLING builder 0x{FnNodeBuild:X} (arg8 = host 0x{_hostCombat:X})...");
        long ret;
        try
        {
            var build = Marshal.GetDelegateForFunctionPointer<NodeBuildFn>(unchecked((nint)FnNodeBuild));
            ret = build(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: the cold call threw (managed): {ex.Message}");
            return;
        }
        int after = (int)_mem.U32(nodeIdxAddr);
        bool built = before < 0 && after >= 0;
        ModLogger.Event(LogVerb.Trace,
            $"body-double: builder returned 0x{ret:X}; sceneNodeIdx[{_hostModel}] {before} -> {after}; NODE-BUILT={built}.");
        if (!built)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: the node was not stamped (build failed or self-aborted); skipping the stage-2 bind.");
            return;
        }
        BindStage2(after, _hostModel, entry);
    }

    /// <summary>Stage 2. Replicate the scene-bind body so the built node becomes the host's own (identity and
    /// animation) instead of a sibling clone. Cold-calls only the two anim lookups; the rest are guarded
    /// writes. Then applies the Canary 4 stamps, engages the CT hold and dumps forensics. The node+0x270
    /// sub-block is skipped (see the history block).</summary>
    private void BindStage2(int nodeIdx, byte modelId, long entry)
    {
        if (!AnimTargetsReady()) return;
        long node = NodePool + (long)nodeIdx * 0x548;
        byte si = _mem.U8(SecondTable + modelId * 4);
        byte animByte = _mem.U8(AnimBase + (long)si * 0x32D6 + 1);
        long r230, r238;
        try
        {
            var animA = Marshal.GetDelegateForFunctionPointer<AnimFn>(unchecked((nint)FnAnimA));
            var animB = Marshal.GetDelegateForFunctionPointer<AnimFn>(unchecked((nint)FnAnimB));
            r230 = animA(animByte);
            r238 = animB(animByte);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: an anim cold call threw (managed): {ex.Message}; node is built but not fully bound.");
            return;
        }
        _mem.WriteBytes(node + 0x150, BitConverter.GetBytes(_hostCombat));   // node->unit link (position + hover)
        _mem.WriteBytes(_hostCombat + 0x01, new[] { modelId });              // clears the 0xFF hide gate -> real listed unit
        _mem.WriteBytes(node + 0x11, new[] { si });
        _mem.WriteBytes(node + 0x230, BitConverter.GetBytes(r230));          // the double's OWN anim objects (decouples the clone)
        _mem.WriteBytes(node + 0x238, BitConverter.GetBytes(r238));
        _mem.WriteBytes(_hostCombat + 0x1B5, new[] { (byte)1 });             // draw enable, written last
        ModLogger.Event(LogVerb.Trace,
            $"body-double: STAGE-2 bound node 0x{node:X} to the player-slot host (si {si:X2}, animByte {animByte:X2}, node+0x230 0x{r230:X}, node+0x238 0x{r238:X}); combat+0x01 -> {modelId:X2}, +0x1B5 -> 01. Unpause: the double should be a real listed Monk in the player region. WATCH THE ENEMY AI.");

        // Canary 4 stamps. The duplicated unit is the source; the player-donor scan is the fallback when a
        // recovery path lost the donor.
        long donor = _donorCombat != 0 ? _donorCombat : FindPlayerDonor(_hostCombat);
        if (donor > 0)
        {
            byte id = _mem.U8(donor + 0x191);
            byte id2 = _mem.U8(donor + 0x192);
            byte t5 = _mem.U8(donor + 0x05);
            byte t6 = _mem.U8(donor + 0x06);
            byte tEE = _mem.U8(donor + 0x1EE);
            _mem.WriteBytes(_hostCombat + 0x1BE, new[] { (byte)1 });   // real-unit marker
            // The +0x191/+0x192 pair is the roster-identity backref that routes control and name (copying
            // Ramza's pair renamed the clone "Ramza"); that is the intended Body Double effect.
            _mem.WriteBytes(_hostCombat + 0x191, new[] { id });
            _mem.WriteBytes(_hostCombat + 0x192, new[] { id2 });
            _mem.WriteBytes(_hostCombat + 0x05, new[] { t5 });         // faction/tint pair
            _mem.WriteBytes(_hostCombat + 0x06, new[] { t6 });
            _mem.WriteBytes(_hostCombat + 0x1EE, new[] { tEE });       // the composed team mirror of +0x05
            _stamped = true;
            ModLogger.Event(LogVerb.Trace,
                $"body-double: CANARY 4 stamps applied from donor 0x{donor:X}: identity {id:X2} {id2:X2} (the double now doubles that unit's name and control), faction {t5:X2} {t6:X2} mirror {tEE:X2}, real-unit marker 01.");
        }
        else
        {
            ModLogger.Event(LogVerb.Trace,
                "body-double: no drawn player donor besides the host; completeness stamps SKIPPED. The double stays a pure decoy; do NOT release the hold (an unassigned controller id crashes on turn arrival).");
        }

        // Canary 3a. Zero CT before unpause as the safe default, then arm the per-tick hold, while the menu
        // still pauses the game.
        _mem.WriteBytes(_hostCombat + CtOffset, ZeroCt);
        _bound = true;
        _boundThisBattle = true;   // the re-arm witness. Only a bind made this battle may re-arm
        _decoyHold = true;

        // Canary 8 flourish. World X/Y from 28*tile+14, Z parked in the sky; the tick loop lowers it.
        // Transform offsets are list-node relative. List node = pool element + 8 (element+0 = in-use dword).
        long listNode = node + 8;
        byte dtx = _mem.U8(_hostCombat + 0x4F);
        byte dty = _mem.U8(_hostCombat + 0x50);
        _mem.WriteBytes(listNode + WorldXOff, BitConverter.GetBytes((ushort)(28 * dtx + 14)));
        _mem.WriteBytes(listNode + WorldYOff, BitConverter.GetBytes((ushort)(28 * dty + 14)));
        short landing = -48;
        if (_donorCombat != 0)
        {
            byte dModel = _mem.U8(_donorCombat + 0x1BC);
            int dIdx = dModel < Slots ? (int)_mem.U32(SceneNodeIdx + dModel * 4) : -1;
            if (dIdx >= 0)
                landing = unchecked((short)_mem.U16(NodePool + (long)dIdx * NodeSize + 8 + WorldZOff));
        }
        _mem.WriteBytes(listNode + WorldZOff, BitConverter.GetBytes(unchecked((ushort)SkyZ)));
        _descendNode = listNode;
        _descendTarget = landing;
        ModLogger.Event(LogVerb.Trace,
            $"body-double: decoy CT-hold engaged; clone parked in the heavens over ({dtx},{dty}), descending to Z {landing}. Unpause and look up. F5 again releases the hold for control.");

        // Canary 3b. DBG dump of the sibling's node next to the new one. The offline diff names the fields the
        // skipped node+0x270 sub-block (suspected VRAM/palette slot math) leaves different.
        DumpForensics(entry, node);
    }

    /// <summary>Read-only post-bind forensics. Per-slot summary, hex dumps of the sibling's and the new node,
    /// and combat snapshots of both. Lines go to the DBG log and to a file in the save dir, because Canary 3
    /// dumps in the rotating log were lost to two post-crash relaunches.</summary>
    private void DumpForensics(long sibEntry, long frankNode)
    {
        var lines = new List<string>(260);
        void Emit(string line)
        {
            ModLogger.Debug(LogVerb.Trace, line);
            lines.Add(line);
        }
        foreach (int slot in SummarySlots)
        {
            long combat = UnitsBase + slot * 0x200L;
            byte job = _mem.U8(combat + 0x03);
            byte gate = _mem.U8(combat + 0x01);
            byte present = _mem.U8(combat + 0x1B5);
            byte model = _mem.U8(combat + 0x1BC);
            // Both tables hold Slots entries (SceneNodeIdx, SecondTable = 0x54 = 21*4); a vacant slot's model 0xFF would read past them.
            string si = "n/a", nodeIdxS = "n/a", n11 = "n/a";
            if (model < Slots)
            {
                si = _mem.U8(SecondTable + model * 4).ToString("X2");
                int nodeIdx = (int)_mem.U32(SceneNodeIdx + model * 4);
                nodeIdxS = nodeIdx.ToString();
                if (nodeIdx >= 0) n11 = _mem.U8(NodePool + (long)nodeIdx * NodeSize + 0x11).ToString("X2");
            }
            Emit($"body-double: slot {slot}: job {job:X2} gate {gate:X2} present {present:X2} model {model} si {si} nodeIdx {nodeIdxS} node+0x11 {n11}");
        }
        ulong sibCombat = _mem.U64(sibEntry + 0x10);
        byte sibModel = _mem.U8((long)sibCombat + 0x1BC);
        int sibIdx = sibModel < Slots ? (int)_mem.U32(SceneNodeIdx + sibModel * 4) : -1;
        if (sibIdx >= 0) DumpBytes(Emit, "dump[sibling]", NodePool + (long)sibIdx * NodeSize, NodeSize);
        else Emit($"body-double: sibling model {sibModel} has no node index; nothing to dump.");
        DumpBytes(Emit, "dump[frank]", frankNode, NodeSize);
        // Combat snapshots for the AI-visibility question (the AI idled with Kerrich enrolled). The "combat"
        // label keeps node_dump_diff.py's dump[...] parser blind to them; spawn_probe banddiff reads the same structs.
        if (_hostCombat != 0) DumpBytes(Emit, "combat[double]", _hostCombat, 0x200);
        if (_donorCombat != 0) DumpBytes(Emit, "combat[donor]", _donorCombat, 0x200);
        DumpBytes(Emit, "combat[sibling]", (long)sibCombat, 0x200);
        PersistForensics(lines);
    }

    private void DumpBytes(Action<string> emit, string label, long addr, int size)
    {
        if (!_mem.TryReadBytes(addr, size, out var buf))
        {
            emit($"body-double: {label} 0x{addr:X} unreadable; no forensics.");
            return;
        }
        emit($"body-double: {label} 0x{addr:X} ({size} bytes):");
        var sb = new StringBuilder(64);
        for (int off = 0; off < size; off += 16)
        {
            sb.Clear();
            for (int i = 0; i < 16 && off + i < size; i++)   // 0x548 is not 16-aligned; its last row is 8 bytes
            {
                sb.Append(buf[off + i].ToString("X2"));
                sb.Append(i == 7 ? "  " : " ");
            }
            emit($"body-double: {label} +0x{off:X3}: {sb}");
        }
    }

    /// <summary>Persist the forensic lines to the save dir (Reloaded\User\Mods), which survives deploys and
    /// log rotation. Never throws into the tick.</summary>
    private void PersistForensics(List<string> lines)
    {
        string path = "";
        try
        {
            path = Path.Combine(_saveDir, $"bodydouble_forensics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(path, lines);
            ModLogger.Event(LogVerb.Trace, $"body-double: forensics persisted to {path} (rotation-proof; parse with tools/probes/node_dump_diff.py).");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"body-double: could not persist forensics to '{path}': {ex.Message}. The DBG lines in livingweapon.log are the only copy this session.");
        }
    }
}
#endif
