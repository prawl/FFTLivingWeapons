using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiyomori's "Kobu" signature. At +3, when the wielder's action deals damage to an enemy
/// whose CURRENT brave (band +0x0F) exceeds the wielder's accumulated max, the wielder's
/// current brave is raised to match (climb-only, capped at Tuning.KobuBraveCap,
/// battle-scoped -- the combat struct is rebuilt each battle so ResetBattle just clears
/// the in-memory ceiling and the engine re-normalizes naturally).
///
/// Pure policy in Kobu.Policy.cs:
///   (1) NextMax: climb-only, capped -- the keystone; struck brave raises ceiling, never lowers.
///   (2) ShouldRaise: write only when live brave is below the accumulated target.
///   (3) IsActive: enabled by BraveOneUp flag + tier >= AtTier.
///
/// Integration (FakeSparseMemory, MemSeats + static-array fingerprint for enemy recognition):
///   KEYSTONE:  wielder band +0x0F = 70; struck enemy band +0x0F = 90; after Tick -> reads 90.
///   WRITE-ADDR: Written dictionary key is exactly wielderEntry + Offsets.ABraveCurrent (0x0F).
///   GUARD:      wielder band +0x47 (AReraise / status byte) NEVER written. Catches the +0x2B-
///               off-band regression (band+0x2B = combat+0x47 = status field, not brave).
///   NEVER-LOWER: struck enemy brave 50 -> wielder stays 70, no write.
///   GATE-NEG:   Acted == 0 -> no brave scan -> no write.
///   RESET:      ResetBattle clears _maxBrave; next-battle first tick re-seeds from natural brave.
/// </summary>
public class KobuTests
{
    private static readonly LiveMemory Live = new();
    private const int KiyomoriId = 43;

    private static WeaponSignature KobuSig(int atTier = 3) =>
        new() { AtTier = atTier, BraveOneUp = true, DisplayLabel = "Kobu" };

    // ---- (1) NextMax: pure policy ----

    [Fact]
    public void NextMax_raises_when_struck_brave_above_current_max()
        => Assert.Equal(90, Kobu.NextMax(70, 90, 97));

    [Fact]
    public void NextMax_never_lowers_when_struck_brave_below_current_max()
        => Assert.Equal(90, Kobu.NextMax(90, 50, 97));

    [Fact]
    public void NextMax_clamps_at_cap()
        => Assert.Equal(97, Kobu.NextMax(95, 100, 97));

    [Fact]
    public void NextMax_equal_struck_and_current_is_a_noop()
        => Assert.Equal(70, Kobu.NextMax(70, 70, 97));

    // ---- (2) ShouldRaise: pure policy ----

    [Fact]
    public void ShouldRaise_true_when_live_below_target()
        => Assert.True(Kobu.ShouldRaise(liveBrave: 70, target: 90));

    [Fact]
    public void ShouldRaise_false_when_live_equals_target()
        => Assert.False(Kobu.ShouldRaise(liveBrave: 90, target: 90));

    [Fact]
    public void ShouldRaise_false_when_live_above_target()
        => Assert.False(Kobu.ShouldRaise(liveBrave: 91, target: 90));

    // ---- (3) IsActive: pure policy ----

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
              int wielderSlot = 24, int enemySlot = 20)
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

        // Wielder: roster slot + band entry
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: wielderBrave, fa: 60, rh: KiyomoriId);
        long wielder = Band.Entry(wielderSlot);
        MemSeats.SeatBand(mem, wielderSlot, weapon: KiyomoriId, lvl: 30, br: wielderBrave, fa: 60,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        // ABraveCurrent at band +0x0F (= AWeapon 0x04 offset; MemSeats writes ABrave at +0x0E only)
        mem.U8s[wielder + Offsets.ABraveCurrent] = (byte)wielderBrave;
        mem.ReadableAddrs.Add(wielder + Offsets.AMaxHp);
        mem.ReadableAddrs.Add(wielder + Offsets.AHp);
        mem.WritableAddrs.Add(wielder + Offsets.ABraveCurrent);  // allow the brave-hold write
        // Also mark the status byte writable so the +0x47 guard test is NON-VACUOUS: if Kobu ever
        // wrote band +0x47 (AReraise) by mistake, the fake would let it land and the guard would fire.
        mem.WritableAddrs.Add(wielder + Offsets.AReraise);

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

    // ---- KEYSTONE integration: struck brave above current raises wielder's band +0x0F ----

    [Fact]
    public void Tick_raises_wielder_band_0x0F_to_struck_enemy_current_brave()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);                     // tick 1: baseline HP 200, seed _maxBrave=70
        mem.U16s[enemy + Offsets.AHp] = 160;          // hit: HP dropped 40
        kobu.Tick(onField: true);                     // tick 2: detect hit, update _maxBrave to 90, hold

        Assert.Equal((byte)90, mem.U8s[wielderEntry + Offsets.ABraveCurrent]);
    }

    // ---- Write-address keystone: Written key must be wielderEntry + Offsets.ABraveCurrent ----

    [Fact]
    public void Tick_write_address_is_wielder_band_plus_ABraveCurrent_0x0F()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);

        // The written key must be EXACTLY wielderEntry + 0x0F, not any other offset
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            $"Kobu must write to wielderEntry + Offsets.ABraveCurrent (0x{Offsets.ABraveCurrent:X2})");
        Assert.Equal((byte)90, mem.Written[wielderEntry + Offsets.ABraveCurrent]);
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

    // ---- Never-lower: struck enemy brave below max never lowers the ceiling ----

    [Fact]
    public void Tick_never_lowers_wielder_brave_when_enemy_current_brave_is_lower()
    {
        // Wielder brave = 70; enemy current brave = 50 (< 70) -> no raise
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 50);

        kobu.Tick(onField: true);          // baseline, seed _maxBrave=70
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);          // hit detected; enemy brave 50 < max 70 -> no change

        // band +0x0F should NOT have been written
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "struck enemy brave (50) is below wielder max (70) -- brave must not be written");
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

    // ---- ResetBattle: clears _maxBrave so the next battle re-seeds from natural brave ----

    [Fact]
    public void ResetBattle_clears_max_brave_so_next_battle_reseeds()
    {
        var (kobu, mem, wielderEntry, enemy) = Build(wielderBrave: 70, enemyCurrentBrave: 90);

        // Battle 1: raise _maxBrave to 90
        kobu.Tick(onField: true);
        mem.U16s[enemy + Offsets.AHp] = 160;
        kobu.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent));

        // Battle exit
        kobu.ResetBattle();

        // New battle: engine re-normalized wielder's brave back to 70
        mem.U8s[wielderEntry + Offsets.ABraveCurrent] = 70;
        mem.U16s[enemy + Offsets.AHp] = 400;   // enemy fully healed for fresh HP baseline
        mem.Written.Clear();                    // isolate post-reset writes

        // First tick of new battle: seed _maxBrave from 70; no enemy HP drop; no write
        kobu.Tick(onField: true);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ABraveCurrent),
            "_maxBrave re-seeded to 70 from natural brave; liveBrave==_maxBrave -> no write");
    }
}
