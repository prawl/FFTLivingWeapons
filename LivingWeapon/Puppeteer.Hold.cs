namespace LivingWeapon;

/// <summary>
/// Puppeteer's possession-hold lifecycle: re-assert the agency bit every tick (Drive), release it
/// after the puppet takes its OWN turn (Expire), and the guarded revert (Release) + copy-moved/freed
/// guard (Valid) both share. The latch-acquisition half (Tick/Evaluate, the detection + arming gate)
/// lives in Puppeteer.cs -- a real seam: this half only ever touches an ALREADY-puppeted victim, the
/// other half only ever decides whether to START puppeting one.
/// </summary>
internal sealed partial class Puppeteer
{
    /// <summary>The fingerprint of the active puppet, or null (test/inspection hook).</summary>
    internal (int mhp, int lvl, int br, int fa)? PuppetFingerprint => _state.Fingerprint;

    /// <summary>Hold the agency bit SET each tick (beats the engine re-deriving it). Release timing is
    /// Expire's job; Drive only keeps the puppet under player control and drops it if the seat's copy
    /// moved or was freed.</summary>
    private void Drive()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (!Valid(addr, fp)) { _recorder?.Invoke("pup", $"release reason=seat-invalid seat=0x{addr:X} gturn={_turns.GlobalTurns}"); _state.Release(); return; }   // copy moved/freed -> drop (cooldown stays)
        SetAgency(_mem, addr, true);
    }

    /// <summary>Release after the puppet takes its OWN <paramref name="puppetTurns"/> turn(s). An own
    /// turn = the engine turn-owner queue names the puppet (QueueNamesPuppet) across an acted
    /// rising..falling edge: the rising edge opens the period, the falling edge completes it. This is
    /// LW-7-immune (the queue is read directly, not via the mis-crediting actor pointer) and it fixes
    /// BOTH failure modes of the old wielder-clock (2026-07-07 tapes: it released on the wrong unit's
    /// turn -- early when the puppet was not the next actor, late when it was fast). The GlobalTurns
    /// CAP (IsCapped) is the backstop should the queue signal never fire live -- the puppet is bounded
    /// to at most PuppeteerWielderlessFallbackTurns global turns, never to battle exit.</summary>
    private void Expire(int puppetTurns)
    {
        if (!_state.HasPuppet) return;

        // Edge-detect the puppet's own turn. Skip garbage acted reads (a transient 0xFF was observed
        // on tape) so they cannot fake a rising/falling edge. Preview-cycling of the queue carries no
        // acted pulse, so it can never open a period here.
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

    /// <summary>True when the engine turn-owner queue (Offsets.TurnQueue, the struct
    /// TurnTracker.TryActiveFingerprint matches) names the active puppet: its max HP and level equal
    /// the puppet's own fingerprint. Read directly, so it is immune to the LW-7 credit collapse that
    /// dumps every turn onto the wielder.</summary>
    private bool QueueNamesPuppet()
    {
        if (_state.Fingerprint is not { } p) return false;
        return _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp) == p.mhp
            && _mem.U16(Offsets.TurnQueue + Offsets.TqLevel) == p.lvl;
    }

    /// <summary>Clear the agency bit on the active puppet (revert to AI) and drop the latch. The cooldown
    /// clock is preserved. No-op when nothing is puppeted.</summary>
    private void Release()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (Valid(addr, fp)) SetAgency(_mem, addr, false);
        _state.Release();
    }

    private bool Valid(long b, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!_mem.Readable(b + Offsets.AMaxHp, 2)) return false;
        return _mem.U16(b + Offsets.AMaxHp) == fp.mhp && _mem.U8(b + Offsets.ALevel) == fp.lvl
            && _mem.U8(b + Offsets.ABrave) == fp.br && _mem.U8(b + Offsets.AFaith) == fp.fa;
    }
}
