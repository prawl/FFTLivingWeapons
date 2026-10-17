using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Per-battle additive cache of the static-array enemy fingerprint set, taken from
/// Band.EnemyFingerprints so the bounds match (including its inclusive-2000 mhp drift). TickField
/// only unions in, never removes, so a one-tick Readable() flap delays an addition instead of
/// dropping an enemy on the damage tick. Same additive capture as EnemyOracle.
/// EagleEye, Larceny, Maim and Ricochet still rebuild per tick and could migrate; Plague cannot (its
/// own scan, different mhp bound). Puppeteer migrated 2026-07-03 (Kobu's cache/rearm shape).</summary>
internal sealed class EnemyFingerprintCache
{
    private readonly IGameMemory _mem;
    private readonly HashSet<(int mhp, int lvl, int br, int fa)> _set = new();

    public EnemyFingerprintCache(IGameMemory mem) => _mem = mem;

    /// <summary>Union in the current enemy fingerprint set; call once per on-field tick.</summary>
    public void TickField() => _set.UnionWith(Band.EnemyFingerprints(_mem));

    public bool Contains((int mhp, int lvl, int br, int fa) fp) => _set.Contains(fp);

    public void ResetBattle() => _set.Clear();
}
