namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Umbral Rod's "Life Sap" signature -- no memory access.
/// The stateful kill-diff watcher and the wielder locate live in LifeSap.cs.
/// </summary>
internal sealed partial class LifeSap
{
    /// <summary>True when the signature is configured (LifeSapOnKill set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.LifeSapOnKill;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>Forwards to <see cref="BandHeal.HealAmount"/> (LW-149 stage G: promoted to the
    /// shared band-heal core so Benediction/Renewal/SpiritualFont/Wyrmblood stop borrowing
    /// LifeSap's name). Kept here so LifeSap's own tests and any missed caller keep working.</summary>
    public static int HealAmount(int maxHp, double pct) => BandHeal.HealAmount(maxHp, pct);

    /// <summary>Forwards to <see cref="BandHeal.NewHp"/> (see <see cref="HealAmount"/>'s doc for why).</summary>
    public static int NewHp(int hp, int maxHp, int heal) => BandHeal.NewHp(hp, maxHp, heal);

    /// <summary>Forwards to <see cref="BandHeal.WriteHp"/> (see <see cref="HealAmount"/>'s doc for why).</summary>
    public static void WriteHp(IGameMemory mem, long entryAddr, int newHp) => BandHeal.WriteHp(mem, entryAddr, newHp);
}
