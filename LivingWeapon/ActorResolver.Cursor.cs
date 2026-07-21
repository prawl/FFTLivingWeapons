namespace LivingWeapon;

/// <summary>Which stage refused a <see cref="ActorResolver.TryResolveCursorPlayer"/> call, so
/// AttackCard.Resolve.cs's resolve-miss tap (LW-87) can log/tape WHICH stage without re-deriving
/// it from raw facts. Ordered the same as the resolve's own stage order.</summary>
internal enum CursorMiss
{
    /// <summary>The resolve succeeded; nothing to report. Never itself reported (a resolve call
    /// always leaves this at None on success, or overwrites it with a real refusal on
    /// failure).</summary>
    None,

    /// <summary><see cref="Band.FlagOwner"/> itself refused: either NO band entry currently reads
    /// ATurnFlag==1 at a real position (the battle-opening edge, or the gap between any two
    /// units' turns), OR two or more DISAGREEING t=1 entries were found (a genuinely ambiguous
    /// flags read). Folded into one stage deliberately: <c>Band.FlagOwner</c>'s own bool return
    /// cannot distinguish the two internally (both are "no safe candidate to answer with"), and
    /// splitting them would need a second out-param on <c>Band.FlagOwner</c> with three more call
    /// sites to update for a distinction no caller currently needs. EXPECTED AND ROUTINE between
    /// any two units' turns; not itself a sign of a problem.</summary>
    NoOwner,

    /// <summary>The flag owner's own frame nameId back-reference (<see cref="Offsets.ANameId"/>)
    /// reads 0: a capture failure (the frame slot's identity back-reference has not been written
    /// yet, or was never captured). Fails closed rather than guess.</summary>
    NameIdZero,

    /// <summary>The flag owner's (nameId,level,brave,faith) bridge matched zero, or more than one,
    /// roster row. EXPECTED AND ROUTINE on every enemy turn (an enemy's identity never appears in
    /// the roster at all) and on any genuinely duplicated roster identity; see
    /// ActorResolver.Flags.cs's ACCEPTED RESIDUAL doctrine.</summary>
    BridgeFail,
}

