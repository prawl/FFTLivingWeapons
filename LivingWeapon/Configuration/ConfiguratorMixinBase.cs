using System.IO;
using Reloaded.Mod.Interfaces;

namespace LivingWeapon.Configuration;

/// <summary>
/// Produces the <see cref="IUpdatableConfigurable"/> array Reloaded-II's property
/// grid needs.  ConfiguratorMixin can override MakeConfigurations if additional
/// config objects are needed in the future.
/// </summary>
public class ConfiguratorMixinBase
{
    public virtual IUpdatableConfigurable[] MakeConfigurations(string configFolder)
    {
        var path   = Path.Combine(configFolder, "Config.json");
        var config = Configurable<Config>.FromFile(path, "FFT Living Weapons Configuration");
        return new IUpdatableConfigurable[] { config };
    }
}

/// <summary>Override point for future multi-config scenarios.</summary>
public class ConfiguratorMixin : ConfiguratorMixinBase { }
