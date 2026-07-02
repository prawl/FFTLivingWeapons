using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Per-battle ADDITIVE cache of the static-array enemy fingerprint set
/// (LivingWeapon/EnemyFingerprintCache.cs). Mirrors EnemyOracle's production-proven additive
/// capture: TickField only ever unions the latest scan in, never removes, so a one-tick
/// Readable() flap on the static array cannot make an enemy vanish mid-battle.
/// </summary>
public class EnemyFingerprintCacheTests
{
    private static FakeSparseMemory SeedEnemy()
    {
        var mem = new FakeSparseMemory();
        long slot = Offsets.ArrayReadBase;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = 400;
        mem.U8s[slot + Offsets.ALevel] = 40;
        mem.U8s[slot + Offsets.ABrave] = 75;
        mem.U8s[slot + Offsets.AFaith] = 55;
        return mem;
    }

    [Fact]
    public void TickField_captures_and_survives_a_readable_flap()
    {
        var mem = SeedEnemy();
        var cache = new EnemyFingerprintCache(mem);

        cache.TickField();
        Assert.True(cache.Contains((400, 40, 75, 55)));

        mem.ReadableAddrs.Remove(Offsets.ArrayReadBase + Offsets.AMaxHp);
        cache.TickField();
        Assert.True(cache.Contains((400, 40, 75, 55)),
            "the cache is additive -- a Readable flap on a later tick must not remove a captured fingerprint");
    }

    [Fact]
    public void Contains_false_for_never_captured_fingerprint()
    {
        var mem = SeedEnemy();
        var cache = new EnemyFingerprintCache(mem);

        cache.TickField();
        Assert.False(cache.Contains((999, 1, 1, 1)));
    }

    [Fact]
    public void ResetBattle_clears()
    {
        var mem = SeedEnemy();
        var cache = new EnemyFingerprintCache(mem);

        cache.TickField();
        Assert.True(cache.Contains((400, 40, 75, 55)));

        cache.ResetBattle();
        Assert.False(cache.Contains((400, 40, 75, 55)));
    }
}
