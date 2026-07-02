#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY SPIKE (compiled out of production entirely): manipulate the game's battle callout
/// banner -- the black bubble that names abilities over the acting unit -- from inside the mod.
///
/// v6 HIJACK MODE. v1-v5 (archived in memory living-weapon-levelup-banner) proved every link
/// except the show: four launch-stable banner objects (validated below each battle), in-process
/// calls into the game's own text setter work from this mod's 33ms thread with no crash, and a
/// committed payload verifiably reaches the LIVE render widget's string -- but forcing the
/// balloon's animation to play from scratch never rendered (the per-frame updater re-derives
/// the playing state from a timeline model we haven't cracked). So v6 stops forcing and RIDES:
/// poll the holder's show flag (holder+0x88 -- pulses 1 for the banner's ~1s life, proven by a
/// controller struct-diff during a natural cast) and on its rising edge re-commit OUR text via
/// the game's own SetTextStringAndCommit (0x14028F720). The natural path has already bound the
/// live inner widget, armed the animation, and started rendering -- the payload swaps in flight
/// within 1-2 ticks of the fade-in. Success = the on-screen bubble shows our text with no hook
/// installed, which would make the eventual tier-up toast a pure poll+call feature (no Denuvo
/// detour fragility at all).
///
/// THREADING: the setter call runs on the mod's loop thread while the UI thread is mid-show.
/// The race is an accepted spike risk (dev build only); v2-v5 made the same class of call
/// repeatedly without a crash. This is the one module that deliberately violates the
/// "guarded access only" rule by CALLING game code; that is why it is #if LWDEV.
/// </summary>
internal sealed class BannerSpike
{
    // Heap objects PROVEN LAUNCH-STABLE 2026-07-02 (validated by static vtables/ids each
    // battle before anything is dereferenced; 3+ relaunches held):
    private const long Holder = 0x436B07D058;    // callout controller+0x58: the text-holder widget
    private const long Widget = 0x436B6A6BE0;    // the NineGrid balloon frame widget
    private const long UnitUi = 0x436B4435A0;    // the balloon-owning unit-UI object
    private const long LiveInner = 0x436B800210; // live balloon text widget (show-time bind target)
    private const long HolderVtable = 0x140718278;
    private const ulong HolderId = 0x999;               // holder+0x08 (unique among 538 class instances)
    private const uint WidgetId = 0x001D0072;           // widget+0x08 (widget has NO vtable at +0x00)
    private const long WidgetDefaultStrPtr = 0x1406F9FA0;   // widget+0x10 static default-string ptr
    private const long UnitUiVtable = 0x140721F68;
    private const ulong UnitUiId = 0x9CE;
    private const long InnerVtable = 0x140721DA0;
    // The game's own "set literal text + commit to the inner widget" method (traced from the
    // banner orchestrator at 0x140111E89; call proven executed in-process by read-back).
    private const long FnSetTextCommit = 0x14028F720;   // rcx = holder, rdx = char* (ANSI)

    private const int ValidateAtTick = 300;   // ~10s at 33ms: battle visuals settled before first reads
    private const string Payload = "BUBBA LUVS +3";   // 13 chars: fits the holder's 15-char SSO buffer

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetTextCommitFn(nint holder, nint text);

    private int _tick;
    private bool _idsChecked;
    private bool _idsOk;
    private bool _showWasUp;
    private int _hijacks;

    /// <summary>Re-arm for the next battle (called from Engine's battle-edge reset).</summary>
    public void ResetBattle()
    {
        _tick = 0;
        _idsChecked = false;
        _idsOk = false;
        _showWasUp = false;
        _hijacks = 0;
    }

    /// <summary>One in-battle tick: validate identities once, then watch the show flag and
    /// hijack every natural banner's payload on its rising edge. Runs on EVERY in-battle tick,
    /// NOT gated on onField: the callout shows during the ability cast animation, when
    /// battleMode drops to 1 and onField reads false -- an onField-gated poll sleeps through
    /// the exact window the show flag pulses (v6's first live run missed every banner).</summary>
    public void Tick()
    {
        try
        {
            if (!_idsChecked)
            {
                if (++_tick < ValidateAtTick) return;
                _idsChecked = true;
                _idsOk = ValidateIdentities();
            }
            if (!_idsOk) return;

            bool showUp = Mem.U8(Holder + 0x88) == 1;
            if (showUp && !_showWasUp)
            {
                _hijacks++;
                HijackShow();
            }
            _showWasUp = showUp;
        }
        catch (Exception ex)
        {
            Log.Error("banner-spike: managed exception -- " + ex.Message);
        }
    }

    /// <summary>Identity validation on the stable anchors. Guarded reads; a moved object is
    /// reported and the spike goes inert for the battle -- nothing is ever blind-dereferenced.</summary>
    private static bool ValidateIdentities()
    {
        ulong hVt = Mem.U64(Holder);
        ulong hId = Mem.U64(Holder + 0x08);
        uint wId = Mem.U32(Widget + 0x08);
        ulong wStr = Mem.U64(Widget + 0x10);
        ulong uVt = Mem.U64(UnitUi);
        ulong uId = Mem.U64(UnitUi + 0x08);
        ulong iVt = Mem.U64(LiveInner);
        bool ok = hVt == HolderVtable && hId == HolderId
                  && wId == WidgetId && wStr == WidgetDefaultStrPtr
                  && uVt == UnitUiVtable && uId == UnitUiId
                  && iVt == InnerVtable;
        Log.Info($"banner-spike: identity check -- holder 0x{hVt:X}/0x{hId:X}, widget 0x{wId:X}/0x{wStr:X}, " +
                 $"unitUI 0x{uVt:X}/0x{uId:X}, inner 0x{iVt:X} => {(ok ? "ALL MATCH, hijack armed" : "MISMATCH, spike inert")}");
        return ok;
    }

    /// <summary>A natural banner just started: re-commit our payload through the game's own
    /// setter so every downstream copy (holder string, live inner widget, render buffers)
    /// carries our text for the rest of the show.</summary>
    private void HijackShow()
    {
        nint text = Marshal.StringToHGlobalAnsi(Payload);
        try
        {
            var setText = Marshal.GetDelegateForFunctionPointer<SetTextCommitFn>(unchecked((nint)FnSetTextCommit));
            setText(unchecked((nint)Holder), text);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
        int len = (int)Mem.U32(Holder + 0x30);
        Log.Info($"banner-spike: HIJACK #{_hijacks} -- show flag rose, re-committed payload " +
                 $"(holder len now {len}) -- is the bubble showing our text?");
    }
}
#endif
