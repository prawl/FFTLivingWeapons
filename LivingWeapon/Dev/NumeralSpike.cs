#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike: cold-call the engine's floating NUMBER-POPUP builder so the mod can put a
/// numeral on screen on command. Shipped effects are currently SILENT (Renewal heals adjacent
/// allies every turn edge and the player sees nothing, LIVE_LEDGER's Renewal row), and every
/// data-side lever for this has now been closed by measurement, not by guesswork:
///
///   - 0x1407832BE holds the last displayed number (damage AND heal, so it is the general value,
///     not a damage field). It has EXACTLY ONE reference in the whole 362 MB image: its own
///     write at 0x140228412. Nothing reads it. Write-only mirror, dead as a lever.
///   - The per-unit record is real (combat +0x1C4 u16 took 224 on a 224 hit, corroborated by HP
///     +0x30 falling 408 -> 184 and the outcome byte +0x1BB flipping to 0x02), and its neighbours
///     +0x1D8 / +0x1E5 latch alongside it. Writing all three on an idle unit drew NOTHING
///     (owner live 2026-07-27), so they are outputs too. The draw is event-driven, not polled.
///
/// So the remaining lever is the routine itself, which is the same place the spawn arc ended up.
///
/// TARGET: FnNumberPopup 0x140227CF8. Located by CE what-writes on 0x1407832BE (one hit,
/// 0x140228410 `mov [1407832BE],dx`), then static disasm outward: nearest int3 padding gives the
/// entry, and `callers` reports EXACTLY ONE call site, 0x140228CF5. Corroborating evidence that
/// this is the number builder and not a bystander: the sibling global 0x140808550 is written at
/// EIGHT distinct sites inside the same function (eight kinds of number), the function indexes
/// units via `lea rcx,[0x141853CE0]` + `shl rax,9` (BattleUnitsBase and its 0x200 stride), and it
/// expands a 4-byte entry at [r15 + r10*4] into four u16 slots at [r14+0xC/0xE/0x10/0x12], which
/// is the four digits.
///
/// THE ARGUMENTS ARE CRIBBED, NOT DERIVED. Owner CE breakpoint at the call site, one hit,
/// 2026-07-27:
///     rcx = 0x142FF5498   presentation object (the call site computes it as rsi + 0x408)
///     rdx = 0x1407DC908   layout/style table (baked: 960/320/400 plus rects)
///     r8  = 0x1408018E8   static
///     r9  = 0x1407B3520   layout array + 0x40 (the caller walks it at 0xC stride)
/// All four are inside the fixed image (base 0x140000000, no ASLR), so unlike the spawn arc's
/// node builder this needs NO runtime pointer discovery. That is the whole reason this spike is
/// worth trying before anything more elaborate.
///
/// WHICH UNIT gets the number is NOT an argument: at the write instruction the function's base
/// pointer was combat slot 16 + 0x1BE, and 0x1C4 == that + 6, so the value is read out of a unit
/// the routine selects for itself. This spike therefore writes the target's +0x1C4 immediately
/// before calling and reports whether the number that appeared matches. If a numeral appears on
/// the WRONG unit, the selection global is the next thing to find (0x142FF3CF8 held a combat-slot
/// pointer at the earlier capture and is the first suspect); that is a useful result, not a
/// failure.
///
/// SAFETY (crash-capable: an internal engine AV is uncatchable in .NET, so prevention is the only
/// defense; this follows StatusSpike's hardening line for line):
///  - Landmark the prologue at the pinned address before EVERY fire. The bytes are the ones the
///    static disasm read at 0x140227CF8; a patched or moved routine becomes a logged refusal.
///  - Fire only on a genuine inLive frame, only while PAUSED (PauseFlag == 1), and only when the
///    game window is foreground, so a global key state cannot fire a cold call while alt-tabbed.
///  - Every cribbed argument is re-checked for readability at fire time. A stale one refuses.
///  - The target unit must pass a sanity gate (level and HP in range) before we write its +0x1C4,
///    and the original u16 is restored after the call whatever happens.
///
/// CANARY DISCIPLINE (denuvo-hook-launch-fragility): the FIRST press each launch proves the call
/// frame is alive and non-crashing. A first cold call of a game function can be dead for a whole
/// launch or can crash; a dead game is a restart, the standard spike tax. THROWAWAY SAVE ONLY.
///
/// USAGE: in a live battle, open a unit's menu so the game is paused, then press F8. F8 keeps
/// clear of the rest of the wired key map (F2/F4 StatusSpike, F5/Shift+F5 BodyDoubleSpike, F6
/// ProvokeSpike). A request-file lane (numeral_request.txt, "slot[,value]") exists for the
/// same reason StatusSpike has one: this box's function keys are a scarce, half-eaten resource
/// (F3 is eaten on this box) and environment variables do not survive the game's launch chain.
/// </summary>
internal sealed class NumeralSpike
{
    private const long FnNumberPopup = 0x140227CF8;

