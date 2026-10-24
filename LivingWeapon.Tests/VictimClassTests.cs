using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// VictimClass.cs is the pure Reliquary Phase 1 classifier: one archetype per kill, precedence
/// undead &gt; caster &gt; human &gt; monster &gt; unknown (docs/RELIQUARY_AC.md's Classify checklist).
/// Enum order is the AC's listing order (Caster=0, Human=1, Monster=2, Undead=3, Unknown=4) --
/// it drives both the Mark event-key space (1000+index) and mark tie-breaks (a count tie
/// resolves in this order). One test per precedence pair, plus the out-of-band/story-id cases
/// that must land Unknown BY DESIGN, plus Dark Knight at 160.
/// </summary>
public class VictimClassTests
{
    [Fact]
    public void Undead_bit_wins_over_caster_band()
    {
        // job 80 (Black Mage) would classify Caster on its own -- the undead bit must override it.
        Assert.Equal(VictimClass.Archetype.Undead, VictimClass.Classify(job: 80, undead: true));
    }

    [Fact]
    public void Undead_caster_classifies_undead()
    {
        Assert.Equal(VictimClass.Archetype.Undead, VictimClass.Classify(job: 90, undead: true));
    }

    [Fact]
    public void Caster_band_prefers_caster_over_human()
    {
        // 79-82/85/90 sit INSIDE the 74-93 human band -- caster must win the overlap.
        foreach (byte casterJob in new byte[] { 79, 80, 81, 82, 85, 90 })
            Assert.Equal(VictimClass.Archetype.Caster, VictimClass.Classify(casterJob, undead: false));
    }

    [Fact]
    public void Human_band_covers_the_non_caster_74_to_93_range()
    {
        foreach (byte job in new byte[] { 74, 75, 76, 77, 78, 83, 84, 86, 87, 88, 89, 91, 92, 93 })
            Assert.Equal(VictimClass.Archetype.Human, VictimClass.Classify(job, undead: false));
    }

    [Fact]
    public void Dark_Knight_160_is_human()
    {
        Assert.Equal(VictimClass.Archetype.Human, VictimClass.Classify(160, undead: false));
    }

    [Fact]
    public void Chocobo_line_94_95_96_classifies_monster()
    {
        foreach (byte job in new byte[] { 94, 95, 96 })
            Assert.Equal(VictimClass.Archetype.Monster, VictimClass.Classify(job, undead: false));
    }

    [Theory]
    [InlineData(94)]   // Chocobo
    [InlineData(95)]   // Black Chocobo
    public void FellPhrase_chocobo_jobs_read_a_monster(int job)
        => Assert.Equal("a monster", VictimClass.FellPhrase(true, (byte)job, false));

    [Fact]
    public void FellPhrase_mime_93_still_reads_a_human()
        => Assert.Equal("a human", VictimClass.FellPhrase(true, 93, false));

    [Fact]
    public void Puppeteer_bands_agree_with_the_victim_classifier()
    {
        for (int j = 0; j <= 255; j++)
        {
            if (j == 160) continue;
            var cls = VictimClass.Classify((byte)j, undead: false);
            Assert.True(Puppeteer.IsMonsterJob(j) == (cls == VictimClass.Archetype.Monster), $"monster band disagrees at job {j}");
            Assert.True(Puppeteer.IsGenericHumanJob(j) == (cls is VictimClass.Archetype.Human or VictimClass.Archetype.Caster),
                $"human band disagrees at job {j}");
        }
    }

    [Fact]
    public void Monster_band_94_144()
    {
        Assert.Equal(VictimClass.Archetype.Monster, VictimClass.Classify(94, undead: false));
        Assert.Equal(VictimClass.Archetype.Monster, VictimClass.Classify(96, undead: false));
        Assert.Equal(VictimClass.Archetype.Monster, VictimClass.Classify(144, undead: false));
        Assert.Equal(VictimClass.Archetype.Monster, VictimClass.Classify(120, undead: false));
    }

    [Fact]
    public void Story_id_37_lands_unknown()
    {
        // Story bosses read out-of-band ids like 37 -- EXPECTED to land Unknown, never a Mark.
        Assert.Equal(VictimClass.Archetype.Unknown, VictimClass.Classify(37, undead: false));
    }

    [Fact]
    public void Ramza_forms_0_to_3_land_unknown()
    {
        for (byte job = 0; job <= 3; job++)
            Assert.Equal(VictimClass.Archetype.Unknown, VictimClass.Classify(job, undead: false));
    }

    [Fact]
    public void Machinist_43_lands_unknown()
    {
        Assert.Equal(VictimClass.Archetype.Unknown, VictimClass.Classify(43, undead: false));
    }

