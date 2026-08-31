using System;

namespace LivingWeapon;

/// <summary>
/// The three detour bodies for <see cref="SaveEdgeHooks"/> and the AfterApply repair they share
/// on the load path -- the HOT-PATH half, split out of SaveEdgeHooks.cs under the 200-line house
/// guideline. Every one of these runs on the GAME's own thread, forwards to the original FIRST
/// EXCEPT the LW-371 projection step below, which by design runs BEFORE it (see the base file's
/// class doc for why), and never throws past its own boundary.
/// </summary>
internal sealed partial class SaveEdgeHooks
{
    private nint DetourSerialize(nint rcx, nint rdx, nint r8, nint r9)
        => SerializeCore(() => _serialize!.OriginalFunction(rcx, rdx, r8, r9));

    /// <summary>The whole serialize-detour body with the native trampoline call injected (the
    /// InventoryResetHook.Process idiom, and a test seam: tests drive this directly the way
    /// AfterApply is already driven). LW-371: the projection runs FIRST, inside its own try -- a
    /// fault here skips it and the game serializes whatever the old chart blocks already hold
    /// (the pre-arc behavior, never worse) -- then <paramref name="original"/> is called exactly
    /// once, OUTSIDE every try, then the existing post-serialize tracker work runs in its own
    /// try.</summary>
    internal nint SerializeCore(Func<nint> original)
    {
        try { if (_templates != null) TemplateSync.Project(_mem, _templates); }
        catch (Exception) { /* a fault here must never reach the game; the old blocks stand as they are */ }

        nint ret = original();   // exactly once, never inside a try

        try
        {
            var hdr = ReadHeader();
            if (hdr != null)
            {
                _tracker.OnSerialized(hdr, ReadCounts());
                if (!_canarySave) { _canarySave = true; SafeLog("The save-edge hook is confirmed working; the game's first save this session was intercepted."); }
            }
        }
        catch (Exception) { /* a managed fault here must never reach the game */ }
        return ret;
    }

    private nint DetourApply(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _apply!.OriginalFunction(rcx, rdx, r8, r9);
        AfterApply(second: false);
        return ret;
    }

    private nint DetourApplyB(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _applyB!.OriginalFunction(rcx, rdx, r8, r9);
        AfterApply(second: true);
        return ret;
    }

    /// <summary>One canary per path: which of the two restore routines a real load runs through
    /// is still unread, so the log has to name the one that actually fired.</summary>
    internal void AfterApply(bool second)
    {
        try
        {
            var hdr = ReadHeader();
            if (hdr == null) return;   // the pointer did not resolve; otherwise every call takes an edge, which is sound because both routines overwrite the bag from the struct
            // LW-371: adopt the old chart blocks the routine below us just restored from the save
            // struct back onto the relocated page (marker rule + wall, TemplateSync.Adopt) BEFORE
            // the bag/template replay below, so the seat that follows always lands on a fresh page.
            if (_templates != null) TemplateSync.Adopt(_mem, _templates);
            // LW-351: repair FIRST, publish the edge SECOND. The routine just below us has this
            // instant finished overwriting the bag from the save file and restoring both menu
            // order templates out of the save struct, so the counts and the template seats both
            // belong here, before the game can draw a menu from either. Doing it in this order
            // also means the tick can never take the edge before the resolution it must re-use is
            // there (the tracker's own lock is the fence between the two threads).
            // LW-351 stage 3 P4-F3: the replay runs in its own try so a fault in it can never
            // suppress the edge the line right after publishes.
            try { _replayOnLoad?.Invoke(SaveEdgeTracker.KeyFromHeader(hdr)); }
            catch (Exception) { /* a replay fault must not cost the edge below */ }
            _tracker.OnApplied(hdr);
            if (second)
            {
                if (_canaryLoadB) return;
                _canaryLoadB = true;
                SafeLog("The second restore routine (0x14021DE98) fired for the first time this session and a load edge was taken from it; note which game action just happened.");
            }
            else
            {
                if (_canaryLoadA) return;
                _canaryLoadA = true;
                SafeLog("The load-edge hook is confirmed working; the game's first save load this session was intercepted (load-apply 0x14021B0E8).");
            }
        }
        catch (Exception) { /* never reaches the game */ }
    }

    private static void SafeLog(string line)
    {
        try { ModLogger.Event(LogVerb.Save, line); } catch (Exception) { }
    }
}
