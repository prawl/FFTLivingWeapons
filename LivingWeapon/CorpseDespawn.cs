namespace LivingWeapon;

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
/// Sequence (LivingPoach.cs's TryDespawn call site, all guarded, fail-closed, ONE attempt --
/// never retried in a loop):
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
/// </summary>
internal static class CorpseDespawn
{
    /// <summary>Attempt the despawn on <paramref name="bandSlot"/>'s corpse (band-slot numbering,
    /// 0..Offsets.BandSlots-1 -- the same slot LivingPoach.RecordPoachDeed and KillTracker's deed
    /// sink already use). <paramref name="expectedNameId"/> is the victim's nameId snapshot
    /// captured at credit time. Returns true only once the write actually landed.</summary>
    internal static bool TryDespawn(IGameMemory mem, int bandSlot, ushort expectedNameId)
    {
        long entry = Band.Entry(bandSlot);
        if (!StillOurCorpse(mem, entry, bandSlot, expectedNameId)) return false;

        long frame = Offsets.FrameReadBase + (long)bandSlot * Offsets.CombatStride;
        if (!TryResolveNode(mem, frame, out long node, out bool unreadable))
        {
            string reason = unreadable
                ? $"a node in the render-node walk was unreadable before band slot {bandSlot}'s combat frame could be matched"
                : $"no render node backreferences band slot {bandSlot}'s combat frame";
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: {reason}.");
            return false;
        }

        return GuardsClear(mem, entry, node, bandSlot) && WriteRemoval(mem, node, bandSlot);
    }

    /// <summary>(a) STALENESS: re-verify right before touching anything -- still Dead, the same
    /// nameId (slot reuse), and not already the engine's own crystal/chest conversion.</summary>
    private static bool StillOurCorpse(IGameMemory mem, long entry, int bandSlot, ushort expectedNameId)
    {
        if (!mem.Readable(entry + Offsets.ADeadStatus, 1) || (mem.U8(entry + Offsets.ADeadStatus) & Offsets.ADeadBit) == 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot} no longer reads Dead.");
            return false;
        }
        if (!mem.Readable(entry + Offsets.ANameId, 2) || mem.U16(entry + Offsets.ANameId) != expectedNameId)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s nameId no longer matches the credited kill (another unit reused the slot).");
            return false;
        }
        if (!mem.Readable(entry + Offsets.ACorpseConvertMarker, 1) || mem.U8(entry + Offsets.ACorpseConvertMarker) != 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot} already converted to a crystal/chest (the engine's own removal, not ours).");
            return false;
        }
        return true;
    }

    /// <summary>(b) NODE RESOLVE: bounded walk of the render-node list from its head, matching
    /// each node's combat back-pointer (builder-written) against <paramref name="frame"/> -- the
    /// corpse's own combat frame address. Guarded reads only; the walk is capped at
    /// Offsets.DespawnNodeWalkMax so a corrupted/looped list still terminates.
    /// <paramref name="unreadable"/> distinguishes the two distinct false-return reasons (the
    /// caller logs a truthful message for each): true only when the walk bailed on an unreadable
    /// node prefix; false (with no match) when the walk exhausted every node/hit the cap without
    /// finding one -- previously both surfaced as the same "no render node backreferences"
    /// message, which was a lie on the unreadable path.</summary>
    private static bool TryResolveNode(IGameMemory mem, long frame, out long node, out bool unreadable)
    {
        node = 0;
        unreadable = false;
        ulong cur = mem.U64(Offsets.DespawnNodeListHead);
        for (int i = 0; i < Offsets.DespawnNodeWalkMax && cur != 0; i++)
        {
            if (!mem.Readable((long)cur, Offsets.DespawnNodeCombatOff + 8))
            {
                unreadable = true;
                return false;
            }
            if ((long)mem.U64((long)cur + Offsets.DespawnNodeCombatOff) == frame)
            {
                node = (long)cur;
                return true;
            }
            cur = mem.U64((long)cur);
        }
        return false;
    }

    /// <summary>(c) GUARDS from the Proven row: never the current actor's node, never a unit
    /// whose turn is open.</summary>
    private static bool GuardsClear(IGameMemory mem, long entry, long node, int bandSlot)
    {
        if (!mem.Readable(node + Offsets.DespawnNodeIdOff, 1) || !mem.Readable(Offsets.DespawnCurrentActorNodeId, 4))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s node id or the current-actor marker is unreadable.");
            return false;
        }
        byte nodeId = mem.U8(node + Offsets.DespawnNodeIdOff);
        if (nodeId == (byte)mem.U32(Offsets.DespawnCurrentActorNodeId))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s node is the current actor; never remove the acting unit.");
            return false;
        }
        if (!mem.Readable(entry + Offsets.ATurnFlag, 1))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s turn flag is unreadable.");
            return false;
        }
        if (mem.U8(entry + Offsets.ATurnFlag) != 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s turn is open; never remove a unit mid-turn.");
            return false;
        }
        return true;
    }

    /// <summary>(d) THE WRITE: one guarded read-modify-write of the flag byte. ONE attempt --
    /// success or refusal, never retried. BYTE-wide, matching the Proven row's own instrument at
    /// node +0x12C (bits 0x30 and value 0x20 both live in the low byte; BodyDoubleSpike.cs's dev
    /// spike reads/writes this same single byte, never a 4-byte word). Also carries the dev
    /// spike's own in-flight guard (BodyDoubleSpike.cs
    /// DespawnMark, ~line 869-874): if the byte already reads any of bits 0x30, a removal is
    /// already in flight on this node (ours from a prior attempt, or the engine's own sweeper
    /// mid-cycle) -- writing over it would stomp that in-flight state, so refuse instead.</summary>
    private static bool WriteRemoval(IGameMemory mem, long node, int bandSlot)
    {
        long addr = node + Offsets.DespawnNodeModeOff;
        if (!mem.Readable(addr, 1))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s node flag byte is unreadable.");
            return false;
        }
        byte oldMode = mem.U8(addr);
        if ((oldMode & Offsets.DespawnNodeModeClearMask) != 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s node flag byte reads 0x{oldMode:X2}; the corpse's node is already marked for removal.");
            return false;
        }
        if (!mem.Writable(addr, 1))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot}'s node flag byte is not writable.");
            return false;
        }
        byte cleared = (byte)(oldMode & ~(byte)Offsets.DespawnNodeModeClearMask);
        byte newMode = (byte)(cleared | (byte)Offsets.DespawnNodeModeRemoveValue);
        mem.W8(addr, newMode);
        ModLogger.Debug(LogVerb.Signature, $"Living Poach despawned the poached corpse at band slot {bandSlot} (node 0x{node:X}); the engine's sweeper completes the removal on its next unpaused frame.");
        return true;
    }
}
