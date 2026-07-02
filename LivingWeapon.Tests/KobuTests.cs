using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiyomori's "Kobu" signature. At +3, when the wielder's action deals damage to an enemy
/// whose CURRENT brave (band +0x0F) exceeds the wielder's own LIVE current brave, the
/// wielder's current brave is raised ONE SHOT to match (capped at Tuning.KobuBraveCap).
/// No ceiling state, no hold, no re-assertion -- between strikes the wielder's brave is a
/// normal stat, free to fall (Brave Break etc.). Premise: a one-shot current-brave write on
/// a player unit STICKS (no engine re-normalize) -- live-verified 2026-07-02,
/// probes/brave_oneshot_probe.py, LIVE_LEDGER row.
///
/// Pure policy in Kobu.Policy.cs:
///   (1) OneShotRaise: the keystone -- returns the value to write, or 0 for no-op. Never
///       lateral/lowering; a failed (insane) wielder read never triggers a write.
///   (2) IsActive: enabled by BraveOneUp flag + tier >= AtTier.
///
/// Integration (FakeSparseMemory, MemSeats + static-array fingerprint for enemy recognition):
///   KEYSTONE:  wielder band +0x0F = 70; struck enemy band +0x0F = 90; after Tick -> reads 90,
///              written exactly once at wielderEntry + Offsets.ABraveCurrent.
///   CLAMP REGRESSION (load-bearing): after a raise, an external brave-lowering write (e.g. Brave
///              Break) STICKS -- Kobu never re-asserts a prior raise on a later tick with no new hit.
///   RE-RAISE:  a later qualifying hit re-evaluates against the (now lower) live brave, not any
///              remembered ceiling.
///   GUARD:     wielder band +0x47 (AReraise / status byte) NEVER written. Catches the +0x2B-
///              off-band regression (band+0x2B = combat+0x47 = status field, not brave).
///   NEVER-LOWER: struck enemy brave 50 -> wielder stays 70, no write.
///   GATE-NEG:  Acted == 0 -> no brave scan -> no write.
///   NO-WIELDER: no deployed main-hand wielder -> no write, no crash.
///   RESET:     ResetBattle clears only the HP baselines (RicochetState); there is no ceiling.
/// </summary>
public class KobuTests
{
    private static readonly LiveMemory Live = new();
    private const int KiyomoriId = 43;

    private static WeaponSignature KobuSig(int atTier = 3) =>
        new() { AtTier = atTier, BraveOneUp = true, DisplayLabel = "Kobu" };

    // ---- (1) OneShotRaise: pure policy ----

    [Fact]
    public void OneShotRaise_raises_when_struck_brave_above_wielder_live()
        => Assert.Equal(90, Kobu.OneShotRaise(wielderLive: 70, struckLive: 90, cap: 97));

    [Fact]
    public void OneShotRaise_clamps_at_cap()
        => Assert.Equal(97, Kobu.OneShotRaise(wielderLive: 95, struckLive: 100, cap: 97));

    [Fact]
    public void OneShotRaise_zero_when_equal()
        => Assert.Equal(0, Kobu.OneShotRaise(wielderLive: 70, struckLive: 70, cap: 97));

    [Fact]
    public void OneShotRaise_zero_when_struck_below_wielder()
        => Assert.Equal(0, Kobu.OneShotRaise(wielderLive: 90, struckLive: 50, cap: 97));

    [Fact]
    public void OneShotRaise_zero_when_clamped_value_is_lateral()
        // struck 100 clamps to cap 97, which is <= wielder's live 97 -- must not write a lateral value
        => Assert.Equal(0, Kobu.OneShotRaise(wielderLive: 97, struckLive: 100, cap: 97));

    [Fact]
    public void OneShotRaise_zero_when_wielderLive_is_insane_zero()
        => Assert.Equal(0, Kobu.OneShotRaise(wielderLive: 0, struckLive: 90, cap: 97));

