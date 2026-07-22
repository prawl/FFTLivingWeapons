using System.Collections.Generic;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// MODULE-LEVEL proof that slot-scoped release (Barrage.Policy.ReleaseSlot) actually stops the
/// two-signature collision the old whole-record restore caused. Drives the REAL Barrage and
/// ShadowBlade classes (not just the ReleaseSlot/InjectSlot primitives -- see BarrageTests for the
/// primitive-level twin of the same scenario) against a staged FakeSparseMemory roster + JobCommand
/// record, ticked in the SAME order Engine.cs uses (Barrage before ShadowBlade).
///
/// THE COLLISION RECIPE (both land in record 7, Knight's "Arts of War"):
///   - Wielder A is a Knight (job 76) with a Yoichi Bow in hand AND Steal (14) mounted as a
///     secondary. Barrage.IsEligibleWielder passes on the secondary check, but TryResolveGrant
///     resolves via the PRIMARY job (Knight is a normal executor) -- landing in record 7, NOT
///     Steal's own record 14. Barrage does NOT always target record 14 (see the correction on
///     Barrage.Policy.TryResolveGrant); this is exactly how it can share a record with ShadowBlade.
///   - Wielder B is a Knight (job 76) with a Sanguine Sword in hand -- Knight is directly on
///     ShadowBladePolicy's whitelist, landing in the SAME record 7.
/// This is the "two Knights, one wielding a Defender-like Knight-sword grant, one a Sanguine Sword"
/// scenario the fix exists for, reproduced with the two grants that already ship (Provoke does not
/// exist yet to test directly).
/// </summary>
public class BarrageShadowBladeCollisionTests
{
    private const int YoichiId = 90;      // Barrage.YoichiId (private in production)
    private const int SanguineId = 23;    // ShadowBlade.SanguineId (private in production)
    private const int BarrageAbilityId = 358;
    private const int KnightJob = 76;
    private const int KnightRecord = 7;
    private const int StealRecord = 14;

    private static long FlagAddr(int recId) => Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
    private static long AbBase(int recId) => Barrage.AbilityBase + (long)recId * Barrage.RecSize;

    /// <summary>Register a record as readable/writable at every byte InjectSlot/ReleaseSlot can
    /// touch, and back it with a TerrainBlocks buffer so TryReadBytes (the whole-record scan
    /// Tick() uses to run FindEmptySlot) can serve it. Returns the buffer -- see <see cref="Sync"/>.</summary>
    private static byte[] StageRecord(FakeSparseMemory m, int recId)
    {
        long flagAddr = FlagAddr(recId), abBase = AbBase(recId);
        var buf = new byte[Barrage.RecSize];
        m.TerrainBlocks[flagAddr] = buf;
        for (int i = 0; i < Barrage.FlagPrefixSize; i++) { m.ReadableAddrs.Add(flagAddr + i); m.WritableAddrs.Add(flagAddr + i); }
        for (int i = 0; i < Barrage.AbilityCount; i++) { m.ReadableAddrs.Add(abBase + i); m.WritableAddrs.Add(abBase + i); }
        return buf;
    }

    /// <summary>Mirror the fake's per-address U8s (what InjectSlot/ReleaseSlot's W8 calls actually
    /// write) into the TerrainBlocks-backed buffer TryReadBytes serves. W8 does NOT touch
    /// TerrainBlocks on its own (real process memory has no such distinction -- this is purely a
    /// FakeSparseMemory bookkeeping split), so a Tick() would otherwise read a stale record and
    /// either re-inject redundantly or (worse, for a SECOND module ticking right after) fail to see
    /// the first module's write and collide on the same slot. Call before every Tick() and again
    /// before reading <paramref name="buf"/> for assertions.</summary>
    private static void Sync(FakeSparseMemory m, byte[] buf, int recId)
    {
        long flagAddr = FlagAddr(recId);
        for (int i = 0; i < buf.Length; i++) buf[i] = m.U8(flagAddr + i);
    }

