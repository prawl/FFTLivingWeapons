using System;

namespace LivingWeapon;

/// <summary>
/// The three save-edge detour bodies of <see cref="SaveEdgeHooks"/>, split out of SaveEdgeHooks.cs.
/// They also hold the AfterApply repair they share on the load path, split under the 200-line guideline. They run on the
/// game's thread, call the original first (except the template projection in SerializeCore, which
/// runs before it, see the base file's class doc) and never throw past their boundary.
/// </summary>
internal sealed partial class SaveEdgeHooks
{
    private nint DetourSerialize(nint rcx, nint rdx, nint r8, nint r9)
        => SerializeCore(() => _serialize!.OriginalFunction(rcx, rdx, r8, r9));

    /// <summary>Serialize-detour body with the native call injected so tests can drive it. The
    /// projection runs first in its own try (on a fault the game serializes the old chart blocks as
    /// before), then <paramref name="original"/> is called once outside any try, then the tracker work.</summary>
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
                _onSerialized?.Invoke();
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

    /// <summary>One canary per path, since it is unknown which restore routine a real load runs.</summary>
    internal void AfterApply(bool second)
    {
        try
        {
            var hdr = ReadHeader();
            if (hdr == null) return;   // the pointer did not resolve; otherwise every call takes an edge, which is sound because both routines overwrite the bag from the struct
            AfterApplyCore(hdr);
            if (second)
            {
                if (_canaryLoadB) return;
                _canaryLoadB = true;
                SafeLog($"The second restore routine (0x{Offsets.FnSaveApplyB:X}) fired for the first time this session and a load edge was taken from it; note which game action just happened.");
            }
            else
            {
                if (_canaryLoadA) return;
                _canaryLoadA = true;
                SafeLog($"The load-edge hook is confirmed working; the game's first save load this session was intercepted (load-apply 0x{Offsets.FnSaveApply:X}).");
            }
        }
        catch (Exception) { /* never reaches the game */ }
    }

    /// <summary>The repair and the edge, shared by both load routines and the flee rewind.</summary>
    private void AfterApplyCore(byte[] hdr)
    {
        // Adopt the old chart blocks the routine just restored from the save struct back onto the relocated page
        // before the bag and template replay, so the seat lands on a fresh page.
        if (_templates != null) TemplateSync.Adopt(_mem, _templates);
        // Repair first, publish the edge second, so counts and seats are right before a menu can draw.
        // The replay has its own try so a fault cannot suppress the edge published after it.
        try { _replayOnLoad?.Invoke(SaveEdgeTracker.KeyFromHeader(hdr)); }
        catch (Exception) { /* a replay fault must not cost the edge below */ }
        _tracker.OnApplied(hdr);
        _onApplied?.Invoke();
    }

    /// <summary>A flee or a Retry puts the pre battle snapshot back without entering either load routine. This takes the
    /// same edge from the snapshot's header and says so every time, since it is rare and the owner reads it.</summary>
    internal void AfterRewind(byte[] header)
    {
        try
        {
            string key = SaveEdgeTracker.KeyFromHeader(header);
            // The old chart blocks hold the snapshot time charts, so adopting them is right on both branches.
            if (_templates != null) TemplateSync.Adopt(_mem, _templates);
            if (!_hasRecord(key))
            {
                // A replay on an unknown key would fall to the first copy seed and zero the counts.
                SafeLog($"The battle was rewound to its pre battle save (key {key}) but this mod holds no bag counts for that save; the new weapons' counts will be re derived from the last total this mod captured.", warn: true);
                Flight.Record("rewind-restore", $"key={key} record=missing");
                return;
            }
            bool replayed = true;
            try { _replayOnLoad?.Invoke(key); }
            catch (Exception) { replayed = false; /* a replay fault must not cost the edge below */ }
            _tracker.OnApplied(header);
            SafeLog($"The battle was rewound to its pre battle save (key {key}); the new weapons' bag counts were put back with it.");
            Flight.Record("rewind-restore", $"key={key} record=found");
            if (replayed) _onRewound?.Invoke();
        }
        catch (Exception) { /* never reaches the game */ }
    }

    private static void SafeLog(string line, bool warn = false)
    {
        try { if (warn) ModLogger.Warn(LogVerb.Save, line); else ModLogger.Event(LogVerb.Save, line); } catch (Exception) { }
    }
}