/// <summary>
/// LW-87 (docs/TODO.md): the pre-action cursor resolve AttackCard's dossier needs, re-anchored on
/// the PSX turn-flags owner (<see cref="Band.FlagOwner"/>, LW-63's exclusive ATurnFlag walk)
/// instead of the condensed turn-queue struct (<see cref="Offsets.TurnQueue"/>) this file read
/// through 2026-07-14. That struct FOLLOWS THE CURSOR, not the turn: the owner's own repro showed
/// the Attack row blanking to vanilla the instant a T-status detour moved the struct onto another
/// unit mid-turn, even though the acting unit's own move/act/wait menu was still open the whole
/// time. docs/LIVE_LEDGER.md's 2026-07-21 hover-follower row (instrument:
/// tools/probes/cursor_resolve_probe.py) is the live evidence this re-anchor rests on.
///
/// Deliberately separate from <see cref="ActorResolver.TryResolveActingPlayer"/>'s register-first
/// preamble: that method answers "who committed this action" (kill attribution, correct once an
/// action has landed), while this one answers "whose turn is open right now" (right the instant
/// the Abilities menu is hovered, before any action is chosen). This method shares its resolve
/// shape with <see cref="ActorResolver.TryResolveFlagOwner"/> (ActorResolver.Flags.cs) -- both
/// walk <see cref="Band.FlagOwner"/> then bridge the winning entry's frame nameId to a roster row
/// via the same <see cref="TryBridgeCursorToRoster"/> helper -- but answers with per-STAGE
/// observability (<see cref="CursorMiss"/>) instead of a single collapsed bool, since
/// AttackCard.Resolve.cs's resolve-miss tap needs to know WHICH stage refused, not merely that one
/// did.
///
/// THE ROSTER BRIDGE IS THE PLAYER FILTER (ActorResolver.Flags.cs's own doctrine, generalized
/// here): an enemy's or an unbridged guest's turn is not a fault of this method's own gates, it is
/// <see cref="CursorMiss.BridgeFail"/>, EXPECTED and routine on every enemy turn (the ACCEPTED
/// RESIDUAL, ActorResolver.Flags.cs's own class doc). An AI-controlled ROSTER unit's turn (a
/// Charmed/Confused/Berserk party member; the LWDEV Body Double clone bridging to its donor) DOES
/// compose that unit's own dossier here -- correct-for-the-acting-unit, a documented delta from
/// the old struct's behavior, not a hole.
///
/// RAW FACTS ONLY (LW-55, unchanged by this re-anchor): a successful resolve returns a
/// <see cref="CursorAnswer"/> carrying the matched roster slot's raw right-hand weapon id, that
/// SAME unit's own equipped weapon read off its band entry (<see cref="Offsets.AWeapon"/>), and
/// its turn-open flag (<see cref="Offsets.ATurnFlag"/>, RE-READ here rather than assumed 1: a
/// falling-edge race between <c>Band.FlagOwner</c>'s walk and this read can legitimately yield 0,
/// one benign refused tick that <see cref="CursorGate.Decide"/>'s gate B still catches -- this is
/// gate B's own remaining job, not something this resolve shortcuts to a constant), and the
/// matched band entry's own slot index: exactly what was read, no doctrine applied here.
/// <see cref="CursorGate.Decide"/> (consulted by AttackCard.Resolve.cs, never by this class) is
/// the only place those facts are judged. There is no register-based fallback: that path was
/// removed 2026-07-06, and a refused resolve since then composes vanilla, full stop.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>True (with <paramref name="answer"/> populated, <paramref name="miss"/> left at
    /// <see cref="CursorMiss.None"/>) only when ALL of: (1) <see cref="Band.FlagOwner"/> names
    /// exactly one real-position band entry with its turn-flag currently set; (2) that entry's
    /// frame nameId back-reference is nonzero; (3) the nameId bridges to EXACTLY ONE roster slot
    /// agreeing on (level,brave,faith). False otherwise, with <paramref name="answer"/> left
    /// default and <paramref name="miss"/> naming which stage refused: the caller (AttackCard.
    /// Resolve.cs) composes vanilla rather than trust a guess.</summary>
    public bool TryResolveCursorPlayer(out CursorAnswer answer, out CursorMiss miss)
    {
        answer = default;
        miss = CursorMiss.None;

        if (!Band.FlagOwner(_mem, out long entry, out int bandSlot))
        {
            miss = CursorMiss.NoOwner;
            return false;
        }

        ushort frameNameId = _mem.U16(entry + Offsets.ANameId);
        if (frameNameId == 0)
        {
            miss = CursorMiss.NameIdZero;
            return false;
        }

        byte br = _mem.U8(entry + Offsets.ABrave);
        byte fa = _mem.U8(entry + Offsets.AFaith);
        byte lvl = _mem.U8(entry + Offsets.ALevel);
        if (!TryBridgeCursorToRoster(frameNameId, lvl, br, fa, out long matchedRosterBase))
        {
            miss = CursorMiss.BridgeFail;
            return false;
        }

        int rosterHand = RawMainHand(matchedRosterBase);
        int bandWeapon = _mem.U16(entry + Offsets.AWeapon);
        byte turnFlag = _mem.U8(entry + Offsets.ATurnFlag);
        answer = new CursorAnswer(matchedRosterBase, rosterHand, bandWeapon, turnFlag, bandSlot);
        return true;
    }

    /// <summary>ActorRegister.Bridge's own proven pattern (nameId narrows, level+brave+faith
    /// confirms), reused here against a BAND entry instead of the register's CurrentEntry.
    /// Exactly one agreeing roster slot returns true; zero (no bridge) or more than one (a
    /// duplicated nameId) returns false. Shared by <see cref="TryResolveCursorPlayer"/> and
    /// <see cref="ActorResolver.TryResolveFlagOwner"/>/<see cref="ActorResolver.TryResolveFlagKiller"/>
    /// (ActorResolver.Flags.cs) -- one bridge implementation, three callers.</summary>
    private bool TryBridgeCursorToRoster(ushort frameNameId, int level, int br, int fa, out long rosterBase)
    {
        rosterBase = 0;
        int matches = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;
            if (_mem.U16(b + Offsets.RNameId) != frameNameId) continue;
            if (!Band.LevelMatchesRoster(rlvl, level)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            matches++;
            rosterBase = b;
        }
        return matches == 1;
    }
}
