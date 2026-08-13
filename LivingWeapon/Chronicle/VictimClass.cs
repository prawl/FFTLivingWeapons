namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1's pure victim classifier (docs/RELIQUARY_AC.md, "Classify"). Turns a
/// captured job byte + undead bit into exactly ONE archetype per kill, and supplies the
/// display prose (Slayer titles, count nouns, per-job victim nouns) the card composer
/// (CardLine.cs) narrates with. No memory access, no IGameMemory -- pure over its inputs.
/// </summary>
internal static class VictimClass
{
    /// <summary>Enum order is LOAD-BEARING: it is the AC's own listing order (Caster, Human,
    /// Monster, Undead, then Unknown), and it drives BOTH the Mark toast event-key space
    /// (1000 + (int)archetype, BannerToast.Policy.cs) AND the tie-break when two archetypes
    /// share the same earned-mark count (the lower enum value wins -- see CardLine.Compose).
    /// Unknown is never a Mark archetype (counted, but VictimClass.Classify never routes a
    /// Mark threshold check against it -- see LegendStore.RecordDeed).</summary>
    internal enum Archetype { Caster = 0, Human = 1, Monster = 2, Undead = 3, Unknown = 4 }

    // Caster band sits INSIDE the human 74-95 range -- checked first so the overlap resolves
    // to Caster, matching the AC's precedence (undead > caster > human > monster > unknown).
    private static bool IsCasterJob(byte job) =>
        job is 79 or 80 or 81 or 82 or 85 or 90;

    private static bool IsHumanJob(byte job) =>
        (job >= 74 && job <= 95) || job == 160;   // 160: Dark Knight's alternate id (both-ids rule)

    private static bool IsMonsterJob(byte job) =>
        job >= 96 && job <= 144;

    /// <summary>Precedence: undead bit &gt; caster band &gt; human band &gt; monster band &gt;
    /// unknown/special. Out-of-band ids (story bosses e.g. 37, Ramza forms 0-3, Machinist 43)
    /// land Unknown BY DESIGN -- human/caster Marks undercount on story battles, an accepted
    /// Phase 1 tradeoff (docs/RELIQUARY_AC.md).</summary>
    public static Archetype Classify(byte job, bool undead)
    {
        if (undead) return Archetype.Undead;
        if (IsCasterJob(job)) return Archetype.Caster;
        if (IsHumanJob(job)) return Archetype.Human;
        if (IsMonsterJob(job)) return Archetype.Monster;
        return Archetype.Unknown;
    }

    /// <summary>The Slayer title earned at a Mark's kill-count threshold, in enum order:
    /// Spellbreaker (Caster) / Manslayer (Human) / Beastbane (Monster) / Requiem (Undead).
    /// Unknown never earns a Mark, so it has no title (returns "" defensively).</summary>
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

    // IC-named job table, PSX order, ids 74..93 (ic-job-id-remap + census corroboration
    // 76/77/80/82/83 on 2026-07-05). Index 0 == job 74.
    private static readonly string[] JobNames =
    {
        "Squire", "Chemist", "Knight", "Archer", "Monk", "White Mage", "Black Mage",
        "Time Mage", "Summoner", "Thief", "Orator", "Mystic", "Geomancer", "Dragoon",
        "Samurai", "Ninja", "Arithmetician", "Bard", "Dancer", "Mime",
    };

    /// <summary>The display-only victim noun for the last-victim narration line (CardLine.cs).
    /// Undead overrides the job table ("risen one"); 94/160 both name "Dark Knight"; the named
    /// IC table covers 74-93; everything else (monster band, or unnamed/out-of-band ids) falls
    /// to "beast"/"foe". This is prose garnish only -- <see cref="Classify"/> is the authoritative
    /// classifier for Marks; a job landing "foe" here can still classify Human via Classify (e.g.
    /// job 95, which has no named-table entry).</summary>
    public static string VictimNoun(byte job, bool undead)
    {
        if (undead) return "risen one";
        if (job is >= 74 and <= 93) return JobNames[job - 74];
        if (job == 94 || job == 160) return "Dark Knight";
        return IsMonsterJob(job) ? "beast" : "foe";
    }

    /// <summary>The console kill-report phrase for a captured victim snapshot: "a caster",
    /// "a human", "a monster", "an undead foe", or "an enemy" when nothing sane was captured
    /// (or the archetype is Unknown). Used by the [kill] console lines (logging facelift):
    /// victim identity in words, never a nameId/job number (those ride the [trace] companion).</summary>
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

    /// <summary>"a"/"an" by the noun's leading-letter vowel sound (an Archer, an Orator, an
    /// Arithmetician; a Squire, a Knight). ASCII-only, matching this module's display prose.</summary>
    public static string WithArticle(string noun)
    {
        if (string.IsNullOrEmpty(noun)) return noun;
        char c = char.ToLowerInvariant(noun[0]);
        bool vowel = c is 'a' or 'e' or 'i' or 'o' or 'u';
        return (vowel ? "an " : "a ") + noun;
    }

    /// <summary>The highest-count archetype AMONG EARNED MARKS (legend.Marks): a count alone
    /// never qualifies, only a threshold-crossed entry. Tie-break by enum order: a strictly
    /// higher count always wins; an exact tie keeps the incumbent unless the challenger's enum
    /// value is LOWER. Extracted from CardLine.Compose (the equip-card story line) so LW-31's
    /// Attack-menu dossier (AttackCard.cs) shares the exact same selection rule instead of a
    /// second hand-rolled copy. Returns null (count -1) when no Mark has been earned yet.</summary>
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
