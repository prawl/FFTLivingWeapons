#if LWDEV
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (LW-58, Canary 4 build): cold-call the engine's own RENDER-NODE BUILDER to REVEAL an
/// inserted-but-hidden ENTD unit ("Frank", battle 435 combat slot 7), bind the node as his own, and hold
/// him as a turnless decoy. Every DATA lever is proven walled:
/// the override nxd cannot author AlwaysPresent (Present column masked to 0xC), the facing/elevation gate
/// is dormant, and sprite/job is NOT the cause (Frank rebuilt as a Monk, a LOADED sheet, still stays
/// hidden). The confirmed state: Frank is a vanilla HIDDEN-RESERVE unit (constructed and placed, but
/// +0x01 = 0xFF do-not-draw, +0x1B5 = 0, sceneNodeIdx[model] = -1 = no render node). The vanilla reveal
/// (FFHacktics Event 0x47 AddGhostUnit) registers the unit's sprite so the engine builds its node. This
/// spike drives the equivalent directly: cold-call node builder 0x14026EBEC on PATH A (allocate), cribbing
/// a DRAWN Monk sibling's scene-load entry (same LOADED sheet, so the builder's sprite-id lookup
/// 0x14E90A5CB resolves instead of self-aborting and freeing the node) but with arg8 = Frank's combat
/// pointer and arg7 = a fresh label, so a NEW node binds to Frank and sceneNodeIdx[7] is stamped.
///
/// CANARY 1 PROVED (live 2026-07-10): the build alone drew a Monk sprite for Frank (render weld broken).
/// But it was a SHALLOW CLONE: it rode the cribbed sibling's identity/animation, so it drew at the
/// sibling's tile, was not a listed unit, and when the real Monks animated they went black while the clone
/// froze mid-walk (shared animation resources). CANARY 2 (live-proven 2026-07-10) added the scene-bind body (the exact
/// per-slot writes 0x1401D517D..0x1401D51B6, verified against the disasm) right after the build, so the new
/// node becomes FRANK: node+0x150 = Frank's combat ptr (unit link, drives position + hover), combat+0x01 =
/// slot idx (clears the 0xFF hide gate, so he becomes a real listed unit with his own logic per the owner
/// hide-test), node+0x11 = the sprite-resource index, node+0x230/0x238 = his OWN animation objects (two
/// bounded 1-arg cold-calls 0x14E8CBF78/0x14E8F2CA0; this decouples him from the sibling and kills the
/// black-Monk conflict), and combat+0x1B5 = 1. The one scene-bind step SKIPPED is the node+0x270 sub-block
/// (0x1401D51BD..0x1401D5224): it consumes edx returned alongside rax from 0x14E8F2CA0 (C# cannot capture a
/// second return register) and is the workflow's flagged AV-prone table math; the builder already left
/// node+0x270 workable (Canary 1 drew with it), so leaving it is safer than reconstructing it blind. This
/// is the reverse of despawn 0x14023BFB0 (the proven per-unit REMOVE), which the arc showed the engine has
/// without a reveal twin.
///
/// WITNESSED (Canary 2, owner 2026-07-10): Kerrich stands as a real, named, listed party-guest Monk. Two
/// faults remained at the time: the turn the scheduler grants him never completes (the whole queue stalls
/// behind him), and the real Monks blacked out when animating. Both are since resolved or downgraded: the
/// turn fault fell to the Canary 4 controller-id stamp, and the black-out shrank to a reddish tint whose
/// suspect is a garbage +0x05 faction byte (fixed by the same Canary 4 stamps).
///
/// CANARY 3 (live-run 2026-07-10): two additions on top of the Canary 2 bind, which proved Kerrich real
/// but left two "incomplete unit" faults. (a) THE DECOY CT-HOLD: clearing the hide gate enrolled him in the CT
/// scheduler, and with no action/AI state his turn never completes, so the whole queue stalls behind him.
/// The scheduler grants a turn at CT >= 100 (CT = combat +0x41, the band +0x25 slam byte, Offsets.ACtSlam,
/// that ExtraTurn writes 100 to GRANT turns, Zwill live-proven). This build zeroes it at bind and holds it
/// at 0 every tick, so his turn NEVER ARRIVES and the queue flows around him. Not a compromise: a Body
/// Double is a decoy that stands, draws aggro, and is targetable, which needs no turns. After the bind, F5
/// toggles the hold (the old release A/B is ANSWERED: release now means taking control of a stamped
/// Kerrich, see the Canary 4 paragraph; unstamped release is refused).
/// GATING (the 1.5 lesson): raw inLive dips false on every mode-1 frame (move-browsing, enemy phases),
/// which is exactly when CT clockticks accrue, so the pin writes on EVERY reached tick and its teardown is
/// the DEBOUNCED battle edge only (Engine.ResetBattleState calls ResetBattle). If the bind stands but the
/// hold is lost, F5 re-arms it (the recovery path in Fire).
/// (b) NODE FORENSICS for the black-Monk fault: after the bind, dump the cribbed sibling's node and
/// Frank's fresh node (0x548 bytes each, file-only DBG lines) plus a per-slot summary. The offline diff
/// names the per-instance fields the skipped node+0x270 sub-block (the suspected VRAM/palette slot math)
/// leaves different: that list is the next canary's fix, captured for free in this live session.
///
/// CANARY 4 (this build; every stamp owner-proven by live pokes 2026-07-10): after the bind, three stamp
/// groups turn the decoy into a COMPLETE unit, values cribbed from a live PLAYER donor (combat slots
/// 16..20, drawn, non-zero job; the earlier +0x02 gate was dynamic turn state, not identity). (a)
/// +0x1BE = 01, the real-unit marker (writer 0x140456444, a separate init phase the hidden-reserve path
/// skips). (b) +0x191/+0x192 = the donor's ROSTER-IDENTITY backref: FE = unassigned, which is why
/// turn-arrival crashed at 0x14018D102 (null controller singleton, factory object id 0x151); with a
/// player unit's pair Kerrich took real turns, moved, attacked, and DIED CLEANLY into a treasure chest
/// (the full lifecycle, zero crashes). The pair also routes NAME resolution: copying Ramza's pair
/// renamed Kerrich to "Ramza" on the field, so the double literally doubles the donor. (c)
/// +0x05/+0x06/+0x1EE = the donor's faction/tint pair plus its composed mirror (garbage in +0x05 tints
/// the shared Monk sheet reddish). The decoy CT-hold still engages at bind as the safe default; F5
/// releases it and his turns go LIVE (refused if the stamps were skipped).
/// THE ONE WALL, DECODED TO A CLASS (owner live + CE + static disasm 2026-07-10): the enemy AI idles
/// while a live walk-visible Kerrich is enrolled (hide gate +0x01=FF or death both un-stick it). Ruled
/// out live: count corruption (Canary 5's refresh, FALSIFIED and DESTRUCTIVE, see the constants block)
/// and the duplicate identity (poked off Ramza's 03 1B, still frozen). Traced with CE (execution BPs
/// work on this game; data watchpoints do not): the AI pipeline RUNS every frozen turn (dispatch
/// 0x140213C7A) but no-ops: subject-select 0x14020F660 is entered, hits its cache-hit fast path
/// (0x14020F684 je 0x14020FABB, latch [0x140C6B307]==0), and jumps to its epilogue 0x14020FB75 (ret)
/// having selected nothing. The count-load loop 0x14020F769 never runs (why the count-byte watch was
/// empty). The latch is subject-select's own "AI decision pending" flag, armed at 0x1402D3EC3; stuck 0
/// = "nothing to decide" = everyone Waits. Deeper dominoes exist; STOP tracing (a multi-layer rabbit
/// hole). CONCLUSION: an un-enrolled walk-visible unit makes the AI arm conclude no decision is
/// pending. THE FIX = enroll Kerrich as a real AI unit (Canary 6, the composed enroll; SHIFT+F5). See
/// the constants block for the verified enroll primitives and FireEnroll for the gates.
///
/// CANARY 7 (this build; the PLAYER-SLOT PIVOT, the lead fix from the 2026-07-10 freeze verdict): the
/// freeze is CONTENT-BLIND: every byte lever poked live (count, identity, agency, the 6a/6b enroll)
/// left the enemy AI frozen, and the only un-stick is removing the double from the walk-visible set
/// (hide gate FF, or the death CONVERSION: a KO'd corpse still freezes, owner-corrected 2026-07-10).
/// An extra walk-visible unit in the ENEMY slot region (slot 7, just past the 7 deployed enemies 0-6)
/// is what the enemy-AI phase chokes on. So Canary 7 RELOCATES the double: copy the hidden slot-7
/// template's combat struct into a VACANT PLAYER slot (16..20; vacant = gate FF + present FF, the
/// deploy loop 0x140270D5C's own empty stamp), fix +0x1BC to the host's OWN model id, and run the
/// same node-build + scene-bind against the HOST. Static recon (workflow wf_5c071993-d4b 2026-07-10)
/// cleared the path: the builder derives its stamp index from arg8's +0x1BC with an INCLUSIVE
/// cmp al,0x14 bound (0x14026EDBD) and writes SecondTable[model] itself (0x14026EDC3) before the
/// SceneNodeIdx stamp (0x14026EDEA); the scene-bind body is a flat 21-slot loop with zero region or
/// team logic (bound cmp r11d,0x15 at 0x1401D523B) that hard-assumes model == slot (the +0x1BC fixup
/// is load-bearing); and the ENTD/data lever is definitively WALLED (no team column; Present
/// hard-masked to 0xC engine-side per OverrideEntryData.layout; the UnitId hi-bit proxy was
/// live-falsified 2026-06-21 in the FFTMultiplayer team spike). THE TEST: if the enemy-AI phase is
/// region-scoped, a player-region double leaves it alive; if it STILL freezes, the capacity is
/// whole-array and the double is likely walled (scope decision).
/// DEATH-WATCH (new with Canary 7): the double's REMOVAL (crystal pop) GAME-OVERED battle 435 twice
/// on 2026-07-10: once carrying Ramza's identity pair and once carrying a generic's (live re-point,
/// write-verified), so the defeat check is NOT identity-keyed and the true cause is unknown
/// (suspects: uncounted-unit removal bookkeeping, battle-specific defeat conditions). Mitigation:
/// once bound, if the double's composed Dead bit (+0x61 bit 0x20) sets, pin his crystal counter
/// (+0x07) at 3 every tick (the proven Sanctuary counter-pin) so the removal edge never fires.
/// KO'd is survivable; REMOVED is the game-over.
///
/// CANARY 8, THE DUPLICATOR (the final canary; owner brief 2026-07-10 late: "simulate adding in a
/// new unit... can we literally just duplicate a unit and materialize them like we did the
/// knight?"): F5 now DUPLICATES THE HOVERED UNIT. No pre-hidden ENTD units, no per-map data;
/// Frank the template is RETIRED. The donor being alive on the field guarantees every input the
/// old approach struggled for: a loaded sprite sheet, a cribbable scene-load entry (the donor's
/// OWN), a cloneable battle-keyed registry object, and live identity stamps. The flow composes
/// the night's proven pieces, in the order the resurrection proved is load-bearing:
///   (1) copy the donor's 0x200 combat struct into a vacant same-region slot (+0x1BC = host slot,
///       gate/membership FF, CT 0, logic tile = a FREE tile adjacent to the donor: the
///       tile-collision lesson);
///   (2) REGISTRY ENROLL while still hidden (clone the donor's battle-keyed object, re-key +0x2C
///       to the host slot, append at table[count], bump the count word then byte): the clone is
///       AI-resolvable BEFORE it is ever walk-visible, which is THE freeze cure (live-proven by
///       the resurrection);
///   (3) cold-call the node builder with a1/a2/a3 = THE DESTINATION TILE (they land in the node's
///       tile key +0x88/89/8A, the AI's tile-lookup: the bug that froze Canary 7's binds was
///       cribbing the SIBLING's tile here) and arg8 = the host;
///   (4) the stage-2 bind + identity stamps FROM THE DONOR (+0x191/2 pair, faction trio, real-unit
///       marker: the dup literally doubles the donor, name and control);
///   (5) the flourish: node world X/Y set by formula (28*tile+14), Z parked at -600 and stepped
///       +12 per tick to the donor's own altitude: THE CLONE DESCENDS FROM THE HEAVENS.
/// The decoy CT-hold still engages at bind as the safe default; F5 again releases it.
///
/// SAFETY (crash-capable: an internal engine AV is uncatchable, so prevention is the only defense):
///  - TargetReady requires Mem.Readable AND a prologue-byte landmark on 0x14026EBEC (a stale or patched
///    address becomes a logged refusal, not a crash). The builder SELF-ABORTS and frees the node if arg5
///    (sprite-id) does not resolve to a loaded handle; Frank-as-Monk shares slots 4/5's loaded sheet, so
///    it resolves.
///  - Fire ONLY while PAUSED (PauseFlag == 1, a unit menu open) to narrow the reentrancy window: the
///    builder front-inserts into the render list [0x140D3A410] the renderer thread traverses, and pausing
///    idles the pipeline. A residual cross-thread window remains (it cannot be eliminated from the loop
///    thread); accept it for a single key-triggered call. Effects are observed after unpause.
///  - Only during a genuine inLive frame, foreground window only (the flight-recorder alt-tab doctrine),
///    writes-enabled (fingerprint guard armed). g_mode (0x142FF9E2C) must read 0 (PATH A allocate) and the
///    render-mgr singleton (*0x140CE3250) must be non-null, else refuse.
///  - Validate slot 7 is Frank-the-Monk (+0x03 == 0x4E) AND hidden (+0x01 == 0xFF) before firing, so a
///    wrong battle or the Black-Mage build is a refusal, not a call against a live unit. The one
///    carve-out is the re-arm path, which accepts a non-0xFF gate ONLY behind the _boundThisBattle
///    witness (a bind THIS spike made since the last battle edge), so a wrong-battle F5 still refuses.
///
/// USAGE (Canary 8 flow): ANY battle, THROWAWAY SAVE ONLY. Open the menu of the unit to DUPLICATE
/// (the hover is the donor pick), press F5 once (copy + registry enroll + node build + bind +
/// donor stamps + CT hold + the descent arm), then unpause and LOOK UP: the clone descends from
/// the heavens onto a free tile beside the donor. WATCH THE ENEMY AI (acting normally = the
/// composed spawn beats the freeze end-to-end). F5 again releases the hold for control; Ctrl+F5
/// despawns (hover or ghost-orphan resolve); Shift+F5 remains the 6a/6b enroll instruments. AI-FREEZE FIX (Canary 6, the composed enroll; SHIFT+F5 is a
/// STAGED key): press 1 = 6a (cold-call the AI-data enroller + write his membership id to +0x02;
/// LIVE 2026-07-10: worked mechanically, row filled, did NOT un-freeze alone); press 2 = 6b (build
/// his AI OBJECT via the engine's own scratch-fill + populator, append it at registry[count], bump
/// both counts by 1); press 3 = refused (fully enrolled). Each press logs its verdict; unpause and
/// watch the enemy AI after each. Run spawn_probe aicensus (external, read-only) before/after for
/// the count/registry picture.
/// </summary>
internal sealed class BodyDoubleSpike
{
    private const long FnNodeBuild  = 0x14026EBEC;   // render-node builder (10-arg Win64; PATH A allocates + stamps)
    private const long SceneTable   = 0x141856728;   // scene-load entry table, stride 0x20 (idx 0..6 populated)
    private const long UnitsBase    = 0x141853CE0;   // BattleUnitsBase, stride 0x200
    private const long SceneNodeIdx = 0x140822DAC;   // int32[] indexed by combat +0x1BC model id (-1 = no node)
    private const long GModeAddr    = 0x142FF9E2C;   // 0 = PATH A (allocate + stamp); 1 = PATH B (reuse)
    private const long RenderMgrPtr = 0x140CE3250;   // arg10 = *this (render-mgr singleton)

