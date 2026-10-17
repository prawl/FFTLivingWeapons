namespace LivingWeapon;

/// <summary>
/// Puppeteer's possession-hold lifecycle. Drive re-asserts the agency bit each tick, Expire releases it after the
/// puppet's own turn, Release reverts it, Valid guards against a moved or freed copy. Detection and arming live in
/// Puppeteer.cs; this half only touches an already-puppeted victim.
/// </summary>
internal sealed partial class Puppeteer
{
    /// <summary>The fingerprint of the active puppet, or null (test/inspection hook).</summary>
    internal (int mhp, int lvl, int br, int fa)? PuppetFingerprint => _state.Fingerprint;

    /// <summary>Hold the agency bit set each tick. Drops the puppet if the seat's copy moved or was freed; release timing is Expire's.</summary>
    private void Drive()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (!Band.SameUnitAtExact(_mem, addr, fp)) { _recorder?.Invoke("pup", $"release reason=seat-invalid seat=0x{addr:X} gturn={_turns.GlobalTurns}"); _state.Release(); return; }   // copy moved/freed -> drop (cooldown stays)
        SetAgency(_mem, addr, true);
    }

    /// <summary>Release after the puppet takes its own <paramref name="puppetTurns"/> turn(s). An own turn is the turn-owner
    /// queue naming the puppet (QueueNamesPuppet) across an acted rising..falling edge. Reading the queue directly avoids
    /// the LW-7 mis-crediting actor pointer, which made the old wielder-clock release on the wrong unit (2026-07-07 tapes).
    /// IsCapped bounds the hold to PuppeteerWielderlessFallbackTurns global turns if the queue signal never fires.</summary>
    private void Expire(int puppetTurns)
    {
        if (!_state.HasPuppet) return;

        // Skip garbage acted reads (a transient 0xFF was seen on tape); queue preview-cycling carries no acted pulse.
        int rawActed = _mem.U8(Offsets.Acted);
        if (rawActed is 0 or 1)
        {
            bool acted = rawActed == 1;
            if (acted && !_pWasActed && QueueNamesPuppet()) _pTurnActive = true;              // its action began
            else if (!acted && _pWasActed && _pTurnActive) { _puppetOwnTurns++; _pTurnActive = false; }  // and ended
            _pWasActed = acted;
        }

        bool done = _puppetOwnTurns >= puppetTurns;
        bool capped = _state.IsCapped(_turns.GlobalTurns, Tuning.PuppeteerWielderlessFallbackTurns);
        if (!done && !capped) return;
        _recorder?.Invoke("pup", $"release reason={(done ? "own-turn" : "cap")} ownTurns={_puppetOwnTurns} gturn={_turns.GlobalTurns}");
        Release();
        ModLogger.Event(LogVerb.Signature, "Puppet control ended after the enemy took its turn; it reverts to its own side.");
    }

    /// <summary>True when the turn-owner queue (Offsets.TurnQueue, matched by TurnTracker.TryActiveFingerprint) names the
    /// active puppet by max HP and level. Immune to the LW-7 credit collapse.</summary>
    private bool QueueNamesPuppet()
    {
        if (_state.Fingerprint is not { } p) return false;
        return _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp) == p.mhp
            && _mem.U16(Offsets.TurnQueue + Offsets.TqLevel) == p.lvl;
    }

    /// <summary>Clear the agency bit on the active puppet (revert to AI) and drop the latch; the cooldown clock is kept.
    /// No-op if nothing is puppeted.</summary>
    private void Release()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (Band.SameUnitAtExact(_mem, addr, fp)) SetAgency(_mem, addr, false);
        _state.Release();
    }

    // The same-unit write-safety check lives in Band.SameUnitAtExact.
}
