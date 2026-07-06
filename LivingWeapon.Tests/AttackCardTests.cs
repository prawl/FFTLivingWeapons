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
    /// <paramref name="sprite"/> defaults to 0 (an ordinary human, AttackRow.Policy.HumanSprite).</summary>
    private static void SeatPlayer(FakeSparseMemory m, int rosterSlot, int bandIdx, int weaponId,
                                   int lvl, int br, int fa, int nameId, int sprite = 0)
    {
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: weaponId, nameId: nameId, sprite: sprite);
        MemSeats.SeatBand(m, bandIdx, weapon: weaponId, lvl: lvl, br: br, fa: fa, gx: bandIdx, gy: bandIdx);
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);
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
        Func<(List<int>, long)?> resolveCursor = () =>
            resolver.TryResolveCursorPlayer(out var w, out long rb) ? (w, rb) : ((List<int>, long)?)null;
        var card = new AttackCard(mem, resolveCursor,
                                   resolver.RawMainHand, resolver.SpriteOf, meta, k);
        return new Rig { Mem = mem, Register = register, Resolver = resolver, Card = card, Kills = k };
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
        // vanilla restore (goal rule: unarmed HUMAN -> "Fists").
        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 4, lvl: 45, br: 40, fa: 50, rh: 0xFFFF, nameId: 44, sprite: 0x80);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 7, weapon: 0xFFFF, lvl: 45, br: 40, fa: 50, gx: 7, gy: 7);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 7, nameId: 44);
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
    public void ResetBattle_restores_vanilla_and_drops_the_cache()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // Windrunner's turn opens (cursor-only resolve)
        Settle(rig.Card);
        Assert.Equal("Windrunner", RowOf(rig.Mem.RegionBytes(copy), 1));

        rig.Card.ResetBattle();

        Assert.Equal(0, rig.Card.HitCountForTests);
        Assert.Equal(AttackCardText.VanillaDesc, RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal(AttackRow.VanillaOffsetBytes, rig.Mem.RegionBytes(AttackCardMemory.RecordAddrFor(copy, 1))[..8]);
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
        rig.Card.ForceRecensusForTests();   // clears the cache (Arm) without touching any live copy
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

    [Fact]
    public void A_weapon_with_a_locked_signature_shows_the_unlocks_tease_on_the_card()
    {
        // Matches the owner's own worked "locked" example (CHANGE 3, 2026-07-06): Peacemaker's
        // Signature.AtTier is 3 (Tuning.ProdThresholds[2] == 50); 34 kills sits at tier 2
        // (25 <= 34 < 50), short of it, so the tease renders instead of "armed".
        var rig = Build(kills: new Dictionary<int, int> { [PeacemakerId] = 34 });
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: PeacemakerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // the Peacemaker wielder's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal("Peacemaker+2", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 34/50 to +3. Unlocks Gun Slinger",
            TailOf(rig.Mem.RegionBytes(copy), 1, "Peacemaker+2".Length));
    }

    [Fact]
    public void A_weapon_with_an_earned_signature_shows_it_armed_on_the_card()
    {
        var rig = Build(kills: new Dictionary<int, int> { [PeacemakerId] = 55 });   // past tier 3
        long copy = 0x7000000000;
        rig.Mem.AddAttackTable(copy, 1, AttackCardText.VanillaDesc);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: PeacemakerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        OpenTurn(rig, level: 50);       // the Peacemaker wielder's turn opens (cursor-only resolve)
        Settle(rig.Card);

        Assert.Equal("Peacemaker+3", RowOf(rig.Mem.RegionBytes(copy), 1));
        Assert.Equal("Kills: 55. Gun Slinger armed",
            TailOf(rig.Mem.RegionBytes(copy), 1, "Peacemaker+3".Length));
    }
}
