using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Yoichi Bow's "Barrage" signature -- no memory access.
/// The stateful wielder scan and JobCommand record inject/restore live in Barrage.cs.
///
/// LIVE-PROVEN layout (2026-06-10 menu session; supersedes barrage_probe.py's "msb" flag,
/// which is whole-u16 order and WRONG -- it rendered ability 358 as Aurablast/102):
///   Record r: flags at ABILITY_BASE + r*25 - 3 = [ExtAb byte0][ExtAb byte1][ExtRSM],
///   then AbilityId1..16, RSMId1..6. Extend bits are MSB-first PER BYTE:
///   byte0 = slots 1-8 (bit7 = slot 1), byte1 = slots 9-16 (bit7 = slot 9).
///   As the LE-composed u16 (byte0 | byte1&lt;&lt;8): slot 10 = 0x4000.
///   Anchor: Archer rec 8 = Aim+1..+8 stored as bytes 150-157 with byte0 = 0xFF (ids 406-413).
///
/// Job resolution (live anchors: Archer 77 -> rec 8 menu-verified; Chemist 75 -> jobIdx 1
/// purchase-diff-verified; FFTHandsFree CharacterData roster-job dict, PSX wheel order from 74):
///   generic band 74..92: rec = job - 69, jobIdx = job - 74, except Dancer 92 shares
///   jobIdx 17 with Bard (records stay distinct: Bard 22, Dance 23). Mime 93 has no
///   learnable actions; story-canonical unique jobs / monsters / Dark Knight are unmapped.
/// </summary>
internal sealed partial class Barrage
{
    /// <summary>Generic Thief job id (roster +0x02). A Thief's PRIMARY command is Steal
    /// (rec 14, a normal executor), so an injected Barrage renders + casts cleanly there --
    /// the one job proven end-to-end. Barrage is THIEF-ONLY by design (the special-executor
    /// jobs like Archer/Aim swallow it; see SpecialExecutorJobs). The card states "Thief Only".</summary>
    public const int ThiefJob = 83;

    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || sig.GrantCommandAbilityId <= 0) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>Barrage is granted ONLY to a Thief wielder (job 83). Enforces the card's
    /// "Thief Only" promise in code.</summary>
    public static bool IsEligibleWielder(int job) => job == ThiefJob;

    /// <summary>Jobs whose PRIMARY command is a special-cased executor that silently drops
    /// foreign ability ids at confirm time (the menu label renders, targeting/preview show
    /// positional basic-attack cosmetics, execution no-ops). PROVEN LIVE for Aim 2026-06-10:
    /// 358/102/146 all swallowed from rec 8 slots 1/9/10/11 on two units; the id whitelist is
    /// code-side (no writable tier table found). Jump shares Aim's tier-row structure; Items/
    /// Throw consult inventory; Arithmeticks has a bespoke picker — excluded conservatively.
    /// Future Archer path: the +0x07 secondary-command route (bypasses the primary executor).</summary>
    private static readonly int[] SpecialExecutorJobs = { 75, 77, 87, 89, 90 }; // Chemist, Archer, Dragoon, Ninja, Arithmetician

    /// <summary>Resolve a roster job byte (+0x02) to its JobCommand record id and its
    /// learned-bitfield jobIdx. Only the generic band 74..92 is mapped, MINUS the
    /// special-executor jobs; Mime (93, nothing learnable), story-canonical unique jobs,
    /// monsters, and Dark Knight return false.</summary>
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

    /// <summary>Resolve a SECONDARY command record (roster +0x07 holds the JobCommand rec id
    /// directly — live-read 14 with Steal mounted) to its learned jobIdx. Only normal-executor
    /// records are grantable: the generic wheel recs minus Items 6 / Aim 8 / Jump 18 / Throw 20
    /// / Arithmeticks 21 / Mime 24, plus the Mettle variants 25-27 (jobIdx 0; Mettle executed
    /// 358 in the morning proof).</summary>
    public static bool TryResolveSecondary(int secondaryRec, out int jobIdx)
    {
        jobIdx = -1;
        if (secondaryRec >= 25 && secondaryRec <= 27) { jobIdx = 0; return true; }   // Mettle variants
        if (secondaryRec < 5 || secondaryRec > 23) return false;
        if (secondaryRec is 6 or 8 or 18 or 20 or 21) return false;                  // special executors
        jobIdx = secondaryRec == 23 ? 17 : secondaryRec - 5;                          // Dance shares Bard's 17
        return true;
    }

    /// <summary>The grant target for a wielder: the PRIMARY job's record when it's a normal
    /// executor; otherwise fall back to the mounted SECONDARY command's record (LIVE-PROVEN
    /// 2026-06-10: Barrage cast from Steal mounted as an Archer's secondary). False when
    /// neither is grantable (e.g. Archer with Items secondary, or no secondary).</summary>
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

    /// <summary>Find the first empty action slot in a 16-byte ability array, paired with an
    /// ExtAb u16 (LE-composed). A slot is empty when its byte is 0 AND its extend bit is 0
    /// (byte 0 + extend bit = ability 256). Returns the 1-indexed slot (1..16) or -1.</summary>
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

    /// <summary>The extend bit for slot i (0-indexed) in the LE-composed ExtAb u16.
    /// MSB-first PER BYTE: byte0 = slots 1-8, byte1 = slots 9-16.
    /// Slot 1 (i=0) -> 0x0080, slot 9 (i=8) -> 0x8000, slot 10 (i=9) -> 0x4000.</summary>
    public static ushort ExtendBit(int slotIdx)
        => (ushort)((0x80 >> (slotIdx % 8)) << (8 * (slotIdx / 8)));

    /// <summary>True when the slot still needs injection (idempotent check): for ability 358
    /// the byte must be 102 AND the slot's own extend bit must be set.</summary>
    public static bool NeedsInject(byte slotByte, ushort extAb, int slotIdx)
        => slotByte != (byte)102 || (extAb & ExtendBit(slotIdx)) == 0;

    /// <summary>Byte index within the 2-byte learned action bitfield for the given 1-indexed
    /// slot. Slots 1-8 live in byte 0; slots 9-16 in byte 1 (same shape as the extend bytes).</summary>
    public static int LearnedByteIndex(int slotIdx1)
        => slotIdx1 <= 8 ? 0 : 1;

    /// <summary>The bitmask within the learned byte for the given 1-indexed slot, MSB-first.
    /// Slot 9 = byte1 0x80 (live-proven: the game's own Barrage purchase targets this bit).</summary>
    public static byte LearnedBitMask(int slotIdx1)
        => (byte)(1 << (7 - (slotIdx1 - 1) % 8));

    /// <summary>Write the ability into the given slot of a JobCommand record.
    /// <paramref name="flagAddr"/> = start of the 3-byte flag prefix (ExtAb b0, ExtAb b1, ExtRSM).
    /// <paramref name="abBase"/> = start of the 16 ability bytes (flagAddr + 3).
    /// ORs the slot's extend bit, preserving the record's other extend bits. VirtualQuery-guarded.</summary>
    public static void InjectSlot(long flagAddr, long abBase, int slotIdx, int abilityId)
    {
        long byteAddr = abBase + slotIdx;
        if (!Mem.Writable(byteAddr, 1)) return;
        Mem.W8(byteAddr, SlotByte(abilityId));

        if (!Mem.Readable(flagAddr, 2)) return;
        ushort extAb = (ushort)(Mem.U8(flagAddr) | (Mem.U8(flagAddr + 1) << 8));
        ushort bit = ExtendBit(slotIdx);
        if (abilityId >= 256) extAb |= bit; else extAb = (ushort)(extAb & ~bit);
        if (!Mem.Writable(flagAddr, 2)) return;
        Mem.W8(flagAddr, (byte)(extAb & 0xFF));
        Mem.W8(flagAddr + 1, (byte)(extAb >> 8));
    }

    /// <summary>Write the 25 saved bytes (flags + ability + RSM) back to the record.
    /// <paramref name="flagAddr"/> = start of the 3-byte flag prefix. VirtualQuery-guarded.</summary>
    public static void RestoreRecord(long flagAddr, byte[] saved)
    {
        if (!Mem.Writable(flagAddr, saved.Length)) return;
        Mem.WriteBytes(flagAddr, saved);
    }
}

