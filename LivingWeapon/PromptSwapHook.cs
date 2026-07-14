using System;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// The NATIVE half of the facing-prompt swap (untestable by construction, mirrors the retired CalloutHook's
/// posture): installs the SetTextString detour and marshals the pinned-buffer swap. PromptSwap
/// owns every decision; this class only hooks, decides nothing, and always forwards to the
/// original, so a managed fault here can never swallow the game's own prompt.
///
/// Split out of PromptSwap.cs (2026-07-14) alongside the 1.5.1 FnSetTextString correction: the two
/// classes were already logically separate (testable decision core vs. untestable native shell),
/// so giving the native half its own file is a real seam, not an evasion of the 200-line trigger.
/// </summary>
internal sealed class PromptSwapHook
{
    // The orchestrator's token-string setter (docs/research/CALLOUT_BANNER_JOURNEY.md, "v10, the
    // COMMIT TAP"): v10 (06:00) found the facing prompt riding this setter, args (holder, char*
    // text, r8, r9); r8/r9's true arity is unproven but forwarding them verbatim is free (the
    // ShowSpike OrchFn precedent). v11 (06:14, eyewitness + log) proved swapping rdx to a pinned
    // ANSI buffer here renders in the prompt slot and survives until the player confirms facing.
    //
    // 1.5.1 RE-ANCHOR (live-diagnosed 2026-07-14, docs/research/PORT_1.5.1_OFFSETS.md): the 1.5
    // address above (0x14028F79C) is a MID-FUNCTION branch target on 1.5.1 (the patch shifted this
    // code region by -0x4C), so installing the detour there corrupted the function and crashed the
    // game on the first battle-prompt render (reproduced twice, both on engaging auto-battle). The
    // then-corrected entry, 0x14028F750, was read live from the running process and looked
    // address-plausible (preceded by a ret + CC padding at 0x14028F74D, the previous function's
    // tail, and opening with a clean prologue), but it was semantically wrong: live disassembly
    // (LW-89 step 2, tools/probes/disasm.py, 2026-07-14) proved 0x14028F750 is a DISPATCH WRAPPER
    // whose real signature is (holder in rcx, flag byte in dl); the pointer this class was calling
    // "the text pointer" in rdx is caller garbage at that entry, which is exactly why every sampled
    // prompt head was garbage or off and no toast ever delivered on 1.5.1 (the LW-89 sampler that
    // motivated this probe).
    //
    // The wrapper resolves the real text from a std::string OBJECT embedded at holder+0x20 (size
    // at +0x30, capacity at +0x38; inline text at +0x20 when capacity is below 0x10, else a heap
    // pointer stored at [holder+0x20]) and BOTH of its branches converge on the TRUE text setter
    // at 0x1403F1098 with the RESOLVED char* in rdx (confirmed by the setter's own second
    // instruction block doing mov rbx, rdx and treating it as the data pointer; the wrapper's
    // variant branch at 0x1403F1068 also ends with jmp 0x1403F1098 after resolving its own rdx).
    // That resolved-text register contract is exactly what the pre-1.5 proven swap (v10/v11 above)
    // rode, so retargeting the hook here to the true setter revives the proven mechanism unchanged
    // instead of reinventing it. FnSetTextString below is the true setter; the wrapper was the
    // impostor. Arm() verifies ExpectedPrologue live before installing (the BodyDoubleSpike.
    // TargetReady cold-call doctrine this class had been missing, via the portable HookLandmark
    // core), so a future shift refuses with one logged warning instead of corrupting the function
    // again.
    private const long FnSetTextString = 0x1403F1098;

    // The 11-byte prologue read live at FnSetTextString (the true setter) on 1.5.1, 2026-07-14
    // (LW-89 step 2, see above): push rbp; push rbx; push rdi; mov rbp,rsp; sub rsp,50h. Checked
    // via HookLandmark.Verify in Arm() before every hook install.
    private static readonly byte[] ExpectedPrologue =
        { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };

    // MaxPayloadLen(96) + NUL: PromptSwap.TryPrepareSwap guarantees the payload it hands back is
    // already <= 96 chars, so this only needs to hold that plus the terminator.
    private const int PinnedBufLen = 97;

