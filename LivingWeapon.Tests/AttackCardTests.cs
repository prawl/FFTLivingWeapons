using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCard is LW-31 stage 3's runtime painter: it censuses the heap for the shared "Attack"
/// table copies (AttackCardTableFixture mirrors the packed layout AttackCardSpike's census
/// proved, now paired with the 36-byte record AttackCardMemory.AddAttackTable plants at
/// LabelAddr - AttackRow.RecordGap), resolves the acting unit via the CURSOR ONLY (the register
/// fallback died 2026-07-06 after an owner-observed wrong-weapon display; see AttackCard.cs's
/// class doc), and renames the ROW ITSELF (name + trimmed tier suffix, or "Fists") while writing
/// the tier-meter tail into the SAME desc footprint, via the three-way anchor discipline
/// (vanilla/current/previous), now compared as 74-byte IMAGES.
/// </summary>
public class AttackCardTests
{
    private const int WindrunnerId = 501;
    private const int StormcallerId = 502;
    private const int ZwillId = 503;   // the owner's own cited worst-case name (21 chars)
    private const int PeacemakerId = 504;   // carries a Signature, for the locked/earned tease tests

    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    /// <summary>Seat a player: a roster slot (main-hand weaponId) plus its matching band entry
    /// and frame nameId, at a distinct (level,brave,faith) fingerprint per slot/bandIdx pair.
    /// <paramref name="sprite"/> defaults to 0 (an ordinary human, AttackRow.Policy.HumanSprite).
    /// LW-55: also seats the band's own turn-flag byte (Offsets.ATurnFlag) at 1, the genuine
    /// turn-owner value CursorGate's gate B requires before ANY dossier composes (mirrors
    /// MushinTests.SetFlags' own seating of the same byte); MemSeats.SeatBand itself is NOT given
    /// a default (shared suite; MushinTests drives that byte through its own helper).</summary>
    private static void SeatPlayer(FakeSparseMemory m, int rosterSlot, int bandIdx, int weaponId,
                                   int lvl, int br, int fa, int nameId, int sprite = 0)
    {
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: weaponId, nameId: nameId, sprite: sprite);
        MemSeats.SeatBand(m, bandIdx, weapon: weaponId, lvl: lvl, br: br, fa: fa, gx: bandIdx, gy: bandIdx);
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);
        m.U8s[Band.Entry(bandIdx) + Offsets.ATurnFlag] = 1;
    }

    private static Dictionary<int, WeaponMeta> Meta() => new()
    {
        [WindrunnerId] = new WeaponMeta { Name = "Windrunner", Wp = 10, Cat = "Bow", Formula = 1 },
        [StormcallerId] = new WeaponMeta { Name = "Stormcaller", Wp = 12, Cat = "Rod", Formula = 2 },
        [ZwillId] = new WeaponMeta { Name = "Zwill Straightblade", Wp = 14, Cat = "Knight Sword", Formula = 1 },
        [PeacemakerId] = new WeaponMeta
        {
            Name = "Peacemaker", Wp = 8, Cat = "Gun", Formula = 1,
            Signature = new WeaponSignature { DisplayLabel = "Gun Slinger", AtTier = 3 },
        },
    };

    /// <summary>The row-name segment of a table copy's footprint: the first NUL-terminated run
    /// starting at the desc position: the RENAMED row text (or the full vanilla desc, which has
    /// no embedded NUL until its own terminator, when the plan is vanilla).</summary>
    private static string RowOf(byte[] regionBytes, int enc)
    {
        int descPos = AttackCardProbeText.DescStart(AttackCardTableFixture.PadBefore, enc);
        var (text, _) = AttackCardProbeText.ReadDesc(regionBytes, descPos, enc, AttackCardText.DefaultBudgetChars);
        return text;
    }

    /// <summary>The tail segment following a <paramref name="rowNameChars"/>-char row name and its
    /// separating NUL.</summary>
    private static string TailOf(byte[] regionBytes, int enc, int rowNameChars)
    {
        int descPos = AttackCardProbeText.DescStart(AttackCardTableFixture.PadBefore, enc);
        int tailStart = descPos + (rowNameChars + 1) * enc;
        var (text, _) = AttackCardProbeText.ReadDesc(regionBytes, tailStart, enc, AttackCardText.DefaultBudgetChars);
        return text;
    }

    /// <summary>The raw 74-byte footprint (name/NUL/tail/NUL-padding, or the flat vanilla image).</summary>
    private static byte[] FootprintOf(byte[] regionBytes, int enc)
    {
        int descPos = AttackCardProbeText.DescStart(AttackCardTableFixture.PadBefore, enc);
        var buf = new byte[AttackRow.FootprintBytes];
        Array.Copy(regionBytes, descPos, buf, 0, AttackRow.FootprintBytes);
        return buf;
    }

    private sealed class Rig
    {
        public AttackCardMemory Mem = null!;
        public ActorRegister Register = null!;
        public ActorResolver Resolver = null!;
        public AttackCard Card = null!;
        public Dictionary<int, int> Kills = null!;
        // LW-55: every recorder call the tripwire makes (AttackCard.Resolve.cs's ReportRefusal),
        // in order, so tests can assert on flight-record content/count directly.
        public List<(string Type, string Payload)> Recorded = null!;
    }

    private static Rig Build(Dictionary<int, int>? kills = null)
    {
        var mem = new AttackCardMemory();
        var meta = Meta();
        var weapons = new HashSet<int>(meta.Keys);
        var register = new ActorRegister(mem);
        var resolver = new ActorResolver(mem, weapons, register);
        // Kept below Tuning.ProdThresholds[0] (5) so both weapons compose at tier 0 (an empty,
        // trimmed-away suffix) unless a test overrides it; the tier-suffix mechanism itself is
        // AttackRow.Policy's own concern (AttackRowPolicyTests), not this runtime suite's.
        var k = kills ?? new Dictionary<int, int> { [WindrunnerId] = 3, [StormcallerId] = 4 };
        Func<CursorAnswer?> resolveCursor = () =>
            resolver.TryResolveCursorPlayer(out var answer) ? answer : (CursorAnswer?)null;
        var recorded = new List<(string Type, string Payload)>();
        var card = new AttackCard(mem, resolveCursor, resolver.SpriteOf, meta, k,
                                   recorder: (t, p) => recorded.Add((t, p)));
        return new Rig { Mem = mem, Register = register, Resolver = resolver, Card = card, Kills = k, Recorded = recorded };
    }

    /// <summary>Drive Arm + StepScan to completion (tiny test regions always finish within a
    /// couple of ticks) plus at least one RepaintDriver pass. Extra ticks once settled are
    /// harmless (RepaintDriver is idempotent when nothing changed).</summary>
    private static void Settle(AttackCard card, int ticks = 5)
    {
        for (int i = 0; i < ticks; i++) card.Tick();
    }

    /// <summary>Open a player's turn the CURSOR-ONLY resolve can see (owner-observed wrong-weapon
    /// display 2026-07-06: the register fallback is gone from this surface): seats the condensed
    /// turn-queue struct at the unit's (level, hp, maxHp) fingerprint. SeatBand's hp/maxHp default
    /// is 100/100, so a distinct LEVEL per seated player keeps the band match unambiguous.</summary>
    private static void OpenTurn(Rig rig, int level) =>
        rig.Mem.SeatCursor(team: 0, level: level, hp: 100, maxHp: 100);

    [Fact]
    public void Census_finds_the_attack_table_and_caches_it()
    {
        var rig = Build();
        rig.Mem.AddHeapRegion(0x7000000000, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void First_paint_renames_the_row_and_writes_the_kills_tail()
    {
        var rig = Build();
        long baseAscii = 0x7000000000;
        long baseUtf16 = 0x7000100000;
        rig.Mem.AddAttackTable(baseAscii, 1, AttackCardText.VanillaDesc);
        rig.Mem.AddAttackTable(baseUtf16, 2, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)

        Settle(rig.Card);

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(baseAscii), 1));
        Assert.Equal("Kills: 3/5 to +", TailOf(rig.Mem.RegionBytes(baseAscii), 1, "Windrunner".Length));

        // enc2 catalogs never participate in the split-image mechanism at all (dead path, see
        // AttackCard.cs's class doc): this copy stays vanilla forever, even while the ascii copy
        // is actively renamed every tick.
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(baseUtf16), 2));
    }

    [Fact]
    public void Turn_owner_change_repaints_the_shared_table_with_the_new_weapons_line()
    {
        // Two physical table copies (the "shared table", about six live per launch in production)
        // both track the acting unit's row+tail as it changes.
        var rig = Build();
        long copyA = 0x7000000000;
        long copyB = 0x7000100000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);
        rig.Mem.AddAttackTable(copyB, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);

        OpenTurn(rig, level: 50);       // Windrunner's turn opens
        Settle(rig.Card);

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copyB), 1));
        Assert.Equal("Kills: 3/5 to +", TailOf(rig.Mem.RegionBytes(copyA), 1, "Windrunner".Length));

        // Turn-owner change: the cursor snaps to slot 3 (Stormcaller).
        OpenTurn(rig, level: 55);
        Settle(rig.Card, ticks: 1);

        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(copyB), 1));
        Assert.Equal("Kills: 4/5 to +", TailOf(rig.Mem.RegionBytes(copyA), 1, "Stormcaller".Length));
        Assert.Equal("Kills: 4/5 to +", TailOf(rig.Mem.RegionBytes(copyB), 1, "Stormcaller".Length));
        // Repainted forward, never evicted: both copies (which held the now-"previous" Windrunner
        // image the instant this repaint ran) are still cached, not dropped as foreign.
        Assert.Equal(2, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Mid_battle_recensus_of_a_still_painted_copy_does_not_block_the_battle_exit_vanilla_restore()
    {
        // F1 regression: a re-census (the same kind any live eviction/RepaintAll triggers) that
        // finds a copy still holding OUR OWN split image must not evict/misclassify it; ResetBattle
        // must still restore both the record's offsets AND the vanilla text afterward.
        var rig = Build();
        long copyA = 0x7000000000;
        long copyB = 0x7000100000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);
        rig.Mem.AddAttackTable(copyB, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens

        Settle(rig.Card);   // first census + first paint

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copyB), 1));

        // Force the same kind of mid-battle re-census any live eviction triggers, while both
        // copies still hold our own split image, not vanilla.
        rig.Card.ForceRecensusForTests();
        Settle(rig.Card);

        rig.Card.ResetBattle();   // battle exit: best-effort vanilla restore

        byte[] expectedImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        foreach (long copy in new[] { copyA, copyB })
        {
            Assert.Equal(expectedImage, FootprintOf(rig.Mem.RegionBytes(copy), 1));
            Assert.Equal(AttackRow.VanillaOffsetBytes, rig.Mem.RegionBytes(AttackCardMemory.RecordAddrFor(copy, 1))[..8]);
        }
    }

    [Fact]
    public void A_boundary_truncated_partial_desc_read_is_never_cached_with_a_bogus_footprint()
    {
        // Same-family edge (F1): a hit whose desc read stops early because the underlying REGION
        // itself ends shortly past the label (a genuine region-end truncation) reads a genuine
        // PARTIAL prefix, not any known image. It must stay foreign and uncached, never poisoning a
        // footprint with a partial length; and, unlike enc1's normal path, it has no record region
        // planted for it either, so Classify would find it Unreadable regardless.
        var rig = Build();
        long goodCopy = 0x7000000000;
        long truncatedCopy = 0x7000100000;
        rig.Mem.AddAttackTable(goodCopy, 1, AttackCardText.VanillaDesc);

        byte[] label = ByteScan.Enc("Attack", 1);
        var partial = new byte[8 + label.Length + 1 + 20];   // lead zeros + label + NUL + 20 real chars, no terminator
        Array.Copy(label, 0, partial, 8, label.Length);
        byte[] prefix = ByteScan.Enc(AttackCardText.VanillaDesc.Substring(0, 20), 1);
        Array.Copy(prefix, 0, partial, 8 + label.Length + 1, prefix.Length);
        rig.Mem.AddHeapRegion(truncatedCopy, partial);
        byte[] truncatedBefore = (byte[])partial.Clone();

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);   // only the good copy is cached
        Assert.Equal(truncatedBefore, rig.Mem.RegionBytes(truncatedCopy));   // byte-for-byte untouched
    }

    [Fact]
    public void Unarmed_human_owner_shows_fist()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        // A second player, band-confirmed UNARMED (RRHand = the empty-hand sentinel) on an ordinary
        // human sprite (default 0): a real player turn with no weapon at all earns "Fists", not a
        // vanilla restore (goal rule: unarmed HUMAN -> "Fists"). Both sides sentinel-agree as
        // unarmed (LW-55 gate A: None) and the turn flag is seated 1 (gate B: None).
        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 4, lvl: 45, br: 40, fa: 50, rh: 0xFFFF, nameId: 44, sprite: 0x80);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 7, weapon: 0xFFFF, lvl: 45, br: 40, fa: 50, gx: 7, gy: 7);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 7, nameId: 44);
        rig.Mem.Sparse.U8s[Band.Entry(7) + Offsets.ATurnFlag] = 1;
        OpenTurn(rig, level: 45);       // the unarmed unit's turn opens
        Settle(rig.Card, ticks: 1);

        Assert.Equal("Fists", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal(AttackCardTail.FistTail, TailOf(rig.Mem.RegionBytes(copy), 1, "Fists".Length));
    }

    [Fact]
    public void Unarmed_monster_owner_restores_vanilla_byte_exact()
    {
        // MONSTER (owner rule): never renamed, vanilla everything, even with an empty hand.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 4, lvl: 45, br: 40, fa: 50, rh: 0xFFFF, nameId: 44, sprite: 0x82);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 7, weapon: 0xFFFF, lvl: 45, br: 40, fa: 50, gx: 7, gy: 7);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 7, nameId: 44);
        OpenTurn(rig, level: 45);       // the unarmed unit's turn opens
        Settle(rig.Card, ticks: 1);

        byte[] expected = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        Assert.Equal(expected, FootprintOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void A_foreign_buffer_is_never_written()
    {
        var rig = Build();
        long goodCopy = 0x7000000000;
        long foreignCopy = 0x7000100000;
        rig.Mem.AddAttackTable(goodCopy, 1, AttackCardText.VanillaDesc);
        // A standalone "Attack" label whose desc is unrelated prose (and no record region at all):
        // never vanilla, current, or previous. Must never be cached or written.
        rig.Mem.AddHeapRegion(foreignCopy, AttackCardTableFixture.Build(1, "Some other command's description text."));
        byte[] foreignBefore = rig.Mem.RegionBytes(foreignCopy);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);   // only the good copy is cached
        var (labelAddr, descAddr) = AttackCardTableFixture.Addrs(foreignCopy, 1);
        Assert.DoesNotContain(descAddr, rig.Mem.WrittenAddrs);
        Assert.Equal(foreignBefore, rig.Mem.RegionBytes(foreignCopy));   // byte-for-byte untouched
    }

    [Fact]
    public void ResetBattle_restores_vanilla_and_keeps_the_warm_cache()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        rig.Card.ResetBattle();

        Assert.Equal(1, rig.Card.HitCountForTests);
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal(AttackRow.VanillaOffsetBytes, rig.Mem.RegionBytes(AttackCardMemory.RecordAddrFor(copy, 1))[..8]);
    }

    [Fact]
    public void Warm_cache_survives_ResetBattle_and_repaints_on_the_first_tick_of_the_next_battle()
    {
        // LW-38: the cache stays warm across ResetBattle, so the next battle's first repaint is an
        // instant SyncHit re-validate off the existing cache entry, never a fresh multi-tick census.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        rig.Card.ResetBattle();

        Assert.Equal(1, rig.Card.HitCountForTests);
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));

        // Battle 2: Windrunner's turn opens again. A SINGLE tick, no census slack at all, is
        // enough to repaint the row, proving the cache from battle 1 was never dropped.
        OpenTurn(rig, level: 50);
        rig.Card.Tick();

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Stale_cached_copy_evicts_and_re_arms_a_full_census_on_the_next_battle()
    {
        // Battle 1 caches copyA and ResetBattle keeps it warm. Before battle 2 gets a chance to
        // re-validate it, the underlying buffer itself went stale (a freed/reused allocation, the
        // same real-world shape SyncHit's label re-verify already guards against). A second,
        // genuinely live copy (copyB) exists by the time battle 2's first repaint runs.
        var rig = Build();
        long copyA = 0x7000000000;
        long copyB = 0x7000100000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);
        Settle(rig.Card);
        Assert.Equal(1, rig.Card.HitCountForTests);

        rig.Card.ResetBattle();
        Assert.Equal(1, rig.Card.HitCountForTests);   // the warm cache survives the reset

        var (labelAddr, _) = AttackCardTableFixture.Addrs(copyA, 1);
        rig.Mem.WriteBytes(labelAddr, new byte[] { 1, 2, 3, 4, 5, 6 });   // corrupt the label bytes directly
        rig.Mem.AddAttackTable(copyB, 1, AttackCardText.VanillaDesc);     // battle 2's own live copy
        // No player turn open yet at battle 2's start (an enemy's turn, same "no cursor answer"
        // convention as Enemy_team_cursor_composes_vanilla): the desired plan stays vanilla, so
        // the eviction and re-census below are the ONLY things happening this pass.
        rig.Mem.SeatCursor(team: 1, level: 50, hp: 100, maxHp: 100);
        int writesBefore = rig.Mem.WrittenAddrs.Count;

        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);                    // copyA evicted, copyB found and cached
        Assert.Equal(writesBefore, rig.Mem.WrittenAddrs.Count);        // eviction + fresh vanilla census: zero writes
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copyB), 1));   // the re-census located it
    }

    [Fact]
    public void ResetBattle_with_an_empty_cache_re_arms_the_census()
    {
        var rig = Build();
        Settle(rig.Card);   // an empty first census: no table exists yet, so the cache stays empty
        Assert.Equal(0, rig.Card.HitCountForTests);

        rig.Card.ResetBattle();

        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Battle_edge_that_aborts_an_in_flight_sweep_still_re_arms_next_battle()
    {
        // LW-57, the load-bearing negative: reproduces the 2026-07-11 live log. A battle edge that
        // lands mid sweep (after Arm, before Finish) must not leave the partial cache masquerading
        // as a complete census: without the !_sweepCompleted term, battle 2's copyB is never found.
        var rig = Build();
        long copyA = 0x7000000000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);

        Settle(rig.Card);   // battle 1: census adopts copyA, sweep completes
        Assert.Equal(1, rig.Card.HitCountForTests);

        rig.Card.ForceRecensusForTests();
        rig.Card.Tick();   // Arm only: sweep now in flight, cache still warm (Arm no longer clears it)

        rig.Card.ResetBattle();   // the abort: no Finish ever ran this sweep

        long copyB = 0x7000100000;
        rig.Mem.AddAttackTable(copyB, 1, AttackCardText.VanillaDesc);

        Settle(rig.Card);   // battle 2

        Assert.Equal(2, rig.Card.HitCountForTests);   // copyB adopted by the re-armed census
    }

    [Fact]
    public void A_repaint_lands_mid_sweep_without_waiting_for_the_sweep_to_finish()
    {
        // LW-57: a sweep must not starve the repaint driver, or the session's first battle shows
        // the vanilla Attack row for the whole census. Alternation lets a repaint land WHILE the
        // sweep is still in flight (HitCountForTests unchanged, sweep not yet Finished).
        var rig = Build();
        long copyA = 0x7000000000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copyA), 1));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);

        rig.Card.ForceRecensusForTests();
        rig.Card.Tick();   // Arm only: phase is pinned to REPAINT next

        OpenTurn(rig, level: 55);   // flip the desired compose to Stormcaller mid-sweep

        rig.Card.Tick();   // repaint phase: lands the new row even though the sweep is still in flight
        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(copyA), 1));

        rig.Card.Tick();   // scan phase: completes the sweep
        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Empty_cache_sweep_scans_every_tick_with_no_repaint_phase_interposed()
    {
        var rig = Build();

        rig.Card.Tick();   // Arm: cache is empty, nothing to census yet
        rig.Card.Tick();   // an empty-cache scan tick finishes the census outright

        Assert.Equal(0, rig.Card.HitCountForTests);

        // A copy shows up after that census already finished; the next battle's Settle re-arms and
        // finds it, proving the second tick above really did Finish (not get stuck alternating).
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);
        rig.Card.ResetBattle();
        Settle(rig.Card);
        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Preserved_hits_re_census_does_not_duplicate_or_write()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        int writesBefore = rig.Mem.WrittenAddrs.Count;
        rig.Card.ForceRecensusForTests();
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);   // no duplicate entry for the same copy
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal(writesBefore, rig.Mem.WrittenAddrs.Count);   // skip-if-equal: zero writes
    }

    [Fact]
    public void Eviction_after_a_completed_census_still_re_censuses_across_a_battle_edge()
    {
        // Guards the `_needsCensus ||` term: an eviction mid-battle sets _needsCensus, but nothing
        // ticks it into an Arm before ResetBattle fires. The pending re-census must survive the
        // battle edge (a completed prior sweep must not mask it via !_sweepCompleted alone).
        var rig = Build();
        long copyA = 0x7000000000;
        rig.Mem.AddAttackTable(copyA, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);
        Settle(rig.Card);
        Assert.Equal(1, rig.Card.HitCountForTests);

        var (labelAddr, _) = AttackCardTableFixture.Addrs(copyA, 1);
        rig.Mem.WriteBytes(labelAddr, new byte[] { 1, 2, 3, 4, 5, 6 });   // corrupt the label bytes directly

        // Force RepaintAll to notice and evict, WITHOUT letting a subsequent Tick arm the resulting
        // _needsCensus (a resolve change is what drives RepaintAll's own eviction check).
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        OpenTurn(rig, level: 55);
        rig.Card.Tick();   // RepaintDriver sees the resolve change, RepaintAll evicts copyA, sets _needsCensus

        Assert.Equal(0, rig.Card.HitCountForTests);

        rig.Card.ResetBattle();   // battle edge lands BEFORE any Tick arms the pending census

        long copyB = 0x7000100000;
        rig.Mem.AddAttackTable(copyB, 1, AttackCardText.VanillaDesc);
        // No player turn open yet at battle 2's start (an enemy's turn, the stale-cache test's own
        // convention): otherwise Stormcaller's battle-1 cursor legitimately paints copyB the moment
        // the census adopts it (SyncHit-on-discovery). Vanilla compose keeps adoption the ONLY
        // observable thing here.
        rig.Mem.SeatCursor(team: 1, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);   // battle 2

        Assert.Equal(1, rig.Card.HitCountForTests);   // copyB adopted: the pending re-census survived the edge
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copyB), 1));
    }

    // LW-31: CURSOR-ONLY resolve (owner-observed wrong-weapon display 2026-07-06). The register
    // fallback stage 2 kept is GONE from this surface: when the cursor does not answer, the plan
    // is vanilla, full stop. The four guard-failure tests below pin that fail-closed rule (they
    // are the flipped descendants of the old falls-back-to-the-register tests).

    [Fact]
    public void Cursor_leads_the_stale_register_to_the_unit_whose_turn_just_opened()
    {
        // THE bug the stage-2 fix closed: the register is still parked on the PREVIOUS actor
        // (Windrunner), it never sees Stormcaller act, while the cursor already names Stormcaller,
        // whose turn has just opened.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();          // priming
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // arrival: slot 2 (Windrunner), the register's ONLY update this test

        // Stormcaller's turn has opened (the cursor snaps to it) but they have not acted; the
        // register never moves off Windrunner.
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 4/5 to +", TailOf(rig.Mem.RegionBytes(copy), 1, "Stormcaller".Length));
    }

    [Fact]
    public void Enemy_team_cursor_composes_vanilla_never_the_registers_answer()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // the register WOULD answer Windrunner; it must not be consulted

        // An enemy's turn is open (team=1): no cursor answer, so the plan is vanilla, full stop.
        rig.Mem.SeatCursor(team: 1, level: 50, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_nameId_disagreement_composes_vanilla_never_the_registers_answer()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // the register WOULD answer Windrunner; it must not be consulted

        // level/hp/maxHp fingerprint-match Stormcaller's band entry, but its frame nameId is
        // overwritten to a value no roster slot carries (the flicker): the cursor refuses, and a
        // refusal now means vanilla (the register's stale answer was exactly the live bug).
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 6, nameId: 999);
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_with_no_roster_match_composes_vanilla_never_the_registers_answer()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // the register WOULD answer Windrunner; it must not be consulted

        // A band entry fingerprint-matches the cursor's level/hp/maxHp, but no roster slot bridges
        // to it at all; the cursor never guesses a roster identity it cannot confirm, and its
        // refusal now means vanilla.
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 9, weapon: StormcallerId, lvl: 55, br: 65, fa: 75, gx: 9, gy: 9);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 9, nameId: 77);   // no roster slot has nameId 77
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Unreadable_cursor_struct_composes_vanilla_never_the_registers_answer()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // the register WOULD answer Windrunner; it must not be consulted

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        // Values planted directly WITHOUT marking Offsets.TurnQueue Readable (FakeSparseMemory's
        // Readable contract is an exact-address set, see its class doc): mirrors a genuine
        // unreadable struct address. The cursor path must fail closed rather than trust a read it
        // cannot confirm succeeded, and failing closed now means vanilla.
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 0;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 55;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;

        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_and_register_both_absent_composes_vanilla()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        // Neither the register (no Update() arrivals at all) nor the cursor (never seeded) has an
        // answer: the dossier must stay vanilla. No record region needed: the desired plan never
        // leaves vanilla, so nothing is ever written.
        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
    }

    // LW-33: SyncHit's own footprint repair, inert for the split-image path (every image fits
    // AttackRow.FootprintBytes by construction) but kept as a regression guard: poisoning the
    // cached footprint must never block a correct repaint.

    [Fact]
    public void A_poisoned_footprint_never_blocks_a_correct_repaint()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);   // first census + paint: renames to "Windrunner" / "Kills: 3/5 to +"

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        // Poison the cached footprint down to an arbitrarily short value.
        rig.Card.PoisonFirstHitFootprintForTests(5);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        OpenTurn(rig, level: 55);       // Stormcaller's turn opens
        Settle(rig.Card, ticks: 1);

        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 4/5 to +", TailOf(rig.Mem.RegionBytes(copy), 1, "Stormcaller".Length));
    }

    // ---- LW-31 stage 3: the row-rename mechanism itself ----

    [Fact]
    public void LOAD_BEARING_the_split_image_and_offset_bytes_are_written_byte_exact_and_nothing_else_moves()
    {
        // The owner's own cited worst-case name, "Zwill Straightblade+3" (21 chars), at tier 3
        // (kills >= Tuning.ProdThresholds[2] == 50).
        var rig = Build(kills: new Dictionary<int, int> { [ZwillId] = 50 });
        long baseAddr = 0x7000000000;
        rig.Mem.AddAttackTable(baseAddr, 1, AttackCardText.VanillaDesc);
        var (labelAddr, descAddr) = AttackCardTableFixture.Addrs(baseAddr, 1);
        long recordAddr = AttackCardMemory.RecordAddrFor(baseAddr, 1);

        byte[] labelBefore = rig.Mem.RegionBytes(baseAddr)[..(AttackCardTableFixture.PadBefore + 7)];
        byte[] recordBefore = rig.Mem.RegionBytes(recordAddr);
        byte byteAfterFootprintBefore = rig.Mem.RegionBytes(baseAddr)[AttackCardTableFixture.PadBefore + 7 + AttackRow.FootprintBytes];

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: ZwillId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // the Zwill wielder's turn opens (cursor-only resolve)

        Settle(rig.Card);

        const string expectedRowName = "Zwill Straightblade+3";
        Assert.Equal(21, expectedRowName.Length);
        const string expectedTail = "Kills: 50";

        // EXACT 74 bytes at labelAddr+7: name, NUL, tail, NUL padding to the full footprint.
        byte[] expectedImage = new byte[AttackRow.FootprintBytes];
        System.Text.Encoding.ASCII.GetBytes(expectedRowName).CopyTo(expectedImage, 0);
        System.Text.Encoding.ASCII.GetBytes(expectedTail).CopyTo(expectedImage, expectedRowName.Length + 1);
        byte[] actualImage = new byte[AttackRow.FootprintBytes];
        Array.Copy(rig.Mem.RegionBytes(baseAddr), AttackCardTableFixture.PadBefore + 7, actualImage, 0, AttackRow.FootprintBytes);
        Assert.Equal(expectedImage, actualImage);
        Assert.Equal(expectedRowName, RowOf(rig.Mem.RegionBytes(baseAddr), 1));
        Assert.Equal(expectedTail, TailOf(rig.Mem.RegionBytes(baseAddr), 1, expectedRowName.Length));

        // EXACT 8 bytes at labelAddr-0x1FC1: {0x1FC8, 0x1FC8+22}.
        byte[] recordAfter = rig.Mem.RegionBytes(recordAddr);
        Assert.Equal(BitConverter.GetBytes(0x1FC8u), recordAfter[..4]);
        Assert.Equal(BitConverter.GetBytes(0x1FC8u + 22u), recordAfter[4..8]);
        Assert.Equal(0x1FC1, (int)(labelAddr - recordAddr));

        // Label bytes, id field, poolOff field, and the byte at footprint+74 stay UNTOUCHED.
        Assert.Equal(labelBefore, rig.Mem.RegionBytes(baseAddr)[..(AttackCardTableFixture.PadBefore + 7)]);
        Assert.Equal(recordBefore[8..16], recordAfter[8..16]);   // poolOff + id fields
        Assert.Equal(byteAfterFootprintBefore, rig.Mem.RegionBytes(baseAddr)[AttackCardTableFixture.PadBefore + 7 + AttackRow.FootprintBytes]);
    }

    [Fact]
    public void Shape_based_restore_fixes_a_two_generations_stale_Ours_record_on_ResetBattle()
    {
        // A record already in "Ours" shape (a real earlier paint), whose desc footprint is then
        // stomped externally to something matching NEITHER the current nor the previous composed
        // image (simulating a copy that drifted stale between this method's own SyncHit passes):
        // ResetBattle's Restore call is the "strand killer": it fixes BOTH the offsets and the
        // vanilla text regardless of what the text currently says, because Classify still reads
        // Ours off the UNTOUCHED record fields.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);   // paints "Windrunner": the record is now genuinely Ours-shaped
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        var (_, descAddr) = AttackCardTableFixture.Addrs(copy, 1);
        byte[] staleForeignImage = AttackRow.BuildImage("SomeOtherWeapon", "Kills: 999.");
        rig.Mem.WriteBytes(descAddr, staleForeignImage);   // external drift, bypassing AttackCard entirely

        rig.Card.ResetBattle();

        Assert.Equal(AttackRow.VanillaOffsetBytes, rig.Mem.RegionBytes(AttackCardMemory.RecordAddrFor(copy, 1))[..8]);
        byte[] expectedImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        Assert.Equal(expectedImage, FootprintOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Label_gone_eviction_performs_zero_writes()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal(1, rig.Card.HitCountForTests);

        var (labelAddr, _) = AttackCardTableFixture.Addrs(copy, 1);
        rig.Mem.WriteBytes(labelAddr, new byte[] { 1, 2, 3, 4, 5, 6 });   // corrupt the label bytes directly
        int writesBefore = rig.Mem.WrittenAddrs.Count;

        // A resolve CHANGE forces RepaintAll to run immediately (bypassing the maintenance
        // throttle), iterating the already-cached hit list directly: SyncHit's very first check
        // (the label re-verify) must fail closed before touching anything else.
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        OpenTurn(rig, level: 55);       // Stormcaller's turn opens
        Settle(rig.Card, ticks: 1);

        Assert.Equal(writesBefore, rig.Mem.WrittenAddrs.Count);   // zero additional writes
        Assert.Equal(0, rig.Card.HitCountForTests);               // evicted
    }

    [Fact]
    public void Foreign_desc_eviction_on_an_Ours_record_restores_the_record_first()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        var (_, descAddr) = AttackCardTableFixture.Addrs(copy, 1);
        rig.Mem.WriteBytes(descAddr, AttackRow.BuildImage("ForeignThing", "Unrelated."));   // stomp the text only; the record stays Ours

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        OpenTurn(rig, level: 55);       // Stormcaller's turn opens
        Settle(rig.Card, ticks: 1);

        // Evicted (a foreign footprint is never adopted forward)...
        Assert.Equal(0, rig.Card.HitCountForTests);
        // ...but the record was restored to vanilla FIRST, since Classify still read Ours off the
        // untouched record fields (SyncHit's eviction branch: "Classify==Ours -> Restore first").
        Assert.Equal(AttackRow.VanillaOffsetBytes, rig.Mem.RegionBytes(AttackCardMemory.RecordAddrFor(copy, 1))[..8]);
        byte[] expectedImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        Assert.Equal(expectedImage, FootprintOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Re_census_adopts_an_Ours_shaped_copy_still_holding_the_current_image_with_no_writes()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        int writesBefore = rig.Mem.WrittenAddrs.Count;
        rig.Card.ForceRecensusForTests();   // re-arms a census (Arm no longer clears the cache) without touching any live copy
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);              // re-adopted
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));   // unchanged
        Assert.Equal(writesBefore, rig.Mem.WrittenAddrs.Count);   // skip-if-equal: zero writes
    }

    [Fact]
    public void Enc2_copy_is_never_poked_vanilla_restore_only()
    {
        // enc==2 catalogs never participate in the split-image mechanism at all: no record write
        // ever targets them, and their text is untouched as long as it already reads vanilla --
        // even while the enc1 sibling is actively renamed across multiple turn-owner changes.
        var rig = Build();
        long asciiCopy = 0x7000000000;
        long utf16Copy = 0x7000100000;
        rig.Mem.AddAttackTable(asciiCopy, 1, AttackCardText.VanillaDesc);
        rig.Mem.AddAttackTable(utf16Copy, 2, AttackCardText.VanillaDesc);
        byte[] utf16Before = rig.Mem.RegionBytes(utf16Copy);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        OpenTurn(rig, level: 55);       // Stormcaller's turn opens
        Settle(rig.Card, ticks: 1);

        Assert.Equal("Stormcaller", RowOf(rig.Mem.RegionBytes(asciiCopy), 1));   // the ascii sibling did change
        Assert.Equal(utf16Before, rig.Mem.RegionBytes(utf16Copy));               // the utf16 copy never moved
        var (_, utf16DescAddr) = AttackCardTableFixture.Addrs(utf16Copy, 2);
        Assert.DoesNotContain(utf16DescAddr, rig.Mem.WrittenAddrs);
        long utf16WouldBeRecordAddr = AttackCardMemory.RecordAddrFor(utf16Copy, 1);   // the enc1 record formula, applied hypothetically
        Assert.DoesNotContain(utf16WouldBeRecordAddr, rig.Mem.WrittenAddrs);
    }

    // ---- CHANGE 3 (owner decision 2026-07-06): the signature tease clause, wired end to end ----
    // DISABLED 2026-07-07 (owner): AttackCard.Resolve.cs still computes sigLabel/sigEarned
    // unchanged (a one-line revert away from re-enabling), but AttackCardTail.ComposeTail never
    // renders the clause now, so these two assert the plain meter head reaches the card.

    [Fact]
    public void A_weapon_with_a_locked_signature_shows_the_unlocks_tease_on_the_card()
    {
        // Matches the owner's own worked "locked" example (CHANGE 3, 2026-07-06): Peacemaker's
        // Signature.AtTier is 3 (Tuning.ProdThresholds[2] == 50); 34 kills sits at tier 2
        // (25 <= 34 < 50), short of it. The tease clause is disabled (owner 2026-07-07), so only
        // the meter head reaches the card now.
        var rig = Build(kills: new Dictionary<int, int> { [PeacemakerId] = 34 });
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: PeacemakerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // the Peacemaker wielder's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal("Peacemaker+2", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 34/50 to +3",
            TailOf(rig.Mem.RegionBytes(copy), 1, "Peacemaker+2".Length));
    }

    [Fact]
    public void A_weapon_with_an_earned_signature_shows_it_armed_on_the_card()
    {
        // The armed clause is disabled (owner 2026-07-07), so only the meter head reaches the card.
        var rig = Build(kills: new Dictionary<int, int> { [PeacemakerId] = 55 });   // past tier 3
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: PeacemakerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // the Peacemaker wielder's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal("Peacemaker+3", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 55",
            TailOf(rig.Mem.RegionBytes(copy), 1, "Peacemaker+3".Length));
    }

    // ==================== LW-55: CursorGate narrowing (turn-flag + weapon-agreement) ====================
    // Two NARROWING-ONLY cross-checks the cursor resolve now passes through CursorGate.Decide
    // before ANY dossier composes: gate B (turn ownership, Offsets.ATurnFlag) then gate A (roster
    // right-hand weapon vs the SAME unit's own band-equipped weapon, Offsets.AWeapon). A refusal
    // composes vanilla, doctrine unchanged (a wrong dossier is worse than vanilla). See
    // CursorGate.cs's own class doc for the full gate-order/sentinel rationale.

    /// <summary>Installs a fake FileConsoleLogger at Info tier (mirrors ModLoggerFacadeTests'
    /// own Install helper): AttackCard's production paths never log at Info, so the console list
    /// captures ONLY Warn/Error lines, letting these tests assert Warn presence/absence precisely
    /// without Debug-tier noise (RepaintDriver/SyncHit log plenty of Debug lines every tick).</summary>
    private static List<string> InstallWarnOnlyConsole()
    {
        var console = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, _ => { }) { LogLevel = LogLevel.Info };
        return console;
    }

    [Fact]
    public void LW55_gate_A_roster_and_band_weapon_disagree_refuses_to_vanilla_with_one_Warn_and_one_card_record()
    {
        // THE load-bearing negative: both ids are meta-registered and TRACKED (an untracked id
        // would compose vanilla even pre-fix, making the test vacuous). Pre-fix, this painted
        // Windrunner's row with the roster's own 100-kill tally under Windrunner's name while the
        // unit's actual band loadout (Stormcaller, 8 kills) was never cross-checked: the live bug.
        var console = InstallWarnOnlyConsole();
        try
        {
            var rig = Build(kills: new Dictionary<int, int> { [WindrunnerId] = 100, [StormcallerId] = 8 });
            long copy = 0x7000000000;
            rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

            // Roster says Windrunner (100 kills, the wrong dossier); the SAME unit's own band entry
            // says Stormcaller is actually equipped: a real live split (a stale roster row / a
            // scripted ENTD opener). The bridge is unambiguous and the turn flag is genuinely open
            // (gate B alone would pass): gate A is the only thing that can refuse here.
            MemSeats.SeatRoster(rig.Mem.Sparse, slot: 2, lvl: 50, br: 60, fa: 70, rh: WindrunnerId, nameId: 42);
            MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 5, weapon: StormcallerId, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
            MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 5, nameId: 42);
            rig.Mem.Sparse.U8s[Band.Entry(5) + Offsets.ATurnFlag] = 1;
            OpenTurn(rig, level: 50);

            Settle(rig.Card);

            Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
            Assert.Single(console);
            Assert.Contains("[WARN]", console[0]);
            Assert.Single(rig.Recorded);
            Assert.Equal("card", rig.Recorded[0].Type);
            Assert.Contains("WeaponMismatch", rig.Recorded[0].Payload);
            Assert.Contains($"rosterHand={WindrunnerId}", rig.Recorded[0].Payload);
            Assert.Contains($"bandWeapon={StormcallerId}", rig.Recorded[0].Payload);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void LW55_gate_B_turn_flag_not_open_refuses_to_vanilla_with_no_Warn_ever()
    {
        var console = InstallWarnOnlyConsole();
        try
        {
            var rig = Build();
            long copy = 0x7000000000;
            rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

            // Roster and band AGREE on Windrunner (zero gate-A anomaly), but the band's own turn
            // flag is never seated (defaults to 0, the unguarded fail-safe read): routine hover
            // behavior (targeting/reticle sweeps over allies and guests every tick) must never be
            // mistaken for a weapon anomaly, so this refuses via gate B alone.
            MemSeats.SeatRoster(rig.Mem.Sparse, slot: 2, lvl: 50, br: 60, fa: 70, rh: WindrunnerId, nameId: 42);
            MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 5, weapon: WindrunnerId, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
            MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 5, nameId: 42);
            OpenTurn(rig, level: 50);

            Settle(rig.Card);

            Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
            Assert.Empty(console);   // NEVER a Warn for the routine hover case
            Assert.Single(rig.Recorded);
            Assert.Equal("card", rig.Recorded[0].Type);
            Assert.Contains("NotTurnOwner", rig.Recorded[0].Payload);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void LW55_gate_agreement_control_matching_weapon_and_open_flag_composes_named_never_the_tripwire()
    {
        // Over-refusal pin: agreement (the overwhelming common case, every turn a player takes)
        // must never trip either gate. Reuses the plain compose shape already covered elsewhere in
        // this file, plus the explicit tripwire-silence assertion LW-55 adds.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);
        Settle(rig.Card);

        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 3/5 to +", TailOf(rig.Mem.RegionBytes(copy), 1, "Windrunner".Length));
        Assert.Empty(rig.Recorded);
    }

    [Fact]
    public void LW55_mismatch_tripwire_dedups_per_battle_and_ResetBattle_clears_it()
    {
        var rig = Build(kills: new Dictionary<int, int> { [WindrunnerId] = 100, [StormcallerId] = 8 });
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 2, lvl: 50, br: 60, fa: 70, rh: WindrunnerId, nameId: 42);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 5, weapon: StormcallerId, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 5, nameId: 42);
        rig.Mem.Sparse.U8s[Band.Entry(5) + Offsets.ATurnFlag] = 1;
        OpenTurn(rig, level: 50);

        Settle(rig.Card, ticks: 10);   // many repeated ticks against the SAME mismatch

        Assert.Single(rig.Recorded);   // deduped to exactly one record for the whole battle

        rig.Card.ResetBattle();
        Settle(rig.Card, ticks: 3);    // the identical mismatch persists into the next battle

        Assert.Equal(2, rig.Recorded.Count);   // ResetBattle cleared the dedup set: the same key records again
    }

    [Fact]
    public void LW55_a_second_distinct_mismatch_key_records_again_within_the_same_battle()
    {
        // The dedup set keys per (kind, rosterHand, bandWeapon), NOT once-per-battle: a first
        // refusal must never latch the tripwire shut against a genuinely DIFFERENT anomaly
        // arriving later in the same battle (no ResetBattle between the two).
        var rig = Build(kills: new Dictionary<int, int> { [WindrunnerId] = 100, [StormcallerId] = 8, [ZwillId] = 12 });
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 2, lvl: 50, br: 60, fa: 70, rh: WindrunnerId, nameId: 42);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 5, weapon: StormcallerId, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 5, nameId: 42);
        rig.Mem.Sparse.U8s[Band.Entry(5) + Offsets.ATurnFlag] = 1;
        OpenTurn(rig, level: 50);

        Settle(rig.Card, ticks: 5);    // first mismatch key: (WeaponMismatch, Windrunner, Stormcaller)

        Assert.Single(rig.Recorded);
        Assert.Contains($"rosterHand={WindrunnerId}", rig.Recorded[0].Payload);

        // The SAME unit's roster hand changes mid-battle to another meta-tracked id (a re-equip
        // shape); the band still says Stormcaller: a DISTINCT key (WeaponMismatch, Zwill,
        // Stormcaller), so the tripwire must fire again despite the earlier report.
        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 2, lvl: 50, br: 60, fa: 70, rh: ZwillId, nameId: 42);
        Settle(rig.Card, ticks: 5);

        Assert.Equal(2, rig.Recorded.Count);
        Assert.Contains($"rosterHand={ZwillId}", rig.Recorded[1].Payload);
        Assert.NotEqual(rig.Recorded[0].Payload, rig.Recorded[1].Payload);   // two distinct evidence trails
    }

    // ==================== Census candidate-rejection log flood fix ====================
    // A heap census walks every "Attack" standalone string it finds, most of them foreign (some
    // OTHER command's row); SyncHit used to log an "evicting" Debug line for EVERY one of those,
    // thousands per sweep, 98.4% of a real live log. A census candidate that never makes the cache
    // is not an eviction: FindHits now counts rejections silently and Finish() reports the total
    // once. A REAL eviction (a cached copy going foreign between passes, RepaintAll) is rare and
    // still logs, moved out of SyncHit and worded with the specific reason.

    [Fact]
    public void Census_candidate_rejection_is_silent_and_aggregated()
    {
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var rig = Build();
            long goodCopy = 0x7000000000;
            long foreignCopy1 = 0x7000100000;
            long foreignCopy2 = 0x7000200000;
            rig.Mem.AddAttackTable(goodCopy, 1, AttackCardText.VanillaDesc);
            // Two foreign standalone "Attack" candidates: some OTHER command's row, never cached.
            // Long enough (over FootprintBytes-1-PadAfter chars) that the 74-byte footprint read
            // itself succeeds, landing on the genuine "unknown footprint" rejection path rather than
            // a short-region read failure: the real flood shape a live heap census hits.
            rig.Mem.AddHeapRegion(foreignCopy1,
                AttackCardTableFixture.Build(1, "Some other command's own unrelated description prose, quite long indeed."));
            rig.Mem.AddHeapRegion(foreignCopy2,
                AttackCardTableFixture.Build(1, "Yet another foreign command's completely different description text as well."));

            Settle(rig.Card);

            // Stem match: this test drives the census only (nothing was ever cached then lost, so
            // no legitimate eviction line exists); ANY evict-flavored per-candidate line is the flood.
            Assert.DoesNotContain(file, l => l.Contains("evict"));
            string finished = Assert.Single(file, l => l.Contains("census finished"));
            Assert.Contains("1 table copies cached", finished);
            Assert.Contains("2 candidates rejected", finished);
            Assert.Equal(1, rig.Card.HitCountForTests);
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void A_real_eviction_during_RepaintAll_logs_exactly_one_line_naming_the_reason()
    {
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var rig = Build();
            long copy = 0x7000000000;
            rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

            SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
            OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
            Settle(rig.Card);
            Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

            file.Clear();   // drop the census/paint noise; only the eviction line itself is under test

            var (_, descAddr) = AttackCardTableFixture.Addrs(copy, 1);
            rig.Mem.WriteBytes(descAddr, AttackRow.BuildImage("ForeignThing", "Unrelated."));   // stomp the text only; the record stays Ours

            SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
            OpenTurn(rig, level: 55);       // Stormcaller's turn opens: forces RepaintAll to re-verify copy
            Settle(rig.Card, ticks: 1);

            Assert.Equal(0, rig.Card.HitCountForTests);   // evicted (a foreign footprint is never adopted forward)

            var evictionLines = file.FindAll(l => l.Contains("evicted"));
            Assert.Single(evictionLines);
            Assert.Contains("foreign-footprint", evictionLines[0]);
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void Enc2_rejection_at_census_is_also_silent_and_counted()
    {
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var rig = Build();
            long goodCopy = 0x7000000000;
            long foreignUtf16Copy = 0x7000100000;
            rig.Mem.AddAttackTable(goodCopy, 1, AttackCardText.VanillaDesc);
            // A standalone utf16 "Attack" candidate holding non-vanilla text: SyncHitEnc2 rejects it,
            // same silent/counted rule as the enc1 candidates above.
            rig.Mem.AddHeapRegion(foreignUtf16Copy,
                AttackCardTableFixture.Build(2, new string('X', AttackCardText.DefaultBudgetChars)));

            Settle(rig.Card);

            // Stem match, same rationale as the enc1 census test above: census-only, so ANY
            // evict-flavored line is a per-candidate leak.
            Assert.DoesNotContain(file, l => l.Contains("evict"));
            string finished = Assert.Single(file, l => l.Contains("census finished"));
            Assert.Contains("1 table copies cached", finished);
            Assert.Contains("1 candidates rejected", finished);
            Assert.Equal(1, rig.Card.HitCountForTests);
        }
        finally { ModLogger.Instance = prior; }
    }
}
