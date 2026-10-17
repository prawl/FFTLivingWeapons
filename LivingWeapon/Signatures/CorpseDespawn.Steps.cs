namespace LivingWeapon;

/// <summary>
/// CorpseDespawn.cs's four guarded steps (a)-(d), split out past the 200-line trigger once
/// DespawnRefusal classification was added. TryDespawn dispatches and these are the primitives it calls in order.
/// </summary>
internal static partial class CorpseDespawn
{
    /// <summary>(a) Staleness. Re-verify before touching anything. Still Dead, same nameId (slot reuse),
    /// and not already an engine chest conversion.</summary>
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
        if (!mem.Readable(entry + Offsets.ACorpseConvertMarker, 1) || (mem.U8(entry + Offsets.ACorpseConvertMarker) & Offsets.ACorpseChestBitMask) != 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot} already converted to a chest (the engine's own removal, not ours); the crystal case is the dead-bit staleness check's job.");
            return false;
        }
        return true;
    }

    /// <summary>(b) Node resolve. Walk the render-node list from its head (capped at
    /// Offsets.DespawnNodeWalkMax so a looped list ends), matching each node's builder-written combat
    /// back-pointer to <paramref name="frame"/>. <paramref name="unreadable"/> is true only when the walk
    /// hit an unreadable node prefix, so the caller can log that apart from "no node found".</summary>
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

    /// <summary>(c) Guards from the Proven row. Never the current actor's node, never a unit whose turn
    /// is open.</summary>
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

    /// <summary>(d) The write. One guarded read-modify-write of the flag byte, single attempt, no retry.
    /// Byte-wide at node +0x12C (bits 0x30, value 0x20), as in the Proven row and BodyDoubleSpike.cs.
    /// Refuses when any of bits 0x30 is already set (BodyDoubleSpike.cs DespawnMark, ~line 869-874),
    /// since a removal is already in flight, ours or the engine's sweeper.</summary>
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
