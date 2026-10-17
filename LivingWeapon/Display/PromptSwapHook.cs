using System;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Native half of the facing-prompt swap. Installs the SetTextString detour and swaps in the pinned buffer;
/// PromptSwap makes every decision. Always forwards to the original, so a managed fault cannot swallow the
/// game's prompt. Split from PromptSwap.cs 2026-07-14 alongside the 1.5.1 FnSetTextString correction.
/// </summary>
internal sealed class PromptSwapHook
{
    // The orchestrator's token-string setter (docs/research/CALLOUT_BANNER_JOURNEY.md, "v10, the COMMIT TAP"),
    // args (holder, char* text, r8, r9). v11 (06:14) showed that swapping rdx to a pinned ANSI buffer here
    // renders in the prompt slot until the player confirms facing. r8/r9 are forwarded verbatim, as ShowSpike OrchFn did.
    //
    // 1.5.1 re-anchor (2026-07-14, docs/research/PORT_1.5.1_OFFSETS.md): the 1.5 address 0x14028F79C became a
    // mid-function branch target (code shifted -0x4C) and the detour crashed the game on the first battle prompt.
    // The corrected entry 0x14028F750 looked plausible (ret + CC padding at 0x14028F74D, clean prologue), but
    // disassembly (LW-89 step 2, tools/probes/disasm.py, 2026-07-14) showed a dispatch wrapper taking
    // (holder in rcx, flag byte in dl), so rdx was caller garbage and no toast delivered on 1.5.1.
    //
    // The wrapper resolves the text from a std::string at holder+0x20 (size +0x30, capacity +0x38; inline when
    // capacity is below 0x10, else a heap pointer at [holder+0x20]). Both its branches converge on the true
    // setter at 0x1403F1098 with the resolved char* in rdx (the variant branch at 0x1403F1068 also ends in
    // jmp 0x1403F1098). Those are 1.5.1 addresses; on 1.5.2 the setter is 0x1403F109C and the variant branch
    // 0x1403F106C. Hooking the true setter restores the pre-1.5 swap (v10/v11). Arm() checks ExpectedPrologue
    // through HookLandmark first (BodyDoubleSpike.TargetReady cold-call check), so a future shift refuses with one warning.
    //
    // 1.5.2 re-anchor (2026-07-24): +4 from 0x1403F1098, the only address 1.5.2 moved. An offline exe diff
    // (tools/probes/exe_reanchor_scan.py, docs/research/PORT_1.5.2_OFFSETS.md) finds the same 11-byte prologue at
    // 0x1403F109C after the same `e9 01 00 00 00 cc` padding, once in 0x1403F0000..0x1403F3000 in both builds.
    // The body is 505/512 bytes identical and the 7 differing bytes are RIP displacement adjustments.
    // The old address now holds `00 00 00 cc` padding, so a blind hook there would corrupt it.
    private static long FnSetTextString => ImageBase.At(0x1403F109C);

    // The 11-byte prologue read at FnSetTextString on 1.5.1, 2026-07-14 (LW-89 step 2): push rbp; push rbx;
    // push rdi; mov rbp,rsp; sub rsp,50h. Unchanged on 1.5.2. Checked by HookLandmark.Verify in Arm().
    private static readonly byte[] ExpectedPrologue =
        { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };

    // MaxPayloadLen(96) + NUL; PromptSwap.TryPrepareSwap never hands back more than 96 chars.
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

    /// <summary>Install the detour. Idempotent; called from Engine.InjectHooks (toasts are on since LW-52).
    /// This is the only tap on the address (the ShowSpike instrument was removed in LW-67). Refuses with one
    /// Warn if the target does not carry ExpectedPrologue (the 1.5.1 crash) or the read fails.</summary>
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

    /// <summary>Install only if the guarded read succeeded and the prologue matches ExpectedPrologue
    /// (HookLandmark.Verify prefix-compare). Separate so PromptSwapHookArmDecisionTests can test it without IReloadedHooks.</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Always forwards (holder, text, r8, r9) to the original, even if TryPrepareSwap faults.</summary>
    private void Detour(nint holder, nint text, nint r8, nint r9)
    {
        if (!_canary)
        {
            _canary = true;
            // Stays on console: its absence marks a dead-hook launch (Denuvo hook launch fragility).
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

    /// <summary>ASCII-encodes payload + NUL into the single pinned buffer (the ShowSpike _textPin/_promptPin pattern)
    /// and returns its address. Prompts are modal, so one buffer is enough.</summary>
    private nint WritePinned(string payload)
    {
        Array.Clear(_payloadBuf);
        int n = Math.Min(payload.Length, PinnedBufLen - 1);
        System.Text.Encoding.ASCII.GetBytes(payload, 0, n, _payloadBuf, 0);
        return _payloadPin.AddrOfPinnedObject();
    }
}
