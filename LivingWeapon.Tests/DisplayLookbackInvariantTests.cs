using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Startup invariant: the sweep lookback (the chunk-boundary prefix) must fit
/// CardPatterns.MaxAnchorLen plus the widest painted slot, or a boundary-straddling
/// card could never anchor-verify. Violation logs an error and continues -- this runs
/// inside the game process, so construction must never throw over a long name.
/// </summary>
public class DisplayLookbackInvariantTests
{
    private static Dictionary<int, WeaponMeta> MetaWithFlavor(string flavor) => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = flavor, Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    [Fact]
    public void Normal_meta_fits_inside_the_lookback()
    {
        var pats = new CardPatterns(MetaWithFlavor("Bright edge of dawn"));
        Assert.True(pats.FitsLookback(DisplaySweep.Lookback));
    }

    [Fact]
    public void Anchor_longer_than_lookback_fails_the_invariant()
    {
        // UTF-16LE doubles every char: a flavor of Lookback/2 chars alone overflows the prefix.
        var pats = new CardPatterns(MetaWithFlavor(new string('y', DisplaySweep.Lookback / 2)));
        Assert.False(pats.FitsLookback(DisplaySweep.Lookback));
    }

    [Fact]
    public void Boundary_anchor_just_inside_the_budget_passes()
    {
        // Widest slot = UTF-16 "Kills: " (14 bytes) + Signatures.KillsMeterSlotChars (11) UTF-16
        // chars (22 bytes) = 36.
        // The longest anchor that still fits: (Lookback - 36) / 2 UTF-16 chars.
        int maxChars = (DisplaySweep.Lookback - 36) / 2;
        Assert.True(new CardPatterns(MetaWithFlavor(new string('y', maxChars))).FitsLookback(DisplaySweep.Lookback));
        Assert.False(new CardPatterns(MetaWithFlavor(new string('y', maxChars + 1))).FitsLookback(DisplaySweep.Lookback));
    }

    [Fact]
    public void Violating_meta_does_not_throw_at_display_construction()
    {
        // The invariant must log-and-continue, never take the game process down.
        var meta = MetaWithFlavor(new string('y', DisplaySweep.Lookback));
        var heap = new FakeHeap(Array.Empty<(long, byte[])>());

        var ex = Record.Exception(() => new Display(meta, new Dictionary<int, int>(), heap));

        Assert.Null(ex);
    }

    // --- FitsTrailSlack: the forward-search twin of FitsLookback (LANDMINE 1, TrailSlack). A
    //     NEW-layout card's flavor can sit AFTER "Kills: ", so the trailing slack (not just the
    //     leading lookback) must fit the widest anchor + the Kills literal + the meter slot.

    [Fact]
    public void FitsTrailSlack_true_at_4096()
    {
        var pats = new CardPatterns(MetaWithFlavor("Bright edge of dawn"));
        Assert.True(pats.FitsTrailSlack(4096));
    }

    [Fact]
    public void FitsTrailSlack_false_at_64()
    {
        var pats = new CardPatterns(MetaWithFlavor("Bright edge of dawn"));
        Assert.False(pats.FitsTrailSlack(64));
    }

    [Fact]
    public void FitsTrailSlack_matches_the_documented_worst_case_formula()
    {
        string flavor = "Bright edge of dawn";
        var pats = new CardPatterns(MetaWithFlavor(flavor));
        int worstCase = pats.MaxAnchorLen + pats.Kills(2).Length + Signatures.KillsMeterSlotChars * 2 + 4;
        Assert.True(pats.FitsTrailSlack(worstCase));
        Assert.False(pats.FitsTrailSlack(worstCase - 1));
    }
}
