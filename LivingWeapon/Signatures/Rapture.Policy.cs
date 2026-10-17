using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Rod of Faith's "Rapture" signature. The arm/hold/expire loop is in Rapture.cs.
/// </summary>
internal sealed partial class Rapture
{
    /// <summary>True when the signature is configured (RaptureMove set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RaptureMove;

    /// <summary>Main-hand only. The weapon earns kills in any hand but grants its gift only from RRHand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>True when hp is strictly below pct of max (integer math, as in Signatures.ConditionMet).
    /// False on junk maxHp.</summary>
    public static bool IsBelow(int hp, int maxHp, double pct)
    {
        if (maxHp <= 0) return false;
        return hp * 100 < maxHp * (int)Math.Round(pct * 100);
    }

    /// <summary>Arm only for a living wielder below the threshold. At HP 0 the heal/revive code owns the unit.</summary>
    public static bool ShouldArm(int hp, int maxHp, double pct) => hp > 0 && IsBelow(hp, maxHp, pct);

    /// <summary>The 3-byte movement-field image with only the granted ability's bit set (movement takes
    /// one effective bit, so the grant replaces the field). Null when the id is outside the field.</summary>
    public static byte[]? FieldFor(int moveAbilityId)
    {
        if (!Signatures.ResolveMovement(moveAbilityId, out int off, out byte mask)) return null;
        var f = new byte[Signatures.MovementBytes];
        f[off] = mask;
        return f;
    }

    /// <summary>Reads the wielder's 3-byte movement field (+<see cref="Offsets.AMovement"/>). Null when unreadable.</summary>
    public static byte[]? ReadField(IGameMemory mem, long entryAddr)
    {
        long a = entryAddr + Offsets.AMovement;
        if (!mem.Readable(a, Signatures.MovementBytes)) return null;
        var f = new byte[Signatures.MovementBytes];
        for (int i = 0; i < f.Length; i++) f[i] = mem.U8(a + i);
        return f;
    }

    /// <summary>Guarded write of a movement field image in one WriteBytes call (a per-byte
    /// loop left a torn-value window). Used for both hold and restore; no-op on failure.</summary>
    public static void WriteField(IGameMemory mem, long entryAddr, byte[] field)
    {
        long a = entryAddr + Offsets.AMovement;
        if (!mem.Writable(a, field.Length)) return;
        mem.WriteBytes(a, field);
    }

    /// <summary>True if the granted bit is set. RaptureMoveId 243 is cut content (docs/research/FOLDABLE_ABILITIES.md),
    /// so the per-window SET/MISS log line settles whether the engine honors it (as with Spiritual Font).</summary>
    public static bool ReadBackSet(IGameMemory mem, long entryAddr, int moveAbilityId)
    {
        if (!Signatures.ResolveMovement(moveAbilityId, out int off, out byte mask)) return false;
        return (mem.U8(entryAddr + Offsets.AMovement + off) & mask) != 0;
    }

    /// <summary>True while the band entry still belongs to the armed wielder (brave/faith match arm time;
    /// level is skipped so a level-up keeps the hold). Units migrate between fixed band slots (see
    /// Maim.Drive), so every held write checks this first.</summary>
    public static bool SameUnit(IGameMemory mem, long entryAddr, (int lvl, int br, int fa) fp)
        => mem.U8(entryAddr + Offsets.ABrave) == fp.br && mem.U8(entryAddr + Offsets.AFaith) == fp.fa;

    /// <summary>Early release when the wielder is alive and back at or above the threshold. False on
    /// junk maxHp.</summary>
    public static bool HasRecovered(int hp, int maxHp, double pct)
        => maxHp > 0 && hp > 0 && !IsBelow(hp, maxHp, pct);
}

/// <summary>The Rapture window's latch. Saved pre-grant movement bytes, turn count at arm, and the last
/// band-entry address (the restore target). Only the first Arm counts, since re-arming while held would
/// save our own teleport bytes (the Maim trap).</summary>
internal sealed class RaptureState
{
    /// <summary>True while a window is live.</summary>
    public bool Held { get; private set; }

    /// <summary>The wielder's own movement bytes, captured at arm.</summary>
    public byte[]? SavedField { get; private set; }

    /// <summary>The wielder's completed-turn count at arm.</summary>
    public int BaselineTurns { get; private set; }

    /// <summary>The last located band-entry address, refreshed each tick.</summary>
    public long Addr { get; set; }

    /// <summary>The armed wielder's (level,brave,faith), checked by Rapture.SameUnit before every held write.</summary>
    public (int lvl, int br, int fa) Fp { get; private set; }

    /// <summary>The image the window holds (Rapture.FieldFor), fixed at arm.</summary>
    public byte[]? GrantField { get; private set; }

    /// <summary>Open the window. No-op while held.</summary>
    public void Arm(long addr, byte[] savedField, int baselineTurns, (int lvl, int br, int fa) fp,
                    byte[] grant)
    {
        if (Held) return;
        Held = true;
        SavedField = (byte[])savedField.Clone();
        BaselineTurns = baselineTurns;
        Addr = addr;
        Fp = fp;
        GrantField = (byte[])grant.Clone();
    }

    /// <summary>Close the window after the restore.</summary>
    public void Release()
    {
        Held = false;
        SavedField = null;
        BaselineTurns = 0;
        Addr = 0;
        Fp = default;
        GrantField = null;
    }
}
