using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Yoichi Bow's Barrage signature.
/// It also holds the InjectSlot/ReleaseSlot record primitives, which take memory as a parameter so they are unit-testable.
/// The stateful wielder scan and per-instance bookkeeping live in Barrage.cs.
///
/// Record layout (2026-06-10 menu session; barrage_probe.py's "msb" flag is whole-u16 order and wrong,
/// it rendered 358 as Aurablast/102). Record r has flags at ABILITY_BASE + r*25, 3 =
/// [ExtAb byte0][ExtAb byte1][ExtRSM], then AbilityId1..16, RSMId1..6. Extend bits are MSB-first per
/// byte (byte0 = slots 1-8, byte1 = slots 9-16), so slot 10 = 0x4000 as an LE u16. Anchor. Archer rec 8
/// = Aim+1..+8 as bytes 150-157 with byte0 = 0xFF (ids 406-413).
///
/// Jobs (Archer 77 -> rec 8 menu-verified, Chemist 75 -> jobIdx 1 purchase-diff-verified; source is
/// FFTHandsFree's CharacterData roster-job dict, PSX wheel order from 74). Generic band 74..92 maps to
/// rec = job, 69, jobIdx = job, 74; Dancer 92 shares jobIdx 17 with Bard but keeps its own record
/// (Bard 22, Dance 23). Mime 93, unique story jobs, monsters and Dark Knight are unmapped.
/// </summary>
internal sealed partial class Barrage
{
    /// <summary>Generic Thief job id (roster +0x02). Thief's primary command is Steal (rec 14), a normal
    /// executor, so an injected Barrage casts cleanly there. Special-executor jobs swallow it (see
    /// SpecialExecutorJobs).</summary>
    public const int ThiefJob = 83;

    /// <summary>JobCommand record id for Steal; the injection target and the secondary-slot eligibility check.</summary>
    public const int ThiefRecord = 14;

    /// <summary>True when the signature is configured (GrantCommandAbilityId set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>A wielder qualifies when Thief is the primary job (83) or Steal is the mounted
    /// secondary (record 14). Matches the card's "Thief Only" text. See <see cref="TryResolveGrant"/>
    /// for where the injection actually lands.</summary>
    public static bool IsEligibleWielder(int job, int secondaryRecord)
        => job == ThiefJob || secondaryRecord == ThiefRecord;

    /// <summary>Jobs whose primary command is a special executor that silently drops foreign ability
    /// ids at confirm time (label renders, execution no-ops). Seen for Aim 2026-06-10, where 358/102/146
    /// were swallowed from rec 8 slots 1/9/10/11. Jump shares Aim's structure; Items/Throw read
    /// inventory; Arithmeticks has its own picker. A future Archer path is the +0x07 secondary route.</summary>
    private static readonly int[] SpecialExecutorJobs = { 75, 77, 87, 89, 90 }; // Chemist, Archer, Dragoon, Ninja, Arithmetician

    /// <summary>Resolve a roster job byte (+0x02) to its JobCommand record id and learned-bitfield
    /// jobIdx. Maps band 74..92 minus the special-executor jobs; everything else returns false.</summary>
    public static bool TryResolveJob(int job, out int recId, out int jobIdx)
    {
        recId = -1;
        jobIdx = -1;
        if (job < 74 || job > 92) return false;
        if (System.Array.IndexOf(SpecialExecutorJobs, job) >= 0) return false;
        recId = job - 69;                       // Squire 74 -> rec 5 ... Dancer 92 -> rec 23
        jobIdx = job == 92 ? 17 : job - 74;     // Bard & Dance share learned jobIdx 17
        return true;
    }

    /// <summary>Resolve a secondary command record (roster +0x07 holds the JobCommand rec id) to its
    /// learned jobIdx. Normal-executor records only. Recs 5..23 minus Items 6, Aim 8, Jump 18, Throw 20,
    /// Arithmeticks 21, plus the Mettle variants 25-27 (jobIdx 0; Mettle executed 358).</summary>
    public static bool TryResolveSecondary(int secondaryRec, out int jobIdx)
    {
        jobIdx = -1;
        if (secondaryRec >= 25 && secondaryRec <= 27) { jobIdx = 0; return true; }   // Mettle variants
        if (secondaryRec < 5 || secondaryRec > 23) return false;
        if (secondaryRec is 6 or 8 or 18 or 20 or 21) return false;                  // special executors
        jobIdx = secondaryRec == 23 ? 17 : secondaryRec - 5;                          // Dance shares Bard's 17
        return true;
    }

    /// <summary>Grant target for a wielder. Uses the primary job's record when it is a normal executor,
    /// else the mounted secondary's record (cast from Steal as an Archer's secondary, 2026-06-10).
    /// False when neither is grantable. The record is rec 14 (Steal) only for a Thief or for a
    /// special-executor primary falling back to Steal. A Knight with Steal secondary resolves to its own
    /// rec 7, so Barrage and ShadowBlade can share a record (see ReleaseSlot / InjectSlot).</summary>
    public static bool TryResolveGrant(int job, int secondaryRec, out int recId, out int jobIdx, out bool viaSecondary)
    {
        viaSecondary = false;
        if (TryResolveJob(job, out recId, out jobIdx)) return true;
        if (TryResolveSecondary(secondaryRec, out jobIdx))
        {
            recId = secondaryRec;
            viaSecondary = true;
            return true;
        }
        recId = -1;
        return false;
    }

