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
/// 16..20, drawn, +0x02 == 01, the player-side controller flag). (a) +0x1BE = 01, the real-unit marker
/// (the validity-predicate battery 0x1400DCE00.. keys on it; its writer 0x140456444 is a separate init
/// phase the hidden-reserve path skips). (b) +0x191 = the donor's CONTROLLER ID: FE (unassigned) is why
/// turn-arrival crashed at 0x14018D102 (null controller singleton, factory object id 0x151); with a
/// player-family id Kerrich took real turns, moved, attacked, and DIED CLEANLY into a treasure chest
/// (the full lifecycle, zero crashes). (c) +0x05/+0x06 = the donor's faction/tint pair (garbage there
/// tints the shared Monk sheet reddish). The decoy CT-hold still engages at bind as the safe default;
/// F5 releases it and his turns go LIVE (owner-proven safe once the id is stamped). STILL OPEN: the
/// enemy AI idles while a live Kerrich is enrolled (hide or death both un-stick it, owner-proven twice);
/// the next lever is +0x1EE bit 0x10 from validity predicate 0x1400DD2D8, tested PROBE-SIDE first
/// (spawn_probe peek/poke at 0x141854CCE), deliberately NOT stamped here until proven.
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
/// USAGE: a FRESH battle 435 on the Frank-as-Monk build, THROWAWAY SAVE ONLY (autosave quarantined). Open a
/// unit's menu (pause), press F5 once (bind + Canary 4 stamps + hold engaged + forensics persisted), then
/// unpause. EXPECT: Kerrich drawn and listed as a turnless decoy; press F5 again to release the hold and
/// take control of him as a party unit. KNOWN OPEN FAULT: the enemy AI idles while a live Kerrich is
/// enrolled (it resumes the moment he dies or hides, owner-proven twice); run the +0x1EE bit-0x10 probe
/// experiment (Canary 4 paragraph above) in the stalled state before ending the battle.
/// </summary>
internal sealed class BodyDoubleSpike
{
    private const long FnNodeBuild  = 0x14026EBEC;   // render-node builder (10-arg Win64; PATH A allocates + stamps)
    private const long SceneTable   = 0x141856728;   // scene-load entry table, stride 0x20 (idx 0..6 populated)
    private const long UnitsBase    = 0x141853CE0;   // BattleUnitsBase, stride 0x200
    private const long FrankCombat  = 0x141854AE0;   // combat slot 7 (Frank, the inserted unit)
    private const long SceneNodeIdx = 0x140822DAC;   // int32[] indexed by combat +0x1BC model id (-1 = no node)
    private const long GModeAddr    = 0x142FF9E2C;   // 0 = PATH A (allocate + stamp); 1 = PATH B (reuse)
    private const long RenderMgrPtr = 0x140CE3250;   // arg10 = *this (render-mgr singleton)

    // Stage-2 bind (scene-bind body 0x1401D517D..): turn the freshly built node into Frank's own unit.
    private const long NodePool     = 0x140D30030;   // render-node pool base; node N = pool + sceneNodeIdx*0x548
    private const long SecondTable  = 0x140822D58;   // per-slot sprite-resource index (si = byte[+slot*4])
    private const long AnimBase     = 0x140D005A0;   // animByte = byte[AnimBase + si*0x32D6 + 1]
    private const long FnAnimA      = 0x14E8CBF78;   // 1-arg (ecx=animByte), bounded lookup -> node+0x230
    private const long FnAnimB      = 0x14E8F2CA0;   // 1-arg (ecx=animByte), bounded lookup -> node+0x238

    private const int EntryStride   = 0x20;
    private const int Slots         = 21;            // combat slots covered by BattleUnitsBase
    private const byte MonkJob      = 0x4E;          // Frank's job on this build; a LOADED generic-male sheet
    private const long FreshLabel   = 0x86;          // arg7 >= 16 forces the find-first-free (fresh alloc) branch

