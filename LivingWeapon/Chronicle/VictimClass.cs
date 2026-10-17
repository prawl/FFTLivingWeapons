namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1's pure victim classifier (docs/RELIQUARY_AC.md, "Classify"). Turns a captured job byte and undead bit into one
/// archetype per kill and supplies the display prose (Slayer titles, count nouns, victim nouns) that the card composer (CardLine.cs)
/// uses. No memory access.
/// </summary>
internal static class VictimClass
{
    /// <summary>Enum order follows the AC's listing (Caster, Human, Monster, Undead, then Unknown). It sets the Mark toast event-key space
    /// (1000 + (int)archetype, BannerToast.Policy.cs) and breaks ties between archetypes with equal earned-mark counts (lower value wins,
    /// see CardLine.Compose). Unknown is never a Mark archetype (see LegendStore.RecordDeed).</summary>
    internal enum Archetype { Caster = 0, Human = 1, Monster = 2, Undead = 3, Unknown = 4 }

    // Caster band sits inside the human 74-95 range, so it is checked first (AC precedence. Undead > caster > human > monster > unknown).
    private static bool IsCasterJob(byte job) =>
        job is 79 or 80 or 81 or 82 or 85 or 90;

    private static bool IsHumanJob(byte job) =>
        (job >= 74 && job <= 95) || job == 160;   // 160. Dark Knight's alternate id (both-ids rule)

    private static bool IsMonsterJob(byte job) =>
        job >= 96 && job <= 144;

    /// <summary>Precedence is undead bit &gt; caster band &gt; human band &gt; monster band &gt; unknown/special. Out-of-band ids (story
    /// bosses e.g. 37, Ramza forms 0-3, Machinist 43) land Unknown, so human/caster Marks undercount on story battles, an accepted
    /// Phase 1 tradeoff (docs/RELIQUARY_AC.md).</summary>
    public static Archetype Classify(byte job, bool undead)
    {
        if (undead) return Archetype.Undead;
        if (IsCasterJob(job)) return Archetype.Caster;
        if (IsHumanJob(job)) return Archetype.Human;
        if (IsMonsterJob(job)) return Archetype.Monster;
        return Archetype.Unknown;
    }

    /// <summary>The Slayer title earned at a Mark's kill-count threshold, in enum order (Spellbreaker Caster, Manslayer Human,
    /// Beastbane Monster, Requiem Undead). Unknown earns no Mark and has no title (returns "" defensively).</summary>
    public static string MarkTitle(Archetype a) => a switch
    {
        Archetype.Caster => "Spellbreaker",
        Archetype.Human => "Manslayer",
        Archetype.Monster => "Beastbane",
        Archetype.Undead => "Requiem",
        _ => "",
    };

    /// <summary>The Ledger-voice plural noun for a kill-count line ("N mages felled").</summary>
    public static string CountNoun(Archetype a) => a switch
    {
        Archetype.Caster => "mages",
        Archetype.Human => "men",
        Archetype.Monster => "beasts",
        Archetype.Undead => "risen",
        _ => "foes",
    };

    // IC-named job table, PSX order, ids 74..93 (ic-job-id-remap, census corroboration 76/77/80/82/83 on 2026-07-05). Index 0 == job 74.
    private static readonly string[] JobNames =
    {
        "Squire", "Chemist", "Knight", "Archer", "Monk", "White Mage", "Black Mage",
        "Time Mage", "Summoner", "Thief", "Orator", "Mystic", "Geomancer", "Dragoon",
        "Samurai", "Ninja", "Arithmetician", "Bard", "Dancer", "Mime",
    };

    /// <summary>Display-only victim noun for the last-victim narration line (CardLine.cs). Undead overrides the job table ("risen one");
    /// 94/160 both name "Dark Knight"; the named IC table covers 74-93; anything else falls to "beast"/"foe". <see cref="Classify"/> is
    /// authoritative for Marks, so a "foe" here can still classify Human (e.g. job 95).</summary>
    public static string VictimNoun(byte job, bool undead)
    {
        if (undead) return "risen one";
        if (job is >= 74 and <= 93) return JobNames[job - 74];
        if (job == 94 || job == 160) return "Dark Knight";
        return IsMonsterJob(job) ? "beast" : "foe";
    }

    /// <summary>Console kill-report phrase for a captured victim snapshot ("a caster", "a human", "a monster", "an undead foe", or "an enemy"
    /// when nothing sane was captured or the archetype is Unknown). Used by the [kill] console lines (logging facelift); nameId and job
    /// numbers ride the [trace] companion.</summary>
    public static string FellPhrase(bool has, byte job, bool undead)
    {
        if (!has) return "an enemy";
        return Classify(job, undead) switch
        {
            Archetype.Caster => "a caster",
            Archetype.Human => "a human",
            Archetype.Monster => "a monster",
            Archetype.Undead => "an undead foe",
            _ => "an enemy",
        };
    }

    /// <summary>"a"/"an" by the noun's leading vowel sound (an Archer, an Orator; a Squire, a Knight). ASCII-only.</summary>
    public static string WithArticle(string noun)
    {
        if (string.IsNullOrEmpty(noun)) return noun;
        char c = char.ToLowerInvariant(noun[0]);
        bool vowel = c is 'a' or 'e' or 'i' or 'o' or 'u';
        return (vowel ? "an " : "a ") + noun;
    }

    /// <summary>The highest-count archetype among earned marks (legend.Marks); a count alone never qualifies, only a threshold-crossed entry.
    /// Ties go to the lower enum value. Shared by CardLine.Compose (equip-card story line) and LW-31's Attack-menu dossier (AttackCard.cs).
    /// Returns null (count -1) when no Mark is earned.</summary>
    public static Archetype? BestMark(WeaponLegend legend, out int count)
    {
        Archetype? best = null;
        int bestCount = -1;
        foreach (int idx in legend.Marks)
        {
            int c = legend.Counts[idx];
            if (c > bestCount || (c == bestCount && best.HasValue && idx < (int)best.Value))
            {
                best = (Archetype)idx;
                bestCount = c;
            }
        }
        count = bestCount;
        return best;
    }
}
