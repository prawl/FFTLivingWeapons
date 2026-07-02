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
}
