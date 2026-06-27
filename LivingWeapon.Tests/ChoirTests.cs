using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Warlock's Staff "Choir" signature -- HOLDER-ONLY.
/// While a +3 Warlock's Staff is held in the MAIN HAND and its bearer is alive:
/// ONLY the bearer's live band entry gets the Non-charge support bit (id 227, band +0x7F
/// mask 0x04) OR-set each tick so their magick casts instantly. No adjacent-ally aura.
///
/// Key design decisions tested here:
///   (1) IsActive: null/radius-0/below-tier -> false; at/above tier with radius -> true.
///   (3) SupportBit pins: id 227 -> (3, 0x04); constant relationship ASupport+3 == CSupport-BandEntry.
///   (4) LOAD-BEARING: bearer gets the bit; adjacent alive ally does NOT.
///   (5) Neighbor-bit OR -- a pre-existing bit (Swiftspell 0x08) is preserved on the bearer: result is 0x0C.
///   (6) Bearer self: bearer's own band entry gets the bit.
///   (7) Bearer-alive gate: HP 0 -> no set (with non-vacuous twin: HP > 0 -> set).
///   (8) Tier gate: below atTier -> no set; at tier -> bearer IS set.
///   (9) Enemy filter: enemy fp not in AllyFingerprints -> not set.
///   (10) Writable/Readable guard: bearer's support addr not in Writable/ReadableAddrs -> no set.
///   (13) Never-strip bearer's own Non-charge support (B2.1 regression).
///   (14) Benched second staff does not block the deployed bearer.
///   (15) ResetBattle clears tracking state.
///   LEVEL-DRIFT: band lvl != roster lvl; clear path still works (_granted is band-keyed).
///   FP-COLLISION: a non-bearer entry with same (mhp,lvl,br,fa) must NOT get the bit.
///   NEW-A: two deployed bearers each get their own bit; adjacent allies do NOT.
///   NEW-B: two deployed bearers, one unequips -> that bearer's bit cleared, other stays.
///   NEW-C: two staves -> exactly 2 winners (the two bearers).
/// </summary>
public class ChoirTests
{
    private const int WarlockStaffId = 60;
    private const int NonChargeId    = 227;

    private const int NcByteOff = 3;
    private const byte NcMask   = 0x04;
    private const int NcBandOff = Offsets.ASupport + NcByteOff;  // == 0x7F

