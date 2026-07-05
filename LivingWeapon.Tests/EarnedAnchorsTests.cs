using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// EarnedAnchors holds decision 12's three-way anchor state per weapon: [baked, CURRENT
/// composed line, PREVIOUS distinct composed line]. The pair rotates ONLY on SetCurrent (a
/// compose-change edge) -- NEVER merely by reading AnchorsFor (paint time never rotates).
/// Both encodings (ASCII / UTF-16LE) are validated equal to the baked Flavor pattern's byte
/// length; a mismatch refuses the update (FlavorSpike.cs:109-116 idiom) rather than risk
/// desyncing the two encodings' painted byte counts.
/// </summary>
public class EarnedAnchorsTests
{
    private const int Id = 1;

    private static CardPatterns Patterns(string flavor = "A fine blade indeed.")
        => new(new Dictionary<int, WeaponMeta>
        {
            [Id] = new WeaponMeta { Name = "Sword", Flavor = flavor, Wp = 10, Cat = "Sword", Formula = 1 },
        });

    [Fact]
    public void Equal_length_enforced()
    {
        var pats = Patterns("A fine blade indeed.");   // 20 chars
        var anchors = new EarnedAnchors(pats);

        string? evicted = anchors.SetCurrent(Id, "too short");   // wrong length -- refused

        Assert.Null(evicted);
        // No current registered -- AnchorsFor yields only the baked pattern.
        var list = anchors.AnchorsFor(Id, 1);
        Assert.Single(list);
    }

    [Fact]
    public void Rotation_on_compose_change_only()
    {
        var flavor = "A fine blade indeed.";   // 20 chars
        var pats = Patterns(flavor);
        var anchors = new EarnedAnchors(pats);
        string lineA = "Sword: 5 felled.    ";   // also 20 chars
        string lineB = "Sword: 9 felled.    ";   // also 20 chars

        anchors.SetCurrent(Id, lineA);
        var listBefore = anchors.AnchorsFor(Id, 1);
        Assert.Equal(2, listBefore.Count);   // baked + current

        // Reading AnchorsFor repeatedly (as painting would) never rotates anything.
        for (int i = 0; i < 5; i++) anchors.AnchorsFor(Id, 1);
        var listStillSame = anchors.AnchorsFor(Id, 1);
        Assert.Equal(2, listStillSame.Count);

        // A genuine compose-change rotates: previous := old current, current := new.
        string? evicted = anchors.SetCurrent(Id, lineB);
        Assert.Equal(lineA, evicted);

        var listAfter = anchors.AnchorsFor(Id, 1);
        Assert.Equal(3, listAfter.Count);   // baked + current(B) + previous(A)
    }

    [Fact]
    public void Dedup_identical()
    {
        var flavor = "A fine blade indeed.";
        var pats = Patterns(flavor);
        var anchors = new EarnedAnchors(pats);
        string line = "Sword: 5 felled.    ";

        anchors.SetCurrent(Id, line);
        // Re-composing the SAME content must not manufacture a spurious rotation.
        string? evicted = anchors.SetCurrent(Id, line);

        Assert.Null(evicted);
        var list = anchors.AnchorsFor(Id, 1);
        Assert.Equal(2, list.Count);   // baked + current only -- no duplicate "previous" entry
    }

    [Fact]
    public void Dedup_identical_after_a_real_rotation_collapses_the_anchor_set()
    {
        var flavor = "A fine blade indeed.";
        var pats = Patterns(flavor);
        var anchors = new EarnedAnchors(pats);
        string lineA = "Sword: 5 felled.    ";
        string lineB = "Sword: 9 felled.    ";

        anchors.SetCurrent(Id, lineA);
        anchors.SetCurrent(Id, lineB);   // previous=A, current=B
        anchors.SetCurrent(Id, lineA);   // previous=B, current=A

        // Rotating SetCurrent alone can't put current==previous (dedup blocks the only path
        // that would); demonstrate AnchorsFor never double-lists an accidental current==previous
        // state via the seed path instead (StoryLines.SeedAtStartup can load such a state from disk).
        var seeded = new EarnedAnchors(pats);
        // SeedCurrent/SeedPrevious mirror StoryLines.SeedAtStartup's use (both given the SAME line).
        seeded.SeedCurrent(Id, lineA);
        seeded.SeedPrevious(Id, lineA);

        var list = seeded.AnchorsFor(Id, 1);
        Assert.Equal(2, list.Count);   // baked + one copy of lineA, not two
    }

    [Fact]
    public void Anchors_for_baked_first()
    {
        var flavor = "A fine blade indeed.";
        var pats = Patterns(flavor);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(Id, "Sword: 5 felled.    ");

        var list = anchors.AnchorsFor(Id, 1);

        Assert.Equal(ByteScan.Ascii(flavor), list[0]);
    }

    [Fact]
    public void Anchors_for_unknown_weapon_yields_empty()
    {
        var pats = Patterns();
        var anchors = new EarnedAnchors(pats);
        Assert.Empty(anchors.AnchorsFor(999, 1));
    }
}
