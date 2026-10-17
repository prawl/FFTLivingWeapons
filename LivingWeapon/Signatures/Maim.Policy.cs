using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Huntress's "Maim" signature, no memory access. Latch state and expiry live in Maim.cs.
/// </summary>
internal sealed partial class Maim
{
    /// <summary>Band-relative offset of the reaction bitfield, CReaction(0x94), BandEntry(0x1C) = 0x78 (memory reaction-suppression-cripple).</summary>
    public const int ReactionBandOff = Offsets.AReaction;

    /// <summary>True when the signature is configured (CrippleTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.CrippleTurns > 0;

    /// <summary>True when the struck unit is an enemy (never latch allies).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>True when a new hit should re-save the bytes; while held the field is zeroed and re-saving would wipe the reaction.</summary>
    public static bool ShouldResave(bool isHeld) => !isHeld;

    /// <summary>Zero all 4 reaction bytes in one WriteBytes call (four W8s left a torn-value window). Guarded.</summary>
    public static void HoldZero(IGameMemory mem, long addr)
    {
        long ra = addr + ReactionBandOff;
        if (!mem.Writable(ra, 4)) return;
        mem.WriteBytes(ra, new byte[4]);
    }

    /// <summary>Write the saved reaction bytes back in one WriteBytes call (same torn-value window). Guarded.</summary>
    public static void Restore(IGameMemory mem, long addr, uint saved)
    {
        long ra = addr + ReactionBandOff;
        if (!mem.Writable(ra, 4)) return;
        mem.WriteBytes(ra, new byte[]
        {
            (byte)(saved & 0xFF),
            (byte)((saved >> 8) & 0xFF),
            (byte)((saved >> 16) & 0xFF),
            (byte)(saved >> 24),
        });
    }

    /// <summary>Read the 4-byte little-endian reaction field; false if unreadable (a 0 sentinel looked like a real
    /// all-zero field). Callers refuse to latch on false (Maim.cs).</summary>
    public static bool TryReadReactionField(IGameMemory mem, long addr, out uint value)
    {
        value = 0;
        long ra = addr + ReactionBandOff;
        if (!mem.Readable(ra, 4)) return false;
        byte b0 = mem.U8(ra), b1 = mem.U8(ra + 1), b2 = mem.U8(ra + 2), b3 = mem.U8(ra + 3);
        value = (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        return true;
    }
}

/// <summary>Per-victim Maim tracking so several enemies can be maimed at once. Latch no-ops while held, since the live field then holds our zeros.</summary>
internal sealed class MaimState
{
    private readonly Dictionary<(int mhp, int lvl, int br, int fa), MaimEntry> _held = new();

    /// <summary>True when this fingerprint is currently being held.</summary>
    public bool IsHeld((int mhp, int lvl, int br, int fa) fp) => _held.ContainsKey(fp);

    /// <summary>Latch a maimed victim; no-op if already held. <paramref name="seedCt"/> is the current CT, so the first sample counts no phantom turn.</summary>
    public void Latch(long addr, (int mhp, int lvl, int br, int fa) fp, uint savedReaction, int seedCt = 0)
    {
        if (_held.ContainsKey(fp)) return;   // never overwrite while a hold is active
        _held[fp] = new MaimEntry(Addr: addr, SavedReaction: savedReaction, TurnCount: 0, LastCt: seedCt);
    }

    /// <summary>The saved (pre-maim) reaction value for a held fingerprint, or null.</summary>
    public uint? SavedReaction((int mhp, int lvl, int br, int fa) fp)
        => _held.TryGetValue(fp, out var e) ? e.SavedReaction : (uint?)null;

    /// <summary>The address stored for a held fingerprint, or 0.</summary>
    public long HeldAddr((int mhp, int lvl, int br, int fa) fp)
        => _held.TryGetValue(fp, out var e) ? e.Addr : 0;

    /// <summary>The CT observation stored for a held fingerprint.</summary>
    public int LastCt((int mhp, int lvl, int br, int fa) fp)
        => _held.TryGetValue(fp, out var e) ? e.LastCt : 0;

    /// <summary>Update last CT for a held fingerprint.</summary>
    public void UpdateCt((int mhp, int lvl, int br, int fa) fp, int ct)
    {
        if (_held.TryGetValue(fp, out var e)) _held[fp] = e with { LastCt = ct };
    }

    /// <summary>Count one turn for the victim.</summary>
    public void CountTurn((int mhp, int lvl, int br, int fa) fp)
    {
        if (_held.TryGetValue(fp, out var e)) _held[fp] = e with { TurnCount = e.TurnCount + 1 };
    }

    /// <summary>The number of victim turns counted since latch.</summary>
    public int TurnCount((int mhp, int lvl, int br, int fa) fp)
        => _held.TryGetValue(fp, out var e) ? e.TurnCount : 0;

    /// <summary>Re-hit while held. Reset the turn counter, keep the saved reaction bytes.</summary>
    public void Refresh((int mhp, int lvl, int br, int fa) fp)
    {
        if (_held.TryGetValue(fp, out var e)) _held[fp] = e with { TurnCount = 0 };
    }

    /// <summary>True when the victim has completed enough turns to expire the hold.</summary>
    public bool IsExpired((int mhp, int lvl, int br, int fa) fp, int crippleTurns)
        => _held.TryGetValue(fp, out var e) && e.TurnCount >= crippleTurns;

    /// <summary>All currently held fingerprints (for Drive iteration).</summary>
    public IEnumerable<(int mhp, int lvl, int br, int fa)> Held => _held.Keys;

    /// <summary>Remove the latch entry for a fingerprint (called after restore).</summary>
    public void Release((int mhp, int lvl, int br, int fa) fp) => _held.Remove(fp);

    /// <summary>Clear all latches (battle exit).</summary>
    public void Clear() => _held.Clear();

    private record struct MaimEntry(long Addr, uint SavedReaction, int TurnCount, int LastCt);
}
