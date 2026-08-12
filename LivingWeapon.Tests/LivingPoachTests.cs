using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-167 Living Poach (docs plan v2, stages 1-2). Mirrors GunSlingerTests' staged structure:
///   Stage-1  LivingPoachPolicy pure decisions (Decide / SpeciesOf / StripIconMarkup)
///   Stage-1b PoachMap loader (the real committed poach.json + missing/corrupt-file disarm)
///   Stage-2  LivingPoach executor via FakeSparseMemory (guarded W8 store write + toast + the
///            per-corpse dedupe latch) and DeedFanout/KillTracker's widened deed seam.
///   Stage-2b The killer's real Poach-bit read (combat support bitfield).
///   Stage-4  The real basic-Attack discriminator (LivingPoach.ReadWasBasicAttack) and the ARMED
///            end-to-end wiring (Engine.cs no longer passes a constant false) -- see LivingPoach.cs's
///            ctor doc. Most stage-2 executor tests above still inject wasBasicAttack directly
///            (MakePoach's bool param) to stay isolated from live-memory fixtures; that is
///            deliberate, not stale coverage -- the discriminator itself is proven separately below.
/// Reliquary/KillTracker's PRE-EXISTING suites (ReliquaryTests.cs, KillTrackerDeedTests.cs,
/// KillTrackerBattleCountersTests.cs) are the regression proof that this feature's seam-widening is
/// byte-identical for every other consumer; they run unmodified.
/// </summary>
public class LivingPoachTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();
    public void Dispose() { foreach (var t in _tempDirs) t.Dispose(); }
    private string TempDir() { var t = TempDirs.Create("lw_poach_"); _tempDirs.Add(t); return t.Dir; }

    /// <summary>Walk up from the test bin dir to the repo root, mirroring MetaSchemaTests'
    /// RepoMetaPath -- returns the LivingWeapon/ dir (poach.json's home), not the file itself.</summary>
    private static string RepoLivingWeaponDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "poach.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "LivingWeapon");
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/poach.json not found above the test bin dir");
    }

    // ── Stage-1: LivingPoachPolicy pure decisions ─────────────────────────────

    // KEYSTONE (the double-fire guard #1): a vanilla-formula weapon must NEVER fire, even with
    // every other gate green -- vanilla's own Poach support already procs through that weapon.
    [Fact]
    public void Decide_KEYSTONE_vanillaFormula_neverFires_evenWithEveryOtherGateGreen()
    {
        var v = LivingPoachPolicy.Decide(weaponIsDormantFormula: false, killerHasPoach: true,
            wasBasicAttack: true, victimJob: 96, speciesMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    // Double-fire guard #2: vanilla poaches an ABILITY kill itself (owner-observed live
    // 2026-08-12), so an ability kill must never ALSO fire the Living Poach roll.
    [Fact]
    public void Decide_wasBasicAttack_false_None()
    {
        var v = LivingPoachPolicy.Decide(true, killerHasPoach: true, wasBasicAttack: false,
            victimJob: 96, speciesMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    [Fact]
    public void Decide_killerHasPoach_false_None()
    {
        var v = LivingPoachPolicy.Decide(true, killerHasPoach: false, wasBasicAttack: true,
            victimJob: 96, speciesMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    [Fact]
    public void Decide_unmapped_species_None()
    {
        var v = LivingPoachPolicy.Decide(true, true, true, victimJob: 96, speciesMapped: false, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    [Theory]
    [InlineData(95)]   // species 0 -- one below the monster band
    [InlineData(145)]  // species 50 -- one past poach.json's 48 species
    public void Decide_job_outside_monster_range_None(int job)
    {
        var v = LivingPoachPolicy.Decide(true, true, true, victimJob: job, speciesMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    // internal enum can't be a public [Theory] parameter type (CS0051) -- expectCommon stands in.
    [Theory]
    [InlineData(0, true)]
    [InlineData(224, true)]
    [InlineData(225, false)]
    [InlineData(255, false)]
    public void Decide_roll_boundaries(int roll, bool expectCommon)
    {
        var v = LivingPoachPolicy.Decide(true, true, true, victimJob: 96, speciesMapped: true, roll: roll);
        Assert.Equal(expectCommon ? PoachVerdict.Common : PoachVerdict.Rare, v);
    }

    [Theory]
    [InlineData(96, 1)]
    [InlineData(101, 6)]
    [InlineData(144, 49)]
    public void SpeciesOf_math(int job, int expectedSpecies)
        => Assert.Equal(expectedSpecies, LivingPoachPolicy.SpeciesOf(job));

    [Theory]
    [InlineData("Chocobo Carcass<Icon=103>", "Chocobo Carcass")]
    [InlineData("Chocobo Carcass", "Chocobo Carcass")]
    public void StripIconMarkup_removes_markup_leaves_plain_names_alone(string raw, string expected)
        => Assert.Equal(expected, LivingPoachPolicy.StripIconMarkup(raw));

    // ── Stage-1b: PoachMap loader ──────────────────────────────────────────────

    [Fact]
    public void PoachMap_loads_the_real_committed_poach_json()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.IsLoaded);

        Assert.True(map.TryGetSpecies(1, out var species1));
        Assert.Equal(1, species1.CommonKey);
        Assert.Equal("Chocobo Carcass", species1.CommonName);

        Assert.True(map.TryGetSpecies(6, out var species6));
        Assert.Equal(11, species6.CommonKey);
    }

    [Fact]
    public void PoachMap_missing_file_disarms_without_throwing()
    {
        PoachMap? map = null;
        var ex = Record.Exception(() => map = new PoachMap(TempDir()));
        Assert.Null(ex);
        Assert.False(map!.IsLoaded);
        Assert.False(map.TryGetSpecies(1, out _));
    }

    [Fact]
    public void PoachMap_corrupt_json_disarms_without_throwing()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "poach.json"), "{ not valid json");
        PoachMap? map = null;
        var ex = Record.Exception(() => map = new PoachMap(dir));
        Assert.Null(ex);
        Assert.False(map!.IsLoaded);
    }

    [Fact]
    public void PoachMap_missing_file_logs_exactly_one_warning()
    {
        using var cap = LogCapture.Start(file: false);
        _ = new PoachMap(TempDir());
        Assert.Single(cap.Console, l => l.Contains("Living Poach is disarmed"));
    }

    // Every carcass key in the committed file must fall inside the store's u8[96] -- the write
    // computes PoachStoreBase + key - 1, so a key outside 1..96 would write below or past the
    // array, and a duplicate key would let two different carcasses collide on one byte. Pins the
    // real committed poach.json so a future regeneration (tools/extract_poach_map.py) can never
    // silently produce either.
    [Fact]
    public void PoachMap_every_carcass_key_is_in_1_96_and_unique()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.IsLoaded);

        var keys = new List<int>();
        for (int species = 1; species <= 100; species++)
        {
            if (!map.TryGetSpecies(species, out var carcass)) continue;
            keys.Add(carcass.CommonKey);
            keys.Add(carcass.RareKey);
        }
        Assert.NotEmpty(keys);

        var seen = new HashSet<int>();
        foreach (int key in keys)
        {
            Assert.InRange(key, 1, 96);
            Assert.True(seen.Add(key), $"duplicate carcass key {key}");
        }
    }

    // ── Stage-2: LivingPoach executor via FakeSparseMemory ─────────────────────

    private const int PoachWeaponId = 999;   // an arbitrary id -- self-built meta, no items.json dependency
    private const int TestSpecies = 1;
    private const int TestCommonKey = 1;
    private const int TestRareKey = 2;
    private const int TestVictimJob = 96;    // SpeciesOf(96) == 1 == TestSpecies

    private static Dictionary<int, WeaponMeta> DormantMeta(int formula = 45) => new()
    {
        [PoachWeaponId] = new WeaponMeta { Name = "Test Blade", Wp = 10, Cat = "Sword", Formula = formula }
    };

    private string MakePoachJson(int species, int commonKey, string commonName, int rareKey, string rareName)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "poach.json"),
            "{\"species\":{\"" + species + "\":{\"common\":{\"key\":" + commonKey + ",\"name\":\"" + commonName + "\"},"
            + "\"rare\":{\"key\":" + rareKey + ",\"name\":\"" + rareName + "\"}}}}");
        return dir;
    }

    private static void MakeStoreSlot(FakeSparseMemory mem, int key, byte current = 0)
    {
        long addr = Offsets.PoachStoreBase + (key - 1);
        mem.ReadableAddrs.Add(addr);
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = current;
    }

    private static LivingPoach MakePoach(FakeSparseMemory mem, PoachMap map, Dictionary<int, WeaponMeta> meta,
        bool killerHasPoach = true, bool wasBasicAttack = true, int roll = 0, BannerToast? toast = null)
    {
        toast ??= new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        // wasBasicAttack takes weaponId (Func<int, bool>, matching killerHasPoach's shape) since
        // LW-167 stage 4 armed it to a per-weapon discriminator (LivingPoach.ReadWasBasicAttack);
        // this helper still injects the boolean directly so every executor-only test below stays
        // isolated from live memory fixtures.
        return new LivingPoach(meta, map, mem, toast, _ => killerHasPoach, _ => wasBasicAttack, () => roll);
    }

    [Fact]
    public void RecordPoachDeed_eligible_increments_the_store_by_exactly_one()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "Chocobo Carcass<Icon=103>"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 5);
        var poach = MakePoach(mem, map, meta, roll: 0);   // roll 0 -> Common

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal((byte)6, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_store_at_cap_255_refuses_the_write()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 255);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.False(mem.Written.ContainsKey(addr));
        Assert.Equal((byte)255, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_unwritable_address_no_write_no_throw()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = 3;   // readable, but NOT marked writable
        var poach = MakePoach(mem, map, meta, roll: 0);

        var ex = Record.Exception(() =>
            poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false));

        Assert.Null(ex);
        Assert.False(mem.Written.ContainsKey(addr));
    }

    [Fact]
    public void RecordPoachDeed_dual_credit_same_corpse_writes_exactly_once()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = new Dictionary<int, WeaponMeta>
        {
            [PoachWeaponId] = new WeaponMeta { Name = "Blade A", Formula = 45 },
            [PoachWeaponId + 1] = new WeaponMeta { Name = "Blade B", Formula = 46 },
        };
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var victim = new VictimSnapshot(true, 42, TestVictimJob, false);

        // Dual-wield credit: KillTracker's CreditKill loop calls the sink once PER credited
        // weapon, same slot+nameId both times.
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 7, false, false);
        poach.RecordPoachDeed(PoachWeaponId + 1, victim, slot: 7, false, false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(addr));
        Assert.Equal(1, mem.WriteOrder.FindAll(a => a == addr).Count);
    }

    [Fact]
    public void RecordPoachDeed_battle_reset_clears_the_latch_same_corpse_can_poach_again()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var victim = new VictimSnapshot(true, 42, TestVictimJob, false);
        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);

        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);   // same battle: deduped
        Assert.Equal((byte)1, mem.U8(addr));

        poach.ResetBattle();
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);   // next battle: fresh latch

        Assert.Equal((byte)2, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_enqueues_toast_once_with_key_5_and_stripped_name()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "Chocobo Carcass<Icon=103>"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestRareKey, current: 0);
        var toast = new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        var poach = new LivingPoach(meta, map, mem, toast, _ => true, _ => true, () => 255);   // roll 255 -> Rare

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        var queued = Assert.Single(toast._queue);
        Assert.Equal(PoachWeaponId, queued.weaponId);
        Assert.Equal(Tuning.PoachToastKey, queued.tier);
        Assert.Equal("Chocobo Carcass", queued.payload);   // markup stripped
    }

    [Fact]
    public void PoachToastKey_never_collides_with_tiers_bulwark_or_milestones()
    {
        Assert.Equal(5, Tuning.PoachToastKey);
        Assert.DoesNotContain(Tuning.PoachToastKey, new[] { 1, 2, 3, Tuning.BulwarkToastKey });
        Assert.True(Tuning.PoachToastKey > 0);   // never collides with a negated milestone key
    }

    [Fact]
    public void RecordPoachDeed_vanillaFormula_weapon_never_writes()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta(formula: 1);   // vanilla-readable formula -- NOT in Tuning.DormantPoachFormulas
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        Assert.Empty(mem.Written);
    }

    [Fact]
    public void RecordPoachDeed_wasBasicAttack_false_never_writes()
    {
        // Proves the EXECUTOR itself honors whatever wasBasicAttack(weaponId) reports (not merely
        // the pure policy) by injecting false directly, independent of the stage-4 discriminator's
        // own live-memory read (covered separately below by ReadWasBasicAttack's own unit tests
        // and the end-to-end armed-wiring test).
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, wasBasicAttack: false, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        Assert.Empty(mem.Written);
    }

    // ── Stage-3: corpse despawn integration (LW-167 stage 3, CorpseDespawn.cs) ────
    // Hand-seeded (not MemSeats: MemSeats never marks Readable, and CorpseDespawn's every read
    // is guard-gated) -- mirrors CorpseDespawnTests.cs's fixture shape exactly.

    private const int DespawnBandSlot = 10;
    private const long DespawnNodeAddr = 0x2000_3000;

    private static long DespawnFrame => Offsets.FrameReadBase + (long)DespawnBandSlot * Offsets.CombatStride;

    /// <summary>A live, freshly-dead, unconverted corpse at DespawnBandSlot with
    /// <paramref name="nameId"/>, plus a single render node whose combat back-pointer resolves to
    /// it -- every CorpseDespawn precondition green by default. Callers override individual
    /// fields/omit the node to exercise one refusal.</summary>
    private static void SeedDespawnableCorpse(FakeSparseMemory mem, ushort nameId, bool turnOpen = false)
    {
        long entry = Band.Entry(DespawnBandSlot);
        mem.U8s[entry + Offsets.ADeadStatus] = Offsets.ADeadBit;
        mem.MarkReadable(entry + Offsets.ADeadStatus, 1);
        mem.U16s[entry + Offsets.ANameId] = nameId;
        mem.MarkReadable(entry + Offsets.ANameId, 2);
        mem.U8s[entry + Offsets.ACorpseConvertMarker] = 0;
        mem.MarkReadable(entry + Offsets.ACorpseConvertMarker, 1);
        mem.U8s[entry + Offsets.ATurnFlag] = (byte)(turnOpen ? 1 : 0);
        mem.MarkReadable(entry + Offsets.ATurnFlag, 1);

        mem.SeedU64(Offsets.DespawnNodeListHead, (ulong)DespawnNodeAddr);
        mem.MarkReadable(Offsets.DespawnNodeListHead, 8);
        mem.SeedU64(DespawnNodeAddr, 0);   // next pointer: end of list
        mem.U8s[DespawnNodeAddr + Offsets.DespawnNodeIdOff] = 3;
        mem.SeedU64(DespawnNodeAddr + Offsets.DespawnNodeCombatOff, (ulong)DespawnFrame);
        mem.MarkReadable(DespawnNodeAddr, Offsets.DespawnNodeCombatOff + 8);
        mem.MarkWritable(DespawnNodeAddr + Offsets.DespawnNodeModeOff, 1);
        // node flag byte 0 -- BYTE-wide, matching the Proven row's own access to +0x12C. Already
        // covered readable by the node-prefix MarkReadable call above (0x12C < CombatOff+8).
        mem.U8s[DespawnNodeAddr + Offsets.DespawnNodeModeOff] = 0;

        // "not the current actor" -- any id other than the node's own 3.
        mem.U16s[Offsets.DespawnCurrentActorNodeId] = 0xFFFF;
        mem.U16s[Offsets.DespawnCurrentActorNodeId + 2] = 0xFFFF;
        mem.MarkReadable(Offsets.DespawnCurrentActorNodeId, 4);
    }

    [Fact]
    public void RecordPoachDeed_eligible_poach_also_despawns_the_corpse()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));   // the poach itself still landed
        // Both the store write and the despawn write are W8 now (byte-wide), so they share
        // mem.Written -- assert the despawn write specifically rather than the dict's size.
        Assert.True(mem.Written.ContainsKey(DespawnNodeAddr + Offsets.DespawnNodeModeOff));
        Assert.Equal((byte)0x20, mem.Written[DespawnNodeAddr + Offsets.DespawnNodeModeOff]);
    }

    [Fact]
    public void RecordPoachDeed_despawn_refusal_does_not_roll_back_the_store_write()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId, turnOpen: true);   // the despawn's own guard refuses
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));   // the carcass still stands (its own W8 write)
        Assert.False(mem.Written.ContainsKey(DespawnNodeAddr + Offsets.DespawnNodeModeOff));   // but nothing despawned
    }

    // ── Stage-2b: the killer's real Poach-bit read (combat support bitfield) ───

    [Fact]
    public void ReadKillerHasPoach_true_when_the_deployed_wielders_support_bit_is_set()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, 24, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        long entry = Band.Entry(24);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        long addr = entry + Offsets.ASupport + off;
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = mask;

        Assert.True(LivingPoach.ReadKillerHasPoach(mem, weaponId));
    }

    [Fact]
    public void ReadKillerHasPoach_false_when_the_bit_is_clear()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, 24, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        long entry = Band.Entry(24);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        long addr = entry + Offsets.ASupport + off;
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = 0;

        Assert.False(LivingPoach.ReadKillerHasPoach(mem, weaponId));
    }

    [Fact]
    public void ReadKillerHasPoach_false_when_the_wielder_cannot_be_located()
    {
        var mem = new FakeSparseMemory();   // nobody deployed at all
        Assert.False(LivingPoach.ReadKillerHasPoach(mem, weaponId: 71));
    }

    // ── Stage-2c: KillTracker's widened deed seam (CreditKill -> RecordPoachDeed) ──

    private const int WilhamSlot = Offsets.SlotsBack;   // band slot 20, player-side actor

    private sealed class CapturingDeedSink : IDeedSink
    {
        public readonly List<(int weaponId, VictimSnapshot victim)> Deeds = new();
        public readonly List<int> Misses = new();
        public readonly List<(int weaponId, VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)> PoachCalls = new();
        public void RecordDeed(int weaponId, in VictimSnapshot victim) => Deeds.Add((weaponId, victim));
        public void DeedMiss(int slot) => Misses.Add(slot);
        public void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)
            => PoachCalls.Add((weaponId, victim, slot, delayedOrCharged, viaFallback));
    }

    [Fact]
    public void CreditKill_reports_to_RecordPoachDeed_alongside_RecordDeed()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var sink = new CapturingDeedSink();
        var weapons = new HashSet<int> { 52 };
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, WilhamSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, weapons, recorder: null, deeds: sink);

        Settle(t);
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);

        // Whether THIS particular fixture resolves via the acted-latch or the turn-queue fallback
        // is KillTracker's own well-tested attribution call (KillTrackerBattleCountersTests.cs
        // etc.) -- not what this test is proving. What matters here is that CreditKill's widened
        // seam reports the SAME weaponId/slot RecordDeed got, and that the delayed-action flag
        // (no Jump/charge was ever armed in this fixture) reads false.
        var call = Assert.Single(sink.PoachCalls);
        Assert.Equal(52, call.weaponId);
        Assert.Equal(0, call.slot);
        Assert.False(call.delayedOrCharged);
    }

    [Fact]
    public void DeedFanout_forwards_RecordDeed_and_DeedMiss_to_inner_and_RecordPoachDeed_to_LivingPoach()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var inner = new CapturingDeedSink();
        var fan = new DeedFanout(inner, poach);
        var victim = new VictimSnapshot(true, 1, TestVictimJob, false);

        fan.RecordDeed(9, in victim);
        fan.DeedMiss(3);
        fan.RecordPoachDeed(PoachWeaponId, in victim, slot: 0, delayedOrCharged: false, viaFallback: false);

        Assert.Single(inner.Deeds);
        Assert.Equal(9, inner.Deeds[0].weaponId);
        Assert.Single(inner.Misses);
        Assert.Equal(3, inner.Misses[0]);
        Assert.Empty(inner.PoachCalls);   // RecordPoachDeed never reaches the inner (Reliquary-side) sink
        Assert.Equal((byte)1, mem.U8(Offsets.PoachStoreBase + (TestCommonKey - 1)));   // it reached LivingPoach instead
    }

    // ── Stage-4: the real basic-Attack discriminator + the ARMED end-to-end wiring ─────
    //
    // LivingPoach.ReadWasBasicAttack (LIVE_LEDGER "The basic-Attack discriminator (LW-167 stage
    // 4)" row, 2026-08-12): resolves the credited weapon's KILLER via the same lane
    // ReadKillerHasPoach uses, then reads that wielder's own AArec action record. kind==5
    // (performing) + abil==0 means a confirmed basic Attack; anything else fails closed to false.

    private const int DiscBandSlot = 24;

    private static long SeedDiscriminatorKiller(FakeSparseMemory mem, int weaponId)
    {
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, DiscBandSlot, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        return Band.Entry(DiscBandSlot);
    }

    [Fact]
    public void ReadWasBasicAttack_true_when_the_killers_record_is_kind5_performing_abil0()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 0;

        Assert.True(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_record_names_an_ability()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 141;   // Rend Weapon -- the owner-observed live counter-example

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_kind_is_receiving_not_performing()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = 6;   // receiving: a struck unit's stale stamp, not the killer's own
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 0;

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_action_record_is_unreadable()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        SeedDiscriminatorKiller(mem, weaponId);   // AArec bytes deliberately never marked Readable

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_killer_cannot_be_located()
    {
        var mem = new FakeSparseMemory();   // nobody deployed at all
        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId: 71));
    }

    /// <summary>Wires the SAME delegate shape Engine.cs uses (both LivingPoach static readers over
    /// live memory, roll pinned to Common so the store address under test is deterministic) --
    /// the ARMED production wiring, not an injected stand-in.</summary>
    private static LivingPoach MakeArmedPoach(FakeSparseMemory mem, PoachMap map, Dictionary<int, WeaponMeta> meta)
    {
        var toast = new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        return new LivingPoach(meta, map, mem, toast,
            killerHasPoach: id => LivingPoach.ReadKillerHasPoach(mem, id),
            wasBasicAttack: id => LivingPoach.ReadWasBasicAttack(mem, id),
            roll: () => 0);
    }

    private static void SeedArmedKiller(FakeSparseMemory mem, int weaponId, ushort abil)
    {
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        mem.ReadableAddrs.Add(entry + Offsets.ASupport + off);
        mem.U8s[entry + Offsets.ASupport + off] = mask;   // killerHasPoach true
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = abil;
    }

    // KEYSTONE POSITIVE: the first reachable-armed happy path. Confirmed RED against the disarmed
    // wiring (wasBasicAttack forced to a constant false, mirroring the pre-stage-4 Engine.cs) before
    // switching MakeArmedPoach's wasBasicAttack delegate to the real ReadWasBasicAttack -- with the
    // constant false the store stayed at 0 (mem.Written empty); with the real discriminator wired
    // in, unchanged otherwise, it writes. This is what proves the discriminator (not some other
    // gate) is what flips the result.
    [Fact]
    public void RecordPoachDeed_ARMED_fully_eligible_credit_with_attack_stamp_writes_the_store()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        SeedArmedKiller(mem, PoachWeaponId, abil: 0);   // confirmed basic Attack
        var poach = MakeArmedPoach(mem, map, meta);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));
    }

    [Fact]
    public void RecordPoachDeed_ARMED_ability_stamp_killer_record_never_writes()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        SeedArmedKiller(mem, PoachWeaponId, abil: 141);   // Rend Weapon: a real ability, not the basic Attack
        var poach = MakeArmedPoach(mem, map, meta);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        Assert.Empty(mem.Written);
    }

    // Kill-shape refusals (deliverable 2): override the discriminator outright. killerHasPoach and
    // wasBasicAttack both inject true (MakePoach's defaults) so a failure here can only be the
    // viaFallback/delayedOrCharged guard, never a starved upstream gate.

    [Fact]
    public void RecordPoachDeed_viaFallback_true_never_writes_even_with_every_other_gate_green()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: true);

        Assert.Empty(mem.Written);
    }

    [Fact]
    public void RecordPoachDeed_delayedOrCharged_true_never_writes_even_with_every_other_gate_green()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestSpecies, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: true, viaFallback: false);

        Assert.Empty(mem.Written);
    }
}
