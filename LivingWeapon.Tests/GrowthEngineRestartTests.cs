using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-90 seam tests for the growth-family capture sites: a battle RESTART rebuilds units with
/// the mod's own held boost baked into the stat byte, and every first-sight capture must see
/// through its own residue via the shared NaturalLedger. Each test drives the internal hold
/// method directly (the LocateIn precedent) through the REAL reset sequence: the subsystem's
/// ResetBattle plus TWO ledger resets (Engine.ResetBattleState fires on both battle edges).
/// The Iai seam (IaiTests' restart trio) demonstrated the red-first mechanism for the shared
/// flow; these pin each growth lane's own capture, compounding, and baked-token behavior.
/// </summary>
public class GrowthEngineRestartTests
{
    private const int NameId = 512;
    private const long StructBase = 0x6000_0000;

    private static (GrowthEngine engine, FakeSparseMemory mem, NaturalLedger ledger) Build()
    {
        var mem = new FakeSparseMemory();
        var ledger = new NaturalLedger();
        var engine = new GrowthEngine(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(),
                                      new TurnTracker(mem), mem, null, ledger);
        return (engine, mem, ledger);
    }

    private static void Restart(GrowthEngine engine, NaturalLedger ledger)
    {
        engine.ResetBattle();
        ledger.OnBattleReset();   // exit edge
        ledger.OnBattleReset();   // enter edge
    }

    // ---- Hold (the multiplicative growth lane): the compounding killer ----

