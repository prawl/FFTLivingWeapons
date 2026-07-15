using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-92: Plague's exact-match victim fingerprint (mhp, lvl, br, fa) dropped the hold when the
/// victim leveled up mid-battle (live capture 2026-07-14: Aitne latched at level 95, 449 maximum
/// HP; leveled to 96, 453 maximum HP with brave/faith stable at 67/51, and the hold dropped even
/// though the write machinery defeated three cures on tape at her CURRENT stats). Fix:
/// Plague.SameVictim (Plague.Policy.cs) tolerates an up-only level drift (Band.LevelMatchesRoster,
/// the same rule the credit path already uses) plus a bounded maxHp GROWTH, while brave/faith
/// must still match exactly; PlagueState.RelatchFp re-anchors the stored fingerprint on every
/// accepted drift so the budget never accumulates. Applied at BOTH exact-match sites: the
/// on-field band-loop verify and Drive's own per-tick verify (the off-field path: Tick's
/// onField=false branch calls Drive directly, with no band-loop pass at all).
///
/// Tests:
///   (1) SameVictim: pure accept/reject matrix.
///   (2) On-field path (site 1): latch survives a level+maxHp drift, re-anchors, the hold write
///       still lands (poison re-OR'd after a simulated cure), and the augment uses the FRESH
///       maxHp after re-anchoring.
///   (3) On-field path (site 1) negative: a brave change drops the latch.
///   (4) Off-field path (site 2): Drive alone (no band-loop pass) re-anchors an in-flight drift.
///   (5) Off-field path (site 2) negative: Drive alone drops a brave-changed victim.
///   (6) Non-vacuity proof that RelatchFp's store actually advances: a SECOND drift, legal only
///       from the RE-ANCHORED fingerprint and illegal from the ORIGINAL one, still survives.
/// </summary>
public class PlagueLevelDriftTests
{
    // ------------------------------------------------------------------ (1) SameVictim: pure policy

    private static readonly (int mhp, int lvl, int br, int fa) Base = (200, 10, 50, 50);

    [Fact]
    public void SameVictim_accepts_identical_tuple()
        => Assert.True(Plague.SameVictim(Base, Base));

    [Fact]
    public void SameVictim_accepts_the_live_capture_shape()
    {
        // 2026-07-14 live capture: level 95 -> 96, maxHp 449 -> 453, brave/faith unchanged.
        var captured = (mhp: 449, lvl: 95, br: 67, fa: 51);
        var current = (mhp: 453, lvl: 96, br: 67, fa: 51);
        Assert.True(Plague.SameVictim(captured, current));
    }