    private static void SeatWielder(FakeSparseMemory m, int rosterSlot, int rhand, int job, int secRec)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        m.U16s[rb + Offsets.RNameId] = 1;
        m.ReadableAddrs.Add(rb + Offsets.RNameId);
        m.U8s[rb + Offsets.RLevel] = 30;
        m.U16s[rb + Offsets.RRHand] = (ushort)rhand;
        m.U8s[rb + Barrage.RJobId] = (byte)job;
        m.U8s[rb + Barrage.RSecondary] = (byte)secRec;
    }

    private static Dictionary<int, WeaponMeta> Meta() => new()
    {
        [YoichiId] = new WeaponMeta { Signature = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = BarrageAbilityId } },
        [SanguineId] = new WeaponMeta { Signature = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = ShadowBladePolicy.ShadowBladeAbilityId } },
    };

    private static Dictionary<int, int> MaxKills() => new() { [YoichiId] = 999, [SanguineId] = 999 };

    [Fact]
    public void Barrage_and_shadow_blade_share_one_record_without_corrupting_each_other()
    {
        var m = new FakeSparseMemory();
        var buf = StageRecord(m, KnightRecord);
        SeatWielder(m, rosterSlot: 0, rhand: YoichiId, job: KnightJob, secRec: StealRecord);   // Wielder A
        SeatWielder(m, rosterSlot: 1, rhand: SanguineId, job: KnightJob, secRec: 0);            // Wielder B

        var kills = MaxKills();
        var barrage = new Barrage(Meta(), kills, m);
        var shadow = new ShadowBlade(Meta(), kills, m);

        void TickBarrage() { Sync(m, buf, KnightRecord); barrage.Tick(); Sync(m, buf, KnightRecord); }
        void TickShadow() { Sync(m, buf, KnightRecord); shadow.Tick(); Sync(m, buf, KnightRecord); }

        // Engine.cs order: Barrage ticks first, so ShadowBlade's FindEmptySlot sees its write.
        TickBarrage();
        TickShadow();

        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 0]);   // Barrage took slot 1 (102 = 358 & 0xFF)
        Assert.Equal((byte)165, buf[Barrage.FlagPrefixSize + 1]);   // Shadow Blade took slot 2, not slot 1

        // Wielder A unequips the Yoichi Bow -- Barrage's grant ends. Under the OLD whole-record
        // restore, this step wiped Shadow Blade's slot too (it would self-heal on ShadowBlade's
        // next tick, but the game would render Shadow Blade missing for one tick, and if instead
        // Shadow Blade ended FIRST it would have resurrected Barrage permanently -- see the class
        // doc on ShadowBlade.cs). ReleaseSlot only ever touches slot 1.
        m.U16s[Offsets.RosterBase + 0 * Offsets.RosterStride + Offsets.RRHand] = 0xFFFF;
        TickBarrage();

        Assert.Equal((byte)0, buf[Barrage.FlagPrefixSize + 0]);     // Barrage's slot released
        Assert.Equal((byte)165, buf[Barrage.FlagPrefixSize + 1]);   // Shadow Blade SURVIVES untouched

        // Wielder B unequips the Sanguine Sword too -- Shadow Blade's grant ends.
        m.U16s[Offsets.RosterBase + 1 * Offsets.RosterStride + Offsets.RRHand] = 0xFFFF;
        TickShadow();

        Assert.Equal(new byte[Barrage.RecSize], buf);   // byte-identical to the pre-injection record
        Assert.Equal((byte)0, buf[Barrage.FlagPrefixSize + 0]);   // Barrage was NOT resurrected by Shadow Blade's release
    }

    [Fact]
    public void Releasing_a_slot_then_regranting_finds_a_fresh_slot_instead_of_reusing_a_stale_index()
    {
        var m = new FakeSparseMemory();
        var buf = StageRecord(m, KnightRecord);
        SeatWielder(m, rosterSlot: 0, rhand: YoichiId, job: KnightJob, secRec: StealRecord);

        var barrage = new Barrage(Meta(), MaxKills(), m);
        void Tick() { Sync(m, buf, KnightRecord); barrage.Tick(); Sync(m, buf, KnightRecord); }

        Tick();
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 0]);   // slot 1

        // Unequip -> release. BarrageState.Clear() runs unconditionally.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;
        Tick();
        Assert.Equal((byte)0, buf[Barrage.FlagPrefixSize + 0]);

        // Something else now legitimately occupies slot 1 (a real ability the player learned, or a
        // table rebuild default). A stale remembered slotIdx applied blindly here would either
        // silently stomp it (old behavior) or find the slot already "correct" and never move off
        // it. Correct behavior: re-run FindEmptySlot and land on the NEXT empty slot.
        m.U8s[AbBase(KnightRecord) + 0] = 77;
        buf[Barrage.FlagPrefixSize + 0] = 77;

        // Re-equip -> re-grant.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = (ushort)YoichiId;
        Tick();

        Assert.Equal((byte)77, buf[Barrage.FlagPrefixSize + 0]);    // the real ability is untouched
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 1]);   // Barrage landed on slot 2 instead
    }

    [Fact]
    public void Job_change_releases_the_old_records_slot_and_verifies_emptiness_in_the_new_record()
    {
        var m = new FakeSparseMemory();
        var stealBuf = StageRecord(m, StealRecord);
        var knightBuf = StageRecord(m, KnightRecord);
        // A real ability already sits in the Knight record's first slot -- proves the re-inject
        // after the job change actually re-verifies emptiness in record 7 instead of blindly
        // reapplying the slotIdx (0) it used in record 14.
        m.U8s[AbBase(KnightRecord) + 0] = 88;
        knightBuf[Barrage.FlagPrefixSize + 0] = 88;

        // Thief primary -> Barrage's own record, Steal (14), no secondary trick needed.
        SeatWielder(m, rosterSlot: 0, rhand: YoichiId, job: Barrage.ThiefJob, secRec: 0);

        var barrage = new Barrage(Meta(), MaxKills(), m);
        void TickInto(int recId, byte[] buf) { Sync(m, buf, recId); barrage.Tick(); Sync(m, buf, recId); }

        TickInto(StealRecord, stealBuf);
        Assert.Equal((byte)102, stealBuf[Barrage.FlagPrefixSize + 0]);   // injected into Steal's record 14

        // Job changes to Knight, with Steal still mounted as the secondary (keeps IsEligibleWielder
        // true) -- TryResolveGrant now resolves via the PRIMARY job, record 7.
        m.U8s[Offsets.RosterBase + Barrage.RJobId] = (byte)KnightJob;
        m.U8s[Offsets.RosterBase + Barrage.RSecondary] = (byte)StealRecord;
        TickInto(KnightRecord, knightBuf);   // (StageRecord already made record 7 readable/writable)
        Sync(m, stealBuf, StealRecord);      // pull the release ReleaseSlot just made into stealBuf too

        Assert.Equal((byte)0, stealBuf[Barrage.FlagPrefixSize + 0]);     // released from the OLD record
        Assert.Equal((byte)88, knightBuf[Barrage.FlagPrefixSize + 0]);   // the real ability in the NEW record untouched
        Assert.Equal((byte)102, knightBuf[Barrage.FlagPrefixSize + 1]);  // Barrage found the next empty slot there
    }

    [Fact]
    public void A_refused_release_still_lets_the_module_find_a_new_slot_next_time()
    {
        var m = new FakeSparseMemory();
        var buf = StageRecord(m, KnightRecord);
        SeatWielder(m, rosterSlot: 0, rhand: YoichiId, job: KnightJob, secRec: StealRecord);

        var barrage = new Barrage(Meta(), MaxKills(), m);
        void Tick() { Sync(m, buf, KnightRecord); barrage.Tick(); Sync(m, buf, KnightRecord); }

        Tick();
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 0]);

        // Something else overwrites our slot directly (a table rebuild landing a real ability
        // there) WITHOUT going through us -- simulates the exact hazard ReleaseSlot's
        // verify-before-write guards against.
        m.U8s[AbBase(KnightRecord) + 0] = 200;
        buf[Barrage.FlagPrefixSize + 0] = 200;

        // Unequip -> Restore() runs, ReleaseSlot refuses (it's not our byte anymore), but
        // BarrageState.Clear() still runs unconditionally.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;
        Tick();
        Assert.Equal((byte)200, buf[Barrage.FlagPrefixSize + 0]);   // untouched by the refused release

        // Re-equip. If the module were still holding onto the stale slotIdx (0), it would either
        // fight forever over a slot it lost, or (with InjectSlot's own symmetric guard) simply
        // never write at all. Correct behavior after Clear(): re-run FindEmptySlot and use slot 2.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = (ushort)YoichiId;
        Tick();

        Assert.Equal((byte)200, buf[Barrage.FlagPrefixSize + 0]);   // still someone else's
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 1]);   // Barrage recovered onto a fresh slot
    }
}
