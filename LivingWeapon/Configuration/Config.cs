using System.ComponentModel;

namespace LivingWeapon.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Treasure Master Always On")]
    [Description("When enabled, treasure tiles are highlighted on every map without " +
                 "requiring a Scholar's Ring to be equipped. Default: on. Turn off only " +
                 "if you want the vanilla ring-gated experience.")]
    [DefaultValue(true)]
    public bool TreasureAlwaysOn { get; set; } = true;
}
