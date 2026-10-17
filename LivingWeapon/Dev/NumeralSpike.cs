#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Dev-only spike that cold-calls FnNumberPopup (0x140227CF8) to put a numeral on screen on command; Renewal
/// heals currently show nothing (LIVE_LEDGER Renewal row). The data side is closed 0x1407832BE (last displayed
/// number) has one reference, its own write at 0x140228412, and the per-unit field combat +0x1C4 (HP +0x30 fell
/// 408 -> 184, outcome byte +0x1BB flipped to 0x02, +0x1D8/+0x1E5 latch with it) drew nothing when written on an
/// idle unit (2026-07-27), so those are outputs. Press F8 in a paused live battle, or queue numeral_request.txt
/// ("slot[,value]"). Throwaway save only; the first press each launch is the canary (denuvo-hook-launch-fragility).
/// </summary>
internal sealed class NumeralSpike
{
    // Found by CE what-writes on 0x1407832BE (one hit, 0x140228410 mov [1407832BE],dx), then static disasm outward from the
    // nearest int3 padding. Sole call site 0x140228CF5. Sibling global 0x140808550 is written at eight sites in it, it indexes
    // units via lea rcx,[0x141853CE0] + shl rax,9 (0x200 stride), and expands a 4-byte entry at [r15+r10*4] into four u16
    // digit slots at [r14+0xC/0xE/0x10/0x12].
    private const long FnNumberPopup = 0x140227CF8;

