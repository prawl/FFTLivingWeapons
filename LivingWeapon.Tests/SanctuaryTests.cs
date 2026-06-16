using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Staff of the Magi's "Sanctuary" signature. While a +3 Staff of the Magi is equipped
/// in the MAIN HAND and its bearer is alive and on the field, every fallen ALLY is held
/// from crystallizing: the crystal counter (band entry -0x15 == combat base +0x07) is
/// re-written to Tuning.SanctuaryHearts (3) every tick, so the revive window never closes.
///
/// Key design decisions tested here:
///   (1) Bearer-alive gate: if the bearer's HP == 0, no writes land ("save the priest").
///   (2) Vacuity twin: both the negative (bearer dead) and positive (bearer alive) run in
///       the same scenario so the negative cannot pass for the wrong reason.
///   (3) Offset correctness: the write target for corpse at band slot s is Band.Entry(s) - 0x15
///       (== Offsets.ACrystalHearts), value Tuning.SanctuaryHearts (3).
///   (4) Ally filter: only fingerprints present in Band.AllyFingerprints are protected;
///       enemy corpses are never touched.
///   (5) Living units: HP > 0 => not fallen => no pin.
///   (6) Tier gate: below AtTier the signature is inactive regardless of bearer state.
///   (7) Dead-streak guard: a corpse must read fallen for DeadNeeded (3) consecutive ticks
///       before any write lands. Protects against phantom band-load transients on a LIVE unit.
///   (8) Writable guard: if the counter address is not in the fake's writable set, the write
///       is silently skipped (the Mem.Writable pre-filter contract).
///   (9) Multi-bearer: two roster slots holding id 66 in main hand -> TryResolveMainHand
///       returns false -> inactive -> no writes.
/// </summary>
public class SanctuaryTests
{
    private static WeaponSignature SancSig(int atTier = 3) =>
        new() { AtTier = atTier, AntiCrystallize = true, DisplayLabel = "Sanctuary" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Sanctuary.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_antiCrystallize_is_false()
        => Assert.False(Sanctuary.IsActive(new WeaponSignature { AtTier = 3, AntiCrystallize = false }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Sanctuary.IsActive(SancSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Sanctuary.IsActive(SancSig(atTier: 3), tier: 3));
        Assert.True(Sanctuary.IsActive(SancSig(atTier: 3), tier: 4));
    }

    // ---- Seeding helpers ----

    /// <summary>Seed a valid band entry at <paramref name="addr"/>. HP 0 + deadBit true = fallen.</summary>
    private static void SeedBandEntry(FakeSparseMemory mem, long addr,
        int hp, int maxHp, int lvl, int br, int fa, int gx = 5, int gy = 5,
        bool deadBit = false, bool writableCounter = true)
    {
        mem.U8s[addr + Offsets.ALevel] = (byte)lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fa;
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)maxHp;
        mem.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        mem.U16s[addr + Offsets.AHp] = (ushort)hp;
        mem.ReadableAddrs.Add(addr + Offsets.AHp);
        mem.U8s[addr + Offsets.AGx] = (byte)gx;
        mem.U8s[addr + Offsets.AGy] = (byte)gy;
        if (deadBit)
            mem.U8s[addr + Offsets.ADeadStatus] = Offsets.ADeadBit;
        if (writableCounter)
            mem.WritableAddrs.Add(addr + Offsets.ACrystalHearts);
    }

    /// <summary>Plant a static-array ally fingerprint (player slot above EnemySlotMax).</summary>
    private static void SeedAllyFp(FakeSparseMemory mem, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 1) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fa;
    }