    [Fact]
    public void Hold_does_not_compound_across_chained_restarts()
    {
        var (engine, mem, ledger) = Build();
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 18;

        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);            // battle 1: 18 -> 21
        Assert.Equal((byte)21, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = 21;                                          // baked rebuild
        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);            // battle 2: corrected, stays 21
        Assert.Equal((byte)21, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = 21;                                          // baked again
        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);            // battle 3: STILL 21, never 24
        Assert.Equal((byte)21, mem.U8s[addr]);                       // pre-fix: 21 -> 24 -> 28 ...
    }

    [Fact]
    public void Hold_baked_token_reowns_after_a_normalize_to_the_residue()
    {
        // Tier crosses during the restart (kills persist), so the recomputed target differs
        // from the baked value: the engine's normalize restores the BAKED baseline, which the
        // record's baked token must recognize or the hold goes foreign for the whole battle.
        var (engine, mem, ledger) = Build();
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 18;

        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);            // battle 1: 18 -> 21
        Restart(engine, ledger);
        mem.U8s[addr] = 21;                                          // baked rebuild

        engine.Hold(addr, 0.30, StatLane.Speed, NameId, 30);            // battle 2, higher tier: corrected 18 -> 23
        Assert.Equal((byte)23, mem.U8s[addr]);

        mem.U8s[addr] = 21;                                          // the engine normalizes to the baked baseline
        engine.Hold(addr, 0.30, StatLane.Speed, NameId, 30);            // re-owned via the baked token
        Assert.Equal((byte)23, mem.U8s[addr]);                       // without it: foreign forever, stays 21
    }

    [Fact]
    public void Hold_level_up_to_the_exact_target_is_accepted_not_corrected()
    {
        // The implementation review's major: a +1 boost target collides with the unit's
        // ordinary +1 level-up gain. Natural 10 at level 30 holds 11; the unit levels to 31
        // and its TRUE natural is now 11. The level key must accept 11, not correct it to 10
        // forever (a restart never commits a level, so level inequality means real change).
        var (engine, mem, ledger) = Build();
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        engine.Hold(addr, 0.10, StatLane.Speed, NameId, 30);         // battle A: 10 -> 11 recorded
        Assert.Equal((byte)11, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = 11;                                          // battle B: TRUE natural 11 (leveled)
        engine.Hold(addr, 0.10, StatLane.Speed, NameId, 31);         // level moved: accept, not correct
        Assert.Equal((byte)12, mem.U8s[addr]);                       // round(11*1.1) = 12, the earned point kept
    }

    // ---- Afterimage (owns the Speed lane) ----

    [Fact]
    public void Afterimage_does_not_compound_across_chained_restarts()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Swiftedge", Wp = 10, Cat = "Sword", Formula = 99, Flavor = "afterimage blade",
            Signature = new WeaponSignature { AtTier = 3, Afterimage = true, DisplayLabel = "Afterimage" }
        };
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        // Below +3 (tier 1): ramp dormant, pure tier growth -- deterministic via Tuning.
        int t1 = (int)Math.Round(10 * (1 + Tuning.SpeedFactor[1]));
        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t1, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = (byte)t1;                                    // baked rebuild
        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t1, mem.U8s[addr]);                       // corrected: target recomputed from 10

        Restart(engine, ledger);
        mem.U8s[addr] = (byte)t1;
        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t1, mem.U8s[addr]);                       // pre-fix: compounds each restart
    }

    // ---- Ultima (owns the PA lane): the HP-scaling must survive a corrected capture ----

    [Fact]
    public void Ultima_scales_from_the_true_natural_after_a_restart_bake()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Materia Blade", Wp = 10, Cat = "Sword", Formula = 1, Flavor = "ultima blade",
            Signature = new WeaponSignature { AtTier = 3, Ultima = true, DisplayLabel = "Ultima" }
        };
        long addr = StructBase + Offsets.CPa;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        // Battle 1 at FULL HP: the hold boosts PA above natural (the bake source).
        var fp = (lvl: 30, br: 65, fa: 60);
        MemSeats.SeatBand(mem, 4, weapon: 77, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 3, gy: 3, hp: 300, maxHp: 300, speed: 7);
        // ReadHp pre-checks Readable on AMaxHp (FakeSparseMemory's exact-address contract).
        mem.ReadableAddrs.Add(Band.Entry(4) + Offsets.AMaxHp);
        int fullTarget = UltimaPolicy.PaHeld(10, 300, 300, tier: 0, Tuning.UltimaMul);
        engine.HoldUltima(StructBase, m, tier: 0, level: fp.lvl, brave: fp.br, faith: fp.fa, rosterNameId: NameId);
        Assert.Equal((byte)fullTarget, mem.U8s[addr]);
        Assert.True(fullTarget > 10, "test premise: full HP boosts PA above natural");

        // Restart with the boost baked; battle 2 opens HURT: the held PA must scale from the
        // TRUE natural 10, not from the baked full-HP target (the plan review's brick trace).
        Restart(engine, ledger);
        mem.U8s[addr] = (byte)fullTarget;
        MemSeats.SeatBand(mem, 4, weapon: 77, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 3, gy: 3, hp: 60, maxHp: 300, speed: 7);
        int hurtTarget = UltimaPolicy.PaHeld(10, 60, 300, tier: 0, Tuning.UltimaMul);
        engine.HoldUltima(StructBase, m, tier: 0, level: fp.lvl, brave: fp.br, faith: fp.fa, rosterNameId: NameId);
        Assert.Equal((byte)hurtTarget, mem.U8s[addr]);
    }

    // ---- Mushin (owns the PA lane) ----

    [Fact]
    public void Mushin_does_not_compound_across_chained_restarts()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Kiku-ichimonji", Wp = 10, Cat = "Katana", Formula = 1, Flavor = "stillness blade",
            Signature = new WeaponSignature { AtTier = 3, Mushin = true, DisplayLabel = "Mushin" }
        };
        long addr = StructBase + Offsets.CPa;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        int t3 = MushinPolicy.PaHeld(10, tier: 3, Tuning.Factor, stacks: 0, Tuning.MushinBonus);
        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t3, mem.U8s[addr]);
        Assert.True(t3 > 10, "test premise: tier-3 growth boosts PA above natural");

        Restart(engine, ledger);
        mem.U8s[addr] = (byte)t3;                                    // baked rebuild
        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t3, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = (byte)t3;
        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal((byte)t3, mem.U8s[addr]);                       // pre-fix: compounds each restart
    }

    // ---- HoldTimedStat (the flat Speed grant) ----

    [Fact]
    public void TimedStat_revert_restores_the_true_natural_after_a_restart_bake()
    {
        var (engine, mem, ledger) = Build();
        var sig = new WeaponSignature { AtTier = 3, StatBonus = 3, Stat = "Speed", ForTurns = 2, DisplayLabel = "Charge" };
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 8;

        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 0, rosterNameId: NameId, level: 30);   // battle 1: 8 -> 11
        Assert.Equal((byte)11, mem.U8s[addr]);

        Restart(engine, ledger);
        mem.U8s[addr] = 11;                                          // baked rebuild

        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 0, rosterNameId: NameId, level: 30);   // corrected capture (nat 8)
        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 5, rosterNameId: NameId, level: 30);   // window over: revert
        Assert.Equal((byte)8, mem.U8s[addr]);                        // pre-fix: captured 11, reverts to 11
    }

    [Fact]
    public void TimedStat_post_revert_normalize_to_the_residue_is_re_corrected()
    {
        // The implementation review's minor: after the window-over revert the record was
        // dropped, so a later normalize restoring the baked residue re-stuck the bonus for
        // the rest of the battle. The corrective sentinel keeps watching (the Iai
        // post-release corrective hold's sibling).
        var (engine, mem, ledger) = Build();
        var sig = new WeaponSignature { AtTier = 3, StatBonus = 3, Stat = "Speed", ForTurns = 2, DisplayLabel = "Charge" };
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 8;

        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 0, rosterNameId: NameId, level: 30);   // battle 1: 8 -> 11
        Restart(engine, ledger);
        mem.U8s[addr] = 11;                                                                          // baked rebuild

        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 0, rosterNameId: NameId, level: 30);   // corrected capture
        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 5, rosterNameId: NameId, level: 30);   // revert -> 8
        Assert.Equal((byte)8, mem.U8s[addr]);

        mem.U8s[addr] = 11;                                                                          // normalize restores the residue
        engine.HoldTimedStat(StructBase, sig, tier: 3, turns: 6, rosterNameId: NameId, level: 30);
        Assert.Equal((byte)8, mem.U8s[addr]);                        // the sentinel re-corrects
    }
}
