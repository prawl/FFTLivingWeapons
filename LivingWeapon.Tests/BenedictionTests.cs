using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Sanctus Staff's "Benediction" signature. While a +3 Sanctus Staff is the LAST PLAYER to act
/// (KillTracker.LastPlayerMainHand == 64), any HP RISE on a live ALLY is boosted by HealBoostPct%
/// (30% by default). The gate is the sticky last-player-actor latch, NOT a timing window: the latch
/// is set when a player acts and persists across enemy turns until the next PLAYER acts, so it spans
/// the multi-second charge of a queued Cure (the failure a time window could not cover). The boost is
/// computed on the OBSERVED restored HP (not the spell's nominal output), so an overheal yields no
/// bonus -- a deliberate design choice (no overheal inflation). The HP write reuses LifeSap.WriteHp /
/// LifeSap.NewHp (the band +0x14 path proven by Ricochet/Wyrmblood/LifeSap).
///
/// Pure jobs in Benediction.Policy.cs:
///   (1) IsActive: gates on HealBoostPct > 0 AND tier >= AtTier.
///   (2) BonusHeal: pct% of observed delta, 0 when delta &lt; 1 (no event).
///   (3) HealState.Observe: baselines first sighting silently; rise returns positive delta;
///       drop or same HP returns 0.
///   (4) HealState.Consume: records our write so the boost is not re-triggered next tick.
///   (5) LifeSap.NewHp as the shared clamp: never revives, clamps at maxHp; a 0-HP ally stays 0.
///
/// Stateful runtime in Benediction.cs: observe every valid ally band entry every tick (baselines
/// while inactive); while the latch names the Sanctus Staff, boost ally HP rises; consume every write;
/// per-fingerprint dedupe (band twins). The gate reads ONLY _tracker.LastPlayerMainHand -- no window,
/// no Acted byte, no fingerprint/Wielder.Locate. The load-bearing negative test flips the latch to a
/// NON-64 weapon id (never the fingerprint, which the new gate does not read).
/// </summary>
public class BenedictionTests
{
    // Pinned buffers are committed addresses in our own process so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature BeneSig(int healBoostPct = 30, int atTier = 3) =>
        new() { AtTier = atTier, HealBoostPct = healBoostPct, DisplayLabel = "Benediction" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Benediction.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_healBoostPct_is_zero()
        => Assert.False(Benediction.IsActive(new WeaponSignature { AtTier = 3, HealBoostPct = 0 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Benediction.IsActive(BeneSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Benediction.IsActive(BeneSig(atTier: 3), tier: 3));
        Assert.True(Benediction.IsActive(BeneSig(atTier: 3), tier: 4));
    }

    // ---- (2) BonusHeal ----

    [Theory]
    [InlineData(100, 30, 30)]    // 100 * 30 / 100 = 30
    [InlineData(10,  30, 3)]     // floor: 10*30/100 = 3
    [InlineData(3,   30, 0)]     // 3*30/100 = 0 (rounds to 0 by floor) -> 0 (delta < floor threshold)
    [InlineData(4,   30, 1)]     // 4*30/100 = 1 -> floor 1 minimum when delta >= 1 AND pct result >= 1
    [InlineData(0,   30, 0)]     // no heal event -> 0
    [InlineData(200, 30, 60)]
    [InlineData(100, 0,  0)]     // pct 0 -> 0
    public void BonusHeal_is_floor_pct_of_delta(int delta, int pct, int expected)
        => Assert.Equal(expected, Benediction.BonusHeal(delta, pct));

    // Small-delta floor: BonusHeal(1, 30) -- 1*30/100 = 0; does NOT force a floor-1 like ChipDamage
    // because 0% of a 1-HP heal is legitimately zero (the scale is different).
    [Fact]
    public void BonusHeal_delta_1_pct_30_is_zero()
        => Assert.Equal(0, Benediction.BonusHeal(1, 30));

    // ---- (3) HealState: HP-rise detection + first-sighting baseline ----

    [Fact]
    public void HealState_first_sighting_baselines_silently()
    {
        var state = new HealState(Offsets.BandSlots);
        Assert.Equal(0, state.Observe(slot: 0, currentHp: 80));
    }

    [Fact]
    public void HealState_rise_returns_positive_delta()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 60);             // baseline at 60
        Assert.Equal(40, state.Observe(0, 100));   // healed 40
    }

    [Fact]
    public void HealState_same_hp_returns_zero()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 80);
        Assert.Equal(0, state.Observe(0, 80));
    }

    [Fact]
    public void HealState_drop_returns_zero()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 100);
        Assert.Equal(0, state.Observe(0, 60));   // HP dropped -> not a heal event
    }

    [Fact]
    public void HealState_two_distinct_rises_both_fire()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 40);
        Assert.Equal(20, state.Observe(0, 60));   // first heal event
        Assert.Equal(30, state.Observe(0, 90));   // second distinct heal event
    }

    // ---- (4) HealState.Consume prevents re-trigger of our own write ----

    [Fact]
    public void Consume_prevents_re_boost_of_our_own_write()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 60);
        state.Observe(0, 100);                   // rise delta = 40
        state.Consume(0, 113);                   // we wrote 113 (100 + 30% bonus of 40 = 112, clamped)
        Assert.Equal(0, state.Observe(0, 113));  // our write is not a fresh heal event
    }

    [Fact]
    public void Consume_keeps_genuine_second_rise_visible()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 60);
        state.Observe(0, 100);
        state.Consume(0, 112);
        // A genuine second heal (different action) should still fire
        Assert.Equal(20, state.Observe(0, 132));
    }

    [Fact]
    public void HealState_ResetBattle_clears_baselines()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 60);
        state.ResetBattle();
        Assert.Equal(0, state.Observe(0, 100));  // first sight after reset: baseline, no event
    }

    // ---- (5) LifeSap.NewHp reused: clamp at max, never revive ----

    [Fact]
    public void BonusHeal_via_NewHp_never_exceeds_maxHp()
    {
        // Unit at HP 90, max 100. Observed heal of 20 (-> HP 90+20=110 but that's pre-observe;
        // the engine already wrote 100 and we observe 100). We compute bonus=BonusHeal(20,30)=6,
        // newHp = NewHp(100, 100, 6) = 100 (clamped). No overheal write.
        int bonus = Benediction.BonusHeal(20, 30);
        Assert.Equal(6, bonus);
        Assert.Equal(100, LifeSap.NewHp(100, 100, bonus));  // already at max -> no boost
    }

    [Fact]
    public void NewHp_leaves_dead_ally_at_zero()
    {
        // A 0-HP ally must never be accidentally revived by our boost, even with a positive bonus.
        int bonus = Benediction.BonusHeal(50, 30);  // = 15
        Assert.Equal(0, LifeSap.NewHp(0, 100, bonus));   // dead stays dead
    }

    [Fact]
    public void NewHp_clamps_boost_to_maxHp()
    {
        // A unit near full: hp=95, max=100, observed rise=10 (bonus=3).
        // newHp should be clamped to 100, not 98.
        int bonus = Benediction.BonusHeal(10, 30);  // = 3
        Assert.Equal(100, LifeSap.NewHp(95 + 10, 100, bonus));  // post-heal hp=105 clamped to 100; no boost
        // More useful: starting from the already-clamped engine hp (100), bonus lands on top:
        Assert.Equal(100, LifeSap.NewHp(100, 100, 3));  // full -> no write
    }

    // ---- Integration: FakeSparseMemory walk (Benediction.Tick) ----
    // These drive the REAL Tick path. The gate reads ONLY KillTracker.LastPlayerMainHand: when it
    // names the Sanctus Staff (64) an ally HP rise is boosted; flip it to ANY other weapon id and no
    // boost lands. There is no window, no Acted gate, no fingerprint/Wielder.Locate -- so the negative
    // test MUST move _lastPlayerMainHand off 64 (the load-bearing variable), not the fingerprint.
    // SeedAllyFp is still required (the ally-only filter reads the static-array fingerprints).

    private static void SeedBandEntry(FakeSparseMemory mem, long addr,
        int hp, int maxHp, int lvl, int br, int fa, int gx, int gy, bool writable = true)
    {
        mem.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)maxHp;
        mem.U8s[addr + Offsets.ALevel] = (byte)lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fa;
        mem.ReadableAddrs.Add(addr + Offsets.AHp);
        mem.U16s[addr + Offsets.AHp] = (ushort)hp;
        mem.U8s[addr + Offsets.AGx] = (byte)gx;
        mem.U8s[addr + Offsets.AGy] = (byte)gy;
        if (writable)
        {
            mem.WritableAddrs.Add(addr + Offsets.AHp);
            mem.WritableAddrs.Add(addr + Offsets.AHp + 1);
        }
    }

    // Plant a static-array ally fingerprint (player slot above EnemySlotMax) so Band.AllyFingerprints
    // recognizes a band unit with this (maxHp,lvl,br,fa) as a healable ally.
    private static void SeedAllyFp(FakeSparseMemory mem, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 1) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fa;
    }

    // The full active scenario: a +3 Sanctus ally at a real-position band slot, recognized as an ally,
    // with the last-player-actor latch set to <paramref name="mainHand"/> (default 64 = active). Flip
    // mainHand to a non-Sanctus id to exercise the gate. Acted is seeded for realism but NO test
    // depends on it -- the new gate never reads it.
    private static (Benediction bene, FakeSparseMemory mem, KillTracker tracker, long wAddr)
        BuildActiveScenario(int tier = 3, int hp = 50, int maxHp = 100, int allySlot = 24,
                            int lvl = 20, int br = 50, int fa = 50, int mainHand = Benediction.SanctusStaffId)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [Benediction.SanctusStaffId] = new WeaponMeta
            {
                Name = "Sanctus Staff", Wp = 5, Cat = "Staff", Formula = 1,
                Flavor = "A gilded stave", Signature = BeneSig()
            }
        };
        var kills = new Dictionary<int, int>
        {
            [Benediction.SanctusStaffId] = tier >= 1 && tier <= 3 ? Tuning.ProdThresholds[tier - 1] : 0
        };
        long wAddr = Band.Entry(allySlot);
        SeedBandEntry(mem, wAddr, hp, maxHp, lvl, br, fa, gx: 5, gy: 5);
        SeedAllyFp(mem, maxHp, lvl, br, fa);

        var tracker = new KillTracker(new Dictionary<int, int>(), mem, new HashSet<int>());
        tracker._lastPlayerMainHand = mainHand;   // the ONLY thing the gate reads

        var bene = new Benediction(meta, kills, tracker, mem: mem);
        return (bene, mem, tracker, wAddr);
    }

    // Read back the u16 HP our boost wrote (LifeSap.WriteHp writes two little-endian bytes via W8).
    private static bool TryReadWritten(FakeSparseMemory mem, long entryAddr, out int hp)
    {
        hp = 0;
        long a = entryAddr + Offsets.AHp;
        if (!mem.Written.ContainsKey(a)) return false;
        hp = mem.Written[a] | (mem.Written.TryGetValue(a + 1, out var hi) ? hi << 8 : 0);
        return true;
    }

    [Fact]
    public void Tick_boosts_an_ally_heal_when_sanctus_is_the_last_actor()
    {
        // Positive: latch == 64. Acted is forced to 0 to prove the gate does NOT read it.
        var (bene, mem, _, w) = BuildActiveScenario(hp: 50, maxHp: 100);
        mem.U8s[Offsets.Acted] = 0;                // the gate must ignore this entirely
        bene.Tick(onField: true);                  // baseline HP 50
        mem.U16s[w + Offsets.AHp] = 80;            // a Cure restored 30 HP
        bene.Tick(onField: true);                  // boost 30% of the observed 30 = 9
        Assert.True(TryReadWritten(mem, w, out int hp));
        Assert.Equal(89, hp);                      // 80 + 9, under max -> the boost landed
    }

    [Fact]
    public void Tick_boost_survives_a_long_charge_gap_with_acted_low()
    {
        // STICKINESS -- the whole reason for the rebuild. A charged Cure lands ~7 s (many ticks of
        // enemy turns) after the wielder selects it. The latch stays 64 the entire time, so the boost
        // fires when the HP finally rises, even though Acted has been 0 throughout.
        var (bene, mem, _, w) = BuildActiveScenario(hp: 50, maxHp: 100);
        mem.U8s[Offsets.Acted] = 0;
        bene.Tick(onField: true);                  // baseline HP 50
        for (int i = 0; i < 30; i++) bene.Tick(onField: true);   // 30 idle ticks, HP unchanged
        mem.U16s[w + Offsets.AHp] = 80;            // the charged Cure finally resolves
        bene.Tick(onField: true);
        Assert.True(TryReadWritten(mem, w, out int hp));
        Assert.Equal(89, hp);                      // time passing with Acted=0 did NOT close the gate
    }

    [Fact]
    public void Tick_does_not_boost_when_another_weapon_is_the_last_actor()
    {
        // THE load-bearing negative: the latch names a different weapon (86), so the Sanctus Staff is
        // NOT the last player to act -> no boost. This reads _lastPlayerMainHand (the new gate), never
        // the fingerprint -- the variable that previously let this assertion pass vacuously.
        var (bene, mem, _, w) = BuildActiveScenario(hp: 50, maxHp: 100, mainHand: 86);
        bene.Tick(onField: true);                  // baseline
        mem.U16s[w + Offsets.AHp] = 80;            // ally HP rose, but Sanctus isn't the latch
        bene.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(w + Offsets.AHp));
    }

    [Fact]
    public void Tick_does_not_boost_an_enemy_heal()
    {
        var (bene, mem, _, _) = BuildActiveScenario(hp: 50, maxHp: 100);   // ally at slot 24
        long enemy = Band.Entry(10);
        SeedBandEntry(mem, enemy, hp: 40, maxHp: 200, lvl: 30, br: 60, fa: 60, gx: 8, gy: 8);  // not an ally fp
        bene.Tick(onField: true);                  // active, baseline both
        mem.U16s[enemy + Offsets.AHp] = 90;        // an enemy got healed while Sanctus holds the latch
        bene.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(enemy + Offsets.AHp));   // the ally-only filter blocks it
    }

    [Fact]
    public void Tick_no_write_when_tier_below_atTier()
    {
        // Full active scenario EXCEPT the kill tier (2 < AtTier 3) -> IsActive false -> no boost,
        // despite a real ally heal while Sanctus holds the latch.
        var (bene, mem, _, w) = BuildActiveScenario(tier: 2, hp: 50, maxHp: 100);
        bene.Tick(onField: true);
        mem.U16s[w + Offsets.AHp] = 80;
        bene.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(w + Offsets.AHp));
    }

    [Fact]
    public void Tick_no_write_when_the_heal_fills_the_target_to_max()
    {
        // Gate open, real rise -- but the heal already topped the ally off, so the clamped boost
        // is a no-op (the deliberate no-overheal-inflation behavior).
        var (bene, mem, _, w) = BuildActiveScenario(hp: 50, maxHp: 100);
        bene.Tick(onField: true);                  // baseline at 50
        mem.U16s[w + Offsets.AHp] = 100;           // Cure topped the ally off
        bene.Tick(onField: true);                  // rise 50, bonus 15, but 100+15 clamps to 100
        Assert.False(mem.Written.ContainsKey(w + Offsets.AHp));
    }

    [Fact]
    public void ResetBattle_clears_baselines()
    {
        var state = new HealState(Offsets.BandSlots);
        state.Observe(0, 60);
        state.ResetBattle();
        // After reset, first sight must baseline again (not treat old baseline as valid)
        Assert.Equal(0, state.Observe(0, 100));
    }

    // ---- WriteHp guard (B3): confirms LifeSap.WriteHp is the shared write path ----

    [Fact]
    public void WriteHp_writes_little_endian_u16_to_hp_offset()
    {
        using var buf = PinnedBuf.Of(256);
        buf.Bytes[Offsets.AHp] = 0;
        buf.Bytes[Offsets.AHp + 1] = 0;
        buf.Bytes[Offsets.AMaxHp]     = 0xE8; // maxHp = 1000 little-endian
        buf.Bytes[Offsets.AMaxHp + 1] = 0x03;
        LifeSap.WriteHp(Live, buf.Addr, newHp: 512);
        int hp = buf.Bytes[Offsets.AHp] | (buf.Bytes[Offsets.AHp + 1] << 8);
        Assert.Equal(512, hp);
    }
}