    // Canary 3a (decoy hold): CT = combat +0x41 (band +0x25, Offsets.ACtSlam, the byte ExtraTurn slams
    // to 100 to GRANT a turn, Zwill live-proven). Pinned to 0 so Frank's turn never arrives. WRITE-only:
    // its reads are documented unreliable (rod-pass CT trap), so the hold never reads it back.
    private const int CtOffset      = 0x41;
    private const int NodeSize      = 0x548;          // render-node stride (pool 0x140D30030)
    private const int SummarySlots  = 8;              // battle 435: combat slots 0..6 vanilla + Frank at 7
    private static readonly byte[] ZeroCt = { 0 };

    // Builder prologue (code_patch read 2026-07-10): mov [rsp+0x20],rbx; push rbp/rsi/rdi/r12/r13/r14/r15.
    private static readonly byte[] Prologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x20, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57 };

    // Both anim lookups open identically (disasm 2026-07-10): movzx eax,cx; lea rcx,[rip+table]. Landmark
    // enough (with the fixed address + Mem.Readable) to reject a patched or wrong routine before calling.
    private static readonly byte[] AnimPrologue = { 0x0F, 0xB7, 0xC1, 0x48, 0x8D, 0x0D };

    // Win64 (Winapi on x64): args 1-4 in rcx/rdx/r8/r9, args 5-10 on the stack at [rsp+0x20..0x48]. All
    // widths <= 8 bytes take a full slot; the callee reads cl/dl/r8b/r9w and the word/dword stack fields.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long NodeBuildFn(long a1, long a2, long a3, long a4, long a5,
                                      long a6, long a7, long a8, long a9, long a10);

    // Anim object fetch: ecx = animByte, returns rax (the object). (0x14E8F2CA0 also returns edx, which the
    // node+0x270 sub-block would consume; C# cannot capture it, so that sub-block is skipped, see the doc.)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long AnimFn(int animByte);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    private const int VkF5 = 0x74;   // reveal + hold toggle. F5, NOT F6: three WIRED spikes (Flavor/Header/AttackCard) already poll F6, so one press would fire them all. ShowSpike claims F5/F8 but is not wired in Engine.

    private readonly IGameMemory _mem;
    private readonly string _saveDir;   // update-safe (Reloaded\User\Mods): survives deploys AND log rotation
    private bool _f5Was;
    private int _hbTick;
    private bool _announced;
    private bool _bound;            // stage-2 bind completed this battle; F5 now toggles the decoy hold
    private bool _decoyHold;        // per-tick CT pin active (Frank's turn never arrives)
    private bool _stamped;          // Canary 4 stamps landed this battle; hold release is refused without them
    private bool _boundThisBattle;  // witness: BindStage2 ran since the last battle edge. The re-arm
                                    // path REQUIRES it: gate==modelId + a stamped node also describes any
                                    // legitimately drawn slot-7 Monk in a battle this spike never touched,
                                    // and a mispressed F5 must not pin a real unit's CT there.
    private byte _boundModelId;     // the combat +0x01 value stage-2 stamped; any other read = bind torn down
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
            ModLogger.Event(LogVerb.Trace, "body-double: armed (dev). Fresh battle 435 on the Frank-Monk build, THROWAWAY SAVE. Open a unit's menu (pause), then F5 cold-calls the node builder to reveal Frank (slot 7).");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"body-double: alive (writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (Pressed(VkF5, ref _f5Was))
        {
            if (_bound) ToggleHold();
            else if (inLive) Fire();
            else ModLogger.Event(LogVerb.Trace, "body-double: F5 ignored (not a settled live frame; browse/enemy frames read mode 1, try again in a paused unit menu).");
        }
        // The hold deliberately does NOT gate on raw inLive: this call site is only reached while the
        // DEBOUNCED battle is in, and on 1.5 raw inLive dips false on every mode-1 frame (move-browsing,
        // enemy phases), which is EXACTLY when CT clockticks accrue. Gating the pin on inLive would leave
        // the whole accrual phase uncovered (a contiguous enemy stretch reaches 100 unwritten) and a
        // single dip must never read as "battle left": teardown is ResetBattle (the debounced edges,
        // Engine.ResetBattleState), never a raw frame. The write target is Frank's fixed static slot,
        // so the broader gate adds no wrong-address risk (the CharmLock/TreasureMaster pre-gate lesson).
        if (_decoyHold) HoldDecoy();
    }

    /// <summary>Debounced battle-edge teardown, called from Engine.ResetBattleState (both edges, the
    /// Larceny-carryover precedent). The bind and its hold never survive a battle; a raw mid-battle
    /// inLive dip must NOT land here.</summary>
    public void ResetBattle()
    {
        if (!_bound && !_decoyHold && !_boundThisBattle) return;
        _bound = false;
        _decoyHold = false;
        _boundThisBattle = false;
        _stamped = false;
        ModLogger.Event(LogVerb.Trace, "body-double: battle edge; bind + decoy hold cleared (a new battle needs a fresh F5).");
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

    /// <summary>The Canary 3 decoy pin: hold Frank's CT at 0 (guarded write, every ~33ms tick) so the
    /// scheduler never grants the turn he cannot complete. Self-disengages if the engine tears the bind
    /// down (combat +0x01 no longer reads the model id stage-2 stamped).</summary>
    private void HoldDecoy()
    {
        if (!Mem.WritesEnabled) return;
        byte gate = _mem.U8(FrankCombat + 0x01);
        if (gate != _boundModelId)
        {
            _bound = false;
            _decoyHold = false;
            ModLogger.Event(LogVerb.Trace, $"body-double: Frank +0x01 reads {gate:X2}, not the bound {_boundModelId:X2}; the engine tore the bind down. Decoy hold disengaged.");
            return;
        }
        _mem.WriteBytes(FrankCombat + CtOffset, ZeroCt);
        if (++_holdHb % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, "body-double: decoy hold alive (CT pinned at 0; the queue should flow around Kerrich)");
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

    /// <summary>A drawn PLAYER unit to crib the Canary 4 completeness values from: combat slots 16..20
    /// (the player half of the init loop's {0-4}/{16-20} ranges; Kenrick sat at slot 17 in the proving
    /// session), drawn (+0x01 != 0xFF) and carrying the player-side controller flag (+0x02 == 01;
    /// enemies and hidden units read FF there, banddiff 2026-07-10). Returns the combat base, or -1.</summary>
    private long FindPlayerDonor()
    {
        for (int slot = 16; slot <= 20; slot++)
        {
            long combat = UnitsBase + slot * 0x200L;
            if (_mem.U8(combat + 0x01) == 0xFF) continue;
            if (_mem.U8(combat + 0x02) != 0x01) continue;
            if (_mem.U8(combat + 0x03) == 0) continue;   // a vacant slot's job byte
            return combat;
        }
        return -1;
    }

    /// <summary>A DRAWN Monk sibling's scene-load entry (its sheet is loaded, so its sprite-id resolves).
    /// Scans populated entries idx 0..6, matching a real combat slot whose job is Monk and which is drawn
    /// (+0x01 != 0xFF) and is not Frank. Returns the entry base address, or -1 if none.</summary>
    private long FindMonkSiblingEntry()
    {
        for (int idx = 0; idx < 7; idx++)
        {
            long entry = SceneTable + idx * EntryStride;
            ulong combat = _mem.U64(entry + 0x10);   // the entry's own arg8 (its combat ptr)
            if (combat < (ulong)UnitsBase) continue;
            long off = (long)combat - UnitsBase;
            if (off < 0 || off >= Slots * 0x200 || (off % 0x200) != 0) continue;   // must land on a real slot
            if ((long)combat == FrankCombat) continue;
            if (_mem.U8((long)combat + 0x03) != MonkJob) continue;   // same LOADED sheet as Frank-the-Monk
            if (_mem.U8((long)combat + 0x01) == 0xFF) continue;      // must be a DRAWN sibling
            return entry;
        }
        return -1;
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
        byte job = _mem.U8(FrankCombat + 0x03);
        if (job != MonkJob)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: slot 7 job {job:X2} is not Monk {MonkJob:X2} (wrong battle or the Black-Mage build). Refusing.");
            return;
        }
        byte modelId = _mem.U8(FrankCombat + 0x1BC);
        long nodeIdxAddr = SceneNodeIdx + modelId * 4;
        int before = (int)_mem.U32(nodeIdxAddr);
        byte gate = _mem.U8(FrankCombat + 0x01);

        // RECOVERY RE-ARM: Frank is already revealed (the gate reads the model id stage-2 stamps and his
        // node exists) but the hold is disengaged. Re-pin without touching the engine, so a teardown that
        // turns out transient never strands a stall-capable Kerrich with no way back. _boundThisBattle is
        // load-bearing: gate==modelId + a stamped node is ALSO the signature of any ordinarily drawn
        // slot-7 Monk in an untouched battle, and F5 there must stay a refusal, not a CT pin.
        if (_boundThisBattle && gate == modelId && before >= 0)
        {
            _mem.WriteBytes(FrankCombat + CtOffset, ZeroCt);
            _bound = true;
            _boundModelId = modelId;
            _decoyHold = true;
            ModLogger.Event(LogVerb.Trace, "body-double: Kerrich is already bound; decoy CT-hold re-armed on the existing bind.");
            return;
        }
        if (gate != 0xFF)
        {
            ModLogger.Event(LogVerb.Trace, $"body-double: Frank +0x01 = {gate:X2} (not 0xFF hidden, not a re-armable bind); wrong state. Refusing.");
            return;
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

        long entry = FindMonkSiblingEntry();
        if (entry < 0)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: no DRAWN Monk sibling scene-load entry to crib (need a loaded-sheet sibling on the field); refusing.");
            return;
        }

        // A previous F5 built the node but stage 2 aborted (anim targets unready or a throw): bind the
        // EXISTING node instead of cold-calling the builder again, which would leak a second node under
        // the same label into the render list.
        if (before >= 0)
        {
            ModLogger.Event(LogVerb.Trace,
                $"body-double: sceneNodeIdx[{modelId}] = {before} is already stamped with Frank still hidden (an aborted stage-2); re-binding the existing node, no second build.");
            BindStage2(before, modelId, entry);
            return;
        }

        if (!TargetReady()) return;
        if (!_mem.TryReadBytes(entry, EntryStride, out var b))
        {
            ModLogger.Error(LogVerb.Trace, "body-double: could not read the sibling scene-load entry; refusing.");
            return;
        }

        // Crib the sibling's node args; override arg8 -> Frank, arg7 -> a fresh label, arg10 -> render-mgr.
        long a1 = b[0x00];
        long a2 = b[0x01];
        long a3 = b[0x02];
        long a4 = (ushort)(b[0x04] | (b[0x05] << 8));
        long a5 = (ushort)(b[0x06] | (b[0x07] << 8));   // sprite-id (a LOADED Monk sheet, so it resolves)
        long a6 = (ushort)(b[0x08] | (b[0x09] << 8));
        long a7 = FreshLabel;                            // force a fresh alloc, do not match the sibling's node
        long a8 = FrankCombat;                           // bind the new node to Frank
        long a9 = (uint)(b[0x18] | (b[0x19] << 8) | (b[0x1A] << 16) | (b[0x1B] << 24));
        long a10 = (long)renderMgr;

        ModLogger.Event(LogVerb.Trace,
            $"body-double: cribbing entry 0x{entry:X} (spriteId {a5:X4}); Frank model {modelId}, sceneNodeIdx[{modelId}] = {before}; COLD-CALLING builder 0x{FnNodeBuild:X} (arg8 = Frank 0x{FrankCombat:X}, arg7 label 0x{a7:X})...");
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
            $"body-double: builder returned 0x{ret:X}; sceneNodeIdx[{modelId}] {before} -> {after}; NODE-BUILT={built}.");
        if (!built)
        {
            ModLogger.Event(LogVerb.Trace, "body-double: the node was not stamped (build failed or self-aborted); skipping the stage-2 bind.");
            return;
        }
        BindStage2(after, modelId, entry);
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
        _mem.WriteBytes(node + 0x150, BitConverter.GetBytes(FrankCombat));   // node->unit link (position + hover)
        _mem.WriteBytes(FrankCombat + 0x01, new[] { modelId });              // clears the 0xFF hide gate -> real listed unit
        _mem.WriteBytes(node + 0x11, new[] { si });
        _mem.WriteBytes(node + 0x230, BitConverter.GetBytes(r230));          // Frank's OWN anim objects (decouples the clone)
        _mem.WriteBytes(node + 0x238, BitConverter.GetBytes(r238));
        _mem.WriteBytes(FrankCombat + 0x1B5, new[] { (byte)1 });             // draw enable, written last
        ModLogger.Event(LogVerb.Trace,
            $"body-double: STAGE-2 bound node 0x{node:X} to Frank (si {si:X2}, animByte {animByte:X2}, node+0x230 0x{r230:X}, node+0x238 0x{r238:X}); combat+0x01 -> {modelId:X2}, +0x1B5 -> 01. Unpause: Frank should be a real listed Monk with his own animation.");

        // CANARY 4: the completeness stamps, every one owner-proven by live pokes (see the class doc).
        // Cribbed from a live player donor so the values track the running battle, not hardcoded bytes.
        long donor = FindPlayerDonor();
        if (donor > 0)
        {
            byte id = _mem.U8(donor + 0x191);
            byte t5 = _mem.U8(donor + 0x05);
            byte t6 = _mem.U8(donor + 0x06);
            _mem.WriteBytes(FrankCombat + 0x1BE, new[] { (byte)1 });   // real-unit marker
            _mem.WriteBytes(FrankCombat + 0x191, new[] { id });        // controller id (player family)
            _mem.WriteBytes(FrankCombat + 0x05, new[] { t5 });         // faction/tint pair
            _mem.WriteBytes(FrankCombat + 0x06, new[] { t6 });
            _stamped = true;
            ModLogger.Event(LogVerb.Trace,
                $"body-double: CANARY 4 stamps applied from donor 0x{donor:X}: controller id {id:X2}, faction {t5:X2} {t6:X2}, real-unit marker 01.");
        }
        else
        {
            ModLogger.Event(LogVerb.Trace,
                "body-double: no drawn player donor in combat slots 16..20; completeness stamps SKIPPED. Kerrich stays a pure decoy; do NOT release the hold (an unassigned controller id crashes on turn arrival).");
        }

        // CANARY 3a: pin CT to 0 BEFORE unpause, the safe default (Kerrich takes no turn until the
        // owner releases the hold). Zero it once here, then arm the per-tick hold; both happen while
        // the menu still has the game paused.
        _mem.WriteBytes(FrankCombat + CtOffset, ZeroCt);
        _bound = true;
        _boundThisBattle = true;   // the re-arm witness: only a bind made this battle may re-arm
        _boundModelId = modelId;
        _decoyHold = true;
        ModLogger.Event(LogVerb.Trace,
            "body-double: decoy CT-hold engaged (combat +0x41 pinned 0). Unpause: Kerrich stands as a decoy; press F5 to release the hold and take control of him (stamped builds only).");

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
        for (int slot = 0; slot < SummarySlots; slot++)
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
        DumpBytes(Emit, "combat[frank]", FrankCombat, 0x200);
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
