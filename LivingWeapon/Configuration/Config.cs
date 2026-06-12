using System.ComponentModel;

namespace LivingWeapon.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Treasure Master Always On")]
    [Description("Treasure Master highlights the battlefield tiles that hide treasure " +
                 "(Move-Find items) so you can see where to search. Turn this on to enable " +
                 "it on every map. Default: off.")]
    [DefaultValue(false)]
    public bool TreasureAlwaysOn { get; set; } = false;
}
