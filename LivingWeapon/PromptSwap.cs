using System;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Facing-prompt toast delivery -- the TESTABLE decision core (no native calls). Detects the
/// Wait-state facing prompt by its EN-only leading text and, when BannerToast has a toast queued,
/// hands back its payload for the hook to swap in; a vanilla prompt (mismatched text) or an empty
/// queue both pass through untouched. The dequeue only ever happens once the prefix has matched,
/// so a non-facing text commit can never silently drain the queue.
///
/// Mechanism is live-proven, not new: ShowSpike.TapDetour (v10/v11, 2026-07-03 06:14 eyewitness +
/// log, docs/research/CALLOUT_BANNER_JOURNEY.md "The Wait-state pivot") showed that hooking SetTextString
/// 0x14028F79C, prefix-matching the incoming text against "Select a facing", and swapping rdx to a
/// pinned plain-ANSI buffer renders in the prompt slot and STAYS until the player confirms facing
/// -- exactly the player-held, contention-free surface this class now drives in production.
/// PromptSwapHook (below) is the native half this class is called from.
/// </summary>
internal sealed class PromptSwap
{
    // EN-only by design: the mod's text layer is EN (french-nxd override is walled, see
    // docs/LIVE_LEDGER.md) -- a non-EN game simply never matches this prefix, so every prompt
    // renders vanilla.
    private const string FacingPromptPrefix = "Select a facing";

    // 16 bytes covers the whole prefix ("Select a facing" = 15 chars) plus one more -- enough for
    // an exact-prefix compare without reading the rest of the (possibly markup-tagged) sentence.
    private const int HeadReadLen = 16;

    // Same clamp CalloutHook.NativeFire.Commit used for the retired piggyback delivery -- the
    // bubble/prompt widgets are both nine-grids with the same practical text budget.
    private const int MaxPayloadLen = 96;

    private readonly BannerToast _toast;
    private readonly IGameMemory _mem;

    public PromptSwap(BannerToast toast, IGameMemory mem)
    {
        _toast = toast;
        _mem = mem;
    }

    /// <summary>True + a swap payload iff textPtr's leading bytes are the facing prompt AND a
    /// toast is queued -- the toast is dequeued ONLY in that case (a non-facing commit can never
    /// consume one). False (with an empty payload) on every other path: null/unreadable pointer,
    /// non-facing text, or an empty queue (vanilla fallback -- Patrick's spec).</summary>
    public bool TryPrepareSwap(long textPtr, out string payload)
    {
        payload = "";
        if (textPtr == 0) return false;
        if (!_mem.TryReadBytes(textPtr, HeadReadLen, out var head)) return false;
        if (!TryDecodeAscii(head, out var text)) return false;
        if (!text.StartsWith(FacingPromptPrefix, StringComparison.Ordinal)) return false;
        if (!_toast.TryTake(out var toast)) return false;

        payload = toast.payload.Length > MaxPayloadLen
            ? toast.payload.Substring(0, MaxPayloadLen)
            : toast.payload;
        // Static Flight.Record call (null-object-safe, trivially cheap -- this runs on the game's
        // own SetTextString thread via PromptSwapHook.Detour, so no lock contention beyond the
        // recorder's own cheap array-append).
        Flight.Record("prompt", $"delivered weapon={toast.weaponId} tier={toast.tier} payload=\"{payload}\"");
        return true;
    }

    /// <summary>Decodes bytes up to the first NUL as ASCII; any non-printable byte before the NUL
    /// is a hard mismatch (a real prompt's leading bytes are always plain markup-free ASCII per
    /// the v10 commit-tap capture, docs/research/CALLOUT_BANNER_JOURNEY.md) rather than a truncated match.</summary>
    private static bool TryDecodeAscii(byte[] bytes, out string text)
    {
        var chars = new char[bytes.Length];
        int n = 0;
        foreach (byte b in bytes)
        {
            if (b == 0) break;
            if (b is < 0x20 or > 0x7E) { text = ""; return false; }
            chars[n++] = (char)b;
        }
        text = new string(chars, 0, n);
        return true;
    }
}

/// <summary>
/// The NATIVE half of the facing-prompt swap (untestable by construction, mirrors the retired CalloutHook's
/// posture): installs the SetTextString detour and marshals the pinned-buffer swap. PromptSwap
/// owns every decision; this class only hooks, decides nothing, and always forwards to the
/// original -- a managed fault here can never swallow the game's own prompt.
/// </summary>
internal sealed class PromptSwapHook
{
    // The orchestrator's token-string setter (docs/research/CALLOUT_BANNER_JOURNEY.md "v10 -- the COMMIT
    // TAP"): v10 (06:00) found the facing prompt riding this setter, args (holder, char* text, r8,
    // r9) -- r8/r9's true arity is unproven but forwarding them verbatim is free (the ShowSpike
    // OrchFn precedent). v11 (06:14, eyewitness + log) proved swapping rdx to a pinned ANSI buffer
    // here renders in the prompt slot and survives until the player confirms facing.
    private const long FnSetTextString = 0x14028F79C;

    // MaxPayloadLen(96) + NUL -- PromptSwap.TryPrepareSwap guarantees the payload it hands back is
    // already <= 96 chars, so this only needs to hold that plus the terminator.
    private const int PinnedBufLen = 97;

    [Function(CallingConventions.Microsoft)]
    private delegate void SetTextStringFn(nint holder, nint text, nint r8, nint r9);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly PromptSwap _swap;
    private readonly GCHandle _payloadPin;    // one pinned buffer, never freed -- prompts are modal
    private readonly byte[] _payloadBuf = new byte[PinnedBufLen];
    private IHook<SetTextStringFn>? _hook;
    private SetTextStringFn? _keepalive;      // GC anchor -- the native thunk must outlive us
    private bool _canary;

    public PromptSwapHook(PromptSwap swap)
    {
        _swap = swap;
        _payloadPin = GCHandle.Alloc(_payloadBuf, GCHandleType.Pinned);
    }

    /// <summary>Install the detour. Idempotent; called from Engine.InjectHooks (toasts are always
    /// on since LW-52 removed the BannerToasts toggle). This is now the only tap on the address
    /// (the ShowSpike dev instrument that used to stack a second hook here was removed in LW-67).</summary>
    public void Arm(IReloadedHooks hooks)
    {
        if (_hook != null) return;
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

    /// <summary>ALWAYS forwards (holder, text, r8, r9) to the original -- a managed fault in
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
    /// freed -- prompts are modal (one at a time), so a single buffer is safe.</summary>
    private nint WritePinned(string payload)
    {
        Array.Clear(_payloadBuf);
        int n = Math.Min(payload.Length, PinnedBufLen - 1);
        System.Text.Encoding.ASCII.GetBytes(payload, 0, n, _payloadBuf, 0);
        return _payloadPin.AddrOfPinnedObject();
    }
}
