#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (LW-58, ShowSpike tradition): cold-call the engine's own status APPLY ENGINE so
/// the ENGINE inflicts a status, instead of us faking its downstream bytes. This is the reverse of
/// the closed lanes proven 2026-07-09: external writes to the composed/inflicted/pending fields are
/// welded, ignored, or scratch (spawn_probe.py's revert/inflict tapes), so the remaining lever is
/// to invoke the engine's routine directly (the callout-banner precedent: ShowSpike.ColdSpawn
/// cold-called a game function from THIS loop thread and it drew, 2026-07-03).
///
/// TARGET: the apply engine at FnApplyStatuses 0x150BF66DC (owner CE what-writes + disasm
/// 2026-07-09). Plain real code (NOT a Denuvo VM springboard like the inner dispatch 0x1401FB064):
/// it takes (ecx = battle-stats slot, edx = mode), derives its own unit-base globals from the slot
/// arg, walks all 40 status ids for that unit, reads the PENDING-ADD field, conflict-scans, and ORs
/// accepted bits into the inflicted layer. We write the pending bit SYNCHRONOUSLY and immediately
/// cold-call it, testing whether a synchronous write-then-process applies where the async
/// external-write-then-natural-consume did NOT (the inflict tapes: consumed, never applied).
///
/// SAFETY (this is crash-capable code; an internal engine AV is uncatchable, so prevention is the
/// only defense, adversarial-review-hardened 2026-07-09):
///  - The target address lies ABOVE the always-mapped main image, so before every fire we require
///    Mem.Readable AND a prologue-byte landmark match; a stale/patched address becomes a logged
///    refusal, not a crash.
///  - IsCallableSeat keeps the engine slot in range (a negative slot underflows the array).
///  - We fire ONLY while paused (PauseFlag == 1) to narrow the reentrancy window: the routine sets
///    PROCESS-SHARED unit-base globals, so a loop-thread call racing the game's per-frame status
///    pass could cross-wire units. Pausing idles the action pipeline that mutates status. A residual
///    per-frame COMPOSE race remains (it cannot be eliminated from the loop thread); accept it for a
///    key-triggered single call. Effects are observed after unpause.
///  - Fire only during a genuine inLive frame (not the debounced exit window), and only when the
///    game window is foreground (a global GetAsyncKeyState must not fire the cold call while
///    alt-tabbed, per the flight-recorder alt-tab doctrine).
///
/// EXPECTED FORKS:
///  - Haste (F2) lands on a live enemy  -> the cold call works and applies; a safe, engine-faithful
///    infliction primitive. Then treasure (F4) on a corpse should convert it (model swap and all).
///  - Nothing applies across modes      -> the pending field is not a sufficient input even
///    synchronously; the next spike (v2) cold-calls the VM dispatch 0x1401FB064(id+1, mode, slot)
///    directly (higher risk). Documented, not attempted here.
///
/// CANARY DISCIPLINE (denuvo-hook-launch-fragility): F2 (harmless Haste) MUST be pressed first each
/// launch to prove the cold call is alive and non-crashing before F4 touches the conversion path. A
/// first cold call of a game function can be dead a whole launch, or crash; a dead game = restart,
/// the standard spike tax. THROWAWAY SAVE ONLY, autosave quarantined.
///
/// USAGE: in a live battle, OPEN A UNIT'S MENU (so PauseFlag == 1), then press F2 (canary) or F4
/// (treasure). Each key owns its own mode counter, cycling the mode arg 0 -> 1 -> 2 so an id's apply
/// mode sweeps deterministically. Read livingweapon.log's status-spike lines for APPLIED=..., then
/// unpause and watch the unit. F2 is in this box's known-working key set; F4 is free.
/// </summary>
internal sealed class StatusSpike
{
    private const long FnApplyStatuses = 0x150BF66DC;   // ecx = battle-stats slot, edx = mode

