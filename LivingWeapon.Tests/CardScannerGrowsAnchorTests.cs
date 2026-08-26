using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-332: the Grows line moves from the body bottom to directly under the Kills scaffold
/// ("Kills: " + slot + "\n" + Grows + "\n" + flavor...), so CardScanner must learn each
/// weapon's baked Grows line as an ADDITIONAL anchor pattern -- otherwise the Kills-to-flavor
/// gap widens enough (the Grows line is ~30-100 bytes, x2 for UTF-16) for a neighboring card's
/// own tail to sit closer and steal the pairing in a packed heap sweep. Same-lane weapons bake
/// byte-IDENTICAL Grows lines, so ownership can never rest on a shared Grows byte match alone.
/// Round 1's fix was a second key (each entry's UNIQUE candidates, i.e. flavor/earned lines,
/// excluding Grows) breaking a tie on nearest distance; round 2 (this file) found two executed
/// counterexamples against that rule (S1, S2 below) and replaced it with an exact-layout probe
/// (CardScanner.LayoutProbeOwner) that runs FIRST -- the two-key nearest-distance rule survives
/// only as its fallback (CardScanner.FindNearestFlavor).
/// </summary>
public class CardScannerGrowsAnchorTests
{
    private const string PaGrows = "<color=30>Grows: Physical Attack.</color>";
    private const string MaGrows = "<color=50>Grows: Magick Attack.</color>";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FindKills_new_layout_grows_line_between_kills_and_flavor_resolves_owner(int enc)
    {
        const string flavor = "A sharp blade";
        var meta = CardScannerFixtures.BuildMetaMapWithGrows((1, "Sword", flavor, PaGrows));
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        var (killsSlotPos, growsPos, flavorPos) =
            CardFixtures.WriteCardForwardWithGrows(buf, 0, PaGrows, flavor, enc);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(enc, hits[0].Enc);
        Assert.Equal(killsSlotPos, hits[0].SlotPos);
        // The Grows line sits closer to "Kills: " than the flavor does (the whole point of
        // registering it as an anchor), so the emitted FlavorPos resolves to IT, not the flavor.
        Assert.Equal(growsPos, hits[0].FlavorPos);
    }

