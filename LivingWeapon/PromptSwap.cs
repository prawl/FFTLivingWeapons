using System;

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
///
/// 1.5.1 CORRECTION (live-diagnosed 2026-07-14, docs/research/PORT_1.5.1_OFFSETS.md): the address
/// above, 0x14028F79C, was that function's ENTRY on 1.5 but the patch shifted this code region by
/// -0x4C, so on 1.5.1 the same address is a MID-FUNCTION branch target, and installing the detour
/// there corrupted the function and crashed the game on the first battle-prompt render (reproduced
/// twice, both on engaging auto-battle). The then-corrected entry, 0x14028F750, was read live from the
/// running process: preceded by a ret + CC padding at 0x14028F74D (the previous function's tail)
/// and opening with a clean prologue (sub rsp,28h; mov rax,[rcx+10h]; mov r8b,dl), which looked
/// like the known shape but was not: LW-89 step 2's live disassembly later proved 0x14028F750 is a
/// DISPATCH WRAPPER (holder in rcx, a flag byte in dl), not the text setter, so the register this
/// paragraph called "the text pointer" was caller garbage at that entry. See the LW-89 paragraphs
/// below for the corrected entry. PromptSwapHook (its own file) carries the corrected address plus
/// a prologue landmark (HookLandmark.Verify) so a future shift refuses the install with one logged
/// warning instead of corrupting the function again. PromptSwapHook is the native half this class
/// is called from.
///
/// LW-89 SAMPLER (2026-07-14): the 1.5.1 port also broke this class's own prefix match: the live
/// facing-prompt text on 1.5.1 silently stopped matching FacingPromptPrefix, and a mismatch here
/// is silent by design (an ordinary vanilla-prompt no-op), so there was no way to see what the
/// new text actually is short of reading raw memory by hand. TryPrepareSwap now samples the
/// first dozen unique decodable prompt heads a session ever sees to the log at Debug tier
/// (file-only in production), so one manual turn end in-game is enough to read the live text
/// back out of livingweapon.log. In hindsight the samples were never going to decode cleanly at
/// 0x14028F750: every sampled head was garbage or off because the register being sampled was the
/// dispatch wrapper's flag byte, not a text pointer at all.
///
/// LW-89 STEP 1b (2026-07-14): the head sampler's own live capture pointed past a plain char*:
/// readable heads arrived missing their first character, others decoded as pointer-like garbage,
/// and the facing prompt never sampled at all, consistent with rdx addressing a string OBJECT
/// (the function's own first check, cmp qword ptr [rdx+18h],10h, is a small-buffer capacity
/// test) rather than the raw text. PromptSwap.Sampler.cs (the same class, split out as a real
/// seam once the sampling logic pushed this file past the 200-line trigger) adds a second,
/// independent bounded sampler that dumps a small window around textPtr (a look-behind, the raw
/// bytes, and a one-level pointer-chase) on every commit, decodable or not, so the object's real
/// layout can be read back out of one capture instead of guessed at.
///
/// LW-89 STEP 2 (2026-07-14): the sampler's object hypothesis was right, just addressed to the
/// wrong function. Live disassembly of the 0x14028F750 wrapper found the string object at
/// holder+0x20 (size at +0x30, capacity at +0x38; inline text at +0x20 when capacity is below
/// 0x10, else a heap pointer at [holder+0x20]), and both of the wrapper's branches resolve that
/// object to a real char* and hand it to the TRUE text setter at 0x1403F1098 in rdx. PromptSwapHook
/// now hooks 0x1403F1098 instead of the wrapper, so textPtr below is that resolved char*, exactly
/// the register contract the pre-1.5 proven swap (v10/v11 above) rode. The on-screen 1.5.1 wording,
/// "Select a facing direction and press F to confirm.", still STARTS WITH "Select a facing", so
/// FacingPromptPrefix did not need to change.
/// </summary>
internal sealed partial class PromptSwap
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

        // LW-89 step 1b: runs before the head read/decode below, independent of both, so an
        // unreadable or undecodable commit is dumped too (see SampleStruct's doc, in
        // PromptSwap.Sampler.cs: those are exactly the ones the head sampler can never see).
        SampleStruct(textPtr);

        if (!_mem.TryReadBytes(textPtr, HeadReadLen, out var head)) return false;
        if (!TryDecodeAscii(head, out var text)) return false;

        // Pure observer: sample BEFORE the prefix/queue checks so it sees every decodable head,
        // matched or not, and never changes any return-path behavior below.
        SampleHead(text);

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