    private static WeaponSignature ChoirSig(int atTier = 3, int radius = 1) =>
        new() { AtTier = atTier, InstantCastRadius = radius, DisplayLabel = "Choir", };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Choir.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_instantCastRadius_is_zero()
        => Assert.False(Choir.IsActive(new WeaponSignature { AtTier = 3, InstantCastRadius = 0 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Choir.IsActive(ChoirSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Choir.IsActive(ChoirSig(atTier: 3), tier: 3));
        Assert.True(Choir.IsActive(ChoirSig(atTier: 3), tier: 4));
    }

    // ---- (3) SupportBit pins and constant relationships ----

    [Fact]
    public void SupportBit_NonCharge_encodes_byte3_mask0x04()
    {
        Assert.True(Signatures.SupportBit(227, out int off, out byte mask));
        Assert.Equal(3, off);
        Assert.Equal(0x04, mask);
    }

    [Fact]
    public void SupportBit_id_198_encodes_byte0_mask0x80()
    {
        Assert.True(Signatures.SupportBit(198, out int off, out byte mask));
        Assert.Equal(0, off);
        Assert.Equal(0x80, mask);
    }

    [Theory]
    [InlineData(197)]
    [InlineData(230)]
    public void SupportBit_out_of_field_returns_false(int id)
        => Assert.False(Signatures.SupportBit(id, out _, out _));

    [Fact]
    public void ASupport_constant_is_0x7C()
        => Assert.Equal(0x7C, Offsets.ASupport);

    [Fact]
    public void ASupport_plus_BandEntry_equals_CSupport()
        => Assert.Equal(Offsets.CSupport, Offsets.BandEntry + Offsets.ASupport);

    [Fact]
    public void Band_write_offset_is_0x7F()
        // This must use the LITERAL 0x7F so a wrong ASupport or NcByteOff constant is caught
        // even if ASupport and NcByteOff drift together in lockstep.
        => Assert.Equal(0x7F, Offsets.ASupport + NcByteOff);

    // ---- Seeding helpers ----

    private static void SeedBandEntry(FakeSparseMemory mem, long addr,
        int hp, int maxHp, int lvl, int br, int fa, int gx, int gy,
        bool writableSupport = true)
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
        long supportAddr = addr + NcBandOff;
        if (writableSupport)
        {
            mem.WritableAddrs.Add(supportAddr);
            mem.ReadableAddrs.Add(supportAddr);
        }
    }

    private static void SeedAllyFp(FakeSparseMemory mem, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 1) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel]  = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave]  = (byte)br;
        mem.U8s[slot + Offsets.AFaith]  = (byte)fa;
    }

    /// <summary>Plant the idx-th distinct ally fingerprint (static array slot EnemySlotMax + idx).</summary>
    private static void SeedAllyFpAt(FakeSparseMemory mem, int idx, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + idx) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel]  = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave]  = (byte)br;
        mem.U8s[slot + Offsets.AFaith]  = (byte)fa;
    }

    /// <summary>True when the Non-charge bit was written SET at this entry's +0x7F.</summary>
    private static bool IsSet(FakeSparseMemory mem, long entry)
        => mem.Written.ContainsKey(entry + NcBandOff) && (mem.Written[entry + NcBandOff] & NcMask) != 0;

    /// <summary>True when the Non-charge bit was written CLEAR at this entry's +0x7F.</summary>
    private static bool IsCleared(FakeSparseMemory mem, long entry)
        => mem.Written.ContainsKey(entry + NcBandOff) && (mem.Written[entry + NcBandOff] & NcMask) == 0;

    private static void SeedRosterSlot(FakeSparseMemory mem, int rosterSlot,
        int lvl, int br, int fa, int mainHandId = WarlockStaffId, int rsupport = 0)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        mem.U8s[rb + Offsets.RLevel]   = (byte)lvl;
        mem.U8s[rb + Offsets.RBrave]   = (byte)br;
        mem.U8s[rb + Offsets.RFaith]   = (byte)fa;
        mem.U16s[rb + Offsets.RRHand]  = (ushort)mainHandId;
        mem.U8s[rb + Offsets.RSupport] = (byte)rsupport;
    }

    /// <summary>Build a standard active scenario: bearer alive (band slot 30), one adjacent
    /// ally (band slot 28, adjacent to bearer by default), kills >= atTier so active.
    /// Under holder-only, the ally is seeded as a real candidate (adjacent, alive, writable)
    /// so a regression to the old aura behavior would set the ally's bit and fail test (4).
    /// writableBearerSupport=false excludes the bearer's support addr from Writable/ReadableAddrs
    /// (for the guard tests (10)). writableAllySupport is retained for completeness.</summary>
    private static (Choir choir, FakeSparseMemory mem, long bearerEntry, long allyEntry)
        BuildActive(int tier = 3, int bearerHp = 200, int bearerGx = 5, int bearerGy = 5,
                    int allyGx = 6, int allyGy = 6, bool writableAllySupport = true,
                    bool writableBearerSupport = true, int rSupport = 0)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int>
        {
            [WarlockStaffId] = tier >= 1 ? Tuning.ProdThresholds[Math_Min(tier, 3) - 1] : 0
        };

        // Roster slot 0 = bearer
        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId, rsupport: rSupport);

        // Bearer band entry (band slot 30): weapon field for Wielder.Locate
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, bearerHp, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: bearerGx, gy: bearerGy, writableSupport: writableBearerSupport);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Ally band entry (band slot 28): adjacent by default; alive, in AllyFingerprints, writable.
        // Under holder-only, Choir must NOT set the ally's bit -- kept as a real candidate so a
        // regression to ally-aura would set it and be caught by test (4).
        long allyEntry = Band.Entry(28);
        SeedBandEntry(mem, allyEntry, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55,
                      gx: allyGx, gy: allyGy, writableSupport: writableAllySupport);
        SeedAllyFp(mem, mhp: 150, lvl: 20, br: 50, fa: 55);

        var choir = new Choir(meta, kills, mem: mem);
        return (choir, mem, bearerEntry, allyEntry);
    }

    private static int Math_Min(int a, int b) => a < b ? a : b;

    // ---- (4) LOAD-BEARING: bearer gets the bit; adjacent alive ally does NOT ----

    [Fact]
    public void Tick_sets_noncharge_on_bearer_and_not_on_adjacent_ally()
    {
        // Ally is adjacent (distance 1), alive, writable -- everything the old aura logic
        // would have granted it. Under holder-only it must NOT receive the bit.
        var (choir, mem, bearerEntry, allyEntry) = BuildActive(bearerGx: 5, bearerGy: 5,
                                                               allyGx: 6, allyGy: 6);
        choir.Tick(onField: true);

        // Bearer MUST have the bit
        Assert.True(IsSet(mem, bearerEntry), "bearer must get the Non-charge bit (holder-only)");

        // Adjacent alive ally must NOT have the bit (holder-only; aura regression would fail here)
        Assert.False(IsSet(mem, allyEntry),
            "adjacent ally must NOT get the bit -- Choir is holder-only; aura regression fails this arm");
    }

    // ---- (5) Neighbor-bit preserved (OR not clobber) on the BEARER ----

    [Fact]
    public void Tick_ORs_bit_not_clobbers_existing_neighbor_bit()
    {
        var (choir, mem, bearerEntry, _) = BuildActive();
        long supportAddr = bearerEntry + NcBandOff;
        // Pre-seed Swiftspell bit (0x08) on the BEARER -- must survive the OR-set
        mem.U8s[supportAddr] = 0x08;

        choir.Tick(onField: true);

        Assert.True(mem.Written.ContainsKey(supportAddr));
        Assert.Equal(0x0C, mem.Written[supportAddr]);   // 0x08 | 0x04
    }

    // ---- (6) Bearer self gets the bit (distance 0) ----

    [Fact]
    public void Tick_sets_bit_on_bearer_self()
    {
        var (choir, mem, bearerEntry, _) = BuildActive();
        // Register bearer fingerprint in a second static array slot so it doesn't overwrite
        // the ally fp seeded by BuildActive at slot EnemySlotMax+1
        long slot2 = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 2) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot2 + Offsets.AMaxHp);
        mem.U16s[slot2 + Offsets.AMaxHp] = 300;
        mem.U8s[slot2 + Offsets.ALevel]  = 35;
        mem.U8s[slot2 + Offsets.ABrave]  = 65;
        mem.U8s[slot2 + Offsets.AFaith]  = 60;

        choir.Tick(onField: true);

        long bearerSupportAddr = bearerEntry + NcBandOff;
        Assert.True(mem.Written.ContainsKey(bearerSupportAddr),
            "bearer's own +0x7F must be written");
        Assert.Equal(NcMask, (byte)(mem.Written[bearerSupportAddr] & NcMask));
    }

    // ---- (7) Bearer-alive gate (non-vacuous twin) ----

    [Fact]
    public void Tick_no_set_when_bearer_hp_is_zero()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(bearerHp: 0);

        choir.Tick(onField: true);

        Assert.False(IsSet(mem, bearerEntry), "bearer dead -> bearer's own bit must NOT be set");
    }

    [Fact]
    public void Tick_sets_bit_when_bearer_is_alive_vacuity_twin()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(bearerHp: 200);

        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerEntry), "bearer alive -> bearer's own bit must be set");
    }

    // ---- (8) Tier gate ----

    [Fact]
    public void Tick_no_set_when_tier_below_atTier()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(tier: 2);   // 2 < atTier 3

        choir.Tick(onField: true);

        Assert.False(IsSet(mem, bearerEntry), "below tier -> bearer bit must NOT be set");
    }

    [Fact]
    public void Tick_sets_bit_at_tier()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(tier: 3);

        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerEntry), "at tier -> bearer bit must be set");
    }

    // ---- (9) Enemy filter: enemy fp not in AllyFingerprints -> not set ----

    [Fact]
    public void Tick_does_not_set_bit_on_enemy()
    {
        var (choir, mem, _, _) = BuildActive();

        // Enemy-only band entry at an adjacent tile (not in ally fp set, not a roster wielder)
        long enemyEntry = Band.Entry(20);
        SeedBandEntry(mem, enemyEntry, hp: 100, maxHp: 100, lvl: 18, br: 60, fa: 50,
                      gx: 6, gy: 5, writableSupport: true);
        // Not added to ally fingerprints

        choir.Tick(onField: true);

        Assert.False(IsSet(mem, enemyEntry), "enemy units must not get the Non-charge bit");
    }

    // ---- (10) Writable/Readable guard -- gated on the BEARER's support address ----

    [Fact]
    public void Tick_no_set_when_bearer_support_addr_not_writable()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(writableBearerSupport: false);

        choir.Tick(onField: true);

        Assert.False(IsSet(mem, bearerEntry),
            "non-writable bearer support addr -> bit must not be written");
    }

    [Fact]
    public void Tick_no_set_when_bearer_support_addr_not_readable()
    {
        var (choir, mem, bearerEntry, _) = BuildActive(writableBearerSupport: false);
        // Add writable but NOT readable
        mem.WritableAddrs.Add(bearerEntry + NcBandOff);
        // ReadableAddrs does NOT have it

        choir.Tick(onField: true);

        Assert.False(IsSet(mem, bearerEntry),
            "non-readable bearer support addr -> bit must not be written");
    }

    // ---- (13) Never-strip bearer's own Non-charge support ----

    [Fact]
    public void Tick_never_strips_bearers_own_NonCharge_support()
    {
        // The protected bearer's OWN roster RSupport == 227 -> protectedBF excludes them from
        // winners. Choir must never set OR clear their innate bit. A canary bearer (slot 1,
        // RSupport=0) confirms Choir IS running at tier 3.
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        // Protected bearer: roster RSupport=227 -> (br=65,fa=60) lands in protectedBF
        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId, rsupport: NonChargeId);
        long protectedEntry = Band.Entry(30);
        SeedBandEntry(mem, protectedEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[protectedEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;
        // Pre-set their innate Non-charge bit
        mem.U8s[protectedEntry + NcBandOff] = NcMask;

        // Canary bearer: RSupport=0, also wielding the staff; confirms Choir is active
        SeedRosterSlot(mem, 1, lvl: 30, br: 70, fa: 68, mainHandId: WarlockStaffId, rsupport: 0);
        long canaryEntry = Band.Entry(28);
        SeedBandEntry(mem, canaryEntry, hp: 180, maxHp: 250, lvl: 30, br: 70, fa: 68,
                      gx: 6, gy: 6, writableSupport: true);
        mem.U16s[canaryEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        SeedAllyFpAt(mem, 1, 300, 35, 65, 60);
        SeedAllyFpAt(mem, 2, 250, 30, 70, 68);

        var choir = new Choir(meta, kills, mem: mem);

        // Tick 1: Choir is active (canary bearer alive); protected bearer excluded from winners
        choir.Tick(onField: true);

        // Canary got the bit (proves Choir ran)
        Assert.True(IsSet(mem, canaryEntry), "canary bearer must get the bit (Choir is running)");
        // Protected bearer: Choir must not overwrite their innate bit via SET
        Assert.False(mem.Written.ContainsKey(protectedEntry + NcBandOff) &&
                     (mem.Written[protectedEntry + NcBandOff] & NcMask) != 0,
            "protected bearer's innate Non-charge must not be overwritten by Choir SET");

        // Tick 2: protected bearer "dies"; their innate bit must NOT be cleared
        mem.U16s[protectedEntry + Offsets.AHp] = 0;
        mem.U8s[protectedEntry + NcBandOff] = NcMask;   // still set from their own pick
        mem.U8s[canaryEntry + NcBandOff]    = 0;        // canary: let SetBit write so IsSet works
        mem.Written.Clear();

        choir.Tick(onField: true);

        Assert.False(IsCleared(mem, protectedEntry),
            "protected bearer's innate Non-charge must never be cleared by Choir -- not in _granted");
    }

    // ---- (14) Benched second staff does not block the deployed bearer ----

    [Fact]
    public void Tick_benched_second_staff_does_not_block_the_deployed_bearer()
    {
        var (choir, mem, bearerEntry, _) = BuildActive();
        // Second roster slot holds id 60 in main hand -- NO band entry (benched)
        SeedRosterSlot(mem, 1, lvl: 28, br: 70, fa: 65, mainHandId: WarlockStaffId);

        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerEntry),
            "benched second staff must not block the deployed bearer from getting the bit");
    }

    // ---- (15) ResetBattle clears tracking ----

    [Fact]
    public void ResetBattle_clears_granted_state_so_revert_does_not_fire_next_battle()
    {
        var (choir, mem, bearerEntry, _) = BuildActive();

        // Grant the bit on the bearer
        choir.Tick(onField: true);
        Assert.True(IsSet(mem, bearerEntry));

        // Reset (simulates battle exit)
        choir.ResetBattle();

        // Simulate a new inactive Choir (tier 0 -> inactive); pre-seed the bearer's bit so
        // ClearBit WOULD fire if _granted weren't clear.
        mem.U8s[bearerEntry + NcBandOff] = NcMask;
        mem.Written.Clear();

        var metaInactive = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var killsZero = new Dictionary<int, int> { [WarlockStaffId] = 0 };  // tier 0 -> inactive
        var choirFresh = new Choir(metaInactive, killsZero, mem: mem);
        choirFresh.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(bearerEntry + NcBandOff),
            "after ResetBattle, _granted is clear so no stale ClearBit fires on the bearer");
    }

    // ---- LEVEL-DRIFT bite test ----

    [Fact]
    public void Tick_clears_bit_under_band_level_drift()
    {
        // Bearer roster lvl=20; band entry lvl=23 (mid-battle level-up drift, within MaxLevelDrift=9).
        // Locate tolerates the drift (LevelMatchesRoster). _granted must record the BAND-read fp
        // (mhp, lvl=23, br, fa), NOT the roster lvl=20. On tick 2 the bearer unequips; the clear
        // scan reads the band entry's lvl=23 and must find it in _granted -> ClearBit fires.
        // A roster-keyed _granted (lvl=20) would miss the band fp(23) -> stuck bit -> test fails.
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        // Roster: lvl=20
        SeedRosterSlot(mem, 0, lvl: 20, br: 65, fa: 60, mainHandId: WarlockStaffId);
        // Band entry: SAME br/fa, but lvl=23 (drifted up mid-battle)
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, hp: 200, maxHp: 300, lvl: 23, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        var choir = new Choir(meta, kills, mem: mem);

        // Tick 1: Locate uses LevelMatchesRoster(20, 23)=true -> finds the bearer -> bit set
        choir.Tick(onField: true);
        Assert.True(IsSet(mem, bearerEntry),
            "tick 1: bearer must get the bit despite roster/band lvl mismatch (Locate tolerates drift)");

        // Tick 2: bearer unequips (RRHand -> 0) -> leaves _bearers; stale band entry still exists
        mem.U16s[Offsets.RosterBase + Offsets.RRHand] = 0;
        mem.U8s[bearerEntry + NcBandOff] = NcMask;   // bit still set in memory
        mem.Written.Clear();

        choir.Tick(onField: true);

        // _granted was {(300, 23, 65, 60)} from tick 1 (band-read lvl=23).
        // Clear scan reads band entry lvl=23 -> fp4=(300,23,65,60) -> matches _granted -> clear fires.
        // Roster-keyed _granted (lvl=20) would miss -> stuck bit -> this assertion fails.
        Assert.True(IsCleared(mem, bearerEntry),
            "tick 2: unequipped bearer's bit must be cleared; _granted must be band-keyed (lvl=23, not roster lvl=20)");
    }

    // ---- FP-COLLISION bite test ----

    [Fact]
    public void Tick_fp_collision_non_bearer_does_not_get_the_bit()
    {
        // A non-bearer band entry shares the bearer's exact (mhp, lvl, br, fa). Under
        // holder-only / address-direct SET, only the entry returned by
        // ResolveDeployedMainHandAll gets the bit -- the collision entry does NOT.
        // A fingerprint-keyed SET would write to every matching entry, failing the far arm.
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Collision entry: same (mhp=300, lvl=35, br=65, fa=60) but no weapon field set
        // (Locate skips it: wid=0 not in hands=[60]) and no matching roster slot
        long collisionEntry = Band.Entry(20);
        SeedBandEntry(mem, collisionEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 7, gy: 7, writableSupport: true);
        // Do NOT seed weapon at collisionEntry (stays 0 -> Locate skips it)

        var choir = new Choir(meta, kills, mem: mem);
        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerEntry),
            "the resolved bearer entry must get the bit");
        Assert.False(IsSet(mem, collisionEntry),
            "a fingerprint-collision entry must NOT get the bit -- SET is address-direct, not fp-keyed");
    }

    // ---- NEW-A: two deployed bearers each get their own bit; adjacent allies do NOT ----

    [Fact]
    public void Tick_two_deployed_bearers_each_get_own_bit_allies_excluded()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerA = Band.Entry(30);
        SeedBandEntry(mem, bearerA, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60, gx: 3, gy: 3);
        mem.U16s[bearerA + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        SeedRosterSlot(mem, 1, lvl: 30, br: 70, fa: 68, mainHandId: WarlockStaffId);
        long bearerB = Band.Entry(28);
        SeedBandEntry(mem, bearerB, hp: 180, maxHp: 250, lvl: 30, br: 70, fa: 68, gx: 8, gy: 8);
        mem.U16s[bearerB + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Adjacent allies -- alive, writable; under holder-only must NOT get the bit
        long allyA = Band.Entry(26);
        SeedBandEntry(mem, allyA, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55, gx: 4, gy: 3);
        long allyB = Band.Entry(24);
        SeedBandEntry(mem, allyB, hp: 140, maxHp: 140, lvl: 22, br: 52, fa: 57, gx: 9, gy: 8);

        SeedAllyFpAt(mem, 1, 300, 35, 65, 60);
        SeedAllyFpAt(mem, 2, 250, 30, 70, 68);
        SeedAllyFpAt(mem, 3, 150, 20, 50, 55);
        SeedAllyFpAt(mem, 4, 140, 22, 52, 57);

        var choir = new Choir(meta, kills, mem: mem);
        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerA), "bearer A must get the bit");
        Assert.True(IsSet(mem, bearerB), "bearer B must get the bit");
        Assert.False(IsSet(mem, allyA),  "ally adjacent to bearer A must NOT get the bit (holder-only)");
        Assert.False(IsSet(mem, allyB),  "ally adjacent to bearer B must NOT get the bit (holder-only)");
    }

    // ---- NEW-B: two deployed bearers, one unequips -> that bearer's bit cleared, other stays ----

    [Fact]
    public void Tick_unequipped_bearer_bit_cleared_other_bearer_unchanged()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerA = Band.Entry(30);
        SeedBandEntry(mem, bearerA, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60, gx: 2, gy: 2);
        mem.U16s[bearerA + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        SeedRosterSlot(mem, 1, lvl: 30, br: 70, fa: 68, mainHandId: WarlockStaffId);
        long bearerB = Band.Entry(28);
        SeedBandEntry(mem, bearerB, hp: 180, maxHp: 250, lvl: 30, br: 70, fa: 68, gx: 8, gy: 8);
        mem.U16s[bearerB + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        SeedAllyFpAt(mem, 1, 300, 35, 65, 60);
        SeedAllyFpAt(mem, 2, 250, 30, 70, 68);

        var choir = new Choir(meta, kills, mem: mem);

        // Tick 1: both bearers active
        choir.Tick(onField: true);
        Assert.True(IsSet(mem, bearerA), "tick 1: bearer A must have the bit");
        Assert.True(IsSet(mem, bearerB), "tick 1: bearer B must have the bit");

        // Bearer B unequips
        long rb1 = Offsets.RosterBase + 1L * Offsets.RosterStride;
        mem.U16s[rb1 + Offsets.RRHand] = 99;   // no longer WarlockStaffId
        mem.U8s[bearerB + NcBandOff] = NcMask;  // pre-seed so ClearBit can observe
        mem.U8s[bearerA + NcBandOff] = 0;       // reset so SetBit fires and lands in Written
        mem.Written.Clear();

        // Tick 2: only bearer A remains
        choir.Tick(onField: true);

        Assert.True(IsSet(mem, bearerA),      "tick 2: bearer A still holds staff -> bit stays set");
        Assert.True(IsCleared(mem, bearerB),  "tick 2: bearer B unequipped -> bit must be cleared");
    }

    // ---- NEW-C: two staves -> exactly 2 winners (the two bearers) ----

    [Fact]
    public void Tick_two_bearers_produce_exactly_two_winners()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig()
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerA = Band.Entry(30);
        SeedBandEntry(mem, bearerA, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60, gx: 2, gy: 2);
        mem.U16s[bearerA + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        SeedRosterSlot(mem, 1, lvl: 30, br: 70, fa: 68, mainHandId: WarlockStaffId);
        long bearerB = Band.Entry(28);
        SeedBandEntry(mem, bearerB, hp: 180, maxHp: 250, lvl: 30, br: 70, fa: 68, gx: 9, gy: 9);
        mem.U16s[bearerB + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Adjacent allies present but must NOT count as winners
        long allyA = Band.Entry(26);
        SeedBandEntry(mem, allyA, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55, gx: 3, gy: 2);
        long allyB = Band.Entry(24);
        SeedBandEntry(mem, allyB, hp: 140, maxHp: 140, lvl: 22, br: 52, fa: 57, gx: 10, gy: 9);

        SeedAllyFpAt(mem, 1, 300, 35, 65, 60);
        SeedAllyFpAt(mem, 2, 250, 30, 70, 68);
        SeedAllyFpAt(mem, 3, 150, 20, 50, 55);
        SeedAllyFpAt(mem, 4, 140, 22, 52, 57);

        var choir = new Choir(meta, kills, mem: mem);
        choir.Tick(onField: true);

        // Exactly the two bearer entries must have the bit set; allies must not
        int setCount = 0;
        foreach (long entry in new[] { bearerA, bearerB, allyA, allyB })
            if (IsSet(mem, entry)) setCount++;

        Assert.Equal(2, setCount);
        Assert.True(IsSet(mem, bearerA), "bearer A must be one of the two winners");
        Assert.True(IsSet(mem, bearerB), "bearer B must be one of the two winners");
    }
}
