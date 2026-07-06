using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCard is LW-31 stage 2's runtime painter: it censuses the heap for the shared "Attack"
/// table copies (AttackCardTableFixture mirrors the packed layout AttackCardSpike's census
/// proved), resolves the acting unit's weapon via the SAME seam KillerStamp trusts
/// (ActorRegister.LastPlayer* + ActorResolver.HandsFromRoster), and writes/restores the desc
/// through the three-way anchor discipline (vanilla/current/previous).
/// </summary>
public class AttackCardTests
{
    private const int WindrunnerId = 501;
    private const int StormcallerId = 502;

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_attackcard_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    /// <summary>Seat a player: a roster slot (main-hand weaponId) plus its matching band entry
    /// and frame nameId, at a distinct (level,brave,faith) fingerprint per slot/bandIdx pair.</summary>
    private static void SeatPlayer(FakeSparseMemory m, int rosterSlot, int bandIdx, int weaponId,
                                   int lvl, int br, int fa, int nameId)
    {
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: weaponId, nameId: nameId);
        MemSeats.SeatBand(m, bandIdx, weapon: weaponId, lvl: lvl, br: br, fa: fa, gx: bandIdx, gy: bandIdx);
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);
    }

    private static Dictionary<int, WeaponMeta> Meta() => new()
    {
        [WindrunnerId] = new WeaponMeta { Name = "Windrunner", Wp = 10, Cat = "Bow", Formula = 1 },
        [StormcallerId] = new WeaponMeta { Name = "Stormcaller", Wp = 12, Cat = "Rod", Formula = 2 },
    };

    private static string DescOf(byte[] regionBytes, int enc)
    {
        int descPos = AttackCardProbeText.DescStart(AttackCardTableFixture.PadBefore, enc);
        var (text, _) = AttackCardProbeText.ReadDesc(regionBytes, descPos, enc, AttackCardText.DefaultBudgetChars);
        return text;
    }

    private sealed class Rig
    {
        public AttackCardMemory Mem = null!;
        public ActorRegister Register = null!;
        public ActorResolver Resolver = null!;
        public AttackCard Card = null!;
        public Dictionary<int, int> Kills = null!;
        public LegendStore Legends = null!;
    }

    private static Rig Build(Dictionary<int, int>? kills = null)
    {
        var mem = new AttackCardMemory();
        var meta = Meta();
        var weapons = new HashSet<int>(meta.Keys);
        var register = new ActorRegister(mem);
        var resolver = new ActorResolver(mem, weapons, register);
        // Kept below Tuning.ProdThresholds[0] (5) so both weapons compose at tier 0 (an empty,
        // trimmed-away suffix); the tier-suffix mechanism itself is AttackCardText's own
        // concern (AttackCardTextTests), not this runtime suite's.
        var k = kills ?? new Dictionary<int, int> { [WindrunnerId] = 3, [StormcallerId] = 4 };
        var legends = LegendStore.Load(TempDir());
        Func<List<int>?> resolveCursor = () => resolver.TryResolveCursorPlayer(out var w) ? w : null;
        var card = new AttackCard(mem, register, resolver.HandsFromRoster, resolveCursor, meta, k, legends);
        return new Rig { Mem = mem, Register = register, Resolver = resolver, Card = card, Kills = k, Legends = legends };
    }

    /// <summary>Drive Arm + StepScan to completion (tiny test regions always finish within a
    /// couple of ticks) plus at least one RepaintDriver pass. Extra ticks once settled are
    /// harmless (RepaintDriver is idempotent when nothing changed).</summary>
    private static void Settle(AttackCard card, int ticks = 5)
    {
        for (int i = 0; i < ticks; i++) card.Tick();
    }

    [Fact]
    public void Census_finds_the_attack_table_and_caches_it()
    {
        var rig = Build();
        rig.Mem.AddHeapRegion(0x7000000000, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);
    }

    [Fact]
    public void First_paint_composes_and_writes_the_armed_wielders_line()
    {
        var rig = Build();
        long baseAscii = 0x7000000000;
        long baseUtf16 = 0x7000100000;
        rig.Mem.AddHeapRegion(baseAscii, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        rig.Mem.AddHeapRegion(baseUtf16, AttackCardTableFixture.Build(2, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();          // priming
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // arrival: slot 2 (Windrunner)

        Settle(rig.Card);

        string expected = "Windrunner Kills: 3.";
        Assert.Equal(expected, DescOf(rig.Mem.RegionBytes(baseAscii), 1));
        Assert.Equal(expected, DescOf(rig.Mem.RegionBytes(baseUtf16), 2));
    }

    [Fact]
    public void Turn_owner_change_repaints_the_shared_table_with_the_new_weapons_line()
    {
        // THE LOAD-BEARING TEST: two physical table copies (the "shared table", about six
        // live per launch in production) both track the acting unit's dossier as it changes.
        var rig = Build();
        long copyA = 0x7000000000;
        long copyB = 0x7000100000;
        rig.Mem.AddHeapRegion(copyA, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        rig.Mem.AddHeapRegion(copyB, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);

        rig.Register.Update();          // priming
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // arrival: slot 2 (Windrunner)
        Settle(rig.Card);

        string windrunnerLine = "Windrunner Kills: 3.";
        Assert.Equal(windrunnerLine, DescOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal(windrunnerLine, DescOf(rig.Mem.RegionBytes(copyB), 1));

        // Turn-owner change: the register moves to slot 3 (Stormcaller).
        PointAt(rig.Mem.Sparse, 6);
        rig.Register.Update();
        Settle(rig.Card, ticks: 1);

        string stormcallerLine = "Stormcaller Kills: 4.";
        Assert.Equal(stormcallerLine, DescOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal(stormcallerLine, DescOf(rig.Mem.RegionBytes(copyB), 1));
        // Repainted forward, never evicted: both copies (which held the now-"previous" Windrunner
        // line the instant this repaint ran) are still cached, not dropped as foreign.
        Assert.Equal(2, rig.Card.HitCountForTests);
    }

    [Fact]
    public void Mid_battle_recensus_of_a_still_painted_copy_does_not_block_the_battle_exit_vanilla_restore()
    {
        // F1 regression: a re-census (the same kind any live eviction/RepaintAll triggers) that
        // finds a copy still holding OUR OWN short composed line must not cache that short live
        // length as the copy's footprint; the true footprint is the vanilla desc's own 73 chars.
        var rig = Build();
        long copyA = 0x7000000000;
        long copyB = 0x7000100000;
        rig.Mem.AddHeapRegion(copyA, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        rig.Mem.AddHeapRegion(copyB, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();          // priming
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // arrival: slot 2 (Windrunner)

        Settle(rig.Card);   // first census (measures the true 73-char vanilla footprint) + first paint

        string shortLine = "Windrunner Kills: 3.";
        Assert.Equal(shortLine, DescOf(rig.Mem.RegionBytes(copyA), 1));
        Assert.Equal(shortLine, DescOf(rig.Mem.RegionBytes(copyB), 1));

        // Force the same kind of mid-battle re-census any live eviction triggers, while both
        // copies still hold our own short current line, not vanilla.
        rig.Card.ForceRecensusForTests();
        Settle(rig.Card);

        rig.Card.ResetBattle();   // battle exit: best-effort vanilla restore

        byte[] expected = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        foreach (long copy in new[] { copyA, copyB })
        {
            byte[] actual = new byte[expected.Length];
            System.Array.Copy(rig.Mem.RegionBytes(copy), AttackCardTableFixture.PadBefore + AttackCardProbeText.DescStart(0, 1), actual, 0, expected.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void A_boundary_truncated_partial_desc_read_is_never_cached_with_a_bogus_footprint()
    {
        // Same-family edge (F1): a hit whose desc read stops early because the underlying REGION
        // itself ends shortly past the label (a genuine region-end truncation, this fixture's fake
        // heap region is simply too short for a full desc read, not a simulation of ChunkReader's
        // own chunk-boundary TrailSlack windowing specifically) reads a genuine PARTIAL prefix, not
        // any of the three known lines. It must stay foreign and uncached, never poisoning a
        // footprint with a partial length.
        var rig = Build();
        long goodCopy = 0x7000000000;
        long truncatedCopy = 0x7000100000;
        rig.Mem.AddHeapRegion(goodCopy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        byte[] label = ByteScan.Enc("Attack", 1);
        var partial = new byte[8 + label.Length + 1 + 20];   // lead zeros + label + NUL + 20 real chars, no terminator
        System.Array.Copy(label, 0, partial, 8, label.Length);
        byte[] prefix = ByteScan.Enc(AttackCardText.VanillaDesc.Substring(0, 20), 1);
        System.Array.Copy(prefix, 0, partial, 8 + label.Length + 1, prefix.Length);
        rig.Mem.AddHeapRegion(truncatedCopy, partial);
        byte[] truncatedBefore = (byte[])partial.Clone();

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();
        Settle(rig.Card);

        Assert.Equal(1, rig.Card.HitCountForTests);   // only the good copy is cached
        Assert.Equal(truncatedBefore, rig.Mem.RegionBytes(truncatedCopy));   // byte-for-byte untouched
    }

    [Fact]
    public void Unarmed_owner_restores_vanilla_byte_exact()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();
        Settle(rig.Card);
        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));

        // A second player, band-confirmed UNARMED (RRHand = the empty-hand sentinel): a real
        // player turn with no tracked weapon in hand.
        MemSeats.SeatRoster(rig.Mem.Sparse, slot: 4, lvl: 45, br: 40, fa: 50, rh: 0xFFFF, nameId: 44);
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 7, weapon: 0xFFFF, lvl: 45, br: 40, fa: 50, gx: 7, gy: 7);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 7, nameId: 44);
        PointAt(rig.Mem.Sparse, 7);
        rig.Register.Update();
        Settle(rig.Card, ticks: 1);

        byte[] expected = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
        byte[] actual = new byte[expected.Length];
        System.Array.Copy(rig.Mem.RegionBytes(copy), AttackCardTableFixture.PadBefore + AttackCardProbeText.DescStart(0, 1), actual, 0, expected.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_foreign_buffer_is_never_written()
    {
        var rig = Build();
        long goodCopy = 0x7000000000;
        long foreignCopy = 0x7000100000;
        rig.Mem.AddHeapRegion(goodCopy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        // A standalone "Attack" label whose desc is unrelated prose: never vanilla, current, or
        // previous. Must never be cached or written.
        rig.Mem.AddHeapRegion(foreignCopy, AttackCardTableFixture.Build(1, "Some other command's description text."));
        byte[] foreignBefore = rig.Mem.RegionBytes(foreignCopy);

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();
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
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();
        Settle(rig.Card);
        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));

        rig.Card.ResetBattle();

        Assert.Equal(0, rig.Card.HitCountForTests);
        Assert.Equal(AttackCardText.VanillaDesc, DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    // LW-31 stage 2: cursor-first resolve (ledger LW-31).
    // The register (ActorRegister.LastPlayer*) only updates once a unit ACTS, but a player hovers
    // the Abilities menu BEFORE acting, so the register is stale by definition at that moment. The
    // 2026-07-05 TurnOwnerSpike tape proved the condensed turn-queue struct (Offsets.TurnQueue)
    // snaps to the acting unit the instant their turn OPENS, leading the register by seconds. The
    // fixes below add a strictly-guarded cursor-first attempt (ActorResolver.TryResolveCursorPlayer,
    // ActorResolver.Cursor.cs), falling back to the pre-existing register resolve whenever any
    // guard fails. Doctrine: a wrong dossier is worse than vanilla, never a guess.

    [Fact]
    public void Cursor_leads_the_stale_register_to_the_unit_whose_turn_just_opened()
    {
        // THE LOAD-BEARING TEST (the bug itself): the register is still parked on the PREVIOUS
        // actor (Windrunner), it never sees Stormcaller act, while the cursor already names
        // Stormcaller, whose turn has just opened. RED before the fix (the dossier stays on
        // Windrunner); GREEN after (the dossier follows the cursor to Stormcaller).
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();          // priming
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // arrival: slot 2 (Windrunner), the register's ONLY update this test

        // Stormcaller's turn has opened (the cursor snaps to it) but they have not acted; the
        // register never moves off Windrunner.
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal("Stormcaller Kills: 4.", DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Enemy_team_cursor_falls_back_to_the_register()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // register: Windrunner

        // An enemy's turn is open (team=1): the cursor must never override the register here.
        rig.Mem.SeatCursor(team: 1, level: 50, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_nameId_disagreement_falls_back_to_the_register()
    {
        // The tape's caution: the identity signal can flicker independently of level/hp during
        // action-confirm. The cursor never trusts level/hp alone; the matched band entry's own
        // frame nameId back-reference must ALSO bridge to exactly one roster slot
        // (ActorRegister.Bridge's own proven pattern). A band entry whose nameId disagrees with
        // every roster slot (simulating the flicker) fails that bridge and falls back.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // register: Windrunner

        // level/hp/maxHp fingerprint-match Stormcaller's band entry, but its frame nameId is
        // overwritten to a value no roster slot carries (the flicker).
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 6, nameId: 999);
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_with_no_roster_match_falls_back_to_the_register()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // register: Windrunner

        // A band entry fingerprint-matches the cursor's level/hp/maxHp, but no roster slot bridges
        // to it at all; the cursor never guesses a roster identity it cannot confirm.
        MemSeats.SeatBand(rig.Mem.Sparse, bandIdx: 9, weapon: StormcallerId, lvl: 55, br: 65, fa: 75, gx: 9, gy: 9);
        MemSeats.SeatFrameNameId(rig.Mem.Sparse, bandIdx: 9, nameId: 77);   // no roster slot has nameId 77
        rig.Mem.SeatCursor(team: 0, level: 55, hp: 100, maxHp: 100);

        Settle(rig.Card);

        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Unreadable_cursor_struct_falls_back_to_the_register()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();          // register: Windrunner

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        // Values planted directly WITHOUT marking Offsets.TurnQueue Readable (FakeSparseMemory's
        // Readable contract is an exact-address set, see its class doc): mirrors a genuine
        // unreadable struct address. The cursor path must fail closed rather than trust a read it
        // cannot confirm succeeded.
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 0;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 55;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        rig.Mem.Sparse.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;

        Settle(rig.Card);

        Assert.Equal("Windrunner Kills: 3.", DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    [Fact]
    public void Cursor_and_register_both_absent_composes_vanilla()
    {
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        // Neither the register (no Update() arrivals at all) nor the cursor (never seeded) has an
        // answer: the dossier must stay vanilla.
        Settle(rig.Card);

        Assert.Equal(AttackCardText.VanillaDesc, DescOf(rig.Mem.RegionBytes(copy), 1));
    }

    // LW-33: SyncHit's own footprint repair.

    [Fact]
    public void SyncHit_repairs_a_poisoned_short_footprint_when_the_live_desc_is_a_known_line()
    {
        // LW-33: SyncHit's own full live read (capped at the same 73-char cap the census uses) is
        // a stronger observation than the census-time pin (AttackCard.Census.cs): it can REPAIR a
        // footprint that was somehow poisoned short, not just avoid poisoning a fresh one.
        var rig = Build();
        long copy = 0x7000000000;
        rig.Mem.AddHeapRegion(copy, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));

        SeatPlayer(rig.Mem.Sparse, rosterSlot: 2, bandIdx: 5, weaponId: WindrunnerId, lvl: 50, br: 60, fa: 70, nameId: 42);
        rig.Register.Update();
        PointAt(rig.Mem.Sparse, 5);
        rig.Register.Update();
        Settle(rig.Card);   // first census + paint: composes "Windrunner Kills: 3." (20 chars)

        string shortLine = "Windrunner Kills: 3.";
        Assert.Equal(shortLine, DescOf(rig.Mem.RegionBytes(copy), 1));

        // Poison the cached footprint down to the currently-painted line's own length.
        rig.Card.PoisonFirstHitFootprintForTests(shortLine.Length);

        // Turn-owner change to a weapon whose composed line is LONGER than the poisoned footprint
        // but still well within the true 73-char vanilla footprint.
        SeatPlayer(rig.Mem.Sparse, rosterSlot: 3, bandIdx: 6, weaponId: StormcallerId, lvl: 55, br: 65, fa: 75, nameId: 43);
        PointAt(rig.Mem.Sparse, 6);
        rig.Register.Update();
        Settle(rig.Card, ticks: 1);

        string longerLine = "Stormcaller Kills: 4.";   // 21 chars: > the poisoned 20, well under 73
        Assert.Equal(longerLine, DescOf(rig.Mem.RegionBytes(copy), 1));
    }
}