    // Stage-2 bind (scene-bind body 0x1401D517D..): turn the freshly built node into Frank's own unit.
    private const long NodePool     = 0x140D30030;   // render-node pool base; node N = pool + sceneNodeIdx*0x548
    private const long SecondTable  = 0x140822D58;   // per-slot sprite-resource index (si = byte[+slot*4])
    private const long AnimBase     = 0x140D005A0;   // animByte = byte[AnimBase + si*0x32D6 + 1]
    private const long FnAnimA      = 0x14E8CBF78;   // 1-arg (ecx=animByte), bounded lookup -> node+0x230
    private const long FnAnimB      = 0x14E8F2CA0;   // 1-arg (ecx=animByte), bounded lookup -> node+0x238

    // CANARY 6 (the composed enroll; the AI-freeze fix, disasm-verified 2026-07-10 after Canary 5
    // was FALSIFIED live). THE FREEZE: a walk-visible but UN-ENROLLED Kerrich makes the AI decision
    // arm (subject-select 0x14020F660) conclude "no decision pending" and no-op, so every unit
    // (enemies AND auto-battle Ramza) Waits. Ruled out live: count corruption (the isolation test
    // read a healthy count 7, still frozen) and the duplicate identity (poked off Ramza's 03 1B,
    // still frozen). CANARY 5's registry refresh 0x140284FE4 is DEAD: it is the DESTRUCTIVE bulk
    // rebuild (it set the count to a nonsense 46 + ~38 phantom objects and never enrolled him);
    // NEVER cold-call it.
    //
    // STEP 1 (this canary): cold-call the per-slot AI-DATA allocator/filler 0x140274F30(rcx=combat
    // slot). Verified from its disasm: it reads the slot's existing id at +0x02 (0xFF = Kerrich,
    // unassigned) -> takes the ALLOCATE branch -> free-finder 0x140275248 -> fills the AI-data entry
    // 0x1411A7D10 + id*0x258 from the slot, stamps entry+1 = id, returns the id in eax (or -1). It
    // does NOT write combat+0x02 back. STEP 2: write the returned id -> Kerrich +0x02 (the
    // membership id the AI layer maps a slot by). This canary deliberately does NOT touch the count
    // byte 0x140D407BB or build an AI object (that is Canary 6b, only if the AI-data entry alone
    // does not un-freeze; an object's +0x2C match word is a copy of +0x02, built by 0x140284A80 via
    // the single-object paths 0x1402BDA00 / 0x140285A9A, never the bulk 0x140284FE4).
    private const long FnEnroll     = 0x140274F30;   // per-slot AI-data allocator/filler; rcx=combat slot, returns id in eax (-1 fail)
    private const long AiCountByte  = 0x140D407BB;   // AI-object count byte (the walk bound; 6b bumps it by exactly 1)
    private const long AiRegistry   = 0x141800F50;   // 56 qword slots -> per-unit AI objects (6b appends ONE entry)
    private const int  AiRegistrySlots = 0x38;
    private const int  AiObjMatchOff   = 0x2C;       // word an AI object matches vs combat +0x02

