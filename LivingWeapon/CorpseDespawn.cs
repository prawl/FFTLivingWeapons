namespace LivingWeapon;

/// <summary>LW-167: classifies a TryDespawn refusal for the retry lifecycle (LivingPoach.Despawn.cs)
/// -- see CorpseDespawn's class doc for exactly which checks land in each bucket.</summary>
internal enum DespawnRefusal
{
    /// <summary>No refusal -- TryDespawn returned true.</summary>
    None,
    /// <summary>The corpse is still confirmed dead and ours; the SAME retry may succeed once the
    /// blocking condition (current actor, open turn, an unresolved/unreadable node, an in-flight
    /// or unwritable flag byte) clears.</summary>
    Transient,
    /// <summary>The corpse is provably no longer the one that was poached (alive again, nameId
    /// mismatch/slot reuse, or already converted to a chest) -- retrying can never help.</summary>
    Permanent,
}

/// <summary>
/// LW-167 stage 3: the guarded corpse-despawn primitive that gives Living Poach vanilla fidelity
/// -- vanilla's own Poach leaves NO corpse behind (no crystal, no chest), so a poached carcass
/// must not linger either. Extracted from BodyDoubleSpike.cs's Ctrl+F5 dev instrument (#if
/// LWDEV) into a second, independent, production-guarded implementation: docs/LIVE_LEDGER.md's
/// "DESPAWN any unit mid-battle, sprite and all" Proven row (owner live 2026-07-10, flipped
/// 2026-07-21) is the mechanism -- write mode 2 into the render node's flag byte (node +0x12C =
/// (old &amp; ~0x30) | 0x20, BYTE-wide -- matching BodyDoubleSpike.cs's dev spike, the Proven row's
/// own instrument, which reads/writes this same single byte, never a 4-byte word)
/// and the engine's own per-frame node sweeper (0x14026E20C) completes
/// the whole removal, unit and sprite together, on its next unpaused frame (the same primitive
/// vanilla crystallization uses). ONE-WAY.
///
/// BodyDoubleSpike keeps its own private copy of the constants and the walk, dev-only and
/// untouched by this file: promoting its addresses into Offsets.cs and writing this second,
/// much smaller, despawn-only implementation is the smaller, safer diff than refactoring a
/// 1576-line working dev spike to share code with a production caller.
///
/// Sequence (every call, guarded and fail-closed; each individual call is still ONE attempt, never
/// looping internally -- LW-167 moved the retrying to the CALLER, LivingPoach.Despawn.cs, which
/// re-invokes this same function on a later tick rather than this function looping itself):
///   (a) STALENESS -- the corpse must still be exactly what the kill-credit moment saw: still
///       Dead, the same nameId (a slot-reuse guard -- another unit may have since occupied this
///       band slot), and not already claimed by the engine's own crystal/chest conversion.
///   (b) NODE RESOLVE -- a bounded walk of the render-node list, matching a node's combat
///       back-pointer against this corpse's own combat frame address.
///   (c) GUARDS from the Proven row -- never the current actor's node, never an open turn.
///   (d) THE WRITE -- one guarded read-modify-write of the flag byte, refusing if a removal is
///       already in flight on this node.
/// A refusal at any step is a Debug log naming it and a false return; it never rolls back
/// anything the caller already did (LivingPoach's store write + toast stand either way -- the
/// carcass simply stays on the field).
///
/// LW-167: <see cref="DespawnRefusal"/> lets a caller (LivingPoach.Despawn.cs's retry lifecycle)
/// tell a worth-retrying refusal from a dead-end one. PERMANENT is exactly a
/// <see cref="StillOurCorpse"/> failure (alive again, nameId mismatch/slot reuse, already chest-
/// converted) -- the corpse is provably no longer the one that was poached, so retrying can never
/// help. Every other refusal (node unresolvable/unreadable, the current-actor/open-turn guards,
/// an unwritable or in-flight-marked flag byte) is TRANSIENT -- the corpse is still confirmed dead
/// and ours, so the SAME conditions that blocked this attempt can clear on a later tick.
/// </summary>
internal static partial class CorpseDespawn
{
    /// <summary>Attempt the despawn on <paramref name="bandSlot"/>'s corpse (band-slot numbering,
    /// 0..Offsets.BandSlots-1 -- the same slot LivingPoach.RecordPoachDeed and KillTracker's deed
    /// sink already use). <paramref name="expectedNameId"/> is the victim's nameId snapshot
    /// captured at credit time. Returns true only once the write actually landed. Convenience
    /// overload for every call site that doesn't need the refusal classification.</summary>
    internal static bool TryDespawn(IGameMemory mem, int bandSlot, ushort expectedNameId)
        => TryDespawn(mem, bandSlot, expectedNameId, out _);

    /// <summary>Same attempt, plus <paramref name="reason"/> classifying a refusal (see the class
    /// doc's PERMANENT/TRANSIENT split) so a caller can decide whether retrying is worthwhile.
    /// <paramref name="reason"/> is <see cref="DespawnRefusal.None"/> exactly when the return is
    /// true.</summary>
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
