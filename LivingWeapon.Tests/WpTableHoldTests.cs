using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-317 stage 3: WpTableHold, the turn-scoped resident item-stats WP write for the "wp"/
/// "wp+faith" gun lanes (see WpTableHold.cs's class doc). Module-level coverage against
/// FakeSparseMemory band/roster seats, mirroring WeaponPaletteTests.cs's shape -- a SeatOwner
/// builder that stages a FlagOwner-satisfying band entry, then per-test Tick sequences
/// asserting on FakeSparseMemory.U8s/WriteOrder (never on a comment citation).
/// </summary>
public class WpTableHoldTests
{
    private const int Slot = Offsets.SlotsBack;          // mirrors WeaponPaletteTests' own band slot
    private const int SlotOther = Offsets.SlotsBack + 1;

    private static long StatsAddr(int weaponId)
        => Offsets.ItemStatsBase + (long)weaponId * Offsets.ItemStatsStride + Offsets.ItemStatsWpOff;

    /// <summary>Seeds a band entry as the FlagOwner (real position, ATurnFlag == 1) wielding
    /// <paramref name="weaponId"/> -- mirrors WeaponPaletteTests.SeatOwner.</summary>
    private static long SeatOwner(FakeSparseMemory mem, int slot, int weaponId, int gx = 5, int gy = 5)
    {
        long entry = Band.Entry(slot);
        MemSeats.SeatBand(mem, slot, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: gx, gy: gy);
        mem.U8s[entry + Offsets.ATurnFlag] = 1;
        return entry;
    }

    /// <summary>Seeds a roster row wielding <paramref name="weaponId"/> in its main hand, at the
    /// EXACT fingerprint <see cref="SeatOwner"/> stages -- the "player filter"
    /// (Wielder.ResolveAnyHandNameId) a real player wielder must clear.</summary>
    private static void SeatMatchingRoster(FakeSparseMemory mem, int rosterSlot, int weaponId)
        => MemSeats.SeatRoster(mem, rosterSlot, lvl: 30, br: 50, fa: 50, rh: weaponId);

    private static Dictionary<int, WeaponMeta> Meta(int weaponId, string lane, int wp = 16)
        => new()
        {
            [weaponId] = new WeaponMeta { Name = "Test Gun", Wp = wp, Cat = "Gun", Formula = 4, Flavor = "f", Lane = lane },
        };

    private static Dictionary<int, int> KillsAtTier3(int weaponId)
        => new() { [weaponId] = Tuning.KillThresholds[2] };

    // ---- TDD item 10 ----