    // CANARY 6b (the object build; the engine's own single-object sequence, lifted verbatim from
    // 0x140285A96..0x140285AB3: lea rcx,[scratch]; mov edx,rowId; call 0x140275CE8 (fill scratch
    // from AI-data row; first insn stores r8d to the mode global 0x1437414A0, so we pass that
    // global's CURRENT value back to make the store a no-op; returns -1 on a bounds/vacant-row
    // fail), then rcx=scratch, rdx=objPtr, r8=rowPtr; call 0x140284A80 (the per-object populator:
    // entry disasm confirms rdx->rbx=object, r8->rdi=row, rcx->rsi=scratch, and it MEMSETS the
    // object to 0x280 itself before filling, so a fresh pool slot is safe; +0x2C gets the row id
    // at 0x140284B8E). 6b then appends objPtr at registry[count], bumps the builder's own count
    // word 0x1437414A8 and the cached count byte by exactly 1 (the walk window [0..count) now
    // includes the new object). The id-map 0x143741530 write is deliberately SKIPPED in v1 (its
    // key/value semantics are murky in the disasm and the subject-select walk matches obj +0x2C
    // directly; add it in 6c only if 6b alone does not un-freeze).
    // CANARY 9 (the coexistence fix; LIVE-PROVEN 2026-07-10 on the slot-19 clone: ONE byte turned
    // a drawing-but-crashing clone into a real AI fighter). The AI-turn orchestrator's early gate
    // (0x151086AE8 `cmp byte [0x141873038+slot], 0x10`) reads a per-slot AI-ROSTER INDEX table that
    // the registry/node/combat enroll never touched: real units hold sequential indices 0x00..0x07,
    // an un-enrolled clone holds 0xFF, so the arm path skips it, the AI-subject pointer [0x141872EA0]
    // never arms, and the facing code 0x150E74A5D derefs the null = the auto-battle crash. Writing
    // the host slot the next free index (count of already-enrolled slots) closes it: the clone was
    // then evaluated cleanly and took a real turn (membership +0x02 and node id 0 did NOT matter;
    // the roster index alone was the fix). Data-only, no cold call.
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

