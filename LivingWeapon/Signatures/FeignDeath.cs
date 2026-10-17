using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wrathblade's "Feign Death" signature (+3). A lethal hit on the wielder makes it flop as a corpse, act prone
/// for ~2 turns while the AI ignores it, then take a finishing blow so the engine's own Reraise stands it up
/// at ~10% HP. Once per battle. Wrathblade damage is the wielder's missing HP (formula 67), so the revive
/// leaves ~90% missing and loads the blade. Observed in game 2026-06-14. FeignDeath.Policy.cs <see cref="Step"/>
/// is the pure state machine; this half reads facts and applies the guarded writes. Phases run Watching,
/// Possum (prone, HP held at 1, Invisible held), Finish (finishing blow at up-next), Recover (dead bit held
/// cleared for FeignRecoverSeconds so the stand-up leaves no hearts or skipped turn). Reraise is applied only
/// at the finishing blow, so its status icon is not shown during play.
/// </summary>
internal sealed partial class FeignDeath : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);
    private const int WrathbladeId = 27;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    // Gate on armed (log facelift). TryResolveMainHand is roster-wide, so a benched +3 Wrathblade would arm the
    // edge. The scoped logger keeps those lines off the console unless a deployed unit wields it.
    private readonly ScopedLogger _slog;
    private bool _wasActive;
    private bool _spent;          // once-per-battle: the played-dead cycle has run its course
    private bool _sawAlive;       // the wielder has been seen alive this battle (arm guard)
    private Phase _phase;
    private DateTime _possumStart;
    private DateTime _recoverStart;
    private bool _finishKilled;   // the finishing blow (HP -> 0) has been dealt
    private bool _finishWasDead;  // the finish death registered -> awaiting the revive edge
    // The wielder's own possum turns, counted off the active-unit struct (TurnQueue). The band CT byte is too noisy.
    private bool _wasActiveWielder;   // was the wielder the engine's active unit last tick (turn-edge count)
    private int _possumTurnCount;     // wielder's completed turns this possum

    public FeignDeath(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, WrathbladeId));
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
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            _slog.Info(active
                ? "Wrathblade at tier three is wielded on the field; a lethal hit becomes a played-dead act, once per battle"
                : "Wrathblade's feign death is no longer active");
        }
        if (!active || !inLive) return;

        long e = Wielder.Locate(_mem, WrathbladeId, _hands, fp);
        if (e == 0) return;   // wielder's live entry not found this tick; resolved again next tick

        int hp = _mem.U16(e + Offsets.AHp);
        bool dead = (_mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
        if (hp > 0 && !dead) _sawAlive = true;

        // Once spent, only watch for a re-arm. A retry reloads faster than the debounced exit edge can reset us,
        // but it restores full HP.
        if (_spent)
        {
            if (ShouldRearm(_spent, hp, _mem.U16(e + Offsets.AMaxHp), dead))
            {
                _phase = Phase.Watching; _spent = false; _finishKilled = false; _finishWasDead = false;
                ModLogger.Event(LogVerb.Signature, "The wielder is back at full HP (battle restart or full heal); the played-dead act is available again");
            }
            else return;
        }

        // Count possum turns off the engine's active-unit struct (TurnQueue). The +0x25 CT byte was jumpy and
        // undercounted, which ran into the 90s safety cap (the "6 turns" overrun). Wall clock is only the idle cap.
        bool possumDone = false;
        if (_phase == Phase.Possum)
        {
            bool nowActive = ActiveUnitIsWielder(e);
            if (TurnEnded(_wasActiveWielder, nowActive))
            {
                _possumTurnCount++;
                ModLogger.Event(LogVerb.Signature, $"The wielder plays dead: turn {_possumTurnCount} of {Tuning.FeignPossumTurns}");
            }
            _wasActiveWielder = nowActive;
            possumDone = _possumTurnCount >= Tuning.FeignPossumTurns
                         || Elapsed(_possumStart, now, Tuning.FeignPossumSeconds);
        }
        bool recoverElapsed = _phase == Phase.Recover && Elapsed(_recoverStart, now, Tuning.FeignRecoverSeconds);
        // At the finishing blow, otherAllyAlive avoids a party wipe from killing the last unit up, and upNext
        // waits until the wielder is not active and at high CT. Killing earlier left it dead and scheduled for
        // a long climb, which crashed the engine.
        bool atBlow = _phase == Phase.Finish && !_finishKilled;
        bool otherAllyAlive = !atBlow || OtherAllyAlive(e);
        bool upNext = !atBlow
                      || (!ActiveUnitIsWielder(e) && _mem.U8(e + Offsets.ACtSlam) >= Tuning.FeignUpNextCt);

        var act = Step(_phase, hp, dead, _sawAlive, possumDone, recoverElapsed, _finishKilled, _finishWasDead, otherAllyAlive, upNext);

        // Invisible (+0x47/0x10) is re-stamped each tick. It breaks when the wielder is hit, not when it acts
        // (corrected 2026-07-22; see the Offsets.AInvisible block and the LIVE_LEDGER orphan-flag row).
        // The finishing blow drops Invisible, holds Reraise (+0x47/0x20), and sets HP 0 plus the dead bit,
        // since the engine does not flag dead on a memory HP write. Killing mid-turn left a stuck dead turn.
        if (act.Invisible is bool inv) SetInvisible(_mem, e, inv);
        if (act.Reraise is bool rr) SetReraise(_mem, e, rr);
        if (act.Dead is bool dd) SetDead(_mem, e, dd);
        if (act.Hp == HpAction.ForceKill) ForceKill(_mem, e);
        else if (act.Hp == HpAction.HoldAlive) HoldAlive(_mem, e);

        if (act.MarkKilled) { _finishKilled = true; ModLogger.Event(LogVerb.Signature, "The wielder's turn is next; the played-dead act ends now so its own turn revives it (a brief dead flag, not a real death)"); }
        if (act.MarkWasDead) _finishWasDead = true;

        if (act.Next != _phase) EnterPhase(act.Next, now, hp);
        if (act.Spent)
        {
            _spent = true;
            ModLogger.Event(LogVerb.Signature, _phase == Phase.Finish
                ? "The last party member is standing; the finishing blow was skipped and the wielder survives at 1 HP (no party wipe)"
                : "The played-dead cycle is complete; it is spent for this battle");
        }
    }

    /// <summary>True when the engine's current active unit (the condensed turn struct at TurnQueue) is the
    /// wielder, matched by max-HP and level. Used to count possum turns and to time the finishing blow.</summary>
    private bool ActiveUnitIsWielder(long wielder)
    {
        int wmhp = _mem.U16(wielder + Offsets.AMaxHp), wlvl = _mem.U8(wielder + Offsets.ALevel);
        return _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp) == wmhp
            && _mem.U16(Offsets.TurnQueue + Offsets.TqLevel) == wlvl;
    }

    /// <summary>True when another player unit is alive in the band: a real-position entry (not a (0,0) twin)
    /// matching a player fingerprint, HP above 0, dead bit clear, and not the wielder or its twin. Guards the
    /// finishing blow against a party wipe. A miscount lands on "no other ally", which degrades to survival.</summary>
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
                ModLogger.Event(LogVerb.Signature, $"A lethal hit landed; the wielder flops as a corpse and plays dead for {Tuning.FeignPossumTurns} of its turns while enemies ignore it");
                break;
            case Phase.Finish:
                ModLogger.Event(LogVerb.Signature, "The played-dead window is over; the wielder stays down until its own turn comes up, then the act ends");
                break;
            case Phase.Recover:
                _recoverStart = now;
                ModLogger.Event(LogVerb.Signature, $"The game raised the wielder at {hp} HP; clearing the knocked-out state");
                break;
        }
        _phase = next;
    }
}