    // ---- (2) IsActive: pure policy ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Kobu.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_braveOneUp_is_false()
        => Assert.False(Kobu.IsActive(new WeaponSignature { BraveOneUp = false, AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Kobu.IsActive(KobuSig(atTier: 3), tier: 2));

    [Fact]
    public void IsActive_true_at_tier()
        => Assert.True(Kobu.IsActive(KobuSig(atTier: 3), tier: 3));

    [Fact]
    public void IsActive_true_above_tier()
        => Assert.True(Kobu.IsActive(KobuSig(atTier: 3), tier: 4));

    // ---- Integration setup ----
    //
    // SeatEnemy: band entry + static-array slot 0 (enemy oracle -- same pattern as MaimTests.SeatEnemyFp).
    // SeatWielder: roster slot + band entry via MemSeats helpers, plus ABraveCurrent (0x0F) seeded manually
    //              (MemSeats.SeatBand writes ABrave at +0x0E; ABraveCurrent at +0x0F is a NEW constant).
    //
    // Build returns the wielder's band-entry address so tests can assert on Written[wielderEntry + 0x0F].

    private static (Kobu kobu, FakeSparseMemory mem, long wielderEntry, long enemyEntry)
        Build(int wielderBrave = 70, int enemyOrigBrave = 75, int enemyCurrentBrave = 90,
              int wielderSlot = 24, int enemySlot = 20, bool seatWielder = true)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [KiyomoriId] = new WeaponMeta
            {
                Name = "Kiyomori", Wp = 11, Cat = "Katana", Formula = 1,
                Flavor = "exquisitely crafted katana",
                Signature = KobuSig()
            }
        };
        var kills = new Dictionary<int, int> { [KiyomoriId] = Tuning.ProdThresholds[2] };  // tier 3
        var tracker = new KillTracker(new Dictionary<int, int>(), mem, new HashSet<int>());
        tracker._lastPlayerMainHand = KiyomoriId;   // wielder is the last to act
        mem.U8s[Offsets.Acted] = 1;                 // and is acting this turn

        long wielder = Band.Entry(wielderSlot);
        if (seatWielder) SeatWielder(mem, wielder, wielderBrave);

        // Enemy: band entry visible in the band scan
        long enemy = Band.Entry(enemySlot);
        MemSeats.SeatBand(mem, enemySlot, weapon: 0, lvl: 40, br: enemyOrigBrave, fa: 55,
                          gx: 4, gy: 5, hp: 200, maxHp: 400);
        mem.U8s[enemy + Offsets.ABraveCurrent] = (byte)enemyCurrentBrave;
        mem.ReadableAddrs.Add(enemy + Offsets.AMaxHp);
        mem.ReadableAddrs.Add(enemy + Offsets.AHp);

        // Static-array enemy fingerprint at slot 0 (enemy-oracle, mirrors MaimTests.SeatEnemyFp)
        long arrSlot = Offsets.ArrayReadBase;
        mem.ReadableAddrs.Add(arrSlot + Offsets.AMaxHp);
        mem.U16s[arrSlot + Offsets.AMaxHp] = 400;
        mem.U8s[arrSlot + Offsets.ALevel]  = 40;
        mem.U8s[arrSlot + Offsets.ABrave]  = (byte)enemyOrigBrave;
        mem.U8s[arrSlot + Offsets.AFaith]  = 55;

        var kobu = new Kobu(meta, kills, tracker, mem: mem);
        return (kobu, mem, wielder, enemy);
    }

    // ---- Wielder seating, extracted from Build so a test can seat the wielder MID-test (after a
    // ---- tick has already run with no wielder deployed) -- see Tick_observes_baselines_while_wielder_locate_fails.

    private static void SeatWielder(FakeSparseMemory mem, long wielder, int wielderBrave)
    {
        int wielderSlot = (int)((wielder - Offsets.BandReadBase) / Offsets.CombatStride);
        // Wielder: roster slot + band entry
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: wielderBrave, fa: 60, rh: KiyomoriId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: KiyomoriId, lvl: 30, br: wielderBrave, fa: 60,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        // ABraveCurrent at band +0x0F (= AWeapon 0x04 offset; MemSeats writes ABrave at +0x0E only)
        mem.U8s[wielder + Offsets.ABraveCurrent] = (byte)wielderBrave;
        mem.ReadableAddrs.Add(wielder + Offsets.AMaxHp);
        mem.ReadableAddrs.Add(wielder + Offsets.AHp);
        mem.WritableAddrs.Add(wielder + Offsets.ABraveCurrent);  // allow the one-shot raise write
        // Also mark the status byte writable so the +0x47 guard test is NON-VACUOUS: if Kobu ever
        // wrote band +0x47 (AReraise) by mistake, the fake would let it land and the guard would fire.
        mem.WritableAddrs.Add(wielder + Offsets.AReraise);
    }

    // ---- KEYSTONE integration: struck brave above current raises wielder's band +0x0F, exactly once ----

