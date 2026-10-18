using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>What IconGlow needs from the heal hook, so tests can stand in for the native half.</summary>
internal interface IIconCacheHealer
{
    /// <summary>True once the hook is installed and the game has called through it at least once.</summary>
    bool Armed { get; }

    /// <summary>False while any screen build touched an icon cache inside the builder window.</summary>
    bool BuildersQuiet { get; }

    /// <summary>Names the hook may heal. Called before the hide so no lookup can miss untracked.</summary>
    void Track(IEnumerable<string> partsUris);
}

/// <summary>Touches nothing. Tests set <see cref="Armed"/> and read back what was tracked.</summary>
internal sealed class NoopIconCacheHealer : IIconCacheHealer
{
    public bool Armed { get; set; }
    public bool BuildersQuiet { get; set; } = true;
    public List<string> Tracked { get; } = new();

    public void Track(IEnumerable<string> partsUris) => Tracked.AddRange(partsUris);
}
