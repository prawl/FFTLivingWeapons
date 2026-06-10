using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Living-Weapon growth tuning. Kept in one place so detection, growth, and display agree.
///
/// Kill thresholds are build-gated: a DEV build (BuildLinked.ps1 passes -p:LwDev=true, which
/// defines LWDEV) uses {1,2,3} so a weapon hits P3 in three kills, AND pre-seeds every weapon
/// to P2 on load -- one kill short of P3 -- so a single kill flips the P3 grant on, live. A
/// PRODUCTION build (Publish.ps1, no flag) uses the real curve {5,20,50} and seeds nothing.
/// </summary>
internal static class Tuning
{
    /// <summary>Both threshold sets, ALWAYS compiled (so a test can reason about the dev curve even
    /// though tests compile under prod). The active one is selected by the LWDEV flag below.</summary>
    public static readonly int[] DevThresholds = { 1, 2, 3 };    // P3 by the third kill (fast verification)
    public static readonly int[] ProdThresholds = { 5, 20, 50 }; // escalating: a fast taste at P, an aspirational P3
#if LWDEV
    public static readonly int[] KillThresholds = DevThresholds;
    /// <summary>DEV: floor every known weapon to <see cref="DevKillSeed"/> kills on load.</summary>
    public const bool DevSeedAllKills = true;
    /// <summary>DEV: per-tick battle-event timeline (damage/heal/move) in the log.</summary>
    public const bool VerboseEvents = true;
    /// <summary>DEV: Spiritual Font fires unconditionally every ~10s in battle (trigger bypass)
    /// -- on-screen proof of the band addressing + HP write + provisional MP pair. External
    /// probes are Denuvo-walled (RPM reads 0 units while the runtime sees 14), so the DLL is
    /// the only instrument that can run this experiment.</summary>
    public const bool FontDevPulse = true;
#else
    public static readonly int[] KillThresholds = ProdThresholds;
    /// <summary>Production seeds nothing -- the wielder earns every tier.</summary>
    public const bool DevSeedAllKills = false;
    /// <summary>Production logs stay lean: kills/turns/grants only, no per-tick events.</summary>
    public const bool VerboseEvents = false;
    /// <summary>Never in production.</summary>
    public const bool FontDevPulse = false;
#endif

    /// <summary>DEV seed floor: every weapon starts at least this many kills. 3 (== P3 under the dev
    /// thresholds) so every +3 signature is live the moment the weapon is equipped.</summary>
    public const int DevKillSeed = 3;

    /// <summary>kills -> tier (0..3) against the active thresholds, checked high to low.</summary>
    public static int TierFor(int kills) => TierForIn(kills, KillThresholds);

    /// <summary>kills -> tier (0..3) against a given threshold set (lets tests check the dev curve).</summary>
    public static int TierForIn(int kills, int[] thresholds) =>
        kills >= thresholds[2] ? 3 : kills >= thresholds[1] ? 2 : kills >= thresholds[0] ? 1 : 0;

    /// <summary>DEV ONLY: floor every known weapon's kill count to <paramref name="floor"/>. Purely
    /// additive -- never lowers an already-higher count (so a weapon that actually climbed past it
    /// keeps its progress). Lets every weapon sit at max tier for fast in-game testing.</summary>
    public static void SeedKills(IEnumerable<int> weaponIds, Dictionary<int, int> kills, int floor)
    {
        foreach (int id in weaponIds)
            if (!kills.TryGetValue(id, out int k) || k < floor) kills[id] = floor;
    }

    /// <summary>tier -> bonus as a fraction of the wielder's natural stat (PA / MA).
    /// Deliberately CONSERVATIVE: an investment mechanic must start under-tuned, because nerfing
    /// earned (kill-grown) power is the most-hated kind of nerf. Easier to buff up than claw back.</summary>
    public static readonly double[] Factor = { 0.00, 0.10, 0.20, 0.30 };

    /// <summary>Speed grows gentler still -- it double-dips (damage AND turn frequency).</summary>
    public static readonly double[] SpeedFactor = { 0.00, 0.05, 0.10, 0.15 };

    /// <summary>tier -> the 2-char name suffix painted on the card ("  " renders as nothing).</summary>
    public static readonly string[] Suffix = { "  ", "+ ", "+2", "+3" };

    /// <summary>Life Sap (Umbral Rod +3): fraction of the wielder's max HP restored when a kill
    /// is credited to the rod (clamped at full; never revives).</summary>
    public const double LifeSapPct = 0.25;

    /// <summary>Wyrmblood (Dragon Rod +3): each splash target mends its OWN maxHP / this divisor
    /// per wielder turn -- 8 == the vanilla Regen rate.</summary>
    public const int WyrmbloodDiv = 8;

    /// <summary>Spiritual Font (Wellspring Rod +3): fraction of max HP the wielder regains at a
    /// completed-turn edge where their grid position changed (the runtime writes the restore
    /// itself -- the engine honors only ONE movement passive, so the font bits are retired).</summary>
    public const double FontHpPct = 0.10;

    /// <summary>Spiritual Font: fraction of max MP regained on the same moved-turn edge. MP writes
    /// ride the PROVISIONAL band +0x18/+0x1A pair, gated per battle (SpiritualFont.MpLayoutOk).</summary>
    public const double FontMpPct = 0.10;

    /// <summary>Rapture (Rod of Faith +3): the window arms when the wielder's HP drops strictly
    /// below this fraction of max. Held UNTIL RECOVERY (no turn cap -- the 3-turn clock was
    /// retired 2026-06-10: the band CT it read never ticked live, while the recovery release
    /// was player-verified the same session).</summary>
    public const double RaptureHpPct = 0.30;

    /// <summary>Rapture: the granted movement ability -- 243 = Master Teleportation (ability.en
    /// key 499). CONFIRMED LIVE 2026-06-10: the player teleported, so the engine honors the bit.
    /// Fallback: flip to 242 (plain Teleport) if the arm-time read-back ever logs MISS.</summary>
    public const int RaptureMoveId = 243;

    /// <summary>Caster gear grows Magick Attack instead of Physical (a mage kills with spells).</summary>
    public static bool IsCaster(string category) => category == "Rod" || category == "Staff";

    /// <summary>Missing-HP formulas ignore every stat -> no growth lever.</summary>
    public static bool SkipFormula(int formula) => formula == 67 || formula == 69;

    /// <summary>Speed-scaling weapons (Swiftfang / Swiftedge).</summary>
    public static bool IsSpeedFormula(int formula) => formula == 99;

    /// <summary>Magic-cast weapons (magic guns) scale off Magick Attack.</summary>
    public static bool IsMagicCastFormula(int formula) => formula == 4;
}