    /// <summary>PACKED-POOL MISPAINT REGRESSION (load-bearing, non-vacuous): card A (new layout)
    /// ends in its own flavor line immediately above card B's "Kills: " line -- exactly what a
    /// packed heap sweep produces when many cards sit back to back. A's flavor is deliberately
    /// SHORTER than the fixed Kills-to-Grows gap B's own anchor sits behind is NOT (see the
    /// distance comment below): before this arc, B's only candidate was its own (now much
    /// farther) flavor, so A's adjacent flavor was the nearer match and stole B's hit. Different
    /// lanes (PA vs MA) -- this test alone needs no same-lane tie-break, just the Grows anchor.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FindKills_packed_pool_a_flavor_does_not_steal_bs_kills_once_grows_is_registered(int enc)
    {
        const string flavorA = "A blade of dependable make"; // 26 chars: deliberately LONGER than
                                                               // B's fixed Kills->Grows gap (7+11+1=19
                                                               // chars), so B's OWN Grows anchor must
                                                               // win on raw distance alone.
        const string flavorB = "B holy relic";
        var meta = CardScannerFixtures.BuildMetaMapWithGrows(
            (1, "Sword", flavorA, PaGrows),
            (2, "Staff", flavorB, MaGrows));
        var pats = new CardPatterns(meta);

        var buf = new byte[600];
        var (killsPosA, growsPosA, flavorPosA) = CardFixtures.WriteCardForwardWithGrows(buf, 0, PaGrows, flavorA, enc);
        int flavorAEnd = flavorPosA + ByteScan.Enc(flavorA, enc).Length;
        // Card B starts EXACTLY where A's flavor ends -- the tightest possible packing, and the
        // scenario that mispaints without a Grows anchor.
        var (killsPosB, growsPosB, flavorPosB) = CardFixtures.WriteCardForwardWithGrows(buf, flavorAEnd, MaGrows, flavorB, enc);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        // The regression check FIRST (count + ids): the mispaint bug this test pins is that B's
        // "Kills: " hit gets attributed to A's id, not merely that some position differs.
        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);   // THE regression: must be B, never A
        Assert.Equal(growsPosA, hits[0].FlavorPos);
        Assert.Equal(growsPosB, hits[1].FlavorPos);
    }

    /// <summary>S2 (LW-332 round 2): a FOREIGN card's flavor line is deliberately SHORTER than
    /// the fixed Kills->Grows gap (19 chars) and ends directly above B's "Kills: " line. Under
    /// the round-1 two-key rule (nearest distance, no exact-layout probe) this wins dAll
    /// OUTRIGHT for B's hit -- different lanes, so the dUnique tiebreak never even engages; the
    /// bug fires on raw distance alone. "Storm-wired." is the REAL pre-round-2 baked flavor for
    /// Stormarc (data/items.json id 86, 12 chars) -- one of the two real flavors short enough to
    /// trigger this (the other, Wrathblade's "Ruin feeds it.", is 14); both were re-flavored to
    /// 20+ chars in this round specifically so no baked flavor can ever reproduce this bug again
    /// (analyze.py's new min-length gate, tools/analyze.py check_grows_gap_min_length).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FindKills_packed_pool_short_foreign_flavor_does_not_steal_bs_kills(int enc)
    {
        const string flavorA = "Storm-wired."; // 12 chars: shorter than the fixed 19-char
                                                 // Kills->Grows gap, so it beats B's own (fixed,
                                                 // farther) Grows anchor on raw distance alone.
        const string flavorB = "B holy relic";
        var meta = CardScannerFixtures.BuildMetaMapWithGrows(
            (1, "Sword", flavorA, PaGrows),
            (2, "Staff", flavorB, MaGrows));   // different lanes: this bug needs no tie-break
        var pats = new CardPatterns(meta);

        var buf = new byte[600];
        var (killsPosA, growsPosA, flavorPosA) = CardFixtures.WriteCardForwardWithGrows(buf, 0, PaGrows, flavorA, enc);
        int flavorAEnd = flavorPosA + ByteScan.Enc(flavorA, enc).Length;
        // Card B starts EXACTLY where A's short flavor ends -- the tightest possible packing.
        var (killsPosB, growsPosB, flavorPosB) = CardFixtures.WriteCardForwardWithGrows(buf, flavorAEnd, MaGrows, flavorB, enc);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);   // THE regression: must be B, never A
        Assert.Equal(growsPosA, hits[0].FlavorPos);
        Assert.Equal(growsPosB, hits[1].FlavorPos);
    }

    /// <summary>S1 (LW-332 round 2, TRUE scenario -- supersedes the round-1 test this replaced,
    /// which tested a weakened stand-in where weapon 1 was never physically present in the
    /// buffer at all): two SAME-LANE cards packed back to back, exactly what a packed heap sweep
    /// produces. A ends in its OWN flavor directly above B's "Kills: " line. A and B bake
    /// byte-IDENTICAL Grows lines (same lane), so the round-1 two-key rule's dAll TIES on the
    /// shared Grows bytes at G (both entries' nearest match resolves to that one physical
    /// occurrence) and the dUnique tiebreak alone decides -- A's adjacent flavor (26 chars away)
    /// beats B's own flavor (19 + 41-char Grows + 1 = 61 chars away), so A wrongly owns B's hit.
    /// The exact-layout probe (STEP 1) fixes this: it checks the FIXED offset G/F directly, so
    /// it never engages "nearest" at all -- A's own flavor bytes never appear at B's F, so only
    /// B passes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FindKills_packed_pool_same_lane_a_flavor_does_not_steal_bs_kills(int enc)
    {
        const string flavorA = "A blade of dependable make"; // 26 chars: between the fixed 19-char
                                                               // Kills->Grows gap and B's own 61-char
                                                               // (19 + 41-char Grows + 1) Kills->flavor
                                                               // gap -- the exact window that makes
                                                               // round-1's dAll TIE on the shared
                                                               // (same-lane) Grows bytes.
        const string flavorB = "B holy relic";
        var meta = CardScannerFixtures.BuildMetaMapWithGrows(
            (1, "Sword", flavorA, PaGrows),
            (2, "Staff", flavorB, PaGrows));   // SAME lane: A and B bake byte-identical Grows
        var pats = new CardPatterns(meta);

        var buf = new byte[600];
        var (killsPosA, growsPosA, flavorPosA) = CardFixtures.WriteCardForwardWithGrows(buf, 0, PaGrows, flavorA, enc);
        int flavorAEnd = flavorPosA + ByteScan.Enc(flavorA, enc).Length;
        // Card B starts EXACTLY where A's flavor ends -- the tightest possible packing, and the
        // scenario that mispaints without the exact-layout probe.
        var (killsPosB, growsPosB, flavorPosB) = CardFixtures.WriteCardForwardWithGrows(buf, flavorAEnd, PaGrows, flavorB, enc);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        // The regression check FIRST (count + ids): the mispaint bug this test pins is that B's
        // "Kills: " hit gets attributed to A's id, not merely that some position differs.
        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);   // THE regression: must be B, never A
        Assert.Equal(growsPosA, hits[0].FlavorPos);
        Assert.Equal(growsPosB, hits[1].FlavorPos);
    }
}
