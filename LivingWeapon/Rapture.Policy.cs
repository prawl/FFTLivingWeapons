using System;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Rod of Faith's "Rapture" signature -- no live state.
/// The stateful arm/hold/expire orchestrator lives in Rapture.cs.
/// </summary>
internal sealed partial class Rapture
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || !sig.RaptureMove) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>The HP gate, integer math (the proven Signatures.ConditionMet shape): true when
    /// hp is strictly below pct of max. Safe false on junk maxHp.</summary>
    public static bool IsBelow(int hp, int maxHp, double pct)
    {
        if (maxHp <= 0) return false;
        return hp * 100 < maxHp * (int)Math.Round(pct * 100);
    }

    /// <summary>Arm only on a LIVING wielder below the threshold -- a corpse needs no teleport
    /// (and HP 0 -&gt; the heal/revive machinery must never see our writes).</summary>
    public static bool ShouldArm(int hp, int maxHp, double pct) => hp > 0 && IsBelow(hp, maxHp, pct);

    /// <summary>The window is over once the wielder has completed the configured turns.</summary>
    public static bool IsExpired(int turnsSinceArm, int windowTurns) => turnsSinceArm >= windowTurns;

    /// <summary>Re-arm hysteresis: after a spent window the wielder's HP must RECOVER to/above
    /// the threshold before a new drop can arm again -- otherwise a wielder parked at low HP
    /// holds Master Teleportation forever and the window is a lie.</summary>
    public static bool CanRearm(bool rearmReady, bool below) => rearmReady || !below;

    /// <summary>The 3-byte movement-field image holding ONLY the granted ability's bit (movement
    /// is exactly-one-effective; the grant REPLACES the field, the restore brings the player's
    /// pick back). Null when the id falls outside the movement field.</summary>
    public static byte[]? FieldFor(int moveAbilityId)
    {
        if (!Signatures.ResolveMovement(moveAbilityId, out int off, out byte mask)) return null;
        var f = new byte[Signatures.MovementBytes];
        f[off] = mask;
        return f;
    }

    /// <summary>Read the wielder's current 3-byte movement field off its band entry
    /// (+<see cref="Offsets.AMovement"/>). Null when unreadable (entry moved/freed).</summary>
    public static byte[]? ReadField(long entryAddr)
    {
        long a = entryAddr + Offsets.AMovement;
        if (!Mem.Readable(a, Signatures.MovementBytes)) return null;
        var f = new byte[Signatures.MovementBytes];
        for (int i = 0; i < f.Length; i++) f[i] = Mem.U8(a + i);
        return f;
    }

    /// <summary>Guarded write of a 3-byte movement field image to the band entry. Used for both
    /// the hold (teleport image) and the restore (saved image); fail-safe no-op.</summary>
    public static void WriteField(long entryAddr, byte[] field)
    {
        long a = entryAddr + Offsets.AMovement;
        if (!Mem.Writable(a, field.Length)) return;
        for (int i = 0; i < field.Length; i++) Mem.W8(a + i, field[i]);
    }
}

/// <summary>The Rapture window's latch: the saved (pre-grant) movement bytes, the turn count at
/// arm time, and the last located band-entry address (the restore target -- entries relocate).
/// NEVER-RE-SAVE invariant (the Maim trap): arming while held would capture our own teleport
/// bytes and the restore would restore the grant; only the first Arm is honored.</summary>
internal sealed class RaptureState
{
    /// <summary>True while a window is live (saved bytes are held).</summary>
    public bool Held { get; private set; }

    /// <summary>The wielder's own movement bytes, captured once at arm time.</summary>
    public byte[]? SavedField { get; private set; }

    /// <summary>The wielder's completed-turn count when the window armed.</summary>
    public int BaselineTurns { get; private set; }

    /// <summary>The last located band-entry address (kept fresh each tick; restore target).</summary>
    public long Addr { get; set; }

    /// <summary>Open the window. No-ops while held (never-re-save).</summary>
    public void Arm(long addr, byte[] savedField, int baselineTurns)
    {
        if (Held) return;
        Held = true;
        SavedField = (byte[])savedField.Clone();
        BaselineTurns = baselineTurns;
        Addr = addr;
    }

    /// <summary>Close the window (after the restore).</summary>
    public void Release()
    {
        Held = false;
        SavedField = null;
        BaselineTurns = 0;
        Addr = 0;
    }
}
