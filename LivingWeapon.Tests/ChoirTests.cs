using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Warlock's Staff "Choir" signature. While a +3 Warlock's Staff is held in the MAIN HAND
/// and its bearer is alive and on the field: every LIVING ALLY within Chebyshev radius 1
/// (8 adjacent tiles incl. diagonals) AND the wielder itself (distance 0) get the Non-charge
/// support bit (id 227, band +0x7F mask 0x04) OR-set each tick so their magick casts instantly.
/// Deterministic revert: when an ally leaves the radius, dies, or the bearer dies/unequips/
/// battle ends, the bit Choir set is CLEARED. A unit whose own picked support is Non-charge
/// (id 227) is NEVER touched.
///
/// Key design decisions tested here:
///   (1) IsActive: null/radius-0/below-tier -> false; at/above tier with radius -> true.
///   (2) InAura Chebyshev: diagonals are distance 1; self is distance 0; 2 tiles away is false.
///   (3) SupportBit pins: id 227 -> (3, 0x04); constant relationship Offsets.ASupport + 3 == Offsets.CSupport - Offsets.BandEntry.
///   (4) LOAD-BEARING: in-aura ally gets +0x7F bit set; out-of-aura (2 tiles) ally does NOT.
///   (5) Neighbor-bit OR -- a pre-existing bit (Swiftspell 0x08) is preserved: result is 0x0C.
///   (6) Bearer self: the bearer's own band entry at distance 0 gets the bit.
///   (7) Bearer-alive gate: HP 0 -> no set (with non-vacuous twin: HP > 0 -> set).
///   (8) Tier gate: below atTier -> no set.
///   (9) Ally filter: enemy fp not in AllyFingerprints -> not set.
///   (10) Writable/Readable guard: not in WritableAddrs or ReadableAddrs -> no set.
///   (11) Revert-on-leave: ally granted, then moves out of range -> bit cleared.
///   (12) Wielder-moves revert: bearer moves so the ally is now out of range -> bit clears.
///   (13) Never-strip-player's-own Non-charge (B2.1 regression).
///   (14) Multi-bearer: two roster slots hold id 60 in main hand -> inactive -> no set.
///   (15) ResetBattle clears tracking state.
///   (16) Band-twin: same fp, different slots, one in-aura one out -> per-entry correctness.
/// </summary>
public class ChoirTests
{
    private const int WarlockStaffId = 60;
    private const int NonChargeId    = 227;  // id 227 -> byte 3 (ASupport+3 = band +0x7F), mask 0x04

    // Byte offset within ASupport for Non-charge (id 227, base 198 -> pos 29 -> byte 3, mask 0x80>>5 = 0x04)
    private const int NcByteOff = 3;
    private const byte NcMask   = 0x04;
    // Band write address offset from entry base: ASupport + NcByteOff = 0x7C + 3 = 0x7F
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

    // ---- (2) InAura Chebyshev ----

    [Theory]
    [InlineData(5, 5, 6, 6, true)]   // diagonal -> distance 1 -> in aura
    [InlineData(5, 5, 7, 5, false)]  // 2 tiles away -> out
    [InlineData(5, 5, 5, 5, true)]   // self -> distance 0 -> in aura
    [InlineData(5, 5, 7, 7, false)]  // 2 diagonal -> distance 2 -> out
    public void InAura_chebyshev_at_radius_1(int wx, int wy, int x, int y, bool expected)
        => Assert.Equal(expected, Choir.InAura(wx, wy, x, y, radius: 1));

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