    /// <summary>The entry's prologue, read statically from the exe on disk 2026-07-27:
    /// mov [rsp+0x10],rbx; push rbp; push rsi; push rdi; push r12; push r13.</summary>
    private static readonly byte[] PopupPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55 };

    // Cribbed from a live call, owner CE breakpoint at 0x140228CF5, 2026-07-27. See the class doc.
    private const long ArgPresentation = 0x142FF5498;   // rcx
    private const long ArgLayoutTable  = 0x1407DC908;   // rdx
    private const long ArgStatic3      = 0x1408018E8;   // r8
    private const long ArgLayoutArray  = 0x1407B3520;   // r9

    /// <summary>Per-unit displayed-number field (combat-struct relative), found by the unitsweep
    /// diff on a known 224 hit and independently predicted by the disasm as [rbp+6] where rbp was
    /// the unit's base + 0x1BE.</summary>
    private const int CNumberValue = 0x1C4;

    // ---- v2 (2026-07-27, after fire #1 returned clean but drew nothing) ----
    //
    // Fire #1 exonerated the call frame (no crash) and static re-derivation exonerated r8 (the
    // caller never sets it before the call: a DEAD argument, leftover from the previous call's
    // internals -- so the real contract is rcx/rdx/r9). The two remaining suspects, each with an
    // opt-in request flag so the default stays the hardened v1 path:
    //
    //  "live"  -- fire WITHOUT the pause requirement. Natural popups never spawn while paused,
    //             so a paused fire may be built and dropped (or expire) before a frame renders
    //             it. Precedent for an unpaused loop-thread cold call drawing: ShowSpike's
    //             ColdSpawn. The reentrancy risk the pause gate existed for is accepted for an
    //             explicit opt-in experiment on a throwaway save.
    //  "actor" -- pre-set the SELECTION GLOBAL before the call and restore it after.
    //             FnNumberPopup derives its unit from [0x142FF3CF8] (disasm 0x140228504:
    //             mov rbp,[0x142FF3CF8]; at the live writer break rbp read 0x141855E9E ==
    //             combat slot 16 base + 0x1BE). Between actions that global is stale/null,
    //             which is exactly the old ledger wall's "transient argument read once at
    //             event time". The write is unit base + 0x1BE for the requested slot.
    private const long ActorGlobal = 0x142FF3CF8;
    private const int ActorBias = 0x1BE;   // the global stores unitBase + 0x1BE, not unitBase

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NumberPopupFn(long presentation, long layoutTable, long arg3, long layoutArray);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    // F8. Current wired key map: F2/F4 StatusSpike, F5/Shift+F5 BodyDoubleSpike, F6 ProvokeSpike
    // (LW-67 deleted the old Flavor/Header/AttackCard spikes that used to squat on F6).
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

    /// <summary>In-battle loop tick: heartbeat, key edge, request-file lane. The cold call runs on
    /// this loop thread, which ShowSpike.ColdSpawn already proved can draw.</summary>
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

    /// <summary>Consume a queued "slot[,value]" request and fire the identical hardened path. The
    /// file is deleted BEFORE it is acted on, so a stale request can never re-fire on a later
    /// launch, and a malformed one is reported rather than guessed at: the one thing worse than
    /// ignoring a request is cold-calling the engine with a number we invented.</summary>
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

    /// <summary>Every precondition that can be checked without calling. Each failure is a logged
    /// refusal, never a crash: the alternative is transferring control to a wrong address inside
    /// the game's own process.</summary>
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

    /// <summary>Write the target's number field, cold-call the popup builder, restore the field.
    /// The restore runs in a finally so a throwing call still leaves the unit as we found it;
    /// note that an engine-side access violation is uncatchable and no finally saves us from it,
    /// which is precisely why TargetReady refuses on every checkable doubt.</summary>
    private void Fire(string label, int slot, int value, bool live, bool actor)
    {
        if (!TargetReady(label, skipPause: live)) return;

        // StatusApply.BattleUnitsBase is the same slot-0 origin the popup routine itself uses
        // (its `lea rcx,[0x141853CE0]` + `shl rax,9`), which is NOT our band-seat convention.
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
                // Pre-set the selection global to this unit (the global stores unitBase + 0x1BE,
                // per the live writer break where rbp read slot-16 base + 0x1BE). Restored below
                // whatever happens, so a stale value never outlives the experiment.
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
            // unchecked: the address is a 64-bit constant above int range, and this process is
            // always x64 (net8.0-windows against a 64-bit game), so the nint cast is exact.
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
