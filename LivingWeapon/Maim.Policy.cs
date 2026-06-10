using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Huntress's "Maim" signature -- no memory access.
/// The stateful victim-latch, hold-zero loop, and CT-based expiry live in Maim.cs.
/// </summary>
internal sealed partial class Maim
{
    /// <summary>Band-relative offset of the reaction bitfield (4 bytes, u32 little-endian).
    /// Derived: CReaction(0x94) - BandEntry(0x1C) = 0x78. Proven live (memory reaction-suppression-cripple).</summary>
    public const int ReactionBandOff = Offsets.AReaction;

    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || sig.CrippleTurns <= 0) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>True when the struck unit is an enemy (never latch allies).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>A completed turn = the victim's CT was near-full and has since reset notably lower.
    /// Mirrors CharmLock.IsTurn exactly (same probe proven for both use cases).</summary>
    public static bool IsTurn(int lastCt, int curCt) => lastCt >= 90 && curCt < 70;

    /// <summary>True when the held victim should have its saved bytes re-fetched on a new hit.
    /// The trap: if already held, the reaction field is ZEROED -- re-reading it would save zeros
    /// and the restore would wipe the reaction permanently. Never re-save while a hold is active.</summary>
    public static bool ShouldResave(bool isHeld) => !isHeld;

    /// <summary>Write zeros to the victim's reaction field -- all 4 bytes. VirtualQuery-guarded.</summary>
    public static void HoldZero(long addr)
    {
        long ra = addr + ReactionBandOff;
        if (!Mem.Writable(ra, 4)) return;
        Mem.W8(ra,     0);
        Mem.W8(ra + 1, 0);
        Mem.W8(ra + 2, 0);
        Mem.W8(ra + 3, 0);
    }

    /// <summary>Write the original reaction bytes back. VirtualQuery-guarded.</summary>
    public static void Restore(long addr, uint saved)
    {
        long ra = addr + ReactionBandOff;
        if (!Mem.Writable(ra, 4)) return;
        Mem.W8(ra,     (byte)(saved & 0xFF));
        Mem.W8(ra + 1, (byte)((saved >> 8) & 0xFF));
        Mem.W8(ra + 2, (byte)((saved >> 16) & 0xFF));
        Mem.W8(ra + 3, (byte)(saved >> 24));
    }

    /// <summary>Read the 4-byte reaction field at <paramref name="addr"/> (little-endian).
    /// Returns 0 if the address is not readable.</summary>
    public static uint ReadReactionField(long addr)
    {
        long ra = addr + ReactionBandOff;
        if (!Mem.Readable(ra, 4)) return 0;
        byte b0 = Mem.U8(ra), b1 = Mem.U8(ra + 1), b2 = Mem.U8(ra + 2), b3 = Mem.U8(ra + 3);
        return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
    }
}

/// <summary>Per-victim tracking for the Maim latch: save address, fingerprint, saved reaction bytes,
/// turn counter, and last CT. Designed so multiple enemies can be maimed simultaneously (one per hit
/// during the acted period). The never-re-save trap: Latch no-ops when IsHeld is already true for the
/// same fingerprint -- the saved field holds original bytes, not the zeros we're currently writing.</summary>
internal sealed class MaimState
{
    private readonly Dictionary<(int mhp, int lvl, int br, int fa), MaimEntry> _held = new();

    /// <summary>True when this fingerprint is currently being held.</summary>
    public bool IsHeld((int mhp, int lvl, int br, int fa) fp) => _held.ContainsKey(fp);

    /// <summary>Latch a newly maimed victim. No-ops if the fingerprint is already held (never-re-save).</summary>
    public void Latch(long addr, (int mhp, int lvl, int br, int fa) fp, uint savedReaction)
    {
        if (_held.ContainsKey(fp)) return;   // never overwrite while a hold is active
        _held[fp] = new MaimEntry(Addr: addr, SavedReaction: savedReaction, TurnCount: 0, LastCt: 255);
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

    /// <summary>Re-hit while held: reset the turn counter, keep the saved reaction bytes.</summary>
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
