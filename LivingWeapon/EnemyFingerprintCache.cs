using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-battle ADDITIVE cache of the static-array enemy fingerprint set. Delegates to
/// Band.EnemyFingerprints so the bounds stay byte-identical (including its documented
/// inclusive-2000 mhp drift) -- mirrors EnemyOracle's production-proven additive capture:
/// TickField only ever unions the latest scan in, never removes, so a transient Readable()
/// flap only delays an addition and self-heals next tick, and mid-battle reinforcements
/// still get captured. Consumers check Contains at event time against the cache instead of
/// a per-tick rebuild, so a one-tick read flap can no longer make an enemy vanish from the
/// set on the exact damage tick.
///
/// Follow-up: CharmLock, EagleEye, Larceny, Maim, and Ricochet still rebuild
/// Band.EnemyFingerprints per tick and can migrate to this cache later (Plague is NOT a
/// candidate -- it runs its own scan with a different mhp bound); migrating them is out of
/// scope for this change. (Puppeteer migrated 2026-07-03 -- Kobu's cache/rearm shape.)
/// </summary>
internal sealed class EnemyFingerprintCache
{
    private readonly IGameMemory _mem;
    private readonly HashSet<(int mhp, int lvl, int br, int fa)> _set = new();

    public EnemyFingerprintCache(IGameMemory mem) => _mem = mem;

    /// <summary>Union in the current static-array enemy fingerprint set. Call once per
    /// on-field tick.</summary>
    public void TickField() => _set.UnionWith(Band.EnemyFingerprints(_mem));

    public bool Contains((int mhp, int lvl, int br, int fa) fp) => _set.Contains(fp);

    public void ResetBattle() => _set.Clear();
}