    /// <summary>Plant a roster entry with the Staff of the Magi (id 66) in main hand.</summary>
    private static void SeedRosterSlot(FakeSparseMemory mem, int rosterSlot,
        int lvl, int br, int fa, int mainHandId = MagiStaffId)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        mem.U8s[rb + Offsets.RLevel] = (byte)lvl;
        mem.U8s[rb + Offsets.RBrave] = (byte)br;
        mem.U8s[rb + Offsets.RFaith] = (byte)fa;
        mem.U16s[rb + Offsets.RRHand] = (ushort)mainHandId;
    }

    private const int MagiStaffId = 66;

    /// <summary>Build the standard active scenario: bearer alive at bearerSlot, one fallen ally at
    /// corpseSlot (streaked past DeadNeeded), ally fingerprint registered.</summary>
    private static (Sanctuary sanc, FakeSparseMemory mem, long corpseEntry)
        BuildActive(int tier = 3, int bearerSlot = 26, int corpseSlot = 24,
                    int bearerHp = 200, int bearerMaxHp = 200,
                    int corpseHp = 0, int corpseLvl = 20, int corseBr = 50, int corseFa = 50,
                    bool writableCounter = true)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [MagiStaffId] = new WeaponMeta
            {
                Name = "Staff of the Magi", Wp = 7, Cat = "Staff", Formula = 1,
                Flavor = "An ordinary-looking wooden stick", Signature = SancSig()
            }
        };
        var kills = new Dictionary<int, int>
        {
            [MagiStaffId] = tier >= 1 && tier <= 3 ? Tuning.ProdThresholds[tier - 1] : 0
        };

        // Roster: slot 0 = bearer
        SeedRosterSlot(mem, 0, lvl: 30, br: 60, fa: 55, mainHandId: MagiStaffId);

        // Bearer band entry (alive)
        long bearerEntry = Band.Entry(bearerSlot);
        SeedBandEntry(mem, bearerEntry, bearerHp, bearerMaxHp, lvl: 30, br: 60, fa: 55,
                      gx: 3, gy: 3, writableCounter: false);
        mem.U16s[bearerEntry + Offsets.CWeapon - Offsets.BandEntry] = (ushort)MagiStaffId;  // weapon field for Locate

        // Fallen ally band entry
        long corpseEntry = Band.Entry(corpseSlot);
        SeedBandEntry(mem, corpseEntry, corpseHp, maxHp: 300, lvl: corpseLvl, br: corseBr, fa: corseFa,
                      gx: 7, gy: 8, deadBit: corpseHp == 0, writableCounter: writableCounter);

        // Ally fingerprint (static array)
        SeedAllyFp(mem, mhp: 300, lvl: corpseLvl, br: corseBr, fa: corseFa);

        var sanc = new Sanctuary(meta, kills, mem: mem);
        return (sanc, mem, corpseEntry);
    }

    /// <summary>Tick Sanctuary <paramref name="n"/> times to saturate the dead-streak guard.</summary>
    private static void TickN(Sanctuary sanc, int n)
    {
        for (int i = 0; i < n; i++) sanc.Tick(onField: true);
    }

    // ---- (a) Bearer-alive gate (NON-VACUOUS NEGATIVE + vacuity twin) ----

    [Fact]
    public void Tick_does_not_write_when_bearer_hp_is_zero()
    {
        // Bearer HP == 0 -> bearer dead -> Sanctuary lifts -> no counter pin.
        var (sanc, mem, corpseEntry) = BuildActive(bearerHp: 0, bearerMaxHp: 200);
        TickN(sanc, 3);   // saturate streak; but bearer is down
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }

    [Fact]
    public void Tick_writes_when_bearer_is_alive_vacuity_twin()
    {
        // IDENTICAL scenario but bearer HP > 0 -> write must land at corpse - 0x15.
        var (sanc, mem, corpseEntry) = BuildActive(bearerHp: 200, bearerMaxHp: 200);
        TickN(sanc, 3);   // 3 ticks = DeadNeeded streak satisfied
        Assert.True(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
        Assert.Equal(Tuning.SanctuaryHearts, mem.Written[corpseEntry + Offsets.ACrystalHearts]);
    }

    // ---- (b) Offset correctness ----

    [Fact]
    public void Tick_write_lands_at_Band_Entry_minus_0x15_with_value_3()
    {
        var (sanc, mem, corpseEntry) = BuildActive(corpseSlot: 24);
        TickN(sanc, 3);
        // Pin the LITERAL offset so a fat-fingered constant is caught: the production write and a
        // bare `+ Offsets.ACrystalHearts` expectation would move in lockstep and never catch a wrong
        // value. -0x15 from the band entry == combat base +0x07 (the live-confirmed crystal counter).
        Assert.Equal(-0x15, Offsets.ACrystalHearts);
        Assert.Equal(0x07, Offsets.BandEntry + Offsets.ACrystalHearts);
        long expected = Band.Entry(24) - 0x15;   // literal, NOT via Offsets.ACrystalHearts
        Assert.True(mem.Written.ContainsKey(expected));
        Assert.Equal(Tuning.SanctuaryHearts, mem.Written[expected]);
    }

    // ---- (c) Enemy corpse -> not pinned ----

    [Fact]
    public void Tick_does_not_pin_enemy_corpse()
    {
        var (sanc, mem, _) = BuildActive();
        // Also seed an enemy corpse at slot 10 (enemy side); fingerprint NOT in ally set
        long enemy = Band.Entry(10);
        SeedBandEntry(mem, enemy, hp: 0, maxHp: 400, lvl: 15, br: 65, fa: 45, gx: 2, gy: 2,
                      deadBit: true, writableCounter: true);
        TickN(sanc, 3);
        Assert.False(mem.Written.ContainsKey(enemy + Offsets.ACrystalHearts));
    }

    // ---- (d) Living ally -> not pinned ----

    [Fact]
    public void Tick_does_not_pin_living_ally()
    {
        var (sanc, mem, _) = BuildActive();
        // Seed an extra LIVING ally band entry at slot 25 (HP > 0)
        long living = Band.Entry(25);
        SeedBandEntry(mem, living, hp: 150, maxHp: 150, lvl: 18, br: 50, fa: 50, gx: 6, gy: 6,
                      writableCounter: true);
        SeedAllyFp(mem, mhp: 150, lvl: 18, br: 50, fa: 50);
        TickN(sanc, 3);
        Assert.False(mem.Written.ContainsKey(living + Offsets.ACrystalHearts));
    }

    // ---- (e) Tier gate ----

    [Fact]
    public void Tick_no_write_when_tier_below_atTier()
    {
        var (sanc, mem, corpseEntry) = BuildActive(tier: 2);   // 2 < atTier 3
        TickN(sanc, 3);
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }

    // ---- (f) Dead-streak guard ----

    [Fact]
    public void Tick_dead_streak_guard_no_write_before_DeadNeeded_ticks()
    {
        var (sanc, mem, corpseEntry) = BuildActive();
        // Tick only twice: streak = 2 < DeadNeeded (3) -> no write yet
        TickN(sanc, 2);
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }

    [Fact]
    public void Tick_dead_streak_guard_writes_on_third_consecutive_dead_tick()
    {
        var (sanc, mem, corpseEntry) = BuildActive();
        TickN(sanc, 3);   // exactly DeadNeeded -> write fires
        Assert.True(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
        Assert.Equal(Tuning.SanctuaryHearts, mem.Written[corpseEntry + Offsets.ACrystalHearts]);
    }

    // ---- (g) Writable guard ----

    [Fact]
    public void Tick_no_write_when_counter_address_not_in_writable_set()
    {
        var (sanc, mem, corpseEntry) = BuildActive(writableCounter: false);
        TickN(sanc, 3);
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }

    // ---- (h) Multi-bearer: two roster slots -> TryResolveMainHand -> false -> no writes ----

    [Fact]
    public void Tick_no_write_when_two_roster_slots_hold_the_staff()
    {
        var (sanc, mem, corpseEntry) = BuildActive();
        // Inject a second roster slot also holding id 66 in main hand
        SeedRosterSlot(mem, 1, lvl: 25, br: 55, fa: 60, mainHandId: MagiStaffId);
        TickN(sanc, 3);
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }

    // ---- ResetBattle clears dead-streak state ----

    [Fact]
    public void ResetBattle_clears_dead_streak_so_new_battle_guard_restarts()
    {
        var (sanc, mem, corpseEntry) = BuildActive();
        TickN(sanc, 2);   // streak = 2; not written yet
        sanc.ResetBattle();
        // After reset the streak resets to 0; two more ticks (total 2 post-reset) must not write
        TickN(sanc, 2);
        Assert.False(mem.Written.ContainsKey(corpseEntry + Offsets.ACrystalHearts));
    }
}