    /// <summary>First empty slot (1..16) or -1. A slot is empty when its byte is 0 and its extend bit
    /// is clear (byte 0 with the bit set is ability 256).</summary>
    public static int FindEmptySlot(byte[] abilityBytes, ushort extAb)
    {
        for (int i = 0; i < 16; i++)
        {
            if (abilityBytes[i] == 0 && (extAb & ExtendBit(i)) == 0) return i + 1;
        }
        return -1;
    }

    /// <summary>The byte value for the given ability id (low 8 bits).</summary>
    public static byte SlotByte(int abilityId) => (byte)(abilityId & 0xFF);

    /// <summary>Extend bit for 0-indexed slot i in the LE-composed ExtAb u16, MSB-first per byte.
    /// Slot 1 is 0x0080, slot 9 is 0x8000, slot 10 is 0x4000.</summary>
    public static ushort ExtendBit(int slotIdx)
        => (ushort)((0x80 >> (slotIdx % 8)) << (8 * (slotIdx / 8)));

    /// <summary>True when the slot still needs injection; for ability 358 the byte must be 102 and the extend bit set.</summary>
    public static bool NeedsInject(byte slotByte, ushort extAb, int slotIdx)
        => slotByte != (byte)102 || (extAb & ExtendBit(slotIdx)) == 0;

    /// <summary>Byte index in the 2-byte learned bitfield for a 1-indexed slot (1-8 in byte 0, 9-16 in byte 1).</summary>
    public static int LearnedByteIndex(int slotIdx1)
        => slotIdx1 <= 8 ? 0 : 1;

    /// <summary>Bitmask within the learned byte for a 1-indexed slot, MSB-first. Slot 9 is byte1 0x80,
    /// the bit the game's own Barrage purchase targets.</summary>
    public static byte LearnedBitMask(int slotIdx1)
        => (byte)(1 << (7 - (slotIdx1 - 1) % 8));

    /// <summary>Write the ability into a JobCommand record slot, but only when the slot is empty or
    /// already holds this ability (same predicate as ReleaseSlot's guard, <see
    /// cref="ShadowBladePolicy.NeedsInject"/> negated). A stale slotIdx, e.g. after the JobCommand table
    /// rebuilds a real ability into our old slot, would otherwise overwrite it.
    /// <paramref name="flagAddr"/> is the 3-byte flag prefix (ExtAb b0, b1, ExtRSM); <paramref name="abBase"/>
    /// is the 16 ability bytes (flagAddr + 3). ORs in the slot's extend bit and keeps the others. Both
    /// writes are confirmed writable first, so a refusal never leaves a cleared byte with its bit set
    /// (that renders as ability 256). Returns true when written.</summary>
    public static bool InjectSlot(IGameMemory mem, long flagAddr, long abBase, int slotIdx, int abilityId)
    {
        long byteAddr = abBase + slotIdx;
        if (!mem.Readable(byteAddr, 1) || !mem.Readable(flagAddr, 2)) return false;
        byte curByte = mem.U8(byteAddr);
        ushort extAb = (ushort)(mem.U8(flagAddr) | (mem.U8(flagAddr + 1) << 8));
        ushort bit = ExtendBit(slotIdx);
        bool isEmpty = curByte == 0 && (extAb & bit) == 0;
        bool isOurs = !ShadowBladePolicy.NeedsInject(curByte, extAb, slotIdx, abilityId);
        if (!isEmpty && !isOurs) return false;   // someone else's ability is sitting here -- refuse

        if (!mem.Writable(byteAddr, 1) || !mem.Writable(flagAddr, 2)) return false;
        mem.W8(byteAddr, SlotByte(abilityId));
        if (abilityId >= 256) extAb |= bit; else extAb = (ushort)(extAb & ~bit);
        mem.W8(flagAddr, (byte)(extAb & 0xFF));
        mem.W8(flagAddr + 1, (byte)(extAb >> 8));
        return true;
    }

    /// <summary>Put OUR slot back to empty (byte 0, its extend bit clear) and leave the rest of the
    /// 25-byte record alone. FindEmptySlot only ever hands out a slot that was already empty, so zeroing
    /// it is the full restore and needs no saved snapshot. It also cannot resurrect another module's
    /// injection in the same record (see BarrageTests' two-module collision test).
    /// Refuses unless the slot still holds our ability, using the same <see
    /// cref="ShadowBladePolicy.NeedsInject"/> negation as InjectSlot. The JobCommand table rebuilds at
    /// boot (see Barrage.cs), so a stale slotIdx can point at a real ability. Both writes are confirmed
    /// writable first, so a refusal never leaves a half-released slot. Returns true when released; the
    /// caller forgets the slot either way (BarrageState.Clear).</summary>
    public static bool ReleaseSlot(IGameMemory mem, long flagAddr, long abBase, int slotIdx, int abilityId)
    {
        long byteAddr = abBase + slotIdx;
        if (!mem.Readable(byteAddr, 1) || !mem.Readable(flagAddr, 2)) return false;
        byte slotByte = mem.U8(byteAddr);
        ushort extAb = (ushort)(mem.U8(flagAddr) | (mem.U8(flagAddr + 1) << 8));
        if (ShadowBladePolicy.NeedsInject(slotByte, extAb, slotIdx, abilityId)) return false;   // not ours

        if (!mem.Writable(byteAddr, 1) || !mem.Writable(flagAddr, 2)) return false;
        mem.W8(byteAddr, 0);
        ushort bit = ExtendBit(slotIdx);
        extAb = (ushort)(extAb & ~bit);
        mem.W8(flagAddr, (byte)(extAb & 0xFF));
        mem.W8(flagAddr + 1, (byte)(extAb >> 8));
        return true;
    }

}