    /// <summary>The entry's prologue, read statically from the exe on disk 2026-07-27:
    /// mov [rsp+0x10],rbx; push rbp; push rsi; push rdi; push r12; push r13.</summary>
    private static readonly byte[] PopupPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55 };

    // Cribbed from an owner CE breakpoint at the call site 0x140228CF5 (one hit, 2026-07-27). rcx is rsi + 0x408 there,
    // rdx is the baked layout table (960/320/400 plus rects), r9 is the layout array + 0x40 (walked at 0xC stride).
    // All four sit in the fixed image, so no runtime pointer discovery is needed.
    private const long ArgPresentation = 0x142FF5498;   // rcx
    private const long ArgLayoutTable  = 0x1407DC908;   // rdx
    private const long ArgStatic3      = 0x1408018E8;   // r8
    private const long ArgLayoutArray  = 0x1407B3520;   // r9

    /// <summary>Per-unit displayed-number field (combat +0x1C4), found by the unitsweep diff on a known 224 hit;
    /// the disasm predicts [rbp+6] with rbp = unit base + 0x1BE.</summary>
    private const int CNumberValue = 0x1C4;

    // v2 (2026-07-27): fire #1 returned clean but drew nothing, and r8 is a dead argument (the caller never sets it),
    // so the live contract is rcx/rdx/r9. Two opt-in request flags test the remaining suspects:
    // "live" fires without the pause requirement, since natural popups never spawn while paused (same as ShowSpike ColdSpawn).
    // "actor" pre-sets the selection global FnNumberPopup reads (disasm 0x140228504. Mov rbp,[0x142FF3CF8]; at the writer
    // break rbp was 0x141855E9E, slot 16 base + 0x1BE) and restores it afterward.
    private const long ActorGlobal = 0x142FF3CF8;
    private const int ActorBias = 0x1BE;   // the global stores unitBase + 0x1BE, not unitBase

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NumberPopupFn(long presentation, long layoutTable, long arg3, long layoutArray);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    // F8 is clear of F2/F4 StatusSpike, F5/Shift+F5 BodyDoubleSpike and F6 ProvokeSpike (the old spikes on F6 are deleted).
    // The request-file lane exists because F3 is eaten on this box and env vars do not survive the game's launch chain.
    private const int VkF8 = 0x77;

    private const string RequestFileName = "numeral_request.txt";
    private const int DefaultSlot = 16;      // player range starts here; the earlier live capture used slot 16
    private const int DefaultValue = 9999;   // unmistakable on screen, and four digits exercises every glyph slot

    private readonly IGameMemory _mem;
    private readonly string? _requestPath;
    private bool _f8Was;
    private int _hbTick;
    private bool _announced;
    private int _fires;

    public NumeralSpike(IGameMemory mem, string? modDir = null)
    {
        _mem = mem;
        _requestPath = string.IsNullOrEmpty(modDir) ? null : System.IO.Path.Combine(modDir, RequestFileName);
    }

    /// <summary>In-battle loop tick. Heartbeat, key edge, request-file lane. The cold call runs on this
    /// loop thread, as ShowSpike.ColdSpawn does.</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Debug(LogVerb.Trace, "numeral-spike: armed (dev). Open a unit's menu to pause, then F8 to cold-call the number popup. FIRST press each launch is the canary. THROWAWAY SAVE ONLY.");
        }
        if (++_hbTick % 300 == 0)
            ModLogger.Debug(LogVerb.Trace, $"numeral-spike: alive (fires {_fires}, writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (!inLive) return;
        if (Pressed(VkF8, ref _f8Was)) Fire("KEY", DefaultSlot, DefaultValue, live: false, actor: false);
        if (_hbTick % 15 == 0) PollRequest();
    }

    /// <summary>Consume a queued "slot[,value]" request and fire. The file is deleted before it is acted on,
    /// so a stale request cannot re-fire on a later launch; a malformed one is logged and ignored.</summary>
    private void PollRequest()
    {
        if (_requestPath == null) return;
        string raw;
        try
        {
            if (!System.IO.File.Exists(_requestPath)) return;
            raw = System.IO.File.ReadAllText(_requestPath).Trim();
            System.IO.File.Delete(_requestPath);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: could not read/delete the request file: {ex.Message}");
            return;
        }
        var parts = raw.Split(',');
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out int slot) || slot < 0 || slot > 20)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: request '{raw}' is not a combat slot in 0..20; ignored.");
            return;
        }
        int value = DefaultValue;
        bool live = false, actor = false;
        for (int i = 1; i < parts.Length; i++)
        {
            string t = parts[i].Trim();
            if (int.TryParse(t, out int v) && v >= 0 && v <= 9999) value = v;
            else if (t.Equals("live", StringComparison.OrdinalIgnoreCase)) live = true;
            else if (t.Equals("actor", StringComparison.OrdinalIgnoreCase)) actor = true;
        }
        ModLogger.Event(LogVerb.Trace, $"numeral-spike: request '{raw}' accepted (slot {slot}, value {value}, live={live}, actor={actor}).");
        Fire($"REQUEST[{slot}]", slot, value, live, actor);
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was && GameIsForeground();
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

    /// <summary>Every precondition checkable without calling. Each failure is a logged refusal.
    /// Follows StatusSpike's hardening. Prologue landmark at the pinned address (bytes read statically at 0x140227CF8),
    /// pause required unless "live", and every cribbed argument re-checked for readability.</summary>
    private bool TargetReady(string label, bool skipPause)
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, the guard has not armed writes this session.");
            return false;
        }
        if (!skipPause && Mem.U8(Offsets.PauseFlag) != 1)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, the game is not paused (open a unit's menu first, or request the 'live' flag).");
            return false;
        }
        if (!Mem.Readable(FnNumberPopup, PopupPrologue.Length) ||
            !_mem.TryReadBytes(FnNumberPopup, PopupPrologue.Length, out var got))
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, 0x{FnNumberPopup:X} is not readable this launch.");
            return false;
        }
        for (int i = 0; i < PopupPrologue.Length; i++)
            if (got[i] != PopupPrologue[i])
            {
                ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, prologue mismatch at 0x{FnNumberPopup:X} (expected {BitConverter.ToString(PopupPrologue)}, got {BitConverter.ToString(got)}). Re-anchor before retrying.");
                return false;
            }
        foreach (var (name, addr) in new[]
                 {
                     ("presentation", ArgPresentation), ("layoutTable", ArgLayoutTable),
                     ("arg3", ArgStatic3), ("layoutArray", ArgLayoutArray),
                 })
        {
            if (!Mem.Readable(addr, 8))
            {
                ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, cribbed {name} argument 0x{addr:X} is not readable this launch.");
                return false;
            }
        }
        return true;
    }

    /// <summary>Write the target's number field, cold-call the popup builder, restore the field in a finally.
    /// The routine picks its own unit (no argument for it), so a numeral over the wrong unit points at the selection
    /// global 0x142FF3CF8. An engine-side AV is uncatchable, so TargetReady refuses on any doubt.</summary>
    private void Fire(string label, int slot, int value, bool live, bool actor)
    {
        if (!TargetReady(label, skipPause: live)) return;

        // Same slot-0 origin the popup routine uses (lea rcx,[0x141853CE0], shl rax,9), not our band-seat convention.
        long unit = StatusApply.BattleUnitsBase + (long)slot * Offsets.CombatStride;
        if (!Mem.Readable(unit, Offsets.CombatStride))
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, combat slot {slot} unreadable at 0x{unit:X}.");
            return;
        }
        int lvl = Mem.U8(unit + 0x29), hp = Mem.U16(unit + 0x30), mhp = Mem.U16(unit + 0x32);
        if (lvl < 1 || lvl > 99 || hp < 1 || hp > mhp || mhp > 9999)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} refused, combat slot {slot} is not a sane live unit (level {lvl}, hp {hp}/{mhp}).");
            return;
        }

        int original = Mem.U16(unit + CNumberValue);
        ulong originalActor = 0;
        bool actorWritten = false;
        _fires++;
        ModLogger.Event(LogVerb.Trace, $"numeral-spike: {label} firing #{_fires} on slot {slot} (level {lvl}, hp {hp}/{mhp}); +0x{CNumberValue:X} {original} -> {value}; live={live} actor={actor}; calling 0x{FnNumberPopup:X}.");
        try
        {
            Mem.W16(unit + CNumberValue, (ushort)value);
            if (actor)
            {
                // The global stores unitBase + 0x1BE (rbp at the writer break was slot 16 base + 0x1BE). Restored in finally.
                if (Mem.Readable(ActorGlobal, 8) && Mem.Writable(ActorGlobal, 8))
                {
                    originalActor = Mem.U64(ActorGlobal);
                    Mem.WriteBytes(ActorGlobal, BitConverter.GetBytes((ulong)(unit + ActorBias)));
                    actorWritten = true;
                    ModLogger.Event(LogVerb.Trace, $"numeral-spike: {label} actor global 0x{ActorGlobal:X}: 0x{originalActor:X} -> 0x{(ulong)(unit + ActorBias):X}.");
                }
                else
                {
                    ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} actor global 0x{ActorGlobal:X} not writable; firing without it.");
                }
            }
            // unchecked. The address is a 64-bit constant above int range; the process is x64, so the nint cast is exact.
            var fn = Marshal.GetDelegateForFunctionPointer<NumberPopupFn>(unchecked((nint)FnNumberPopup));
            fn(ArgPresentation, ArgLayoutTable, ArgStatic3, ArgLayoutArray);
            ModLogger.Event(LogVerb.Trace, $"numeral-spike: {label} returned without crashing. Look at the field: a numeral means this flag combination is the missing state; note WHICH unit it appeared over.");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"numeral-spike: {label} threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Mem.W16(unit + CNumberValue, (ushort)original);
            if (actorWritten)
                Mem.WriteBytes(ActorGlobal, BitConverter.GetBytes(originalActor));
        }
    }
}
#endif