    [Fact]
    public void Ids_above_monster_band_land_unknown()
    {
        Assert.Equal(VictimClass.Archetype.Unknown, VictimClass.Classify(145, undead: false));
        Assert.Equal(VictimClass.Archetype.Unknown, VictimClass.Classify(255, undead: false));
    }

    [Fact]
    public void Enum_order_matches_the_AC_listing_order()
    {
        Assert.Equal(0, (int)VictimClass.Archetype.Caster);
        Assert.Equal(1, (int)VictimClass.Archetype.Human);
        Assert.Equal(2, (int)VictimClass.Archetype.Monster);
        Assert.Equal(3, (int)VictimClass.Archetype.Undead);
        Assert.Equal(4, (int)VictimClass.Archetype.Unknown);
    }

    // ---- MarkTitle / CountNoun (enum order: Spellbreaker/Manslayer/Beastbane/Requiem, mages/men/beasts/risen) ----

    // InlineData can't carry the internal Archetype enum as a Theory parameter type (CS0051:
    // a public test method can't declare an internal-typed parameter) -- pass the int and cast.
    [Theory]
    [InlineData(0, "Spellbreaker")]
    [InlineData(1, "Manslayer")]
    [InlineData(2, "Beastbane")]
    [InlineData(3, "Requiem")]
    public void MarkTitle_matches_the_Slayer_set(int archetype, string expected)
        => Assert.Equal(expected, VictimClass.MarkTitle((VictimClass.Archetype)archetype));

    [Theory]
    [InlineData(0, "mages")]
    [InlineData(1, "men")]
    [InlineData(2, "beasts")]
    [InlineData(3, "risen")]
    public void CountNoun_matches_the_Ledger_voice(int archetype, string expected)
        => Assert.Equal(expected, VictimClass.CountNoun((VictimClass.Archetype)archetype));

    // ---- VictimNoun spot-checks (IC-named table, undead override, Dark Knight, monster/unknown default) ----

    [Theory]
    [InlineData(74, "Squire")]
    [InlineData(75, "Chemist")]
    [InlineData(76, "Knight")]
    [InlineData(77, "Archer")]
    [InlineData(78, "Monk")]
    [InlineData(79, "White Mage")]
    [InlineData(80, "Black Mage")]
    [InlineData(81, "Time Mage")]
    [InlineData(82, "Summoner")]
    [InlineData(83, "Thief")]
    [InlineData(84, "Orator")]
    [InlineData(85, "Mystic")]
    [InlineData(86, "Geomancer")]
    [InlineData(87, "Dragoon")]
    [InlineData(88, "Samurai")]
    [InlineData(89, "Ninja")]
    [InlineData(90, "Arithmetician")]
    [InlineData(91, "Bard")]
    [InlineData(92, "Dancer")]
    [InlineData(93, "Mime")]
    public void VictimNoun_names_the_IC_job_table(byte job, string expected)
        => Assert.Equal(expected, VictimClass.VictimNoun(job, undead: false));

    [Fact]
    public void VictimNoun_dark_knight_is_160()
    {
        Assert.Equal("Dark Knight", VictimClass.VictimNoun(160, undead: false));
    }

    [Fact]
    public void VictimNoun_chocobo_jobs_are_beasts()
    {
        Assert.Equal("beast", VictimClass.VictimNoun(94, undead: false));
        Assert.Equal("beast", VictimClass.VictimNoun(95, undead: false));
    }

    [Fact]
    public void VictimNoun_undead_overrides_the_job_table()
    {
        Assert.Equal("risen one", VictimClass.VictimNoun(77, undead: true));
    }

    [Fact]
    public void VictimNoun_monster_band_is_beast()
    {
        Assert.Equal("beast", VictimClass.VictimNoun(120, undead: false));
    }

    [Fact]
    public void VictimNoun_unknown_is_foe()
    {
        Assert.Equal("foe", VictimClass.VictimNoun(37, undead: false));
    }

    // ---- WithArticle ----

    [Theory]
    [InlineData("Archer", "an Archer")]
    [InlineData("Orator", "an Orator")]
    [InlineData("Arithmetician", "an Arithmetician")]
    [InlineData("Squire", "a Squire")]
    [InlineData("Knight", "a Knight")]
    [InlineData("risen one", "a risen one")]
    public void WithArticle_picks_a_or_an_by_leading_vowel(string noun, string expected)
        => Assert.Equal(expected, VictimClass.WithArticle(noun));

    // ---- MaxArchetypes headroom (moved here from Tuning per the plan's own test, but the
    // constant lives in Tuning.cs -- see TuningTests for the count check) ----
}
