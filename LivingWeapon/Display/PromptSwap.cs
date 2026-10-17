using System;

namespace LivingWeapon;

/// <summary>
/// Facing-prompt toast delivery, the testable decision core (no native calls). Matches the Wait-state
/// facing prompt by its EN-only leading text and, when BannerToast has a toast queued, returns its
/// payload for the hook to swap in. Anything else passes through, and the queue is drained only after
/// the prefix matches. The mechanism comes from ShowSpike.TapDetour (v10/v11, 2026-07-03,
/// docs/research/CALLOUT_BANNER_JOURNEY.md "The Wait-state pivot"), which hooks SetTextString, matches
/// "Select a facing" and swaps rdx to a pinned ANSI buffer.
///
/// On 1.5.1 (2026-07-14, docs/research/PORT_1.5.1_OFFSETS.md) the patch shifted the code by -0x4C, so the
/// 1.5 entry 0x14028F79C became mid-function and detouring it crashed the game. 0x14028F750 (after the
/// ret + CC padding at 0x14028F74D) looked like the new entry, but disassembly showed it is a dispatch
/// wrapper (holder in rcx, flag byte in dl), which is why the first head samples were garbage.
/// The wrapper reads a string object at holder+0x20 (size +0x30, capacity +0x38, inline text when
/// capacity is below 0x10, else a heap pointer at [holder+0x20]) and passes the char* in rdx to the
/// text setter at 0x1403F1098 (1.5.2 moved it +4 to 0x1403F109C, PromptSwapHook.FnSetTextString).
/// PromptSwapHook hooks that setter behind a HookLandmark.Verify prologue check. The 1.5.1 text
/// "Select a facing direction and press F to confirm." still matches FacingPromptPrefix.
/// TryPrepareSwap logs the first dozen unique prompt heads at Debug tier (the sampler), and
/// PromptSwap.Sampler.cs (split out past the 200-line trigger) dumps a window around textPtr.
internal sealed partial class PromptSwap
{
    // EN-only because the mod's text layer is EN (french-nxd override is walled, docs/LIVE_LEDGER.md);
    // a non-EN game never matches and shows the vanilla prompt.
    private const string FacingPromptPrefix = "Select a facing";

    // 16 bytes covers the 15-char prefix plus one, enough for a prefix compare.
    private const int HeadReadLen = 16;

    // Same clamp CalloutHook.NativeFire.Commit used for the retired piggyback delivery; both widgets
    // are nine-grids with the same text budget.
    private const int MaxPayloadLen = 96;

    private readonly BannerToast _toast;
    private readonly IGameMemory _mem;

    public PromptSwap(BannerToast toast, IGameMemory mem)
    {
        _toast = toast;
        _mem = mem;
    }

    /// <summary>True plus a payload iff textPtr starts with the facing prompt and a toast is queued; the
    /// toast is dequeued only then. False with an empty payload otherwise.</summary>
    public bool TryPrepareSwap(long textPtr, out string payload)
    {
        payload = "";
        if (textPtr == 0) return false;

        // Runs before the head decode so unreadable or undecodable commits are dumped
        // too (see SampleStruct, PromptSwap.Sampler.cs).
        SampleStruct(textPtr);

        if (!_mem.TryReadBytes(textPtr, HeadReadLen, out var head)) return false;
        if (!TryDecodeAscii(head, out var text)) return false;

        // Observer only; sampled before the prefix and queue checks so it sees every decodable head.
        SampleHead(text);

        if (!text.StartsWith(FacingPromptPrefix, StringComparison.Ordinal)) return false;
        if (!_toast.TryTake(out var toast)) return false;

        payload = toast.payload.Length > MaxPayloadLen
            ? toast.payload.Substring(0, MaxPayloadLen)
            : toast.payload;
        // Runs on the game's SetTextString thread via PromptSwapHook.Detour; Record is null-object-safe and cheap.
        Flight.Record("prompt", $"delivered weapon={toast.weaponId} tier={toast.tier} payload=\"{payload}\"");
        return true;
    }

    /// <summary>Decodes bytes up to the first NUL as ASCII. A non-printable byte before the NUL is a mismatch
    /// (real prompts start with plain ASCII per the v10 commit-tap capture, docs/research/CALLOUT_BANNER_JOURNEY.md).</summary>
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