    // CANARY 8 descent flourish: park the fresh node's world Z in the heavens and step it to the
    // donor's altitude on the tick loop (transform ownership: idle nodes are unowned, so the step
    // animation is uncontested; world X/Y = 28*tile + 14, the live-proven formulas).
    private const int WorldXOff     = 0x4C;           // node u16 world X
    private const int WorldZOff     = 0x4E;           // node u16 (signed) world Z; -12*height, Float adds one unit
    private const int WorldYOff     = 0x50;           // node u16 world Y
    private const short SkyZ        = -600;
    private const int DescentStep   = 12;             // one height unit per ~33ms tick

    // Canary 3a (decoy hold): CT = combat +0x41 (band +0x25, Offsets.ACtSlam, the byte ExtraTurn slams
    // to 100 to GRANT a turn, Zwill live-proven). Pinned to 0 so Frank's turn never arrives. WRITE-only:
    // its reads are documented unreliable (rod-pass CT trap), so the hold never reads it back.
    private const int CtOffset      = 0x41;
    private const int NodeSize      = 0x548;          // render-node stride (pool 0x140D30030)
    private static readonly int[] SummarySlots = { 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20 };
    private static readonly byte[] ZeroCt = { 0 };

    // CANARY 7 (player-slot pivot) constants. Host vacancy = the deploy loop's own empty stamp
    // (gate +0x01 = FF at 0x140270FC5 AND present +0x1B5 = FF at 0x140270FBE, workflow decode
    // 2026-07-10); a slot failing either is occupied or mid-lifecycle, never a host. Death-watch:
    // composed status Dead = combat +0x61 bit 0x20; crystal counter = combat +0x07, pinned at 3
    // (the proven Sanctuary mechanism) so the removal edge that game-overed 435 never fires.
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

    // Both anim lookups open identically (disasm 2026-07-10): movzx eax,cx; lea rcx,[rip+table]. Landmark
    // enough (with the fixed address + Mem.Readable) to reject a patched or wrong routine before calling.
    private static readonly byte[] AnimPrologue = { 0x0F, 0xB7, 0xC1, 0x48, 0x8D, 0x0D };