    [Function(CallingConventions.Microsoft)]
    private delegate void SetTextStringFn(nint holder, nint text, nint r8, nint r9);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly PromptSwap _swap;
    private readonly IGameMemory _mem;
    private readonly GCHandle _payloadPin;    // one pinned buffer, never freed (prompts are modal)
    private readonly byte[] _payloadBuf = new byte[PinnedBufLen];
    private IHook<SetTextStringFn>? _hook;
    private SetTextStringFn? _keepalive;      // GC anchor (the native thunk must outlive us)
    private bool _canary;

    public PromptSwapHook(PromptSwap swap, IGameMemory mem)
    {
        _swap = swap;
        _mem = mem;
        _payloadPin = GCHandle.Alloc(_payloadBuf, GCHandleType.Pinned);
    }

    /// <summary>Install the detour. Idempotent; called from Engine.InjectHooks (toasts are always
    /// on since LW-52 removed the BannerToasts toggle). This is now the only tap on the address
    /// (the ShowSpike dev instrument that used to stack a second hook here was removed in LW-67).
    /// BEFORE installing, verifies the target still carries ExpectedPrologue (the 1.5.1 incident:
    /// installing on a stale/wrong address corrupted the function and crashed the game); a
    /// mismatch or failed read refuses the install with one Warn line instead of hooking blind.</summary>
    public void Arm(IReloadedHooks hooks)
    {
        if (_hook != null) return;
        bool readOk = _mem.TryReadBytes(FnSetTextString, ExpectedPrologue.Length, out var prologue);
        if (!ShouldArm(readOk, prologue))
        {
            ModLogger.Warn(LogVerb.Toast,
                $"The prompt hook target 0x{FnSetTextString:X} does not match the expected prologue (game patched or wrong address); tier-up toasts are disabled this session.");
            return;
        }
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<SetTextStringFn>(_keepalive, FnSetTextString).Activate();
            ModLogger.Event(LogVerb.Toast, "Connected to the game's prompt system; tier-up toasts can now be delivered.");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Toast, "Could not connect to the game's prompt system; tier-up toasts will not be delivered: " + ex.Message);
        }
    }

    /// <summary>Pure decision: install only if the guarded read succeeded AND the prologue matches
    /// ExpectedPrologue exactly (HookLandmark.Verify's prefix-compare). Extracted so
    /// PromptSwapHookArmDecisionTests can exercise the composed refusal without touching
    /// IReloadedHooks (the Reloaded plumbing itself stays untested, as today).</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>ALWAYS forwards (holder, text, r8, r9) to the original, so a managed fault in
    /// TryPrepareSwap can never skip the game's own prompt commit.</summary>
    private void Detour(nint holder, nint text, nint r8, nint r9)
    {
        if (!_canary)
        {
            _canary = true;
            // Stays on console: the ABSENCE of this line is the dead-hook-launch tell
            // (Denuvo hook launch fragility).
            ModLogger.EventWithTrace(LogVerb.Toast,
                "The prompt hook is confirmed working; the game's first prompt this session was intercepted.",
                $"prompt-hook canary detail (thread {GetCurrentThreadId()})");
        }
        try
        {
            if (_swap.TryPrepareSwap((long)text, out var payload))
            {
                text = WritePinned(payload);
                ModLogger.EventWithTrace(LogVerb.Toast, $"Delivered the toast \"{payload}\".",
                    $"toast delivery detail (holder=0x{(long)holder:X})");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Toast, "An error occurred while preparing a toast (the original prompt still displayed normally): " + ex.Message);
        }
        _hook!.OriginalFunction(holder, text, r8, r9);
    }

    /// <summary>ASCII-encodes payload + NUL into the one pinned buffer allocated in the ctor
    /// (the ShowSpike _textPin/_promptPin pattern) and returns its address. Never re-pinned or
    /// freed (prompts are modal, one at a time), so a single buffer is safe.</summary>
    private nint WritePinned(string payload)
    {
        Array.Clear(_payloadBuf);
        int n = Math.Min(payload.Length, PinnedBufLen - 1);
        System.Text.Encoding.ASCII.GetBytes(payload, 0, n, _payloadBuf, 0);
        return _payloadPin.AddrOfPinnedObject();
    }
}
