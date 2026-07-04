using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wrathblade's "Feign Death" signature (+3): a lethal hit on the wielder becomes a comedy of playing
/// dead. The wielder flops as a corpse (the real death animation plays), then -- for ~2 turns -- acts
/// while prone and IGNORED by the AI, before the finishing blow drops and the engine's OWN Reraise
/// stands them back up, animated, at ~10% HP. ONCE per battle.
///
/// The lifecycle (FeignDeath.Policy.cs <see cref="Step"/> is the pure state machine; this half only
/// reads facts, calls Step, and applies the guarded writes):
///   Watching -- armed and alive, waiting for the lethal hit (HP 0 or the dead bit).
///   Possum   -- clear the dead bit + hold HP at 1 (prone but alive, so it still takes turns) and hold
///               Invisible (+0x47/0x10, re-stamped: it breaks the instant the unit acts) so the AI
///               skips it. Single-target enemies ignore it; AoE splash can still reach it. Lasts 2 of
///               the wielder's turns, counted off the active-unit struct (the band CT byte is too noisy
///               to count); FeignPossumSeconds is an idle-only safety cap.
///   Finish   -- keep playing dead (still prone + Invisible) until the wielder is UP NEXT in the queue,
///               THEN deal the finishing blow: drop Invisible, hold Reraise (+0x47/0x20), HP -> 0 AND
///               the dead bit set (the engine does NOT flag dead on a memory HP write, so Reraise needs
///               the bit). Striking only at up-next keeps the dead-and-scheduled window to a sliver --
///               killing earlier crashed the engine, killing mid-turn left a stuck dead-but-active turn.
///   Recover  -- the engine raised the wielder: drop Reraise, HOLD the dead/KO bit CLEARED for
///               FeignRecoverSeconds so the stand-up leaves no hearts / skipped turn, then spent.
///
/// PROVEN LIVE 2026-06-14 end to end in-game (real combat death, 2 turns acting while the AI ignores
/// it, animated stand-up at 10% HP with the KO state cleared). SYNERGY (why Wrathblade):
/// its damage = the wielder's MISSING HP (formula 67) -- reviving at ~10% leaves ~90% missing, loading
/// the blade to near-max. Reraise is applied ONLY at the finishing blow, never the whole battle, so
/// the status icon is not on show during play. Every read/write is guarded (Writable + W8/WriteBytes).
/// </summary>
internal sealed partial class FeignDeath : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);
    private const int WrathbladeId = 27;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private bool _wasActive;
    private bool _spent;          // once-per-battle: the played-dead cycle has run its course
    private bool _sawAlive;       // the wielder has been seen alive this battle (arm guard)
    private Phase _phase;
    private DateTime _possumStart;
    private DateTime _recoverStart;
    private bool _finishKilled;   // the finishing blow (HP -> 0) has been dealt
    private bool _finishWasDead;  // the finish death registered -> awaiting the revive edge
    // Wielder's OWN turns during possum, counted off the active-unit struct (TurnQueue): a turn ends
    // when the wielder stops being the active unit. (The band CT byte is too noisy to count reliably.)
    private bool _wasActiveWielder;   // was the wielder the engine's active unit last tick (turn-edge count)
    private int _possumTurnCount;     // wielder's completed turns this possum

    public FeignDeath(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _spent = false; _wasActive = false; _sawAlive = false;
        _phase = Phase.Watching;
        _finishKilled = false; _finishWasDead = false;
        _wasActiveWielder = false; _possumTurnCount = 0;
    }

    public void Tick(DateTime now, bool inLive)
    {
        if (!_meta.TryGetValue(WrathbladeId, out var m)) return;
        int tier = Tuning.TierOf(_kills, WrathbladeId);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier)
                      && Wielder.TryResolveMainHand(_mem, WrathbladeId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Log($"feign-death {(active ? "ACTIVE -- Wrathblade at +3 is wielded; a lethal hit becomes a played-dead corpse that acts for ~2 turns (ignored), then springs up at ~10% HP (once per battle)" : "inactive")}");
        }
        if (!active || !inLive) return;

        long e = Wielder.Locate(_mem, WrathbladeId, _hands, fp);
        if (e == 0) return;   // wielder's live entry not found this tick; resolved again next tick

        int hp = _mem.U16(e + Offsets.AHp);
        bool dead = (_mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
        if (hp > 0 && !dead) _sawAlive = true;

        // Once spent, the only thing to do is watch for a re-arm: a battle restart ("Retry") reloads
        // too fast for the debounced exit edge to reset us, but it puts the wielder back at full HP.
        if (_spent)
        {
            if (ShouldRearm(_spent, hp, _mem.U16(e + Offsets.AMaxHp), dead))
            {
                _phase = Phase.Watching; _spent = false; _finishKilled = false; _finishWasDead = false;
                ModLogger.Log("feign-death: the wielder is back at full HP (battle restart or full heal) -- the feign is re-armed");
            }
            else return;
        }

        // Count the wielder's completed turns off the ENGINE'S ACTIVE-UNIT struct (TurnQueue), NOT the
        // noisy +0x25 CT: a turn ends when the wielder stops being the active unit. The active struct
        // cleanly identifies who's acting; the CT byte was jumpy and undercounted, so the count kept
        // missing turns and falling to the 90s safety cap (the "6 turns" overrun). Wall clock = idle cap.
        bool possumDone = false;
        if (_phase == Phase.Possum)
        {
            bool nowActive = ActiveUnitIsWielder(e);
            if (TurnEnded(_wasActiveWielder, nowActive))
            {
                _possumTurnCount++;
                ModLogger.Log($"feign-death: played-dead turn {_possumTurnCount}/{Tuning.FeignPossumTurns}");
            }
            _wasActiveWielder = nowActive;
            possumDone = _possumTurnCount >= Tuning.FeignPossumTurns
                         || Elapsed(_possumStart, now, Tuning.FeignPossumSeconds);
        }
        bool recoverElapsed = _phase == Phase.Recover && Elapsed(_recoverStart, now, Tuning.FeignRecoverSeconds);
        // At the finishing-blow instant we need two facts (each read only here, once per feign):
        //   otherAllyAlive -- don't manufacture a party wipe by killing the last unit up.
        //   upNext         -- only strike once the wielder has climbed back to "up next" (high CT,
        //                     another unit still active). Killing it earlier left it dead-and-scheduled
        //                     for a long climb, which crashed the engine; striking at up-next means the
        //                     corpse exists only a sliver before its turn auto-raises it.
        bool atBlow = _phase == Phase.Finish && !_finishKilled;
        bool otherAllyAlive = !atBlow || OtherAllyAlive(e);
        bool upNext = !atBlow
                      || (!ActiveUnitIsWielder(e) && _mem.U8(e + Offsets.ACtSlam) >= Tuning.FeignUpNextCt);

        var act = Step(_phase, hp, dead, _sawAlive, possumDone, recoverElapsed, _finishKilled, _finishWasDead, otherAllyAlive, upNext);

        if (act.Invisible is bool inv) SetInvisible(_mem, e, inv);
        if (act.Reraise is bool rr) SetReraise(_mem, e, rr);
        if (act.Dead is bool dd) SetDead(_mem, e, dd);
        if (act.Hp == HpAction.ForceKill) ForceKill(_mem, e);
        else if (act.Hp == HpAction.HoldAlive) HoldAlive(_mem, e);

        if (act.MarkKilled) { _finishKilled = true; ModLogger.Log("feign-death: the wielder's turn is coming up -- ending the played-dead act now so its own turn revives it (a brief dead flag, not a real death)"); }
        if (act.MarkWasDead) _finishWasDead = true;

        if (act.Next != _phase) EnterPhase(act.Next, now, hp);
        if (act.Spent)
        {
            _spent = true;
            ModLogger.Log(_phase == Phase.Finish
                ? "feign-death: last party member standing -- skipped the finishing blow; the wielder survives the feign at 1 HP (no party wipe)"
                : "feign-death: the played-dead cycle is complete -- spent for this battle");
        }
    }

    /// <summary>True when the engine's CURRENT active unit (the condensed turn struct at TurnQueue) is
    /// the wielder -- matched by max-HP + level. Used to count the wielder's possum turns (a turn ends
    /// when it stops matching) and to gate the finishing blow to the "up next" window: we strike only
    /// while it is NOT active but at high CT, so the corpse is dead-and-scheduled for a sliver before
    /// its turn auto-raises it (killing it far from its turn crashed the engine; mid-turn left a stuck
    /// dead-but-active turn).</summary>
    private bool ActiveUnitIsWielder(long wielder)
    {
        int wmhp = _mem.U16(wielder + Offsets.AMaxHp), wlvl = _mem.U8(wielder + Offsets.ALevel);
        return _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp) == wmhp
            && _mem.U16(Offsets.TurnQueue + Offsets.TqLevel) == wlvl;
    }

    /// <summary>True iff at least one OTHER player unit is alive in the band -- a real-position entry
    /// (not a frozen (0,0) twin) matching a player fingerprint, with live HP &gt; 0 and the dead bit
    /// clear, whose identity differs from the wielder's (so the wielder and its own twin are excluded).
    /// Gates the finishing blow: force-killing the LAST standing unit would be a party wipe (game over)
    /// before Reraise could fire. Errs SAFE -- any miscount lands on "no other ally" (degrade to
    /// survival), never on a false "ally alive" that would risk the wipe.</summary>
    private bool OtherAllyAlive(long wielder)
    {
        int wMhp = _mem.U16(wielder + Offsets.AMaxHp), wLvl = _mem.U8(wielder + Offsets.ALevel);
        int wBr = _mem.U8(wielder + Offsets.ABrave), wFa = _mem.U8(wielder + Offsets.AFaith);
        var allyFps = Band.AllyFingerprints(_mem);
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            if (_mem.U8(e + Offsets.AGx) == 0 && _mem.U8(e + Offsets.AGy) == 0) continue;   // (0,0) twin: stale liveness
            int mhp = _mem.U16(e + Offsets.AMaxHp), lvl = _mem.U8(e + Offsets.ALevel);
            int br = _mem.U8(e + Offsets.ABrave), fa = _mem.U8(e + Offsets.AFaith);
            if (mhp == wMhp && lvl == wLvl && br == wBr && fa == wFa) continue;   // the wielder (or its twin)
            if (!allyFps.Contains((mhp, lvl, br, fa))) continue;                  // not a player unit
            if (_mem.U16(e + Offsets.AHp) == 0) continue;                         // KO'd
            if ((_mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0) continue;   // dead bit set
            return true;
        }
        return false;
    }

    /// <summary>Start the timer (if any) for the phase being entered and log the edge once.</summary>
    private void EnterPhase(Phase next, DateTime now, int hp)
    {
        switch (next)
        {
            case Phase.Possum:
                _possumStart = now; _wasActiveWielder = false; _possumTurnCount = 0;
                ModLogger.Log($"feign-death: lethal hit -- the wielder flops as a corpse and plays dead (acting while the AI ignores it) for {Tuning.FeignPossumTurns} of its turns");
                break;
            case Phase.Finish:
                ModLogger.Log("feign-death: played-dead window over -- staying down until the wielder's own turn comes up, then ending the act");
                break;
            case Phase.Recover:
                _recoverStart = now;
                ModLogger.Log($"feign-death: the engine raised the wielder at {hp} HP -- clearing the KO state");
                break;
        }
        _phase = next;
    }
}