    [Fact]
    public void Tick_raises_wielder_band_0x0F_to_struck_enemy_current_brave()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);                     // tick 1: baseline HP 200
        mem.U16s[enemy + Offsets.AHp] = 160;          // hit: HP dropped 40
        kobu.Tick(onField: true);                     // tick 2: detect hit, one-shot raise to 90

        Assert.Equal((byte)90, mem.U8s[wielderEntry + Offsets.ABraveCurrent]);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            $"Kobu must write to wielderEntry + Offsets.ABraveCurrent (0x{Offsets.ABraveCurrent:X2})");
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
    }

    // ---- LOAD-BEARING CLAMP REGRESSION: no hold -- an external brave-lowering write STICKS ----
    // This is the bug the rework fixes. Against the old hold-era code this test FAILS: the hold
    // block re-stamps _maxBrave (90) onto the wielder every tick it reads below the ceiling, so a
    // Brave-break to 60 would be clamped right back up. The one-shot rework must let it stand.

    [Fact]
    public void Tick_does_not_reassert_a_prior_raise_after_an_external_brave_drop()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);                     // raises 70 -> 90
        Assert.Equal((byte)90, mem.U8s[wielderEntry + Offsets.ABraveCurrent]);

        // Simulate an external brave-lowering effect (e.g. a Brave Break) after the raise.
        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 60;
        mem.Written.Clear();

        // Several more ticks, no new qualifying hit (HP held stable so no drop fires).
        for (int i = 0; i < 5; i++)
            kobu.Tick(onField: true);

        Assert.Equal((byte)60, mem.U8s[wielderEntry + Offsets.ABraveCurrent]);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "no new qualifying hit landed -- Kobu must not re-assert the prior raise (no hold)");
    }

    // ---- RE-RAISE AFTER DIP: the comparison basis is LIVE brave, not any remembered ceiling ----

    [Fact]
    public void Tick_reraises_from_the_new_live_brave_after_a_dip()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);                     // raises 70 -> 90

        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 60;   // external dip
        mem.Written.Clear();

        // A fresh qualifying hit: re-baseline HP, then drop it again, on an enemy with current brave 80.
        mem.U16s[enemy + Offsets.AMaxHp] = 400;
        mem.U16s[enemy + Offsets.AHp] = 400;
        mem.U8s[enemy + Offsets.ABraveCurrent] = 80;
        kobu.Tick(onField: true);                     // baseline HP 400 at the new value
        mem.U16s[enemy + Offsets.AHp] = 350;           // hit: HP dropped 50
        kobu.Tick(onField: true);                      // detect hit: live 60 < struck 80 -> raise to 80

        Assert.Equal((byte)80, mem.U8s[wielderEntry + Offsets.ABraveCurrent]);
        Assert.Equal((byte)80, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
    }

    // ---- GUARD: band +0x47 (AReraise / Reraise-Invisible-Float status) must stay untouched ----
    // Regression guard: if Kobu accidentally writes wielderEntry + 0x47 (AReraise, the status byte)
    // instead of wielderEntry + 0x0F (ABraveCurrent), this test fails loudly. Note: writing +0x2B off
    // a band address lands on combat+0x47 (the status byte, not the same address as band+0x47);
    // this guard also catches any write to the band-relative status offset directly.

    [Fact]
    public void Tick_does_not_write_band_plus_0x47_AReraise_status_byte()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.AReraise),
            $"Kobu must not write band +0x{Offsets.AReraise:X2} (AReraise/status) -- use ABraveCurrent 0x0F only");
    }

    // ---- Never-lower: struck enemy brave below wielder's live brave never writes ----

    [Fact]
    public void Tick_never_lowers_wielder_brave_when_enemy_current_brave_is_lower()
    {
        // Wielder brave = 70; enemy current brave = 50 (< 70) -> no raise
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 50);

        kobu.Tick(onField: true);          // baseline
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);          // hit detected; enemy brave 50 < wielder live 70 -> no change

        // band +0x0F should NOT have been written
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "struck enemy brave (50) is below wielder live brave (70) -- brave must not be written");
    }

    // ---- Acting-gate negative: Acted == 0 prevents the enemy brave scan ----

    [Fact]
    public void Tick_does_not_raise_brave_when_Acted_is_zero()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);
        mem.U8s[Offsets.Acted] = 0;    // override: wielder not acting this turn

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "with Acted=0 the acting gate is closed; enemy brave scan must not run");
    }

    // ---- No deployed wielder: ResolveDeployedMainHand returns 0 -> no write, no crash ----

    [Fact]
    public void Tick_does_nothing_when_no_deployed_wielder()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90, seatWielder: false);

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "no deployed main-hand wielder -- Kobu must no-op, not crash");
    }

    // ---- ResetBattle: clears only the HP baselines (no ceiling left to clear) ----

    [Fact]
    public void ResetBattle_clears_hp_baselines_for_the_next_battle()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        // Battle 1: raise wielder brave to 90
        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent));

        // Battle exit
        kobu.ResetBattle();

        // New battle: a fresh combat struct: enemy fully healed, wielder brave whatever it now is.
        mem.U16s[enemy + Offsets.AHp] = 400;   // enemy fully healed for a fresh HP baseline
        mem.Written.Clear();                    // isolate post-reset writes

        // First tick of new battle just re-baselines HP (RicochetState reset) -- no HP drop yet, no write.
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "ResetBattle only clears HP baselines; the first post-reset tick has no HP drop -- no write");
    }

    // ---- Non-lossy consumption: a one-tick DETECTABLE transient rearms the drop instead of eating it ----
    // (kobu-raise-detection-diagnosis, 2026-07-02: an identical strike was eaten by a one-tick transient
    // on a downstream check while the active gate was open; these tests lock in the fix.)

    [Fact]
    public void Tick_raises_through_a_transient_static_array_read_flap()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);   // tick 1: captures the fp cache + baselines HP

        // One-tick flap: the static-array slot vanishes from a per-tick rebuild on the drop tick.
        mem.ReadableAddrs.Remove(Offsets.ArrayReadBase + Offsets.AMaxHp);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);

        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "the cached enemy fingerprint set must survive a Readable flap on the drop tick");
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
    }

    // ---- LOAD-BEARING: the exact failure mode diagnosed live 2026-07-02 ----

    [Fact]
    public void Tick_retries_raise_when_wielder_brave_read_transiently_fails()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);   // tick 1: baseline HP

        // Fail-safe-zero wielder brave read on the drop tick -- a DETECTABLE transient.
        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 0;
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "an insane (fail-safe-zero) wielder brave read must not write, but must rearm the drop for retry");

        // Restore the read; NO further HP change -- the rearmed drop must re-detect next tick.
        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 70;
        kobu.Tick(onField: true);
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);

        // Consume-after-write tail: the event was consumed by the successful raise -- no double-fire.
        mem.Written.Clear();
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "the raise must consume the event -- no double-fire on a later tick");
    }

    [Fact]
    public void Tick_retries_when_write_target_transiently_unwritable()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);   // tick 1: baseline HP

        mem.WritableAddrs.Remove(wielderEntry + Offsets.ABraveCurrent);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "an unwritable target must not write, but must rearm the drop for retry");

        mem.WritableAddrs.Add(wielderEntry + Offsets.ABraveCurrent);
        kobu.Tick(onField: true);   // no further HP change -- the rearmed drop re-detects
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
    }

    [Fact]
    public void Tick_observes_baselines_while_wielder_locate_fails()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90, seatWielder: false);

        // Old code's whole-tick `if (wielderEntry == 0) return` made this baseline impossible.
        kobu.Tick(onField: true);   // tick 1: baseline HP forms even with no deployed wielder

        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);   // drop detected -- rearm-no-wielder (no wielder to raise yet)
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent));

        SeatWielder(mem, wielderEntry, 70);
        kobu.Tick(onField: true);   // no further HP change -- the rearmed drop re-detects now the wielder exists
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
    }

    [Fact]
    public void Tick_consumes_legit_no_op_without_retry()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 50);

        kobu.Tick(onField: true);   // tick 1: baseline HP
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);   // struck 50 < live 70: legit no-op, consumed (no rearm)

        // Raise ONLY the enemy's CURRENT brave -- must NOT touch orig brave (+0x0E, the fingerprint),
        // or this test would divert through the not-enemy path and become vacuous.
        mem.U8s[enemy + Offsets.ABraveCurrent] = 90;

        kobu.Tick(onField: true);
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "a legit no-op must consume the event, not rearm it -- no retry off a stale drop");
    }

    [Fact]
    public void Tick_stops_retrying_once_active_window_closes()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);   // tick 1: baseline HP
        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 0;
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);   // rearm-brave-read: rearmed for retry

        mem.U8s[Offsets.Acted] = 0;   // active window closes
        kobu.Tick(onField: true);     // first inactive tick consumes the rearmed delta ("inactive" verdict)

        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 70;
        mem.U8s[Offsets.Acted] = 1;
        kobu.Tick(onField: true);
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "an inactive tick must consume the rearmed delta -- reopening the window must not resurrect it");
    }

    [Fact]
    public void ResetBattle_clears_the_cached_enemy_fingerprints()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);   // capture the fp cache + baseline HP
        kobu.ResetBattle();

        // Nothing left to recapture -- the static-array slot goes unreadable for the new battle.
        mem.ReadableAddrs.Remove(Offsets.ArrayReadBase + Offsets.AMaxHp);

        kobu.Tick(onField: true);   // fresh baseline (HP state was also cleared by ResetBattle)
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);   // drop detected, but empty cache -> not-enemy, consumed

        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "ResetBattle must clear the cached enemy fingerprints -- an empty cache treats the drop as not-enemy");
    }
}
