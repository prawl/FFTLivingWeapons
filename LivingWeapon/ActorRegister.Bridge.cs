namespace LivingWeapon;

/// <summary>
/// ActorRegister's roster-identity bridge, split out at the 200-line refactor seam: matches the
/// engine's actor-pointer entry (nameId, level, brave, faith) against the 20 roster slots to
/// classify it Player, Enemy, or Unknown. ActorRegister.cs (the tick-driven ownership tracker)
/// calls into this; the enum and the class-level doc stay there.
/// </summary>
internal sealed partial class ActorRegister
{
    /// <summary>Roster bridge: a slot matches iff it is OCCUPIED (RLevel 1..99), its RNameId equals
    /// <paramref name="nameId"/> (already required &gt;0 by the 0==0 trap guard below), AND its
    /// (level,brave,faith) matches the entry's OWN band bytes -- belt-and-suspenders against the
    /// unproven enemy/player nameId-pool overlap (K2 of the kill-attribution plan). Exactly one
    /// match -&gt; Player. Zero, with a POSITIVELY-READ nonzero nameId -&gt; Enemy (authoritative;
    /// enemy nameIds never match a real slot). More than one match, OR <paramref name="nameId"/>
    /// itself reading 0 (the 0==0 trap: a capture failure, not a confident "this is an enemy") --
    /// Unknown (the gate is unsatisfied and callers fall through to the TQ-fingerprint body,
    /// so a genuine player whose nameId capture failed still resolves normally there).</summary>
    private RosterBridge Bridge(long entry, ushort nameId, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId == 0) return RosterBridge.Unknown;   // the 0==0 trap: capture failure, not "confident enemy"
        int lvl = _mem.U8(entry + Offsets.ALevel);
        int br = _mem.U8(entry + Offsets.ABrave);
        int fa = _mem.U8(entry + Offsets.AFaith);
        int matches = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            if (_mem.U16(b + Offsets.RNameId) != nameId) continue;
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            matches++;
            rosterBase = b;
        }
        return matches == 1 ? RosterBridge.Player : matches == 0 ? RosterBridge.Enemy : RosterBridge.Unknown;
    }
}
