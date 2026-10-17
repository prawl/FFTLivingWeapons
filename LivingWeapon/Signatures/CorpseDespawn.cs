namespace LivingWeapon;

/// <summary>classifies a TryDespawn refusal for the retry lifecycle (LivingPoach.Despawn.cs);
/// see CorpseDespawn's class doc.</summary>
internal enum DespawnRefusal
{
    /// <summary>No refusal -- TryDespawn returned true.</summary>
    None,
    /// <summary>The corpse is still confirmed dead and ours; the same retry may succeed once the
    /// blocking condition (current actor, open turn, an unresolved/unreadable node, an in-flight
    /// or unwritable flag byte) clears.</summary>
    Transient,
    /// <summary>The corpse is provably no longer the one that was poached (alive again, nameId
    /// mismatch/slot reuse, or already converted to a chest) -- retrying can never help.</summary>
    Permanent,
}

/// <summary>
/// LW-167 stage 3: guarded corpse despawn for Living Poach. Vanilla Poach leaves no corpse (no
/// crystal, no chest), so a poached carcass must not linger. Production copy of the Ctrl+F5 dev
/// instrument in BodyDoubleSpike.cs (#if LWDEV), which keeps its own private constants.
/// Mechanism is the Proven row "DESPAWN any unit mid-battle, sprite and all" in
/// docs/LIVE_LEDGER.md (2026-07-10, flipped 2026-07-21). It writes mode 2 into the render node's
/// flag byte (node +0x12C = (old &amp; ~0x30) | 0x20, a single byte, never a word) and the
/// engine's node sweeper (0x14026E20C) removes unit and sprite on its next unpaused frame. One-way.
///
/// One call is one attempt; the caller (LivingPoach.Despawn.cs) retries on a later tick. Steps:
/// staleness check (still Dead, same nameId against slot reuse, not already chest-converted),
/// bounded render-node walk matching the corpse's combat frame, the Proven row's guards (not the
/// current actor, no open turn), then one read-modify-write that refuses if a removal is already
/// in flight. A refusal logs at Debug and returns false; the caller's store write and toast stand.
///
/// <see cref="DespawnRefusal"/> tells a caller whether to retry. Permanent is a
/// <see cref="StillOurCorpse"/> failure (alive again, slot reuse, chest-converted). Everything
/// else is transient, since the corpse is still ours and the blocker may clear.
/// </summary>
internal static partial class CorpseDespawn
{
    /// <summary>Try to despawn the corpse in <paramref name="bandSlot"/> (0..Offsets.BandSlots-1).
    /// <paramref name="expectedNameId"/> is the victim's nameId at credit time. Returns true only
    /// once the write landed.</summary>
    internal static bool TryDespawn(IGameMemory mem, int bandSlot, ushort expectedNameId)
        => TryDespawn(mem, bandSlot, expectedNameId, out _);

    /// <summary>Same attempt, with <paramref name="reason"/> classifying a refusal (see the class
    /// doc). It is <see cref="DespawnRefusal.None"/> exactly when the return is true.</summary>
    internal static bool TryDespawn(IGameMemory mem, int bandSlot, ushort expectedNameId, out DespawnRefusal reason)
    {
        long entry = Band.Entry(bandSlot);
        if (!StillOurCorpse(mem, entry, bandSlot, expectedNameId))
        {
            reason = DespawnRefusal.Permanent;
            return false;
        }

        long frame = Offsets.FrameReadBase + (long)bandSlot * Offsets.CombatStride;
        if (!TryResolveNode(mem, frame, out long node, out bool unreadable))
        {
            string why = unreadable
                ? $"a node in the render-node walk was unreadable before band slot {bandSlot}'s combat frame could be matched"
                : $"no render node backreferences band slot {bandSlot}'s combat frame";
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: {why}.");
            reason = DespawnRefusal.Transient;
            return false;
        }

        if (!GuardsClear(mem, entry, node, bandSlot))
        {
            reason = DespawnRefusal.Transient;
            return false;
        }

        if (!WriteRemoval(mem, node, bandSlot))
        {
            reason = DespawnRefusal.Transient;
            return false;
        }

        reason = DespawnRefusal.None;
        return true;
    }
}
