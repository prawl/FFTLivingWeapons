using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardScanner.FindKills discovers NEW paint sites by searching for the nearest preceding
/// flavor-like anchor. Reliquary Phase 1 (decision 12): once a weapon's card shows an EARNED
/// line instead of its baked flavor, FindKills must still discover and attribute that site --
/// otherwise a freshly-allocated UI buffer showing an already-earned story would never get a
/// Kills site registered at all.
/// </summary>
public class CardScannerEarnedAnchorTests
{
    // Both exactly 13 chars -- EarnedAnchors requires the earned line's encoded length to equal
    // the baked flavor's, so this mirrors what a real weapon's registered earned line looks like.
    private const string BakedFlavor = "A sharp blade";
    private const string EarnedLine = "EARNEDLINE!!!";

    [Fact]
    public void FindKills_attributes_a_site_whose_onscreen_text_is_an_earned_line()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", BakedFlavor));
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(1, EarnedLine);

        var buf = new byte[200];
        int pos = 0;
        CardFixtures.WriteKillsBlock(buf, pos, EarnedLine, gap: 10);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits, anchors);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(pos, hits[0].FlavorPos);
    }

    [Fact]
    public void FindKills_ties_each_earned_line_to_its_own_weapon_across_two_cards()
    {
        var meta = CardScannerFixtures.BuildMetaMap(
            (1, "Sword", BakedFlavor), (2, "Staff", "B holy relic"));   // "B holy relic" also 12 chars
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(1, EarnedLine);
        string earnedStaffLine = "STAFFEARNED!";   // 12 chars, matches "B holy relic"
        anchors.SetCurrent(2, earnedStaffLine);

        var buf = new byte[400];
        int posA = 0;
        int endA = CardFixtures.WriteKillsBlock(buf, posA, EarnedLine, gap: 10) + 4;
        int posB = endA + 20;
        CardFixtures.WriteKillsBlock(buf, posB, earnedStaffLine, gap: 10);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits, anchors);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);
        Assert.Equal(posA, hits[0].FlavorPos);
        Assert.Equal(posB, hits[1].FlavorPos);
    }

    [Fact]
    public void FindKills_still_finds_the_baked_flavor_when_no_earned_line_is_registered()
    {
        // A weapon with NO current earned line: AnchorsFor yields baked-only, so this must
        // behave exactly like the anchors==null path.
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", BakedFlavor));
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);   // nothing registered

        var buf = new byte[200];
        CardFixtures.WriteKillsBlock(buf, 0, BakedFlavor, gap: 10);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits, anchors);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Id);
    }
}
