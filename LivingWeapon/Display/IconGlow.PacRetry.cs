using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>Keeps a failed pac mirror queued for a few later passes. A failed write can still
/// have landed, so a retry also searches for the bytes the earlier try was writing.</summary>
internal sealed partial class IconGlow
{
    internal const int PacRetryCap = 5;

    // Attempts counts failed tries so far and OldBytes is the art the pac held before the first one.
    private sealed record PacRetryRecord(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget, int Attempts);

    private readonly Dictionary<(int Id, string Surface), PacRetryRecord> _pacRetry = new();

    internal int PendingPacRetries { get { lock (_lock) return _pacRetry.Count; } }

    /// <summary>Decides what a fresh write does to a pending retry for the same surface.</summary>
    private (bool Queue, byte[] OldBytes, byte[]? PriorTarget) ResolveAgainstRetry(int id, string surface, byte[] oldBytes, byte[] fresh)
    {
        lock (_lock)
        {
            if (!_pacRetry.TryGetValue((id, surface), out var r)) return (true, oldBytes, null);
            if (r.NewBytes.SequenceEqual(fresh)) return (false, oldBytes, null);
            _pacRetry.Remove((id, surface));
            return (true, r.OldBytes, r.NewBytes);
        }
    }

    private void DropRetryUnreadable(int id, string surface)
    {
        bool had;
        lock (_lock) had = _pacRetry.Remove((id, surface));
        if (had)
            ModLogger.Debug(LogVerb.Display, $"IconGlow dropped the pending pac retry for icon {id} ({surface}); the deployed icon file could not be read.");
    }

    /// <summary>Re queues every failed mirror, leaving its record for the attempt count.</summary>
    private void RequeuePacRetries()
    {
        lock (_lock)
            foreach (var (key, r) in _pacRetry)
                _pendingPacPatches.Add((key.Id, r.Entry, r.OldBytes, r.NewBytes, r.PriorTarget));
    }

    private void RunPacRetriesOnly()
    {
        try
        {
            _pacPatchedThisApply = 0;
            _pacAlreadyCurrentThisApply = 0;
            _hidesThisApply = 0;
            _pendingPacPatches.Clear();
            _pacPatchedThisPass.Clear();
            FlushPacBatch();
            if (_pacPatchedThisApply > 0 || _hidesThisApply > 0)
                ModLogger.Debug(LogVerb.Display, $"IconGlow: pac retry, {_pacPatchedThisApply} pac-patched, hide: {_hidesThisApply} surface(s) hidden.");
        }
        catch (Exception)
        {
            GiveUpOnThrowingPass();
        }
        finally
        {
            lock (_lock) _applying = false;
        }
    }

    private void GiveUpOnThrowingPass()
    {
        var gaveUp = new List<(int Id, string Surface)>();
        int kept;
        lock (_lock)
        {
            foreach (var pair in _pacRetry.ToList())
                if (BumpRetry(pair.Key, pair.Value) > PacRetryCap) gaveUp.Add(pair.Key);
            kept = _pacRetry.Count;
        }
        foreach (var (id, surface) in gaveUp)
            WarnPacOnce(id, $"IconGlow could not mirror icon {id} ({surface}) into modded.pac this session; the retry pass kept failing ({PacRetryCap + 1} tries); it will still show correctly after a restart.");
        ModLogger.Debug(LogVerb.Display, $"IconGlow pac retry threw; {kept} mirror(s) stay queued and {gaveUp.Count} given up.");
    }

    // Caller holds _lock. Returns the new attempt count and drops the record once it passes the cap.
    private int BumpRetry((int Id, string Surface) key, PacRetryRecord r)
    {
        int attempts = r.Attempts + 1;
        if (attempts <= PacRetryCap) _pacRetry[key] = r with { Attempts = attempts };
        else _pacRetry.Remove(key);
        return attempts;
    }

    private PacRetryRecord? TakeRetry(int id, string surface)
    {
        lock (_lock) return _pacRetry.Remove((id, surface), out var r) ? r : null;
    }

    private void CountUnavailableRetry(int id, string surface)
    {
        lock (_lock)
            if (_pacRetry.TryGetValue((id, surface), out var waiting)) BumpRetry((id, surface), waiting);
    }

    private void RoutePacFailed(int id, IconGlowEntry entry, byte[] oldBytes, byte[] newBytes, byte[]? priorTarget)
    {
        string detail = LastPacDetail(id, entry.Surface);
        int attempts;
        lock (_lock)
        {
            var key = (id, entry.Surface);
            var record = _pacRetry.TryGetValue(key, out var prior) ? prior : new PacRetryRecord(entry, oldBytes, newBytes, priorTarget, 0);
            attempts = BumpRetry(key, record);
        }
        if (attempts <= PacRetryCap)
            ModLogger.Debug(LogVerb.Display,
                $"IconGlow could not mirror icon {id} ({entry.Surface}) into modded.pac ({detail}); retrying on a later pass ({PacRetryCap - attempts + 1} retries left).");
        else
            WarnPacOnce(id, $"IconGlow could not mirror icon {id} ({entry.Surface}) into modded.pac this session after {PacRetryCap + 1} tries ({detail}); it will still show correctly after a restart.");
    }

    private string LastPacDetail(int id, string surface) => _store.LastPacError(id, surface)?.Detail ?? "no detail";
}