    [Fact]
    public void WpTableHold_WritesOnWieldersTurnRestoresAfter()
    {
        const int gunId = 73;
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, gunId);
        SeatMatchingRoster(mem, 0, gunId);
        long addr = StatsAddr(gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;   // baked WP
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)19, mem.U8s[addr]);   // 16 + WpBonus[3] (3)

        // Acting moves on: a different band entry (wielding an unrelated, un-metaed weapon) now
        // owns the turn.
        mem.U8s[entry + Offsets.ATurnFlag] = 0;
        long otherEntry = Band.Entry(SlotOther);
        MemSeats.SeatBand(mem, SlotOther, weapon: 1, lvl: 40, br: 60, fa: 60, gx: 1, gy: 1);
        mem.U8s[otherEntry + Offsets.ATurnFlag] = 1;

        hold.Tick(inLive: true);
        Assert.Equal((byte)16, mem.U8s[addr]);   // restored, with a read-back verify
    }

    // ---- LW-346: extended-inventory ids have no row in the resident table ----

    [Fact]
    public void WpTableHold_NeverIndexesPastTheResidentTableForAnExtendedId()
    {
        const int extId = ExtendedCatalog.FirstExtendedId;   // 261: row 261 would land in the EquipBonus table
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, extId);
        SeatMatchingRoster(mem, 0, extId);
        long addr = StatsAddr(extId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;
        var hold = new WpTableHold(Meta(extId, "wp", wp: 16), KillsAtTier3(extId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)16, mem.U8s[addr]);   // untouched: the id is past Offsets.ItemStatsRows
        Assert.DoesNotContain(mem.WriteOrder, a => a == addr);
    }

    [Fact]
    public void WpTableHold_BumpsAnExtendedIdInsideItsOwnStubRow()
    {
        const int extId = ExtendedCatalog.FirstExtendedId;
        const long row = 0x150000038L;   // the stub page's first 8-byte row (any address; the resolver owns it)
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, extId);
        SeatMatchingRoster(mem, 0, extId);
        long addr = row + Offsets.ItemStatsWpOff;
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;
        var hold = new WpTableHold(Meta(extId, "wp", wp: 16), KillsAtTier3(extId), mem, id => id == extId ? row : -1);

        hold.Tick(inLive: true);
        Assert.Equal((byte)19, mem.U8s[addr]);   // bumped inside OUR row, never the resident table
        Assert.DoesNotContain(mem.WriteOrder, a => a == StatsAddr(extId));

        mem.U8s[entry + Offsets.ATurnFlag] = 0;
        for (int i = 0; i < Tuning.WpUnresolvedRestoreTicks; i++) hold.Tick(inLive: true);
        Assert.Equal((byte)16, mem.U8s[addr]);   // restored the same way
    }

    [Fact]
    public void WpTableHold_LeavesAnExtendedIdAloneWhenTheResolverDoesNotKnowIt()
    {
        const int extId = ExtendedCatalog.FirstExtendedId + 1;
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, extId);
        SeatMatchingRoster(mem, 0, extId);
        var hold = new WpTableHold(Meta(extId, "wp", wp: 16), KillsAtTier3(extId), mem, _ => -1);
        hold.Tick(inLive: true);
        Assert.Empty(mem.WriteOrder);
    }

    // ---- TDD item 11 ----

    [Fact]
    public void WpTableHold_ForeignByteStandsDown()
    {
        const int gunId = 73;
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, gunId);
        SeatMatchingRoster(mem, 0, gunId);
        long addr = StatsAddr(gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 22;   // neither the baked WP (16) nor any target we could have written
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)22, mem.U8s[addr]);   // left alone
        Assert.Empty(mem.WriteOrder);

        // Stood down for the rest of the battle: a second tick makes no further attempt (and so
        // logs no second warning -- the stand-down latch is what keeps the log to one line).
        hold.Tick(inLive: true);
        Assert.Equal((byte)22, mem.U8s[addr]);
        Assert.Empty(mem.WriteOrder);
    }

    // ---- TDD item 12 (three-part bundle) ----

    [Fact]
    public void WpTableHold_EnemyTurnOrRefusal_RestoresAndNeverBumps()
    {
        const int gunId = 73;
        long addr = StatsAddr(gunId);

        // Enemy turn: FlagOwner resolves a real band entry wielding the catalogued gun, but no
        // roster row wields it at that fingerprint (an enemy).
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);
        hold.Tick(inLive: true);
        Assert.Equal((byte)16, mem.U8s[addr]);
        Assert.Empty(mem.WriteOrder);

        // Outright FlagOwner refusal: no band entry carries ATurnFlag == 1 at all.
        var mem2 = new FakeSparseMemory();
        mem2.MarkWritable(addr, 1);
        mem2.U8s[addr] = 16;
        var hold2 = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem2);
        hold2.Tick(inLive: true);
        Assert.Equal((byte)16, mem2.U8s[addr]);
        Assert.Empty(mem2.WriteOrder);
    }

    [Fact]
    public void WpTableHold_ResetBattle_RestoresOutstanding()
    {
        const int gunId = 73;
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, gunId);
        SeatMatchingRoster(mem, 0, gunId);
        long addr = StatsAddr(gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)19, mem.U8s[addr]);

        hold.ResetBattle();
        Assert.Equal((byte)16, mem.U8s[addr]);   // physically restored: no battle-load refresh for this table
    }

    // ---- F1 (rev 3 ruling R3-6): a foreign write landing MID-HOLD must be dropped, not
    // stomped back to baked -- the foreign writer has already overwritten our bump, so nothing
    // of ours is outstanding, and restoring baked would clobber the very value the foreign-write
    // guard exists to protect. Contrast with WpTableHold_ForeignByteStandsDown above, which is
    // the PRE-hold case (no write of ours ever landed) and keeps its existing refuse-to-write
    // behavior unchanged.

    [Fact]
    public void WpTableHold_ForeignMidHold_DropsWithoutRestoring()
    {
        const int gunId = 73;
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, gunId);
        SeatMatchingRoster(mem, 0, gunId);
        long addr = StatsAddr(gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;   // baked WP
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)19, mem.U8s[addr]);   // our bump landed, hold outstanding

        // A foreign writer stomps the byte to an unrelated value mid-hold.
        mem.U8s[addr] = 22;

        hold.Tick(inLive: true);   // ownership pre-read finds neither baked (16) nor target (19)
        Assert.Equal((byte)22, mem.U8s[addr]);   // stand-down: no write ever lands over it

        // Acting moves on: end of turn, the normal restore trigger.
        mem.U8s[entry + Offsets.ATurnFlag] = 0;
        long otherEntry = Band.Entry(SlotOther);
        MemSeats.SeatBand(mem, SlotOther, weapon: 1, lvl: 40, br: 60, fa: 60, gx: 1, gy: 1);
        mem.U8s[otherEntry + Offsets.ATurnFlag] = 1;

        hold.Tick(inLive: true);
        Assert.Equal((byte)22, mem.U8s[addr]);   // still no restore write -- the record was cleared

        // A battle reset must not restore either -- there is nothing outstanding to restore.
        hold.ResetBattle();
        Assert.Equal((byte)22, mem.U8s[addr]);
    }

    [Fact]
    public void WpTableHold_TransientRefusal_GracesThreeTicksBeforeRestoring()
    {
        const int gunId = 73;
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, gunId);
        SeatMatchingRoster(mem, 0, gunId);
        long addr = StatsAddr(gunId);
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 16;
        var hold = new WpTableHold(Meta(gunId, "wp", wp: 16), KillsAtTier3(gunId), mem);

        hold.Tick(inLive: true);
        Assert.Equal((byte)19, mem.U8s[addr]);

        // ATurnFlag drops for everyone this tick -- a transient scan gap, not a clean "someone
        // else's turn" signal. Band.FlagOwner refuses.
        mem.U8s[entry + Offsets.ATurnFlag] = 0;

        hold.Tick(inLive: true);   // refusal tick 1 of 3: grace, no restore yet
        Assert.Equal((byte)19, mem.U8s[addr]);
        hold.Tick(inLive: true);   // refusal tick 2 of 3
        Assert.Equal((byte)19, mem.U8s[addr]);
        hold.Tick(inLive: true);   // refusal tick 3 of 3: grace exhausted -> restore
        Assert.Equal((byte)16, mem.U8s[addr]);
    }
}
