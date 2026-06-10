using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Yoichi Bow's "Barrage" signature -- no memory access.
/// The stateful wielder scan and JobCommand record inject/restore live in Barrage.cs.
///
/// PROVEN layout (barrage_probe.py, memory barrage-jobcommand-injection):
///   ABILITY_BASE points at record 0's AbilityId1 byte; record r starts at ABILITY_BASE + r*25.
///   Per-record: [ExtAb u16 LE][ExtRSM u8] at -3 bytes, then [AbilityId1..16 as bytes][RSMId1..6].
///   Extend bits are MSB-first per byte: slot i (0-indexed) uses bit (15-i) of the ExtAb u16.
///   Ability 358: byte = 102 (358 & 0xFF), extend bit must be set.
///   Wrong-bit tell = ghost "Vengeance" menu entries (the probe's observed failure mode).
///
/// NEEDS-LIVE-VERIFY (noted in CSV sigNote):
///   1. Generic-job record mapping: job id -> JobCommand record index (Ramza Mettle proven at
///      rec 27 for job id 3; generic jobs need live verification via barrage_probe.py dump).
///   2. The learned-bit jobIdx: roster +0x32 + jobIdx*3, slot 10 = byte1 0x40 (learned_probe.py
///      is the oracle; jobIdx == job id is assumed here but needs live confirmation).
/// </summary>
internal sealed partial class Barrage
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || sig.GrantCommandAbilityId <= 0) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>Find the first empty action slot in a 16-byte ability array, paired with an ExtAb u16.
    /// A slot is empty when its byte is 0 AND its extend bit is 0 (a byte-0 + extend bit means ability 256).
    /// Returns the 1-indexed slot number (1..16) or -1 if no empty slot.</summary>
    public static int FindEmptySlot(byte[] abilityBytes, ushort extAb)
    {
        for (int i = 0; i < 16; i++)
        {
            ushort bit = ExtendBit(i);
            if (abilityBytes[i] == 0 && (extAb & bit) == 0) return i + 1;
        }
        return -1;
    }

    /// <summary>The byte value for the given ability id (low 8 bits).</summary>
    public static byte SlotByte(int abilityId) => (byte)(abilityId & 0xFF);

    /// <summary>The extend bit in the ExtAb u16 for slot i (0-indexed), MSB-first.
    /// Slot 0 -> bit 15 (0x8000), slot 15 -> bit 0 (0x0001).</summary>
    public static ushort ExtendBit(int slotIdx) => (ushort)(1 << (15 - slotIdx));

    /// <summary>True when the slot still needs injection (idempotent check).
    /// Already-injected = slotByte matches AND extend bit is set (for abilities >= 256).</summary>
    public static bool NeedsInject(byte slotByte, ushort extAb, int slotIdx)
    {
        ushort bit = ExtendBit(slotIdx);
        // The slot is correctly injected when byte == expected AND the extend bit state is correct.
        // For ability 358 (>= 256): both the byte (102) and the extend bit must be set.
        return slotByte != (byte)102 || (extAb & bit) == 0;
    }

    /// <summary>Byte index within the 2-byte action-ability bitfield (bytes 0-1) for the given
    /// 1-indexed slot. Slots 1-8 live in byte 0; slots 9-16 live in byte 1. MSB-first.</summary>
    public static int LearnedByteIndex(int slotIdx1)
        => slotIdx1 <= 8 ? 0 : 1;

    /// <summary>The bitmask within the learned byte for the given 1-indexed slot, MSB-first.
    /// Slot 1 = bit 7 (0x80) of byte 0; slot 9 = bit 7 (0x80) of byte 1; slot 10 = bit 6 (0x40) of byte 1.
    /// Formula: mask = 1 << (8 - ((slotIdx1 - 1) % 8) - 1) = 1 << (7 - ((slotIdx1-1) % 8)).</summary>
    public static byte LearnedBitMask(int slotIdx1)
        => (byte)(1 << (7 - (slotIdx1 - 1) % 8));

    /// <summary>Write ability 358 into the given slot of a JobCommand record's in-memory buffer.
    /// <paramref name="flagAddr"/> = start of the 3-byte flag prefix (ExtAb_lo, ExtAb_hi, ExtRSM).
    /// <paramref name="abBase"/> = start of the 16 ability bytes (flagAddr + 3).
    /// VirtualQuery-guarded.</summary>
    public static void InjectSlot(long flagAddr, long abBase, int slotIdx, int abilityId)
    {
        // Write the ability byte.
        long byteAddr = abBase + slotIdx;
        if (!Mem.Writable(byteAddr, 1)) return;
        Mem.W8(byteAddr, SlotByte(abilityId));

        // Update the ExtAb u16: read current, set/clear the extend bit.
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

/// <summary>Tracks the saved JobCommand record (never-re-save invariant) and the slot used for injection.
/// The save is keyed by record id; only the first save is honored (re-save would overwrite original bytes
/// with injected state, making a restore restore injected zeros instead of the real ability list).</summary>
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

    /// <summary>Clear saved state (on battle exit or grant end).</summary>
    public void Clear() { _saved.Clear(); _slotIdx = -1; }
}
