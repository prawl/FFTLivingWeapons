using System;

namespace LivingWeapon;

/// <summary>
/// The three detour bodies for <see cref="SaveEdgeHooks"/> and the AfterApply repair they share
/// on the load path -- the HOT-PATH half, split out of SaveEdgeHooks.cs under the 200-line house
/// guideline. Every one of these runs on the GAME's own thread, forwards to the original FIRST,
/// and never throws past its own boundary (see the base file's class doc for why the load path
/// alone writes memory here).
/// </summary>
internal sealed partial class SaveEdgeHooks
{
    private nint DetourSerialize(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _serialize!.OriginalFunction(rcx, rdx, r8, r9);
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
            // LW-351: repair FIRST, publish the edge SECOND. The routine just below us has this
            // instant finished overwriting the bag from the save file and restoring both menu
            // order templates out of the save struct, so the counts and the template seats both
            // belong here, before the game can draw a menu from either. Doing it in this order
            // also means the tick can never take the edge before the resolution it must re-use is
            // there (the tracker's own lock is the fence between the two threads).
            _replayOnLoad?.Invoke(SaveEdgeTracker.KeyFromHeader(hdr));
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
