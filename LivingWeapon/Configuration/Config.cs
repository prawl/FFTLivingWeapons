using System.ComponentModel;

namespace LivingWeapon.Configuration;

/// <summary>
/// Mod configuration. TreasureAlwaysOn is the sole player-facing toggle exposed to the Reloaded-II
/// launcher via Configurator. LW-52 removed the BannerToasts, DevSeedKills, and VerboseLog toggles
/// from the launcher so players cannot switch off designed behavior: toasts are always on,
/// dev-seeding is governed by the LWDEV compile flag, and the console logs at Info (the log FILE
/// still records every line). Those behaviors keep their compiled Tuning defaults; only the
/// player-visible buttons are gone.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Treasure Master Always On")]
    [Description("Treasure Master (auto-marks the battle tiles that hide Move-Find treasure) is " +
                 "normally enabled by equipping the Scholar's Ring on any party member. " +
                 "Turn this on to force-enable it on every map without the ring. Default: off.")]
    [DefaultValue(false)]
    public bool TreasureAlwaysOn { get; set; } = false;
}
