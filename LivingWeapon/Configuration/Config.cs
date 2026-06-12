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
}