    [Fact]
    public void SameVictim_accepts_drift_up_to_the_MaxLevelDrift_and_MaxHpGrowthPerLatch_bounds()
    {
        var current = (mhp: Base.mhp + Plague.MaxHpGrowthPerLatch, lvl: Base.lvl + Band.MaxLevelDrift,
                        br: Base.br, fa: Base.fa);
        Assert.True(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_level_down()
    {
        var current = (mhp: Base.mhp, lvl: Base.lvl - 1, br: Base.br, fa: Base.fa);
        Assert.False(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_maxHp_shrink()
    {
        var current = (mhp: Base.mhp - 1, lvl: Base.lvl, br: Base.br, fa: Base.fa);
        Assert.False(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_maxHp_growth_beyond_the_bound()
    {
        var current = (mhp: Base.mhp + Plague.MaxHpGrowthPerLatch + 1, lvl: Base.lvl + 1,
                        br: Base.br, fa: Base.fa);
        Assert.False(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_brave_change()
    {
        var current = (mhp: Base.mhp, lvl: Base.lvl, br: Base.br + 1, fa: Base.fa);
        Assert.False(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_faith_change()
    {
        var current = (mhp: Base.mhp, lvl: Base.lvl, br: Base.br, fa: Base.fa + 1);
        Assert.False(Plague.SameVictim(Base, current));
    }

    [Fact]
    public void SameVictim_rejects_level_drift_beyond_MaxLevelDrift()
    {
        var current = (mhp: Base.mhp + 4, lvl: Base.lvl + Band.MaxLevelDrift + 1, br: Base.br, fa: Base.fa);
        Assert.False(Plague.SameVictim(Base, current));
    }

    // ------------------------------------------------------------------ integration setup
    // Mirrors MaimTests' BuildMaimedVictim/SeatVictim/SeatEnemyFp pattern: a real Plague instance
    // ticked against a FakeSparseMemory laid out at the true Offsets.

    private const int VenomboltId = 80;

    private static WeaponSignature PlagueSig(int atTier = 3) => new() { AtTier = atTier, DisplayLabel = "Plague" };

    private static Dictionary<int, WeaponMeta> MakePlagueMeta() => new()
    {
        [VenomboltId] = new WeaponMeta
        {
            Name = "Venombolt", Wp = 9, Cat = "Rod", Formula = 1,
            Flavor = "a venomous rod", Signature = PlagueSig()
        }
    };

    private static void SeatRosterSlot(FakeSparseMemory mem, int slot, int rh)
    {
        long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        mem.ReadableAddrs.Add(rb + Offsets.RNameId);
        mem.U16s[rb + Offsets.RNameId] = 1;
        mem.U16s[rb + Offsets.RRHand] = (ushort)rh;
        mem.U16s[rb + Offsets.ROffHand] = 0xFFFF;
    }

    private static void SeatVictim(FakeSparseMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp,
                                    bool poisoned, byte timer, byte ct, ushort hp)
    {
        mem.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[addr + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fp.fa;
        mem.ReadableAddrs.Add(addr + Offsets.APoison);
        mem.WritableAddrs.Add(addr + Offsets.APoison);
        mem.U8s[addr + Offsets.APoison] = poisoned ? Offsets.APoisonBit : (byte)0;
        mem.ReadableAddrs.Add(addr + Offsets.APoisonTimer);
        mem.WritableAddrs.Add(addr + Offsets.APoisonTimer);
        mem.U8s[addr + Offsets.APoisonTimer] = timer;
        mem.ReadableAddrs.Add(addr + Offsets.ACtTurn);
        mem.U8s[addr + Offsets.ACtTurn] = ct;
        mem.ReadableAddrs.Add(addr + Offsets.AHp);
        mem.WritableAddrs.Add(addr + Offsets.AHp);
        mem.U16s[addr + Offsets.AHp] = hp;
    }

    private static void SeatEnemyFp(FakeSparseMemory mem, (int mhp, int lvl, int br, int fa) fp)
    {
        long slot = Offsets.ArrayReadBase;   // static-array slot 0 (enemy side); a frozen snapshot.
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fp.fa;
    }

    /// <summary>Build a Plague instance with one victim already latched (poison edge + acted
    /// window overlapping at a fixed clock, mirroring PlagueGraceTests' grace-window mechanics).</summary>
    private static (Plague plague, FakeSparseMemory mem, long victim) BuildLatchedVictim(
        (int mhp, int lvl, int br, int fa) fp, byte seedCt = 0)
    {
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [VenomboltId] = Tuning.ProdThresholds[2] };   // tier 3
        var tracker = new KillTracker(new Dictionary<int, int>(), mem, new HashSet<int>())
        {
            _lastPlayerMainHand = VenomboltId
        };
        mem.U8s[Offsets.Acted] = 1;

        SeatRosterSlot(mem, slot: 0, rh: VenomboltId);
        SeatEnemyFp(mem, fp);

        long victim = Band.Entry(5);
        SeatVictim(mem, victim, fp, poisoned: false, timer: Tuning.PoisonTimerInit, ct: seedCt, hp: 100);

        const long now = 1000;
        var plague = new Plague(MakePlagueMeta(), kills, tracker, nowMs: () => now, mem: mem);

        plague.Tick(onField: true, inLive: true);            // baseline: not poisoned, no edge yet

        mem.U8s[victim + Offsets.APoison] = Offsets.APoisonBit;   // the wielder's poison lands
        plague.Tick(onField: true, inLive: true);            // edge + window overlap -> latch fires

        return (plague, mem, victim);
    }

    // ------------------------------------------------------------------ (2) on-field: drift survives, re-anchors, augment uses fresh maxHp

    [Fact]
    public void OnField_latch_survives_level_and_maxHp_drift_reanchors_and_augment_uses_fresh_maxHp()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var (plague, mem, victim) = BuildLatchedVictim(fp, seedCt: 50);

        // ApplyAugment writes HP via a single atomic WriteBytes (no tearing); FakeSparseMemory
        // records that call in WrittenBytes/OnWrite rather than syncing it back into U16s (by
        // design, see FakeSparseMemory's own doc comment), so observe it through the hook.
        ushort lastWrittenHp = 100;
        long hpAddr = victim + Offsets.AHp;
        mem.OnWrite = (addr, bytes) =>
        {
            if (addr == hpAddr && bytes.Length == 2) lastWrittenHp = (ushort)(bytes[0] | (bytes[1] << 8));
        };

        // Mid-battle level-up: level+1, maxHp+4 (the live-capture shape), brave/faith unchanged.
        mem.U16s[victim + Offsets.AMaxHp] = 204;
        mem.U8s[victim + Offsets.ALevel] = 11;
        mem.U8s[victim + Offsets.ACtTurn] = 90;               // CT climbs toward full
        plague.Tick(onField: true, inLive: true);              // drift accepted + re-anchored this tick

        // Prove the hold write still lands after the drift: simulate a cure. NOTE this alone
        // does not prove the STORED fingerprint was updated by RelatchFp: Drive passes the
        // freshly read fingerprint, not the stored one, to DriveOne, so this write lands even
        // if RelatchFp's body no-ops. The non-vacuous proof that the STORE itself advances is
        // OnField_latch_survives_a_second_drift_past_the_original_MaxLevelDrift_budget below,
        // where a second drift's accept/drop decision reads the stored value back.
        mem.U8s[victim + Offsets.APoison] = 0;
        mem.U8s[victim + Offsets.APoisonTimer] = 10;
        plague.Tick(onField: true, inLive: true);
        Assert.Equal(Offsets.APoisonBit, mem.U8s[victim + Offsets.APoison]);
        Assert.Equal(Tuning.PoisonTimerInit, mem.U8s[victim + Offsets.APoisonTimer]);

        // The victim's own turn completes (CT resets from near-full); the augment must use the
        // FRESH (post-drift) maxHp, not the stale captured value.
        mem.U8s[victim + Offsets.ACtTurn] = 10;
        plague.Tick(onField: true, inLive: true);

        int expectedFresh = Plague.AugmentDamage(mhp: 204, hp: 100);
        int expectedStale = Plague.AugmentDamage(mhp: 200, hp: 100);
        Assert.Equal(expectedFresh, lastWrittenHp);
        Assert.NotEqual(expectedStale, lastWrittenHp);   // would differ under the old exact-match bug
    }

    // ------------------------------------------------------------------ (3) on-field negative: brave change drops the latch

    [Fact]
    public void OnField_latch_drops_when_brave_changes()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var (plague, mem, victim) = BuildLatchedVictim(fp);

        mem.U8s[victim + Offsets.ABrave] = 51;   // a different unit occupying the slot, not a level-up
        plague.Tick(onField: true, inLive: true);   // SameVictim rejects -> latch dropped

        // Prove the drop: clear poison externally and tick again; Drive only iterates HELD
        // addresses, so nothing re-ORs it once the latch is gone.
        mem.U8s[victim + Offsets.APoison] = 0;
        plague.Tick(onField: true, inLive: true);
        Assert.Equal(0, (int)mem.U8s[victim + Offsets.APoison]);
    }

    // ------------------------------------------------------------------ (4) off-field (Drive-only): drift survives and re-anchors

    [Fact]
    public void OffField_drive_alone_reanchors_a_level_and_maxHp_drift()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var (plague, mem, victim) = BuildLatchedVictim(fp);

        // A mid-battle level-up lands while the player is off the field (an enemy's own turn):
        // Tick's onField=false branch calls Drive directly, with no band-loop pass at all.
        mem.U16s[victim + Offsets.AMaxHp] = 204;
        mem.U8s[victim + Offsets.ALevel] = 11;
        mem.U8s[victim + Offsets.APoison] = 0;          // a cure lands in the same beat
        mem.U8s[victim + Offsets.APoisonTimer] = 5;

        plague.Tick(onField: false, inLive: true);      // Drive-only path must accept + re-anchor + write

        Assert.Equal(Offsets.APoisonBit, mem.U8s[victim + Offsets.APoison]);
        Assert.Equal(Tuning.PoisonTimerInit, mem.U8s[victim + Offsets.APoisonTimer]);

        // A second off-field tick at the (now unchanged) drifted values must still write:
        // proof the re-anchor stuck rather than being tolerated for one tick only.
        mem.U8s[victim + Offsets.APoison] = 0;
        plague.Tick(onField: false, inLive: true);
        Assert.Equal(Offsets.APoisonBit, mem.U8s[victim + Offsets.APoison]);
    }

    // ------------------------------------------------------------------ (5) off-field (Drive-only) negative: brave change drops the latch

    [Fact]
    public void OffField_drive_alone_drops_when_brave_changes()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var (plague, mem, victim) = BuildLatchedVictim(fp);

        mem.U8s[victim + Offsets.ABrave] = 51;   // a different unit occupying the slot, not a level-up
        plague.Tick(onField: false, inLive: true);   // Drive-only path must drop, not re-anchor

        mem.U8s[victim + Offsets.APoison] = 0;
        plague.Tick(onField: false, inLive: true);
        Assert.Equal(0, (int)mem.U8s[victim + Offsets.APoison]);   // nothing re-ORs a dropped latch
    }

    // ------------------------------------------------------------------ (6) non-vacuity: a SECOND drift, legal only from the RE-ANCHORED fp, still survives

    [Fact]
    public void OnField_latch_survives_a_second_drift_past_the_original_MaxLevelDrift_budget()
    {
        // This is the test the verifier's sabotage targets: no-op PlagueState.RelatchFp's body
        // (leave it doing nothing) and this is the ONE test in the suite that goes red. Every
        // other drift test in this file only checks that a write lands, and Drive passes the
        // FRESHLY READ fingerprint (not the stored one) to DriveOne, so a write lands whether
        // or not the store actually advances. The only place the STORED value matters is the
        // ACCEPT/DROP decision itself on a later tick, which is what this test chains.
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var (plague, mem, victim) = BuildLatchedVictim(fp);

        // First drift: level 10 to 18 (8 levels, legal from the ORIGINAL latch), maxHp +50
        // (well within Plague.MaxHpGrowthPerLatch). Simulate a cure in the same beat; this
        // step's own accept/drop decision compares against the ORIGINAL captured fingerprint
        // either way, so the write lands whether or not RelatchFp's body actually runs. The
        // point of this step is to advance the STORED fingerprint (in a correct implementation)
        // to level 18 ahead of the second drift below.
        mem.U16s[victim + Offsets.AMaxHp] = 250;
        mem.U8s[victim + Offsets.ALevel] = 18;
        mem.U8s[victim + Offsets.APoison] = 0;
        mem.U8s[victim + Offsets.APoisonTimer] = 10;
        plague.Tick(onField: true, inLive: true);
        Assert.Equal(Offsets.APoisonBit, mem.U8s[victim + Offsets.APoison]);
        Assert.Equal(Tuning.PoisonTimerInit, mem.U8s[victim + Offsets.APoisonTimer]);

        // Second drift: level 18 to 26 (8 more levels, legal from the RE-ANCHORED level 18, but
        // 16 levels from the ORIGINAL level 10, past Band.MaxLevelDrift). THIS step's accept/
        // drop decision reads the STORED fingerprint back: a working RelatchFp compares against
        // level 18 and accepts; a no-op RelatchFp still compares against the stale level 10 and
        // rejects, dropping the hold and leaving the poison bit cleared below.
        mem.U16s[victim + Offsets.AMaxHp] = 300;
        mem.U8s[victim + Offsets.ALevel] = 26;
        mem.U8s[victim + Offsets.APoison] = 0;
        mem.U8s[victim + Offsets.APoisonTimer] = 10;
        plague.Tick(onField: true, inLive: true);

        Assert.Equal(Offsets.APoisonBit, mem.U8s[victim + Offsets.APoison]);
        Assert.Equal(Tuning.PoisonTimerInit, mem.U8s[victim + Offsets.APoisonTimer]);
    }
}