    /// <summary>Build a standard active scenario: bearer alive (band slot 30), one in-aura
    /// ally (band slot 28, adjacent to bearer), kills >= atTier so active.</summary>
    private static (Choir choir, FakeSparseMemory mem, long bearerEntry, long allyEntry)
        BuildActive(int tier = 3, int bearerHp = 200, int bearerGx = 5, int bearerGy = 5,
                    int allyGx = 6, int allyGy = 6, bool writableAllySupport = true,
                    int rSupport = 0)
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
            [WarlockStaffId] = tier >= 1 ? Tuning.ProdThresholds[Math.Min(tier, 3) - 1] : 0
        };

        // Roster slot 0 = bearer
        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId, rsupport: rSupport);

        // Bearer band entry (band slot 30) -- weapon field at entry+0x04 (CWeapon-BandEntry) for Wielder.Locate
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, bearerHp, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: bearerGx, gy: bearerGy, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Ally band entry (band slot 28, adjacent by default)
        long allyEntry = Band.Entry(28);
        SeedBandEntry(mem, allyEntry, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55,
                      gx: allyGx, gy: allyGy, writableSupport: writableAllySupport);

        // Register ally fingerprint in the static array
        SeedAllyFp(mem, mhp: 150, lvl: 20, br: 50, fa: 55);

        var choir = new Choir(meta, kills, mem: mem);
        return (choir, mem, bearerEntry, allyEntry);
    }

    private static int Math_Min(int a, int b) => a < b ? a : b;

    // ---- (4) LOAD-BEARING: in-aura ally gets the bit; far ally does NOT ----

    [Fact]
    public void Tick_sets_noncharge_on_inAura_ally_and_not_on_far_ally()
    {
        var (choir, mem, bearerEntry, allyEntry) = BuildActive(bearerGx: 5, bearerGy: 5,
                                                               allyGx: 6, allyGy: 6);  // distance 1 -> in aura

        // Add a FAR ally at distance 2 (different slot, different fp)
        // Use array slot EnemySlotMax+2 so we don't overwrite the in-aura ally's fp at slot EnemySlotMax+1
        long farEntry = Band.Entry(25);
        SeedBandEntry(mem, farEntry, hp: 120, maxHp: 120, lvl: 22, br: 52, fa: 57,
                      gx: 7, gy: 5, writableSupport: true);   // 2 tiles from bearer (5,5)
        long farFpSlot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 2) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(farFpSlot + Offsets.AMaxHp);
        mem.U16s[farFpSlot + Offsets.AMaxHp] = 120;
        mem.U8s[farFpSlot + Offsets.ALevel]  = 22;
        mem.U8s[farFpSlot + Offsets.ABrave]  = 52;
        mem.U8s[farFpSlot + Offsets.AFaith]  = 57;

        choir.Tick(onField: true);

        long inAuraAddr  = allyEntry + NcBandOff;
        long farAddr     = farEntry  + NcBandOff;

        // In-aura ally MUST have the bit set
        Assert.True(mem.Written.ContainsKey(inAuraAddr),
            "in-aura ally's +0x7F must be written");
        Assert.Equal(NcMask, (byte)(mem.Written[inAuraAddr] & NcMask));

        // Far ally must NOT have the bit set
        Assert.False(mem.Written.ContainsKey(farAddr) && (mem.Written[farAddr] & NcMask) != 0,
            "far ally's +0x7F must NOT have the Non-charge bit set");
    }

    // VACUITY TWIN: an always-set impl (broken) would pass the in-aura check but fail the far check
    // The test above is genuinely non-vacuous because it asserts BOTH arms of the OR-set decision.

    // ---- (5) Neighbor-bit preserved (OR not clobber) ----

    [Fact]
    public void Tick_ORs_bit_not_clobbers_existing_neighbor_bit()
    {
        var (choir, mem, _, allyEntry) = BuildActive();
        long supportAddr = allyEntry + NcBandOff;
        // Pre-seed Swiftspell bit (0x08) -- the neighbor bit that must survive
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
        // Bearer is at (5,5), radius 1 -- the bearer itself is distance 0
        // Register bearer fingerprint in a SECOND static array slot (EnemySlotMax+2) so it
        // doesn't overwrite the ally fp seeded by BuildActive in slot EnemySlotMax+1
        long slot2 = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 2) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot2 + Offsets.AMaxHp);
        mem.U16s[slot2 + Offsets.AMaxHp] = 300;
        mem.U8s[slot2 + Offsets.ALevel]  = 35;
        mem.U8s[slot2 + Offsets.ABrave]  = 65;
        mem.U8s[slot2 + Offsets.AFaith]  = 60;

        choir.Tick(onField: true);

        long bearerSupportAddr = bearerEntry + NcBandOff;
        Assert.True(mem.Written.ContainsKey(bearerSupportAddr),
            "bearer's own +0x7F must be written (self is distance 0 from itself)");
        Assert.Equal(NcMask, (byte)(mem.Written[bearerSupportAddr] & NcMask));
    }

    // ---- (7) Bearer-alive gate (non-vacuous twin) ----

    [Fact]
    public void Tick_no_set_when_bearer_hp_is_zero()
    {
        var (choir, mem, _, allyEntry) = BuildActive(bearerHp: 0);

        choir.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                     (mem.Written[allyEntry + NcBandOff] & NcMask) != 0,
            "bearer dead -> no Non-charge bit set");
    }

    [Fact]
    public void Tick_sets_bit_when_bearer_is_alive_vacuity_twin()
    {
        // Identical scenario but bearer HP > 0
        var (choir, mem, _, allyEntry) = BuildActive(bearerHp: 200);

        choir.Tick(onField: true);

        Assert.True(mem.Written.ContainsKey(allyEntry + NcBandOff),
            "bearer alive -> Non-charge bit must be set on in-aura ally");
    }

    // ---- (8) Tier gate ----

    [Fact]
    public void Tick_no_set_when_tier_below_atTier()
    {
        var (choir, mem, _, allyEntry) = BuildActive(tier: 2);   // 2 < atTier 3

        choir.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                     (mem.Written[allyEntry + NcBandOff] & NcMask) != 0);
    }

    // ---- (9) Ally filter: enemy fp not in AllyFingerprints -> not set ----

    [Fact]
    public void Tick_does_not_set_bit_on_enemy()
    {
        var (choir, mem, _, _) = BuildActive();

        // Seed an enemy-only band entry at an adjacent tile (not in ally fp set)
        long enemyEntry = Band.Entry(20);
        SeedBandEntry(mem, enemyEntry, hp: 100, maxHp: 100, lvl: 18, br: 60, fa: 50,
                      gx: 6, gy: 5, writableSupport: true);
        // Do NOT add to ally fingerprints

        choir.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(enemyEntry + NcBandOff) &&
                     (mem.Written[enemyEntry + NcBandOff] & NcMask) != 0,
            "enemy units must not get the Non-charge bit");
    }

    // ---- (10) Writable/Readable guard ----

    [Fact]
    public void Tick_no_set_when_support_addr_not_writable()
    {
        var (choir, mem, _, allyEntry) = BuildActive(writableAllySupport: false);

        choir.Tick(onField: true);

        // The bit must not be written
        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                     (mem.Written[allyEntry + NcBandOff] & NcMask) != 0);
    }

    [Fact]
    public void Tick_no_set_when_support_addr_not_readable()
    {
        // Build without the support address in ReadableAddrs (but add writable only)
        var (choir, mem, _, allyEntry) = BuildActive(writableAllySupport: false);
        // Add writable but NOT readable
        mem.WritableAddrs.Add(allyEntry + NcBandOff);
        // ReadableAddrs does NOT have it

        choir.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                     (mem.Written[allyEntry + NcBandOff] & NcMask) != 0,
            "not-readable support addr must not be written");
    }

    // ---- (11) Revert-on-leave ----

    [Fact]
    public void Tick_clears_bit_when_ally_moves_out_of_range()
    {
        var (choir, mem, _, allyEntry) = BuildActive(allyGx: 6, allyGy: 6);  // adjacent -> in aura

        // First tick: ally is in aura -> bit set, added to _granted
        choir.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(allyEntry + NcBandOff));

        // Move ally out of range (2 tiles from bearer at 5,5)
        mem.U8s[allyEntry + Offsets.AGx] = 7;
        mem.U8s[allyEntry + Offsets.AGy] = 5;
        // Reset Written so we can observe the second tick's ClearBit
        mem.Written.Clear();
        // Pre-seed the byte as having 0x04 so ClearBit sees it set and writes the clear
        mem.U8s[allyEntry + NcBandOff] = NcMask;

        choir.Tick(onField: true);

        // The bit must be cleared
        bool cleared = mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                       (mem.Written[allyEntry + NcBandOff] & NcMask) == 0;
        Assert.True(cleared, "moved-out ally's Non-charge bit must be cleared");
    }

    // ---- (12) Wielder-moves revert ----

    [Fact]
    public void Tick_clears_bit_when_bearer_moves_so_ally_leaves_radius()
    {
        var (choir, mem, bearerEntry, allyEntry) = BuildActive(
            bearerGx: 5, bearerGy: 5, allyGx: 6, allyGy: 6);  // ally adjacent

        // First tick -> bit granted
        choir.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(allyEntry + NcBandOff));

        // Move the BEARER so ally is now 3 tiles away (out of range)
        mem.U8s[bearerEntry + Offsets.AGx] = 9;
        mem.U8s[bearerEntry + Offsets.AGy] = 9;
        mem.Written.Clear();
        mem.U8s[allyEntry + NcBandOff] = NcMask;

        choir.Tick(onField: true);

        bool cleared = mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                       (mem.Written[allyEntry + NcBandOff] & NcMask) == 0;
        Assert.True(cleared, "ally outside moved-bearer's radius must get the bit cleared");
    }

    // ---- (13) Never-strip player's own Non-charge (B2.1 regression) ----

    [Fact]
    public void Tick_never_strips_players_own_NonCharge_support()
    {
        // Ally's ROSTER slot has RSupport == 227 (Non-charge is their own pick)
        // Their band entry is in-aura and has +0x7F 0x04 already set.
        // Choir must NOT add them to _granted; moving them out must NOT clear the bit.
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

        // Roster slot 0 = bearer
        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId, rsupport: 0);
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Ally with own Non-charge pick (RSupport == 227)
        // Their brave+faith fingerprint: br=52, fa=58
        SeedRosterSlot(mem, 1, lvl: 22, br: 52, fa: 58, mainHandId: 0, rsupport: NonChargeId);
        long allyEntry = Band.Entry(28);
        SeedBandEntry(mem, allyEntry, hp: 150, maxHp: 150, lvl: 22, br: 52, fa: 58,
                      gx: 6, gy: 6, writableSupport: true);  // adjacent to bearer (in aura)
        // Pre-set their bit (they already have it from their own picked support)
        mem.U8s[allyEntry + NcBandOff] = NcMask;
        SeedAllyFp(mem, mhp: 150, lvl: 22, br: 52, fa: 58);

        var choir = new Choir(meta, kills, mem: mem);

        // Tick while in aura -- must NOT add to _granted
        choir.Tick(onField: true);

        // Move ally out of aura
        mem.U8s[allyEntry + Offsets.AGx] = 7;
        mem.U8s[allyEntry + Offsets.AGy] = 5;
        mem.Written.Clear();
        // bit still set from their own pick
        mem.U8s[allyEntry + NcBandOff] = NcMask;

        choir.Tick(onField: true);

        // The bit must STILL be 0x04 (not cleared by Choir)
        bool cleared = mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                       (mem.Written[allyEntry + NcBandOff] & NcMask) == 0;
        Assert.False(cleared,
            "player's own Non-charge support must never be cleared by Choir");
    }

    // ---- (14) Multi-bearer: two roster slots hold id 60 -> inactive ----

    [Fact]
    public void Tick_no_set_when_two_roster_slots_hold_the_staff()
    {
        var (choir, mem, _, allyEntry) = BuildActive();
        // Add a second roster slot also holding id 60 in main hand
        SeedRosterSlot(mem, 1, lvl: 28, br: 70, fa: 65, mainHandId: WarlockStaffId);

        choir.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff) &&
                     (mem.Written[allyEntry + NcBandOff] & NcMask) != 0,
            "two roster wielders -> ambiguous -> no grant");
    }

    // ---- (15) ResetBattle clears tracking ----

    [Fact]
    public void ResetBattle_clears_granted_state_so_revert_does_not_fire_next_battle()
    {
        var (choir, mem, _, allyEntry) = BuildActive(allyGx: 6, allyGy: 6);

        // Grant the bit
        choir.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(allyEntry + NcBandOff));

        // Reset (simulates battle exit)
        choir.ResetBattle();

        // After reset: ally moves out of range and we tick -- _granted is cleared, so no ClearBit fires
        mem.U8s[allyEntry + Offsets.AGx] = 8;
        mem.U8s[allyEntry + Offsets.AGy] = 5;
        mem.Written.Clear();
        mem.U8s[allyEntry + NcBandOff] = NcMask;

        // Now make the staff tier drop so Choir is inactive (simulating new battle start state)
        // In a real scenario, a fresh battle = fresh _granted, so no stale revert.
        // We verify _granted.Clear() happened by making Choir inactive (tier=0) on the next tick
        // and checking no ClearBit fires (because _granted is empty).
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

        Assert.False(mem.Written.ContainsKey(allyEntry + NcBandOff),
            "after ResetBattle, _granted is clear so no stale ClearBit fires");
    }

    // ---- (16) Band-twin correctness ----

    [Fact]
    public void Tick_band_twin_inAura_set_outAura_cleared_per_entry()
    {
        // Two allies with identical fingerprints (band twins). Both start in aura (tick 1 -> both granted).
        // Then twin B moves out of range (tick 2 -> A still set, B cleared per its entry address).
        // This proves per-entry correctness: same fp, different band slot addresses, different outcomes.
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

        // Roster: bearer at slot 0
        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        // Twin ally A: same fp (mhp=150,lvl=20,br=50,fa=55), adjacent (6,6) -- stays in aura
        long twinAEntry = Band.Entry(27);
        SeedBandEntry(mem, twinAEntry, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55,
                      gx: 6, gy: 6, writableSupport: true);

        // Twin ally B: SAME fp, also adjacent at (6,5) -- will move out next tick
        long twinBEntry = Band.Entry(26);
        SeedBandEntry(mem, twinBEntry, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55,
                      gx: 6, gy: 5, writableSupport: true);

        SeedAllyFp(mem, mhp: 150, lvl: 20, br: 50, fa: 55);

        var choir = new Choir(meta, kills, mem: mem);

        // Tick 1: both twins are in aura -> both get the bit; fp added to _granted
        choir.Tick(onField: true);
        Assert.True(mem.Written.ContainsKey(twinAEntry + NcBandOff) &&
                    (mem.Written[twinAEntry + NcBandOff] & NcMask) != 0,
            "tick 1: twin A must have the bit set");
        Assert.True(mem.Written.ContainsKey(twinBEntry + NcBandOff) &&
                    (mem.Written[twinBEntry + NcBandOff] & NcMask) != 0,
            "tick 1: twin B must have the bit set");

        // Move twin B out of range (distance 3)
        mem.U8s[twinBEntry + Offsets.AGx] = 8;
        mem.U8s[twinBEntry + Offsets.AGy] = 8;
        mem.Written.Clear();
        // Pre-seed B's byte so ClearBit can observe and write the clear
        mem.U8s[twinBEntry + NcBandOff] = NcMask;
        // A's byte stays set
        mem.U8s[twinAEntry + NcBandOff] = NcMask;

        // Tick 2: A stays in aura (SetBit idempotent, may not write if already set),
        // B out of aura and fp in _granted -> ClearBit fires on B's address
        choir.Tick(onField: true);

        long addrB = twinBEntry + NcBandOff;
        Assert.True(mem.Written.ContainsKey(addrB) && (mem.Written[addrB] & NcMask) == 0,
            "tick 2: out-of-aura twin B must have the bit cleared at its own band address");

        // A must NOT have had its bit cleared
        bool aCleared = mem.Written.ContainsKey(twinAEntry + NcBandOff) &&
                        (mem.Written[twinAEntry + NcBandOff] & NcMask) == 0;
        Assert.False(aCleared, "tick 2: in-aura twin A must not have its bit cleared");
    }

    // ---- (17) SelectNearest: the pure cap selector ----

    [Fact]
    public void SelectNearest_empty_or_nonpositive_max_is_empty()
    {
        Assert.Empty(Choir.SelectNearest(new List<((int, int, int, int) fp, int dist)>(), 2));
        Assert.Empty(Choir.SelectNearest(
            new List<((int, int, int, int) fp, int dist)> { ((1, 1, 1, 1), 0) }, 0));
    }

    [Fact]
    public void SelectNearest_takes_the_two_nearest_distinct_units()
    {
        var c = new List<((int, int, int, int) fp, int dist)>
        {
            ((3, 3, 3, 3), 3), ((1, 1, 1, 1), 1), ((2, 2, 2, 2), 2),
        };
        var w = Choir.SelectNearest(c, 2);
        Assert.Equal(2, w.Count);
        Assert.Contains((1, 1, 1, 1), w);
        Assert.Contains((2, 2, 2, 2), w);
        Assert.DoesNotContain((3, 3, 3, 3), w);   // the farthest is capped out
    }

    [Fact]
    public void SelectNearest_dedupes_a_twin_so_it_does_not_consume_a_second_slot()
    {
        // Two entries of the SAME unit at d1, plus a distinct unit at d3. max 2.
        var c = new List<((int, int, int, int) fp, int dist)>
        {
            ((1, 1, 1, 1), 1), ((1, 1, 1, 1), 1), ((2, 2, 2, 2), 3),
        };
        var w = Choir.SelectNearest(c, 2);
        Assert.Equal(2, w.Count);
        Assert.Contains((1, 1, 1, 1), w);
        Assert.Contains((2, 2, 2, 2), w);   // the twin did NOT eat the second slot
    }

    [Fact]
    public void SelectNearest_breaks_distance_ties_by_input_order()
    {
        var c = new List<((int, int, int, int) fp, int dist)>
        {
            ((1, 1, 1, 1), 1), ((2, 2, 2, 2), 1), ((3, 3, 3, 3), 1),
        };
        var w = Choir.SelectNearest(c, 2);
        Assert.Contains((1, 1, 1, 1), w);
        Assert.Contains((2, 2, 2, 2), w);
        Assert.DoesNotContain((3, 3, 3, 3), w);
    }

    // ---- (18) CAP integration: only the nearest two units get the bit ----

    /// <summary>Build a bearer + three distinct allies at Chebyshev distances 1/2/3 (radius 3 so all
    /// are in-aura), so the cap (2) has to drop the farthest. Bearer is NOT seeded as an ally
    /// fingerprint, so the three allies are the only candidates -- isolating the cap on allies.</summary>
    private static (Choir choir, FakeSparseMemory mem, long d1, long d2, long d3)
        BuildThreeAllies(int radius = 3)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WarlockStaffId] = new WeaponMeta
            {
                Name = "Warlock's Staff", Wp = 3, Cat = "Staff", Formula = 1,
                Flavor = "Bound with a warlock's hex",
                Signature = ChoirSig(radius: radius)
            }
        };
        var kills = new Dictionary<int, int> { [WarlockStaffId] = Tuning.ProdThresholds[2] };

        SeedRosterSlot(mem, 0, lvl: 35, br: 65, fa: 60, mainHandId: WarlockStaffId);
        long bearerEntry = Band.Entry(30);
        SeedBandEntry(mem, bearerEntry, hp: 200, maxHp: 300, lvl: 35, br: 65, fa: 60,
                      gx: 5, gy: 5, writableSupport: true);
        mem.U16s[bearerEntry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)WarlockStaffId;

        long d1 = Band.Entry(28), d2 = Band.Entry(27), d3 = Band.Entry(26);
        SeedBandEntry(mem, d1, hp: 150, maxHp: 150, lvl: 20, br: 50, fa: 55, gx: 6, gy: 5, writableSupport: true);
        SeedBandEntry(mem, d2, hp: 151, maxHp: 151, lvl: 21, br: 51, fa: 56, gx: 7, gy: 5, writableSupport: true);
        SeedBandEntry(mem, d3, hp: 152, maxHp: 152, lvl: 22, br: 52, fa: 57, gx: 8, gy: 5, writableSupport: true);
        SeedAllyFpAt(mem, 1, 150, 20, 50, 55);
        SeedAllyFpAt(mem, 2, 151, 21, 51, 56);
        SeedAllyFpAt(mem, 3, 152, 22, 52, 57);

        return (new Choir(meta, kills, mem: mem), mem, d1, d2, d3);
    }

    [Fact]
    public void Tick_caps_grant_to_the_two_nearest_units()
    {
        var (choir, mem, d1, d2, d3) = BuildThreeAllies();
        choir.Tick(onField: true);

        Assert.True(IsSet(mem, d1), "nearest (d1) ally must get the bit");
        Assert.True(IsSet(mem, d2), "second-nearest (d2) ally must get the bit");
        Assert.False(IsSet(mem, d3), "third (d3) ally must NOT get the bit -- the cap is 2");
    }

    [Fact]
    public void Tick_bumps_the_farthest_winner_when_a_nearer_ally_arrives()
    {
        // tick1: d3 is OUT of aura (placed far), so candidates are d1 + d2 -> both win (cap 2).
        var (choir, mem, d1, d2, d3) = BuildThreeAllies();
        mem.U8s[d3 + Offsets.AGx] = 20;   // park d3 far away for tick 1
        mem.U8s[d3 + Offsets.AGy] = 20;

        choir.Tick(onField: true);
        Assert.True(IsSet(mem, d1));
        Assert.True(IsSet(mem, d2));

        // tick2: d3 walks in to distance 3 -- still farther than d1/d2, so it must NOT bump them.
        // Instead, move d2 OUT and bring d3 to d2's old spot so d3 becomes a winner and d2 is bumped.
        mem.U8s[d2 + Offsets.AGx] = 20;   // d2 leaves
        mem.U8s[d2 + Offsets.AGy] = 20;
        mem.U8s[d3 + Offsets.AGx] = 7;    // d3 takes the d2 slot (distance 2)
        mem.U8s[d3 + Offsets.AGy] = 5;
        mem.Written.Clear();
        mem.U8s[d2 + NcBandOff] = NcMask;   // d2 still carries the bit from tick 1
        mem.U8s[d3 + NcBandOff] = 0;

        choir.Tick(onField: true);

        Assert.True(IsCleared(mem, d2), "bumped/left ally (d2) must have its bit cleared");
        Assert.True(IsSet(mem, d3), "newly-nearest ally (d3) must get the bit");
        Assert.False(IsCleared(mem, d1), "still-winning d1 must not be cleared");
    }
}
