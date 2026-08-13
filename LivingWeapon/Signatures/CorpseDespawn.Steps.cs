namespace LivingWeapon;

/// <summary>
/// CorpseDespawn.cs's four guarded steps (a)-(d) -- split out as a real seam once DespawnRefusal
/// classification pushed the file over the 200-line trigger: TryDespawn (CorpseDespawn.cs) is the
/// entry point/dispatch that a caller reads top-to-bottom; these are the guarded primitives it
/// calls in sequence, each independently readable without the dispatch wrapping it.
/// </summary>
internal static partial class CorpseDespawn
{
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
        if (!mem.Readable(entry + Offsets.ACorpseConvertMarker, 1) || (mem.U8(entry + Offsets.ACorpseConvertMarker) & Offsets.ACorpseChestBitMask) != 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's corpse despawn refused: band slot {bandSlot} already converted to a chest (the engine's own removal, not ours); the crystal case is the dead-bit staleness check's job.");
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
