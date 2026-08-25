using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-251: the WeaponPalette runtime -- the per-turn weapon-sprite palette repaint (see
/// LivingWeapon/Display/WeaponPalette.cs's class doc for the mechanism, and
/// WeaponPalette.Policy.cs for the pure seven-row decision table this suite's first section
/// exercises directly). Module-level coverage (this file's remainder) drives WeaponPalette.Tick
/// against FakeSparseMemory band seats, mirroring BulwarkTests.cs's shape: a builder that seeds a
/// FlagOwner-satisfying band entry, then per-test Tick sequences asserting on
/// FakeSparseMemory.WrittenBytes/WriteOrder (never on a comment citation -- the LW-257
/// vacuous-test lesson).
/// </summary>
public class WeaponPaletteTests
{
    // ================= 1. WeaponPalettePolicy.Decide -- pure, no memory (TDD item 1) =================

    private const int ReassertTicks = WeaponPalette.ReassertTicks;

    [Fact]
    public void Decide_NothingPaintedNothingDesired_Nothing()
        => Assert.Equal(WeaponPaletteAction.Nothing,
            WeaponPalettePolicy.Decide(paintedPal: -1, paintedWeapon: -1, paintedGlow: 0,
                desiredPal: -1, desiredWeapon: -1, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_PaintedNothingDesired_Restore()
        => Assert.Equal(WeaponPaletteAction.Restore,
            WeaponPalettePolicy.Decide(paintedPal: 3, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: -1, desiredWeapon: -1, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_NothingPaintedSomethingDesired_Paint()
        => Assert.Equal(WeaponPaletteAction.Paint,
            WeaponPalettePolicy.Decide(paintedPal: -1, paintedWeapon: -1, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 9, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_SamePaintedAndDesired_BelowReassert_Nothing()
        => Assert.Equal(WeaponPaletteAction.Nothing,
            WeaponPalettePolicy.Decide(paintedPal: 4, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 9, desiredGlow: 0,
                ticksUnchanged: ReassertTicks - 1, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_SamePaintedAndDesired_AtReassertThreshold_Paint()
        => Assert.Equal(WeaponPaletteAction.Paint,
            WeaponPalettePolicy.Decide(paintedPal: 4, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 9, desiredGlow: 0,
                ticksUnchanged: ReassertTicks, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_DifferentPalette_RestoreThenPaint()
        => Assert.Equal(WeaponPaletteAction.RestoreThenPaint,
            WeaponPalettePolicy.Decide(paintedPal: 3, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 22, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));

    [Fact]
    public void Decide_SamePaletteDifferentWeapon_PaintOnly_NoRestore()
        => Assert.Equal(WeaponPaletteAction.Paint,
            WeaponPalettePolicy.Decide(paintedPal: 4, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 22, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));

    // ---- LW-295 TDD item 1: the seventh row -- same (pal, weapon), glow changed -> Paint,
    //      regardless of ticksUnchanged (a tier bump/drop repaints right away, not on the
    //      reassert cadence). Both directions: a glow INCREASE (the only one that happens in
    //      play) and a glow DROP (the row itself is direction-agnostic). ----

    [Fact]
    public void Decide_SamePalWeapon_GlowRose_Paint()
    {
        Assert.Equal(WeaponPaletteAction.Paint,
            WeaponPalettePolicy.Decide(paintedPal: 4, paintedWeapon: 9, paintedGlow: 0,
                desiredPal: 4, desiredWeapon: 9, desiredGlow: 1,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));
    }

    [Fact]
    public void Decide_SamePalWeapon_GlowDropped_Paint()
    {
        Assert.Equal(WeaponPaletteAction.Paint,
            WeaponPalettePolicy.Decide(paintedPal: 4, paintedWeapon: 9, paintedGlow: 2,
                desiredPal: 4, desiredWeapon: 9, desiredGlow: 0,
                ticksUnchanged: 0, reassertTicks: ReassertTicks));
    }

    // ================= 1b. WeaponPalettePolicy.Brighten/BrightenAll -- pure (TDD items 3-5) ======

    // ---- LW-295 TDD item 3 (THE LOAD-BEARING TEST): the exact ladder from the committed
    //      look-test probe (tools/probes/lw305_bench_paint.py, commit 2dbab25) for its own sample
    //      code 0x14B8 (r=24, g=5, b=5) at all three shipped tier factors. The f=0.75 row is the
    //      one that pins the rounding mode: g and b both land on the 24.5 half-case, and only
    //      MidpointRounding.ToEven (Math.Round's default, matching Python's round()) reproduces
    //      0x631D -- AwayFromZero would produce 0x673D instead. ----

    [Fact]
    public void Brighten_MatchesProbeLadder()
    {
        const int code = 0x14B8;
        Assert.Equal(0x319A, WeaponPalettePolicy.Brighten(code, 0.25));
        Assert.Equal(0x4A5C, WeaponPalettePolicy.Brighten(code, 0.50));
        Assert.Equal(0x631D, WeaponPalettePolicy.Brighten(code, 0.75));
    }

    // ---- LW-295 TDD item 4: every channel clamps at 31 (never overflows into the next channel's
    //      bits or into bit 15), and bit 15 is ALWAYS stripped from the result -- PaintBanks is
    //      the only legitimate source of that bit (the LW-308 rule). ----

    [Fact]
    public void Brighten_Clamps31_AndStripsBit15()
    {
        Assert.Equal(0x7FFF, WeaponPalettePolicy.Brighten(0x7FFF, 0.75));   // already maxed: unchanged
        Assert.Equal(0x7FFF, WeaponPalettePolicy.Brighten(0xFFFF, 0.75));   // bit 15 set on input: stripped
        Assert.Equal(0x7FFF, WeaponPalettePolicy.Brighten(0x0000, 1.0));    // black, full factor -> white
    }

    // ---- LW-295 TDD item 5: the f == 0.0 (tier 0, no glow) fast path returns the INPUT array
    //      reference itself, not a freshly built copy -- no allocation on the common
    //      untiered/plain-wielder path. ----

    [Fact]
    public void BrightenAll_FactorZero_ReturnsSameReference()
    {
        var codes = Codes();
        Assert.Same(codes, WeaponPalettePolicy.BrightenAll(codes, 0.0));
    }

    // ================= 2. Module-level: WeaponPalette.Tick against FakeSparseMemory =================

    private const long BankA = Offsets.WeaponPaletteBankA;
    private const long BankB = Offsets.WeaponPaletteBankB;
    private const int Stride = Offsets.WeaponPaletteStride;
    private static readonly long[] Banks = { BankA, BankB };

    private static long Tgt(long bank, int pal) => bank + pal * Stride;

    /// <summary>15 distinct, deliberately non-BGR555-lookalike authored codes for entries 1..15
    /// (all well under 0x8000, exactly like a real q5-quantised code never sets bit 15).</summary>
    private static int[] Codes() => new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    /// <summary>A second, distinguishable 15-code set for a SECOND authored weapon in the same
    /// test (so a paint can be told apart from Codes()'s own weapon by value alone).</summary>
    private static int[] AltCodes() => new[] { 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115 };

    /// <summary>A full 16-entry raw bank image (entry 0 included, distinguishable per-index so a
    /// test can prove it was left alone). <paramref name="bit15At"/> sets bit 15 on the named
    /// entries only, so the carry can be observed against a KNOWN subset.</summary>
    private static ushort[] SeedBank(params int[] bit15At)
    {
        var e = new ushort[16];
        for (int i = 0; i < 16; i++) e[i] = (ushort)(0x0100 + i);
        foreach (int i in bit15At) e[i] |= 0x8000;
        return e;
    }

    private static void StageBank(FakeSparseMemory mem, long bank, int pal, ushort[] entries16)
    {
        var bytes = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            bytes[i * 2] = (byte)(entries16[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(entries16[i] >> 8);
        }
        mem.TerrainBlocks[Tgt(bank, pal)] = bytes;
    }

    /// <summary>Reads a bank's current 32-byte image back through TryReadBytes -- the same lane
    /// PaintBanks/RestoreBanks themselves read/write through, so this reflects every write
    /// exactly as production code would see it (unlike a raw U16()/U8s read-back, which does NOT
    /// observe a WriteBytes call -- FakeSparseMemory's own documented channel gap).</summary>
    private static ushort[] ReadBank(FakeSparseMemory mem, long bank, int pal)
    {
        Assert.True(mem.TryReadBytes(Tgt(bank, pal), 32, out var raw));
        var e = new ushort[16];
        for (int i = 0; i < 16; i++) e[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
        return e;
    }

    private const int Slot = Offsets.SlotsBack;   // band slot 20, mirrors FlagOwnerResolveTests' SlotA
    private const int SlotOther = Offsets.SlotsBack + 1;

    /// <summary>Seeds a band entry as the FlagOwner (real position, ATurnFlag == 1) wielding
    /// <paramref name="weaponId"/>.</summary>
    private static long SeatOwner(FakeSparseMemory mem, int slot, int weaponId, int gx = 5, int gy = 5)
    {
        long entry = Band.Entry(slot);
        MemSeats.SeatBand(mem, slot, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: gx, gy: gy);
        mem.U8s[entry + Offsets.ATurnFlag] = 1;
        return entry;
    }

    /// <summary>LW-295: seeds a roster row wielding <paramref name="weaponId"/> in its main hand,
    /// at the EXACT (level,brave,faith) fingerprint <see cref="SeatOwner"/> stages on the band
    /// side (lvl 30, br 50, fa 50) -- so DesiredGlow's roster read finds this row as a match for
    /// whatever band owner SeatOwner seated. RNameId stays 0 (default): DesiredGlow's gate is
    /// judged on matchCount, never the roster nameId.</summary>
    private static void SeatMatchingRoster(FakeSparseMemory mem, int rosterSlot, int weaponId)
        => MemSeats.SeatRoster(mem, rosterSlot, lvl: 30, br: 50, fa: 50, rh: weaponId);

    private static Dictionary<int, WeaponMeta> Meta(int weaponId, int palette, int[]? colors = null)
        => new()
        {
            [weaponId] = new WeaponMeta
            {
                Name = "Test Weapon", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "f",
                Palette = palette, Colors = colors ?? Codes(),
            }
        };

    /// <summary>Both banks staged at <paramref name="pal"/> with <paramref name="seed"/>, an
    /// owner seated wielding <paramref name="weaponId"/>, and a WeaponPalette wired to a
    /// single-weapon meta map authoring that weapon at that palette.</summary>
    private static (FakeSparseMemory mem, WeaponPalette wp) Build(int weaponId, int pal, ushort[] seed)
    {
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId);
        foreach (long bank in Banks) StageBank(mem, bank, pal, seed);
        var wp = new WeaponPalette(Meta(weaponId, pal), mem, new Dictionary<int, int>());
        return (mem, wp);
    }

    // ---- TDD item 2: paint writes both banks at +2, 30 bytes; entry 0 (offset 0..1) untouched ----

    [Fact]
    public void Paint_WritesBothBanksAtPlus2_30Bytes_Entry0Untouched()
    {
        var seed = SeedBank();
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);

        wp.Tick(true);

        foreach (long bank in Banks)
        {
            long tgt = Tgt(bank, 4);
            Assert.Contains(mem.WrittenBytes, w => w.addr == tgt + 2 && w.bytes.Length == 30);

            var after = ReadBank(mem, bank, 4);
            Assert.Equal(seed[0], after[0]);   // entry 0 (transparency) never written
            for (int i = 1; i <= 15; i++)
                Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);   // low 15 bits == the authored code
        }
    }

    // ---- TDD item 3: bit-15 carry (the load-bearing non-vacuous test) ----

    [Fact]
    public void Paint_CarriesBit15FromCurrentBytes_NeverInventsIt()
    {
        // Entries 2, 6, and 15 start with bit 15 set; the rest do not.
        var seed = SeedBank(2, 6, 15);
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);

        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
            {
                bool expectBit15 = i == 2 || i == 6 || i == 15;
                Assert.Equal(expectBit15, (after[i] & 0x8000) != 0);
                Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);
            }
        }
    }

    // ---- TDD item 4: restore writes the snapshot verbatim, including its own bit 15s ----

    [Fact]
    public void Restore_WritesThePreFirstPaintSnapshotVerbatim()
    {
        var seed = SeedBank(3, 9);   // entries 3 and 9 start with bit 15 set
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);

        wp.Tick(true);   // paint: captures the snapshot from `seed`

        // The owner's weapon changes to one with no authored colours -> Restore.
        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 12345;
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
                Assert.Equal(seed[i], after[i]);   // verbatim, bit 15 included
        }
    }

    // ---- TDD item 5: snapshot immutability (the starved-bracket correctness property) ----

    [Fact]
    public void Restore_UsesTheOriginalPreFirstPaintBytes_EvenIfMemoryWasMutatedSincePaint()
    {
        var seed = SeedBank(3, 9);
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);

        wp.Tick(true);   // paint: snapshot captured from `seed`

        // Something else (a starved bracket, a probe, a battle load) mutates the raw bank bytes
        // underneath the runtime, WITHOUT going through WeaponPalette's own write path.
        var mutated = SeedBank(1, 4, 8);
        foreach (long bank in Banks) StageBank(mem, bank, 4, mutated);

        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 12345;   // unauthored -> Restore
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
                Assert.Equal(seed[i], after[i]);   // the ORIGINAL snapshot, not `mutated`
        }
    }

    // ---- TDD item 6: an unreadable bank is skipped entirely (no write, no snapshot) ----

    [Fact]
    public void Paint_UnreadableBank_UntouchedAndNoSnapshot_OtherBankStillPaints()
    {
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, 9);
        StageBank(mem, BankA, 4, SeedBank());   // BankB deliberately left unstaged -- TryReadBytes fails there
        var wp = new WeaponPalette(Meta(9, 4), mem, new Dictionary<int, int>());

        wp.Tick(true);

        long tgtA = Tgt(BankA, 4), tgtB = Tgt(BankB, 4);
        Assert.Contains(mem.WrittenBytes, w => w.addr == tgtA + 2);
        Assert.DoesNotContain(mem.WrittenBytes, w => w.addr >= tgtB && w.addr < tgtB + 32);

        // Proves no snapshot was recorded for BankB: stage it (now readable) with fresh content,
        // then force a Restore -- if a (BankB, 4) snapshot had been wrongly recorded (e.g. as
        // zeros), this Restore would overwrite the fresh content with it. It must not.
        var freshB = SeedBank(5);
        StageBank(mem, BankB, 4, freshB);
        int writesBeforeRestore = mem.WrittenBytes.Count;
        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 12345;   // unauthored -> Restore
        wp.Tick(true);

        Assert.DoesNotContain(mem.WrittenBytes.Skip(writesBeforeRestore), w => w.addr >= tgtB && w.addr < tgtB + 32);
        Assert.Equal(freshB, ReadBank(mem, BankB, 4));   // BankB's fresh content survives untouched
    }

    // ---- TDD item 7: FlagOwner refusal holds state (no restore, no write) ----

    [Fact]
    public void Tick_NoFlagOwner_HoldsState_NoWrites()
    {
        var mem = new FakeSparseMemory();
        // A band entry exists and carries a valid, authored weapon, but ATurnFlag is never set
        // to 1 -- Band.FlagOwner must refuse (mirrors FlagOwnerResolveTests.FlagOwner_zero_t1...).
        MemSeats.SeatBand(mem, Slot, weapon: 9, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        StageBank(mem, BankA, 4, SeedBank());
        StageBank(mem, BankB, 4, SeedBank());
        var wp = new WeaponPalette(Meta(9, 4), mem, new Dictionary<int, int>());

        wp.Tick(true);

        Assert.Empty(mem.WrittenBytes);
    }

    // ---- TDD item 8: an unauthored/absent weapon on the owner's turn restores the painted palette ----

    [Fact]
    public void UnauthoredWeaponOnOwnersTurn_RestoresThePaintedPalette()
    {
        var seed = SeedBank();
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);
        wp.Tick(true);   // paint

        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 999999 & 0xFFFF;   // not in meta at all
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed[i], after[i]);
        }
    }

    // ---- TDD item 9: re-assert every ReassertTicks -- exactly one extra write cycle, bit 15
    //      re-carried from the CURRENT bytes (seeded differently before the re-assert) ----

    [Fact]
    public void SameOwnerHeldPastReassertTicks_RepaintsOnce_CarryingCurrentBit15()
    {
        var seed = SeedBank(2);
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);

        wp.Tick(true);   // initial paint: one write cycle (both banks)
        int writesAfterInitialPaint = mem.WrittenBytes.Count;
        Assert.Equal(Banks.Length, writesAfterInitialPaint);

        // ReassertTicks further ticks with nothing changed: all "Nothing" (no writes) -- the
        // counter Decide reads is the value BEFORE this tick's own increment, so it takes
        // exactly ReassertTicks such ticks to carry the counter up to ReassertTicks itself.
        for (int i = 0; i < ReassertTicks; i++) wp.Tick(true);
        Assert.Equal(writesAfterInitialPaint, mem.WrittenBytes.Count);

        // Something rewrote the banks underneath (a different bit-15 pattern this time), without
        // going through WeaponPalette -- the re-assert must carry THESE bytes' bit 15s, not the
        // original snapshot's and not the first paint's.
        var reseeded = SeedBank(9, 11);
        foreach (long bank in Banks) StageBank(mem, bank, 4, reseeded);

        wp.Tick(true);   // the ReassertTicks-th unchanged tick -> exactly one more write cycle

        Assert.Equal(writesAfterInitialPaint + Banks.Length, mem.WrittenBytes.Count);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
            {
                bool expectBit15 = i == 9 || i == 11;
                Assert.Equal(expectBit15, (after[i] & 0x8000) != 0);
                Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);
            }
        }
    }

    // ---- TDD item 10: ResetBattle clears the latch and keeps _vanilla (observable: a fresh
    //      paint WRITE happens on the next tick with the same owner) ----

    [Fact]
    public void ResetBattle_ClearsLatch_NextTickWithSameOwnerRepaints()
    {
        var seed = SeedBank();
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);
        wp.Tick(true);
        int writesAfterFirstPaint = mem.WrittenBytes.Count;

        wp.ResetBattle();
        wp.Tick(true);   // same owner, same weapon -- must repaint (latch was cleared)

        Assert.Equal(writesAfterFirstPaint + Banks.Length, mem.WrittenBytes.Count);
    }

    // ---- TDD item 15: inLive == false writes nothing, even with a resolvable owner+weapon ----

    [Fact]
    public void Tick_InLiveFalse_NoWrites()
    {
        var (mem, wp) = Build(weaponId: 9, pal: 4, SeedBank());

        wp.Tick(false);

        Assert.Empty(mem.WrittenBytes);
    }

    // ---- TDD item 16: palette-switch ordering -- the old palette's restore writes land BEFORE
    //      the new palette's paint writes ----

    [Fact]
    public void PaletteSwitch_RestoresOldPaletteBeforePaintingTheNew()
    {
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);   // owner A, weapon 9, palette 4
        StageBank(mem, BankA, 4, SeedBank());
        StageBank(mem, BankB, 4, SeedBank());
        StageBank(mem, BankA, 7, SeedBank(3));
        StageBank(mem, BankB, 7, SeedBank(3));
        var meta = Meta(9, 4);
        meta[22] = new WeaponMeta { Name = "Other", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "g", Palette = 7, Colors = Codes() };
        var wp = new WeaponPalette(meta, mem, new Dictionary<int, int>());

        wp.Tick(true);   // paint owner A's weapon 9 into palette 4

        // The turn passes to a different owner (a different band slot) wielding weapon 22
        // (palette 7, a DIFFERENT palette from 4).
        mem.U8s[Band.Entry(Slot) + Offsets.ATurnFlag] = 0;
        SeatOwner(mem, SlotOther, weaponId: 22);

        int writeOrderCountBefore = mem.WriteOrder.Count;
        wp.Tick(true);

        long restoreAddr = Tgt(BankA, 4) + 2;
        long paintAddr = Tgt(BankA, 7) + 2;
        var thisTickOrder = mem.WriteOrder.Skip(writeOrderCountBefore).ToList();
        int restoreIdx = thisTickOrder.IndexOf(restoreAddr);
        int paintIdx = thisTickOrder.IndexOf(paintAddr);
        Assert.True(restoreIdx >= 0 && paintIdx >= 0, "expected both a restore write and a paint write this tick");
        Assert.True(restoreIdx < paintIdx, "the old palette's restore must be written before the new palette's paint");
    }

    // ================= 3. Additive edge-case coverage (module-level) =================
    // Six scenarios not exercised above. Each pins a correctness property of the shared
    // (bank, pal) -> vanilla-snapshot dictionary and the FlagOwner-driven latch, using only the
    // public surface (Tick + ResetBattle) against FakeSparseMemory, mirroring section 2's style.

    // ---- Scenario 1: same-palette handoff between two DIFFERENT authored owners. The second
    //      owner's paint must not restore the first owner's palette in between (Decide's own
    //      "same palette, different weapon -> Paint only" row), so the snapshot a later restore
    //      uses is still the bytes captured before EITHER owner ever painted. ----

    [Fact]
    public void SamePaletteHandoff_SecondOwnerPaintsWithoutRestore_LaterRestoreUsesPreFirstOwnerBytes()
    {
        var mem = new FakeSparseMemory();
        var seed = SeedBank(1, 13);   // the true, pre-anyone vanilla bytes
        StageBank(mem, BankA, 4, seed);
        StageBank(mem, BankB, 4, seed);
        var meta = Meta(9, 4);   // owner A: weapon 9 -> palette 4
        meta[22] = new WeaponMeta { Name = "Other", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "g", Palette = 4, Colors = AltCodes() };   // owner B: weapon 22 -> SAME palette 4
        var wp = new WeaponPalette(meta, mem, new Dictionary<int, int>());

        long entryA = SeatOwner(mem, Slot, weaponId: 9);
        wp.Tick(true);   // owner A paints; captures the palette-4 snapshot from `seed`
        int writesBeforeHandoff = mem.WrittenBytes.Count;

        // Owner A's turn ends; a different band entry, a different authored weapon, the SAME
        // palette becomes the FlagOwner.
        mem.U8s[entryA + Offsets.ATurnFlag] = 0;
        long entryB = SeatOwner(mem, SlotOther, weaponId: 22);
        wp.Tick(true);

        // Same palette, different weapon -> exactly one more write cycle (Paint only): no restore
        // write landed between owner A and owner B.
        Assert.Equal(writesBeforeHandoff + Banks.Length, mem.WrittenBytes.Count);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(AltCodes()[i - 1], after[i] & 0x7FFF);   // owner B's codes now showing
        }

        // Owner B's weapon becomes unauthored -> Restore. Prove the restore uses the bytes
        // captured before owner A's FIRST paint, not anything from owner B's intervening one.
        mem.U16s[entryB + Offsets.AWeapon] = 999999 & 0xFFFF;
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed[i], after[i]);   // the ORIGINAL pre-owner-A vanilla, bit 15 included
        }
    }

    // ---- Scenario 2: mid-turn weapon swap on the SAME band entry (a steal/break), to a
    //      DIFFERENT palette -- the module has no owner-identity concept, so a bare weapon-id
    //      rewrite with no re-seat must drive the same RestoreThenPaint the cross-owner switch
    //      test (section 2, above) drives. The "swap to an UNAUTHORED id" half of this scenario
    //      is already covered by UnauthoredWeaponOnOwnersTurn_RestoresThePaintedPalette (same
    //      band entry, weapon rewritten to an id absent from meta -> Restore, no paint). ----

    [Fact]
    public void MidTurnWeaponSwap_SameEntry_DifferentPalette_RestoresOldThenPaintsNew()
    {
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, weaponId: 9);   // weapon 9, palette 4
        var seed4 = SeedBank(3);
        var seed7 = SeedBank(11);
        StageBank(mem, BankA, 4, seed4);
        StageBank(mem, BankB, 4, seed4);
        StageBank(mem, BankA, 7, seed7);
        StageBank(mem, BankB, 7, seed7);
        var meta = Meta(9, 4);
        meta[22] = new WeaponMeta { Name = "Other", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "g", Palette = 7, Colors = AltCodes() };
        var wp = new WeaponPalette(meta, mem, new Dictionary<int, int>());

        wp.Tick(true);   // paint weapon 9 into palette 4

        // The SAME entry's weapon id changes mid-turn (a steal or a break); the flag byte itself
        // is never touched -- FlagOwner still resolves the same entry.
        mem.U16s[entry + Offsets.AWeapon] = 22;
        int writeOrderCountBefore = mem.WriteOrder.Count;
        wp.Tick(true);

        long restoreAddr = Tgt(BankA, 4) + 2;
        long paintAddr = Tgt(BankA, 7) + 2;
        var order = mem.WriteOrder.Skip(writeOrderCountBefore).ToList();
        int restoreIdx = order.IndexOf(restoreAddr);
        int paintIdx = order.IndexOf(paintAddr);
        Assert.True(restoreIdx >= 0 && paintIdx >= 0, "expected both a restore write and a paint write this tick");
        Assert.True(restoreIdx < paintIdx, "the old palette's restore must land before the new palette's paint");

        foreach (long bank in Banks)
        {
            var restored = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed4[i], restored[i]);   // palette 4 back to vanilla, verbatim

            var painted = ReadBank(mem, bank, 7);
            for (int i = 1; i <= 15; i++) Assert.Equal(AltCodes()[i - 1], painted[i] & 0x7FFF);   // palette 7 now carries weapon 22's codes
        }
    }

    // ---- Scenario 3: mid-turn weapon swap on the SAME band entry, to a weapon mapped to the
    //      SAME palette -- Decide's "same palette, different weapon -> Paint only" row again, but
    //      pinned here with full content checks: the repaint carries weapon 22's codes, AND the
    //      snapshot a later restore uses is still weapon 9's pre-paint bytes (never re-captured
    //      from the intermediate, already-repainted bank). ----

    [Fact]
    public void MidTurnWeaponSwap_SameEntry_SamePalette_RepaintsNoRestore_SnapshotUnchanged()
    {
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, weaponId: 9);
        var seed = SeedBank(4, 8);
        StageBank(mem, BankA, 4, seed);
        StageBank(mem, BankB, 4, seed);
        var meta = Meta(9, 4);
        meta[22] = new WeaponMeta { Name = "Other", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "g", Palette = 4, Colors = AltCodes() };
        var wp = new WeaponPalette(meta, mem, new Dictionary<int, int>());

        wp.Tick(true);   // paint weapon 9's codes into palette 4; captures the snapshot from `seed`
        int writesAfterFirstPaint = mem.WrittenBytes.Count;

        mem.U16s[entry + Offsets.AWeapon] = 22;   // same entry, same palette, different weapon
        wp.Tick(true);

        // Same-palette swap is a Paint only -- exactly one more write cycle, never a
        // restore-then-paint's two.
        Assert.Equal(writesAfterFirstPaint + Banks.Length, mem.WrittenBytes.Count);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
                Assert.Equal(AltCodes()[i - 1], after[i] & 0x7FFF);   // now carries weapon 22's codes
        }

        // The snapshot captured at the FIRST paint (weapon 9's, from `seed`) must still be the one
        // a later restore uses -- proving the W1 -> W2 same-palette transition never re-captured it.
        mem.U16s[entry + Offsets.AWeapon] = 999999 & 0xFFFF;   // unauthored -> Restore
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed[i], after[i]);   // back to the ORIGINAL seed, not AltCodes
        }
    }

    // ---- Scenario 4: FlagOwner goes dark AFTER a paint (death, ambiguity, turn end with no
    //      successor yet) and stays dark for many ticks -- comfortably past ReassertTicks, since
    //      Tick's early return on FlagOwner refusal means _ticksUnchanged never advances while
    //      dark, so the reassert cadence itself cannot fire during darkness either. ResetBattle
    //      then clears the latch; the next resolvable owner must repaint from a FRESH read (proven
    //      by reseeding the banks with a different bit-15 pattern between darkness and revival).
    //      ----

    [Fact]
    public void OwnerGoesDark_HoldsAcrossManyTicks_ResetBattleThenFreshOwnerRepaintsFromFreshRead()
    {
        var seed = SeedBank(1, 7);
        var (mem, wp) = Build(weaponId: 9, pal: 4, seed);
        long entry = Band.Entry(Slot);

        wp.Tick(true);   // paint
        int writesAfterPaint = mem.WrittenBytes.Count;

        mem.U8s[entry + Offsets.ATurnFlag] = 0;   // the owner's turn flag goes dark

        for (int i = 0; i < ReassertTicks * 3; i++) wp.Tick(true);
        Assert.Equal(writesAfterPaint, mem.WrittenBytes.Count);   // held: no restore, no repaint, no matter how long

        wp.ResetBattle();   // battle-enter/exit edge: the banks were just refreshed from the loaded file

        // Something else (exactly what ResetBattle's own doc describes) rewrites the raw bytes
        // underneath while the latch is clear, so a genuinely fresh read is distinguishable from a
        // stale cached one.
        var reloaded = SeedBank(6, 12);
        foreach (long bank in Banks) StageBank(mem, bank, 4, reloaded);

        mem.U8s[entry + Offsets.ATurnFlag] = 1;   // a fresh resolvable owner, same weapon, same palette
        wp.Tick(true);

        Assert.Equal(writesAfterPaint + Banks.Length, mem.WrittenBytes.Count);   // exactly one fresh paint cycle
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++)
            {
                bool expectBit15 = i == 6 || i == 12;
                Assert.Equal(expectBit15, (after[i] & 0x8000) != 0);   // carried from the RELOADED bytes, not the original seed
                Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);
            }
        }
    }

    // ---- Scenario 5: a restore requested for a palette with NO captured snapshot on one bank
    //      (unreadable across every paint attempt on that palette, including a reassert repaint),
    //      readable only later -- the restore must write nothing to that bank, while the OTHER
    //      bank (which DOES have a snapshot) restores to its exact original content, not whatever
    //      bytes happened to be present during the second paint attempt. ----

    [Fact]
    public void SnapshotMissBank_NeverWritten_AcrossMultiplePaintAttempts_HealthyBankRestoresVerbatim()
    {
        var mem = new FakeSparseMemory();
        long entry = SeatOwner(mem, Slot, weaponId: 9);
        var seedA = SeedBank(2, 10);
        StageBank(mem, BankA, 4, seedA);   // BankB deliberately left unstaged for the whole test
        var wp = new WeaponPalette(Meta(9, 4), mem, new Dictionary<int, int>());

        wp.Tick(true);   // paint attempt 1 (the initial paint)

        // Ride past the reassert cadence to force a SECOND paint attempt on the same (bank, pal):
        // BankB must stay unreadable, and therefore snapshot-less, across BOTH attempts.
        for (int i = 0; i < ReassertTicks; i++) wp.Tick(true);
        var reseededA = SeedBank(5);
        StageBank(mem, BankA, 4, reseededA);
        wp.Tick(true);   // paint attempt 2 (the reassert repaint)

        long tgtB = Tgt(BankB, 4);
        Assert.DoesNotContain(mem.WrittenBytes, w => w.addr >= tgtB && w.addr < tgtB + 32);

        // BankB becomes readable only now, with fresh content distinguishable from anything a
        // wrongly recorded snapshot could produce.
        var freshB = SeedBank(9);
        StageBank(mem, BankB, 4, freshB);
        int writesBeforeRestore = mem.WrittenBytes.Count;

        mem.U16s[entry + Offsets.AWeapon] = 999999 & 0xFFFF;   // unauthored -> Restore
        wp.Tick(true);

        // BankB: still no write, ever -- a restore must never invent bytes for a bank it has no
        // snapshot for.
        Assert.DoesNotContain(mem.WrittenBytes.Skip(writesBeforeRestore), w => w.addr >= tgtB && w.addr < tgtB + 32);
        Assert.Equal(freshB, ReadBank(mem, BankB, 4));

        // BankA: it DOES have a snapshot (captured on paint attempt 1, from `seedA`) and must
        // restore to that ORIGINAL content verbatim -- not `reseededA`, the bytes present during
        // attempt 2.
        var afterA = ReadBank(mem, BankA, 4);
        for (int i = 1; i <= 15; i++) Assert.Equal(seedA[i], afterA[i]);
    }

    // ---- Scenario 6: two authored owners alternating across two DIFFERENT palettes over several
    //      turns. Each transition must restore the previous palette before painting the next (the
    //      section-2 ordering test's own property), and after several such alternations, BOTH
    //      palettes' snapshots must still be their original vanilla bytes -- no drift from being
    //      restored-and-repainted repeatedly. ----

    [Fact]
    public void AlternatingOwnersAcrossPalettes_RestoresBeforeEachPaint_SnapshotsNeverDriftAcrossRounds()
    {
        var mem = new FakeSparseMemory();
        var seed4 = SeedBank(2, 5);
        var seed7 = SeedBank(9);
        StageBank(mem, BankA, 4, seed4);
        StageBank(mem, BankB, 4, seed4);
        StageBank(mem, BankA, 7, seed7);
        StageBank(mem, BankB, 7, seed7);
        var meta = Meta(9, 4);   // owner A (slot 20): weapon 9 -> palette 4
        meta[22] = new WeaponMeta { Name = "Other", Wp = 5, Cat = "Sword", Formula = 1, Flavor = "g", Palette = 7, Colors = AltCodes() };   // owner B (slot 21): weapon 22 -> palette 7
        var wp = new WeaponPalette(meta, mem, new Dictionary<int, int>());

        SeatOwner(mem, Slot, weaponId: 9);
        wp.Tick(true);   // paint palette 4

        for (int round = 0; round < 3; round++)
        {
            mem.U8s[Band.Entry(Slot) + Offsets.ATurnFlag] = 0;
            SeatOwner(mem, SlotOther, weaponId: 22);
            wp.Tick(true);   // restore palette 4, paint palette 7

            mem.U8s[Band.Entry(SlotOther) + Offsets.ATurnFlag] = 0;
            SeatOwner(mem, Slot, weaponId: 9);
            wp.Tick(true);   // restore palette 7, paint palette 4
        }
        // Currently painted: palette 4 (owner A), after 3 full alternation rounds.

        // Hand off to owner B again to force palette 4's restore, and check it against seed4.
        mem.U8s[Band.Entry(Slot) + Offsets.ATurnFlag] = 0;
        SeatOwner(mem, SlotOther, weaponId: 22);
        wp.Tick(true);   // restore palette 4 (checked below), paint palette 7
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed4[i], after[i]);   // still the ORIGINAL vanilla, no drift
        }

        // Owner B's weapon becomes unauthored to force palette 7's restore, and check it against seed7.
        mem.U16s[Band.Entry(SlotOther) + Offsets.AWeapon] = 999999 & 0xFFFF;
        wp.Tick(true);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 7);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed7[i], after[i]);   // still the ORIGINAL vanilla, no drift
        }
    }

    // ================= 3. LW-308 hardening =================

    // ---- Bit 15 in a baked code is stripped before the carry: the bank is the ONLY bit-15 source ----

    [Fact]
    public void Paint_BakedCodeWithBit15_StrippedBeforeCarry()
    {
        var codes = Codes();
        codes[0] = 0x8001;   // an (illegal) authored code carrying bit 15, targeting entry 1
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, SeedBank());   // seed has NO bit 15 anywhere
        var wp = new WeaponPalette(Meta(9, 4, codes), mem, new Dictionary<int, int>());

        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var e = ReadBank(mem, bank, 4);
            Assert.Equal(0, e[1] & 0x8000);        // bit 15 must come from the bank alone, and the seed had none
            Assert.Equal(0x0001, e[1] & 0x7FFF);   // the colour half of the code still lands
        }
    }

    // ---- SnapshotSuspect: pure verdict on a bank whose bytes match neither snapshot nor last paint ----

    private static ushort[] Fifteen(int seed) => Enumerable.Range(0, 15).Select(i => (ushort)(seed + i)).ToArray();

    [Fact]
    public void SnapshotSuspect_CurrentMatchesVanilla_False()
        => Assert.False(WeaponPalettePolicy.SnapshotSuspect(Fifteen(0x100), Fifteen(0x100), Fifteen(0x200)));

    [Fact]
    public void SnapshotSuspect_CurrentMatchesLastWritten_False()
        => Assert.False(WeaponPalettePolicy.SnapshotSuspect(Fifteen(0x200), Fifteen(0x100), Fifteen(0x200)));

    [Fact]
    public void SnapshotSuspect_CurrentMatchesNeither_True()
        => Assert.True(WeaponPalettePolicy.SnapshotSuspect(Fifteen(0x300), Fifteen(0x100), Fifteen(0x200)));

    [Fact]
    public void SnapshotSuspect_NoLastWritten_ComparesVanillaOnly()
    {
        Assert.False(WeaponPalettePolicy.SnapshotSuspect(Fifteen(0x100), Fifteen(0x100), null));
        Assert.True(WeaponPalettePolicy.SnapshotSuspect(Fifteen(0x300), Fifteen(0x100), null));
    }

    // ---- The stateful half warns LOUDLY (once per battle per bank+palette) on foreign bank bytes ----

    [Fact]
    public void Paint_ForeignBankBytes_WarnsOnce_AndNeverRememorizes()
    {
        var (mem, wp) = Build(9, 4, SeedBank());
        wp.Tick(true);   // first paint: snapshot captured, codes written

        // A foreign writer stomps BOTH banks with bytes matching neither vanilla nor our paint.
        foreach (long bank in Banks)
            StageBank(mem, bank, 4, SeedBank().Select(v => (ushort)(v ^ 0x1234)).ToArray());

        using var cap = LogCapture.Start();
        for (int i = 0; i <= ReassertTicks; i++) wp.Tick(true);   // ride to the re-assert paint

        int warns = cap.File.Count(l => l.Contains("neither vanilla nor our last paint"));
        Assert.Equal(2, warns);   // one per bank, same palette, same battle

        // Stomp again: the per-battle guard holds, no second warn for the same (bank, palette).
        foreach (long bank in Banks)
            StageBank(mem, bank, 4, SeedBank().Select(v => (ushort)(v ^ 0x4321)).ToArray());
        for (int i = 0; i <= ReassertTicks; i++) wp.Tick(true);
        Assert.Equal(2, cap.File.Count(l => l.Contains("neither vanilla nor our last paint")));

        // The suspicion never re-memorizes: a restore still writes the ORIGINAL first-look snapshot.
        var seed = SeedBank();
        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 999999 & 0xFFFF;   // unauthored -> Restore
        wp.Tick(true);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed[i], after[i]);
        }
    }

    [Fact]
    public void Paint_ForeignBankBytes_WarnGuardClearsOnResetBattle()
    {
        var (mem, wp) = Build(9, 4, SeedBank());
        wp.Tick(true);
        foreach (long bank in Banks)
            StageBank(mem, bank, 4, SeedBank().Select(v => (ushort)(v ^ 0x1234)).ToArray());

        using var cap = LogCapture.Start();
        for (int i = 0; i <= ReassertTicks; i++) wp.Tick(true);
        Assert.Equal(2, cap.File.Count(l => l.Contains("neither vanilla nor our last paint")));

        wp.ResetBattle();   // new battle: the guard re-arms
        foreach (long bank in Banks)
            StageBank(mem, bank, 4, SeedBank().Select(v => (ushort)(v ^ 0x4321)).ToArray());
        wp.Tick(true);      // fresh paint in the "new" battle sees foreign bytes again
        Assert.Equal(4, cap.File.Count(l => l.Contains("neither vanilla nor our last paint")));
    }

    // ================= 4. LW-295 kill-tier glow (Tick-level) =================
    // DesiredGlow's memory-reading half (WeaponPalette.Desire.cs) exercised only through Tick --
    // it is private, the same "public surface only" discipline section 2/3 already follow.

    // ---- TDD item 6: a PLAYER wielder (roster row matches the acting unit's own weapon+fp) at a
    //      non-zero kill tier paints the BRIGHTENED codes, not the plain authored ones. ----

    [Fact]
    public void Tick_PlayerWielderAtTier_PaintsBrightenedCodes()
    {
        var seed = SeedBank();
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 10 };   // prod tier 2 (>= 10, < 15)
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);

        var expected = WeaponPalettePolicy.BrightenAll(Codes(), Tuning.GlowFactors[2]);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(expected[i - 1], after[i] & 0x7FFF);
        }
    }

    // ---- LW-295 verify-round item: the tier-glow paint path carries bit 15 too -- item 6 above
    //      masks every written entry with & 0x7FFF and so never proves it. This pins the SAME
    //      staging as Paint_CarriesBit15FromCurrentBytes_NeverInventsIt (bit 15 set on entries 2,
    //      6, 15) through a glowing (tier 2, factor 0.50) wielder and asserts the UNMASKED
    //      ushort: the bank's own bit 15 OR'd onto the brightened code, never the plain code and
    //      never a dropped bit 15. This is a pin on already-correct behaviour (PaintBanks already
    //      carries bit 15 the same way regardless of glow) -- it goes green immediately, so
    //      non-vacuity comes from the exact hand-computed values below, not from a red-first run.
    //
    //      By hand: Codes() = 1..15. Brighten operates on ALL THREE channels every time, not just
    //      the ones a code happens to set -- so even though every one of these codes is < 32 (a
    //      bare r channel, g=b=0), the g and b channels still brighten FROM 0 toward white:
    //      round(0 + (31-0)*0.50) = round(15.5) = 16 (Math.Round's default MidpointRounding.ToEven,
    //      the same banker's-rounding half-case rule the class doc cites), for BOTH g and b, on
    //      every entry. That constant contributes (16<<5)|(16<<10) = 0x4200 to every entry; only
    //      the r channel (= round(c + (31-c)*0.50)) varies per entry:
    //        c= 1 -> 1+15.0=16.0  -> 16      c= 9 -> 9+11.0=20.0  -> 20
    //        c= 2 -> 2+14.5=16.5  -> 16      c=10 -> 10+10.5=20.5 -> 20
    //        c= 3 -> 3+14.0=17.0  -> 17      c=11 -> 11+10.0=21.0 -> 21
    //        c= 4 -> 4+13.5=17.5  -> 18      c=12 -> 12+ 9.5=21.5 -> 22
    //        c= 5 -> 5+13.0=18.0  -> 18      c=13 -> 13+ 9.0=22.0 -> 22
    //        c= 6 -> 6+12.5=18.5  -> 18      c=14 -> 14+ 8.5=22.5 -> 22
    //        c= 7 -> 7+12.0=19.0  -> 19      c=15 -> 15+ 8.0=23.0 -> 23
    //        c= 8 -> 8+11.5=19.5  -> 20
    //      So brightened(c) = 0x4200 | r, e.g. c=1 -> 0x4200|16 = 0x4210. Entries 2, 6, 15 carry
    //      bit 15 (0x8000) from SeedBank(2, 6, 15); every other entry does not. Combined (entry
    //      index i, 1-based, value = brightened(Codes()[i-1]) | carriedBit15):
    //        1:0x4210  2:0xC210  3:0x4211  4:0x4212  5:0x4212  6:0xC212  7:0x4213  8:0x4214
    //        9:0x4214 10:0x4214 11:0x4215 12:0x4216 13:0x4216 14:0x4216 15:0xC217 ----

    [Fact]
    public void Tick_PlayerWielderAtTier_CarriesBankBit15s()
    {
        var seed = SeedBank(2, 6, 15);   // same subset as Paint_CarriesBit15FromCurrentBytes_NeverInventsIt
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 10 };   // prod tier 2 (>= 10, < 15) -> factor 0.50
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);

        ushort[] expected =
        {
            0x4210, 0xC210, 0x4211, 0x4212, 0x4212, 0xC212, 0x4213, 0x4214,
            0x4214, 0x4214, 0x4215, 0x4216, 0x4216, 0x4216, 0xC217,
        };
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(expected[i - 1], after[i]);   // UNMASKED
        }
    }

    // ---- TDD item 7: the SAME weapon id, tallied to the SAME tier, but with NO roster row
    //      matching the acting unit (an enemy wielding an identical weapon) paints PLAIN --
    //      the enemy's own turn never glows. ----

    [Fact]
    public void Tick_NoRosterMatch_SameWeapon_PaintsPlainAuthored()
    {
        var seed = SeedBank();
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);   // the acting unit -- no roster row staged at all
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 10 };   // tier 2 -- but no player match
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);   // plain
        }
    }

    // ---- TDD item 8: a kill tally that crosses a tier boundary MID-BATTLE (no ResetBattle)
    //      repaints brighter on the very next tick -- the new Decide row (item 1) firing through
    //      the full Tick pipeline, not just the pure policy call. ----

    [Fact]
    public void Tick_TierBumpMidBattle_RepaintsBrighter()
    {
        var seed = SeedBank();
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 9 };   // prod tier 1 (>= 5, < 10)
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);
        var tier1 = WeaponPalettePolicy.BrightenAll(Codes(), Tuning.GlowFactors[1]);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(tier1[i - 1], after[i] & 0x7FFF);
        }

        kills[9] = 10;   // crosses into tier 2 mid-battle -- no ResetBattle call
        wp.Tick(true);

        var tier2 = WeaponPalettePolicy.BrightenAll(Codes(), Tuning.GlowFactors[2]);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(tier2[i - 1], after[i] & 0x7FFF);
        }
    }

    // ---- TDD item 9: a player wielder below the first kill threshold paints PLAIN (tier 0, no
    //      glow -- the shared kills dict is wired in but the wielder hasn't earned anything yet). ----

    [Fact]
    public void Tick_TierZeroPlayer_PaintsPlainAuthored()
    {
        var seed = SeedBank();
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 4 };   // below prod tier 1 (5)
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(Codes()[i - 1], after[i] & 0x7FFF);
        }
    }

    // ---- TDD item 10: restore is unaffected by glow -- a palette painted BRIGHTENED still
    //      restores the pre-first-paint snapshot verbatim (bit 15 included) once the owner's
    //      weapon becomes unauthored. ----

    [Fact]
    public void Restore_AfterBrightenedPaint_RestoresPreFirstPaintSnapshotVerbatim()
    {
        var seed = SeedBank(3, 9);   // entries 3 and 9 start with bit 15 set
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 10 };   // tier 2 -- painted brightened
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);   // paints tier-2 brightened codes; captures the snapshot from `seed`

        mem.U16s[Band.Entry(Slot) + Offsets.AWeapon] = 12345;   // unauthored -> Restore
        wp.Tick(true);

        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(seed[i], after[i]);   // vanilla, bit 15 included
        }
    }

    // ---- TDD item 11: TWO roster rows share the acting unit's weapon+fingerprint (matchCount 2)
    //      -- Wielder.ResolveAnyHandNameId's 4-arg overload returns -1 for this (the historic
    //      zero-vs-ambiguous conflation), but DesiredGlow must judge on matchCount >= 1, NEVER on
    //      that -1 return, so the wielder still glows. ----

    [Fact]
    public void Tick_TwoRosterTwins_SameWeaponFp_StillGlows()
    {
        var seed = SeedBank();
        var mem = new FakeSparseMemory();
        SeatOwner(mem, Slot, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 0, weaponId: 9);
        SeatMatchingRoster(mem, rosterSlot: 1, weaponId: 9);   // a twin: same weapon, same fp
        foreach (long bank in Banks) StageBank(mem, bank, 4, seed);
        var kills = new Dictionary<int, int> { [9] = 10 };   // tier 2
        var wp = new WeaponPalette(Meta(9, 4), mem, kills);

        wp.Tick(true);

        var expected = WeaponPalettePolicy.BrightenAll(Codes(), Tuning.GlowFactors[2]);
        foreach (long bank in Banks)
        {
            var after = ReadBank(mem, bank, 4);
            for (int i = 1; i <= 15; i++) Assert.Equal(expected[i - 1], after[i] & 0x7FFF);
        }
    }
}