    // Raw exe bytes (ic_disasm 2026-07-10). FnEnroll opens mov [rsp+0x10],rbx; mov [rsp+0x18],rbp;
    // push rsi/rdi/r12/r14. Same refusal discipline as the node builder's Prologue.
    private static readonly byte[] EnrollPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56 };

    // 6b landmarks (ic_disasm raw dumps 2026-07-10): FnScratchFill is a fixed .xcode thunk
    // (jmp rel32 to 0x14EF1F570); FnObjPopulate opens mov rax,rsp; mov [rax+8],rbx;
    // mov [rax+0x10],rbp; mov [rax+0x18],rsi.
    private static readonly byte[] ScratchFillThunk =
        { 0xE9, 0x83, 0x98, 0xCA, 0x0E };
    private static readonly byte[] ObjPopulatePrologue =
        { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48 };

    // CTRL+F5 = the CLEAN DESPAWN instrument (owner request 2026-07-10; contract decoded same day,
    // agent a0239384, byte-cited). The ledger's "cold-call 0x14023BFB0(ecx=slot)" was WRONG twice
    // over: that address is mid-instruction inside the EVENT INTERPRETER's opcode-0x3D wait loop
    // (fn 0x14023A8E0, a coroutine: never cold-callable). The real mechanism is DECLARATIVE and
    // DATA-ONLY (Option A): write mode 2 into the render node's +0x12C flag word
    // ((old & ~0x30) | 0x20 = "remove me") and the engine's own per-frame node sweeper 0x14026E20C
    // performs the whole removal itself on its own frame: combat+0x01=0xFF (0x14026E2D3),
    // combat+0x1B5=0x80 (0x14026E2DB), node+0x12C |= 0x30 done-mark (0x14026E2B3), then the leaf
    // teardown 0x14026E16C (element in-use dword cleared, conditional model-loaded byte cleared,
    // node UNLINKED from [0x140D3A410]). Same primitive vanilla uses for crystal/chest conversion
    // (callers 0x1401F0A47/0x1401F25A8) and scripted event removal. ONE guarded byte write, no
    // cold call, no thread race. The sweeper is pump-gated on NOT-paused ([0x140C6B1C8] != 1), so
    // the write lands while paused and the removal completes on unpause; Tick watches for the
    // done-mark and logs (timeout restores the saved +0x12C). Target selection: the hovered unit
    // (condensed cursor fingerprint, any team), else the unique ORPHAN (gate FF but node still
    // back-linked via +0x148: a ghost-statue sprite). sceneNodeIdx goes STALE by design (vanilla
    // removals leave it; the next bind's dedup sweep 0x14026EDD2 scrubs it).
    private const long NodeListHead   = 0x140D3A410;  // render-node singly-linked list head
    private const int  NodeIdOff      = 0x08;         // node id byte (list lookups match this)
    private const int  NodeCombatOff  = 0x148;        // node -> combat back-pointer (builder-written)
    private const int  NodeModeOff    = 0x12C;        // flag word; bits 0x30 = removal mode/done
    private const byte NodeModeRemove = 0x20;         // mode 2 = "remove me" (sweeper consumes)
    // (The per-id byte table 0x140C6CFE0 stride 9 is the "engine engaged with this unit" marker,
    // hover/menu-set; it is NOT a busy gate and is deliberately not checked: see FireDespawn.)
    private const long SpecialNodeId  = 0x140CF873C;  // dword: the CURRENT ACTOR's node id; never remove it
    private const int  NodeWalkMax    = 64;           // list-walk bound (spawn_probe precedent)
    // Sweeper mode-dispatch landmark at 0x14026E28B (verifies the +0x12C semantics still hold
    // before any write; raw exe bytes read live 2026-07-10).
    private const long SweeperDispatch = 0x14026E28B;
    private static readonly byte[] SweeperDispatchBytes =
        { 0x8B, 0xCA, 0xC1, 0xE9, 0x04, 0x83, 0xE1, 0x03, 0x83, 0xE9, 0x01, 0x74, 0x5C, 0x83, 0xF9, 0x01 };
    private const int TurnFlagOff = 0x1B8;            // PSX turn flag (frame +0x1B8): nonzero = turn open
    private const int DespawnTimeoutTicks = 300;      // ~10s at 33ms: sweeper never fired -> revert

    // Win64 (Winapi on x64): args 1-4 in rcx/rdx/r8/r9, args 5-10 on the stack at [rsp+0x20..0x48]. All
    // widths <= 8 bytes take a full slot; the callee reads cl/dl/r8b/r9w and the word/dword stack fields.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long NodeBuildFn(long a1, long a2, long a3, long a4, long a5,
                                      long a6, long a7, long a8, long a9, long a10);

    // Anim object fetch: ecx = animByte, returns rax (the object). (0x14E8F2CA0 also returns edx, which the
    // node+0x270 sub-block would consume; C# cannot capture it, so that sub-block is skipped, see the doc.)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long AnimFn(int animByte);

    // The Canary 6 enroller: rcx = combat slot ptr, edx ignored for Kerrich (spriteset 0x80 > 0x7f
    // skips the edx branch at 0x140274F58). Returns the allocated AI-data id in eax (-1 on failure).
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnrollFn(long rcx, int edx);

    // The 6b pair: scratch fill (returns -1 on bounds/vacant-row fail) and the object populator
    // (fills the 0x280 object it first memsets; no meaningful return).
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ScratchFillFn(long scratch, int rowId, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ObjPopulateFn(long scratch, long obj, long row);


    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private const int VkF5 = 0x74;   // reveal + hold toggle. F5, NOT F6: three WIRED spikes (Flavor/Header/AttackCard) already poll F6, so one press would fire them all. ShowSpike claims F5/F8 but is not wired in Engine.
    private const int VkShift = 0x10; // Canary 5 AI-registry refresh rides SHIFT+F5, NOT a fresh F-key: ShowSpike.cs's key doc records F3/F7/F9 as LL-hook EATEN on this box and F8 as stopped-registering, and the only proven working set is F2/F4/F5/F6 (all claimed by wired spikes). Reusing this spike's own proven F5 with a Shift gate avoids gambling on an untested key.
    private const int VkCtrl = 0x11;  // CTRL+F5 = the clean despawn; same scarce-key reasoning as Shift.

    private readonly IGameMemory _mem;
    private readonly string _saveDir;   // update-safe (Reloaded\User\Mods): survives deploys AND log rotation
    private bool _f5Was;
    private int _hbTick;
    private bool _announced;
    private bool _bound;            // stage-2 bind completed this battle; F5 now toggles the decoy hold
    private bool _decoyHold;        // per-tick CT pin active (Frank's turn never arrives)
    private bool _stamped;          // Canary 4 stamps landed this battle; hold release is refused without them
    private bool _boundThisBattle;  // witness: BindStage2 ran since the last battle edge. The re-arm
                                    // path REQUIRES it: gate==model + a stamped node also describes any
                                    // legitimately drawn unit in a battle this spike never touched,
                                    // and a mispressed F5 must not pin a real unit's CT there.
    private long _hostCombat;       // Canary 7: the claimed vacant player slot's combat base (0 = none yet)
    private byte _hostModel;        // the host's own slot index == its model id (+0x1BC fixup, +0x01 at bind)
    private bool _deathPinned;      // death-watch latch: the crystal-counter pin engaged (one loud log)
    private long _despawnNode;      // Ctrl+F5 pending removal: the mode-2-marked node (0 = none)
    private long _despawnCombat;    // its combat base (completion check + host-state cleanup)
    private byte _despawnOldMode;   // the +0x12C byte before the mark (restored on sweeper timeout)
    private int _despawnTicks;      // ticks since the mark (timeout counter)
    private long _donorCombat;      // Canary 8: the duplicated unit (identity stamps + registry clone source)
    private long _descendNode;      // descent flourish: the node being lowered (0 = none)
    private short _descendTarget;   // the donor's altitude the descent lands at
    private int _holdHb;

    public BodyDoubleSpike(IGameMemory mem, string saveDir)
    {
        _mem = mem;
        _saveDir = saveDir;
    }

    /// <summary>In-battle loop tick: heartbeat, the F5 edge (reveal before the bind, hold-toggle after),
    /// and the per-tick decoy CT pin. The cold call runs HERE on the loop thread (the ShowSpike /
    /// StatusSpike precedent that a loop-thread cold call draws), gated to a paused frame.
    /// <paramref name="inLive"/> is the caller's genuine-live predicate.</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Event(LogVerb.Trace, "body-double: armed (dev, CANARY 8 THE DUPLICATOR). Any battle, THROWAWAY SAVE. Open the menu of the unit to duplicate (hover = donor), then F5: copy + registry enroll + node build + bind, and the clone descends from the heavens beside the donor. Ctrl+F5 despawns. Watch the enemy AI.");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"body-double: alive (writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (Pressed(VkF5, ref _f5Was))
        {
            // CTRL+F5 = the clean despawn (checked FIRST so Ctrl+Shift never falls into the enroll);
            // SHIFT+F5 = CANARY 6, the composed enroll (the AI-freeze fix). It replaced the DEAD
            // Canary 5 refresh (0x140284FE4, the DESTRUCTIVE bulk rebuild that set count 8->46 +
            // phantoms and did NOT beat the freeze; the freeze is not count corruption). Plain F5
            // keeps the reveal / hold-toggle behavior.
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
        // The hold deliberately does NOT gate on raw inLive: this call site is only reached while the
        // DEBOUNCED battle is in, and on 1.5 raw inLive dips false on every mode-1 frame (move-browsing,
        // enemy phases), which is EXACTLY when CT clockticks accrue. Gating the pin on inLive would leave
        // the whole accrual phase uncovered (a contiguous enemy stretch reaches 100 unwritten) and a
        // single dip must never read as "battle left": teardown is ResetBattle (the debounced edges,
        // Engine.ResetBattleState), never a raw frame. The write target is the host's fixed static slot,
        // so the broader gate adds no wrong-address risk (the CharmLock/TreasureMaster pre-gate lesson).
        if (_decoyHold) HoldDecoy();
        if (_boundThisBattle) DeathWatch();
        if (_despawnNode != 0) DespawnWatch();
        if (_descendNode != 0) DescendStep();
    }

    /// <summary>The heavens-descent flourish: step the fresh clone's world Z toward the donor's
    /// altitude, one height unit per tick (idle transforms are unowned, so nothing contests the
    /// animation; the clone's first real move or turn-open re-stamps Z anyway).</summary>
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

    /// <summary>Debounced battle-edge teardown, called from Engine.ResetBattleState (both edges, the
    /// Larceny-carryover precedent). The bind and its hold never survive a battle; a raw mid-battle
    /// inLive dip must NOT land here.</summary>
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

    /// <summary>Post-bind F5: flip the decoy hold. Release is REFUSED unless the Canary 4 stamps landed
    /// this battle: a released, stamped Kerrich takes controllable turns (owner-proven 2026-07-10); an
    /// unstamped one crashes on turn arrival (the unassigned-controller-id null deref at 0x14018D102).</summary>
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

    /// <summary>The Canary 3 decoy pin: hold the double's CT at 0 (guarded write, every ~33ms tick) so
    /// the scheduler never grants the turn he cannot complete. Self-disengages if the engine tears the
    /// bind down (combat +0x01 no longer reads the model id stage-2 stamped).</summary>
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

    /// <summary>Death-watch (Canary 7): once a bind happened this battle, watch the double's composed
    /// Dead bit and, while dead, pin his crystal counter at 3 (guarded, every tick, the proven Sanctuary
    /// hold) so the corpse never CONVERTS: the removal edge game-overed battle 435 twice on 2026-07-10
    /// (cause unknown, identity-keyed FALSIFIED). A KO is survivable and revival releases the pin.</summary>
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

    /// <summary>Verify the builder is mapped this launch AND still carries its prologue (the address is
    /// beyond the fixed image's always-mapped span, so neither is guaranteed). Turns the one detectable
    /// crash class into a logged refusal.</summary>
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

    /// <summary>Both anim cold-call targets must be mapped and carry their expected opener before we call
    /// them in stage-2. Same discipline as TargetReady, for the two 1-arg lookups.</summary>
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

    /// <summary>The enroller must be mapped and carry its exe prologue before we cold-call it: the
    /// TargetReady discipline (a stale or patched address becomes a logged refusal, not a crash).</summary>
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

    /// <summary>Registry membership by the planner's OWN key (resolve loop 0x1402078F9, disasm
    /// 2026-07-10: the loop base lea at 0x1402078ED resolves to 0x141853CE1 = UnitsBase+1, so the
    /// pre-incremented scan pointer skips combat +0x01 (the hide gate, 0xFF = do-not-draw) and
    /// matches combat +0x02, zero-extended, vs registry object word +0x2C). Read-only and
    /// fail-safe: guarded reads, and a garbage registry pointer just misses. NOTE: the earlier
    /// synthesis that read the skip at +0x00 / key at +0x01 was off by one; the result-arithmetic
    /// lea at 0x14020793E uses the true base +0, proving the scan pointer's +1 pre-increment.</summary>
    private bool HostRegistered(out int regIdx)
    {
        regIdx = -1;
        if (_hostCombat == 0) return false;
        if (_mem.U8(_hostCombat + 0x01) == 0xFF) return false;   // planner's own skip: hidden/unbound
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

    /// <summary>CANARY 6 (SHIFT+F5): the COMPOSED ENROLL, step 1+2 (constants block for the decode).
    /// The freeze = Kerrich is a walk-visible but un-enrolled unit, so the AI decision arm no-ops
    /// and everyone Waits. Cold-call the per-slot AI-data allocator 0x140274F30(rcx=Kerrich slot):
    /// it allocates a free id, fills the AI-data entry from his slot, and returns the id (or -1).
    /// Then write that id -> his combat +0x02 (the membership id the AI maps a slot by). Does NOT
    /// touch the count byte or build an AI object (Canary 6b, only if this alone does not un-freeze).
    /// Gated to a paused unit menu (a turn/action boundary; never rebuild AI state mid-planner) with
    /// a live bind this battle. Refuses if Kerrich is already enrolled (+0x02 != 0xFF), so a mispress
    /// cannot double-allocate. THROWAWAY SAVE: this is a cold call into engine allocation code.</summary>
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
            // 6a already ran this battle (the AI-data row exists, live-verified: row 0x2E filled
            // 80 2E 4E.. parallel to a real enemy's 80 01 57..). Stage 6b: the object layer.
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

    /// <summary>CANARY 6b: build Kerrich's AI OBJECT, the engine's own single-object sequence
    /// lifted verbatim (constants block for the decode). Preconditions: 6a ran (combat +0x02 = a
    /// real id whose AI-data row carries the id stamp at entry+1; live-verified row 0x2E reads
    /// 80 2E 4E.., parallel to a real enemy's row) and no registry object matches yet. Steps:
    /// scratch-fill from the row, populate a FRESH pool object (the populator memsets it first),
    /// append it at registry[count], bump the builder's count word + the cached count byte by
    /// exactly 1 so the AI walk window includes it. Every write guarded; any precondition miss is
    /// a logged refusal. THROWAWAY SAVE: two cold calls into engine code.</summary>
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

    /// <summary>CTRL+F5: the CLEAN DESPAWN (owner instrument). Resolve a target, then cold-call the
    /// engine's own DECLARATIVE per-unit removal (mode-2 mark; constants block for the decode) so
    /// the unit AND its render node go together (the reverse door; a gate-FF poke removes only
    /// logic and strands a ghost-statue sprite). Refusals: no unambiguous target, Ramza (slot 16,
    /// the story lead; removing him is the defeat condition), a unit whose turn is OPEN (the
    /// AI-subject pointer complex from the 2026-07-10 crash decode), the engine's reserved node id,
    /// a busy gate byte, a removal already in flight, and the sweeper-dispatch landmark. ONE-WAY
    /// once the sweeper runs: re-adding is the hard direction this whole arc exists to crack.</summary>
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
            // The "special id" global is the CURRENT ACTOR's node id (live-decoded 2026-07-10):
            // never remove the unit whose turn is running.
            ModLogger.Event(LogVerb.Trace, $"body-double: CTRL+F5 refused: node id {id} is the current actor ([0x140CF873C]); never remove the acting unit.");
            return;
        }
        // NO gate-byte refusal: 0x140C6CFE0+id*9 turned out to be the "engine engaged with this
        // unit" marker (menu open / hover), which the AIMING flow itself sets on the target: the
        // original refusal self-blocked every hover-resolved despawn (owner hit it live
        // 2026-07-10). The first live pull (shell-fired with the hovered unit's byte reading 01)
        // completed byte-perfectly, so the marker is harmless for the mode-2 mark.
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

    /// <summary>Watch a pending mode-2 removal: the sweeper 0x14026E20C stamps combat+0x01=FF and
    /// marks node+0x12C done (|= 0x30) when it processes the mark. Completion logs once; a timeout
    /// (sweeper never reached, e.g. the game stayed paused) restores the saved mode byte so no
    /// half-marked node outlives the attempt.</summary>
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

    /// <summary>Despawn target resolution, two tiers, both fail-closed on ambiguity. The node comes
    /// from ONE bounded walk of the render-node list (head [0x140D3A410]; node+0x148 = the combat
    /// back-pointer, builder-written), the identity ground truth: sceneNodeIdx goes stale by design
    /// on engine removals, so it is never trusted here. Tier 1, the HOVERED unit: the condensed
    /// cursor struct's (level,hp,maxHp) fingerprint (any team; the follows-the-cursor trap is the
    /// feature) matched uniquely against the visible combat slots. Tier 2, nothing hovered: the
    /// unique ORPHAN, a node whose combat reads gate FF (a ghost-statue sprite: logic hidden, node
    /// alive). Two orphans or twin fingerprints = refusal naming them.</summary>
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

        // No hover answer: the unique ghost-statue orphan (node alive, combat gate FF).
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

    /// <summary>A drawn PLAYER unit to crib the Canary 4 completeness values from: combat slots 16..20
    /// (the player half of the init loop's {0-4}/{16-20} ranges). Drawn (+0x01 != 0xFF) with a non-zero
    /// job suffices; the earlier +0x02 == 01 gate was WRONG (that byte is dynamic turn state: Kenrick
    /// read 01 mid-battle but Ramza reads 00 at bind, which skipped the stamps on 2026-07-10).
    /// Out-of-battle fail-safe reads still refuse via the job gate (unreadable memory reads 0).
    /// Canary 7 changes: the HOST slot is excluded (post-bind it is itself a drawn player unit, and the
    /// double must never donate to itself), and GENERICS (17..20) are preferred over slot 16 (Ramza):
    /// the identity pair routes name + control, so a slot-16 donor puts a second field "Ramza" up; the
    /// crystal game-over proved identity-INDEPENDENT (2026-07-10 re-point falsification), but doubling
    /// the story lead stays avoided. Returns the combat base, or -1.</summary>
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

    /// <summary>CANARY 8: find a vacant slot in the donor's region (players 16..20, enemies 8..15;
    /// the deploy pipeline's regions are positional). Vacancy = the deploy loop's own empty stamp
    /// (gate FF + present FF); fully ZEROED slots preferred over deploy-staging residue (a deployed
    /// generic once left a hidden stat GHOST at slot 20). Returns the combat base and slot, or -1.</summary>
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
                return combat;   // zeroed: the preferred host
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

    /// <summary>CANARY 8: the hovered unit = the donor (the condensed cursor struct fingerprint,
    /// any team, matched uniquely against the visible combat slots; the follows-the-cursor trap is
    /// the feature). Fail-closed on twins and non-hover states.</summary>
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

    /// <summary>CANARY 8: a free tile adjacent to the donor for the clone (co-tile spawns are the
    /// proven target-shadowing + movement soft-lock). Neighbors first, then range-2 in the four
    /// cardinal directions; occupancy = any visible unit's logic tile. Map walkability is NOT
    /// checked (unknowable from here): a wall pick is a cosmetic strand, not a crash.</summary>
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

    /// <summary>CANARY 8: the donor's own scene-load entry (its combat ptr sits at entry +0x10);
    /// the donor being DRAWN guarantees its sheet is loaded, so the cribbed sprite-id resolves.</summary>
    private long FindSceneEntryOf(long combat)
    {
        for (int idx = 0; idx < 8; idx++)
        {
            long entry = SceneTable + idx * EntryStride;
            if ((long)_mem.U64(entry + 0x10) == combat) return entry;
        }
        return -1;
    }

    /// <summary>CANARY 8: copy the donor's full 0x200 combat struct into the vacant host, then fix
    /// the per-slot fields. +0x1BC = host slot is LOAD-BEARING twice (the builder's stamp index and
    /// the scene-bind loop's model == slot invariant); gate/membership FF and CT 0 keep the copy
    /// inert until the bind; the logic tile is the chosen FREE destination, never the donor's.</summary>
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

    /// <summary>CANARY 8, the freeze cure (live-proven by the resurrection): clone the donor's
    /// battle-keyed registry object, re-key +0x2C to the host slot, append its pointer at
    /// table[count], and bump the count word THEN the count byte (the walk never sees a
    /// half-entry). Data-only; runs while the copy is still hidden so the clone is AI-resolvable
    /// before it is ever walk-visible.</summary>
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

        // CANARY 9: assign the host the next free AI-ROSTER INDEX (the one byte that made the clone
        // a real AI fighter, live-proven). Next index = the count of slots already carrying a valid
        // index (< the gate cap); the reals are 0..N-1 sequential, so that count IS the next slot.
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

        // CANARY 7: a host was already claimed this battle: recovery paths only, NEVER a second copy
        // (re-copying over a live bind would stomp the engine's own state on that slot).
        if (_hostCombat != 0)
        {
            byte hGate = _mem.U8(_hostCombat + 0x01);
            int hNode = (int)_mem.U32(SceneNodeIdx + _hostModel * 4);

            // RECOVERY RE-ARM: the double is bound (gate reads the host model and his node exists) but
            // the hold is disengaged. Re-pin without touching the engine, so a teardown that turns out
            // transient never strands a stall-capable double with no way back. _boundThisBattle is
            // load-bearing on the plain-F5 path via _bound; here the host claim itself is the witness
            // (only this spike's copy can have set _hostCombat since the last battle edge).
            if (hGate == _hostModel && hNode >= 0)
            {
                _mem.WriteBytes(_hostCombat + CtOffset, ZeroCt);
                _bound = true;
                _decoyHold = true;
                ModLogger.Event(LogVerb.Trace, "body-double: the player-slot double is already bound; decoy CT-hold re-armed on the existing bind.");
                return;
            }
            // A previous F5 built the node but stage 2 aborted (anim targets unready or a throw): bind
            // the EXISTING node instead of cold-calling the builder again, which would leak a second
            // node under the same label into the render list.
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
            // hGate == 0xFF && hNode < 0: the copy landed but no node yet; fall through to the build.
        }
        else
        {
            // CANARY 8: duplicate the HOVERED unit. The donor supplies everything: body bytes,
            // scene entry, registry object, identity. Order is load-bearing (class doc).
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

        // Crib the DONOR's node args, but a1/a2/a3 = THE HOST'S OWN TILE (they land in the node's
        // tile key +0x88/89/8A, the AI decision pipeline's lookup: cribbing the donor's tile here
        // is exactly the bug that froze Canary 7's binds). arg8 -> the host, arg7 -> a fresh label,
        // arg10 -> render-mgr. The builder stamps SceneNodeIdx[arg8's +0x1BC] and writes
        // SecondTable[model] itself, so the host's tables come out engine-authored.
        long a1 = _mem.U8(_hostCombat + 0x4F);           // dest tile x
        long a2 = _mem.U8(_hostCombat + 0x50);           // dest tile y
        long a3 = (byte)(_mem.U8(_hostCombat + 0x51) >> 7);   // layer bit
        long a4 = (ushort)(b[0x04] | (b[0x05] << 8));
        long a5 = (ushort)(b[0x06] | (b[0x07] << 8));   // sprite-id (the donor's own LOADED sheet)
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

    /// <summary>STAGE 2: replicate the scene-bind body so the built node becomes FRANK (identity + own
    /// animation) instead of a shallow sibling clone. Cold-calls are the two bounded anim lookups; the
    /// rest are guarded direct writes. The node+0x270 sub-block is deliberately skipped (see the class
    /// doc). On success, applies the Canary 4 completeness stamps (donor-cribbed controller id, faction
    /// pair, real-unit marker), engages the decoy CT-hold, and dumps the forensics.</summary>
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

        // CANARY 4 stamps, Canary 8 flavor: the identity/faction source is THE DUPLICATED UNIT
        // itself (the clone doubles the donor, name and control); the old player-donor scan stays
        // as the fallback for recovery paths that lost the donor.
        long donor = _donorCombat != 0 ? _donorCombat : FindPlayerDonor(_hostCombat);
        if (donor > 0)
        {
            byte id = _mem.U8(donor + 0x191);
            byte id2 = _mem.U8(donor + 0x192);
            byte t5 = _mem.U8(donor + 0x05);
            byte t6 = _mem.U8(donor + 0x06);
            byte tEE = _mem.U8(donor + 0x1EE);
            _mem.WriteBytes(_hostCombat + 0x1BE, new[] { (byte)1 });   // real-unit marker
            // The +0x191/+0x192 pair is the ROSTER-IDENTITY backref: it routes control ownership AND
            // name resolution, so the double literally doubles the donor (copying Ramza's pair
            // renamed him "Ramza" on the field, owner-witnessed). That IS the Body Double fantasy.
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

        // CANARY 3a: pin CT to 0 BEFORE unpause, the safe default (the double takes no turn until the
        // owner releases the hold). Zero it once here, then arm the per-tick hold; both happen while
        // the menu still has the game paused.
        _mem.WriteBytes(_hostCombat + CtOffset, ZeroCt);
        _bound = true;
        _boundThisBattle = true;   // the re-arm witness: only a bind made this battle may re-arm
        _decoyHold = true;

        // CANARY 8 flourish + certainty: world X/Y stamped from the proven formulas (28*tile+14),
        // Z parked in the heavens; the tick loop lowers the clone to the donor's own altitude.
        // Transform offsets are LIST-NODE relative: list node = pool element + 8 (the reconciled
        // pool layout: element+0 = in-use dword, element+8 = the node the renderer links).
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

        // CANARY 3b: black-Monk forensics. File-only DBG dump of the cribbed sibling's node next to
        // Frank's fresh one; the offline diff names the per-instance fields the skipped node+0x270
        // sub-block (the suspected VRAM/palette slot math) leaves different.
        DumpForensics(entry, node);
    }

    /// <summary>Read-only post-bind forensics: a per-slot summary (job, gates, model, si, node index),
    /// full hex dumps of the sibling's node and Frank's (the black-Monk diff input), and full combat
    /// snapshots of both (the AI-visibility diff input). Every line lands as a DBG log line AND in a
    /// persisted file in the update-safe save dir: the Canary 3 dumps were written only to the rotating
    /// log and two post-crash relaunches destroyed them before they were read.</summary>
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
            // Both tables are Slots entries long (SceneNodeIdx - SecondTable = 0x54 = 21*4); a vacant
            // slot's model byte (0xFF) would walk past them and log out-of-table image bytes as data.
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
        // Combat snapshots for the AI-visibility question (Canary 3: the AI idles with Kerrich enrolled
        // even while turnless). The "combat" label keeps node_dump_diff.py's dump[...] parser blind to
        // them; spawn_probe banddiff reads the same structs live with annotations.
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

    /// <summary>Persist the forensic lines to their own file in the update-safe save dir
    /// (Reloaded\User\Mods), which survives deploys AND log rotation. Never throws into the tick;
    /// on failure the DBG lines in livingweapon.log are the only copy.</summary>
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
