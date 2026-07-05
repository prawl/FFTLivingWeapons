namespace LivingWeapon;

/// <summary>
/// Puppeteer's possession-hold lifecycle: re-assert the agency bit every tick (Drive), release it on
/// the wielder's own next turn (Expire), and the guarded revert (Release) + copy-moved/freed guard
/// (Valid) both share. The latch-acquisition half (Tick/Evaluate, the detection + arming gate) lives
/// in Puppeteer.cs -- a real seam: this half only ever touches an ALREADY-puppeted victim, the other
/// half only ever decides whether to START puppeting one.
/// </summary>
internal sealed partial class Puppeteer
{
    /// <summary>The fingerprint of the active puppet, or null (test/inspection hook).</summary>
    internal (int mhp, int lvl, int br, int fa)? PuppetFingerprint => _state.Fingerprint;

    /// <summary>Hold the agency bit SET each tick (beats the engine re-deriving it). The expiry clock
    /// now rides the WIELDER's own turn count via TurnTracker -- the puppet taking its turn no longer
    /// advances the expiry (so Drive no longer needs to observe the turn-queue hand-off).</summary>
    private void Drive()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (!Valid(addr, fp)) { _state.Release(); return; }   // copy moved/freed -> drop (cooldown stays)
        SetAgency(_mem, addr, true);
    }

    /// <summary>Release when the GALEWIND WIELDER takes its next turn (its own TurnTracker clock
    /// advances past the captured baseline). If the wielder was unresolved at dominate, fall back
    /// to a GlobalTurns threshold. Either way the puppet gets a full move+act+wait: the wielder's
    /// clock only advances on the WIELDER's acted edge, never on the puppet's.</summary>
    private void Expire(int puppetTurns)
    {
        if (!_state.HasPuppet) return;
        int wTurns = _state.WielderFp is { } w ? _turns.Turns(w.lvl, w.br, w.fa) : 0;
        if (!_state.IsExpired(wTurns, _turns.GlobalTurns, puppetTurns, Tuning.PuppeteerWielderlessFallbackTurns)) return;
        Release();
        ModLogger.Event(LogVerb.Signature, "Puppet control ended on the wielder's next turn; the enemy reverts to its own side.");
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
