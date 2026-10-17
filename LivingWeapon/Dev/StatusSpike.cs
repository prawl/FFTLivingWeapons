#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Dev-only spike (LW-58, ShowSpike tradition) that cold-calls the engine's status apply routine so the
/// engine inflicts the status itself. External writes to the composed/inflicted/pending fields were
/// closed lanes on 2026-07-09 (spawn_probe.py tapes). Target is FnApplyStatuses 0x150BF66DC (CE
/// what-writes + disasm 2026-07-09), ecx = battle-stats slot, edx = mode. A pending-add bit is written
/// and the routine called at once. Same shape as ShowSpike.ColdSpawn (2026-07-03). Fires only with a
/// paused unit menu (PauseFlag == 1); F2 Haste on a live enemy is the canary, F4 treasure on a corpse.
/// Throwaway save only, see denuvo-hook-launch-fragility. Request lane usage is on RequestFileName.
/// </summary>
internal sealed class StatusSpike
{
    private const long FnApplyStatuses = 0x150BF66DC;   // v1 target. Ecx = battle-stats slot, edx = mode

    // The v1 apply engine's prologue (disasm 2026-07-09): mov [rsp+8],rbx; mov [rsp+0x10],rbp.
    private static readonly byte[] ApplyPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10 };

    // ---- v2 (2026-07-22): resolve the routine at runtime ----
    // The pinned v1 address refused with a prologue mismatch on 2026-07-22 (unrelated code there; the region
    // above the fixed image moves between launches). The inner dispatch at 0x1401FB064 is in the fixed image
    // (no ASLR) and is an E9 rel32 thunk, so its rel32 gives the current address. Measured 2026-07-22:
    // e9 57 4f 1a 0d -> 0x14D39FFC0, which opens with a normal frame.
    //     sub rsp,0x28 / movsxd r10,ecx (arg1) / mov r11d,edx (arg2) / movzx ecx,r8w (arg3, 16-bit)
    // Arg order is inferred from the v1 header (id + 1, mode, slot), hence the canary below.
    private const long DispatchThunk = 0x1401FB064;   // fixed-image E9 thunk; target resolved per launch
    private static readonly byte[] DispatchPrologue = { 0x48, 0x83, 0xEC, 0x28, 0x4C, 0x63, 0xD1, 0x41, 0x89, 0xD3 };

    // x64 (rcx, rdx) per the disassembled prologue.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ApplyStatusesFn(int unitSlot, int mode);

    // v2 dispatch (id + 1, mode, slot). The callee narrows the third arg itself (movzx).
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DispatchStatusFn(int idPlusOne, int mode, int unitSlot);

    /// <summary>Resolve the dispatch routine for this launch by decoding the fixed-image thunk, then check
    /// its prologue. Returns 0 on any doubt, which the caller logs as a refusal.</summary>
    private long ResolveDispatch(string label)
    {
        if (!Mem.Readable(DispatchThunk, 5) || !_mem.TryReadBytes(DispatchThunk, 5, out var thunk))
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} dispatch thunk 0x{DispatchThunk:X} unreadable; refusing.");
            return 0;
        }
        if (thunk[0] != 0xE9)
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} dispatch thunk is not an E9 jmp (got {thunk[0]:X2}); refusing.");
            return 0;
        }
        int rel = thunk[1] | (thunk[2] << 8) | (thunk[3] << 16) | (thunk[4] << 24);
        long target = DispatchThunk + 5 + rel;
        if (!Mem.Readable(target, DispatchPrologue.Length) ||
            !_mem.TryReadBytes(target, DispatchPrologue.Length, out var got))
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: {label} resolved target 0x{target:X} is not readable; refusing.");
            return 0;
        }
        for (int i = 0; i < DispatchPrologue.Length; i++)
            if (got[i] != DispatchPrologue[i])
            {
                ModLogger.Error(LogVerb.Trace, $"status-spike: {label} resolved target 0x{target:X} prologue mismatch (expected {BitConverter.ToString(DispatchPrologue)}, got {BitConverter.ToString(got)}); refusing.");
                return 0;
            }
        ModLogger.Event(LogVerb.Trace, $"status-spike: {label} dispatch resolved to 0x{target:X} this launch (thunk rel {rel:X8}), prologue verified.");
        return target;
    }

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
    private readonly string? _requestPath;   // null when no modDir was supplied (tests); see RequestFileName
    private bool _f2Was, _f4Was;
    private int _hasteMode, _treasureMode;   // per-key sweeps 0 -> 1 -> 2 independently
    private int _requestMode;                // the request lane's own sweep, independent of the keys
    private int _argOrder;                   // dispatch arg permutation; see the switch in Fire
    private int _subject;                    // which unit identifier arg3 carries; see SubjectFor
    private int _hbTick;
    private bool _announced;

    /// <summary>Request lane (2026-07-22). Keys only fire their two hardcoded ids; the open questions now
    /// are frog (does the engine rebuild the model), invite and charm (merge team-colour bits, the
    /// Guest/Traitor allegiance wall) and the 23 ids whose raw writes gave an icon and nothing else.
    /// battle_toolbag.py writes this file because env vars do not reach the game and function keys are
    /// scarce (the fingerprint-guard drill uses the same marker-file lane).
    /// Format "statusId[,mode][,corpse][,orderN]", e.g. "22", "15,1,corpse", "28,0,order1"; orderN picks the
    /// dispatch arg permutation (0..4). The file is deleted on read so it cannot re-fire next launch. The
    /// target comes from FindEnemy; no seat is accepted because band seats and combat slots differ.</summary>
    private const string RequestFileName = "status_request.txt";

    public StatusSpike(IGameMemory mem, string? modDir = null)
    {
        _mem = mem;
        _requestPath = string.IsNullOrEmpty(modDir) ? null : System.IO.Path.Combine(modDir, RequestFileName);
    }

    /// <summary>In-battle loop tick. Heartbeat, key edges, request poll. <paramref name="inLive"/> is the
    /// genuine-live-battle predicate, not the debounced sticky state.</summary>
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
        if (_hbTick % 15 == 0) PollRequest();   // ~0.5s. Responsive without hammering the disk
    }

    /// <summary>Consume a queued request file (see RequestFileName) and fire it through the same path as
    /// the keys. A malformed file is deleted and logged, never guessed at.</summary>
    private void PollRequest()
    {
        if (_requestPath == null) return;
        string raw;
        try
        {
            if (!System.IO.File.Exists(_requestPath)) return;
            raw = System.IO.File.ReadAllText(_requestPath).Trim();
            System.IO.File.Delete(_requestPath);   // delete FIRST. A request must never fire twice
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: could not read/delete the request file: {ex.Message}");
            return;
        }
        var parts = raw.Split(',');
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out int statusId) || statusId < 0 || statusId > 39)
        {
            ModLogger.Error(LogVerb.Trace, $"status-spike: request '{raw}' is not a status id in 0..39; ignored.");
            return;
        }
        bool corpse = parts.Length > 2 && parts[2].Trim().Equals("corpse", StringComparison.OrdinalIgnoreCase);
        _argOrder = 0;
        _subject = 2;   // default to nameId. The decode says arg3 is a 16-bit handle, and slot already failed
        foreach (var seg in parts)
        {
            string t = seg.Trim();
            if (t.StartsWith("order", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(t.Substring(5), out int ord) && ord >= 0 && ord <= 4)
                _argOrder = ord;
            if (t.StartsWith("subj", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(t.Substring(4), out int sub) && sub >= 0 && sub <= 2)
                _subject = sub;
        }
        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int modeArg) && modeArg >= 0 && modeArg <= 2)
            _requestMode = modeArg;   // explicit mode overrides the sweep for this shot
        ModLogger.Event(LogVerb.Trace, $"status-spike: request '{raw}' accepted (status {statusId}, mode {_requestMode}, argOrder {_argOrder}, {(corpse ? "corpse" : "live")} target).");
        Fire($"REQUEST[{statusId}]", statusId, corpse, ref _requestMode);
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

    /// <summary>Checks the v1 target is mapped this launch and still has the expected prologue, since it
    /// lies above the always-mapped image. Failure is a logged refusal instead of a crash.</summary>
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

    /// <summary>First enemy-side band seat that maps to a callable engine slot, is sane, matches the
    /// alive/corpse filter and does not already carry the status. Prefers a real-position entry over a
    /// frozen (0,0) mirror twin (Band.ActiveOwner's rule). -1 if none.</summary>
    private int FindEnemy(bool corpse, int by, byte mask)
    {
        int twin = -1;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            if (s >= Offsets.BandSlots / 2) break;             // enemy side only
            if (!StatusApply.IsCallableSeat(s)) continue;       // AV guard. Engine slot in range
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

    /// <summary>Which identifier the dispatch's third argument wants (2026-07-22 decode). It is narrowed to
    /// 16 bits (movzx ecx, r8w) and used in a lookup at 0x140260ABC whose null result aborts the routine,
    /// matching our four clean no-op fires. A nameId fits 16 bits better than a slot, but that is a guess,
    /// so all three are selectable. 0 = engine slot (seat, 8), 1 = band seat, 2 = nameId.</summary>
    private int SubjectFor(int seat, int engineSlot)
    {
        switch (_subject)
        {
            case 1: return seat;
            case 2: return _mem.U16(Band.Entry(seat) + Offsets.ANameId);
            default: return engineSlot;
        }
    }

    // Safety. An engine AV is uncatchable, so refusals come first. The v1 target needs a readable
    // address plus a prologue match, IsCallableSeat keeps the slot in range, and we fire only while
    // paused since the routine sets process-shared unit-base globals. A per-frame compose race remains
    // and is accepted for a keyed single call. Fire only in a genuine inLive frame with the game
    // window foreground. If nothing applies across modes, v3 would call the VM dispatch directly.
    // Each key has its own mode counter cycling 0 -> 1 -> 2; read livingweapon.log for APPLIED=.
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
        // v2 first (runtime-resolved dispatch); the pinned v1 engine is a fallback when its landmark still matches.
        long dispatch = ResolveDispatch(label);
        if (dispatch == 0 && !TargetReady(label)) return;   // neither door verified. Refuse

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
        // The pending write is a v1 requirement. v2 names the id directly, so it is redundant there
        // and kept so both paths compare on one tape.
        _mem.WriteBytes(pend, new[] { (byte)(p | mask) });   // synchronous pending-ADD request
        string via = dispatch != 0 ? $"v2 dispatch 0x{dispatch:X} (id+1={statusId + 1}, mode={mode}, subj={SubjectFor(seat, engineSlot)} [kind {_subject}], order {_argOrder})"
                                   : $"v1 apply 0x{FnApplyStatuses:X} (slot={engineSlot}, mode={mode})";
        ModLogger.Event(LogVerb.Trace,
            $"status-spike: {label} seat {seat} pos ({gx},{gy}) engineSlot {engineSlot} status {statusId} mode {mode}; pending {p:X2}->{(byte)(p | mask):X2}, composed {compBefore:X2} inflicted {inflBefore:X2}; COLD-CALLING {via}...");
        try
        {
            if (dispatch != 0)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<DispatchStatusFn>(unchecked((nint)dispatch));
                // Arg order is inferred, so it is a knob. One deploy tries every permutation. Order 0 is the
                // v1 header's reading (id+1, mode, slot).
                int subj = SubjectFor(seat, engineSlot);
                switch (_argOrder)
                {
                    case 1: fn(subj, mode, statusId + 1); break;
                    case 2: fn(subj, statusId + 1, mode); break;
                    case 3: fn(statusId + 1, subj, mode); break;
                    case 4: fn(mode, statusId + 1, subj); break;
                    default: fn(statusId + 1, mode, subj); break;   // the decoded order
                }
            }
            else
            {
                var apply = Marshal.GetDelegateForFunctionPointer<ApplyStatusesFn>(unchecked((nint)FnApplyStatuses));
                apply(engineSlot, mode);
            }
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