    // The apply engine's prologue (disasm 2026-07-09): mov [rsp+8],rbx; mov [rsp+0x10],rbp. A
    // landmark that the address still points at the expected routine (game not patched/relocated).
    private static readonly byte[] ApplyPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10 };

    // x64 (rcx, rdx): first int in ecx, second in edx, matching the disassembled prologue.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ApplyStatusesFn(int unitSlot, int mode);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    private const int VkF2 = 0x71;   // canary (Haste, live enemy)
    private const int VkF4 = 0x73;   // treasure (id 15, corpse)

    private readonly IGameMemory _mem;
    private bool _f2Was, _f4Was;
    private int _hasteMode, _treasureMode;   // per-key sweeps 0 -> 1 -> 2 independently
    private int _hbTick;
    private bool _announced;

    public StatusSpike(IGameMemory mem) => _mem = mem;

    /// <summary>In-battle loop tick: heartbeat + key edges. The cold call runs HERE on the loop
    /// thread (ShowSpike.ColdSpawn proved a loop-thread cold call draws), gated to a paused frame.
    /// <paramref name="inLive"/> is the caller's genuine-live-battle predicate (not the debounced
    /// sticky state), the same gate every battle-memory writer honors.</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Debug(LogVerb.Trace, "status-spike: armed (dev). Open a unit's menu (pause), then F2 = CANARY Haste on a live enemy (press FIRST to prove the cold call is safe); F4 = TREASURE on a corpse. THROWAWAY SAVE ONLY.");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"status-spike: alive (haste mode {_hasteMode}, treasure mode {_treasureMode}, writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (!inLive) return;   // only fire during a genuine live-battle frame
        if (Pressed(VkF2, ref _f2Was)) Fire("CANARY", StatusApply.HasteId, corpse: false, ref _hasteMode);
        if (Pressed(VkF4, ref _f4Was)) Fire("TREASURE", StatusApply.TreasureId, corpse: true, ref _treasureMode);
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

    /// <summary>Verify the cold-call target is mapped this launch AND still carries the expected
    /// prologue (the address is beyond the fixed image's always-mapped span, so neither is
    /// guaranteed). Converts the one detectable crash class into a logged refusal.</summary>
    private bool TargetReady(string label)
    {
        if (!Mem.Readable(FnApplyStatuses, ApplyPrologue.Length))
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} apply target 0x{FnApplyStatuses:X} is not mapped this launch; refusing the cold call.");
            return false;
        }
        if (!_mem.TryReadBytes(FnApplyStatuses, ApplyPrologue.Length, out var got))
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} could not read the apply target's prologue; refusing the cold call.");
            return false;
        }
        for (int i = 0; i < ApplyPrologue.Length; i++)
            if (got[i] != ApplyPrologue[i])
            {
                ModLogger.Error(LogVerb.Trace, $"status-spike: {label} apply target 0x{FnApplyStatuses:X} prologue mismatch (game patched or wrong address); refusing the cold call.");
                return false;
            }
        return true;
    }

    /// <summary>First enemy-side band seat (seat &lt; 24 == n &lt; 0) that maps to a callable engine
    /// slot, is sane, matches the alive/corpse filter, and does NOT already carry the target status
    /// (a pre-set target could not prove the call did anything). Prefers a real-position entry over a
    /// frozen (0,0) mirror twin (Band.ActiveOwner's rule); returns the twin only if no real-position
    /// candidate exists. -1 if none.</summary>
    private int FindEnemy(bool corpse, int by, byte mask)
    {
        int twin = -1;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            if (s >= Offsets.BandSlots / 2) break;             // enemy side only
            if (!StatusApply.IsCallableSeat(s)) continue;       // AV guard: engine slot in range
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            int hp = _mem.U16(e + Offsets.AHp);
            if (corpse ? hp != 0 : hp == 0) continue;
            byte comp = _mem.U8(e + StatusApply.Composed + by);
            byte infl = _mem.U8(e + StatusApply.Inflicted + by);
            if (StatusApply.AlreadyHeld(comp, infl, mask)) continue;   // can't prove anything on a pre-set target
            bool realPos = _mem.U8(e + Offsets.AGx) != 0 || _mem.U8(e + Offsets.AGy) != 0;
            if (realPos) return s;                              // prefer a real-position unit
            if (twin < 0) twin = s;                             // remember the (0,0) twin as a fallback
        }
        return twin;
    }

    private void Fire(string label, int statusId, bool corpse, ref int modeCounter)
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Event(LogVerb.Trace, $"status-spike: {label} ignored; the fingerprint guard has not armed yet (no writes).");
            return;
        }
        if (Mem.U8(Offsets.PauseFlag) != 1)
        {
            ModLogger.Event(LogVerb.Trace, $"status-spike: {label} needs a paused menu; open a unit's menu first so the engine's status pass is idle (reentrancy guard).");
            return;
        }
        if (!TargetReady(label)) return;

        int by = StatusApply.StatusByte(statusId);
        byte mask = StatusApply.StatusMask(statusId);
        int seat = FindEnemy(corpse, by, mask);
        if (seat < 0)
        {
            ModLogger.Event(LogVerb.Trace, $"status-spike: {label} found no usable {(corpse ? "KO'd" : "live")} enemy (none, or all already carry the status) in the band right now.");
            return;
        }
        int mode = modeCounter;
        modeCounter = (modeCounter + 1) % 3;

        long e = Band.Entry(seat);
        int engineSlot = StatusApply.EngineSlot(seat);
        long pend = e + StatusApply.PendingAdd + by;
        long comp = e + StatusApply.Composed + by;
        long infl = e + StatusApply.Inflicted + by;

        byte compBefore = _mem.U8(comp);
        byte inflBefore = _mem.U8(infl);
        int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
        byte p = _mem.U8(pend);
        _mem.WriteBytes(pend, new[] { (byte)(p | mask) });   // synchronous pending-ADD request
        ModLogger.Event(LogVerb.Trace,
            $"status-spike: {label} seat {seat} pos ({gx},{gy}) engineSlot {engineSlot} status {statusId} mode {mode}; pending {p:X2}->{(byte)(p | mask):X2}, composed {compBefore:X2} inflicted {inflBefore:X2}; COLD-CALLING apply 0x{FnApplyStatuses:X}...");
        try
        {
            var apply = Marshal.GetDelegateForFunctionPointer<ApplyStatusesFn>(unchecked((nint)FnApplyStatuses));
            apply(engineSlot, mode);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} cold call threw (managed): {ex.Message}");
            return;
        }
        byte compAfter = _mem.U8(comp);
        byte inflAfter = _mem.U8(infl);
        byte pendAfter = _mem.U8(pend);
        bool applied = StatusApply.NewlyApplied(compBefore, inflBefore, compAfter, inflAfter, mask);
        ModLogger.Event(LogVerb.Trace,
            $"status-spike: {label} after -> composed {compAfter:X2} inflicted {inflAfter:X2} pending {pendAfter:X2}; APPLIED={applied}. Unpause and watch the unit for an icon/tick{(corpse ? "/model swap" : "")}.");
    }
}
#endif
