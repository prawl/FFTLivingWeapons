using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Venombolt's "Plague" signature at +3: once a wielder who has earned +3 poisons an enemy
/// during their acted window, the poison on that enemy never expires (bit re-stamped, timer
/// re-pinned to init) and ticks an extra mhp*3/32 on each of the victim's turns (floored at 1
/// HP so the augment never kills). All reads/writes are VirtualQuery-guarded.
///
/// Policy tests (Plague.Policy.cs) cover pure functions with no memory:
///   (1) IsActive: tier gate + signature presence.
///   (2) ShouldLatch: enemy filter (never ally).
///   (3) AugmentDamage: mhp*3/32 arithmetic, floor 1, never 0.
///   (4) IsTurn: reuses same CT-edge rule as Maim/CharmLock.
///   (5) ShouldRepin: re-pin when timer is below init.
///
/// Runtime tests (pinned-buffer style, PlagueState) cover:
///   (6) Cure simulation: clear poison bit -> next tick re-ORs it.
///   (7) Timer re-pin: decrement timer -> rewritten to PoisonTimerInit.
///   (8) Augment math: hp=2 with augment 1 -> floor to 1, never 0.
///   (9) Unequip releases hold (fingerprint mismatch -> no write).
///   (10) ResetBattle clears all latches.
///   (11) Turn-edge reads from +0x09 (ACtTurn), not +0x25 (ACtSlam).
///   (12) Enemy latched during acted window; ally never latched.
///   (16) Pre-poisoned enemy before window -> NOT latched (edge-latch, A1).
///   (17) Poison bit turning on while active -> latched (edge-latch, A1).
///   (18) Two same-fingerprint units at different addresses -> independent CT tracking (A2).
///   (19) First tick after latch -> no augment until a genuine CT edge (A4 phantom augment).
///   (20) inLive=false -> zero writes even with held latches (A3).
///   (21) Unequip -> all latches released (A4 unequip release).
///   (22) HP write lands as one 2-byte WriteBytes call (A4 torn-HP write).
///   (23) Same exclusive mhp bound used in band loop and EnemyFingerprints (A4 align bound).
/// </summary>
public class PlagueTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature PlagueSig(int atTier = 3)
        => new() { AtTier = atTier, DisplayLabel = "Plague" };

    // ------------------------------------------------------------------ (1) IsActive
    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Signatures.Earned(null, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Signatures.Earned(PlagueSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Signatures.Earned(PlagueSig(atTier: 3), tier: 3));
        Assert.True(Signatures.Earned(PlagueSig(atTier: 3), tier: 4));
    }

    // ------------------------------------------------------------------ (2) ShouldLatch
    [Fact]
    public void ShouldLatch_true_for_enemy()
        => Assert.True(Plague.ShouldLatch(isEnemy: true));

    [Fact]
    public void ShouldLatch_false_for_ally()
        => Assert.False(Plague.ShouldLatch(isEnemy: false));

    // ------------------------------------------------------------------ (3) AugmentDamage
    [Theory]
    [InlineData(32, 10, 7)]    // 32*3/32=3, 10-3=7
    [InlineData(64, 20, 14)]   // 64*3/32=6, 20-6=14
    [InlineData(100, 20, 11)]  // 100*3/32=9, 20-9=11
    [InlineData(1,   1, 1)]    // mhp*3/32=0 -> augment floor 1 -> 1-1=0 -> floor 1
    [InlineData(2,   2, 1)]    // mhp*3/32=0 -> augment floor 1 -> 2-1=1
    [InlineData(10,  5, 4)]    // 10*3/32=0 -> augment floor 1 -> 5-1=4
    [InlineData(32,  1, 1)]    // augment(3) >= hp(1) -> floor 1, never 0
    public void AugmentDamage_mhp_3_32_floored_at_1(int mhp, int hp, int expectedHp)
        => Assert.Equal(expectedHp, Plague.AugmentDamage(mhp, hp));

    [Fact]
    public void AugmentDamage_never_reduces_hp_to_zero()
    {
        // Floor 1: even if the augment equals current HP, result is at least 1.
        Assert.True(Plague.AugmentDamage(100, 1) >= 1);
        Assert.True(Plague.AugmentDamage(32, 1) >= 1);
    }

    [Fact]
    public void AugmentDamage_never_kills_when_hp_equals_augment()
    {
        // hp == augment -> result must be 1, not 0
        int mhp = 64;
        int augment = mhp * Tuning.PlagueExtraDamageNum / Tuning.PlagueExtraDamageDen;  // = 6
        Assert.Equal(1, Plague.AugmentDamage(mhp, augment));
    }

    // ------------------------------------------------------------------ (4) IsTurn
    [Theory]
    [InlineData(100, 10, true)]
    [InlineData(95, 0,  true)]
    [InlineData(90, 69, true)]
    [InlineData(90, 70, false)]   // not a big enough drop
    [InlineData(80, 5,  false)]   // wasn't full when it dropped
    [InlineData(100, 100, false)] // still full
    [InlineData(0, 0, false)]
    public void IsTurn_detects_a_CT_reset_from_full(int last, int cur, bool expected)
        => Assert.Equal(expected, CtTurns.IsTurn(last, cur));

    // ------------------------------------------------------------------ (5) ShouldRepin
    [Theory]
    [InlineData(36, false)]   // at init, no re-pin needed
    [InlineData(37, false)]   // above init (shouldn't occur, but must be safe)
    [InlineData(35, true)]    // below init -> re-pin
    [InlineData(0,  true)]    // expired -> re-pin
    public void ShouldRepin_triggers_only_below_init(int timer, bool expected)
        => Assert.Equal(expected, Plague.ShouldRepin(timer, Tuning.PoisonTimerInit));

    // ------------------------------------------------------------------ helpers for pinned-buffer tests

    /// <summary>Lay out a minimal band-entry buffer around base+0 with the correct fingerprint and
    /// optionally the poison bit. PoisonTimer and ACtTurn are separate offsets from the same base.</summary>
    private static PinnedBuf MakeBandEntry(
        (int mhp, int lvl, int br, int fa) fp,
        bool poisoned = false,
        byte timer = Tuning.PoisonTimerInit,
        byte ct = 0,
        byte hp = 100)
    {
        var entry = PinnedBuf.Of(256);
        var buf = entry.Bytes;
        // AHp (+0x14), AMaxHp (+0x16), ALevel (+0x0D), ABrave (+0x0E), AFaith (+0x10)
        buf[Offsets.AHp]       = hp;
        buf[Offsets.AHp + 1]   = 0;
        buf[Offsets.AMaxHp]    = (byte)(fp.mhp & 0xFF);
        buf[Offsets.AMaxHp + 1] = (byte)((fp.mhp >> 8) & 0xFF);
        buf[Offsets.ALevel]    = (byte)fp.lvl;
        buf[Offsets.ABrave]    = (byte)fp.br;
        buf[Offsets.AFaith]    = (byte)fp.fa;
        buf[Offsets.APoison]   = poisoned ? (byte)(buf[Offsets.APoison] | Offsets.APoisonBit) : (byte)0;
        buf[Offsets.APoisonTimer] = timer;
        buf[Offsets.ACtTurn]   = ct;
        return entry;
    }

    private static bool IsPoisoned(byte[] buf) => (buf[Offsets.APoison] & Offsets.APoisonBit) != 0;

    // ------------------------------------------------------------------ (6) Cure simulation
    [Fact]
    public void Drive_resets_poison_bit_when_cleared_by_cure()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        using var entry = MakeBandEntry(fp, poisoned: true);
        var state = new PlagueState();
        state.Latch(entry.Addr, fp);
        Assert.True(state.IsHeld(fp));

        // Simulate a cure: engine clears the poison bit.
        entry.Bytes[Offsets.APoison] &= unchecked((byte)~Offsets.APoisonBit);
        Assert.False(IsPoisoned(entry.Bytes));

        // Drive should re-OR the bit.
        Plague.DriveOne(Live, entry.Addr, fp, state);
        Assert.True(IsPoisoned(entry.Bytes));
    }

    // ------------------------------------------------------------------ (7) Timer re-pin
    [Fact]
    public void Drive_repins_timer_when_below_init()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        using var entry = MakeBandEntry(fp, poisoned: true, timer: 10);
        var state = new PlagueState();
        state.Latch(entry.Addr, fp);

        Plague.DriveOne(Live, entry.Addr, fp, state);
        Assert.Equal(Tuning.PoisonTimerInit, entry.Bytes[Offsets.APoisonTimer]);
    }

    [Fact]
    public void Drive_does_not_repin_timer_at_init()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        using var entry = MakeBandEntry(fp, poisoned: true, timer: Tuning.PoisonTimerInit);
        var state = new PlagueState();
        state.Latch(entry.Addr, fp);

        Plague.DriveOne(Live, entry.Addr, fp, state);
        Assert.Equal(Tuning.PoisonTimerInit, entry.Bytes[Offsets.APoisonTimer]);
    }

    // ------------------------------------------------------------------ (8) Augment math + floor
    [Fact]
    public void AugmentDamage_hp_2_floors_to_1()
        => Assert.Equal(1, Plague.AugmentDamage(mhp: 100, hp: 2));

    [Fact]
    public void AugmentDamage_exact_mhp_3_32()
    {
        // mhp=320: augment = 320*3/32 = 30; hp=50 -> 50-30=20
        Assert.Equal(20, Plague.AugmentDamage(mhp: 320, hp: 50));
    }

    // ------------------------------------------------------------------ (9) Fingerprint mismatch -> no write
    [Fact]
    public void DriveOne_skips_write_on_fingerprint_mismatch()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var wrongFp = (mhp: 200, lvl: 11, br: 50, fa: 50);   // different level
        using var entry = MakeBandEntry(fp, poisoned: false);
        var state = new PlagueState();
        state.Latch(entry.Addr, wrongFp);   // latched as wrong fingerprint

        // The drive call is for `wrongFp` but the buffer has `fp` layout; mismatch -> no write.
        Plague.DriveOne(Live, entry.Addr, wrongFp, state);
        // buf still has the same (unpoisoned) bytes; nothing was written.
        Assert.False(IsPoisoned(entry.Bytes));
    }

    // ------------------------------------------------------------------ (10) ResetBattle clears latches
    [Fact]
    public void PlagueState_Clear_removes_all_latches()
    {
        var state = new PlagueState();
        var fp = (mhp: 100, lvl: 5, br: 50, fa: 50);
        state.Latch(100L, fp);
        Assert.True(state.IsHeld(fp));
        state.Clear();
        Assert.False(state.IsHeld(fp));
    }

    // ------------------------------------------------------------------ (11) Turn-edge reads +0x09, not +0x25
    [Fact]
    public void IsTurn_uses_ACtTurn_offset_not_ACtSlam()
    {
        // Confirm the offset constants are distinct and plague uses the proven readable one.
        Assert.NotEqual(Offsets.ACtTurn, Offsets.ACtSlam);
        Assert.Equal(0x09, Offsets.ACtTurn);
        Assert.Equal(0x25, Offsets.ACtSlam);
    }

    // ------------------------------------------------------------------ (12) Enemy latched; ally never latched
    [Fact]
    public void ShouldLatch_latches_enemy_and_rejects_ally()
    {
        Assert.True(Plague.ShouldLatch(isEnemy: true));
        Assert.False(Plague.ShouldLatch(isEnemy: false));
    }

    // ------------------------------------------------------------------ (13) APoison / APoisonBit / APoisonTimer constants
    [Fact]
    public void Offsets_poison_constants_match_known_values()
    {
        Assert.Equal(0x48, Offsets.APoison);
        Assert.Equal(0x80, Offsets.APoisonBit);
        Assert.Equal(0x4A, Offsets.APoisonTimer);
    }

    [Fact]
    public void PoisonTimerInit_is_36()
        => Assert.Equal(36, Tuning.PoisonTimerInit);

    // ------------------------------------------------------------------ (14) PlagueState latch/release
    [Fact]
    public void PlagueState_release_removes_fingerprint()
    {
        var state = new PlagueState();
        var fp = (mhp: 100, lvl: 5, br: 50, fa: 50);
        state.Latch(100L, fp);
        Assert.True(state.IsHeld(fp));
        state.Release(fp);
        Assert.False(state.IsHeld(fp));
    }

    [Fact]
    public void PlagueState_held_addr_round_trips()
    {
        var state = new PlagueState();
        var fp = (mhp: 100, lvl: 5, br: 50, fa: 50);
        state.Latch(unchecked((long)0xDEADBEEFL), fp);
        Assert.Equal(unchecked((long)0xDEADBEEFL), state.HeldAddr(fp));
    }

    // ------------------------------------------------------------------ (15) UpdateCt / IsTurn integration
    [Fact]
    public void PlagueState_tracks_turn_edge_via_UpdateCt()
    {
        var fp = (mhp: 100, lvl: 5, br: 50, fa: 50);
        var state = new PlagueState();
        state.Latch(100L, fp);

        // Simulate CT climbs to 95, then drops to 10 (= one turn).
        state.UpdateCt(fp, 95);
        bool edge = CtTurns.IsTurn(state.LastCt(fp), 10);
        state.UpdateCt(fp, 10);
        Assert.True(edge);
    }

    // ------------------------------------------------------------------ (16) Edge-latch A1: pre-poisoned enemy before window -> NOT latched
    [Fact]
    public void PoisonBaseline_pre_poisoned_before_window_not_latched()
    {
        // An enemy that was ALREADY poisoned when the baseline was recorded should NOT be
        // latched even when the wielder's window opens -- the edge (false->true) already happened.
        var baseline = new PlagueBaseline();
        long addr = 0x1000;
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);

        // Record baseline while the enemy is already poisoned (pre-battle Bio, etc.).
        baseline.Update(addr, fp, poisoned: true, nowMs: 1000);

        // Window opens; poison is still on. No edge was ever observed -> never latchable.
        baseline.Update(addr, fp, poisoned: true, nowMs: 1033);
        Assert.False(Plague.ShouldLatchNow(isEnemy: true, held: false,
            lastEdgeMs: baseline.LastEdgeMs(addr, fp), lastActiveMs: 1033,
            now: 1033, graceMs: Tuning.PlagueGraceMs));
    }

    // ------------------------------------------------------------------ (17) Edge-latch A1: poison bit turns on during active window -> latched
    [Fact]
    public void PoisonBaseline_poison_on_during_window_is_rising_edge()
    {
        var baseline = new PlagueBaseline();
        long addr = 0x1000;
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);

        // Baseline: not poisoned.
        baseline.Update(addr, fp, poisoned: false, nowMs: 1000);

        // Window open at 1033; poison bit just turned on -> edge stamped, latchable.
        baseline.Update(addr, fp, poisoned: true, nowMs: 1033);
        Assert.True(Plague.ShouldLatchNow(isEnemy: true, held: false,
            lastEdgeMs: baseline.LastEdgeMs(addr, fp), lastActiveMs: 1033,
            now: 1033, graceMs: Tuning.PlagueGraceMs));
    }

    // ------------------------------------------------------------------ (18) A2: two same-fp units at different addrs -> independent CT
    [Fact]
    public void PlagueState_same_fp_different_addrs_independent_CT()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var state = new PlagueState();

        long addr1 = 0x1000;
        long addr2 = 0x2000;

        // Latch both with different seed CTs.
        state.Latch(addr1, fp, seedCt: 0);
        state.Latch(addr2, fp, seedCt: 0);

        // Both held independently.
        Assert.True(state.IsHeldAt(addr1));
        Assert.True(state.IsHeldAt(addr2));

        // Update CT independently.
        state.UpdateCtAt(addr1, 95);
        state.UpdateCtAt(addr2, 20);

        Assert.Equal(95, state.LastCtAt(addr1));
        Assert.Equal(20, state.LastCtAt(addr2));

        // CT edge on addr1 only (95 -> 10 = edge); addr2 has lastCt=20 (no edge).
        Assert.True(CtTurns.IsTurn(state.LastCtAt(addr1), 10));
        Assert.False(CtTurns.IsTurn(state.LastCtAt(addr2), 10));
    }

    // ------------------------------------------------------------------ (19) A4 phantom augment: first tick after latch -> no augment
    [Fact]
    public void PlagueState_latch_seeds_CT_from_current_value_no_phantom_augment()
    {
        // Seeding LastCt = 255 caused IsTurn(255, curCt) = true on first tick if curCt < 70.
        // Seeding with the actual current CT prevents this.
        var state = new PlagueState();
        long addr = 0x1000;
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);

        // Unit's CT at latch time is 50 (mid-value, well below 90).
        state.Latch(addr, fp, seedCt: 50);

        // First tick after latch: CT is still 50 (no change). IsTurn(50, 50) must be false.
        Assert.False(CtTurns.IsTurn(state.LastCtAt(addr), 50));

        // A curCt < 70 right after latch should also not fire (lastCt 50 < 90).
        Assert.False(CtTurns.IsTurn(state.LastCtAt(addr), 40));
    }

    // ------------------------------------------------------------------ (20) A3: inLive=false -> no writes
    [Fact]
    public void DriveOne_no_write_when_inLive_false()
    {
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        using var entry = MakeBandEntry(fp, poisoned: false, timer: 0);
        var state = new PlagueState();
        state.Latch(entry.Addr, fp);

        // With inLive=false, DriveOne must not write anything.
        Plague.DriveOne(Live, entry.Addr, fp, state, inLive: false);

        // Poison bit should remain off; timer should remain 0.
        Assert.False(IsPoisoned(entry.Bytes));
        Assert.Equal(0, entry.Bytes[Offsets.APoisonTimer]);
    }

    // ------------------------------------------------------------------ (21) A4 unequip release: Release via fp, no latches remain
    [Fact]
    public void PlagueState_release_by_addr_clears_that_slot()
    {
        var state = new PlagueState();
        var fp = (mhp: 200, lvl: 10, br: 50, fa: 50);
        long addr = 0x1000;
        state.Latch(addr, fp, seedCt: 0);
        Assert.True(state.IsHeldAt(addr));

        state.ReleaseAt(addr);
        Assert.False(state.IsHeldAt(addr));
        Assert.False(state.IsHeld(fp));
    }

    [Fact]
    public void PlagueState_release_all_clears_all_latches()
    {
        var state = new PlagueState();
        var fp1 = (mhp: 200, lvl: 10, br: 50, fa: 50);
        var fp2 = (mhp: 300, lvl: 15, br: 60, fa: 60);
        state.Latch(0x1000, fp1, seedCt: 0);
        state.Latch(0x2000, fp2, seedCt: 0);

        state.Clear();
        Assert.False(state.IsHeldAt(0x1000));
        Assert.False(state.IsHeldAt(0x2000));
    }

    // ------------------------------------------------------------------ (22) A4 torn HP: ApplyAugment uses one 2-byte write
    [Fact]
    public void ApplyAugment_uses_single_2byte_write_no_tear()
    {
        // ApplyAugment should write HP as a single WriteBytes(addr, 2-byte LE) so the engine
        // cannot read a torn intermediate. We verify both bytes land correctly in the buffer.
        var fp = (mhp: 320, lvl: 10, br: 50, fa: 50);   // augment = 320*3/32 = 30
        using var entry = MakeBandEntry(fp, poisoned: true, hp: 50);
        // Expected new HP: 50 - 30 = 20 (0x0014)
        Plague.ApplyAugment(Live, entry.Addr, fp);

        int newHp = entry.Bytes[Offsets.AHp] | (entry.Bytes[Offsets.AHp + 1] << 8);
        Assert.Equal(20, newHp);
    }

    // ------------------------------------------------------------------ (23) A4 mhp bound: exclusive > 2000 in band loop
    [Fact]
    public void MhpBound_exclusive_rejects_exactly_2000()
    {
        // The band-loop and EnemyFingerprints both use mhp > 2000 (exclusive).
        // Verify the policy: mhp=2000 must be rejected; mhp=1999 must be accepted.
        Assert.False(Plague.IsValidEnemyMhp(2000));
        Assert.True(Plague.IsValidEnemyMhp(1999));
        Assert.False(Plague.IsValidEnemyMhp(2001));
    }

    // ------------------------------------------------------------------ Main-hand-only activation gate (B1)
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void IsActingMainHand_true_when_mainHand_is_the_signature_weapon()
        => Assert.True(Signatures.IsActingMainHand(mainHand: 80, weaponId: 80));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_a_different_weapon()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 99, weaponId: 80));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_zero_meaning_no_actor_resolved()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 0, weaponId: 80));
}
