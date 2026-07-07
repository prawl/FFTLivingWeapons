using System.ComponentModel;

namespace LivingWeapon.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Treasure Master Always On")]
    [Description("Treasure Master (auto-marks the battle tiles that hide Move-Find treasure) is " +
                 "normally enabled by equipping the Scholar's Ring on any party member. " +
                 "Turn this on to force-enable it on every map without the ring. Default: off.")]
    [DefaultValue(false)]
    public bool TreasureAlwaysOn { get; set; } = false;

    [DisplayName("Tier-Up Banner Toasts")]
    [Description("When a Living Weapon's kill tally crosses a tier, announce it by hijacking the " +
                 "game's own battle callout bubble the next time one naturally shows. Turn this off " +
                 "to disable the announcement entirely (growth itself is unaffected). Default: on.")]
    [DefaultValue(true)]
    public bool BannerToasts { get; set; } = true;

    [DisplayName("Dev: Seed All Kill Tallies")]
    [Description("DEV builds only: pre-seed every weapon's kill tally to +3 on load for fast testing. " +
                 "Turn off to start every weapon at 0 kills so tiers are earned naturally. Has no " +
                 "effect in release builds (seeding is compiled out).")]
    [DefaultValue(true)]
    public bool DevSeedKills { get; set; } = true;

    [DisplayName("Verbose Diagnostic Log")]
    [Description("Show Debug-tier diagnostic lines (per-tick battle-event timeline, signature " +
                 "verdict dumps, etc.) on the Reloaded console too, not just in livingweapon.log. " +
                 "The log FILE always has this detail regardless of this setting -- this only " +
                 "controls console noise. Default: off.")]
    [DefaultValue(false)]
    public bool VerboseLog { get; set; } = false;

    [DisplayName("Dev: Force Fingerprint Mismatch")]
    [Description("DEV builds only: makes the mod stand down at launch as if the game had been " +
                 "patched, to test the startup fingerprint guard's loud stand-down path without a " +
                 "real game patch. Has no effect in release builds (the knob does not exist there).")]
    [DefaultValue(false)]
    public bool DevForceFingerprintMismatch { get; set; } = false;
}
