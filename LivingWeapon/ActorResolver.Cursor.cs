namespace LivingWeapon;

/// <summary>
/// LW-31 stage-2 fix (ledger LW-31): the pre-action cursor resolve AttackCard's dossier needs.
/// Deliberately separate from <see cref="ActorResolver.TryResolveActingPlayer"/>'s register-first
/// preamble: that method answers "who committed this action" (kill attribution, correct once an
/// action has landed), while this one answers "whose turn just opened" (right the instant the
/// Abilities menu is hovered, before any action is chosen). Reads the condensed turn-queue struct
/// (<see cref="Offsets.TurnQueue"/>) EXCLUSIVELY, with no register consultation at all: the
/// TurnOwnerSpike tape (2026-07-05) proved this struct snaps to the acting unit at TURN OPEN,
/// leading the register's own arrival (which only updates once a unit ACTS) by several seconds on
/// the same unit.
///
/// NEVER reads <see cref="Offsets.TqNameId"/>: Offsets.cs already documents it as a trap, a
/// SEQUENTIAL battle index rather than a roster nameId (a Time Mage's index 1 once collided with
/// Ramza's roster nameId 1 and mis-credited every kill to Ramza), and the same tape caught it
/// flickering to a garbage value while level/hp stayed correct mid action-confirm. Identity here
/// comes ONLY from the two proven sources: the (level,hp,maxHp) band fingerprint (the same shape
/// as this class's own turn-queue fallback body, twin filter included), plus the matched band
/// entry's own frame nameId back-reference (<see cref="Offsets.ANameId"/>), bridged to roster
/// exactly like <see cref="ActorRegister"/>'s own Bridge method (nameId narrows, level+brave+faith
/// confirms). Any guard failure or ambiguity returns false, meaning "no cursor answer", never a
/// guess.
///
/// RAW FACTS ONLY (LW-55): a successful resolve returns a <see cref="CursorAnswer"/> carrying the
/// matched roster slot's raw right-hand weapon id, that SAME unit's own equipped weapon read off
/// its band entry (<see cref="Offsets.AWeapon"/>) and turn-open flag (<see cref="Offsets.ATurnFlag"/>),
/// and the matched band entry's own slot index: exactly what was read, no doctrine applied here.
/// <see cref="CursorGate.Decide"/> (consulted by AttackCard.Resolve.cs, never by this class) is
/// the only place those facts are judged. There is no register-based fallback: that path was
/// removed 2026-07-06, and a refused resolve since then composes vanilla, full stop.
/// </summary>
internal sealed partial class ActorResolver
{
    // The struct span this resolve reads: TqTeam(0x02)..TqMaxHp(0x10)+2 bytes.
    private const int CursorSpan = Offsets.TqMaxHp + 2;

    /// <summary>True (with <paramref name="answer"/> populated) only when ALL of: (1) the struct
    /// is readable at all; (2) its team field is 0, a player's turn (an enemy or ally/guest turn
    /// returns false untouched); (3) its (level,hp,maxHp) fingerprint matches EXACTLY ONE band
    /// entry (twin filter included, mirrors <see cref="TryResolveActingPlayer"/>'s own turn-queue
    /// body); (4) that band entry's frame nameId back-reference bridges to EXACTLY ONE roster slot
    /// agreeing on (level,brave,faith). False otherwise (unreadable, non-player turn,
    /// ambiguous/absent band match, or the nameId bridge failing/ambiguous), with
    /// <paramref name="answer"/> left default: the caller (AttackCard.Resolve.cs) composes vanilla
    /// rather than trust a guess. The two new band reads inside <paramref name="answer"/>
    /// (<see cref="CursorAnswer.BandWeapon"/>, <see cref="CursorAnswer.TurnFlag"/>) are UNGUARDED
    /// fail-safe reads, this class's own idiom: only the TurnQueue head above is Readable-guarded;
    /// <c>Mem</c> fail-safes an unreadable address to 0, and 0 reads as sentinel/flag-down, which
    /// <see cref="CursorGate.Decide"/> refuses anyway, so no explicit guard is needed here.</summary>
    public bool TryResolveCursorPlayer(out CursorAnswer answer)
    {
        answer = default;

        if (!_mem.Readable(Offsets.TurnQueue, CursorSpan)) return false;
        if (_mem.U16(Offsets.TurnQueue + Offsets.TqTeam) != 0) return false;   // not a player's turn

        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp    = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;

        if (!TryFindCursorBandEntry(maxHp, hp, level, out long entry, out int bandSlot)) return false;

        ushort frameNameId = _mem.U16(entry + Offsets.ANameId);
        if (frameNameId == 0) return false;   // capture failure: fail closed, never a guess

        byte br = _mem.U8(entry + Offsets.ABrave);
        byte fa = _mem.U8(entry + Offsets.AFaith);
        if (!TryBridgeCursorToRoster(frameNameId, level, br, fa, out long matchedRosterBase)) return false;

        int rosterHand = RawMainHand(matchedRosterBase);
        int bandWeapon = _mem.U16(entry + Offsets.AWeapon);
        byte turnFlag = _mem.U8(entry + Offsets.ATurnFlag);
        answer = new CursorAnswer(matchedRosterBase, rosterHand, bandWeapon, turnFlag, bandSlot);
        return true;
    }

    /// <summary>Band walk fingerprint-matching (maxHp,hp,level), twin filter included: the SAME
    /// shape as <see cref="TryResolveActingPlayer"/>'s own turn-queue body, factored out here so
    /// this cursor resolve does not duplicate the twin-filter logic inline. <paramref name="matchedSlot"/>
    /// is the winning entry's own band slot index (LW-55: carried into <see cref="CursorAnswer.BandSlot"/>
    /// for the tripwire's evidence trail), -1 when nothing matched.</summary>
    private bool TryFindCursorBandEntry(ushort maxHp, ushort hp, ushort level, out long matched, out int matchedSlot)
    {
        matched = 0;
        matchedSlot = -1;
        bool found = false, foundReal = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;
            if (realPos && !foundReal && found) { found = false; foundReal = true; }
            if (realPos) foundReal = true;

            if (!found) { matched = addr; matchedSlot = s; found = true; }
            else if (matched != addr) return false;   // ambiguous band match
        }
        return found;
    }

    /// <summary>ActorRegister.Bridge's own proven pattern (nameId narrows, level+brave+faith
    /// confirms), reused here against a BAND entry instead of the register's CurrentEntry.
    /// Exactly one agreeing roster slot returns true; zero (no bridge) or more than one (a
    /// duplicated nameId) returns false.</summary>
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