/// <summary>Tracks the saved JobCommand record (never-re-save invariant) and the slot used for
/// injection. The save is keyed by record id; only the first save is honored (a re-save would
/// overwrite the original bytes with injected state, making restore restore the injection).</summary>
internal sealed class BarrageState
{
    private readonly Dictionary<int, byte[]> _saved = new();
    private int _slotIdx = -1;   // 0-indexed slot used for injection (-1 = not injected)

    /// <summary>True if we have saved the original record bytes for this record id.</summary>
    public bool HasSaved(int recId) => _saved.ContainsKey(recId);

    /// <summary>Save the original record bytes. No-ops if already saved (never-re-save).</summary>
    public void Save(int recId, byte[] record)
    {
        if (_saved.ContainsKey(recId)) return;
        var copy = new byte[record.Length];
        System.Array.Copy(record, copy, record.Length);
        _saved[recId] = copy;
    }

    /// <summary>The saved bytes for the given record id, or null.</summary>
    public byte[]? GetSaved(int recId) => _saved.TryGetValue(recId, out var b) ? b : null;

    /// <summary>The 0-indexed slot currently used for injection, or -1.</summary>
    public int SlotIdx
    {
        get => _slotIdx;
        set => _slotIdx = value;
    }

    /// <summary>Clear saved state (on grant end / job change).</summary>
    public void Clear() { _saved.Clear(); _slotIdx = -1; }
}
