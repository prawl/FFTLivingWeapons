using System;

namespace LivingWeapon;

/// <summary>
/// What happens at the moment of a save, a load or a rewind, on the game's own thread.
/// Each body lets the game run first, except the chart copy before a save, and nothing they do may throw into the game.
/// </summary>
internal sealed partial class SaveEdgeHooks
{
    private nint DetourSerialize(nint rcx, nint rdx, nint r8, nint r9)
        => SerializeCore(() => _serialize!.OriginalFunction(rcx, rdx, r8, r9));

    /// <summary>The save detour's body, with the game's routine passed in so tests can drive it. Our charts are
    /// copied into the old blocks first, so the file saves them, and if that fails the game saves the old blocks as they were.</summary>
    internal nint SerializeCore(Func<nint> original)
    {
        try { if (_templates != null) TemplateSync.Project(_mem, _templates); }
        catch (Exception) { /* the old blocks are saved as they are */ }

        nint ret = original();   // once, and outside any try so a game exception reaches the game

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
        catch (Exception) { /* a managed fault here must never reach the game, and a missed save record only costs that save's counts */ }
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

    /// <summary>Each load routine logs its first run, since nobody knows yet which one a real load uses.</summary>
    internal void AfterApply(bool second)
    {
        try
        {
            var hdr = ReadHeader();
            if (hdr == null) return;   // both routines overwrite the bag from the struct, so every call that reads a header counts as a load
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
        catch (Exception) { /* nothing may throw into the game */ }
    }

    /// <summary>Shared by both load routines.</summary>
    private void AfterApplyCore(byte[] hdr)
    {
        // The game just restored the old chart blocks from the save, so copy them onto our page first and
        // the replay callback below works on the loaded charts.
        if (_templates != null) TemplateSync.Adopt(_mem, _templates);
        // Put the counts back before telling the tick about the load, so a menu never draws them wrong,
        // and give the replay its own try so a fault there still lets the load through.
        try { _replayOnLoad?.Invoke(SaveEdgeTracker.KeyFromHeader(hdr)); }
        catch (Exception) { /* a replay fault must not cost the edge below, the tick replays again when it sees the load */ }
        _tracker.OnApplied(hdr);
        _onApplied?.Invoke();
    }

    /// <summary>A flee puts the pre battle snapshot back without going through either load routine, so this treats
    /// it as a load of that snapshot. It logs every time because it is rare and worth reading.</summary>
    internal void AfterRewind(byte[] header)
    {
        try
        {
            string key = SaveEdgeTracker.KeyFromHeader(header);
            // The old blocks now hold the charts from the snapshot, which is right whether or not a record exists.
            if (_templates != null) TemplateSync.Adopt(_mem, _templates);
            if (!_hasRecord(key))
            {
                // Replaying a key we never saw would fall back to the starting counts and wipe the real ones.
                SafeLog($"The battle was rewound to its pre battle save (key {key}) but this mod holds no bag counts for that save; the new weapons' counts will be re derived from the last total this mod captured.", warn: true);
                Flight.Record("rewind-restore", $"key={key} record=missing");
                return;
            }
            bool replayed = true;
            try { _replayOnLoad?.Invoke(key); }
            catch (Exception) { replayed = false; /* a replay fault must not cost the edge below, the load still goes through to the tick */ }
            _tracker.OnApplied(header);
            SafeLog($"The battle was rewound to its pre battle save (key {key}); the new weapons' bag counts were put back with it.");
            Flight.Record("rewind-restore", $"key={key} record=found");
            if (replayed) _onRewound?.Invoke();
        }
        catch (Exception) { /* nothing may throw into the game */ }
    }

    private static void SafeLog(string line, bool warn = false)
    {
        try { if (warn) ModLogger.Warn(LogVerb.Save, line); else ModLogger.Event(LogVerb.Save, line); } catch (Exception) { }
    }
}
